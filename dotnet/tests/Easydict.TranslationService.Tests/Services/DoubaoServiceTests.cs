using System.Net;
using System.Text;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Mock-based tests for DoubaoService (豆包).
/// Covers SSE parsing, error handling, language codes, and configuration.
/// </summary>
public class DoubaoServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly DoubaoService _service;

    public DoubaoServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new DoubaoService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsDoubao()
    {
        _service.ServiceId.Should().Be("doubao");
    }

    [Fact]
    public void DisplayName_IsDoubao()
    {
        _service.DisplayName.Should().Be("Doubao");
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
    public void IsStreaming_IsTrue()
    {
        _service.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void AvailableModels_ContainsExpectedModels()
    {
        DoubaoService.AvailableModels.Should().Contain("doubao-seed-translation-250915");
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
        languages.Should().Contain(Language.Hindi);
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

    [Fact]
    public async Task TranslateStreamAsync_YieldsChunks_OnSuccess()
    {
        // Arrange
        _service.Configure("test-key");

        // Doubao SSE format: event lines followed by data lines
        var sseContent = new StringBuilder();
        sseContent.AppendLine("event: response.output_text.delta");
        sseContent.AppendLine("""data: {"delta":"Hello"}""");
        sseContent.AppendLine();
        sseContent.AppendLine("event: response.output_text.delta");
        sseContent.AppendLine("""data: {"delta":" World"}""");
        sseContent.AppendLine();
        sseContent.AppendLine("data: [DONE]");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent.ToString(), Encoding.UTF8, "text/event-stream")
        };
        _mockHandler.EnqueueResponse(response);

        var request = new TranslationRequest
        {
            Text = "你好世界",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in _service.TranslateStreamAsync(request))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().Contain("Hello");
        chunks.Should().Contain(" World");
    }

    [Fact]
    public async Task TranslateStreamAsync_IgnoresNonDeltaEvents()
    {
        // Arrange
        _service.Configure("test-key");

        var sseContent = new StringBuilder();
        // Non-delta event should be ignored
        sseContent.AppendLine("event: response.created");
        sseContent.AppendLine("""data: {"id":"resp_123"}""");
        sseContent.AppendLine();
        // Delta event should be captured
        sseContent.AppendLine("event: response.output_text.delta");
        sseContent.AppendLine("""data: {"delta":"Hello"}""");
        sseContent.AppendLine();
        sseContent.AppendLine("event: response.completed");
        sseContent.AppendLine("""data: {"status":"completed"}""");
        sseContent.AppendLine();
        sseContent.AppendLine("data: [DONE]");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent.ToString(), Encoding.UTF8, "text/event-stream")
        };
        _mockHandler.EnqueueResponse(response);

        var request = new TranslationRequest
        {
            Text = "测试",
            ToLanguage = Language.English
        };

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in _service.TranslateStreamAsync(request))
        {
            chunks.Add(chunk);
        }

        // Assert - only the delta text should be captured
        chunks.Should().HaveCount(1);
        chunks[0].Should().Be("Hello");
    }

    [Fact]
    public async Task TranslateAsync_CombinesChunks()
    {
        // Arrange
        _service.Configure("test-key");

        var sseContent = new StringBuilder();
        sseContent.AppendLine("event: response.output_text.delta");
        sseContent.AppendLine("""data: {"delta":"Hello"}""");
        sseContent.AppendLine();
        sseContent.AppendLine("event: response.output_text.delta");
        sseContent.AppendLine("""data: {"delta":" World"}""");
        sseContent.AppendLine();
        sseContent.AppendLine("data: [DONE]");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent.ToString(), Encoding.UTF8, "text/event-stream")
        };
        _mockHandler.EnqueueResponse(response);

        var request = new TranslationRequest
        {
            Text = "你好世界",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello World");
        result.ServiceName.Should().Be("Doubao");
    }

    [Fact]
    public async Task TranslateAsync_RemovesSurroundingQuotes()
    {
        // Arrange
        _service.Configure("test-key");

        var sseContent = new StringBuilder();
        sseContent.AppendLine("event: response.output_text.delta");
        sseContent.AppendLine("""data: {"delta":"\"Hello World\""}""");
        sseContent.AppendLine();
        sseContent.AppendLine("data: [DONE]");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent.ToString(), Encoding.UTF8, "text/event-stream")
        };
        _mockHandler.EnqueueResponse(response);

        var request = new TranslationRequest
        {
            Text = "你好世界",
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello World");
    }

    [Fact]
    public async Task TranslateStreamAsync_ThrowsOnUnauthorized()
    {
        // Arrange
        _service.Configure("invalid-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.Unauthorized, "Invalid API key");

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

    [Fact]
    public async Task TranslateStreamAsync_ThrowsOnRateLimited()
    {
        // Arrange
        _service.Configure("test-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);

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
            .Where(e => e.ErrorCode == TranslationErrorCode.RateLimited);
    }

    [Fact]
    public async Task TranslateStreamAsync_ThrowsOnServerError()
    {
        // Arrange
        _service.Configure("test-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.InternalServerError);

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
            .Where(e => e.ErrorCode == TranslationErrorCode.ServiceUnavailable);
    }

    [Fact]
    public async Task TranslateStreamAsync_SendsCorrectRequestFormat()
    {
        // Arrange
        _service.Configure("test-key");

        var sseContent = new StringBuilder();
        sseContent.AppendLine("event: response.output_text.delta");
        sseContent.AppendLine("""data: {"delta":"Hi"}""");
        sseContent.AppendLine();
        sseContent.AppendLine("data: [DONE]");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent.ToString(), Encoding.UTF8, "text/event-stream")
        };
        _mockHandler.EnqueueResponse(response);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var body = _mockHandler.LastRequestBody;
        body.Should().Contain("\"model\":");
        body.Should().Contain("\"stream\":true");
        body.Should().Contain("\"input\":");
        body.Should().Contain("\"input_text\"");
        body.Should().Contain("\"translation_options\"");
        body.Should().Contain("\"source_language\":\"en\"");
        body.Should().Contain("\"target_language\":\"zh\"");
    }

    [Fact]
    public async Task TranslateStreamAsync_SendsBearerToken()
    {
        // Arrange
        _service.Configure("my-doubao-key");

        var sseContent = new StringBuilder();
        sseContent.AppendLine("data: [DONE]");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent.ToString(), Encoding.UTF8, "text/event-stream")
        };
        _mockHandler.EnqueueResponse(response);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.Headers.Should().ContainSingle(h => h.Key == "Authorization");
        var auth = sentRequest.Headers.GetValues("Authorization").First();
        auth.Should().Be("Bearer my-doubao-key");
    }

    [Fact]
    public async Task TranslateAsync_TrimsWhitespace_WhenNoSurroundingQuotes()
    {
        // Arrange
        _service.Configure("test-key");

        var sseContent = new StringBuilder();
        sseContent.AppendLine("event: response.output_text.delta");
        sseContent.AppendLine("""data: {"delta":"  Hello World \n"}""");
        sseContent.AppendLine();
        sseContent.AppendLine("data: [DONE]");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent.ToString(), Encoding.UTF8, "text/event-stream")
        };
        _mockHandler.EnqueueResponse(response);

        var request = new TranslationRequest
        {
            Text = "你好世界",
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert - whitespace should be trimmed even without surrounding quotes
        result.TranslatedText.Should().Be("Hello World");
    }

    [Theory]
    [InlineData(Language.SimplifiedChinese, "zh")]
    [InlineData(Language.TraditionalChinese, "zh-Hant")]
    [InlineData(Language.English, "en")]
    [InlineData(Language.Japanese, "ja")]
    public async Task TranslateStreamAsync_UsesCorrectLanguageCodes(Language targetLang, string expectedCode)
    {
        // Arrange
        _service.Configure("test-key");

        var sseContent = new StringBuilder();
        sseContent.AppendLine("data: [DONE]");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseContent.ToString(), Encoding.UTF8, "text/event-stream")
        };
        _mockHandler.EnqueueResponse(response);

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = targetLang
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var body = _mockHandler.LastRequestBody;
        body.Should().Contain($"\"target_language\":\"{expectedCode}\"");
    }
}
