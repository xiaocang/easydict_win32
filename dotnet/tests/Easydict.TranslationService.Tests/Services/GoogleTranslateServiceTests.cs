using System.Net;
using System.Text.Json;
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

    [Fact]
    public async Task TranslateAsync_ExtractsSourcePhonetic()
    {
        // Arrange - dj=1 format includes src_translit for source romanization
        var googleResponse = """
            {
                "sentences": [
                    {"trans": "Hello", "orig": "你好", "src_translit": "nǐ hǎo"}
                ],
                "src": "zh-CN"
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().NotBeNull();
        result.WordResult.Phonetics!.Should().Contain(p => p.Text == "nǐ hǎo" && p.Accent == "src");
    }

    [Fact]
    public async Task TranslateAsync_ExtractsTargetPhonetic()
    {
        // Arrange - dj=1 format includes translit for target romanization
        var googleResponse = """
            {
                "sentences": [
                    {"trans": "你好", "orig": "hello", "translit": "nǐ hǎo"}
                ],
                "src": "en"
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().NotBeNull();
        result.WordResult.Phonetics!.Should().Contain(p => p.Text == "nǐ hǎo" && p.Accent == "dest");
    }

    [Fact]
    public async Task TranslateAsync_ExtractsBothPhonetics()
    {
        // Arrange - response with both src_translit and translit
        var googleResponse = """
            {
                "sentences": [
                    {"trans": "你好", "orig": "hello", "src_translit": "hello", "translit": "nǐ hǎo"}
                ],
                "src": "en"
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().HaveCount(2);
        result.WordResult.Phonetics![0].Accent.Should().Be("src");
        result.WordResult.Phonetics[0].Text.Should().Be("hello");
        result.WordResult.Phonetics[1].Accent.Should().Be("dest");
        result.WordResult.Phonetics[1].Text.Should().Be("nǐ hǎo");
    }

    [Fact]
    public async Task TranslateAsync_NoPhonetics_WhenNotPresent()
    {
        // Arrange - response without transliteration
        var googleResponse = """
            {
                "sentences": [{"trans": "Hello", "orig": "Bonjour"}],
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
        result.WordResult.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_ConcatenatesMultiSentencePhonetics()
    {
        // Arrange - multiple sentences each with src_translit
        var googleResponse = """
            {
                "sentences": [
                    {"trans": "Hello. ", "orig": "你好。", "src_translit": "nǐ hǎo. "},
                    {"trans": "Thank you.", "orig": "谢谢。", "src_translit": "xièxiè."}
                ],
                "src": "zh-CN"
            }
            """;
        _mockHandler.EnqueueJsonResponse(googleResponse);

        var request = new TranslationRequest
        {
            Text = "你好。谢谢。",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().NotBeNull();
        result.WordResult.Phonetics!.Should().Contain(p => p.Text == "nǐ hǎo. xièxiè." && p.Accent == "src");
    }

    [Fact]
    public async Task TranslateAsync_SendsBrowserUserAgent()
    {
        // Google throttles bare no-UA clients with 429 TooManyRequests
        _mockHandler.EnqueueJsonResponse("""{"sentences": [{"trans": "Hi"}], "src": "en"}""");

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        await _service.TranslateAsync(request);

        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.Headers.UserAgent.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TranslateAsync_OnGtx429_FallsBackToWebEndpoint()
    {
        // Arrange: gtx GET returns 429, then the web-endpoint POST succeeds
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("""[["Hola"]]""");

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.Spanish
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hola");
        result.OriginalText.Should().Be("Hello");

        var requests = _mockHandler.Requests;
        requests.Should().HaveCount(2);
        requests[1].Method.Should().Be(HttpMethod.Post);
        requests[1].RequestUri!.Host.Should().Be("translate-pa.googleapis.com");
        requests[1].Headers.Contains("X-Goog-Api-Key").Should().BeTrue();
        _mockHandler.LastRequestBody.Should().Contain("wt_lib").And.Contain("Hello");
    }

    [Fact]
    public async Task TranslateAsync_OnGtxAndWebEndpoint429_ThrowsRateLimited()
    {
        // Arrange: both the gtx GET and the web-endpoint POST return 429
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
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
    public async Task TranslateAsync_OnFallbackMalformedJson_ThrowsRateLimited()
    {
        // Arrange: gtx 429, then the web endpoint returns a non-JSON body
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("not json");

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert: fallback failure degrades to the original gtx error
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => _service.TranslateAsync(request));

        exception.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
    }

    [Fact]
    public async Task TranslateAsync_FallbackDecodesHtmlEntities()
    {
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("""[["Hola &amp; mundo"]]""");

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.Spanish
        };

        var result = await _service.TranslateAsync(request);

        result.TranslatedText.Should().Be("Hola & mundo");
    }

    [Fact]
    public async Task TranslateAsync_FallbackConcatenatesChunks()
    {
        // The web endpoint may return one string per chunk for long inputs
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("""[["Hola ", "mundo"]]""");

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.Spanish
        };

        var result = await _service.TranslateAsync(request);

        result.TranslatedText.Should().Be("Hola mundo");
    }

    [Fact]
    public async Task TranslateAsync_FallbackResult_ReportsDetectedLanguage()
    {
        // The web endpoint reports the detected source language at root[1][0]
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("""[["Hola"],["es"]]""");

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.Auto,
            ToLanguage = Language.Spanish
        };

        var result = await _service.TranslateAsync(request);

        result.DetectedLanguage.Should().Be(Language.Spanish);
        result.TranslatedText.Should().Be("Hola");
    }

    [Fact]
    public async Task TranslateAsync_FallbackEscapesHtmlAndPreservesLineBreaks()
    {
        // translateHtml parses its input as HTML, so markup characters must be escaped
        // and line breaks sent as <br> (raw newlines are collapsed by the endpoint)
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("""[["si a &lt; b &amp;&amp; c &gt; d<br>AT&amp;T gana"]]""");

        var request = new TranslationRequest
        {
            Text = "if a < b && c > d\nAT&T wins",
            FromLanguage = Language.English,
            ToLanguage = Language.Spanish
        };

        var result = await _service.TranslateAsync(request);

        using var body = JsonDocument.Parse(_mockHandler.LastRequestBody!);
        body.RootElement[0][0][0].GetString()
            .Should().Be("if a &lt; b &amp;&amp; c &gt; d<br>AT&amp;T wins");
        result.TranslatedText.Should().Be("si a < b && c > d\nAT&T gana");
    }

    [Theory]
    [InlineData("a < b", "a &lt; b")]
    [InlineData("AT&T", "AT&amp;T")]
    [InlineData("<br>", "&lt;br&gt;")]
    [InlineData("line1\nline2", "line1<br>line2")]
    [InlineData("line1\r\nline2\rline3", "line1<br>line2<br>line3")]
    public void EncodeForTranslateHtml_EscapesMarkupAndLineBreaks(string input, string expected)
    {
        GoogleTranslateService.EncodeForTranslateHtml(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("a &lt; b", "a < b")]
    [InlineData("one<br>two<br/>three<BR />four", "one\ntwo\nthree\nfour")]
    [InlineData("&lt;br&gt;", "<br>")]
    [InlineData("it&#39;s &quot;ok&quot;", "it's \"ok\"")]
    public void DecodeFromTranslateHtml_RestoresTextAndLineBreaks(string input, string expected)
    {
        GoogleTranslateService.DecodeFromTranslateHtml(input).Should().Be(expected);
    }

    [Fact]
    public async Task TranslateAsync_AfterGtx429_SkipsGtxDuringCooldown()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var service = new GoogleTranslateService(_httpClient, time);

        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("""[["Hola"]]""");
        _mockHandler.EnqueueJsonResponse("""[["Adiós"]]""");

        await service.TranslateAsync(CreateEnToEsRequest("Hello"));
        time.Advance(TimeSpan.FromSeconds(30));
        var second = await service.TranslateAsync(CreateEnToEsRequest("Goodbye"));

        // The second query goes straight to the web endpoint: one request, not two
        second.TranslatedText.Should().Be("Adiós");
        var requests = _mockHandler.Requests;
        requests.Should().HaveCount(3);
        requests[2].Method.Should().Be(HttpMethod.Post);
        requests[2].RequestUri!.Host.Should().Be("translate-pa.googleapis.com");
    }

    [Fact]
    public async Task TranslateAsync_AfterCooldownExpires_UsesGtxAgain()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var service = new GoogleTranslateService(_httpClient, time);

        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("""[["Hola"]]""");
        _mockHandler.EnqueueJsonResponse("""{"sentences": [{"trans": "Adiós"}], "src": "en"}""");

        await service.TranslateAsync(CreateEnToEsRequest("Hello"));
        time.Advance(GoogleTranslateService.DefaultGtxCooldown + TimeSpan.FromSeconds(1));
        var second = await service.TranslateAsync(CreateEnToEsRequest("Goodbye"));

        second.TranslatedText.Should().Be("Adiós");
        var requests = _mockHandler.Requests;
        requests.Should().HaveCount(3);
        requests[2].Method.Should().Be(HttpMethod.Get);
        requests[2].RequestUri!.Host.Should().Be("translate.googleapis.com");
    }

    [Fact]
    public async Task TranslateAsync_HonoursRetryAfterForCooldown()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var service = new GoogleTranslateService(_httpClient, time);

        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        _mockHandler.EnqueueResponse(throttled);
        _mockHandler.EnqueueJsonResponse("""[["Hola"]]""");
        _mockHandler.EnqueueJsonResponse("""[["Hola"]]""");
        _mockHandler.EnqueueJsonResponse("""{"sentences": [{"trans": "Hola"}], "src": "en"}""");

        await service.TranslateAsync(CreateEnToEsRequest("Hello"));

        // Past the 60s default but inside Retry-After: still skipping gtx
        time.Advance(TimeSpan.FromSeconds(90));
        await service.TranslateAsync(CreateEnToEsRequest("Hello"));
        _mockHandler.LastRequest!.Method.Should().Be(HttpMethod.Post);

        time.Advance(TimeSpan.FromSeconds(31));
        await service.TranslateAsync(CreateEnToEsRequest("Hello"));
        _mockHandler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        _mockHandler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task TranslateAsync_DuringCooldownAndFallbackFails_ThrowsRateLimitedWithoutGtx()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var service = new GoogleTranslateService(_httpClient, time);

        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);

        await Assert.ThrowsAsync<TranslationException>(
            () => service.TranslateAsync(CreateEnToEsRequest("Hello")));
        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => service.TranslateAsync(CreateEnToEsRequest("Hello")));

        exception.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
        var requests = _mockHandler.Requests;
        requests.Should().HaveCount(3);
        requests[2].Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task DetectLanguageAsync_OnGtx429_UsesWebEndpointDetection()
    {
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueJsonResponse("""[["Hello"],["fr"]]""");

        var detected = await _service.DetectLanguageAsync("Bonjour");

        detected.Should().Be(Language.French);
        _mockHandler.LastRequest!.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task DetectLanguageAsync_OnGtxAndWebEndpoint429_ThrowsTooManyRequests()
    {
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => _service.DetectLanguageAsync("Bonjour"));

        exception.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static TranslationRequest CreateEnToEsRequest(string text) => new()
    {
        Text = text,
        FromLanguage = Language.English,
        ToLanguage = Language.Spanish
    };

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
