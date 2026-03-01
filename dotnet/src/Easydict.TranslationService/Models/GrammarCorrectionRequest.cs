namespace Easydict.TranslationService.Models;

/// <summary>
/// Request for grammar correction.
/// </summary>
public sealed class GrammarCorrectionRequest
{
    /// <summary>
    /// Text to check and correct.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// The language of the text (Auto for auto-detection).
    /// </summary>
    public Language Language { get; init; } = Language.Auto;

    /// <summary>
    /// Whether to include explanations for each correction.
    /// </summary>
    public bool IncludeExplanations { get; init; } = true;

    /// <summary>
    /// Optional timeout in milliseconds (default: 30000).
    /// </summary>
    public int TimeoutMs { get; init; } = 30000;
}
