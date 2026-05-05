using Polyglot.TextLayout.Preparation;

namespace Polyglot.TextLayout.FontFitting;

/// <summary>
/// Binary-search font size solver. Finds the largest font size in
/// [MinFontSize, StartFontSize] such that the text fits the given constraints.
/// ~6 iterations for 24pt→6pt at 0.25pt precision (vs 36 linear steps at 0.5pt).
/// </summary>
public static class FontFitSolver
{
    private const double Tolerance = 0.25;

    /// <summary>
    /// Solves for the optimal font size.
    /// </summary>
    /// <param name="request">Fitting constraints and text.</param>
    /// <param name="engine">Layout engine for prepare + layout.</param>
    /// <param name="measurerFactory">
    /// Creates an <see cref="ITextMeasurer"/> for a given font size.
    /// Called once per binary-search iteration.
    /// </param>
    public static FontFitResult Solve(
        FontFitRequest request,
        ITextLayoutEngine engine,
        Func<double, ITextMeasurer> measurerFactory)
    {
        var prepareRequest = new TextPrepareRequest
        {
            Text = request.Text,
            NormalizeWhitespace = request.NormalizeWhitespace,
        };

        // Try original size first (fast path: no shrink needed)
        if (TryFit(request.StartFontSize, request, engine, measurerFactory, prepareRequest, out var lineCount))
        {
            var lineHeight = request.StartFontSize * request.LineHeightMultiplier;
            return new FontFitResult(request.StartFontSize, lineHeight, WasShrunk: false, WasTruncated: false, lineCount);
        }

        // Binary search for largest fitting font size
        var lo = request.MinFontSize;
        var hi = request.StartFontSize;
        var bestSize = lo;
        var bestLineCount = 0;
        var bestFits = false;

        while (hi - lo > Tolerance)
        {
            var mid = (lo + hi) / 2.0;
            if (TryFit(mid, request, engine, measurerFactory, prepareRequest, out lineCount))
            {
                bestSize = mid;
                bestLineCount = lineCount;
                bestFits = true;
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        // Check if minimum size fits
        if (!bestFits)
        {
            TryFit(request.MinFontSize, request, engine, measurerFactory, prepareRequest, out bestLineCount);
            bestSize = request.MinFontSize;
        }

        var chosenLineHeight = bestSize * request.LineHeightMultiplier;
        return new FontFitResult(
            bestSize,
            chosenLineHeight,
            WasShrunk: true,
            WasTruncated: !bestFits,
            bestLineCount);
    }

    private static bool TryFit(
        double fontSize,
        FontFitRequest request,
        ITextLayoutEngine engine,
        Func<double, ITextMeasurer> measurerFactory,
        TextPrepareRequest prepareRequest,
        out int lineCount)
    {
        var measurer = measurerFactory(fontSize);
        var prepared = engine.Prepare(prepareRequest, measurer);
        var lineHeight = fontSize * request.LineHeightMultiplier;

        if (request.LineWidths is { Count: > 0 } lineWidths)
        {
            // Line rect mode — use count-only layout to avoid string allocation
            var result = engine.Layout(prepared, lineWidths);
            lineCount = result.LineCount;

            var maxLineCount = request.MaxLineCount ?? lineWidths.Count;
            if (maxLineCount > 0 && lineCount > maxLineCount)
                return false;

            if (request.MaxHeight.HasValue)
            {
                var totalHeight = lineCount * lineHeight;
                if (totalHeight > request.MaxHeight.Value + 0.01)
                    return false;
            }

            // Check font size against line heights
            if (request.LineHeights is { Count: > 0 } lineHeights)
            {
                if (lineCount > lineHeights.Count)
                    return false;

                var minHeight = double.MaxValue;
                for (var i = 0; i < lineCount; i++)
                    minHeight = Math.Min(minHeight, lineHeights[i]);

                if (fontSize > minHeight * 0.98)
                    return false;
            }

            return true;
        }
        else if (request.MaxWidth.HasValue)
        {
            // Block rect mode
            var result = engine.Layout(prepared, request.MaxWidth.Value);
            lineCount = result.LineCount;

            if (request.MaxHeight.HasValue)
            {
                var maxLines = Math.Max(1, (int)Math.Floor(request.MaxHeight.Value / lineHeight));
                return lineCount <= maxLines;
            }

            return true;
        }

        lineCount = 0;
        return true;
    }
}
