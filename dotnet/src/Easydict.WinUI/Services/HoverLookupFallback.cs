using System.Diagnostics;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>Tries services sequentially until one produces a meaning the popup can display.</summary>
internal static class HoverLookupFallback
{
    internal static async Task<TranslationResult?> TranslateAsync(
        IReadOnlyList<string> serviceIds,
        Func<string, CancellationToken, Task<TranslationResult>> translate,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var serviceId in serviceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(attemptTimeout);
            try
            {
                var result = await translate(serviceId, attempt.Token).WaitAsync(attempt.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.ResultKind == TranslationResultKind.Success &&
                    !string.IsNullOrWhiteSpace(HoverLookupContentBuilder.FormatBody(result, null)))
                {
                    return result;
                }

                Debug.WriteLine($"[HoverLookup] Service '{serviceId}' returned no meaning; trying next service");
            }
            catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastError = ex;
                Debug.WriteLine($"[HoverLookup] Service '{serviceId}' failed; trying next service: {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (lastError != null)
        {
            // A service timeout is a lookup error, not cancellation of the whole hover operation.
            throw new InvalidOperationException("All hover lookup services failed or returned no meaning.", lastError);
        }

        return null;
    }
}
