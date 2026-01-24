using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for LingueeService (dictionary with context examples).
/// </summary>
public class LingueeServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly LingueeService _service;

    public LingueeServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new LingueeService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsLinguee()
    {
        _service.ServiceId.Should().Be("linguee");
    }

    [Fact]
    public void DisplayName_IsLingueeDictionary()
    {
        _service.DisplayName.Should().Be("Linguee Dictionary");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        // Linguee uses public proxy API, no key required
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_AlwaysTrue()
    {
        // No configuration needed for Linguee
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsEuropeanLanguages()
    {
        var languages = _service.SupportedLanguages;

        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.German);
        languages.Should().Contain(Language.French);
        languages.Should().Contain(Language.Spanish);
        languages.Should().Contain(Language.Italian);
        languages.Should().Contain(Language.Portuguese);
        languages.Should().Contain(Language.Dutch);
        languages.Should().Contain(Language.Polish);
        languages.Should().Contain(Language.Russian);
        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.Japanese);
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslationWithExamples()
    {
        // Arrange
        var lingueeResponse = """
            {
                "exact": [
                    {"word": "hello", "translations": [{"word": "bonjour"}]}
                ],
                "featured_word": "hello",
                "query": "hello"
            }
            """;
        _mockHandler.EnqueueJsonResponse(lingueeResponse);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.French
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("bonjour");
        result.OriginalText.Should().Be("hello");
        result.ServiceName.Should().Be("Linguee Dictionary");
    }

    [Fact]
    public async Task TranslateAsync_IncludesAlternativeTranslations()
    {
        // Arrange
        var lingueeResponse = """
            {
                "exact": [
                    {
                        "word": "good",
                        "translations": [
                            {"word": "bon"},
                            {"word": "bien"},
                            {"word": "bonne"}
                        ]
                    }
                ],
                "featured_word": "good",
                "query": "good"
            }
            """;
        _mockHandler.EnqueueJsonResponse(lingueeResponse);

        var request = new TranslationRequest
        {
            Text = "good",
            FromLanguage = Language.English,
            ToLanguage = Language.French
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("bon");
        result.Alternatives.Should().NotBeNull();
        result.Alternatives.Should().Contain("bien");
        result.Alternatives.Should().Contain("bonne");
    }

    [Fact]
    public async Task TranslateAsync_HandlesEmptyResults()
    {
        // Arrange
        var lingueeResponse = """
            {
                "exact": [],
                "featured_word": "",
                "query": "nonexistentword"
            }
            """;
        _mockHandler.EnqueueJsonResponse(lingueeResponse);

        var request = new TranslationRequest
        {
            Text = "nonexistentword",
            FromLanguage = Language.English,
            ToLanguage = Language.French
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("nonexistentword");
        result.Alternatives.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnServerError()
    {
        // Arrange
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.InternalServerError);

        var request = new TranslationRequest
        {
            Text = "hello",
            ToLanguage = Language.French
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        exception.ServiceId.Should().Be("linguee");
    }

    [Fact]
    public async Task TranslateAsync_SendsCorrectUrl()
    {
        // Arrange
        var lingueeResponse = """{"exact": [{"word": "hello", "translations": [{"word": "bonjour"}]}]}""";
        _mockHandler.EnqueueJsonResponse(lingueeResponse);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.French
        };

        // Act
        await _service.TranslateAsync(request);

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest.Should().NotBeNull();
        sentRequest!.RequestUri!.Host.Should().Be("linguee-api.fly.dev");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("/api/v2/translations");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("query=hello");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("src=en");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("dst=fr");
    }

    [Fact]
    public async Task TranslateAsync_HandlesInvalidLanguagePair()
    {
        // Linguee doesn't support all language pairs
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.Arabic,
            ToLanguage = Language.Vietnamese
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.UnsupportedLanguage);
    }

    [Fact]
    public async Task DetectLanguageAsync_ReturnsAuto()
    {
        // Linguee doesn't support language detection
        var result = await _service.DetectLanguageAsync("hello");
        result.Should().Be(Language.Auto);
    }
}
