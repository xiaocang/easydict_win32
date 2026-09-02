using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for OrcaRouterService specific behavior.
/// </summary>
public class OrcaRouterServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly OrcaRouterService _service;

    public OrcaRouterServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new OrcaRouterService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsOrcaRouter()
    {
        _service.ServiceId.Should().Be("orcarouter");
    }

    [Fact]
    public void DisplayName_IsOrcaRouter()
    {
        _service.DisplayName.Should().Be("OrcaRouter");
    }

    [Fact]
    public void RequiresApiKey_IsTrue()
    {
        _service.RequiresApiKey.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_IsFalse_WhenNoApiKey()
    {
        _service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsTrue_AfterConfigure()
    {
        _service.Configure("test-key");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void DefaultModel_IsFreeRouter()
    {
        _service.Configure("test-key");
        _service.Model.Should().Be("orcarouter/free");
    }

    [Fact]
    public void DefaultConfiguration_UsesOfficialOpenAICompatibleEndpoints()
    {
        _service.Endpoint.Should().Be("https://api.orcarouter.ai/v1/chat/completions");
        _service.ModelsEndpoint.Should().Be("https://api.orcarouter.ai/v1/models");
    }

    [Fact]
    public void ReferralUrl_MatchesMaintainerLink()
    {
        OrcaRouterService.ReferralUrl.Should().Be("https://www.orcarouter.ai/ref/ref_a42265f998f62828c4d6");
    }

    [Fact]
    public void AvailableModels_ContainsFreeAndAutoRouters()
    {
        OrcaRouterService.AvailableModels.Should().Contain("orcarouter/free");
        OrcaRouterService.AvailableModels.Should().Contain("orcarouter/auto");
        OrcaRouterService.AvailableModels.Should().Contain("deepseek/deepseek-v4-flash-free");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesEndpointAndAppAttributionHeaders()
    {
        _service.Configure("test-key");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.Host.Should().Be("api.orcarouter.ai");
        sentRequest.Headers.GetValues("HTTP-Referer").Should().ContainSingle(
            "https://github.com/xiaocang/easydict_win32");
        sentRequest.Headers.GetValues("X-Title").Should().ContainSingle(
            "Easydict for Windows");
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslation()
    {
        _service.Configure("test-key");
        _mockHandler.EnqueueStreamingResponse(new[]
        {
            """{"choices":[{"delta":{"content":"你好"}}]}"""
        });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.TranslatedText.Should().Be("你好");
        result.ServiceName.Should().Be("OrcaRouter");
    }

    [Fact]
    public async Task TranslateStreamAsync_ThrowsWhenNotConfigured()
    {
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        var action = async () =>
        {
            await foreach (var _ in _service.TranslateStreamAsync(request)) { }
        };

        await action.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task FetchModelsAsync_SendsBearerTokenAndParsesFreeIdSuffix()
    {
        _service.Configure("test-key");
        _mockHandler.EnqueueJsonResponse("""
        {
            "data": [
                { "id": "openai/gpt-5.4", "name": "GPT-5.4" },
                { "id": "deepseek/deepseek-v4-flash-free", "name": "DeepSeek V4 Flash (Free)" },
                { "id": "orcarouter/auto", "name": "Auto" }
            ]
        }
        """);

        var models = await _service.FetchModelsAsync();

        models.Should().HaveCount(3);
        models[0].Id.Should().Be("deepseek/deepseek-v4-flash-free");
        models[0].IsFree.Should().BeTrue();

        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.Headers.Authorization!.Parameter.Should().Be("test-key");
    }
}
