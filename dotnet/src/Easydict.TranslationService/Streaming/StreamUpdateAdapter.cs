using System.Runtime.CompilerServices;
using System.Text;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Streaming;

/// <summary>
/// Converts a rich update stream into the legacy delta-only chunk stream expected by
/// <see cref="IStreamTranslationService.TranslateStreamAsync"/>.
/// </summary>
public static class StreamUpdateAdapter
{
    /// <summary>
    /// Project rich updates to text deltas. Snapshots and the final result are emitted as the suffix
    /// that extends the text seen so far; a snapshot that does not extend the accumulated text
    /// (non-monotonic output) is ignored because it cannot be expressed as an append.
    /// </summary>
    public static async IAsyncEnumerable<string> ToDeltas(
        IAsyncEnumerable<TranslationStreamUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var accumulated = new StringBuilder();
        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            string? emit = null;
            switch (update)
            {
                case TranslationStreamUpdate.TextDelta delta:
                    emit = delta.Text;
                    break;
                case TranslationStreamUpdate.TextSnapshot snapshot:
                    emit = SuffixIfExtends(accumulated, snapshot.Text);
                    break;
                case TranslationStreamUpdate.Completed completed:
                    emit = SuffixIfExtends(accumulated, completed.Result.TranslatedText);
                    break;
            }

            if (string.IsNullOrEmpty(emit))
            {
                continue;
            }

            accumulated.Append(emit);
            yield return emit;
        }
    }

    /// <summary>
    /// Returns the part of <paramref name="candidate"/> that follows <paramref name="accumulated"/>
    /// when the candidate starts with the accumulated text; otherwise <c>null</c>.
    /// </summary>
    internal static string? SuffixIfExtends(StringBuilder accumulated, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        if (accumulated.Length == 0)
        {
            return candidate;
        }

        if (candidate.Length < accumulated.Length)
        {
            return null;
        }

        for (var i = 0; i < accumulated.Length; i++)
        {
            if (accumulated[i] != candidate[i])
            {
                return null;
            }
        }

        return candidate.Substring(accumulated.Length);
    }
}
