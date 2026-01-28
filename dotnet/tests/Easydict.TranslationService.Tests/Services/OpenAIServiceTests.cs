using System.Net;
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
        OpenAIService.AvailableModels.Should().Contain("gpt-4o-mini");
        OpenAIService.AvailableModels.Should().Contain("gpt-4o");
        OpenAIService.AvailableModels.Should().Contain("gpt-4-turbo");
        OpenAIService.AvailableModels.Should().Contain("gpt-3.5-turbo");
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
}
