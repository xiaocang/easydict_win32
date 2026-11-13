namespace Easydict.TranslationService.Models;

/// <summary>
/// Request for a translation.
/// </summary>
public sealed class TranslationRequest
{
    public TranslationDiagnostics? Diagnostics { get; init; }

    public TranslationRequest WithDiagnostics(TranslationDiagnostics diagnostics) => new()
    {
        Text = Text, FromLanguage = FromLanguage, ToLanguage = ToLanguage,
        TimeoutMs = TimeoutMs, BypassCache = BypassCache, CustomPrompt = CustomPrompt,
        OriginalText = OriginalText, DetectedFromLanguage = DetectedFromLanguage,
        AutomaticDirection = AutomaticDirection, Diagnostics = diagnostics
    };

    /// <summary>Let a prompt-capable service detect the source and choose the translation direction.</summary>
    public AutomaticLanguageDirection? AutomaticDirection { get; init; }

    /// <summary>
    /// Text to translate.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Source language (Auto for auto-detection).
    /// </summary>
    public Language FromLanguage { get; init; } = Language.Auto;

    /// <summary>
    /// Target language for translation. Auto when <see cref="AutomaticDirection"/> lets
    /// the model choose it; the host does not claim a language that has not been detected.
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

    /// <summary>
    /// Optional custom prompt to append to the system message for LLM-based translation services.
    /// Has no effect on non-LLM services (Google, DeepL, etc.).
    /// </summary>
    public string? CustomPrompt { get; init; }

    /// <summary>
    /// The text exactly as captured from the user (before any host preprocessing such as
    /// whitespace normalization). <c>null</c> when identical to <see cref="Text"/>.
    /// Adapters that need the raw selection (for example Bob plugins' <c>originalText</c>)
    /// should read this first and fall back to <see cref="Text"/>.
    /// </summary>
    public string? OriginalText { get; init; }

    /// <summary>
    /// The source language detected by the host, when it is known independently of
    /// <see cref="FromLanguage"/>. <c>null</c> when no detection was performed.
    /// </summary>
    public Language? DetectedFromLanguage { get; init; }
}

