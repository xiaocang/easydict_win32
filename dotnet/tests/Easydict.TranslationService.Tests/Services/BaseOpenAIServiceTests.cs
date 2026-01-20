using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for BaseOpenAIService and OpenAI-compatible services.
/// Uses a concrete test implementation since BaseOpenAIService is abstract.
/// </summary>
public class BaseOpenAIServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly TestOpenAIService _service;

    public BaseOpenAIServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new TestOpenAIService(_httpClient);
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenApiKeyNotSet()
    {
        _service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ReturnsTrue_WhenApiKeySet()
    {
        _service.Configure("sk-test-key");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsExpectedLanguages()
    {
        var languages = _service.SupportedLanguages;

        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
        languages.Should().Contain(Language.French);
        languages.Should().Contain(Language.German);
    }

    [Fact]
    public async Task TranslateStreamAsync_ThrowsWhenNotConfigured()
    {
        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var action = async () =>
        {
            await foreach (var _ in _service.TranslateStreamAsync(request))
            {
                // consume stream
            }
        };

        await action.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task TranslateStreamAsync_YieldsChunks_OnSuccess()
    {
        // Arrange
        _service.Configure("sk-test-key");
        var sseEvents = new[]
        {
            """{"choices":[{"delta":{"content":"Hello"}}]}""",
            """{"choices":[{"delta":{"content":" World"}}]}"""
        };
        _mockHandler.EnqueueStreamingResponse(sseEvents);

        var request = new TranslationRequest
        {
            Text = "Bonjour le monde",
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

        // Act & Assert
        var action = async () =>
        {
            await foreach (var _ in _service.TranslateStreamAsync(request))
            {
                // consume stream
            }
        };

        await action.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task TranslateStreamAsync_ThrowsOnRateLimited()
    {
        // Arrange
        _service.Configure("sk-test-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests, "Rate limit exceeded");

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var action = async () =>
        {
            await foreach (var _ in _service.TranslateStreamAsync(request))
            {
                // consume stream
            }
        };

        await action.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.RateLimited);
    }

    [Fact]
    public async Task TranslateStreamAsync_ThrowsOnServerError()
    {
        // Arrange
        _service.Configure("sk-test-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.InternalServerError, "Server error");

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act & Assert
        var action = async () =>
        {
            await foreach (var _ in _service.TranslateStreamAsync(request))
            {
                // consume stream
            }
        };

        await action.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.ServiceUnavailable);
    }

    [Fact]
    public async Task TranslateStreamAsync_SendsCorrectHeaders()
    {
        // Arrange
        _service.Configure("sk-test-api-key");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request))
        {
            // consume stream
        }

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest.Should().NotBeNull();
        sentRequest!.Headers.Authorization.Should().NotBeNull();
        sentRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sentRequest.Headers.Authorization.Parameter.Should().Be("sk-test-api-key");
    }

    [Fact]
    public async Task TranslateStreamAsync_SendsCorrectRequestBody()
    {
        // Arrange
        _service.Configure("sk-test-key");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request))
        {
            // consume stream
        }

        // Assert
        var content = _mockHandler.LastRequestBody;
        content.Should().NotBeNull();
        content.Should().Contain("\"model\":");
        content.Should().Contain("\"messages\":");
        content.Should().Contain("\"stream\":true");
    }

    [Fact]
    public async Task TranslateAsync_CombinesStreamChunks()
    {
        // Arrange
        _service.Configure("sk-test-key");
        var sseEvents = new[]
        {
            """{"choices":[{"delta":{"content":"Hello"}}]}""",
            """{"choices":[{"delta":{"content":" "}}]}""",
            """{"choices":[{"delta":{"content":"World"}}]}"""
        };
        _mockHandler.EnqueueStreamingResponse(sseEvents);

        var request = new TranslationRequest
        {
            Text = "Bonjour le monde",
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello World");
    }

    [Fact]
    public async Task TranslateAsync_RemovesSurroundingQuotes()
    {
        // Arrange
        _service.Configure("sk-test-key");
        var sseEvents = new[]
        {
            """{"choices":[{"delta":{"content":"\"Hello World\""}}]}"""
        };
        _mockHandler.EnqueueStreamingResponse(sseEvents);

        var request = new TranslationRequest
        {
            Text = "Bonjour le monde",
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello World");
    }

    /// <summary>
    /// Concrete implementation for testing BaseOpenAIService.
    /// </summary>
    private class TestOpenAIService : BaseOpenAIService
    {
        private string _apiKey = "";

        public TestOpenAIService(HttpClient httpClient) : base(httpClient) { }

        public override string ServiceId => "test-openai";
        public override string DisplayName => "Test OpenAI Service";
        public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
        public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

        public override string Endpoint => "https://api.test.com/v1/chat/completions";
        public override string ApiKey => _apiKey;
        public override string Model => "test-model";

        public void Configure(string apiKey)
        {
            _apiKey = apiKey;
        }
    }
}
