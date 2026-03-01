namespace Easydict.TranslationService.Models;

/// <summary>
/// Result of a grammar correction operation from an LLM service.
/// </summary>
public sealed record GrammarCorrectionResult
{
    /// <summary>
    /// The original input text.
    /// </summary>
    public required string OriginalText { get; init; }

    /// <summary>
    /// The corrected text.
    /// </summary>
    public required string CorrectedText { get; init; }

    /// <summary>
    /// Explanation of changes made (may contain multiple items).
    /// </summary>
    public string? Explanation { get; init; }

    /// <summary>
    /// The service that performed the correction.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Time taken in milliseconds.
    /// </summary>
    public long TimingMs { get; init; }

    /// <summary>
    /// Whether corrections were found.
    /// </summary>
    public bool HasCorrections => !string.Equals(
        OriginalText.Trim(), CorrectedText.Trim(), StringComparison.Ordinal);
}
