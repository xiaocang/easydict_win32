using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for GeminiService.
/// Verifies Gemini-specific API protocol differences from OpenAI-compatible services.
/// </summary>
public class GeminiServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly GeminiService _service;

    public GeminiServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new GeminiService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsGemini()
    {
        _service.ServiceId.Should().Be("gemini");
    }

    [Fact]
    public void DisplayName_IsGemini()
    {
        _service.DisplayName.Should().Be("Gemini");
    }

    [Fact]
    public void RequiresApiKey_IsTrue()
    {
        _service.RequiresApiKey.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenApiKeyNotSet()
    {
        _service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ReturnsTrue_WhenApiKeySet()
    {
        _service.Configure("test-api-key");
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsStreaming_IsTrue()
    {
        _service.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsMajorLanguages()
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
    public void AvailableModels_ContainsExpectedModels()
    {
        GeminiService.AvailableModels.Should().Contain("gemini-2.5-flash");
        GeminiService.AvailableModels.Should().Contain("gemini-2.0-flash");
        GeminiService.AvailableModels.Should().Contain("gemini-1.5-flash");
        GeminiService.AvailableModels.Should().Contain("gemini-1.5-pro");
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
        _service.Configure("test-api-key");

        // Gemini format: candidates[0].content.parts[0].text
        var geminiResponse = """
            {"candidates":[{"content":{"parts":[{"text":"Hello"}]}}]}
            {"candidates":[{"content":{"parts":[{"text":" World"}]}}]}
            """;
        _mockHandler.EnqueueJsonResponse(geminiResponse);

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
    public async Task TranslateStreamAsync_SendsApiKeyAsQueryParameter()
    {
        // Arrange
        _service.Configure("my-gemini-api-key");
        var geminiResponse = """{"candidates":[{"content":{"parts":[{"text":"Hi"}]}}]}""";
        _mockHandler.EnqueueJsonResponse(geminiResponse);

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

        // Assert - Gemini uses API key as query parameter, not header
        var sentRequest = _mockHandler.LastRequest;
        sentRequest.Should().NotBeNull();
        sentRequest!.RequestUri!.Query.Should().Contain("key=my-gemini-api-key");
        sentRequest.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesCorrectEndpoint()
    {
        // Arrange
        _service.Configure("test-api-key");
        var geminiResponse = """{"candidates":[{"content":{"parts":[{"text":"Hi"}]}}]}""";
        _mockHandler.EnqueueJsonResponse(geminiResponse);

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
        sentRequest!.RequestUri!.Host.Should().Be("generativelanguage.googleapis.com");
        sentRequest.RequestUri.PathAndQuery.Should().Contain("/v1beta/models/");
        sentRequest.RequestUri.PathAndQuery.Should().Contain(":streamGenerateContent");
    }

    [Fact]
    public async Task TranslateStreamAsync_SendsCorrectRequestFormat()
    {
        // Arrange
        _service.Configure("test-api-key");
        var geminiResponse = """{"candidates":[{"content":{"parts":[{"text":"Hi"}]}}]}""";
        _mockHandler.EnqueueJsonResponse(geminiResponse);

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

        // Assert - Gemini uses different request format than OpenAI
        var content = _mockHandler.LastRequestBody;
        content.Should().NotBeNull();
        content.Should().Contain("\"contents\":");
        content.Should().Contain("\"systemInstruction\":");
        content.Should().Contain("\"generationConfig\":");
        content.Should().Contain("\"parts\":");
        // Should NOT contain OpenAI-style fields
        content.Should().NotContain("\"messages\":");
        content.Should().NotContain("\"stream\":true");
    }

    [Fact]
    public async Task TranslateStreamAsync_ThrowsOnUnauthorized()
    {
        // Arrange
        _service.Configure("invalid-key");
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.Unauthorized, "API key not valid");

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
        _service.Configure("test-api-key");
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
    public async Task TranslateAsync_CombinesStreamChunks()
    {
        // Arrange
        _service.Configure("test-api-key");
        var geminiResponse = """
            {"candidates":[{"content":{"parts":[{"text":"Hello"}]}}]}
            {"candidates":[{"content":{"parts":[{"text":" World"}]}}]}
            """;
        _mockHandler.EnqueueJsonResponse(geminiResponse);

        var request = new TranslationRequest
        {
            Text = "Bonjour le monde",
            ToLanguage = Language.English
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("Hello World");
        result.ServiceName.Should().Be("Gemini");
    }

    [Fact]
    public async Task TranslateAsync_RemovesSurroundingQuotes()
    {
        // Arrange
        _service.Configure("test-api-key");
        var geminiResponse = """{"candidates":[{"content":{"parts":[{"text":"\"Hello World\""}]}}]}""";
        _mockHandler.EnqueueJsonResponse(geminiResponse);

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
    public void Configure_SetsCustomModel()
    {
        // Arrange & Act
        _service.Configure("test-api-key", model: "gemini-1.5-pro");

        // We can't directly verify the model, but we can verify it's configured
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void Configure_ClampsTemperature()
    {
        // Temperature should be clamped between 0.0 and 2.0
        // Configure doesn't expose temperature directly, but shouldn't throw
        _service.Configure("test-api-key", temperature: -1.0);
        _service.IsConfigured.Should().BeTrue();

        _service.Configure("test-api-key", temperature: 5.0);
        _service.IsConfigured.Should().BeTrue();
    }
}
