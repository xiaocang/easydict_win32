using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for OpenRouterService specific behavior.
/// </summary>
public class OpenRouterServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly OpenRouterService _service;

    public OpenRouterServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new OpenRouterService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsOpenRouter()
    {
        _service.ServiceId.Should().Be("openrouter");
    }

    [Fact]
    public void DisplayName_IsOpenRouter()
    {
        _service.DisplayName.Should().Be("OpenRouter");
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
        _service.Model.Should().Be("openrouter/free");
    }

    [Fact]
    public void AvailableModels_ContainsFreeAndAutoRouters()
    {
        OpenRouterService.AvailableModels.Should().Contain("openrouter/free");
        OpenRouterService.AvailableModels.Should().Contain("openrouter/auto");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesOpenRouterEndpoint()
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
        sentRequest!.RequestUri!.Host.Should().Be("openrouter.ai");
    }

    [Fact]
    public async Task TranslateStreamAsync_SendsAttributionHeaders()
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
        sentRequest!.Headers.Contains("HTTP-Referer").Should().BeTrue();
        sentRequest.Headers.Contains("X-Title").Should().BeTrue();
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
        result.ServiceName.Should().Be("OpenRouter");
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
    public async Task FetchModelsAsync_ParsesAndSortsFreeModelsFirst()
    {
        _mockHandler.EnqueueJsonResponse("""
        {
            "data": [
                { "id": "openai/gpt-5.4-mini", "name": "GPT-5.4 mini", "pricing": { "prompt": "0.0000025", "completion": "0.00001" } },
                { "id": "meta/llama-4:free", "name": "Llama 4 (free)" },
                { "id": "openrouter/auto", "name": "Auto" }
            ]
        }
        """);

        var models = await _service.FetchModelsAsync();

        models.Should().HaveCount(3);
        models[0].Id.Should().Be("meta/llama-4:free");
        models[0].IsFree.Should().BeTrue();
    }
}
