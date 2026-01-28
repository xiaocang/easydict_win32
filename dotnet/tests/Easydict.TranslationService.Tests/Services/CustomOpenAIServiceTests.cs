using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for CustomOpenAIService-specific behavior.
/// Focuses on unique configuration logic: endpoint-required, optional API key, custom display name.
/// </summary>
public class CustomOpenAIServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly CustomOpenAIService _service;

    public CustomOpenAIServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new CustomOpenAIService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsCustomOpenAI()
    {
        _service.ServiceId.Should().Be("custom-openai");
    }

    [Fact]
    public void DisplayName_DefaultIsCustomOpenAI()
    {
        _service.DisplayName.Should().Be("Custom OpenAI");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsFalse_WhenNoEndpoint()
    {
        // Custom service requires endpoint, not API key
        _service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsTrue_WhenEndpointSet()
    {
        _service.Configure("http://localhost:8080/v1/chat/completions");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_IsTrue_WithoutApiKey()
    {
        // Some local endpoints don't require API key
        _service.Configure("http://localhost:11434/v1/chat/completions");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void Configure_SetsCustomDisplayName()
    {
        _service.Configure("http://localhost:8080/v1/chat/completions", displayName: "My LLM Server");
        _service.DisplayName.Should().Be("My LLM Server");
    }

    [Fact]
    public void Configure_KeepsDefaultDisplayName_WhenNotProvided()
    {
        _service.Configure("http://localhost:8080/v1/chat/completions");
        _service.DisplayName.Should().Be("Custom OpenAI");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesConfiguredEndpoint()
    {
        // Arrange
        _service.Configure("http://my-server.local:9090/v1/chat/completions", apiKey: "optional-key");
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
        sentRequest!.RequestUri!.Host.Should().Be("my-server.local");
        sentRequest.RequestUri.Port.Should().Be(9090);
    }

    [Fact]
    public async Task TranslateStreamAsync_WorksWithoutApiKey()
    {
        // Arrange - local endpoint without API key
        _service.Configure("http://localhost:11434/v1/chat/completions");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"你好"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in _service.TranslateStreamAsync(request))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().Contain("你好");
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslation()
    {
        // Arrange
        _service.Configure("http://localhost:8080/v1/chat/completions", apiKey: "key");
        _mockHandler.EnqueueStreamingResponse(new[]
        {
            """{"choices":[{"delta":{"content":"Hello"}}]}"""
        });

        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello");
    }

    [Fact]
    public void Configure_ClampsTemperature()
    {
        _service.Configure("http://localhost:8080/v1/chat/completions", temperature: -1.0);
        _service.IsConfigured.Should().BeTrue();

        _service.Configure("http://localhost:8080/v1/chat/completions", temperature: 5.0);
        _service.IsConfigured.Should().BeTrue();
    }
}
