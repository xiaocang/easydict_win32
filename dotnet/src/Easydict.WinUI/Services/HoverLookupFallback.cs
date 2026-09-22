using System.Diagnostics;
using Easydict.TranslationService;
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
        TranslationException? proxyFailure = null;
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

                // Keep going even on a proxy failure: the candidate order puts locally imported
                // mdx:: dictionaries after the remote ones, and those do no networking at all, so
                // a dead proxy does not mean the rest of the chain cannot answer.
                if (ex is TranslationException { ErrorCode: TranslationErrorCode.ProxyError } proxyEx)
                {
                    proxyFailure ??= proxyEx;
                }

                lastError = ex;
                Debug.WriteLine($"[HoverLookup] Service '{serviceId}' failed; trying next service: {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Prefer a proxy failure as the reported cause: when nothing answered, the dead proxy is
        // the one thing the user can act on, and it should not be buried under whichever service
        // happened to fail last.
        var cause = proxyFailure ?? lastError;
        if (cause != null)
        {
            // A service timeout is a lookup error, not cancellation of the whole hover operation.
            throw new InvalidOperationException("All hover lookup services failed or returned no meaning.", cause);
        }

        return null;
    }
}
