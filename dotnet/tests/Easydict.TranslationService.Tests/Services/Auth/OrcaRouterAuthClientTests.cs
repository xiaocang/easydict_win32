using System.Net;
using System.Text.Json;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Services.Auth;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services.Auth;

public class OrcaRouterAuthClientTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly OrcaRouterAuthClient _client;

    public OrcaRouterAuthClientTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _client = new OrcaRouterAuthClient(new HttpClient(_mockHandler));
    }

    [Fact]
    public void BuildAuthorizeUri_TargetsOrcaRouterAuthPage()
    {
        var uri = OrcaRouterAuthClient.BuildAuthorizeUri("http://127.0.0.1:5123/cb", "challenge", "state123");

        uri.Scheme.Should().Be("https");
        uri.Host.Should().Be("www.orcarouter.ai");
        uri.AbsolutePath.Should().Be("/auth");
    }

    [Fact]
    public void BuildAuthorizeUri_EncodesCallbackAndIncludesAllPkceParameters()
    {
        var uri = OrcaRouterAuthClient.BuildAuthorizeUri("http://127.0.0.1:5123/cb", "abc-_XYZ", "st_ate");
        var query = uri.Query;

        query.Should().StartWith("?callback_url=http%3A%2F%2F127.0.0.1%3A5123%2Fcb");
        query.Should().Contain("&code_challenge=abc-_XYZ");
        query.Should().Contain("&code_challenge_method=S256");
        query.Should().Contain("&state=st_ate");
        query.Should().Contain("&app_name=Easydict%20for%20Windows");
        query.Should().EndWith("&ref=" + OrcaRouterService.ReferralCode);
        query.Should().Contain("ref=ref_a42265f998f62828c4d6");
    }

    [Fact]
    public void BuildAuthorizeUri_EscapesCustomAppName()
    {
        var uri = OrcaRouterAuthClient.BuildAuthorizeUri("http://127.0.0.1:1/cb", "c", "s", appName: "My App&Co");

        uri.Query.Should().Contain("app_name=My%20App%26Co");
    }

    [Theory]
    [InlineData("", "challenge", "state")]
    [InlineData("http://127.0.0.1:1/cb", "", "state")]
    [InlineData("http://127.0.0.1:1/cb", "challenge", "")]
    public void BuildAuthorizeUri_RejectsMissingArguments(string callback, string challenge, string state)
    {
        var act = () => OrcaRouterAuthClient.BuildAuthorizeUri(callback, challenge, state);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ExchangeCodeForApiKeyAsync_PostsCodeAndVerifierAsJson()
    {
        _mockHandler.EnqueueJsonResponse("""{"key":"sk-orca-abc"}""");

        var key = await _client.ExchangeCodeForApiKeyAsync("code123", "verifier456");

        key.Should().Be("sk-orca-abc");

        var request = _mockHandler.LastRequest!;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://api.orcarouter.ai/api/v1/auth/keys");
        request.Headers.Authorization.Should().BeNull();
        request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var body = JsonDocument.Parse(_mockHandler.LastRequestBody!);
        var root = body.RootElement;
        root.GetProperty("code").GetString().Should().Be("code123");
        root.GetProperty("code_verifier").GetString().Should().Be("verifier456");
        root.EnumerateObject().Should().HaveCount(2, "the exchange body is exactly {code, code_verifier}");
    }

    [Fact]
    public async Task ExchangeCodeForApiKeyAsync_IdentifiesEasydict()
    {
        _mockHandler.EnqueueJsonResponse("""{"key":"sk-orca-abc"}""");

        await _client.ExchangeCodeForApiKeyAsync("code", "verifier");

        var request = _mockHandler.LastRequest!;
        request.Headers.GetValues("X-Title").Should().ContainSingle().Which.Should().Be(OrcaRouterService.AppTitle);
        request.Headers.GetValues("HTTP-Referer").Should().ContainSingle()
            .Which.Should().Be("https://github.com/xiaocang/easydict_win32");
    }

    [Theory]
    [InlineData("""{"key":"k1"}""", "k1")]
    [InlineData("""{"api_key":"k2"}""", "k2")]
    [InlineData("""{"apiKey":"k3"}""", "k3")]
    [InlineData("""{"data":{"key":"k4"}}""", "k4")]
    [InlineData("""{"data":{"api_key":"k5"}}""", "k5")]
    [InlineData("""{"success":true,"data":{"apiKey":"k6","name":"Easydict"}}""", "k6")]
    public void ParseApiKey_AcceptsKnownShapes(string json, string expected)
    {
        OrcaRouterAuthClient.ParseApiKey(json).Should().Be(expected);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"key":""}""")]
    [InlineData("""{"key":null}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":"sk-not-an-object"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void ParseApiKey_RejectsResponsesWithoutKey(string json)
    {
        var act = () => OrcaRouterAuthClient.ParseApiKey(json);

        act.Should().Throw<TranslationException>()
            .Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task ExchangeCodeForApiKeyAsync_MapsBadRequestToServiceUnavailableWithBody()
    {
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.BadRequest, "code expired");

        var act = () => _client.ExchangeCodeForApiKeyAsync("code", "verifier");

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        ex.Which.Message.Should().Contain("400").And.Contain("code expired");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TranslationErrorCode.InvalidApiKey)]
    [InlineData(HttpStatusCode.Forbidden, TranslationErrorCode.InvalidApiKey)]
    [InlineData(HttpStatusCode.TooManyRequests, TranslationErrorCode.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, TranslationErrorCode.ServiceUnavailable)]
    public async Task ExchangeCodeForApiKeyAsync_MapsStatusCodes(HttpStatusCode status, TranslationErrorCode expected)
    {
        _mockHandler.EnqueueErrorResponse(status);

        var act = () => _client.ExchangeCodeForApiKeyAsync("code", "verifier");

        (await act.Should().ThrowAsync<TranslationException>()).Which.ErrorCode.Should().Be(expected);
    }

    [Fact]
    public async Task ExchangeCodeForApiKeyAsync_MapsNetworkFailure()
    {
        var client = new OrcaRouterAuthClient(new HttpClient(new ThrowingHandler()));

        var act = () => client.ExchangeCodeForApiKeyAsync("code", "verifier");

        (await act.Should().ThrowAsync<TranslationException>()).Which.ErrorCode.Should().Be(TranslationErrorCode.NetworkError);
    }

    [Fact]
    public async Task ExchangeCodeForApiKeyAsync_RejectsEmptyArguments()
    {
        var act = () => _client.ExchangeCodeForApiKeyAsync("", "verifier");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
