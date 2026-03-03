namespace Easydict.TranslationService.Models;

/// <summary>
/// Helper for dictionary result display logic.
/// </summary>
public static class DictionaryDisplayHelper
{
    /// <summary>
    /// Checks whether TranslatedText is a flattened version of the definitions,
    /// meaning showing both structured definitions and TranslatedText would be redundant.
    /// Returns true for Youdao-style results where TranslatedText is built from definitions
    /// (e.g., "int. hello\nn. greeting"). Returns false for GoogleWeb-style results where
    /// TranslatedText is an independent plain translation (e.g., "你好").
    /// </summary>
    public static bool IsTranslatedTextRedundantWithDefinitions(TranslationResult result)
    {
        var definitions = result.WordResult?.Definitions;
        if (definitions == null || definitions.Count == 0)
            return false;

        var translatedText = result.TranslatedText;
        if (string.IsNullOrEmpty(translatedText))
            return false;

        // Heuristic: if the translated text contains part-of-speech markers from
        // the definitions, it's likely a flattened version of the definitions.
        // Youdao format: "int. 喂；你好\nn. 表示问候" — contains "int." and "n."
        // GoogleWeb format: "你好" — does NOT contain "interjection" or "noun"
        var posCount = 0;
        foreach (var def in definitions)
        {
            if (!string.IsNullOrEmpty(def.PartOfSpeech) &&
                translatedText.Contains(def.PartOfSpeech, StringComparison.Ordinal))
            {
                posCount++;
            }
        }

        // If most definitions' POS markers appear in TranslatedText, it's redundant
        return posCount > 0 && posCount >= (definitions.Count + 1) / 2;
    }
}
