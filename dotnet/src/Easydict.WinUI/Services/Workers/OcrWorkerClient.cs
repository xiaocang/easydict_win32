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
    private readonly WorkerSpawner _spawner = new();
    private readonly Func<CancellationToken, Task<SidecarClient.SidecarClient>>? _spawnOverride;
    private bool _disposed;

    public OcrWorkerClient(SettingsService settings, IOcrService fallback)
    {
        _settings = settings;
        _fallback = fallback;
    }

    internal OcrWorkerClient(
        SettingsService settings,
        IOcrService fallback,
        Func<CancellationToken, Task<SidecarClient.SidecarClient>> spawnOverride)
        : this(settings, fallback)
    {
        _spawnOverride = spawnOverride;
    }

    public string ServiceId => "windows_ocr_worker";
    public string DisplayName => "Windows OCR Worker";
    public bool IsAvailable => _fallback.IsAvailable;

    public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => _fallback.GetAvailableLanguages();

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

            SidecarClient.SidecarClient client;
            try
            {
                client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (CanFallbackToInProc(ex))
            {
                Debug.WriteLine($"[OcrWorker] Falling back to in-proc OCR: {ex.Message}");
                return await _fallback.RecognizeAsync(
                    pixelData,
                    pixelWidth,
                    pixelHeight,
                    preferredLanguageTag,
                    cancellationToken).ConfigureAwait(false);
            }

            await using var clientLease = client.ConfigureAwait(false);
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
                    },
                    timeoutMs: 0,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return MapResult(dto);
            }
            catch (SidecarProcessExitedException ex) when (CanFallbackToInProc(ex))
            {
                Debug.WriteLine($"[OcrWorker] Falling back to in-proc OCR after worker exit: {ex.Message}");
                return await _fallback.RecognizeAsync(
                    pixelData,
                    pixelWidth,
                    pixelHeight,
                    preferredLanguageTag,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SidecarProcessExitedException ex)
            {
                throw new InvalidOperationException($"OCR worker exited unexpectedly (code={ex.ExitCode})", ex);
            }
        }
        finally
        {
            TryDelete(tempPath);
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

    public void Dispose()
    {
        _disposed = true;
    }
}
