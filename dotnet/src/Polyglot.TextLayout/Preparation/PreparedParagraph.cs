using Polyglot.TextLayout.Segmentation;

namespace Polyglot.TextLayout.Preparation;

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
    /// Advance width contribution when this segment is at the end of a line.
    /// For Space segments: 0 (trailing whitespace hangs past the margin).
    /// For SoftHyphen segments: <see cref="DiscretionaryHyphenWidth"/> (adds visible hyphen).
    /// For all others: equals <see cref="Widths"/>[i].
    /// Inspired by Pretext's lineEndFitAdvances.
    /// </summary>
    public required double[] LineEndFitAdvances { get; init; }

    /// <summary>
    /// Per-grapheme widths within each segment. Populated eagerly during <c>Prepare()</c>
    /// for <see cref="SegmentKind.Word"/> segments with more than one grapheme cluster.
    /// Null for all other segment kinds (CjkGrapheme, Space, HardBreak, punctuation).
    /// </summary>
    public required double[]?[] GraphemeWidths { get; init; }

    /// <summary>
    /// Prefix sums of <see cref="GraphemeWidths"/> for binary-search line breaking
    /// of long segments. <c>GraphemePrefixSums[i][k]</c> = sum of first k+1 grapheme widths
    /// in segment i. Populated eagerly during <c>Prepare()</c> alongside <see cref="GraphemeWidths"/>.
    /// Null when <see cref="GraphemeWidths"/> is null for that segment.
    /// </summary>
    public required double[]?[] GraphemePrefixSums { get; init; }

    /// <summary>
    /// Grapheme strings for segments that have been grapheme-decomposed.
    /// Used to reconstruct text when a segment is broken mid-grapheme.
    /// Populated eagerly during <c>Prepare()</c> alongside <see cref="GraphemeWidths"/>.
    /// Null when <see cref="GraphemeWidths"/> is null for that segment.
    /// </summary>
    public required string[]?[] Graphemes { get; init; }

    /// <summary>
    /// True when the segment's first character is prohibited from starting a line
    /// per Japanese kinsoku shori rules (JIS X 4051). Includes closing brackets,
    /// CJK periods/commas, small kana, prolonged sound mark, iteration marks.
    /// </summary>
    public required bool[] IsProhibitedLineStart { get; init; }

    /// <summary>
    /// True when the segment's last character is prohibited from ending a line
    /// per Japanese kinsoku shori rules. Includes opening brackets and CJK opening marks.
    /// </summary>
    public required bool[] IsProhibitedLineEnd { get; init; }

    /// <summary>
    /// Indices of <see cref="SegmentKind.HardBreak"/> segments within the segments array.
    /// Empty when there are no hard breaks. Used for chunk-based fast-path optimization.
    /// </summary>
    public required int[] HardBreakIndices { get; init; }

    /// <summary>
    /// Width of a discretionary hyphen character, measured once during preparation.
    /// Used as <see cref="LineEndFitAdvances"/> value for <see cref="SegmentKind.SoftHyphen"/> segments.
    /// Zero when no soft-hyphens are present in the text.
    /// </summary>
    public double DiscretionaryHyphenWidth { get; init; }

    /// <summary>True when there are no hard breaks — enables simplified layout path.</summary>
    public bool IsSingleChunk => HardBreakIndices.Length == 0;

    /// <summary>Number of segments.</summary>
    public int Count => Segments.Length;
}
