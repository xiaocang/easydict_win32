using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for CaiyunService (彩云小译).
/// </summary>
public class CaiyunServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly CaiyunService _service;

    public CaiyunServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new CaiyunService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsCaiyun()
    {
        _service.ServiceId.Should().Be("caiyun");
    }

    [Fact]
    public void DisplayName_IsCaiyun()
    {
        _service.DisplayName.Should().Be("Caiyun");
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
    public void IsConfigured_IsTrue_AfterConfiguration()
    {
        _service.Configure("test-api-key");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsChineseEnglishAndMore()
    {
        var languages = _service.SupportedLanguages;

        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.TraditionalChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
        languages.Should().Contain(Language.Spanish);
        languages.Should().Contain(Language.French);
        languages.Should().Contain(Language.Russian);
        languages.Should().Contain(Language.German);
        languages.Should().Contain(Language.Italian);
        languages.Should().Contain(Language.Portuguese);
    }

    [Fact]
    public async Task TranslateAsync_EnglishToChinese_ReturnsTranslation()
    {
        // Arrange
        _service.Configure("test-api-key");
        var caiyunResponse = """
            {
                "target": ["你好，世界！"]
            }
            """;
        _mockHandler.EnqueueJsonResponse(caiyunResponse);

        var request = new TranslationRequest
        {
            Text = "Hello, world!",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("你好，世界！");
        result.OriginalText.Should().Be("Hello, world!");
        result.ServiceName.Should().Be("Caiyun");
    }

    [Fact]
    public async Task TranslateAsync_AutoDetect_UsesAuto2zh()
    {
        // Arrange
        _service.Configure("test-api-key");
        var caiyunResponse = """
            {
                "target": ["你好"]
            }
            """;
        _mockHandler.EnqueueJsonResponse(caiyunResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.Auto,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        var requestBody = _mockHandler.LastRequestBody;
        requestBody.Should().Contain("\"trans_type\":\"auto2zh\"");
    }

    [Fact]
    public async Task TranslateAsync_SendsCorrectAuthorizationHeader()
    {
        // Arrange
        var apiKey = "test-api-key-12345";
        _service.Configure(apiKey);
        var caiyunResponse = """{"target": ["测试"]}""";
        _mockHandler.EnqueueJsonResponse(caiyunResponse);

        var request = new TranslationRequest
        {
            Text = "Test",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest.Should().NotBeNull();
        sentRequest!.Headers.Should().ContainSingle(h => h.Key == "X-Authorization");
        var authHeader = sentRequest.Headers.GetValues("X-Authorization").First();
        authHeader.Should().Be($"token {apiKey}");
    }

    [Fact]
    public async Task TranslateAsync_SendsCorrectRequestFormat()
    {
        // Arrange
        _service.Configure("test-key");
        var caiyunResponse = """{"target": ["Hola"]}""";
        _mockHandler.EnqueueJsonResponse(caiyunResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.Spanish
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var requestBody = _mockHandler.LastRequestBody;
        requestBody.Should().NotBeNull();
        requestBody.Should().Contain("\"source\":[\"Hello\"]");
        requestBody.Should().Contain("\"trans_type\":\"en2es\"");
        requestBody.Should().Contain("\"request_id\":");
    }

    [Fact]
    public async Task TranslateAsync_ThrowsWhenNotConfigured()
    {
        // Arrange
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
        exception.ServiceId.Should().Be("caiyun");
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnInvalidApiKey()
    {
        // Arrange
        _service.Configure("invalid-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.Unauthorized, "Invalid API key");

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnRateLimited()
    {
        // Arrange
        _service.Configure("test-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
    }

    [Fact]
    public async Task TranslateAsync_ConcatenatesMultipleTargetLines()
    {
        // Arrange
        _service.Configure("test-key");
        var caiyunResponse = """
            {
                "target": ["Hello, ", "world!"]
            }
            """;
        _mockHandler.EnqueueJsonResponse(caiyunResponse);

        var request = new TranslationRequest
        {
            Text = "你好，世界！",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello, world!");
    }

    [Fact]
    public void Configure_SetsApiKey()
    {
        var apiKey = "my-api-key";
        _service.Configure(apiKey);

        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task DetectLanguageAsync_ReturnsAuto()
    {
        // Caiyun supports auto-detection but we don't expose separate detection endpoint
        var result = await _service.DetectLanguageAsync("Hello");
        result.Should().Be(Language.Auto);
    }
}
