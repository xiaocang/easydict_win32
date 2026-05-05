namespace Polyglot.TextLayout.Segmentation;

/// <summary>
/// Japanese line-breaking rules (kinsoku shori) per JIS X 4051 / CSS Text Level 3.
/// Defines characters that cannot start or end a line in CJK typesetting.
/// </summary>
public static class KinsokuTable
{
    /// <summary>
    /// Characters that must not appear at the start of a line.
    /// Includes closing brackets, CJK periods/commas, small kana, prolonged sound mark,
    /// iteration marks, and fullwidth terminal punctuation.
    /// </summary>
    public static bool IsProhibitedLineStart(char ch)
    {
        return ch is
            // Closing brackets
            '\uFF09'   // FULLWIDTH RIGHT PARENTHESIS
            or '\u3001' // IDEOGRAPHIC COMMA
            or '\u3002' // IDEOGRAPHIC FULL STOP
            or '\u300D' // RIGHT CORNER BRACKET 」
            or '\u300F' // RIGHT WHITE CORNER BRACKET 』
            or '\u3011' // RIGHT BLACK LENTICULAR BRACKET 】
            or '\u3015' // RIGHT TORTOISE SHELL BRACKET 〕
            or '\u3009' // RIGHT ANGLE BRACKET 〉
            or '\u300B' // RIGHT DOUBLE ANGLE BRACKET 》
            or '\u3017' // RIGHT WHITE LENTICULAR BRACKET 〗
            or '\u3019' // RIGHT WHITE TORTOISE SHELL BRACKET 〙
            or '\u301B' // RIGHT WHITE SQUARE BRACKET 〛
            or '\u301F' // LOW DOUBLE PRIME QUOTATION MARK 〟
            // CJK punctuation
            or '\u30FB' // KATAKANA MIDDLE DOT ・
            or '\u30FC' // KATAKANA-HIRAGANA PROLONGED SOUND MARK ー
            // Fullwidth punctuation
            or '\uFF01' // FULLWIDTH EXCLAMATION MARK ！
            or '\uFF1F' // FULLWIDTH QUESTION MARK ？
            or '\uFF1B' // FULLWIDTH SEMICOLON ；
            or '\uFF1A' // FULLWIDTH COLON ：
            or '\uFF0C' // FULLWIDTH COMMA ，
            or '\uFF0E' // FULLWIDTH FULL STOP ．
            // Small hiragana
            or '\u3041' // SMALL A ぁ
            or '\u3043' // SMALL I ぃ
            or '\u3045' // SMALL U ぅ
            or '\u3047' // SMALL E ぇ
            or '\u3049' // SMALL O ぉ
            or '\u3063' // SMALL TU っ
            or '\u3083' // SMALL YA ゃ
            or '\u3085' // SMALL YU ゅ
            or '\u3087' // SMALL YO ょ
            or '\u308E' // SMALL WA ゎ
            // Small katakana
            or '\u30A1' // SMALL A ァ
            or '\u30A3' // SMALL I ィ
            or '\u30A5' // SMALL U ゥ
            or '\u30A7' // SMALL E ェ
            or '\u30A9' // SMALL O ォ
            or '\u30C3' // SMALL TU ッ
            or '\u30E3' // SMALL YA ャ
            or '\u30E5' // SMALL YU ュ
            or '\u30E7' // SMALL YO ョ
            or '\u30EE' // SMALL WA ヮ
            // Iteration marks
            or '\u309D' // HIRAGANA ITERATION MARK ゝ
            or '\u309E' // HIRAGANA VOICED ITERATION MARK ゞ
            or '\u30FD' // KATAKANA ITERATION MARK ヽ
            or '\u30FE' // KATAKANA VOICED ITERATION MARK ヾ
            or '\u3005' // IDEOGRAPHIC ITERATION MARK 々
            or '\u303B'; // VERTICAL IDEOGRAPHIC ITERATION MARK 〻
    }

    /// <summary>
    /// Characters that must not appear at the end of a line.
    /// Includes opening brackets and CJK opening marks.
    /// </summary>
    public static bool IsProhibitedLineEnd(char ch)
    {
        return ch is
            '\uFF08'   // FULLWIDTH LEFT PARENTHESIS （
            or '\u3014' // LEFT TORTOISE SHELL BRACKET 〔
            or '\u3008' // LEFT ANGLE BRACKET 〈
            or '\u300A' // LEFT DOUBLE ANGLE BRACKET 《
            or '\u300C' // LEFT CORNER BRACKET 「
            or '\u300E' // LEFT WHITE CORNER BRACKET 『
            or '\u3010' // LEFT BLACK LENTICULAR BRACKET 【
            or '\u3016' // LEFT WHITE LENTICULAR BRACKET 〖
            or '\u3018' // LEFT WHITE TORTOISE SHELL BRACKET 〘
            or '\u301A' // LEFT WHITE SQUARE BRACKET 〚
            or '\u301D'; // REVERSED DOUBLE PRIME QUOTATION MARK 〝
    }

    /// <summary>
    /// Left-sticky punctuation that attaches to the preceding CJK character.
    /// These ASCII punctuation marks should not start a line when they follow CJK text.
    /// </summary>
    public static bool IsLeftSticky(char ch)
    {
        return ch is '.' or ',' or '!' or '?' or ':' or ';'
            or ')' or ']' or '}' or '%'
            or '"' or '\u2026'; // HORIZONTAL ELLIPSIS …
    }
}
