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

