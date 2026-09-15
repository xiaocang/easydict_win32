using System.Diagnostics;
using Easydict.SidecarClient.Protocol;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.Workers;
using Microsoft.UI.Dispatching;

namespace Easydict.WinUI.Services;

/// <summary>
/// Identifies an OCR result that should be surfaced to the user.
/// </summary>
internal enum OcrFailureReason
{
    NoTextRecognized,
    EngineUnavailable,
    Failed
}

/// <summary>
/// Orchestrates the OCR translation flow: Screenshot → OCR → Translate.
/// All operations are asynchronous and non-blocking to the UI thread.
/// </summary>
public sealed class OcrTranslateService : IDisposable, IAsyncDisposable
{
    private bool _disposed;
    private readonly ScreenCaptureService _captureService = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<OcrFailureReason>? _failureReporter;
    private OcrWorkerClient? _ppOcrV6Client;
    private PpOcrV6ClientKey? _ppOcrV6ClientKey;
    private readonly SemaphoreSlim _ocrPipelineLock = new(1, 1);
    // Concurrency guard: only one OCR operation can run at a time.
    // Owned by RunOcrPipelineAsync — only that method creates and disposes.
    // Other code may Cancel() but must NOT Dispose().
    private CancellationTokenSource? _currentCts;

    public OcrTranslateService(DispatcherQueue dispatcherQueue)
        : this(dispatcherQueue, failureReporter: null)
    {
    }

    internal OcrTranslateService(
        DispatcherQueue dispatcherQueue,
        Action<OcrFailureReason>? failureReporter)
    {
        _dispatcherQueue = dispatcherQueue;
        _failureReporter = failureReporter;
    }

    /// <summary>
    /// Capture screenshot → OCR → show result in MiniWindow for translation.
    /// Safe to call from any thread. Cancels any in-flight OCR operation.
    /// </summary>
    public async Task OcrTranslateAsync()
    {
        Debug.WriteLine("[OcrTranslate] Starting OCR translate flow...");

        // Recognition can take seconds (a cloud engine, a cold local model). Put the window
        // on screen as soon as the region is captured so the flow is never silent (issue #216).
        var text = await RunOcrPipelineAsync("OcrTranslate", ShowRecognizingStatus)
            .ConfigureAwait(false);
        if (text is null) return;

        // The query takes the status line from here: it shows "translating" on its own.
        _busyStatusShown = false;

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            MiniWindowService.Instance.ShowWithText(text, QuerySourceKind.Ocr);
        }))
        {
            Debug.WriteLine("[OcrTranslate] Failed to enqueue MiniWindow show — dispatcher shut down?");
        }
    }

    /// <summary>
    /// Capture screenshot → OCR → copy result to clipboard (silent mode).
    /// Safe to call from any thread. Cancels any in-flight OCR operation.
    /// </summary>
    public async Task SilentOcrAsync()
    {
        Debug.WriteLine("[OcrTranslate] Starting silent OCR flow...");

        var text = await RunOcrPipelineAsync("SilentOcr").ConfigureAwait(false);
        if (text is null) return;

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                Debug.WriteLine($"[OcrTranslate] Silent OCR: {text.Length} chars → clipboard");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OcrTranslate] Silent OCR clipboard error: {ex.Message}");
            }
        }))
        {
            Debug.WriteLine("[OcrTranslate] Failed to enqueue clipboard write — dispatcher shut down?");
        }
    }

    private async Task<string?> RunOcrPipelineAsync(string label, Action? onRegionCaptured = null)
    {
        using var cts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _currentCts, cts);
        var pipelineLockAcquired = false;
        string? recognizedText = null;
        try
        {
            CancelPreviousOperation(previousCts);
            var capture = await _captureService.CaptureRegionAsync(cts.Token).ConfigureAwait(false);
            if (capture is null) return null;

            cts.Token.ThrowIfCancellationRequested();
            onRegionCaptured?.Invoke();
            await _ocrPipelineLock.WaitAsync(cts.Token).ConfigureAwait(false);
            pipelineLockAcquired = true;

            using (capture)
            {
                var ocrOptions = OcrServiceOptions.FromSettings(SettingsService.Instance);
                LogOcrDiagnostics(label, ocrOptions);
                var (ocrEngine, ownedByCaller) = await GetOcrEngineAsync(ocrOptions).ConfigureAwait(false);
                try
                {
                    if (!ocrEngine.IsAvailable)
                    {
                        var message = $"[OcrTranslate] {label} OCR engine unavailable";
                        Debug.WriteLine(message);
                        CrashDiagnostics.Log(message);
                        ReportFailure(OcrFailureReason.EngineUnavailable);
                        return null;
                    }

                    var preferredLanguage = GetPreferredOcrLanguage();
                    var ocrResult = await ocrEngine.RecognizeAsync(
                        capture, preferredLanguage, cts.Token).ConfigureAwait(false);

                    cts.Token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(ocrResult.Text))
                    {
                        Debug.WriteLine($"[OcrTranslate] No text recognized ({label})");
                        ReportFailure(OcrFailureReason.NoTextRecognized);
                        return null;
                    }

                    Debug.WriteLine($"[OcrTranslate] {label}: {ocrResult.Text.Length} chars recognized");
                    recognizedText = ocrResult.Text;
                    return recognizedText;
                }
                finally
                {
                    if (ownedByCaller)
                    {
                        await DisposeAsync(ocrEngine).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (TimeoutException ex)
        {
            var message = $"[OcrTranslate] {label} timed out: {ex.Message}";
            Debug.WriteLine(message);
            CrashDiagnostics.Log(message);
            ReportFailure(OcrFailureReason.Failed);
            return null;
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            Debug.WriteLine($"[OcrTranslate] {label} cancelled");
            return null;
        }
        catch (OperationCanceledException ex)
        {
            var message = $"[OcrTranslate] {label} cancelled unexpectedly: {ex.Message}";
            Debug.WriteLine(message);
            CrashDiagnostics.Log(message);
            ReportFailure(OcrFailureReason.Failed);
            return null;
        }
        catch (Exception ex)
        {
            var message = $"[OcrTranslate] {label} error: {ex.Message}";
            Debug.WriteLine(message);
            CrashDiagnostics.Log(message);
            ReportFailure(OcrFailureReason.Failed);
            return null;
        }
        finally
        {
            // Covers the paths that end without a failure report (cancelled mid-recognition).
            // On success the caller owns the status, so leave the spinner up until the query starts.
            if (recognizedText is null)
            {
                ClearBusyStatus(messageKey: null);
            }
            if (pipelineLockAcquired)
            {
                _ocrPipelineLock.Release();
            }
            Interlocked.CompareExchange(ref _currentCts, null, cts);
        }
    }

    /// <summary>
    /// True between the screenshot and the result, while the mini window shows the OCR
    /// status. Only one pipeline runs at a time, so a plain flag is enough.
    /// </summary>
    private volatile bool _busyStatusShown;

    private void ShowRecognizingStatus()
    {
        _busyStatusShown = true;
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            MiniWindowService.Instance.ShowBusy(
                LocalizationService.Instance.GetString("StatusRecognizingText"));
        }))
        {
            _busyStatusShown = false;
            Debug.WriteLine("[OcrTranslate] Failed to enqueue OCR status — dispatcher shut down?");
        }
    }

    /// <summary>
    /// End the OCR status phase when no translation will follow. No-op unless the status
    /// is actually showing, so the failure message set here is not wiped by the caller.
    /// The message is resolved on the UI thread, where the localization resources live.
    /// </summary>
    private void ClearBusyStatus(string? messageKey)
    {
        if (!_busyStatusShown) return;
        _busyStatusShown = false;

        _dispatcherQueue.TryEnqueue(() => MiniWindowService.Instance.ClearBusy(
            messageKey is null ? null : LocalizationService.Instance.GetString(messageKey)));
    }

    private async Task<(IOcrService Engine, bool OwnedByCaller)> GetOcrEngineAsync(OcrServiceOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (options.Engine != OcrEngineType.PpOcrV6)
        {
            await DisposePpOcrV6ClientAsync().ConfigureAwait(false);
            return (OcrServiceFactory.Create(options), true);
        }

        var settings = SettingsService.Instance;
        var key = new PpOcrV6ClientKey(
            options.Model,
            Math.Clamp(
                settings.PpOcrV6ThreadCount,
                PpOcrV6ModelCatalog.MinThreadCount,
                PpOcrV6ModelCatalog.MaxThreadCount),
            settings.PpOcrV6UseGpu,
            settings.PpOcrV6AllowFallback);
        if (_ppOcrV6Client is null || _ppOcrV6ClientKey != key)
        {
            await DisposePpOcrV6ClientAsync().ConfigureAwait(false);
            _ppOcrV6Client = new OcrWorkerClient(
                settings,
                new WindowsOcrService(),
                OcrEngineType.PpOcrV6,
                options.Model,
                key.ThreadCount,
                key.AllowFallback,
                key.UseGpu);
            _ppOcrV6ClientKey = key;
        }

        return (_ppOcrV6Client, false);
    }

    private static async ValueTask DisposeAsync(IOcrService service)
    {
        if (service is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            (service as IDisposable)?.Dispose();
        }
    }

    internal async Task ReleasePpOcrV6ModelAsync(string modelId)
    {
        if (_disposed || _ppOcrV6ClientKey?.ModelId != modelId)
        {
            return;
        }

        try
        {
            _currentCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (!await _ocrPipelineLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            throw new IOException("PP-OCRv6 is busy and could not be released safely.");
        }

        try
        {
            await DisposePpOcrV6ClientAsync().ConfigureAwait(false);
        }
        finally
        {
            _ocrPipelineLock.Release();
        }
    }

    private void DisposePpOcrV6Client()
    {
        _ppOcrV6Client?.Dispose();
        _ppOcrV6Client = null;
        _ppOcrV6ClientKey = null;
    }

    private async ValueTask DisposePpOcrV6ClientAsync()
    {
        var client = Interlocked.Exchange(ref _ppOcrV6Client, null);
        _ppOcrV6ClientKey = null;
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cts = Interlocked.Exchange(ref _currentCts, null);
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        DisposePpOcrV6Client();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cts = Interlocked.Exchange(ref _currentCts, null);
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await _ocrPipelineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposePpOcrV6ClientAsync().ConfigureAwait(false);
        }
        finally
        {
            _ocrPipelineLock.Release();
        }
    }

    private readonly record struct PpOcrV6ClientKey(
        string ModelId,
        int ThreadCount,
        bool UseGpu,
        bool AllowFallback);

    internal static void CancelPreviousOperation(CancellationTokenSource? previousCts)
    {
        try
        {
            previousCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous operation owner completed between the exchange and cancellation.
        }
    }

    private void ReportFailure(OcrFailureReason reason)
    {
        // Say what went wrong where the user is already looking, not only in the dialog.
        ClearBusyStatus(reason switch
        {
            OcrFailureReason.NoTextRecognized => "OcrNoTextRecognized",
            OcrFailureReason.EngineUnavailable => "OcrEngineUnavailable",
            _ => "OcrFailed"
        });

        if (_failureReporter is null)
        {
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _failureReporter(reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OcrTranslate] Failed to report OCR failure: {ex.Message}");
            }
        }))
        {
            Debug.WriteLine("[OcrTranslate] Failed to enqueue OCR failure notification — dispatcher shut down?");
        }
    }

    /// <summary>
    /// Gets the list of OCR languages available on the system.
    /// </summary>
    public IReadOnlyList<Models.OcrLanguage> GetAvailableLanguages()
    {
        var service = OcrServiceFactory.Create();
        try
        {
            return service.GetAvailableLanguages();
        }
        finally
        {
            (service as IDisposable)?.Dispose();
        }
    }

    private static string? GetPreferredOcrLanguage()
    {
        var setting = SettingsService.Instance.OcrLanguage;
        return string.IsNullOrEmpty(setting) || setting == "auto" ? null : setting;
    }

    /// <summary>
    /// Logs the OCR engine actually resolved for this flow, plus the current process id.
    /// Helps diagnose settings-desync reports (e.g. issue #176) where a hotkey and the
    /// in-app button appear to use different engines — divergent engines across the same
    /// setting indicate the triggers ran in different processes.
    /// </summary>
    private static void LogOcrDiagnostics(string flow, OcrServiceOptions options)
    {
        var settings = SettingsService.Instance;
        var engineDetails = options.Engine is Models.OcrEngineType.WindowsNative
            ? $"useWorker={settings.UseOcrWorker}"
            : $"endpoint={FormatEndpointForDiagnostics(options)} model={options.Model} thinking={options.EnableThinking}";
        var message =
            $"[OcrTranslate] {flow} pid={Environment.ProcessId} engine={options.Engine} {engineDetails}";
        Debug.WriteLine(message);
        CrashDiagnostics.Log(message);
    }

    internal static string FormatEndpointForDiagnostics(OcrServiceOptions options) =>
        OcrServiceOptions.IsKnownDefaultEndpoint(options.Endpoint)
            ? options.Endpoint
            : "<redacted>";
}
