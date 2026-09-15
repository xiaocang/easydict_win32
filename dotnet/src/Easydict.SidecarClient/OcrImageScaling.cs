namespace Easydict.SidecarClient;

/// <summary>
/// Upscaling helpers for OCR pre-processing.
/// Windows OCR needs glyphs roughly 30-40 px tall to recognize them reliably, but a
/// screenshot taken on a laptop panel (no display scaling, small UI font) often carries
/// text barely 12-16 px tall. Recognizing such a capture a second time at a larger size
/// recovers text the native-resolution pass drops entirely.
/// Pure logic with no WinRT dependency so it can be unit tested on any platform, and no
/// dependency on any host's OCR model types so it is shared by both the in-process
/// <c>WindowsOcrService</c> (Easydict.WinUI) and the out-of-process OCR worker
/// (Easydict.Workers.Ocr) — both engines need the same retry, or small text silently fails
/// depending on whether OCR worker isolation is enabled (see issue #217).
/// </summary>
public static class OcrImageScaling
{
    /// <summary>Line height, in pixels, the retry pass aims for.</summary>
    public const double TargetLineHeight = 40.0;

    /// <summary>Lines at least this tall recognize well enough that a retry is not worth its cost.</summary>
    public const double AcceptableLineHeight = 26.0;

    /// <summary>Scale used when the first pass recognized nothing at all and there is no height to measure.</summary>
    public const double BlindRetryScale = 2.0;

    /// <summary>Retries below this factor cannot change the outcome enough to pay for a second pass.</summary>
    public const double MinUsefulScale = 1.25;

    public const double MaxScale = 4.0;

    /// <summary>Upper bound on the retry bitmap, keeping the extra buffer under ~64 MB.</summary>
    public const long MaxScaledPixelCount = 16_000_000;

    /// <summary>
    /// Decides how much to enlarge the capture for a second recognition pass.
    /// Returns 1.0 when the first pass already worked on large enough text, when the
    /// image cannot be enlarged within the engine limits, or when the gain would be marginal.
    /// </summary>
    /// <param name="lineHeights">Heights, in source-image pixels, of the lines the first pass recognized.</param>
    /// <param name="width">Source image width in pixels.</param>
    /// <param name="height">Source image height in pixels.</param>
    /// <param name="maxDimension">Largest edge the OCR engine accepts (<c>OcrEngine.MaxImageDimension</c>).</param>
    public static double ComputeRetryScale(
        IReadOnlyList<double> lineHeights,
        int width,
        int height,
        int maxDimension)
    {
        if (width <= 0 || height <= 0 || maxDimension <= 0) return 1.0;
        if (width > maxDimension || height > maxDimension) return 1.0;

        var desired = DesiredScale(lineHeights);
        if (desired <= 1.0) return 1.0;

        desired = Math.Min(desired, MaxScale);

        // Stay inside the engine's dimension limit and the memory budget.
        var dimensionLimit = Math.Min(
            (double)maxDimension / width,
            (double)maxDimension / height);
        var pixelLimit = Math.Sqrt((double)MaxScaledPixelCount / ((double)width * height));
        desired = Math.Min(desired, Math.Min(dimensionLimit, pixelLimit));

        return desired < MinUsefulScale ? 1.0 : desired;
    }

    private static double DesiredScale(IReadOnlyList<double> lineHeights)
    {
        var medianHeight = MedianLineHeight(lineHeights);

        // Nothing measurable came back: the text may be too small for the engine to
        // find any line at all, so enlarge blindly and try once more.
        if (medianHeight <= 0) return BlindRetryScale;

        return medianHeight >= AcceptableLineHeight ? 1.0 : TargetLineHeight / medianHeight;
    }

    /// <summary>
    /// Median of the given line heights, ignoring degenerate (non-positive) entries.
    /// Returns 0 when no entry carries a usable height.
    /// </summary>
    public static double MedianLineHeight(IReadOnlyList<double> lineHeights)
    {
        if (lineHeights is null || lineHeights.Count == 0) return 0;

        var heights = lineHeights
            .Where(h => h > 0)
            .OrderBy(h => h)
            .ToArray();

        if (heights.Length == 0) return 0;

        var middle = heights.Length / 2;
        return heights.Length % 2 == 1
            ? heights[middle]
            : (heights[middle - 1] + heights[middle]) / 2.0;
    }

    /// <summary>
    /// Whether the enlarged pass produced a better reading than the native-resolution pass.
    /// Windows OCR fails on small text by dropping characters and whole lines, so the pass
    /// that recovered more non-whitespace characters is the better one.
    /// </summary>
    public static bool ShouldPreferRetry(string? originalText, string? retryText)
    {
        return CountRecognizedChars(retryText) > CountRecognizedChars(originalText);
    }

    private static int CountRecognizedChars(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c)) count++;
        }

        return count;
    }

    /// <summary>
    /// Maps a single bounding rectangle recognized on an enlarged image back onto
    /// source-image coordinates, so callers keep working in the coordinate space of the
    /// original capture.
    /// </summary>
    public static (double X, double Y, double Width, double Height) MapRect(
        double x, double y, double width, double height, double scaleX, double scaleY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleY);

        return (x / scaleX, y / scaleY, width / scaleX, height / scaleY);
    }

    /// <summary>
    /// Target size for a retry pass, rounded to whole pixels and never smaller than the source.
    /// </summary>
    public static (int Width, int Height) ScaledSize(int width, int height, double scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);

        return (
            Math.Max(width, (int)Math.Round(width * scale)),
            Math.Max(height, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Bilinear resampling of a BGRA8 buffer. Bilinear keeps glyph strokes continuous when
    /// enlarging, where nearest-neighbour would only produce larger, equally blocky text.
    /// </summary>
    public static byte[] ScaleBgra(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int targetWidth,
        int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);

        var requiredLength = (long)width * height * 4;
        if (source.Length < requiredLength)
        {
            throw new ArgumentException(
                $"source length ({source.Length}) is less than expected ({requiredLength}) for {width}x{height} BGRA8",
                nameof(source));
        }

        var destination = new byte[(long)targetWidth * targetHeight * 4];
        var xRatio = (double)width / targetWidth;
        var yRatio = (double)height / targetHeight;
        var sourceStride = width * 4;

        for (var y = 0; y < targetHeight; y++)
        {
            // Sample at pixel centres so the enlarged image stays aligned with the source.
            var srcY = ((y + 0.5) * yRatio) - 0.5;
            var y0 = (int)Math.Floor(srcY);
            var fy = srcY - y0;
            if (y0 < 0) { y0 = 0; fy = 0; }
            if (y0 >= height - 1) { y0 = height - 1; fy = 0; }
            var y1 = Math.Min(y0 + 1, height - 1);

            var row0 = y0 * sourceStride;
            var row1 = y1 * sourceStride;
            var destRow = y * targetWidth * 4;

            for (var x = 0; x < targetWidth; x++)
            {
                var srcX = ((x + 0.5) * xRatio) - 0.5;
                var x0 = (int)Math.Floor(srcX);
                var fx = srcX - x0;
                if (x0 < 0) { x0 = 0; fx = 0; }
                if (x0 >= width - 1) { x0 = width - 1; fx = 0; }
                var x1 = Math.Min(x0 + 1, width - 1);

                var i00 = row0 + (x0 * 4);
                var i01 = row0 + (x1 * 4);
                var i10 = row1 + (x0 * 4);
                var i11 = row1 + (x1 * 4);
                var destIndex = destRow + (x * 4);

                for (var channel = 0; channel < 4; channel++)
                {
                    var top = (source[i00 + channel] * (1 - fx)) + (source[i01 + channel] * fx);
                    var bottom = (source[i10 + channel] * (1 - fx)) + (source[i11 + channel] * fx);
                    var value = (top * (1 - fy)) + (bottom * fy);
                    destination[destIndex + channel] = (byte)Math.Clamp(Math.Round(value), 0, 255);
                }
            }
        }

        return destination;
    }
}
