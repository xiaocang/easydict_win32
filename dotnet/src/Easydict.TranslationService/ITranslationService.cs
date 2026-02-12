using Easydict.TranslationService.Models;

namespace Easydict.TranslationService;

/// <summary>
/// Interface for translation services.
/// </summary>
public interface ITranslationService
{
    /// <summary>
    /// Unique identifier for this service type.
    /// </summary>
    string ServiceId { get; }

    /// <summary>
    /// Display name of the service.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this service requires an API key.
    /// </summary>
    bool RequiresApiKey { get; }

    /// <summary>
    /// Whether this service is currently configured and ready to use.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Languages supported by this service.
    /// </summary>
    IReadOnlyList<Language> SupportedLanguages { get; }

    /// <summary>
    /// Check if a language pair is supported.
    /// </summary>
    bool SupportsLanguagePair(Language from, Language to);

    /// <summary>
    /// Translate text.
    /// </summary>
    /// <param name="request">Translation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Translation result.</returns>
    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detect the language of text.
    /// </summary>
    /// <param name="text">Text to detect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detected language.</returns>
    Task<Language> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Exception thrown when translation fails.
/// </summary>
public class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
    public TranslationException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// Error code for categorizing the error.
    /// </summary>
    public TranslationErrorCode ErrorCode { get; init; } = TranslationErrorCode.Unknown;

    /// <summary>
    /// The service that threw the error.
    /// </summary>
    public string? ServiceId { get; init; }
}

/// <summary>
/// Translation error codes.
/// </summary>
public enum TranslationErrorCode
{
    Unknown,
    NetworkError,
    Timeout,
    RateLimited,
    InvalidApiKey,
    UnsupportedLanguage,
    TextTooLong,
    ServiceUnavailable,
    InvalidResponse,
    InvalidModel
}

