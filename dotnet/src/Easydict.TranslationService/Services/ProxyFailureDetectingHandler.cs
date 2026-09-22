using System.Net;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Sits above the transport and re-labels a failed connection as a proxy failure, but only for
/// requests that were actually routed through the configured proxy.
/// </summary>
/// <remarks>
/// The bypass check is what makes the label trustworthy: with "bypass proxy for localhost" on,
/// a local Ollama that is simply not running still reports itself, not the proxy.
/// </remarks>
internal sealed class ProxyFailureDetectingHandler : DelegatingHandler
{
    private readonly IWebProxy _proxy;
    private readonly string _proxyEndpoint;

    internal ProxyFailureDetectingHandler(HttpMessageHandler innerHandler, IWebProxy proxy, string proxyEndpoint)
        : base(innerHandler)
    {
        _proxy = proxy;
        _proxyEndpoint = proxyEndpoint;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (
            ex is not ProxyUnreachableException
            && GoesThroughProxy(request.RequestUri)
            && ProxyFailureClassifier.IsFirstHopFailure(ex))
        {
            throw new ProxyUnreachableException(_proxyEndpoint, ex);
        }
        catch (OperationCanceledException ex) when (
            !cancellationToken.IsCancellationRequested
            && ProxyFailureClassifier.IsConnectTimeout(ex)
            && GoesThroughProxy(request.RequestUri))
        {
            // A proxy that silently drops connection attempts never produces an
            // HttpRequestException: the connection pool's own ConnectTimeout fires first and
            // reports a cancellation. Our token being uncancelled is what rules out the caller
            // giving up and HttpClient.Timeout, which cancel it before any of this unwinds.
            throw new ProxyUnreachableException(_proxyEndpoint, ex);
        }
    }

    private bool GoesThroughProxy(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return false;
        }

        try
        {
            return !_proxy.IsBypassed(requestUri) && _proxy.GetProxy(requestUri) is not null;
        }
        catch (Exception)
        {
            // A proxy that cannot even answer "do you handle this?" is not something to
            // attribute a failure to.
            return false;
        }
    }
}
