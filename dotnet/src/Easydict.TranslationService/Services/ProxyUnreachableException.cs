namespace Easydict.TranslationService.Services;

/// <summary>
/// Raised when a request that was routed through the user's configured HTTP proxy never got
/// past that proxy — the connection to it was refused, timed out, or its host did not resolve.
/// </summary>
/// <remarks>
/// It derives from <see cref="HttpRequestException"/> so the services keep their existing
/// <c>catch (HttpRequestException)</c> handling; what it adds is the certainty that the proxy
/// itself is the broken hop. <see cref="TranslationManager"/> uses that to report
/// <see cref="TranslationErrorCode.ProxyError"/> and to skip the host retries, which would
/// otherwise triple the wait in front of an error only the user can fix.
/// </remarks>
public sealed class ProxyUnreachableException : HttpRequestException
{
    public ProxyUnreachableException(string proxyEndpoint, Exception inner)
        : base(BuildMessage(proxyEndpoint, inner), inner)
    {
        ProxyEndpoint = proxyEndpoint;
    }

    /// <summary>
    /// The configured proxy that could not be reached, as "scheme://host:port".
    /// </summary>
    public string ProxyEndpoint { get; }

    private static string BuildMessage(string proxyEndpoint, Exception inner)
    {
        return $"Cannot reach the HTTP proxy {proxyEndpoint}: {DescribeCause(inner)}";
    }

    /// <summary>
    /// The innermost message describes the actual hop failure ("connection refused", "host
    /// unreachable"); the outer HTTP layer only repeats a generic "error occurred". Transport
    /// messages name the proxy endpoint, never the request URL, so nothing about the query text
    /// can reach the UI through here.
    /// </summary>
    private static string DescribeCause(Exception inner)
    {
        var cause = inner;
        while (cause.InnerException is not null)
        {
            cause = cause.InnerException;
        }

        return cause.Message;
    }
}
