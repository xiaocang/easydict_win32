using System.Text;
using Polyglot.TextLayout.Layout;
using Polyglot.TextLayout.Preparation;
using Polyglot.TextLayout.Segmentation;

namespace Polyglot.TextLayout;

/// <summary>
/// Core text layout engine. Greedy first-fit line breaking with:
/// - Punctuation grouping (close-punct never starts a line, open-punct never ends a line)
/// - Long-segment binary search on grapheme prefix sums
/// - Space trimming at line start (matches pdf2zh converter.py:488)
/// </summary>
public sealed class TextLayoutEngine : ITextLayoutEngine
{
    /// <summary>Singleton instance.</summary>
    public static readonly TextLayoutEngine Instance = new();

    public PreparedParagraph Prepare(TextPrepareRequest request, ITextMeasurer measurer)
    {
        var (segments, kinds) = TextSegmenter.Segment(request.Text, request.NormalizeWhitespace);
        var count = segments.Length;
        var widths = new double[count];
        var lineEndFitAdvances = new double[count];
        var graphemeWidths = new double[]?[count];
        var graphemePrefixSums = new double[]?[count];
        var graphemes = new string[]?[count];
        var isProhibitedLineStart = new bool[count];
        var isProhibitedLineEnd = new bool[count];
        var hardBreakIndices = new List<int>();
        var discretionaryHyphenWidth = 0.0;
        var hasSoftHyphen = false;

        for (var i = 0; i < count; i++)
        {
            var segment = segments[i];
            var kind = kinds[i];

            // Kinsoku flags: check first/last char of each segment
            if (segment.Length > 0)
            {
                isProhibitedLineStart[i] = KinsokuTable.IsProhibitedLineStart(segment[0]);
                isProhibitedLineEnd[i] = KinsokuTable.IsProhibitedLineEnd(segment[^1]);
            }

            if (kind == SegmentKind.HardBreak)
            {
                widths[i] = 0;
                lineEndFitAdvances[i] = 0;
                hardBreakIndices.Add(i);
                continue;
            }

            if (kind == SegmentKind.SoftHyphen)
            {
                widths[i] = 0; // invisible when not at break
                hasSoftHyphen = true;
                // lineEndFitAdvances set below after hyphen width is measured
                continue;
            }

            if (kind == SegmentKind.CjkGrapheme)
            {
                var w = measurer.MeasureGrapheme(segment);
                widths[i] = w;
                lineEndFitAdvances[i] = w;
                continue;
            }

            var segWidth = measurer.MeasureSegment(segment);
            widths[i] = segWidth;

            // Trailing space hangs past the margin (fitAdvance = 0)
            lineEndFitAdvances[i] = kind == SegmentKind.Space ? 0 : segWidth;

            // Pre-compute grapheme widths for word segments (needed for long-segment breaking)
            if (kind == SegmentKind.Word && segment.Length > 1)
            {
                var gList = new List<string>();
                var gwList = new List<double>();
                foreach (var g in ScriptClassifier.EnumerateGraphemes(segment))
                {
                    gList.Add(g);
                    gwList.Add(measurer.MeasureGrapheme(g));
                }

                if (gList.Count > 1)
                {
                    graphemes[i] = gList.ToArray();
                    graphemeWidths[i] = gwList.ToArray();
                    var pfxSums = new double[gwList.Count];
                    pfxSums[0] = gwList[0];
                    for (var j = 1; j < gwList.Count; j++)
                        pfxSums[j] = pfxSums[j - 1] + gwList[j];
                    graphemePrefixSums[i] = pfxSums;
                }
            }
        }

        // Measure discretionary hyphen once (lazy: only if soft-hyphens present)
        if (hasSoftHyphen)
        {
            discretionaryHyphenWidth = measurer.MeasureGrapheme("-");
            // Back-fill lineEndFitAdvances for soft-hyphen segments
            for (var i = 0; i < count; i++)
            {
                if (kinds[i] == SegmentKind.SoftHyphen)
                    lineEndFitAdvances[i] = discretionaryHyphenWidth;
            }
        }

        return new PreparedParagraph
        {
            Segments = segments,
            Widths = widths,
            Kinds = kinds,
            LineEndFitAdvances = lineEndFitAdvances,
            GraphemeWidths = graphemeWidths,
            GraphemePrefixSums = graphemePrefixSums,
            Graphemes = graphemes,
            IsProhibitedLineStart = isProhibitedLineStart,
            IsProhibitedLineEnd = isProhibitedLineEnd,
            HardBreakIndices = hardBreakIndices.ToArray(),
            DiscretionaryHyphenWidth = discretionaryHyphenWidth,
        };
    }

    public LayoutResult Layout(PreparedParagraph prepared, double maxWidth)
    {
        var lineCount = 0;
        var maxLineWidth = 0.0;

        WalkLineRanges(prepared, maxWidth, range =>
        {
            lineCount++;
            if (range.Width > maxLineWidth)
                maxLineWidth = range.Width;
        });

        return new LayoutResult(lineCount, maxLineWidth, HasOverflow: false);
    }

    public LayoutLinesResult LayoutWithLines(PreparedParagraph prepared, double maxWidth)
    {
        var lines = new List<LayoutLine>();
        var maxLineWidth = 0.0;
        var cursor = LayoutCursor.Start;

        while (cursor.SegmentIndex < prepared.Count)
        {
            var line = LayoutNextLine(prepared, cursor, maxWidth);
            if (line is null)
                break;

            lines.Add(line);
            if (line.Width > maxLineWidth)
                maxLineWidth = line.Width;

            cursor = new LayoutCursor(line.EndSegment, line.EndGrapheme);
        }

        return new LayoutLinesResult(lines, maxLineWidth, HasOverflow: false);
    }

    public LayoutResult Layout(PreparedParagraph prepared, IReadOnlyList<double> maxWidths)
    {
        if (maxWidths.Count == 0)
            return new LayoutResult(0, 0, HasOverflow: false);

        var lineCount = 0;
        var maxLineWidth = 0.0;
        var cursor = LayoutCursor.Start;

        while (cursor.SegmentIndex < prepared.Count)
        {
            var width = maxWidths[Math.Min(lineCount, maxWidths.Count - 1)];
            var line = LayoutNextLineCore(prepared, cursor, width, buildText: false);
            if (line is null)
                break;

            lineCount++;
            if (line.Width > maxLineWidth)
                maxLineWidth = line.Width;

            cursor = new LayoutCursor(line.EndSegment, line.EndGrapheme);
        }

        return new LayoutResult(lineCount, maxLineWidth, HasOverflow: false);
    }

    public LayoutLinesResult LayoutWithLines(PreparedParagraph prepared, IReadOnlyList<double> maxWidths)
    {
        if (maxWidths.Count == 0)
            return new LayoutLinesResult([], 0, HasOverflow: false);

        var lines = new List<LayoutLine>();
        var maxLineWidth = 0.0;
        var cursor = LayoutCursor.Start;
        var lineIndex = 0;

        while (cursor.SegmentIndex < prepared.Count)
        {
            var width = maxWidths[Math.Min(lineIndex, maxWidths.Count - 1)];
            var line = LayoutNextLine(prepared, cursor, width);
            if (line is null)
                break;

            lines.Add(line);
            if (line.Width > maxLineWidth)
                maxLineWidth = line.Width;

            cursor = new LayoutCursor(line.EndSegment, line.EndGrapheme);
            lineIndex++;
        }

        return new LayoutLinesResult(lines, maxLineWidth, HasOverflow: false);
    }

    public int WalkLineRanges(PreparedParagraph prepared, double maxWidth, Action<LayoutLineRange> onLine)
    {
        var lineCount = 0;
        var cursor = LayoutCursor.Start;

        while (cursor.SegmentIndex < prepared.Count)
        {
            var line = LayoutNextLineCore(prepared, cursor, maxWidth, buildText: false);
            if (line is null)
                break;

            onLine(new LayoutLineRange(
                line.StartSegment, line.EndSegment,
                line.StartGrapheme, line.EndGrapheme,
                line.Width));
            lineCount++;

            cursor = new LayoutCursor(line.EndSegment, line.EndGrapheme);
        }

        return lineCount;
    }

    public LayoutLine? LayoutNextLine(PreparedParagraph prepared, LayoutCursor start, double maxWidth)
    {
        return LayoutNextLineCore(prepared, start, maxWidth, buildText: true);
    }

    /// <summary>
    /// Core line-breaking algorithm. Greedy first-fit with punctuation grouping.
    /// - Close-punctuation never starts a line (grouped with preceding content, with overflow check)
    /// - Open-punctuation never ends a line (if it would be the last token, defer to next line)
    /// - Kinsoku line-start prohibition: CJK chars like small kana, prolonged sound marks,
    ///   and iteration marks never start a line (carried to the preceding line)
    /// - Left-sticky punctuation: ASCII punct (. , ! ? etc.) after CJK sticks to preceding char
    /// - Trailing spaces trimmed from width calculation consistently
    /// </summary>
    private LayoutLine? LayoutNextLineCore(PreparedParagraph prepared, LayoutCursor start, double maxWidth, bool buildText)
    {
        var segments = prepared.Segments;
        var widths = prepared.Widths;
        var kinds = prepared.Kinds;

        if (start.SegmentIndex >= segments.Length)
            return null;

        var seg = start.SegmentIndex;
        var graphemeOffset = start.GraphemeIndex;

        // If we're resuming mid-segment due to a long-segment break
        if (graphemeOffset > 0 && seg < segments.Length)
        {
            return LayoutLineFromMidSegment(prepared, start, maxWidth, buildText);
        }

        // Skip leading spaces
        while (seg < segments.Length && kinds[seg] == SegmentKind.Space)
            seg++;

        if (seg >= segments.Length)
            return null;

        // Handle hard break at line start
        if (kinds[seg] == SegmentKind.HardBreak)
        {
            return new LayoutLine(seg, seg + 1, 0, 0, 0, string.Empty);
        }

        var lineStartSeg = seg;
        var lineWidth = 0.0;     // includes trailing spaces
        var contentWidth = 0.0;  // excludes trailing spaces
        var lastContentKind = SegmentKind.Space; // track kind of last non-space segment for left-sticky
        var sb = buildText ? new StringBuilder() : null;

        while (seg < segments.Length)
        {
            var kind = kinds[seg];

            // Hard break ends the line
            if (kind == SegmentKind.HardBreak)
            {
                seg++; // consume the hard break
                break;
            }

            var segWidth = widths[seg];

            // Check if this segment fits
            if (lineWidth + segWidth > maxWidth && lineWidth > 0)
            {
                // Kinsoku line-start prohibition: if this segment must not start a line,
                // carry it onto the current line despite overflow. This prevents characters
                // like small kana (っ, ょ), prolonged sound mark (ー), middle dot (・),
                // and iteration marks (々) from appearing at line start.
                // Safety: only carry if the line already has content (lineWidth > 0),
                // and only for narrow segments (CjkGrapheme or ClosePunctuation) to avoid
                // runaway overflow with long Word segments.
                if (prepared.IsProhibitedLineStart[seg]
                    && kind is SegmentKind.CjkGrapheme or SegmentKind.ClosePunctuation)
                {
                    // Fall through to add it to the current line
                }
                // Left-sticky punctuation: ASCII punct like . , ! ? after CJK content
                // sticks to the preceding character and must not start a new line.
                else if (IsLeftStickyBreak(segments[seg], lastContentKind))
                {
                    // Fall through to add it to the current line
                }
                else
                {
                    break;
                }
            }

            // If segment doesn't fit and line IS empty, we must emit it (or break it)
            if (lineWidth + segWidth > maxWidth && lineWidth == 0)
            {
                // Try grapheme-level breaking for long segments
                if (kind == SegmentKind.Word && prepared.GraphemePrefixSums[seg] is not null)
                {
                    return BreakLongSegment(prepared, seg, maxWidth, lineStartSeg, buildText);
                }

                // Can't break — emit entire segment on this line
                sb?.Append(segments[seg]);
                lineWidth += segWidth;
                contentWidth = lineWidth;
                seg++;
                break;
            }

            // Segment fits — add it
            if (kind == SegmentKind.Space)
            {
                // Don't add leading space
                if (lineWidth == 0)
                {
                    seg++;
                    continue;
                }
                lineWidth += segWidth;
                sb?.Append(segments[seg]);
            }
            else
            {
                lineWidth += segWidth;
                sb?.Append(segments[seg]);
                contentWidth = lineWidth;
                lastContentKind = kind;

                // Look ahead: if next segment is ClosePunctuation, group it only if it fits
                while (seg + 1 < segments.Length && kinds[seg + 1] == SegmentKind.ClosePunctuation)
                {
                    var closeWidth = widths[seg + 1];
                    if (lineWidth + closeWidth > maxWidth)
                        break; // let close-punct overflow to next line rather than exceed width
                    seg++;
                    lineWidth += closeWidth;
                    sb?.Append(segments[seg]);
                    contentWidth = lineWidth;
                }
            }

            seg++;

            // Open-punctuation should not end a line: if the next segment is
            // OpenPunctuation and the segment after that won't fit, we'd leave
            // open-punct dangling. Check if next is open-punct and the thing
            // after it would overflow — if so, break now before the open-punct.
            if (seg < segments.Length && kinds[seg] == SegmentKind.OpenPunctuation)
            {
                var openWidth = widths[seg];
                // Peek at what follows the open-punct
                var afterOpenWidth = seg + 1 < segments.Length ? widths[seg + 1] : 0.0;
                if (lineWidth + openWidth + afterOpenWidth > maxWidth && lineWidth > 0)
                {
                    // Break here; open-punct starts the next line
                    break;
                }
            }
        }

        var text = sb?.ToString().TrimEnd() ?? string.Empty;
        return new LayoutLine(lineStartSeg, seg, 0, 0, contentWidth, text);
    }

    /// <summary>
    /// Checks whether breaking before this segment would violate left-sticky rules.
    /// ASCII punctuation (. , ! ? : ; ) ] } % " …) should stick to preceding CJK content.
    /// </summary>
    private static bool IsLeftStickyBreak(string segment, SegmentKind lastContentKind)
    {
        if (segment.Length == 0) return false;
        // Only applies when the preceding content was CJK
        if (lastContentKind != SegmentKind.CjkGrapheme) return false;
        return KinsokuTable.IsLeftSticky(segment[0]);
    }

    /// <summary>
    /// Resume layout from the middle of a segment (after a long-segment break).
    /// </summary>
    private LayoutLine? LayoutLineFromMidSegment(
        PreparedParagraph prepared, LayoutCursor start, double maxWidth, bool buildText)
    {
        var seg = start.SegmentIndex;
        var graphemeStart = start.GraphemeIndex;
        var graphemes = prepared.Graphemes[seg];
        var gWidths = prepared.GraphemeWidths[seg];

        if (graphemes is null || gWidths is null || graphemeStart >= graphemes.Length)
        {
            // Skip to next segment
            return LayoutNextLineCore(prepared, new LayoutCursor(seg + 1, 0), maxWidth, buildText);
        }

        var sb = buildText ? new StringBuilder() : null;
        var lineWidth = 0.0;
        var gi = graphemeStart;

        while (gi < graphemes.Length)
        {
            var gw = gWidths[gi];
            if (lineWidth + gw > maxWidth && lineWidth > 0)
                break;

            sb?.Append(graphemes[gi]);
            lineWidth += gw;
            gi++;
        }

        // If we consumed the entire remaining segment, advance cursor to next segment
        if (gi >= graphemes.Length)
        {
            return new LayoutLine(seg, seg + 1, graphemeStart, 0, lineWidth, sb?.ToString() ?? string.Empty);
        }

        // Stopped mid-segment — next line resumes at (seg, gi)
        return new LayoutLine(seg, seg, graphemeStart, gi, lineWidth, sb?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Binary search on grapheme prefix sums to break a long segment.
    /// </summary>
    private LayoutLine? BreakLongSegment(
        PreparedParagraph prepared, int seg, double maxWidth, int lineStartSeg, bool buildText)
    {
        var prefixSums = prepared.GraphemePrefixSums[seg]!;
        var graphemes = prepared.Graphemes[seg]!;

        // Binary search: find largest k such that prefixSums[k] <= maxWidth
        var lo = 0;
        var hi = prefixSums.Length - 1;
        var bestK = 0;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (prefixSums[mid] <= maxWidth)
            {
                bestK = mid + 1; // can fit mid+1 graphemes
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Must emit at least 1 grapheme
        if (bestK == 0) bestK = 1;

        var text = string.Empty;
        if (buildText)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < bestK; i++)
                sb.Append(graphemes[i]);
            text = sb.ToString();
        }

        var width = bestK > 0 && bestK <= prefixSums.Length ? prefixSums[bestK - 1] : 0;

        return new LayoutLine(lineStartSeg, seg, 0, bestK, width, text);
    }
}
