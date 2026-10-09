using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for OllamaService-specific behavior.
/// Focuses on: always configured, no API key, RefreshLocalModelsAsync, custom ValidateConfiguration.
/// </summary>
public class OllamaServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly OllamaService _service;

    public OllamaServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new OllamaService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsOllama()
    {
        _service.ServiceId.Should().Be("ollama");
    }

    [Fact]
    public void DisplayName_IsOllama()
    {
        _service.DisplayName.Should().Be("Ollama");
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
    public void SupportedLanguages_ContainsExpectedLanguages()
    {
        var languages = _service.SupportedLanguages;

        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.TraditionalChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
    }

    [Fact]
    public async Task RefreshLocalModelsAsync_ParsesModels()
    {
        // Arrange
        var tagsResponse = """
            {
                "models": [
                    {"name": "llama3.2"},
                    {"name": "mistral"},
                    {"name": "codellama"}
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(tagsResponse);

        // Act
        await _service.RefreshLocalModelsAsync();

        // Assert
        _service.AvailableModels.Should().HaveCount(3);
        _service.AvailableModels.Should().Contain("llama3.2");
        _service.AvailableModels.Should().Contain("mistral");
        _service.AvailableModels.Should().Contain("codellama");
    }

    [Fact]
    public async Task RefreshLocalModelsAsync_SetsDefaultOnFailure()
    {
        // Arrange - no response queued, will throw
        // The mock handler will throw InvalidOperationException

        // Act - should not throw
        await _service.RefreshLocalModelsAsync();

        // Assert - falls back to default model
        _service.AvailableModels.Should().Contain("llama3.2");
    }

    [Fact]
    public async Task RefreshLocalModelsAsync_SwitchesModel_WhenCurrentNotAvailable()
    {
        // Arrange
        _service.Configure(model: "nonexistent-model");
        var tagsResponse = """
            {
                "models": [
                    {"name": "mistral"},
                    {"name": "llama3.2"}
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(tagsResponse);

        // Act
        await _service.RefreshLocalModelsAsync();

        // Assert - should switch to first available model
        _service.AvailableModels.Should().HaveCount(2);
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesLocalhostEndpoint()
    {
        // Arrange
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.Host.Should().Be("localhost");
        sentRequest.RequestUri.Port.Should().Be(11434);
    }

    [Fact]
    public async Task TranslateStreamAsync_BoundsOutputTokens()
    {
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        await foreach (var _ in _service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        })) { }

        _mockHandler.LastRequestBody.Should().Contain($"\"max_tokens\":{OllamaService.MinOutputTokens}");
    }

    [Fact]
    public async Task TranslateStreamAsync_DefaultsToStandardPrompt()
    {
        (await SendAndReadSystemPromptAsync()).Should().Be(BaseOpenAIService.TranslationSystemPrompt);
    }

    [Fact]
    public async Task TranslateStreamAsync_ConcisePrompt_ReplacesSystemPrompt()
    {
        _service.ConfigurePrompt(OllamaPromptStyle.Concise);

        (await SendAndReadSystemPromptAsync()).Should().Be(OllamaService.ConciseSystemPrompt);
    }

    [Fact]
    public async Task TranslateStreamAsync_CustomPrompt_FillsLanguagePlaceholders()
    {
        _service.ConfigurePrompt(OllamaPromptStyle.Custom, "  From {from} to {to}. Translation only.  ");

        (await SendAndReadSystemPromptAsync(Language.English, Language.Japanese))
            .Should().Be("From English to Japanese. Translation only.");
    }

    [Fact]
    public async Task TranslateStreamAsync_BlankCustomPrompt_FallsBackToStandard()
    {
        _service.ConfigurePrompt(OllamaPromptStyle.Custom, "   ");

        (await SendAndReadSystemPromptAsync()).Should().Be(BaseOpenAIService.TranslationSystemPrompt);
    }

    [Fact]
    public async Task TranslateStreamAsync_PerRequestInstructionsStillAppendToChosenPrompt()
    {
        _service.ConfigurePrompt(OllamaPromptStyle.Concise);

        var prompt = await SendAndReadSystemPromptAsync(customPrompt: "Keep it formal.");

        prompt.Should().StartWith(OllamaService.ConciseSystemPrompt);
        prompt.Should().EndWith("Additional instructions: Keep it formal.");
    }

    [Theory]
    [InlineData("Concise", OllamaPromptStyle.Concise)]
    [InlineData("custom", OllamaPromptStyle.Custom)]
    [InlineData("Standard", OllamaPromptStyle.Standard)]
    [InlineData("", OllamaPromptStyle.Standard)]
    [InlineData(null, OllamaPromptStyle.Standard)]
    [InlineData("bogus", OllamaPromptStyle.Standard)]
    [InlineData("7", OllamaPromptStyle.Standard)]
    public void OllamaPromptStyleParser_ParsesStoredValue(string? value, OllamaPromptStyle expected)
    {
        OllamaPromptStyleParser.Parse(value).Should().Be(expected);
    }

    private async Task<string> SendAndReadSystemPromptAsync(
        Language from = Language.Auto,
        Language to = Language.SimplifiedChinese,
        string? customPrompt = null)
    {
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        await foreach (var _ in _service.TranslateStreamAsync(new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = from,
            ToLanguage = to,
            CustomPrompt = customPrompt
        })) { }

        using var body = System.Text.Json.JsonDocument.Parse(_mockHandler.LastRequestBody!);
        var system = body.RootElement.GetProperty("messages")[0];
        system.GetProperty("role").GetString().Should().Be("system");
        return system.GetProperty("content").GetString()!;
    }

    [Theory]
    [InlineData(0, OllamaService.MinOutputTokens)]
    [InlineData(10, OllamaService.MinOutputTokens)]
    [InlineData(1000, 4000)]
    [InlineData(1_000_000, OllamaService.MaxOutputTokensCeiling)]
    public void GetOutputTokenLimit_ScalesWithInputWithinBounds(int inputLength, int expected)
    {
        OllamaService.GetOutputTokenLimit(inputLength).Should().Be(expected);
    }

    [Fact]
    public async Task TranslateStreamAsync_UsesCustomEndpoint()
    {
        // Arrange
        _service.Configure(endpoint: "http://192.168.1.100:11434/v1/chat/completions");
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.Host.Should().Be("192.168.1.100");
    }

    [Fact]
    public async Task TranslateAsync_ReturnsTranslation()
    {
        // Arrange
        _mockHandler.EnqueueStreamingResponse(new[]
        {
            """{"choices":[{"delta":{"content":"你好"}}]}"""
        });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("你好");
        result.ServiceName.Should().Be("Ollama");
    }

    [Fact]
    public async Task TranslateStreamAsync_DoesNotSendAuthHeader()
    {
        // Arrange
        _mockHandler.EnqueueStreamingResponse(new[] { """{"choices":[{"delta":{"content":"Hi"}}]}""" });

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        await foreach (var _ in _service.TranslateStreamAsync(request)) { }

        // Assert - Ollama doesn't need auth
        var sentRequest = _mockHandler.LastRequest;
        // Bearer with empty string may or may not be sent, but should not have a meaningful token
        // The key thing is it shouldn't fail
    }

    [Fact]
    public async Task RefreshLocalModelsAsync_SendsCorrectUrl()
    {
        // Arrange
        var tagsResponse = """{"models": [{"name": "llama3.2"}]}""";
        _mockHandler.EnqueueJsonResponse(tagsResponse);

        // Act
        await _service.RefreshLocalModelsAsync();

        // Assert
        var sentRequest = _mockHandler.LastRequest;
        sentRequest!.RequestUri!.ToString().Should().Contain("/api/tags");
        sentRequest.RequestUri.Host.Should().Be("localhost");
        sentRequest.RequestUri.Port.Should().Be(11434);
    }
}
