using Easydict.TranslationService.Models;

namespace Easydict.TranslationService;

/// <summary>
/// Interface for streaming translation services (LLM-based).
/// Extends ITranslationService to support real-time streaming output.
/// </summary>
public interface IStreamTranslationService : ITranslationService
{
    /// <summary>
    /// Whether this service supports streaming responses.
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Stream translate text, yielding partial results as they arrive.
    /// Each yielded string is a content chunk (not accumulated).
    /// Implementations should use [EnumeratorCancellation] on the cancellationToken parameter.
    /// </summary>
    /// <param name="request">Translation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async stream of partial translated text chunks.</returns>
    IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}
