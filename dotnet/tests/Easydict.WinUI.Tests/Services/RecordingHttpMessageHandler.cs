using System.Net;
using System.Net.Http.Headers;

namespace Easydict.WinUI.Tests.Services;

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

    public Uri? LastRequestUri { get; private set; }

    public AuthenticationHeaderValue? LastAuthorization { get; private set; }

    public string? LastRequestBody { get; private set; }

    public string? LastContentType { get; private set; }

    public RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responseFactory = null)
    {
        _responseFactory = responseFactory ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastAuthorization = request.Headers.Authorization;

        if (request.Content is not null)
        {
            LastContentType = request.Content.Headers.ContentType?.MediaType;
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return await _responseFactory(request, cancellationToken);
    }
}
