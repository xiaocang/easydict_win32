using System.Net;
using System.Text;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for OpenAIService-specific behavior (identity, configuration, models).
/// Base streaming/translation behavior is covered by BaseOpenAIServiceTests.
/// </summary>
public class OpenAIServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly OpenAIService _service;

    public OpenAIServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new OpenAIService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsOpenAI()
    {
        _service.ServiceId.Should().Be("openai");
    }

    [Fact]
    public void DisplayName_IsOpenAI()
    {
        _service.DisplayName.Should().Be("OpenAI");
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
        _service.Configure("sk-test");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void AvailableModels_ContainsExpectedModels()
    {
        OpenAIService.AvailableModels.Should().Contain(OpenAIService.DefaultModel);
        OpenAIService.AvailableModels.Should().Contain("gpt-5.4-mini");
        OpenAIService.AvailableModels.Should().Contain("gpt-5.1");
        OpenAIService.AvailableModels.Should().Contain("gpt-5-mini");
        OpenAIService.AvailableModels.Should().Contain("gpt-5-nano");
        OpenAIService.AvailableModels.Should().Contain("gpt-5");
        OpenAIService.AvailableModels.Should().Contain("gpt-4o-mini");
        OpenAIService.AvailableModels.Should().Contain("gpt-4o");
    }

    [Fact]
    public void Configure_SetsCustomEndpoint()
    {
        _service.Configure("sk-test", endpoint: "https://custom.api.com/v1/chat/completions");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void Configure_SetsCustomModel()
    {
        _service.Configure("sk-test", model: "gpt-4o");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void Configure_ClampsTemperature()
    {
        _service.Configure("sk-test", temperature: -1.0);
        _service.IsConfigured.Should().BeTrue();

        _service.Configure("sk-test", temperature: 5.0);
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesOpenAIEndpoint()
    {
        // Arrange
        _service.Configure("sk-test");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.Host.Should().Be("api.openai.com");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesCustomEndpoint_WhenConfigured()
    {
        // Arrange
        _service.Configure("sk-test", endpoint: "https://my-proxy.com/v1/chat/completions");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.Host.Should().Be("my-proxy.com");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesNoneReasoning_WithModernGpt5ResponsesModel()
    {
        _service.Configure("sk-test", model: "gpt-5.4-mini", temperature: 0.3);
        EnqueueResponsesStream("Hi");

        await ConsumeAsync(_service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        }));

        var body = _mockHandler.LastRequestBody!;
        body.Should().Contain("\"temperature\":0.3");
        body.Should().Contain("\"reasoning\":");
        body.Should().Contain("\"effort\":\"none\"");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesCompatibleTemperature_WithLegacyGpt5ResponsesModel()
    {
        _service.Configure("sk-test", model: "gpt-5-mini", temperature: 0.3);
        EnqueueResponsesStream("Hi");

        await ConsumeAsync(_service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        }));

        var body = _mockHandler.LastRequestBody!;
        body.Should().Contain("\"temperature\":1");
        body.Should().Contain("\"reasoning\":");
        body.Should().Contain("\"effort\":\"minimal\"");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesReasoningEffort_WithModernGpt5ChatCompletionsModel()
    {
        _service.Configure(
            "sk-test",
            endpoint: OpenAIService.LegacyChatCompletionsEndpoint,
            model: "gpt-5.4-mini",
            temperature: 0.3);
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        await ConsumeAsync(_service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        }));

        var body = _mockHandler.LastRequestBody!;
        body.Should().Contain("\"temperature\":0.3");
        body.Should().Contain("\"reasoning_effort\":\"none\"");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesCompatibleTemperature_WithLegacyGpt5ChatCompletionsModel()
    {
        _service.Configure(
            "sk-test",
            endpoint: OpenAIService.LegacyChatCompletionsEndpoint,
            model: "gpt-5-mini",
            temperature: 0.3);
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        await ConsumeAsync(_service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        }));

        var body = _mockHandler.LastRequestBody!;
        body.Should().Contain("\"temperature\":1");
        body.Should().Contain("\"reasoning_effort\":\"minimal\"");
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<string> stream)
    {
        await foreach (var _ in stream) { }
    }

    private void EnqueueResponsesStream(string text)
    {
        var sse =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"" + text + "\"}\n" +
            "\n" +
            "data: [DONE]\n\n";

        _mockHandler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
    }
}
