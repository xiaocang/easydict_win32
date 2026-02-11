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
    private readonly IOcrService _ocrService;
    private readonly DispatcherQueue _dispatcherQueue;

    // Concurrency guard: only one OCR operation can run at a time.
    // Owned by OcrTranslateAsync/SilentOcrAsync — only those methods create and dispose.
    // Other code may Cancel() but must NOT Dispose().
    private CancellationTokenSource? _currentCts;

    public OcrTranslateService(DispatcherQueue dispatcherQueue)
        : this(new WindowsOcrService(), dispatcherQueue)
    {
    }

    public OcrTranslateService(IOcrService ocrService, DispatcherQueue dispatcherQueue)
    {
        _ocrService = ocrService;
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// Capture screenshot → OCR → show result in MiniWindow for translation.
    /// Safe to call from any thread. Cancels any in-flight OCR operation.
    /// </summary>
    public async Task OcrTranslateAsync()
    {
        Debug.WriteLine("[OcrTranslate] Starting OCR translate flow...");

        using var cts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _currentCts, cts);
        try { previousCts?.Cancel(); } catch (ObjectDisposedException) { }

        try
        {
            var capture = await _captureService.CaptureRegionAsync();
            if (capture is null) return;

            cts.Token.ThrowIfCancellationRequested();

            using (capture)
            {
                var preferredLanguage = GetPreferredOcrLanguage();
                var ocrResult = await _ocrService.RecognizeAsync(
                    capture, preferredLanguage, cts.Token);

                if (string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    Debug.WriteLine("[OcrTranslate] No text recognized");
                    return;
                }

                Debug.WriteLine($"[OcrTranslate] Recognized {ocrResult.Text.Length} chars, showing in MiniWindow");

                // Marshal to UI thread to show the MiniWindow
                if (!_dispatcherQueue.TryEnqueue(() =>
                {
                    MiniWindowService.Instance.ShowWithText(ocrResult.Text);
                }))
                {
                    Debug.WriteLine("[OcrTranslate] Failed to enqueue UI update — dispatcher shut down?");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[OcrTranslate] Operation cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OcrTranslate] Error: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentCts, null, cts);
        }
    }

    /// <summary>
    /// Capture screenshot → OCR → copy result to clipboard (silent mode).
    /// Safe to call from any thread. Cancels any in-flight OCR operation.
    /// </summary>
    public async Task SilentOcrAsync()
    {
        Debug.WriteLine("[OcrTranslate] Starting silent OCR flow...");

        using var cts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _currentCts, cts);
        try { previousCts?.Cancel(); } catch (ObjectDisposedException) { }

        try
        {
            var capture = await _captureService.CaptureRegionAsync();
            if (capture is null) return;

            cts.Token.ThrowIfCancellationRequested();

            using (capture)
            {
                var preferredLanguage = GetPreferredOcrLanguage();
                var ocrResult = await _ocrService.RecognizeAsync(
                    capture, preferredLanguage, cts.Token);

                if (string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    Debug.WriteLine("[OcrTranslate] No text recognized (silent)");
                    return;
                }

                Debug.WriteLine($"[OcrTranslate] Silent OCR: {ocrResult.Text.Length} chars → clipboard");

                // Copy to clipboard on UI thread
                if (!_dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                        dataPackage.SetText(ocrResult.Text);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OcrTranslate] Clipboard error: {ex.Message}");
                    }
                }))
                {
                    Debug.WriteLine("[OcrTranslate] Failed to enqueue clipboard write — dispatcher shut down?");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[OcrTranslate] Silent OCR cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OcrTranslate] Silent OCR error: {ex.Message}");
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
        return _ocrService.GetAvailableLanguages();
    }

    private static string? GetPreferredOcrLanguage()
    {
        var setting = SettingsService.Instance.OcrLanguage;
        return string.IsNullOrEmpty(setting) || setting == "auto" ? null : setting;
    }
}
