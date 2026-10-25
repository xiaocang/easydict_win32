using System.Net;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class MagpieGatewayDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_VerifiesMagpieAndFetchesModelsWithoutChangingRawIds()
    {
        using var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/hello" => Json("{\"name\":\"magpie\",\"version\":\"1\"}"),
            "/v1/models" => Json("{\"data\":[{\"id\":\"openai/gpt-6-luna\",\"display_name\":\"GPT 6 Luna\"}] }"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var client = new HttpClient(handler);

        var result = await MagpieGatewayDiscoveryService.DiscoverAsync(
            client, "http://127.0.0.1:4545", null, allowRemote: false);

        result.Status.Should().Be(MagpieDiscoveryStatus.Success);
        var models = result.Models ?? throw new InvalidOperationException("A successful result must include models.");
        models.Single().Id.Should().Be("openai/gpt-6-luna");
        models.Single().Name.Should().Be("GPT 6 Luna");
        handler.Requests.Select(request => request.RequestUri!.AbsoluteUri).Should().Equal(
            "http://127.0.0.1:4545/api/hello",
            "http://127.0.0.1:4545/v1/models");
        handler.Requests[1].Headers.Authorization!.Parameter.Should().Be("magpie-easydict");
    }

    [Fact]
    public async Task DiscoverAsync_RequiresRemoteOptInHttpsAndKeyBeforeSendingRequests()
    {
        using var handler = new RecordingHandler(_ => Json("{}"));
        using var client = new HttpClient(handler);

        var noOptIn = await MagpieGatewayDiscoveryService.DiscoverAsync(
            client, "https://gateway.example", "real-key", allowRemote: false);
        var httpRemote = await MagpieGatewayDiscoveryService.DiscoverAsync(
            client, "http://gateway.example", "real-key", allowRemote: true);
        var noKey = await MagpieGatewayDiscoveryService.DiscoverAsync(
            client, "https://gateway.example", null, allowRemote: true);
        var attributionKey = await MagpieGatewayDiscoveryService.DiscoverAsync(
            client, "https://gateway.example", MagpieGatewayDiscoveryService.LocalAttributionKey, allowRemote: true);

        noOptIn.Status.Should().Be(MagpieDiscoveryStatus.InvalidAddress);
        httpRemote.Status.Should().Be(MagpieDiscoveryStatus.InvalidAddress);
        noKey.Status.Should().Be(MagpieDiscoveryStatus.InvalidAddress);
        attributionKey.Status.Should().Be(MagpieDiscoveryStatus.InvalidAddress);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_UsesUserKeyOnlyAfterRemoteOptInAndReportsUnauthorized()
    {
        using var successHandler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/hello" => Json("{\"name\":\"magpie\"}"),
            _ => Json("{\"data\":[{\"id\":\"group/model\"}]}")
        });
        using var successClient = new HttpClient(successHandler);
        var success = await MagpieGatewayDiscoveryService.DiscoverAsync(
            successClient, "https://gateway.example", "authorized-key", allowRemote: true);
        success.Status.Should().Be(MagpieDiscoveryStatus.Success);
        successHandler.Requests[1].Headers.Authorization!.Parameter.Should().Be("authorized-key");

        using var unauthorizedHandler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/hello"
            ? Json("{\"name\":\"magpie\"}")
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var unauthorizedClient = new HttpClient(unauthorizedHandler);
        var unauthorized = await MagpieGatewayDiscoveryService.DiscoverAsync(
            unauthorizedClient, "http://127.0.0.1:3425", null, allowRemote: false);
        unauthorized.Status.Should().Be(MagpieDiscoveryStatus.Unauthorized);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsAnUnrelatedServerBeforeFetchingModels()
    {
        using var handler = new RecordingHandler(_ => Json("{\"name\":\"other\"}"));
        using var client = new HttpClient(handler);

        var result = await MagpieGatewayDiscoveryService.DiscoverAsync(
            client, MagpieGatewayDiscoveryService.DefaultBaseAddress, null, allowRemote: false);

        result.Status.Should().Be(MagpieDiscoveryStatus.WrongServer);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/hello");
    }

    [Fact]
    public async Task DiscoverAsync_ReportsEmptyCatalogAndHonorsCancellation()
    {
        using var emptyHandler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/hello"
            ? Json("{\"name\":\"magpie\"}")
            : Json("{\"data\":[]}"));
        using var emptyClient = new HttpClient(emptyHandler);
        var empty = await MagpieGatewayDiscoveryService.DiscoverAsync(
            emptyClient, MagpieGatewayDiscoveryService.DefaultBaseAddress, null, allowRemote: false);
        empty.Status.Should().Be(MagpieDiscoveryStatus.NoModels);

        using var cancelledHandler = new RecordingHandler(_ => Json("{}"));
        using var cancelledClient = new HttpClient(cancelledHandler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await MagpieGatewayDiscoveryService.DiscoverAsync(
            cancelledClient, MagpieGatewayDiscoveryService.DefaultBaseAddress, null, allowRemote: false, cancellation.Token);
        cancelled.Status.Should().Be(MagpieDiscoveryStatus.Cancelled);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Headers = { Authorization = request.Headers.Authorization }
            });
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
