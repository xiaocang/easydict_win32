namespace Polyglot.TextLayout.Layout;

/// <summary>
/// Count-only layout result — no line strings allocated. Fast path for font fitting.
/// </summary>
public sealed record LayoutResult(
    int LineCount,
    double MaxLineWidth,
    bool HasOverflow);
