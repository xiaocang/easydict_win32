using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for GoogleTranslateService.
/// </summary>
public class GoogleTranslateServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly GoogleTranslateService _service;

    public GoogleTranslateServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new GoogleTranslateService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsGoogle()
    {
        _service.ServiceId.Should().Be("google");
    }

    [Fact]
    public void DisplayName_IsGoogleTranslate()
    {
        _service.DisplayName.Should().Be("Google Translate");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_AlwaysTrue()
    {
        // Google Translate doesn't require API key, so always configured
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
        // Arrange
        var googleResponse = """
            {
                "sentences": [
                    {"trans": "Hello", "orig": "Bonjour"}
                ],
                "src": "fr"
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

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
        result.ServiceName.Should().Be("Google Translate");
    }

    [Fact]
    public async Task TranslateAsync_ConcatenatesMultipleSentences()
    {
        // Arrange
        var googleResponse = """
            {
                "sentences": [
                    {"trans": "Hello. "},
                    {"trans": "How are you?"}
                ],
                "src": "en"
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

        var request = new TranslationRequest
        {
            Text = "Bonjour. Comment allez-vous?",
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello. How are you?");
    }

    [Fact]
    public async Task TranslateAsync_DetectsSourceLanguage()
    {
        // Arrange
        var googleResponse = """
            {
                "sentences": [{"trans": "Hello"}],
                "src": "fr"
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

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
    public async Task TranslateAsync_ThrowsOnRateLimited()
    {
        // Arrange
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
        exception.ServiceId.Should().Be("google");
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnServerError()
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
    public async Task TranslateAsync_SendsCorrectUrl()
    {
        // Arrange
        var googleResponse = """{"sentences": [{"trans": "Hi"}], "src": "en"}""";
        _mockHandler.EnqueueJsonResponse(googleResponse);

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
        sentRequest!.RequestUri!.Host.Should().Be("translate.googleapis.com");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("sl=en");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("tl=zh-CN");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("q=Hello");
    }

    [Fact]
    public async Task TranslateAsync_UsesAutoForAutoLanguage()
    {
        // Arrange
        var googleResponse = """{"sentences": [{"trans": "Hi"}], "src": "en"}""";
        _mockHandler.EnqueueJsonResponse(googleResponse);

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
        sentRequest!.RequestUri!.PathAndQuery.Should().Contain("sl=auto");
    }

    [Fact]
    public async Task DetectLanguageAsync_ReturnsDetectedLanguage()
    {
        // Arrange
        var googleResponse = """
            {
                "sentences": [{"trans": "Hello"}],
                "src": "ja"
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

        // Act
        var result = await _service.DetectLanguageAsync("konnichiwa");

        // Assert
        result.Should().Be(Language.Japanese);
    }

    [Fact]
    public async Task TranslateAsync_IncludesAlternatives_WhenAvailable()
    {
        // Arrange
        var googleResponse = """
            {
                "sentences": [{"trans": "Hello"}],
                "src": "en",
                "alternative_translations": [
                    {
                        "alternative": [
                            {"word_postproc": "Hi"},
                            {"word_postproc": "Hey"}
                        ]
                    }
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

        var request = new TranslationRequest
        {
            Text = "Bonjour",
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.Alternatives.Should().NotBeNull();
        result.Alternatives.Should().Contain("Hi");
        result.Alternatives.Should().Contain("Hey");
    }
}
