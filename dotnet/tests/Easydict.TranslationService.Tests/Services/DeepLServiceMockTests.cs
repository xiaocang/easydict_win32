using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Mock-based tests for DeepLService covering both web and API modes.
/// </summary>
public class DeepLServiceMockTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly DeepLService _service;

    public DeepLServiceMockTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new DeepLService(_httpClient);
    }

    #region Web Translation Mode (default, no API key)

    [Fact]
    public async Task TranslateAsync_WebMode_ReturnsTranslation()
    {
        // Arrange
        var webResponse = """
            {
                "jsonrpc": "2.0",
                "id": 123000,
                "result": {
                    "texts": [{"text": "Hello"}],
                    "lang": "EN"
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(webResponse);

        var request = new TranslationRequest
        {
            Text = "Bonjour",
            FromLanguage = Language.French,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello");
        result.ServiceName.Should().Be("DeepL");
    }

    [Fact]
    public async Task TranslateAsync_WebMode_DetectsSourceLanguage()
    {
        // Arrange
        var webResponse = """
            {
                "jsonrpc": "2.0",
                "id": 123000,
                "result": {
                    "texts": [{"text": "Hello"}],
                    "lang": "FR"
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(webResponse);

        var request = new TranslationRequest
        {
            Text = "Bonjour",
            FromLanguage = Language.Auto,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.DetectedLanguage.Should().Be(Language.French);
    }

    [Fact]
    public async Task TranslateAsync_WebMode_SendsJsonRpcFormat()
    {
        // Arrange
        var webResponse = """
            {"jsonrpc":"2.0","id":123000,"result":{"texts":[{"text":"Hi"}],"lang":"EN"}}
            """;
        _mockHandler.EnqueueJsonResponse(webResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest.Should().NotBeNull();
        sentRequest!.RequestUri!.ToString().Should().Contain("www2.deepl.com/jsonrpc");

        var body = _mockHandler.LastRequestBody;
        body.Should().Contain("LMT_handle_texts");
        body.Should().Contain("\"jsonrpc\"");
    }

    [Fact]
    public async Task TranslateAsync_WebMode_SetsAntiDetectionHeaders()
    {
        // Arrange
        var webResponse = """
            {"jsonrpc":"2.0","id":123000,"result":{"texts":[{"text":"Hi"}],"lang":"EN"}}
            """;
        _mockHandler.EnqueueJsonResponse(webResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.Headers.Should().ContainSingle(h => h.Key == "Origin");
        sentRequest.Headers.GetValues("Origin").First().Should().Be("https://www.deepl.com");
    }

    [Fact]
    public async Task TranslateAsync_WebMode_ThrowsOnServerError()
    {
        // Arrange
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.InternalServerError);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
    }

    [Fact]
    public async Task TranslateAsync_WebMode_ThrowsOnJsonRpcError()
    {
        // Arrange
        var errorResponse = """
            {
                "jsonrpc": "2.0",
                "id": 123000,
                "error": {"message": "Too many requests"}
            }
            """;
        _mockHandler.EnqueueJsonResponse(errorResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
    }

    #endregion

    #region API Mode

    [Fact]
    public async Task TranslateAsync_ApiMode_ReturnsTranslation()
    {
        // Arrange
        _service.Configure("test-api-key:fx", useWebFirst: false);
        var apiResponse = """
            {
                "translations": [
                    {"text": "Bonjour", "detected_source_language": "EN"}
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(apiResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.French
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Bonjour");
    }

    [Fact]
    public async Task TranslateAsync_ApiMode_UsesFreeHostForFxKey()
    {
        // Arrange
        _service.Configure("test-key:fx", useWebFirst: false);
        var apiResponse = """{"translations":[{"text":"Hi"}]}""";
        _mockHandler.EnqueueJsonResponse(apiResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.Host.Should().Be("api-free.deepl.com");
    }

    [Fact]
    public async Task TranslateAsync_ApiMode_UsesProHostForNonFxKey()
    {
        // Arrange
        _service.Configure("test-pro-key", useWebFirst: false);
        var apiResponse = """{"translations":[{"text":"Hi"}]}""";
        _mockHandler.EnqueueJsonResponse(apiResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.Host.Should().Be("api.deepl.com");
    }

    [Fact]
    public async Task TranslateAsync_ApiMode_SendsDeepLAuthHeader()
    {
        // Arrange
        _service.Configure("my-deepl-key:fx", useWebFirst: false);
        var apiResponse = """{"translations":[{"text":"Hi"}]}""";
        _mockHandler.EnqueueJsonResponse(apiResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.Headers.Authorization.Should().NotBeNull();
        sentRequest.Headers.Authorization!.Scheme.Should().Be("DeepL-Auth-Key");
        sentRequest.Headers.Authorization.Parameter.Should().Be("my-deepl-key:fx");
    }

    [Fact]
    public async Task TranslateAsync_ApiMode_ThrowsOnForbidden()
    {
        // Arrange
        _service.Configure("invalid-key", useWebFirst: false);
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.Forbidden);

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
    public async Task TranslateAsync_ApiMode_ThrowsOnRateLimited()
    {
        // Arrange
        _service.Configure("test-key:fx", useWebFirst: false);
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
    public async Task TranslateAsync_ApiMode_DetectsLanguageFromResponse()
    {
        // Arrange
        _service.Configure("test-key:fx", useWebFirst: false);
        var apiResponse = """
            {
                "translations": [
                    {"text": "Hello", "detected_source_language": "ja"}
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(apiResponse);

        var request = new TranslationRequest
        {
            Text = "こんにちは",
            FromLanguage = Language.Auto,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello");
        result.DetectedLanguage.Should().Be(Language.Japanese);
    }

    #endregion

    #region Fallback from Web to API

    [Fact]
    public async Task TranslateAsync_FallsBackToApi_WhenWebFails()
    {
        // Arrange: configure with API key and web-first mode
        _service.Configure("test-key:fx", useWebFirst: true);

        // First request (web) fails
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.ServiceUnavailable);

        // Second request (API fallback) succeeds
        var apiResponse = """{"translations":[{"text":"Fallback result"}]}""";
        _mockHandler.EnqueueJsonResponse(apiResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Fallback result");
        _mockHandler.Requests.Should().HaveCount(2);
    }

    #endregion

    #region Language Code Mapping

    [Theory]
    [InlineData(Language.SimplifiedChinese, "ZH")]
    [InlineData(Language.TraditionalChinese, "ZH-HANT")]
    [InlineData(Language.Portuguese, "PT-PT")]
    public async Task TranslateAsync_WebMode_UsesCorrectLanguageCodes(Language targetLang, string expectedCode)
    {
        // Arrange
        var webResponse = """
            {"jsonrpc":"2.0","id":123000,"result":{"texts":[{"text":"result"}],"lang":"EN"}}
            """;
        _mockHandler.EnqueueJsonResponse(webResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = targetLang
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var body = _mockHandler.LastRequestBody;
        body.Should().Contain(expectedCode);
    }

    #endregion
}
