using Easydict.TranslationService.Models;

namespace Easydict.TranslationService;

/// <summary>
/// A streaming service that can express more than appended text: snapshots that replace the
/// accumulated text and a structured final result delivered in-band. Implementations should
/// derive <see cref="IStreamTranslationService.TranslateStreamAsync"/> from the rich stream via
/// <see cref="Streaming.StreamUpdateAdapter.ToDeltas"/> so legacy consumers keep working.
/// </summary>
public interface IRichStreamTranslationService : IStreamTranslationService
{
    /// <summary>
    /// Stream translation updates. The sequence may contain any number of
    /// <see cref="TranslationStreamUpdate.TextDelta"/> / <see cref="TranslationStreamUpdate.TextSnapshot"/>
    /// items and should end with exactly one <see cref="TranslationStreamUpdate.Completed"/>.
    /// Implementations should use [EnumeratorCancellation] on the cancellationToken parameter.
    /// </summary>
    IAsyncEnumerable<TranslationStreamUpdate> TranslateStreamUpdatesAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}
