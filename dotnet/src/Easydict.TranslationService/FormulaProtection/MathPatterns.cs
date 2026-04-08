namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Shared regex patterns for math font and Unicode detection.
/// Single source of truth — used by FormulaPreservationService, CharacterParagraphBuilder,
/// and WinUI LongDocumentTranslationService.
/// </summary>
public static class MathPatterns
{
    /// <summary>
    /// Regex pattern matching mathematical font names (TeX Computer Modern, MS math fonts, etc.).
    /// Word-boundary anchored on short abbreviations (BL, RM, EU, LA, RS) to avoid
    /// matching fragments inside common text fonts like "Lato-Regular" or "TimesNewRoman".
    /// </summary>
    public const string MathFontPattern =
        @"CM[^R]|CMSY|CMMI|CMEX|MS\.M|MSAM|MSBM|XY|MT\w*Math|Symbol|Euclid|Mathematica|MathematicalPi|STIX" +
        @"|\bBL\b|\bRM\b|\bEU\b|\bLA\b|\bRS\b" +
        @"|LINE|LCIRCLE" +
        @"|TeX-|rsfs|txsy|wasy|stmary" +
        @"|\w+Sym\w*|\b\w{1,5}Math\w*";

    /// <summary>
    /// Regex pattern matching Unicode math characters: math operators, letterlike symbols,
    /// Greek letters, superscripts/subscripts, modifier letters, combining marks.
    /// Narrowed: U+2000–U+200A general spaces excluded to avoid false positives;
    /// only U+200B–U+200D (ZWSP/ZWNJ/ZWJ) retained as math signals.
    /// </summary>
    public const string MathUnicodePattern =
        @"[\u2200-\u22FF\u2100-\u214F\u0370-\u03FF\u2070-\u209F\u00B2\u00B3\u00B9" +
        @"\u2150-\u218F\u27C0-\u27EF\u2980-\u29FF" +
        @"\u02B0-\u02FF\u0300-\u036F\u02C6-\u02CF\u200B-\u200D]";

    /// <summary>
    /// Known math and ML-style function names that may legitimately appear as the
    /// English-looking residue of a formula block.
    /// </summary>
    public static readonly HashSet<string> MathFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "attention",
        "softmax",
        "multihead",
        "concat",
        "layernorm",
        "ffn",
        "relu",
        "gelu",
        "sin",
        "cos",
        "min",
        "max",
        "argmax",
        "argmin",
        "log",
        "exp",
        "tr",
    };

    /// <summary>
    /// Returns true if <paramref name="token"/> looks like a mathematical subscript or
    /// superscript token: letters, digits, or common operators (+, -, =, ., ,, (, ), /, *).
    /// Footnote markers (\u2020, \u2021, \u00a7, \u00b6, etc.) return false.
    /// </summary>
    public static bool IsMathToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        return token.All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '=' or '.' or ',' or '(' or ')' or '/' or '*');
    }
}
