namespace Easydict.TranslationService.Models;

/// <summary>
/// Copy helpers for <see cref="WordResult"/>, which is an init-only class rather than a record.
/// Every field must be carried over explicitly; forgetting one silently drops dictionary data.
/// </summary>
internal static class WordResultExtensions
{
    /// <summary>
    /// Return a copy of <paramref name="source"/> with <paramref name="phonetics"/> substituted and
    /// every other list (definitions, examples, word forms, synonyms) preserved.
    /// </summary>
    public static WordResult WithPhonetics(this WordResult? source, IReadOnlyList<Phonetic> phonetics)
    {
        return new WordResult
        {
            Phonetics = phonetics,
            Definitions = source?.Definitions,
            Examples = source?.Examples,
            WordForms = source?.WordForms,
            Synonyms = source?.Synonyms
        };
    }
}
