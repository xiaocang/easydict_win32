namespace Easydict.TranslationService.Models;

/// <summary>
/// Result of a translation request.
/// Using record for immutable data with 'with' expression support.
/// </summary>
public sealed record TranslationResult
{
    /// <summary>
    /// The translated text.
    /// </summary>
    public required string TranslatedText { get; init; }

    /// <summary>
    /// The original query text.
    /// </summary>
    public required string OriginalText { get; init; }

    /// <summary>
    /// Detected source language (if auto-detect was used).
    /// </summary>
    public Language DetectedLanguage { get; init; } = Language.Auto;

    /// <summary>
    /// Target language of translation.
    /// </summary>
    public Language TargetLanguage { get; init; }

    /// <summary>
    /// The translation service that produced this result.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Time taken for the translation in milliseconds.
    /// </summary>
    public long TimingMs { get; init; }

    /// <summary>
    /// Whether this result was served from cache.
    /// </summary>
    public bool FromCache { get; init; }

    /// <summary>
    /// Alternative translations if available.
    /// </summary>
    public IReadOnlyList<string>? Alternatives { get; init; }

    /// <summary>
    /// Word definitions/dictionary data if available.
    /// </summary>
    public WordResult? WordResult { get; init; }
}

/// <summary>
/// Dictionary/word lookup result for single words.
/// </summary>
public sealed class WordResult
{
    /// <summary>
    /// Phonetic pronunciations.
    /// </summary>
    public IReadOnlyList<Phonetic>? Phonetics { get; init; }

    /// <summary>
    /// Definitions grouped by part of speech.
    /// </summary>
    public IReadOnlyList<Definition>? Definitions { get; init; }

    /// <summary>
    /// Example sentences.
    /// </summary>
    public IReadOnlyList<string>? Examples { get; init; }

    /// <summary>
    /// Word forms/inflections (e.g., past tense, plural).
    /// </summary>
    public IReadOnlyList<WordForm>? WordForms { get; init; }

    /// <summary>
    /// Synonym groups by part of speech.
    /// </summary>
    public IReadOnlyList<Synonym>? Synonyms { get; init; }
}

/// <summary>
/// Phonetic pronunciation info.
/// </summary>
public sealed class Phonetic
{
    /// <summary>
    /// Phonetic spelling (e.g., /həˈloʊ/).
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Audio URL for pronunciation.
    /// </summary>
    public string? AudioUrl { get; init; }

    /// <summary>
    /// Accent type (e.g., "US", "UK").
    /// </summary>
    public string? Accent { get; init; }
}

/// <summary>
/// Word definition grouped by part of speech.
/// </summary>
public sealed class Definition
{
    /// <summary>
    /// Part of speech (e.g., "noun", "verb").
    /// </summary>
    public string? PartOfSpeech { get; init; }

    /// <summary>
    /// Meanings for this part of speech.
    /// </summary>
    public IReadOnlyList<string>? Meanings { get; init; }
}

/// <summary>
/// Word form/inflection (e.g., past tense: "ran", plural: "runs").
/// </summary>
public sealed class WordForm
{
    /// <summary>
    /// Form name (e.g., "过去式", "复数", "比较级").
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Form value (e.g., "ran", "runs", "bigger").
    /// </summary>
    public string? Value { get; init; }
}

/// <summary>
/// Synonym group for a part of speech.
/// </summary>
public sealed class Synonym
{
    /// <summary>
    /// Part of speech (e.g., "n.").
    /// </summary>
    public string? PartOfSpeech { get; init; }

    /// <summary>
    /// Meaning description (e.g., "问候").
    /// </summary>
    public string? Meaning { get; init; }

    /// <summary>
    /// Synonym words (e.g., ["greeting", "salutation"]).
    /// </summary>
    public IReadOnlyList<string>? Words { get; init; }
}

