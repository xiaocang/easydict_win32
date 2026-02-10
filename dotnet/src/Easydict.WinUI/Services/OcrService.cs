using System.Diagnostics;
using Easydict.WinUI.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Easydict.WinUI.Services;

/// <summary>
/// OCR recognition service wrapping Windows.Media.Ocr (WinRT).
/// All recognition runs on a background thread to avoid blocking the UI.
/// </summary>
public sealed class OcrService
{
    /// <summary>
    /// Recognizes text in an image.
    /// This method is safe to call from any thread — recognition runs asynchronously.
    /// </summary>
    /// <param name="pixelData">Raw BGRA8 pixel data.</param>
    /// <param name="pixelWidth">Image width in pixels.</param>
    /// <param name="pixelHeight">Image height in pixels.</param>
    /// <param name="preferredLanguageTag">
    /// BCP-47 language tag to use for recognition (e.g. "zh-Hans-CN").
    /// Pass null or empty to auto-detect from user profile languages.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>OCR result with recognized text and line information.</returns>
    public async Task<OcrResult> RecognizeAsync(
        byte[] pixelData,
        int pixelWidth,
        int pixelHeight,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        // Create SoftwareBitmap on a background thread
        var bitmap = CreateSoftwareBitmap(pixelData, pixelWidth, pixelHeight);

        try
        {
            return await RecognizeBitmapAsync(bitmap, preferredLanguageTag, cancellationToken);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>
    /// Recognizes text from a <see cref="ScreenCaptureResult"/>.
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(
        ScreenCaptureResult capture,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        return await RecognizeAsync(
            capture.PixelData,
            capture.PixelWidth,
            capture.PixelHeight,
            preferredLanguageTag,
            cancellationToken);
    }

    /// <summary>
    /// Gets the list of OCR languages currently available on the system.
    /// </summary>
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages()
    {
        return OcrEngine.AvailableRecognizerLanguages
            .Select(lang => new OcrLanguage
            {
                Tag = lang.LanguageTag,
                DisplayName = lang.DisplayName
            })
            .ToList();
    }

    private static SoftwareBitmap CreateSoftwareBitmap(byte[] pixelData, int width, int height)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixelData.AsBuffer());
        return bitmap;
    }

    private static async Task<OcrResult> RecognizeBitmapAsync(
        SoftwareBitmap bitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken)
    {
        var engine = CreateEngine(preferredLanguageTag);
        if (engine is null)
        {
            Debug.WriteLine("[OcrService] No OCR engine available");
            return new OcrResult();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var winResult = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

        var lines = winResult.Lines.Select(ConvertLine).ToList();
        var sortedLines = OcrTextMerger.GroupAndSortLines(lines);
        var text = OcrTextMerger.MergeLines(sortedLines);

        return new OcrResult
        {
            Text = text,
            Lines = sortedLines,
            TextAngle = winResult.TextAngle,
            DetectedLanguage = DetectLanguage(engine)
        };
    }

    private static OcrEngine? CreateEngine(string? preferredLanguageTag)
    {
        if (!string.IsNullOrEmpty(preferredLanguageTag))
        {
            try
            {
                var lang = new Windows.Globalization.Language(preferredLanguageTag);
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine is not null) return engine;
                Debug.WriteLine($"[OcrService] Language '{preferredLanguageTag}' not available, falling back to profile");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OcrService] Failed to create engine for '{preferredLanguageTag}': {ex.Message}");
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static OcrLine ConvertLine(Windows.Media.Ocr.OcrLine winLine)
    {
        var words = winLine.Words.Select(w => w.Text).ToList();
        var text = OcrTextMerger.MergeWords(words);

        // Calculate bounding rect as union of all word rects
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var word in winLine.Words)
        {
            var r = word.BoundingRect;
            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.X + r.Width > maxX) maxX = r.X + r.Width;
            if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
        }

        return new OcrLine
        {
            Text = text,
            BoundingRect = new OcrRect(minX, minY, maxX - minX, maxY - minY)
        };
    }

    private static OcrLanguage? DetectLanguage(OcrEngine engine)
    {
        var lang = engine.RecognizerLanguage;
        return lang is null ? null : new OcrLanguage
        {
            Tag = lang.LanguageTag,
            DisplayName = lang.DisplayName
        };
    }
}
