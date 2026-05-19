using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.LongDocument;
using Easydict.Workers.LongDoc.Infrastructure;

namespace Easydict.Workers.LongDoc.Handlers;

/// <summary>
/// Handles the "translate_document" request. Streams status / progress /
/// block_translated events back to the host during the long-running operation,
/// and returns the final TranslateDocumentResult on completion.
/// </summary>
internal sealed class TranslateDocumentHandler
{
    private readonly WorkerState _state;
    private readonly IpcEventWriter _writer;

    public TranslateDocumentHandler(WorkerState state, IpcEventWriter writer)
    {
        _state = state;
        _writer = writer;
    }

    public async Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                "Worker has not received a configure request yet");
        }

        if (parameters is null)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                "translate_document requires params");
        }

        TranslateDocumentParams? p;
        try
        {
            p = parameters.Value.Deserialize<TranslateDocumentParams>(
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
        }
        catch (JsonException ex)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                $"translate_document deserialization failed: {ex.Message}");
        }

        if (p is null) throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "translate_document params was null");

        try
        {
            await _writer.WriteEventAsync(LongDocEvents.Status,
                new StatusEventData { Message = "Initializing translation engine..." },
                requestId);

            using var manager = WorkerTranslationManagerFactory.Build(_state.Settings!);
            var coreService = new LongDocumentTranslationService(manager);

            // Progress callback — streams every IProgress<T> tick as a "progress" event.
            var progress = new Progress<LongDocumentTranslationProgress>(p =>
            {
                _ = _writer.WriteEventAsync(LongDocEvents.Progress, new ProgressEventData
                {
                    Stage = p.Stage.ToString(),
                    CurrentBlock = p.CurrentBlock,
                    TotalBlocks = p.TotalBlocks,
                    CurrentPage = p.CurrentPage,
                    TotalPages = p.TotalPages,
                    Percentage = p.Percentage,
                    CurrentBlockPreview = p.CurrentBlockPreview,
                }, requestId);
            });

            // ────────────────────────────────────────────────────────────────────────
            // FIXME(p1a-follow-up): Wire the PDF/Markdown/TXT source-document builder.
            //
            // The existing WinUI-side orchestrator
            //   Easydict.WinUI/Services/LongDocumentTranslationService.cs:BuildSourceDocumentAsync
            // owns PDF parsing (MuPDF.NET), Markdown/TXT chunking, and layout detection
            // (DocLayoutYOLO / TATR / Vision LLM). It reads dozens of SettingsService.Instance
            // properties directly, which is why it can't be invoked verbatim from the worker.
            //
            // To finish this handler:
            //   1. Extract BuildSourceDocumentAsync (and its private helpers) into a
            //      worker-compatible builder that takes a SettingsSnapshot parameter
            //      instead of reaching for SettingsService.Instance. Suggested home:
            //      Easydict.TranslationService/LongDocument/SourceDocumentBuilder.cs.
            //   2. Replace WinUI-side service references with worker-compatible
            //      layout/download services that do not reach for SettingsService.Instance.
            //   3. Then below: var sourceDoc = await SourceDocumentBuilder.BuildAsync(p, _state.Settings, status: msg =>
            //          _writer.WriteEventAsync(LongDocEvents.Status, new StatusEventData { Message = msg }, requestId),
            //          progress: progress, cancellationToken);
            //      var coreOptions = new LongDocumentTranslationOptions { ... };
            //      var coreResult = await coreService.TranslateAsync(sourceDoc, coreOptions, cancellationToken);
            //   4. Build the export via the source-shared DocumentExport pipeline,
            //      write to outputPath, return TranslateDocumentResult.
            //
            // For this commit the handler reports a clear, structured error so the
            // host code path (LongDocWorkerClient) can fall back to in-proc execution
            // via SettingsService.UseLongDocWorker = false. The scaffolding around it
            // (IPC, dispatch, cancellation, progress streaming, error mapping, exit
            // semantics) is fully wired and exercised by the test suite.
            // ────────────────────────────────────────────────────────────────────────

            throw new WorkerHandlerException(WorkerErrorCodes.Internal,
                "translate_document is not yet plumbed in this worker build. " +
                "Toggle Settings.UseLongDocWorker=false to fall back to the in-proc path.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkerHandlerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Map known translation errors to a structured "service_error".
            if (ex is TranslationException tex)
            {
                throw new WorkerHandlerException(WorkerErrorCodes.ServiceError,
                    tex.Message,
                    new { errorCode = tex.ErrorCode.ToString(), serviceId = tex.ServiceId });
            }
            throw;
        }
    }

}
