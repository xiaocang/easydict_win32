namespace Polyglot.TextLayout.Layout;

/// <summary>
/// Full layout result with line text and geometry.
/// </summary>
public sealed record LayoutLinesResult(
    IReadOnlyList<LayoutLine> Lines,
    double MaxLineWidth,
    bool HasOverflow);
