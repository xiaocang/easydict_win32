using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Easydict.WinUI.Services;

/// <summary>
/// Orchestrates the OCR translation flow: Screenshot → OCR → Translate.
/// All operations are asynchronous and non-blocking to the UI thread.
/// </summary>
public sealed class OcrTranslateService
{
    private readonly ScreenCaptureService _captureService = new();
    private readonly DispatcherQueue _dispatcherQueue;

    // Concurrency guard: only one OCR operation can run at a time.
    // Owned by RunOcrPipelineAsync — only that method creates and disposes.
    // Other code may Cancel() but must NOT Dispose().
    private CancellationTokenSource? _currentCts;

    public OcrTranslateService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
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
        if (previousCts != null)
        {
            previousCts.Cancel();
            _captureService.CancelCurrentCapture();
        }

        try
        {
            var capture = await _captureService.CaptureRegionAsync(cts.Token).ConfigureAwait(false);
            if (capture is null) return null;

            cts.Token.ThrowIfCancellationRequested();

            using (capture)
            {
                var ocrOptions = OcrServiceOptions.FromSettings(SettingsService.Instance);
                LogOcrDiagnostics(label, ocrOptions);
                var ocrEngine = OcrServiceFactory.Create(ocrOptions);
                var preferredLanguage = GetPreferredOcrLanguage();
                var ocrResult = await ocrEngine.RecognizeAsync(
                    capture, preferredLanguage, cts.Token).ConfigureAwait(false);

                cts.Token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    Debug.WriteLine($"[OcrTranslate] No text recognized ({label})");
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
            App.LogToFile(message);
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
            App.LogToFile(message);
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OcrTranslate] {label} error: {ex.Message}");
            return null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentCts, null, cts);
        }
    }

    /// <summary>
    /// Gets the list of OCR languages available on the system.
    /// </summary>
    public IReadOnlyList<Models.OcrLanguage> GetAvailableLanguages()
    {
        return OcrServiceFactory.Create().GetAvailableLanguages();
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
        var message =
            $"[OcrTranslate] {flow} pid={Environment.ProcessId} engine={options.Engine} " +
            $"useWorker={settings.UseOcrWorker} endpoint={FormatEndpointForDiagnostics(options)} " +
            $"model={options.Model}";
        Debug.WriteLine(message);
        App.LogToFile(message);
    }

    internal static string FormatEndpointForDiagnostics(OcrServiceOptions options) =>
        OcrServiceOptions.IsKnownDefaultEndpoint(options.Endpoint)
            ? options.Endpoint
            : "<redacted>";
}
