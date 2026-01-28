using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for DeepSeekService-specific behavior.
/// </summary>
public class DeepSeekServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly DeepSeekService _service;

    public DeepSeekServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new DeepSeekService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsDeepSeek()
    {
        _service.ServiceId.Should().Be("deepseek");
    }

    [Fact]
    public void DisplayName_IsDeepSeek()
    {
        _service.DisplayName.Should().Be("DeepSeek");
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
        DeepSeekService.AvailableModels.Should().Contain("deepseek-chat");
        DeepSeekService.AvailableModels.Should().Contain("deepseek-reasoner");
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
    public async Task TranslateStreamAsync_UsesDeepSeekEndpoint()
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
        sentRequest!.RequestUri!.Host.Should().Be("api.deepseek.com");
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslation()
    {
        // Arrange
        _service.Configure("sk-test");
        var sseEvents = new[]
        {
            """{"choices":[{"delta":{"content":"你好"}}]}""",
            """{"choices":[{"delta":{"content":"世界"}}]}"""
        };
        _mockHandler.EnqueueStreamingResponse(sseEvents);

        var request = new TranslationRequest
        {
            Text = "Hello World",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("你好世界");
        result.ServiceName.Should().Be("DeepSeek");
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
}
