using System.Text;
using static Polyglot.TextLayout.Segmentation.ScriptClassifier;

namespace Polyglot.TextLayout.Segmentation;

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
            // Use Rune to correctly handle surrogate pairs
            Rune rune;
            int runeLength;
            if (Rune.TryGetRuneAt(normalized, i, out rune))
            {
                runeLength = rune.Utf16SequenceLength;
            }
            else
            {
                // Unpaired surrogate — treat as single Latin char
                rune = Rune.ReplacementChar;
                runeLength = 1;
            }

            var ch = normalized[i];
            var category = ClassifyRune(rune, ch);

            switch (category)
            {
                case CharCategory.HardBreak:
                    FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
                    segments.Add("\n");
                    kinds.Add(SegmentKind.HardBreak);
                    i++;
                    break;

                case CharCategory.SoftHyphen:
                    FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
                    segments.Add("\u00AD");
                    kinds.Add(SegmentKind.SoftHyphen);
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
                    // Use the full rune length to handle surrogate pairs (Extension-B, etc.)
                    segments.Add(normalized.Substring(i, runeLength));
                    kinds.Add(SegmentKind.CjkGrapheme);
                    i += runeLength;
                    break;

                case CharCategory.OpenPunctuation:
                    FlushBuffer(buffer, segments, kinds, SegmentKind.Word);
                    segments.Add(normalized.Substring(i, runeLength));
                    kinds.Add(SegmentKind.OpenPunctuation);
                    i += runeLength;
                    break;

                case CharCategory.ClosePunctuation:
                    // If we have a word buffer, attach closing punct to it
                    if (buffer.Length > 0)
                    {
                        buffer.Append(normalized, i, runeLength);
                        i += runeLength;
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
                        segments.Add(normalized.Substring(i, runeLength));
                        kinds.Add(SegmentKind.ClosePunctuation);
                        i += runeLength;
                    }
                    break;

                default: // Latin/Cyrillic/Greek/Other
                    buffer.Append(normalized, i, runeLength);
                    i += runeLength;
                    // Accumulate word characters
                    while (i < normalized.Length)
                    {
                        if (Rune.TryGetRuneAt(normalized, i, out var nextRune))
                        {
                            if (ClassifyRune(nextRune, normalized[i]) == CharCategory.Latin)
                            {
                                buffer.Append(normalized, i, nextRune.Utf16SequenceLength);
                                i += nextRune.Utf16SequenceLength;
                            }
                            else
                            {
                                break;
                            }
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
    /// Classifies a Rune, falling back to char-based classification for BMP characters.
    /// Handles supplementary CJK planes (Extension B+) via Rune.
    /// </summary>
    private static CharCategory ClassifyRune(Rune rune, char firstChar)
    {
        // BMP characters use the fast char-based classifier
        if (rune.Value <= 0xFFFF)
            return Classify(firstChar);

        // Supplementary planes: check for CJK Extension B-H (U+20000..U+3FFFF)
        if (rune.Value is >= 0x20000 and <= 0x3FFFF)
            return CharCategory.Cjk;

        // Other supplementary characters treated as Latin/Other
        return CharCategory.Latin;
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
