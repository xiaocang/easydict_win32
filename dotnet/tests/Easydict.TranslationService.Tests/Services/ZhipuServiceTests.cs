using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for ZhipuService (智谱) specific behavior.
/// </summary>
public class ZhipuServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly ZhipuService _service;

    public ZhipuServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new ZhipuService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsZhipu()
    {
        _service.ServiceId.Should().Be("zhipu");
    }

    [Fact]
    public void DisplayName_ContainsZhipu()
    {
        _service.DisplayName.Should().Contain("Zhipu");
        _service.DisplayName.Should().Contain("智谱");
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
        ZhipuService.AvailableModels.Should().Contain("glm-4-flash-250414");
        ZhipuService.AvailableModels.Should().Contain("glm-4.5-flash");
        ZhipuService.AvailableModels.Should().Contain("glm-4.7");
        ZhipuService.AvailableModels.Should().Contain("glm-4.5-air");
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesZhipuEndpoint()
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
        sentRequest!.RequestUri!.Host.Should().Be("open.bigmodel.cn");
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
        result.ServiceName.Should().Contain("Zhipu");
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
