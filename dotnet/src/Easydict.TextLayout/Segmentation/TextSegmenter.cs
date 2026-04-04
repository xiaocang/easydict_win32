using System.Text;
using static Easydict.TextLayout.Segmentation.ScriptClassifier;

namespace Easydict.TextLayout.Segmentation;

/// <summary>
/// Segments text into layout-aware tokens for line breaking.
/// Produces parallel arrays of (segment string, kind) for efficient iteration.
/// </summary>
public static class TextSegmenter
{
    /// <summary>
    /// Segments text into layout-aware tokens.
    /// Whitespace is normalized: consecutive spaces/tabs collapse to a single space.
    /// Hard breaks (\n) are preserved.
    /// </summary>
    public static (string[] Segments, SegmentKind[] Kinds) Segment(string text, bool normalizeWhitespace = true)
    {
        if (string.IsNullOrEmpty(text))
            return ([], []);

        var segments = new List<string>();
        var kinds = new List<SegmentKind>();
        var buffer = new StringBuilder();
        var normalized = normalizeWhitespace ? NormalizeWhitespace(text) : text;

        var i = 0;
        while (i < normalized.Length)
        {
            var ch = normalized[i];
            var category = Classify(ch);

            switch (category)
            {
                case CharCategory.HardBreak:
                    FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
                    segments.Add("\n");
                    kinds.Add(SegmentKind.HardBreak);
                    i++;
                    break;

                case CharCategory.Space:
                    FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
                    // Collect contiguous spaces (already collapsed if normalizeWhitespace)
                    var spaceStart = i;
                    while (i < normalized.Length && normalized[i] is ' ' or '\t' or '\r')
                        i++;
                    segments.Add(normalized[spaceStart..i]);
                    kinds.Add(SegmentKind.Space);
                    break;

                case CharCategory.Cjk:
                    FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
                    // Each CJK character is its own segment
                    // Use grapheme enumeration to handle surrogate pairs
                    segments.Add(ch.ToString());
                    kinds.Add(SegmentKind.CjkGrapheme);
                    i++;
                    break;

                case CharCategory.OpenPunctuation:
                    FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
                    segments.Add(ch.ToString());
                    kinds.Add(SegmentKind.OpenPunctuation);
                    i++;
                    break;

                case CharCategory.ClosePunctuation:
                    // If we have a word buffer, attach closing punct to it
                    if (buffer.Length > 0)
                    {
                        buffer.Append(ch);
                        i++;
                        // Consume additional closing punctuation
                        while (i < normalized.Length && IsClosePunctuation(normalized[i]))
                        {
                            buffer.Append(normalized[i]);
                            i++;
                        }
                        FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
                    }
                    else
                    {
                        segments.Add(ch.ToString());
                        kinds.Add(SegmentKind.ClosePunctuation);
                        i++;
                    }
                    break;

                default: // Latin/Cyrillic/Greek/Other
                    buffer.Append(ch);
                    i++;
                    // Accumulate word characters and trailing close-punctuation
                    while (i < normalized.Length)
                    {
                        var nextCh = normalized[i];
                        var nextCat = Classify(nextCh);
                        if (nextCat == CharCategory.Latin)
                        {
                            buffer.Append(nextCh);
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    break;
            }
        }

        FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
        return (segments.ToArray(), kinds.ToArray());
    }

    /// <summary>
    /// Normalizes whitespace: collapses consecutive spaces/tabs to a single space.
    /// Preserves \n as hard breaks. Trims trailing spaces before hard breaks.
    /// </summary>
    internal static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                // Trim trailing space before hard break
                if (sb.Length > 0 && sb[^1] == ' ')
                    sb.Length--;
                sb.Append('\n');
                lastWasSpace = false;
            }
            else if (ch is ' ' or '\t' or '\r')
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }

    private static void FlushBuffer(StringBuilder buffer, List<string> segments, List<SegmentKind> kinds, SegmentKind kind)
    {
        if (buffer.Length > 0)
        {
            segments.Add(buffer.ToString());
            kinds.Add(kind);
            buffer.Clear();
        }
    }
}
