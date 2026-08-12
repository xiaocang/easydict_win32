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
public sealed class OcrTranslateService : IDisposable
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

        var text = await RunOcrPipelineAsync("OcrTranslate").ConfigureAwait(false);
        if (text is null) return;

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            MiniWindowService.Instance.ShowWithText(text);
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

    private async Task<string?> RunOcrPipelineAsync(string label)
    {
        using var cts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _currentCts, cts);
        var pipelineLockAcquired = false;
        try
        {
            CancelPreviousOperation(previousCts);
            var capture = await _captureService.CaptureRegionAsync(cts.Token).ConfigureAwait(false);
            if (capture is null) return null;

            cts.Token.ThrowIfCancellationRequested();
            await _ocrPipelineLock.WaitAsync(cts.Token).ConfigureAwait(false);
            pipelineLockAcquired = true;

            using (capture)
            {
                var ocrOptions = OcrServiceOptions.FromSettings(SettingsService.Instance);
                LogOcrDiagnostics(label, ocrOptions);
                var ocrEngine = GetOcrEngine(ocrOptions);
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
                return ocrResult.Text;
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
            if (pipelineLockAcquired)
            {
                _ocrPipelineLock.Release();
            }
            Interlocked.CompareExchange(ref _currentCts, null, cts);
        }
    }

    private IOcrService GetOcrEngine(OcrServiceOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (options.Engine != OcrEngineType.PpOcrV6)
        {
            DisposePpOcrV6Client();
            return OcrServiceFactory.Create(options);
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
            DisposePpOcrV6Client();
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

        return _ppOcrV6Client;
    }

    private void DisposePpOcrV6Client()
    {
        _ppOcrV6Client?.Dispose();
        _ppOcrV6Client = null;
        _ppOcrV6ClientKey = null;
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

        if (_ocrPipelineLock.Wait(TimeSpan.FromSeconds(5)))
        {
            try
            {
                DisposePpOcrV6Client();
            }
            finally
            {
                _ocrPipelineLock.Release();
                _ocrPipelineLock.Dispose();
            }

            return;
        }

        DisposePpOcrV6Client();
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
