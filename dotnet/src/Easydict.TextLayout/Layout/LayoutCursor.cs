namespace Easydict.TextLayout.Layout;

/// <summary>
/// A position within a <see cref="Preparation.PreparedParagraph"/> for incremental layout.
/// </summary>
public readonly record struct LayoutCursor(int SegmentIndex, int GraphemeIndex)
{
    /// <summary>Starting cursor at the beginning of text.</summary>
    public static readonly LayoutCursor Start = new(0, 0);
}
