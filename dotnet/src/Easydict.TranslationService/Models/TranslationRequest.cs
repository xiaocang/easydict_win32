namespace Easydict.TranslationService.Models;

/// <summary>
/// Request for a translation.
/// </summary>
public sealed class TranslationRequest
{
    /// <summary>
    /// Text to translate.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Source language (Auto for auto-detection).
    /// </summary>
    public Language FromLanguage { get; init; } = Language.Auto;

    /// <summary>
    /// Target language for translation.
    /// </summary>
    public required Language ToLanguage { get; init; }

    /// <summary>
    /// Optional timeout in milliseconds (default: 30000).
    /// </summary>
    public int TimeoutMs { get; init; } = 30000;

    /// <summary>
    /// Whether to skip cache and force a fresh translation.
    /// </summary>
    public bool BypassCache { get; init; } = false;
}

