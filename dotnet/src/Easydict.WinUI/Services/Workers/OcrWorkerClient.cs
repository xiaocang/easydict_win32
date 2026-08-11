using System.Diagnostics;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services.Workers;

internal sealed class OcrWorkerClient : IOcrService, IDisposable
{
    private const string WorkerSubdir = "ocr";
    private const string WorkerExeName = "Easydict.Workers.Ocr.exe";

    private readonly SettingsService _settings;
    private readonly IOcrService _fallback;
    private readonly OcrEngineType _engine;
    private readonly string? _modelId;
    private readonly int _threadCount;
    private readonly bool _useGpu;
    private readonly PpOcrV6ModelStore _modelStore;
    private readonly WorkerSpawner _spawner = new();
    private readonly Func<CancellationToken, Task<SidecarClient.SidecarClient>>? _spawnOverride;
    private readonly bool _allowFallback;
    private readonly SemaphoreSlim _recognizeLock = new(1, 1);
    private SidecarClient.SidecarClient? _client;
    private bool _disposed;

    public OcrWorkerClient(
        SettingsService settings,
        IOcrService fallback,
        OcrEngineType engine = OcrEngineType.WindowsNative,
        string? modelId = null,
        int? threadCount = null,
        bool? allowFallback = null,
        bool? useGpu = null)
    {
        _settings = settings;
        _fallback = fallback;
        _engine = engine;
        _modelId = engine == OcrEngineType.PpOcrV6 ? modelId ?? settings.OcrModel : null;
        _threadCount = Math.Clamp(threadCount ?? settings.PpOcrV6ThreadCount, 1, 16);
        _allowFallback = allowFallback ?? settings.PpOcrV6AllowFallback;
        _useGpu = useGpu ?? settings.PpOcrV6UseGpu;
        _modelStore = new PpOcrV6ModelStore();
    }

    internal OcrWorkerClient(
        SettingsService settings,
        IOcrService fallback,
        Func<CancellationToken, Task<SidecarClient.SidecarClient>> spawnOverride)
        : this(settings, fallback, OcrEngineType.WindowsNative)
    {
        _spawnOverride = spawnOverride;
    }

    public string ServiceId => _engine == OcrEngineType.PpOcrV6 ? "pp_ocrv6" : "windows_ocr_worker";
    public string DisplayName => _engine == OcrEngineType.PpOcrV6 ? "PP-OCRv6" : "Windows OCR Worker";
    public bool IsAvailable => _engine == OcrEngineType.PpOcrV6
        ? IsPpOcrV6ModelInstalled() || (_allowFallback && _fallback.IsAvailable)
        : _fallback.IsAvailable;

    public IReadOnlyList<OcrLanguage> GetAvailableLanguages()
    {
        if (_engine != OcrEngineType.PpOcrV6)
        {
            return _fallback.GetAvailableLanguages();
        }

        return PpOcrV6ModelCatalog.TryGet(_modelId, out var model)
            ? model!.Languages.Select(tag => new OcrLanguage { Tag = tag, DisplayName = tag }).ToList()
            : [];
    }

    private bool IsPpOcrV6ModelInstalled()
    {
        return PpOcrV6ModelCatalog.TryGet(_modelId, out var model)
            && _modelStore.GetStateByPresence(model!.Id) == PpOcrV6ModelState.Installed;
    }

    public async Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> pixelData,
        int pixelWidth,
        int pixelHeight,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OcrWorkerClient));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        var expectedLength = pixelWidth * pixelHeight * 4; // BGRA8
        if (pixelData.Length < expectedLength)
            throw new ArgumentException(
                $"pixelData length ({pixelData.Length}) is less than expected ({expectedLength}) for {pixelWidth}x{pixelHeight} BGRA8",
                nameof(pixelData));

        var tempPath = CreateTempPixelPath();
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await stream.WriteAsync(pixelData, cancellationToken).ConfigureAwait(false);
            }

            await _recognizeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var dto = await client.SendRequestAsync<OcrResultDto>(
                        OcrMethods.Recognize,
                        new OcrRecognizeParams
                        {
                            PixelDataPath = tempPath,
                            PixelWidth = pixelWidth,
                            PixelHeight = pixelHeight,
                            PreferredLanguageTag = preferredLanguageTag,
                            Engine = _engine == OcrEngineType.PpOcrV6
                                ? OcrEngines.PpOcrV6
                                : OcrEngines.WindowsNative,
                            ModelId = _engine == OcrEngineType.PpOcrV6 ? _modelId : null,
                            ThreadCount = _engine == OcrEngineType.PpOcrV6 ? _threadCount : null,
                            UseGpu = _engine == OcrEngineType.PpOcrV6 && _useGpu,
                        },
                        timeoutMs: 0,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    var result = MapResult(dto);
                    if (_engine != OcrEngineType.PpOcrV6)
                    {
                        await InvalidateClientAsync().ConfigureAwait(false);
                    }

                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await InvalidateClientAsync().ConfigureAwait(false);
                    throw;
                }
                catch (SidecarProcessExitedException ex)
                {
                    await InvalidateClientAsync().ConfigureAwait(false);
                    if (CanFallback(ex))
                    {
                        Debug.WriteLine($"[OcrWorker] Falling back to in-proc OCR after worker exit: {ex.Message}");
                        return await _fallback.RecognizeAsync(
                            pixelData,
                            pixelWidth,
                            pixelHeight,
                            preferredLanguageTag,
                            cancellationToken).ConfigureAwait(false);
                    }

                    throw new InvalidOperationException($"OCR worker exited unexpectedly (code={ex.ExitCode})", ex);
                }
                catch (SidecarErrorException ex)
                {
                    await InvalidateClientAsync().ConfigureAwait(false);
                    if (CanFallback(ex))
                    {
                        Debug.WriteLine($"[OcrWorker] Falling back to in-proc OCR after worker error: {ex.Message}");
                        return await _fallback.RecognizeAsync(
                            pixelData,
                            pixelWidth,
                            pixelHeight,
                            preferredLanguageTag,
                            cancellationToken).ConfigureAwait(false);
                    }

                    throw;
                }
            }
            catch (Exception ex) when (CanFallback(ex))
            {
                await InvalidateClientAsync().ConfigureAwait(false);
                Debug.WriteLine($"[OcrWorker] Falling back to in-proc OCR: {ex.Message}");
                return await _fallback.RecognizeAsync(
                    pixelData,
                    pixelWidth,
                    pixelHeight,
                    preferredLanguageTag,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _recognizeLock.Release();
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<SidecarClient.SidecarClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsRunning: true })
        {
            return _client;
        }

        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }

        _client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);
        return _client;
    }

    private async Task InvalidateClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<SidecarClient.SidecarClient> SpawnConfiguredAsync(CancellationToken cancellationToken)
    {
        if (_spawnOverride is not null)
        {
            return await _spawnOverride(cancellationToken).ConfigureAwait(false);
        }

        var snapshot = WorkerSpawner.BuildSnapshot(_settings);
        return await _spawner.StartAndConfigureAsync(
            WorkerKinds.Ocr,
            WorkerSubdir,
            WorkerExeName,
            snapshot,
            cancellationToken).ConfigureAwait(false);
    }

    internal static OcrResult MapResult(OcrResultDto? dto)
    {
        if (dto is null)
        {
            return new OcrResult();
        }

        // Rebuild the recognized text with the same CJK-aware merging used by the in-process
        // WindowsOcrService (WindowsOcrService.RecognizeBitmapAsync), so worker output is identical
        // — in particular, no space is inserted between adjacent CJK characters. When the worker
        // did not supply per-word data (older worker), fall back to its pre-joined text.
        var hasWords = dto.Lines.Any(line => line.Words is { Count: > 0 });

        var lines = dto.Lines.Select(line => new OcrLine
        {
            Text = line.Words is { Count: > 0 }
                ? OcrTextMerger.MergeWords(line.Words)
                : line.Text,
            Confidence = line.Confidence,
            BoundingRect = new OcrRect(
                line.BoundingRect.X,
                line.BoundingRect.Y,
                line.BoundingRect.Width,
                line.BoundingRect.Height),
        }).ToList();

        IReadOnlyList<OcrLine> sortedLines = hasWords
            ? OcrTextMerger.GroupAndSortLines(lines)
            : lines;
        var text = hasWords
            ? OcrTextMerger.MergeLines(sortedLines)
            : dto.Text;

        return new OcrResult
        {
            Text = text,
            Lines = sortedLines,
            TextAngle = dto.TextAngle,
            DetectedLanguage = dto.DetectedLanguage is null
                ? null
                : new OcrLanguage
                {
                    Tag = dto.DetectedLanguage.Tag,
                    DisplayName = dto.DetectedLanguage.DisplayName,
                },
        };
    }

    private static string CreateTempPixelPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Easydict", "ocr-worker");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.bgra");
    }

    private static void TryDelete(string path)
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
            // Best-effort cleanup of transient pixel files.
        }
    }

    internal static bool CanFallbackToInProc(Exception ex)
    {
        return ex is WorkerStartFailedException
            or WorkerVersionMismatchException
            or FileNotFoundException
            or SidecarProcessExitedException;
    }

    internal bool CanFallback(Exception ex)
    {
        if (_engine == OcrEngineType.PpOcrV6 && !_allowFallback)
        {
            return false;
        }

        if (ex is SidecarErrorException sidecarError)
        {
            return sidecarError.Error.Code is
                "model_missing" or
                "model_invalid" or
                "runtime_missing" or
                "gpu_unavailable" or
                "inference_error" or
                "service_error" or
                "internal_error";
        }

        return CanFallbackToInProc(ex);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _client = null;
        _recognizeLock.Dispose();
    }
}
