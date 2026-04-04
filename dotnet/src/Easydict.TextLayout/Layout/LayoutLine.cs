namespace Easydict.TextLayout.Layout;

/// <summary>
/// A laid-out line of text with content and geometry.
/// </summary>
public sealed record LayoutLine(
    int StartSegment,
    int EndSegment,
    int StartGrapheme,
    int EndGrapheme,
    double Width,
    string Text);
