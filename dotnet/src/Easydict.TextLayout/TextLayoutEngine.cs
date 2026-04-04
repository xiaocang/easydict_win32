using System.Text;
using Easydict.TextLayout.Layout;
using Easydict.TextLayout.Preparation;
using Easydict.TextLayout.Segmentation;

namespace Easydict.TextLayout;

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
        var graphemeWidths = new double[]?[count];
        var graphemePrefixSums = new double[]?[count];
        var graphemes = new string[]?[count];
        var totalWidth = 0.0;

        for (var i = 0; i < count; i++)
        {
            var segment = segments[i];
            var kind = kinds[i];

            if (kind == SegmentKind.HardBreak)
            {
                widths[i] = 0;
                continue;
            }

            if (kind == SegmentKind.CjkGrapheme)
            {
                var w = measurer.MeasureGrapheme(segment);
                widths[i] = w;
                totalWidth += w;
                // CJK graphemes are already atomic; grapheme arrays not needed
                continue;
            }

            var segWidth = measurer.MeasureSegment(segment);
            widths[i] = segWidth;

            if (kind != SegmentKind.Space)
                totalWidth += segWidth;

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
                    var prefixSums = new double[gwList.Count];
                    prefixSums[0] = gwList[0];
                    for (var j = 1; j < gwList.Count; j++)
                        prefixSums[j] = prefixSums[j - 1] + gwList[j];
                    graphemePrefixSums[i] = prefixSums;
                }
            }
        }

        return new PreparedParagraph
        {
            Segments = segments,
            Widths = widths,
            Kinds = kinds,
            GraphemeWidths = graphemeWidths,
            GraphemePrefixSums = graphemePrefixSums,
            Graphemes = graphemes,
            TotalWidth = totalWidth,
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

        // Ensure at least 1 line for non-empty text
        if (lineCount == 0 && prepared.Count > 0)
            lineCount = 1;

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
    /// </summary>
    private LayoutLine? LayoutNextLineCore(PreparedParagraph prepared, LayoutCursor start, double maxWidth, bool buildText)
    {
        var segments = prepared.Segments;
        var widths = prepared.Widths;
        var kinds = prepared.Kinds;

        if (start.SegmentIndex >= segments.Length)
            return null;

        // Skip leading spaces at line start (but not at text start on first segment with grapheme offset)
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
            return new LayoutLine(seg, seg + 1, 0, 0, 0, buildText ? string.Empty : string.Empty);
        }

        var lineStartSeg = seg;
        var lineWidth = 0.0;
        var sb = buildText ? new StringBuilder() : null;
        var lastNonSpaceEnd = seg; // track last non-space segment end for trimming

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
                // Segment doesn't fit and line is non-empty — break before this segment
                // But check: if next is ClosePunctuation, we should have included it with the previous word
                // This is handled naturally because close-punct is attached to words during segmentation
                break;
            }

            // If segment doesn't fit and line IS empty, we must emit it (or break it)
            if (lineWidth + segWidth > maxWidth && lineWidth == 0)
            {
                // Try grapheme-level breaking for long segments
                if (kind == SegmentKind.Word && prepared.GraphemePrefixSums[seg] is { } prefixSums)
                {
                    return BreakLongSegment(prepared, seg, maxWidth, lineStartSeg, buildText);
                }

                // Can't break — emit entire segment on this line
                sb?.Append(segments[seg]);
                lineWidth += segWidth;
                seg++;
                lastNonSpaceEnd = seg;
                break;
            }

            // Segment fits — add it
            if (kind == SegmentKind.Space)
            {
                // Don't add leading space
                if (sb is { Length: 0 })
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
                lastNonSpaceEnd = seg + 1;

                // Look ahead: if next segment is ClosePunctuation, group it with this segment
                while (seg + 1 < segments.Length && kinds[seg + 1] == SegmentKind.ClosePunctuation)
                {
                    seg++;
                    lineWidth += widths[seg];
                    sb?.Append(segments[seg]);
                    lastNonSpaceEnd = seg + 1;
                }
            }

            seg++;

            // Look ahead: if next segment is OpenPunctuation and we're about to break,
            // don't break between this segment and the open-punct
            // (This is naturally handled: open-punct will be picked up at the start of next line)
        }

        // Trim trailing spaces from line text
        var text = sb?.ToString().TrimEnd() ?? string.Empty;
        var trimmedWidth = lineWidth;
        // Adjust width by removing trailing space widths
        for (var i = seg - 1; i >= lineStartSeg; i--)
        {
            if (kinds[i] == SegmentKind.Space)
                trimmedWidth -= widths[i];
            else
                break;
        }

        return new LayoutLine(lineStartSeg, seg, 0, 0, trimmedWidth, text);
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
