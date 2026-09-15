namespace Easydict.TranslationService.Models;

/// <summary>
/// Helper for formatting phonetic transcription data for UI display.
/// </summary>
public static class PhoneticDisplayHelper
{
    /// <summary>
    /// Maps phonetic accent codes to short display labels.
    /// US/UK use Chinese labels (美/英), src/dest use 原/译.
    /// </summary>
    public static string? GetAccentDisplayLabel(string? accent)
    {
        return accent switch
        {
            "US" => "美",
            "UK" => "英",
            "src" => "原",
            "dest" => "译",
            null or "" => null,
            _ => accent
        };
    }

    /// <summary>
    /// Formats phonetic text for display, wrapping in slashes if not already wrapped.
    /// </summary>
    public static string FormatPhoneticText(string text)
    {
        if (text.StartsWith('/') && text.EndsWith('/'))
            return text;

        return $"/{text}/";
    }

    /// <summary>
    /// Extracts displayable phonetics from a TranslationResult.
    /// Returns an empty list if no phonetics are available.
    /// </summary>
    public static IReadOnlyList<Phonetic> GetDisplayablePhonetics(TranslationResult? result)
    {
        var phonetics = result?.WordResult?.Phonetics;
        if (phonetics == null || phonetics.Count == 0)
            return [];

        return phonetics.Where(p => !string.IsNullOrEmpty(p.Text)).ToList();
    }

    /// <summary>
    /// Extracts only target-language phonetics from a TranslationResult.
    /// Filters for "dest", "US", or "UK" accents and excludes source language phonetics.
    /// Returns an empty list if no target phonetics are available.
    /// </summary>
    public static IReadOnlyList<Phonetic> GetTargetPhonetics(TranslationResult? result)
    {
        var phonetics = result?.WordResult?.Phonetics;
        if (phonetics == null || phonetics.Count == 0)
            return [];

        // Only include phonetics that are explicitly marked as target language
        // dest = target language accent, US/UK = English accents (typically target for Chinese input)
        return phonetics
            .Where(p => !string.IsNullOrEmpty(p.Text) && 
                       (p.Accent == "dest" || p.Accent == "US" || p.Accent == "UK"))
            .ToList();
    }

    /// <summary>
    /// Extracts only the English pronunciation phonetics (US/UK) from a result.
    /// </summary>
    /// <remarks>
    /// These describe the English word in play regardless of translation direction, so they
    /// are the ones host-side enrichment can supply. A romanization such as pinyin
    /// ("src"/"dest") is deliberately not counted here: it is not a pronunciation guide
    /// for an English word and must not suppress enrichment.
    /// </remarks>
    public static IReadOnlyList<Phonetic> GetEnglishPhonetics(TranslationResult? result)
    {
        var phonetics = result?.WordResult?.Phonetics;
        if (phonetics == null || phonetics.Count == 0)
            return [];

        return phonetics
            .Where(p => !string.IsNullOrEmpty(p.Text) && (p.Accent == "US" || p.Accent == "UK"))
            .ToList();
    }

    /// <summary>
    /// Resolves the English text that a US/UK phonetic on this result describes.
    /// </summary>
    /// <remarks>
    /// Phonetics belong to the word being looked up, not to a translation direction: the
    /// English side is the translation when translating into English (zh→en) and the
    /// original text when translating out of it (en→zh). Returns null when neither side
    /// is English, in which case US/UK phonetics are not meaningful for this result.
    /// </remarks>
    public static string? GetEnglishPhoneticSubject(TranslationResult? result)
    {
        if (result is null)
            return null;

        if (result.TargetLanguage == Language.English)
            return result.TranslatedText;

        if (result.DetectedLanguage == Language.English)
            return result.OriginalText;

        // Services disagree about DetectedLanguage — several leave it unset or echo an
        // unresolved Auto — so fall back to the script of the text rather than hiding a
        // pronunciation the service actually provided.
        if (WordQueryHeuristics.LooksLikeEnglishWord(result.OriginalText))
            return result.OriginalText;

        if (WordQueryHeuristics.LooksLikeEnglishWord(result.TranslatedText))
            return result.TranslatedText;

        return null;
    }

    /// <summary>
    /// Selects the phonetics worth showing for a result, in display order.
    /// </summary>
    /// <remarks>
    /// Phonetics are a dictionary affordance, so they are shown for word lookups in either
    /// direction and suppressed for sentence translation. US/UK pronunciations require an English side;
    /// romanizations ("src"/"dest", e.g. pinyin from Google) are shown whenever a service
    /// provides them.
    /// </remarks>
    public static IReadOnlyList<Phonetic> GetDisplayPhonetics(TranslationResult? result)
    {
        if (result is null)
            return [];

        // A result is a dictionary lookup when either side is word-sized: the query in en→zh,
        // the translation in zh→en. Checking only the query would discard pronunciations for
        // CJK words longer than the CJK heuristic allows (コンピューター, 안녕하세요, a four-character
        // idiom) whose English translation is an ordinary word. Checking only the translation
        // would discard them for dictionary services whose translation is a gloss rather than a
        // word ("int. 喂；你好" from Youdao).
        if (!WordQueryHeuristics.IsWordQuery(result.OriginalText)
            && !WordQueryHeuristics.IsWordQuery(result.TranslatedText))
        {
            return [];
        }

        var phonetics = result.WordResult?.Phonetics;
        if (phonetics == null || phonetics.Count == 0)
            return [];

        var hasEnglishSide = GetEnglishPhoneticSubject(result) is not null;

        return phonetics
            .Where(p => !string.IsNullOrEmpty(p.Text))
            .Where(p => p.Accent switch
            {
                "US" or "UK" => hasEnglishSide,
                "src" or "dest" => true,
                _ => false
            })
            // Pronunciation first, then romanizations of either side
            .OrderBy(p => p.Accent switch
            {
                "US" => 0,
                "UK" => 1,
                "src" => 2,
                _ => 3
            })
            .ToList();
    }
}
