namespace Polyglot.TextLayout;

/// <summary>
/// Measures text advance widths in points at a specific font and size.
/// Implementations are stateful with respect to font/size — create a new instance
/// when font size changes (e.g., during font fitting iterations).
/// </summary>
public interface ITextMeasurer
{
    /// <summary>
    /// Measures the advance width of a multi-character text segment.
    /// </summary>
    double MeasureSegment(string text);

    /// <summary>
    /// Measures the advance width of a single grapheme cluster.
    /// Used for breaking long segments and CJK per-character layout.
    /// </summary>
    double MeasureGrapheme(string grapheme);
}
