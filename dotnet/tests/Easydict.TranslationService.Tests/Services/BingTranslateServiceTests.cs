using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for BingTranslateService.
/// </summary>
public class BingTranslateServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly BingTranslateService _service;

    /// <summary>
    /// Sample Bing Translator page HTML containing IG, IID, and token credentials.
    /// </summary>
    private const string FakeTranslatorPage = """
        <html>
        <body>
        <script>IG:"ABC123DEF456"</script>
        <div data-iid="translator.5023.1"></div>
        <script>var params_AbusePreventionHelper = [1234567890,"fakeTokenValue123",3600000];</script>
        </body>
        </html>
        """;

    public BingTranslateServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new BingTranslateService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsBing()
    {
        _service.ServiceId.Should().Be("bing");
    }

    [Fact]
    public void DisplayName_IsBingTranslate()
    {
        _service.DisplayName.Should().Be("Bing Translate");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_AlwaysTrue()
    {
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsMajorLanguages()
    {
        var languages = _service.SupportedLanguages;

        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.TraditionalChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
        languages.Should().Contain(Language.French);
        languages.Should().Contain(Language.Spanish);
        languages.Should().Contain(Language.German);
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslatedText()
    {
        // Arrange - first request fetches translator page for credentials
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        // Second request is the actual translation
        var bingResponse = """
            [{"detectedLanguage":{"language":"fr","score":1.0},"translations":[{"text":"Hello","to":"en"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse);

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
        result.OriginalText.Should().Be("Bonjour");
        result.ServiceName.Should().Be("Bing Translate");
    }

    [Fact]
    public async Task TranslateAsync_DetectsSourceLanguage()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var bingResponse = """
            [{"detectedLanguage":{"language":"ja","score":1.0},"translations":[{"text":"Hello","to":"en"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse);

        var request = new TranslationRequest
        {
            Text = "こんにちは",
            FromLanguage = Language.Auto,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.DetectedLanguage.Should().Be(Language.Japanese);
    }

    [Fact]
    public async Task TranslateAsync_HandlesSimplifiedChinese()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var bingResponse = """
            [{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好世界","to":"zh-Hans"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse);

        var request = new TranslationRequest
        {
            Text = "Hello world",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("你好世界");
    }

    [Fact]
    public async Task TranslateAsync_HandlesTraditionalChinese()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var bingResponse = """
            [{"detectedLanguage":{"language":"zh-Hant","score":1.0},"translations":[{"text":"Hello world","to":"en"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse);

        var request = new TranslationRequest
        {
            Text = "你好世界",
            FromLanguage = Language.TraditionalChinese,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello world");
        result.DetectedLanguage.Should().Be(Language.TraditionalChinese);
    }

    [Fact]
    public async Task TranslateAsync_SendsCorrectPostBody()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var bingResponse = """
            [{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"Hola","to":"es"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.Spanish
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert - second request is the translation (first is page fetch)
        _mockHandler.Requests.Should().HaveCount(2);

        var translationRequest = _mockHandler.Requests[1];
        translationRequest.Method.Should().Be(HttpMethod.Post);
        translationRequest.RequestUri!.PathAndQuery.Should().Contain("/ttranslatev3");
        translationRequest.RequestUri.PathAndQuery.Should().Contain("IG=ABC123DEF456");

        // Check POST body
        var body = _mockHandler.LastRequestBody;
        body.Should().NotBeNull();
        body.Should().Contain("text=Hello");
        body.Should().Contain("fromLang=en");
        body.Should().Contain("to=es");
        body.Should().Contain("token=fakeTokenValue123");
    }

    [Fact]
    public async Task TranslateAsync_UsesAutoDetectForAutoLanguage()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var bingResponse = """
            [{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.Auto,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var body = _mockHandler.LastRequestBody;
        body.Should().Contain("fromLang=auto-detect");
        body.Should().Contain("to=zh-Hans");
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnRateLimited()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);
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
        exception.ServiceId.Should().Be("bing");
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnServerError()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);
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
    public async Task TranslateAsync_HandlesErrorResponse()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var errorResponse = """{"statusCode":400,"errorMessage":"Invalid request"}""";
        _mockHandler.EnqueueJsonResponse(errorResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.Message.Should().Contain("Bing API error");
    }

    [Fact]
    public async Task TranslateAsync_UsesGlobalHostByDefault()
    {
        // Arrange
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var bingResponse = """
            [{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert - page fetch uses www.bing.com
        _mockHandler.Requests[0].RequestUri!.Host.Should().Be("www.bing.com");
        _mockHandler.Requests[1].RequestUri!.Host.Should().Be("www.bing.com");
    }

    [Fact]
    public async Task TranslateAsync_UsesChinaHostWhenConfigured()
    {
        // Arrange
        _service.Configure(useChinaHost: true);

        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var bingResponse = """
            [{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert - both requests use cn.bing.com
        _mockHandler.Requests[0].RequestUri!.Host.Should().Be("cn.bing.com");
        _mockHandler.Requests[1].RequestUri!.Host.Should().Be("cn.bing.com");
    }

    [Fact]
    public async Task TranslateAsync_CachesCredentials()
    {
        // Arrange - only one page fetch, but two translations
        _mockHandler.EnqueueJsonResponse(FakeTranslatorPage);

        var bingResponse1 = """
            [{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse1);

        var bingResponse2 = """
            [{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"世界","to":"zh-Hans"}]}]
            """;
        _mockHandler.EnqueueJsonResponse(bingResponse2);

        // Act - two translations
        var result1 = await _service.TranslateAsync(new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        });

        var result2 = await _service.TranslateAsync(new TranslationRequest
        {
            Text = "World",
            ToLanguage = Language.SimplifiedChinese
        });

        // Assert - credentials fetched once, translation called twice
        _mockHandler.Requests.Should().HaveCount(3); // 1 page fetch + 2 translations
        result1.TranslatedText.Should().Be("你好");
        result2.TranslatedText.Should().Be("世界");
    }
}
