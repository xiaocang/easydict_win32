using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Interface for OCR (Optical Character Recognition) services.
/// Implementations may use different engines: Windows.Media.Ocr, Tesseract, PaddleOCR, etc.
/// </summary>
public interface IOcrService
{
    /// <summary>
    /// Unique identifier for this OCR service (e.g. "windows_ocr", "tesseract").
    /// </summary>
    string ServiceId { get; }

    /// <summary>
    /// Human-readable display name (e.g. "Windows OCR", "Tesseract").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this OCR engine is available on the current system.
    /// For example, Windows OCR requires language packs to be installed.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Recognizes text in an image.
    /// This method should be safe to call from any thread.
    /// </summary>
    /// <param name="pixelData">Raw BGRA8 pixel data.</param>
    /// <param name="pixelWidth">Image width in pixels.</param>
    /// <param name="pixelHeight">Image height in pixels.</param>
    /// <param name="preferredLanguageTag">
    /// BCP-47 language tag (e.g. "zh-Hans-CN", "en-US").
    /// Pass null to auto-detect or use the engine's default.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>OCR result with recognized text and line information.</returns>
    Task<OcrResult> RecognizeAsync(
        byte[] pixelData,
        int pixelWidth,
        int pixelHeight,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the list of languages supported by this OCR engine on the current system.
    /// </summary>
    IReadOnlyList<OcrLanguage> GetAvailableLanguages();
}

/// <summary>
/// Extension methods for <see cref="IOcrService"/>.
/// </summary>
public static class OcrServiceExtensions
{
    /// <summary>
    /// Recognizes text from a <see cref="ScreenCaptureResult"/>.
    /// Convenience overload that unpacks the capture's pixel data.
    /// </summary>
    public static Task<OcrResult> RecognizeAsync(
        this IOcrService service,
        ScreenCaptureResult capture,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        return service.RecognizeAsync(
            capture.PixelData,
            capture.PixelWidth,
            capture.PixelHeight,
            preferredLanguageTag,
            cancellationToken);
    }
}
