using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;

namespace Easydict.WinUI.Services.Workers;

/// <summary>
/// Adapter that runs long-document translation in a child worker process. The
/// public method signature mirrors the in-proc Easydict.WinUI.Services
/// LongDocumentTranslationService.TranslateToPdfAsync so the call site can
/// swap implementations behind the SettingsService.UseLongDocWorker flag.
///
/// On each translation, the adapter spawns a fresh worker, sends configure +
/// translate_document, streams status/progress/block_translated events back
/// to the caller's IProgress/onProgress callbacks, and disposes the worker
/// after completion. The worker exits via its own Environment.Exit(0) call,
/// reclaiming MuPDF/ONNX native heap.
/// </summary>
internal sealed class LongDocWorkerClient : IDisposable
{
    private const string WorkerSubdir = "longdoc";
    private const string WorkerExeName = "Easydict.Workers.LongDoc.exe";

    private readonly SettingsService _settings;
    private readonly WorkerSpawner _spawner = new();
    private SidecarClient.SidecarClient? _activeClient;
    private bool _disposed;

    public LongDocWorkerClient(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Runs translation in a child worker. Spawns once per call (worker exits
    /// after completion per the "exit on completion" lifecycle).
    /// </summary>
    public async Task<LongDocumentTranslationResult> TranslateToPdfAsync(
        LongDocumentInputMode mode,
        string input,
        Language from,
        Language to,
        string outputPath,
        string serviceId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        LayoutDetectionMode layoutDetection = LayoutDetectionMode.Heuristic,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual,
        PdfExportMode pdfExportMode = PdfExportMode.ContentStreamReplacement,
        string? visionEndpoint = null,
        string? visionApiKey = null,
        string? visionModel = null,
        IProgress<LongDocumentTranslationProgress>? progress = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LongDocWorkerClient));

        var snapshot = WorkerSpawner.BuildSnapshot(_settings);
        var client = await _spawner.StartAndConfigureAsync(
            WorkerKinds.LongDoc, WorkerSubdir, WorkerExeName, snapshot, cancellationToken).ConfigureAwait(false);
        _activeClient = client;

        try
        {
            void OnEvent(IpcEvent evt)
            {
                switch (evt.Event)
                {
                    case LongDocEvents.Status:
                        if (evt.Data is JsonElement statusEl)
                        {
                            var status = statusEl.Deserialize<StatusEventData>(JsonOptions);
                            if (status is not null) onProgress?.Invoke(status.Message);
                        }
                        break;
                    case LongDocEvents.Progress:
                        if (evt.Data is JsonElement progEl)
                        {
                            var p = progEl.Deserialize<ProgressEventData>(JsonOptions);
                            if (p is not null && progress is not null)
                            {
                                progress.Report(MapProgress(p));
                            }
                        }
                        break;
                    case LongDocEvents.BlockTranslated:
                        // Per-block events are informational at this layer; the worker
                        // will emit a final translate_document response with the
                        // aggregate result. Logging only.
                        Debug.WriteLine($"[LongDocWorker] block_translated event received");
                        break;
                }
            }

            client.OnEvent += OnEvent;
            try
            {
                // Forward cancellation to the worker by sending a "cancel" request
                // alongside cancelling the local SendRequestAsync.
                string? inflightRequestId = null;

                using var cancelReg = cancellationToken.Register(() =>
                {
                    var requestId = Volatile.Read(ref inflightRequestId);
                    if (!string.IsNullOrEmpty(requestId))
                    {
                        _ = client.SendRequestAsync(
                            WorkerMethods.Cancel,
                            new CancelRequestParams { TargetRequestId = requestId! },
                            timeoutMs: 5000);
                    }
                });

                var resultJsonPath = LongDocResultFileStore.CreateTempPath();
                TranslateDocumentResult? result;
                try
                {
                    result = await client.SendRequestAsync<TranslateDocumentResult>(
                        LongDocMethods.TranslateDocument,
                        new TranslateDocumentParams
                        {
                            InputPath = input,
                            OutputPath = outputPath,
                            InputMode = mode.ToString(),
                            From = from.ToString(),
                            To = to.ToString(),
                            ServiceId = serviceId,
                            OutputMode = outputMode.ToString(),
                            PdfExportMode = pdfExportMode.ToString(),
                            LayoutDetection = layoutDetection.ToString(),
                            PageRange = _settings.LongDocPageRange,
                            VisionEndpoint = visionEndpoint,
                            VisionApiKey = visionApiKey,
                            VisionModel = visionModel,
                            ResultJsonPath = resultJsonPath,
                        },
                        timeoutMs: 0, // No host-side timeout for long ops; cancellation is the escape hatch.
                        cancellationToken: cancellationToken,
                        onRequestId: requestId => Volatile.Write(ref inflightRequestId, requestId)).ConfigureAwait(false);

                    if (result is not null)
                    {
                        result = await HydrateResultAsync(result, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteResultFile(resultJsonPath);
                }

                if (result is null)
                {
                    throw new TranslationException("Worker returned null translate_document result")
                    {
                        ErrorCode = TranslationErrorCode.Unknown,
                        ServiceId = serviceId,
                    };
                }

                return MapResult(result);
            }
            finally
            {
                client.OnEvent -= OnEvent;
            }
        }
        catch (SidecarErrorException sex)
        {
            throw MapWorkerError(sex, serviceId);
        }
        catch (SidecarProcessExitedException pex)
        {
            throw new TranslationException(
                $"Long-document worker exited unexpectedly (code={pex.ExitCode})", pex)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = serviceId,
            };
        }
        finally
        {
            // Worker exits on completion (Environment.Exit(0)); explicit StopAsync is
            // belt-and-suspenders for the failure path.
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
            _activeClient = null;
        }
    }

    private static TranslationException MapWorkerError(SidecarErrorException sex, string serviceId)
    {
        // sex.Code is the worker-side error code; map back to TranslationErrorCode.
        var code = sex.Error.Code switch
        {
            WorkerErrorCodes.Cancelled => TranslationErrorCode.Unknown,
            WorkerErrorCodes.ModelMissing => TranslationErrorCode.InvalidModel,
            WorkerErrorCodes.ServiceError => TranslationErrorCode.ServiceUnavailable,
            _ => TranslationErrorCode.Unknown,
        };
        return new TranslationException(sex.Error.Message, sex)
        {
            ErrorCode = code,
            ServiceId = serviceId,
        };
    }

    private static LongDocumentTranslationProgress MapProgress(ProgressEventData p)
    {
        return new LongDocumentTranslationProgress
        {
            Stage = Enum.TryParse<LongDocumentTranslationStage>(p.Stage, out var stage)
                ? stage
                : LongDocumentTranslationStage.Translating,
            CurrentBlock = p.CurrentBlock,
            TotalBlocks = p.TotalBlocks,
            CurrentPage = p.CurrentPage,
            TotalPages = p.TotalPages,
            Percentage = p.Percentage,
            CurrentBlockPreview = p.CurrentBlockPreview,
        };
    }

    internal static async Task<TranslateDocumentResult> HydrateResultAsync(
        TranslateDocumentResult result,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.ResultJsonPath))
        {
            return result;
        }

        if (!File.Exists(result.ResultJsonPath))
        {
            throw new TranslationException($"Worker result file was not found: {result.ResultJsonPath}")
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
            };
        }

        return await LongDocResultFileStore.ReadAsync(result.ResultJsonPath, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static LongDocumentTranslationResult MapResult(TranslateDocumentResult result)
    {
        var failed = result.FailedChunkIndexes?.OrderBy(i => i).ToList() ?? [];
        return new LongDocumentTranslationResult
        {
            State = MapState(result.State, failed),
            OutputPath = result.OutputPath ?? string.Empty,
            BilingualOutputPath = result.BilingualOutputPath,
            TotalChunks = result.TotalChunks,
            SucceededChunks = result.SucceededChunks,
            FailedChunkIndexes = failed,
            QualityReport = ParseQualityReport(result.QualityReport, result.TotalChunks, result.SucceededChunks, failed),
            Checkpoint = null,
        };
    }

    internal static bool CanFallbackToInProc(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return false;
        }

        if (ex is WorkerStartFailedException or WorkerVersionMismatchException or FileNotFoundException)
        {
            return true;
        }

        return ex is TranslationException { InnerException: SidecarErrorException sidecarError } &&
            sidecarError.Error.Code == WorkerErrorCodes.Internal;
    }

    private static LongDocumentJobState MapState(string? state, IReadOnlyCollection<int> failed)
    {
        if (state?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            return LongDocumentJobState.Failed;
        }

        if (state?.Equals("PartiallyCompleted", StringComparison.OrdinalIgnoreCase) == true ||
            state?.Equals("PartialSuccess", StringComparison.OrdinalIgnoreCase) == true ||
            failed.Count > 0)
        {
            return LongDocumentJobState.PartialSuccess;
        }

        return LongDocumentJobState.Completed;
    }

    private static LongDocumentQualityReport ParseQualityReport(
        string? qualityReportJson,
        int totalChunks,
        int succeededChunks,
        IReadOnlyList<int> failed)
    {
        if (!string.IsNullOrWhiteSpace(qualityReportJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<LongDocumentQualityReport>(qualityReportJson, JsonOptions);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[LongDocWorker] Failed to parse quality report: {ex.Message}");
            }
        }

        return new LongDocumentQualityReport
        {
            StageTimingsMs = new Dictionary<string, long>(),
            TotalBlocks = totalChunks,
            TranslatedBlocks = succeededChunks,
            SkippedBlocks = 0,
            FailedBlocks = failed
                .Select(index => new FailedBlockInfo
                {
                    IrBlockId = $"worker-flat-{index}",
                    SourceBlockId = $"chunk-{index}",
                    PageNumber = 0,
                    RetryCount = 0,
                    Error = "Worker returned a flat failed-chunk index without block details.",
                })
                .ToList(),
        };
    }

    private static void TryDeleteResultFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp result files are best-effort cleanup only.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _activeClient?.Dispose(); } catch { /* swallow */ }
    }

}
