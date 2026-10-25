using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

public sealed class MagpieServiceTests
{
    [Theory]
    [InlineData("https://gateway.example/v1/chat/completions", "authorized-key", false)]
    [InlineData("http://gateway.example/v1/chat/completions", "authorized-key", true)]
    [InlineData("https://gateway.example/v1/chat/completions", "", true)]
    [InlineData("https://gateway.example/v1/chat/completions", "magpie-easydict", true)]
    public async Task RemoteGatewayAccess_IsRejectedBeforeCatalogTranslationOrCorrectionRequests(
        string endpoint,
        string apiKey,
        bool allowRemote)
    {
        var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var service = new MagpieService(client);
        service.Configure(endpoint, apiKey, "provider/model", allowRemote);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FetchModelsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "source",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(service.CorrectGrammarStreamAsync(new GrammarCorrectionRequest
        {
            Text = "source",
            Language = Language.English
        })));

        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task RemoteGatewayAccess_AllowsExplicitHttpsOptInWithAuthorizedKey()
    {
        var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var service = new MagpieService(client);
        service.Configure("https://gateway.example/v1/chat/completions", "authorized-key", "provider/model", allowRemote: true);
        handler.EnqueueJsonResponse("""{"data":[{"id":"provider/model"}]}""");

        var models = await service.FetchModelsAsync();

        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("https://gateway.example/v1/models");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("authorized-key");
        models.Should().ContainSingle(model => model.Id == "provider/model");
    }

    [Fact]
    public async Task FetchModelsAsync_UsesGatewayCatalogAndKeepsRawIds()
    {
        var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var service = new MagpieService(client);
        service.Configure("http://127.0.0.1:4545/v1/chat/completions", "magpie-easydict", "provider/model");
        handler.EnqueueJsonResponse("""{"data":[{"id":"provider/model","display_name":"Model Label"}]}""");

        var models = await service.FetchModelsAsync();

        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("http://127.0.0.1:4545/v1/models");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("magpie-easydict");
        models.Single().Id.Should().Be("provider/model");
        models.Single().Name.Should().Be("Model Label");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesConfiguredChatCompletionsEndpointAndRawModelId()
    {
        var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var service = new MagpieService(client);
        service.Configure("http://127.0.0.1:4545/v1/chat/completions", "magpie-easydict", "openai/gpt-6-luna");
        handler.EnqueueStreamingResponse(["""{"choices":[{"delta":{"content":"translated"}}]}"""]);

        var chunks = new List<string>();
        await foreach (var chunk in service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "source",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        }))
        {
            chunks.Add(chunk);
        }

        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("http://127.0.0.1:4545/v1/chat/completions");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("magpie-easydict");
        handler.LastRequestBody.Should().Contain("\"model\":\"openai/gpt-6-luna\"");
        chunks.Should().Contain("translated");
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<string> stream)
    {
        await foreach (var _ in stream) { }
    }
}
