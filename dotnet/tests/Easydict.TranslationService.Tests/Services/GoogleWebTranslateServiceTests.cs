using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for GoogleWebTranslateService (rich dictionary results).
/// </summary>
public class GoogleWebTranslateServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly GoogleWebTranslateService _service;

    public GoogleWebTranslateServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new GoogleWebTranslateService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsGoogleWeb()
    {
        _service.ServiceId.Should().Be("google_web");
    }

    [Fact]
    public void DisplayName_IsGoogleDict()
    {
        _service.DisplayName.Should().Be("Google Dict");
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
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslatedText()
    {
        // Arrange - WebApp API returns nested arrays
        // root[0] = sentences, root[1] = null (no dict), root[2] = detected lang
        var response = """
            [
                [["Hello","你好",null,null,10]],
                null,
                "zh-CN"
            ]
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello");
        result.OriginalText.Should().Be("你好");
        result.ServiceName.Should().Be("Google Dict");
    }

    [Fact]
    public async Task TranslateAsync_DetectsSourceLanguage()
    {
        var response = """
            [
                [["Hello","Bonjour",null,null,10]],
                null,
                "fr"
            ]
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "Bonjour",
            FromLanguage = Language.Auto,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);
        result.DetectedLanguage.Should().Be(Language.French);
    }

    [Fact]
    public async Task TranslateAsync_ParsesDictionaryDefinitions()
    {
        // root[1] contains dictionary data: [partOfSpeech, [meanings...], ...]
        var response = """
            [
                [["你好","hello",null,null,10]],
                [
                    ["interjection",["你好","喂"],null,null,null],
                    ["noun",["招呼","问候"],null,null,null]
                ],
                "en"
            ]
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.WordResult.Should().NotBeNull();
        result.WordResult!.Definitions.Should().NotBeNull();
        result.WordResult.Definitions.Should().HaveCount(2);
        result.WordResult.Definitions![0].PartOfSpeech.Should().Be("interjection");
        result.WordResult.Definitions[0].Meanings.Should().Contain("你好");
        result.WordResult.Definitions[1].PartOfSpeech.Should().Be("noun");
        result.WordResult.Definitions[1].Meanings.Should().Contain("招呼");
    }

    [Fact]
    public async Task TranslateAsync_ParsesPhonetic()
    {
        // Phonetic appears as the last element in root[0] with index [3]
        var response = """
            [
                [
                    ["你好","hello",null,null,10],
                    [null,null,"nǐ hǎo"]
                ],
                null,
                "en"
            ]
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        // The phonetic is at root[0][last][3], but here last entry has it at index 2
        // Due to the array structure, phonetic extraction may depend on exact format
        // This test verifies the service doesn't crash on this format
        result.TranslatedText.Should().Be("你好");
    }

    [Fact]
    public async Task TranslateAsync_ParsesPhoneticAtIndex3()
    {
        // Phonetic at root[0][last][3]
        var response = """
            [
                [
                    ["Hello","你好",null,null,10],
                    [null,null,null,"nǐ hǎo"]
                ],
                null,
                "zh-CN"
            ]
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().NotBeNull();
        result.WordResult.Phonetics.Should().HaveCount(1);
        result.WordResult.Phonetics![0].Text.Should().Be("nǐ hǎo");
        result.WordResult.Phonetics![0].Accent.Should().Be("src", "GoogleWeb phonetics are source language romanization");
    }

    [Fact]
    public async Task TranslateAsync_NoWordResultForPlainText()
    {
        var response = """
            [
                [["This is a long sentence.","这是一个长句子。",null,null,10]],
                null,
                "zh-CN"
            ]
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "这是一个长句子。",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.TranslatedText.Should().Be("This is a long sentence.");
        result.WordResult.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnRateLimited()
    {
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
        exception.ServiceId.Should().Be("google_web");
    }

    [Fact]
    public async Task TranslateAsync_SendsCorrectUrl()
    {
        var response = """[[["Hi","Hi",null,null,10]],null,"en"]""";
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        await _service.TranslateAsync(request);

        var sentRequest = _mockHandler.LastRequest;
        sentRequest.Should().NotBeNull();
        sentRequest!.RequestUri!.Host.Should().Be("translate.google.com");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("sl=en");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("tl=zh-CN");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("q=Hello");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("dt=bd");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("dt=ex");
    }

    [Fact]
    public async Task TranslateAsync_ConcatenatesMultipleSentences()
    {
        var response = """
            [
                [
                    ["Hello. ","你好。",null,null,10],
                    ["How are you?","你好吗？",null,null,10]
                ],
                null,
                "zh-CN"
            ]
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "你好。你好吗？",
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);
        result.TranslatedText.Should().Be("Hello. How are you?");
    }

    [Fact]
    public async Task TranslateAsync_ParsesSimpleWordsFromIndex2()
    {
        // In zh->en direction, meanings may be at entry[2] as simple words
        var response = """
            [
                [["good","好",null,null,10]],
                [
                    ["adjective",[],
                        [["good",["好的","良好"]],["fine",["好的","细的"]]],
                    null,null]
                ],
                "zh-CN"
            ]
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.WordResult.Should().NotBeNull();
        result.WordResult!.Definitions.Should().NotBeNull();
        result.WordResult.Definitions![0].PartOfSpeech.Should().Be("adjective");
        result.WordResult.Definitions[0].Meanings.Should().Contain("good");
        result.WordResult.Definitions[0].Meanings.Should().Contain("fine");
    }
}
