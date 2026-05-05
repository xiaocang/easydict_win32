namespace Polyglot.TextLayout.Layout;

/// <summary>
/// Geometry-only line range without string allocation.
/// Used by <see cref="ITextLayoutEngine.WalkLineRanges"/> for fast counting and width probing.
/// </summary>
public readonly record struct LayoutLineRange(
    int StartSegment,
    int EndSegment,
    int StartGrapheme,
    int EndGrapheme,
    double Width);
