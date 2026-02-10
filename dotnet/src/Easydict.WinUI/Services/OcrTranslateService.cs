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
    private readonly OcrService _ocrService = new();
    private readonly DispatcherQueue _dispatcherQueue;

    public OcrTranslateService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// Capture screenshot → OCR → show result in MiniWindow for translation.
    /// Safe to call from any thread.
    /// </summary>
    public async Task OcrTranslateAsync()
    {
        Debug.WriteLine("[OcrTranslate] Starting OCR translate flow...");

        try
        {
            var capture = await _captureService.CaptureRegionAsync();
            if (capture is null) return;

            using (capture)
            {
                var preferredLanguage = GetPreferredOcrLanguage();
                var ocrResult = await _ocrService.RecognizeAsync(capture, preferredLanguage);

                if (string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    Debug.WriteLine("[OcrTranslate] No text recognized");
                    return;
                }

                Debug.WriteLine($"[OcrTranslate] Recognized {ocrResult.Text.Length} chars, showing in MiniWindow");

                // Marshal to UI thread to show the MiniWindow
                _dispatcherQueue.TryEnqueue(() =>
                {
                    MiniWindowService.Instance.ShowWithText(ocrResult.Text);
                });
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
    }

    /// <summary>
    /// Capture screenshot → OCR → copy result to clipboard (silent mode).
    /// Safe to call from any thread.
    /// </summary>
    public async Task SilentOcrAsync()
    {
        Debug.WriteLine("[OcrTranslate] Starting silent OCR flow...");

        try
        {
            var capture = await _captureService.CaptureRegionAsync();
            if (capture is null) return;

            using (capture)
            {
                var preferredLanguage = GetPreferredOcrLanguage();
                var ocrResult = await _ocrService.RecognizeAsync(capture, preferredLanguage);

                if (string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    Debug.WriteLine("[OcrTranslate] No text recognized (silent)");
                    return;
                }

                Debug.WriteLine($"[OcrTranslate] Silent OCR: {ocrResult.Text.Length} chars → clipboard");

                // Copy to clipboard on UI thread
                _dispatcherQueue.TryEnqueue(() =>
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
                });
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
