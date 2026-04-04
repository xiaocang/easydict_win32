using Easydict.TextLayout.Segmentation;

namespace Easydict.TextLayout.Preparation;

/// <summary>
/// The output of the preparation phase: segmented, measured, and cached text data.
/// All arrays are parallel with the same length (<see cref="Count"/>).
/// Layout operations use these arrays without further measurement calls.
/// </summary>
public sealed class PreparedParagraph
{
    /// <summary>Segment text values.</summary>
    public required string[] Segments { get; init; }

    /// <summary>Measured advance width of each segment in points.</summary>
    public required double[] Widths { get; init; }

    /// <summary>Layout classification of each segment.</summary>
    public required SegmentKind[] Kinds { get; init; }

    /// <summary>
    /// Per-grapheme widths within each segment. Non-null for CjkGrapheme segments
    /// and for Word segments whose width exceeds a threshold (populated lazily during layout).
    /// Null for segments that don't need grapheme-level breaking.
    /// </summary>
    public required double[]?[] GraphemeWidths { get; init; }

    /// <summary>
    /// Prefix sums of <see cref="GraphemeWidths"/> for binary-search line breaking
    /// of long segments. <c>GraphemePrefixSums[i][k]</c> = sum of first k+1 grapheme widths
    /// in segment i. Null when <see cref="GraphemeWidths"/> is null for that segment.
    /// </summary>
    public required double[]?[] GraphemePrefixSums { get; init; }

    /// <summary>
    /// Grapheme strings for segments that have been grapheme-decomposed.
    /// Used to reconstruct text when a segment is broken mid-grapheme.
    /// Null when <see cref="GraphemeWidths"/> is null for that segment.
    /// </summary>
    public required string[]?[] Graphemes { get; init; }

    /// <summary>Total width if laid out on a single line (sum of all non-HardBreak Widths).</summary>
    public double TotalWidth { get; init; }

    /// <summary>Number of segments.</summary>
    public int Count => Segments.Length;
}
