using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for KimiService (Moonshot AI) specific behavior.
/// </summary>
public class KimiServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly KimiService _service;

    public KimiServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new KimiService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsKimi()
    {
        _service.ServiceId.Should().Be("kimi");
    }

    [Fact]
    public void DisplayName_ContainsKimi()
    {
        _service.DisplayName.Should().Contain("Kimi");
        _service.DisplayName.Should().Contain("Moonshot");
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
    public void AvailableModels_ContainsExpectedModels()
    {
        KimiService.AvailableModels.Should().Contain("kimi-k2-turbo-preview");
        KimiService.AvailableModels.Should().Contain("kimi-k2-0905-preview");
        KimiService.AvailableModels.Should().Contain("kimi-latest");
        KimiService.AvailableModels.Should().Contain("moonshot-v1-8k");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesKimiEndpoint()
    {
        // Arrange
        _service.Configure("test-key");
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
        sentRequest!.RequestUri!.Host.Should().Be("api.moonshot.cn");
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslation()
    {
        // Arrange
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

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("你好");
        result.ServiceName.Should().Contain("Kimi");
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
