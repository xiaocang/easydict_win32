namespace Easydict.TranslationService.Models;

/// <summary>
/// Heuristics for deciding whether a piece of text is a dictionary-style lookup
/// (a single word or short phrase) rather than a sentence to be translated.
/// </summary>
/// <remarks>
/// These live in the model layer because both the dictionary services and the host
/// (phonetic enrichment, phonetic display) need the same answer for the same text.
/// </remarks>
public static class WordQueryHeuristics
{
    /// <summary>
    /// Check if query text looks like a single word or short phrase suitable for dictionary lookup.
    /// </summary>
    public static bool IsWordQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        // Too long for a typical dictionary word
        if (trimmed.Length > 50)
            return false;

        // Contains line breaks or sentence-ending punctuation (English and CJK)
        if (trimmed.Contains('\n') ||
            trimmed.Contains('.') || trimmed.Contains('!') || trimmed.Contains('?') ||
            trimmed.Contains('。') || trimmed.Contains('！') || trimmed.Contains('？'))
            return false;

        // Count CJK characters (Chinese, Japanese, Korean)
        var cjkCount = trimmed.Count(IsCJKCharacter);

        if (cjkCount > 0)
        {
            // For CJK text: treat as word only if very short (1-3 characters)
            // Longer CJK strings are likely sentences or phrases that need translation
            return cjkCount <= 3 && cjkCount == trimmed.Length;
        }

        // For English/Latin: letters, hyphens, apostrophes, spaces
        var wordChars = trimmed.Count(c => char.IsLetter(c) || c == '-' || c == '\'' || c == ' ');
        return wordChars >= trimmed.Length * 0.8;
    }

    /// <summary>
    /// Check whether text is a word query written in plain English letters.
    /// </summary>
    /// <remarks>
    /// Used only as a fallback when language detection produced <see cref="Language.Auto"/>,
    /// to decide whether an English pronunciation lookup is worth attempting. Other
    /// Latin-script languages can pass this check; the lookup simply returns nothing for them.
    /// </remarks>
    public static bool LooksLikeEnglishWord(string? text)
    {
        if (!IsWordQuery(text))
            return false;

        var hasLetter = false;
        foreach (var c in text!.Trim())
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                hasLetter = true;
                continue;
            }

            if (c is '-' or '\'' or ' ')
                continue;

            return false;
        }

        return hasLetter;
    }

    /// <summary>
    /// Check if a character is a CJK (Chinese, Japanese, Korean) character.
    /// </summary>
    private static bool IsCJKCharacter(char c)
    {
        // CJK Unified Ideographs (Chinese characters used in Chinese, Japanese, Korean)
        // U+4E00 to U+9FFF: CJK Unified Ideographs
        // U+3400 to U+4DBF: CJK Unified Ideographs Extension A
        // Also include common Japanese Hiragana and Katakana
        // U+3040 to U+309F: Hiragana
        // U+30A0 to U+30FF: Katakana
        // U+AC00 to U+D7AF: Korean Hangul Syllables
        return (c >= '一' && c <= '鿿') ||  // CJK Unified Ideographs
               (c >= '㐀' && c <= '䶿') ||  // CJK Extension A
               (c >= '぀' && c <= 'ゟ') ||  // Hiragana
               (c >= '゠' && c <= 'ヿ') ||  // Katakana
               (c >= '가' && c <= '힯');    // Korean Hangul
    }
}
