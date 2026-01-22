using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for NiuTransService (小牛翻译).
/// </summary>
public class NiuTransServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly NiuTransService _service;

    public NiuTransServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new NiuTransService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsNiutrans()
    {
        _service.ServiceId.Should().Be("niutrans");
    }

    [Fact]
    public void DisplayName_IsNiuTrans()
    {
        _service.DisplayName.Should().Be("NiuTrans");
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
    public void SupportedLanguages_ContainsManyLanguages()
    {
        var languages = _service.SupportedLanguages;

        // NiuTrans supports 450+ languages, verify major ones
        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.TraditionalChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
        languages.Should().Contain(Language.French);
        languages.Should().Contain(Language.Spanish);
        languages.Should().Contain(Language.German);
        languages.Should().Contain(Language.Russian);
        languages.Should().Contain(Language.Arabic);
        languages.Should().Contain(Language.Italian);
        languages.Should().Contain(Language.Portuguese);
        languages.Should().Contain(Language.Dutch);
        languages.Should().Contain(Language.Polish);
        languages.Should().Contain(Language.Turkish);
        languages.Should().Contain(Language.Vietnamese);
        languages.Should().Contain(Language.Thai);
        languages.Should().Contain(Language.Indonesian);
    }

    [Fact]
    public async Task TranslateAsync_EnglishToChinese_ReturnsTranslation()
    {
        // Arrange
        _service.Configure("test-api-key");
        var niutransResponse = """
            {
                "tgt_text": "你好，世界！"
            }
            """;
        _mockHandler.EnqueueJsonResponse(niutransResponse);

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
        result.ServiceName.Should().Be("NiuTrans");
    }

    [Fact]
    public async Task TranslateAsync_SendsCorrectRequestFormat()
    {
        // Arrange
        _service.Configure("test-key");
        var niutransResponse = """{"tgt_text": "Hola"}""";
        _mockHandler.EnqueueJsonResponse(niutransResponse);

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
        requestBody.Should().Contain("\"src_text\":\"Hello\"");
        requestBody.Should().Contain("\"from\":\"en\"");
        requestBody.Should().Contain("\"to\":\"es\"");
    }

    [Fact]
    public async Task TranslateAsync_IncludesHMACAuthorizationHeader()
    {
        // Arrange
        _service.Configure("test-api-key");
        var niutransResponse = """{"tgt_text": "测试"}""";
        _mockHandler.EnqueueJsonResponse(niutransResponse);

        var request = new TranslationRequest
        {
            Text = "Test",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest.Should().NotBeNull();
        
        // Verify Authorization header exists with HMAC signature
        sentRequest!.Headers.Should().ContainSingle(h => h.Key == "Authorization");
        var authHeader = sentRequest.Headers.GetValues("Authorization").First();
        authHeader.Should().Contain("algorithm=\"hmac-sha256\"");
        authHeader.Should().Contain("headers=\"host date request-line digest\"");
        authHeader.Should().Contain("signature=");
    }

    [Fact]
    public async Task TranslateAsync_IncludesDigestHeader()
    {
        // Arrange
        _service.Configure("test-api-key");
        var niutransResponse = """{"tgt_text": "测试"}""";
        _mockHandler.EnqueueJsonResponse(niutransResponse);

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
        
        // Verify Digest header exists (SHA-256 hash of request body)
        sentRequest!.Headers.Should().ContainSingle(h => h.Key == "Digest");
        var digestHeader = sentRequest.Headers.GetValues("Digest").First();
        digestHeader.Should().StartWith("SHA-256=");
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
        exception.ServiceId.Should().Be("niutrans");
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnInvalidApiKey()
    {
        // Arrange
        _service.Configure("invalid-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.Unauthorized, "Invalid signature");

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
    public async Task TranslateAsync_UsesAutoForAutoLanguage()
    {
        // Arrange
        _service.Configure("test-key");
        var niutransResponse = """{"tgt_text": "你好"}""";
        _mockHandler.EnqueueJsonResponse(niutransResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.Auto,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var requestBody = _mockHandler.LastRequestBody;
        requestBody.Should().Contain("\"from\":\"auto\"");
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
        // NiuTrans doesn't have separate detection endpoint
        var result = await _service.DetectLanguageAsync("Hello");
        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task TranslateAsync_HandlesEmptyResponse()
    {
        // Arrange
        _service.Configure("test-key");
        var niutransResponse = """{"tgt_text": ""}""";
        _mockHandler.EnqueueJsonResponse(niutransResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().BeEmpty();
    }
}
