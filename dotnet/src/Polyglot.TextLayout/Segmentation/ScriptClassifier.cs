using System.Globalization;

namespace Polyglot.TextLayout.Segmentation;

/// <summary>
/// Classifies characters by script family for segmentation and line-breaking decisions.
/// </summary>
public static class ScriptClassifier
{
    public enum CharCategory
    {
        Cjk,
        Latin,
        Space,
        HardBreak,
        OpenPunctuation,
        ClosePunctuation,
        SoftHyphen,
        Other,
    }

    /// <summary>
    /// Classifies a single character into a layout-relevant category.
    /// </summary>
    public static CharCategory Classify(char ch)
    {
        if (ch == '\n')
            return CharCategory.HardBreak;

        if (ch == '\u00AD')
            return CharCategory.SoftHyphen;

        if (ch is ' ' or '\t' or '\r')
            return CharCategory.Space;

        if (IsOpenPunctuation(ch))
            return CharCategory.OpenPunctuation;

        if (IsClosePunctuation(ch))
            return CharCategory.ClosePunctuation;

        if (IsCjk(ch))
            return CharCategory.Cjk;

        return CharCategory.Latin;
    }

    /// <summary>
    /// Returns true for CJK ideographs, kana, hangul, and fullwidth forms.
    /// Matches the ranges used by both MuPdfExportService.IsCjkCharacter and
    /// PdfExportService.IsCjkCharacter/TokenizeForWrapping.
    /// </summary>
    public static bool IsCjk(char ch)
    {
        return ch is >= '\u3000' and <= '\u303F'    // CJK Symbols and Punctuation
            or >= '\u3040' and <= '\u309F'           // Hiragana
            or >= '\u30A0' and <= '\u30FF'           // Katakana
            or >= '\u3400' and <= '\u4DBF'           // CJK Extension A
            or >= '\u4E00' and <= '\u9FFF'           // CJK Unified Ideographs
            or >= '\uAC00' and <= '\uD7AF'           // Hangul Syllables
            or >= '\uF900' and <= '\uFAFF'           // CJK Compatibility Ideographs
            or >= '\uFF00' and <= '\uFFEF';          // Fullwidth Forms
    }

    /// <summary>
    /// Returns true for opening brackets/quotes that should group with the following segment.
    /// </summary>
    public static bool IsOpenPunctuation(char ch)
    {
        return ch is '(' or '[' or '{' or '<'
            or '\u3008'   // LEFT ANGLE BRACKET 〈
            or '\u300A'   // LEFT DOUBLE ANGLE BRACKET 《
            or '\u300C'   // LEFT CORNER BRACKET
            or '\u300E'   // LEFT WHITE CORNER BRACKET
            or '\u3010'   // LEFT BLACK LENTICULAR BRACKET
            or '\u3014'   // LEFT TORTOISE SHELL BRACKET
            or '\u3016'   // LEFT WHITE LENTICULAR BRACKET
            or '\u3018'   // LEFT WHITE TORTOISE SHELL BRACKET
            or '\u301A'   // LEFT WHITE SQUARE BRACKET
            or '\u301D'   // REVERSED DOUBLE PRIME QUOTATION MARK 〝
            or '\uFF08'   // FULLWIDTH LEFT PARENTHESIS
            or '\u201C'   // LEFT DOUBLE QUOTATION MARK
            or '\u2018'   // LEFT SINGLE QUOTATION MARK
            or '\u00AB';  // LEFT-POINTING DOUBLE ANGLE QUOTATION MARK
    }

    /// <summary>
    /// Returns true for closing brackets/quotes/terminal punctuation that should
    /// group with the preceding segment.
    /// </summary>
    public static bool IsClosePunctuation(char ch)
    {
        return ch is ')' or ']' or '}' or '>'
            or '.' or ',' or ';' or ':' or '!' or '?' or '%'
            or '\u3001'   // IDEOGRAPHIC COMMA
            or '\u3002'   // IDEOGRAPHIC FULL STOP
            or '\u3009'   // RIGHT ANGLE BRACKET 〉
            or '\u300B'   // RIGHT DOUBLE ANGLE BRACKET 》
            or '\u300D'   // RIGHT CORNER BRACKET
            or '\u300F'   // RIGHT WHITE CORNER BRACKET
            or '\u3011'   // RIGHT BLACK LENTICULAR BRACKET
            or '\u3015'   // RIGHT TORTOISE SHELL BRACKET
            or '\u3017'   // RIGHT WHITE LENTICULAR BRACKET
            or '\u3019'   // RIGHT WHITE TORTOISE SHELL BRACKET
            or '\u301B'   // RIGHT WHITE SQUARE BRACKET
            or '\u301F'   // LOW DOUBLE PRIME QUOTATION MARK 〟
            or '\uFF09'   // FULLWIDTH RIGHT PARENTHESIS
            or '\uFF0C'   // FULLWIDTH COMMA
            or '\uFF1A'   // FULLWIDTH COLON ：
            or '\uFF1B'   // FULLWIDTH SEMICOLON
            or '\uFF01'   // FULLWIDTH EXCLAMATION MARK
            or '\uFF1F'   // FULLWIDTH QUESTION MARK
            or '\uFF0E'   // FULLWIDTH FULL STOP
            or '\u201D'   // RIGHT DOUBLE QUOTATION MARK
            or '\u2019'   // RIGHT SINGLE QUOTATION MARK
            or '\u00BB';  // RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK
    }

    /// <summary>
    /// Enumerates grapheme clusters in a string using StringInfo (UAX29).
    /// </summary>
    public static IEnumerable<string> EnumerateGraphemes(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }
}
