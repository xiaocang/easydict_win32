using Easydict.TranslationService.Models;

namespace Easydict.TranslationService;

/// <summary>
/// Interface for services that support grammar correction via LLM.
/// Implemented alongside IStreamTranslationService by LLM-based services.
/// </summary>
public interface IGrammarCorrectionService
{
    /// <summary>
    /// Streams grammar correction output as text chunks.
    /// The accumulated output should be parsed by <see cref="Services.GrammarCorrectionParser"/>
    /// to extract corrected text and explanations.
    /// </summary>
    /// <param name="request">Grammar correction request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async stream of text chunks.</returns>
    IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        CancellationToken cancellationToken = default);
}
