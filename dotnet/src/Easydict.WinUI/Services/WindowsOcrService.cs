using System.Diagnostics;
using Easydict.SidecarClient;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.Memory;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace Easydict.WinUI.Services;

/// <summary>
/// OCR service using the built-in Windows.Media.Ocr (WinRT) engine.
/// Supports 26+ languages via installed Windows language packs.
/// All recognition runs on a background thread to avoid blocking the UI.
/// </summary>
public sealed class WindowsOcrService : IOcrService
{
    private bool? _isAvailable;

    public string ServiceId => "windows_ocr";

    public string DisplayName => "Windows OCR";

    public bool IsAvailable => _isAvailable ??=
        WinOcr.OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    /// <inheritdoc />
    public Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> pixelData,
        int pixelWidth,
        int pixelHeight,
        string? preferredLanguageTag = null,
        CancellationToken cancellationToken = default)
        => RecognizeAsync(pixelData, pixelWidth, pixelHeight, preferredLanguageTag, cancellationToken, null);

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> pixelData,
        int pixelWidth,
        int pixelHeight,
        string? preferredLanguageTag,
        CancellationToken cancellationToken,
        Action? onRetry)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        var expectedLength = pixelWidth * pixelHeight * 4; // BGRA8
        if (pixelData.Length < expectedLength)
            throw new ArgumentException(
                $"pixelData length ({pixelData.Length}) is less than expected ({expectedLength}) for {pixelWidth}x{pixelHeight} BGRA8",
                nameof(pixelData));

        var engine = CreateEngine(preferredLanguageTag);
        if (engine is null)
        {
            Debug.WriteLine("[WindowsOcrService] No OCR engine available");
            return new OcrResult();
        }

        var result = await RecognizePixelsAsync(
            engine, pixelData, pixelWidth, pixelHeight, cancellationToken);

        return await RefineWithUpscaledPassAsync(
            engine, result, pixelData, pixelWidth, pixelHeight, cancellationToken, onRetry);
    }

    /// <summary>
    /// Screenshots taken on a laptop panel often carry text too small for the engine, which
    /// then returns partial text or nothing at all. When the first pass came back with
    /// small (or no) lines, recognize the capture again enlarged and keep the better reading.
    /// </summary>
    private static async Task<OcrResult> RefineWithUpscaledPassAsync(
        WinOcr.OcrEngine engine,
        OcrResult firstPass,
        ReadOnlyMemory<byte> pixelData,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken,
        Action? onRetry)
    {
        var lineHeights = firstPass.Lines.Select(line => line.BoundingRect.Height).ToList();
        var scale = OcrImageScaling.ComputeRetryScale(
            lineHeights, pixelWidth, pixelHeight, (int)WinOcr.OcrEngine.MaxImageDimension);
        if (scale <= 1.0) return firstPass;

        cancellationToken.ThrowIfCancellationRequested();

        var (scaledWidth, scaledHeight) = OcrImageScaling.ScaledSize(pixelWidth, pixelHeight, scale);
        Debug.WriteLine(
            $"[WindowsOcrService] Retrying at {scaledWidth}x{scaledHeight} (x{scale:F2}) — " +
            $"first pass median line height {OcrImageScaling.MedianLineHeight(lineHeights):F1}px");

        onRetry?.Invoke();

        byte[] scaledPixels;
        try
        {
            scaledPixels = OcrImageScaling.ScaleBgra(
                pixelData.Span, pixelWidth, pixelHeight, scaledWidth, scaledHeight);
        }
        catch (OutOfMemoryException ex)
        {
            Debug.WriteLine($"[WindowsOcrService] Upscale skipped: {ex.Message}");
            return firstPass;
        }

        try
        {
            var secondPass = await RecognizePixelsAsync(
                engine, scaledPixels, scaledWidth, scaledHeight, cancellationToken);

            if (!OcrImageScaling.ShouldPreferRetry(firstPass.Text, secondPass.Text))
            {
                return firstPass;
            }

            return MapToSourceCoordinates(
                secondPass,
                (double)scaledWidth / pixelWidth,
                (double)scaledHeight / pixelHeight);
        }
        finally
        {
            Array.Clear(scaledPixels);
        }
    }

    /// <summary>
    /// Maps a result recognized on an enlarged image back onto source-image coordinates,
    /// so callers keep working in the coordinate space of the original capture.
    /// </summary>
    private static OcrResult MapToSourceCoordinates(OcrResult result, double scaleX, double scaleY)
    {
        if (result.Lines.Count == 0) return result;

        var lines = result.Lines
            .Select(line =>
            {
                var (x, y, w, h) = OcrImageScaling.MapRect(
                    line.BoundingRect.X, line.BoundingRect.Y,
                    line.BoundingRect.Width, line.BoundingRect.Height,
                    scaleX, scaleY);
                return line with { BoundingRect = new OcrRect(x, y, w, h) };
            })
            .ToList();

        return result with { Lines = lines };
    }

    private static async Task<OcrResult> RecognizePixelsAsync(
        WinOcr.OcrEngine engine,
        ReadOnlyMemory<byte> pixelData,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken)
    {
        var bitmap = CreateSoftwareBitmap(pixelData, pixelWidth, pixelHeight);

        try
        {
            return await RecognizeBitmapAsync(engine, bitmap, cancellationToken);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages()
    {
        return WinOcr.OcrEngine.AvailableRecognizerLanguages
            .Select(lang => new OcrLanguage
            {
                Tag = lang.LanguageTag,
                DisplayName = lang.DisplayName
            })
            .ToList();
    }

    private static SoftwareBitmap CreateSoftwareBitmap(ReadOnlyMemory<byte> pixelData, int width, int height)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        byte[]? temporaryArray = null;
        try
        {
            bitmap.CopyFromBuffer(PixelMemory.AsBufferForInterop(pixelData, out temporaryArray));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            if (temporaryArray is not null)
            {
                Array.Clear(temporaryArray);
            }
        }
    }

    private static async Task<OcrResult> RecognizeBitmapAsync(
        WinOcr.OcrEngine engine,
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken)
    {
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

    private static WinOcr.OcrEngine? CreateEngine(string? preferredLanguageTag)
    {
        if (!string.IsNullOrEmpty(preferredLanguageTag))
        {
            try
            {
                var lang = new Windows.Globalization.Language(preferredLanguageTag);
                var engine = WinOcr.OcrEngine.TryCreateFromLanguage(lang);
                if (engine is not null) return engine;
                Debug.WriteLine($"[WindowsOcrService] Language '{preferredLanguageTag}' not available, falling back to profile");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsOcrService] Failed to create engine for '{preferredLanguageTag}': {ex.Message}");
            }
        }

        return WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static OcrLine ConvertLine(WinOcr.OcrLine winLine)
    {
        var wordTexts = new List<string>(winLine.Words.Count);
        var words = new List<OcrWord>(winLine.Words.Count);

        // Calculate bounding rect as union of all word rects, and keep the
        // per-word rects (used by hover word lookup to hit-test the pointer).
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var word in winLine.Words)
        {
            var r = word.BoundingRect;
            wordTexts.Add(word.Text);
            words.Add(new OcrWord
            {
                Text = word.Text,
                BoundingRect = new OcrRect(r.X, r.Y, r.Width, r.Height)
            });

            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.X + r.Width > maxX) maxX = r.X + r.Width;
            if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
        }

        var text = OcrTextMerger.MergeWords(wordTexts);
        var lineRect = words.Count == 0
            ? default
            : new OcrRect(minX, minY, maxX - minX, maxY - minY);

        return new OcrLine
        {
            Text = text,
            BoundingRect = lineRect,
            Words = words
        };
    }

    private static OcrLanguage? DetectLanguage(WinOcr.OcrEngine engine)
    {
        var lang = engine.RecognizerLanguage;
        return lang is null ? null : new OcrLanguage
        {
            Tag = lang.LanguageTag,
            DisplayName = lang.DisplayName
        };
    }
}
