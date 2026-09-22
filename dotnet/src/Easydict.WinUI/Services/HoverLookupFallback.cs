using System.Diagnostics;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>Tries services sequentially until one produces a meaning the popup can display.</summary>
internal static class HoverLookupFallback
{
    /// <param name="isNetworkFree">
    /// Identifies candidates that answer without the network. Used only to move them forward once
    /// the proxy is known to be dead, never to drop a candidate — see
    /// <see cref="HoverLookupRules.IsNetworkFree"/> for why the signal is not strong enough for
    /// that.
    /// </param>
    internal static async Task<TranslationResult?> TranslateAsync(
        IReadOnlyList<string> serviceIds,
        Func<string, CancellationToken, Task<TranslationResult>> translate,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken,
        Func<string, bool>? isNetworkFree = null)
    {
        Exception? lastError = null;
        TranslationException? proxyFailure = null;
        var pending = new List<string>(serviceIds);
        var promotedNetworkFree = false;

        while (pending.Count > 0)
        {
            var serviceId = pending[0];
            pending.RemoveAt(0);

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
                // a dead proxy does not mean the rest of the chain cannot answer. What it does
                // mean is that waiting out a connect timeout per remote candidate first is wasted,
                // so try the network-free ones before them. A stable partition, not a filter:
                // every candidate is still attempted, because nothing here proves that the ones
                // left use the dead proxy.
                if (ex is TranslationException { ErrorCode: TranslationErrorCode.ProxyError } proxyEx)
                {
                    proxyFailure ??= proxyEx;

                    if (!promotedNetworkFree && isNetworkFree is not null)
                    {
                        promotedNetworkFree = true;
                        pending = pending.Where(isNetworkFree)
                            .Concat(pending.Where(id => !isNetworkFree(id)))
                            .ToList();
                    }
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
