using Easydict.TextLayout.Layout;
using Easydict.TextLayout.Preparation;

namespace Easydict.TextLayout;

/// <summary>
/// Two-phase text layout engine inspired by Pretext's prepare-then-layout architecture.
/// The preparation phase segments and measures text. The layout phase uses pure arithmetic
/// for line breaking — no measurement calls.
/// </summary>
public interface ITextLayoutEngine
{
    /// <summary>
    /// Preparation phase: normalize, segment, measure, and cache text data.
    /// The result is reusable for multiple layout calls at the same font size.
    /// </summary>
    PreparedParagraph Prepare(TextPrepareRequest request, ITextMeasurer measurer);

    /// <summary>
    /// Count-only layout: returns line count and max width without allocating line strings.
    /// Fast path for font fitting.
    /// </summary>
    LayoutResult Layout(PreparedParagraph prepared, double maxWidth);

    /// <summary>
    /// Fixed-width layout: produces line strings and geometry.
    /// </summary>
    LayoutLinesResult LayoutWithLines(PreparedParagraph prepared, double maxWidth);

    /// <summary>
    /// Variable-width count-only layout: returns line count without allocating line strings.
    /// Fast path for font fitting with per-line widths.
    /// </summary>
    LayoutResult Layout(PreparedParagraph prepared, IReadOnlyList<double> maxWidths);

    /// <summary>
    /// Variable-width layout: each line can have a different max width.
    /// If text exceeds the number of widths, continues using the last width.
    /// </summary>
    LayoutLinesResult LayoutWithLines(PreparedParagraph prepared, IReadOnlyList<double> maxWidths);

    /// <summary>
    /// Geometry-only walk: calls <paramref name="onLine"/> for each line without allocating strings.
    /// Returns total line count.
    /// </summary>
    int WalkLineRanges(PreparedParagraph prepared, double maxWidth, Action<LayoutLineRange> onLine);

    /// <summary>
    /// Incremental layout: lays out one line at a time starting from <paramref name="start"/>.
    /// Returns null when all text has been consumed.
    /// Used for MuPdf content stream generation where each line emits PDF operators.
    /// </summary>
    LayoutLine? LayoutNextLine(PreparedParagraph prepared, LayoutCursor start, double maxWidth);
}
