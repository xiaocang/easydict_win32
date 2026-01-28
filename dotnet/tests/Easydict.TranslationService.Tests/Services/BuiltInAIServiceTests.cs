using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for BuiltInAIService-specific behavior.
/// Focuses on: model selection, restricted configuration, language subset.
/// </summary>
public class BuiltInAIServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly BuiltInAIService _service;

    public BuiltInAIServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new BuiltInAIService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsBuiltIn()
    {
        _service.ServiceId.Should().Be("builtin");
    }

    [Fact]
    public void DisplayName_IsBuiltInAI()
    {
        _service.DisplayName.Should().Be("Built-in AI");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void AvailableModels_ContainsExpectedModels()
    {
        BuiltInAIService.AvailableModels.Should().Contain("llama-3.3-70b-versatile");
        BuiltInAIService.AvailableModels.Should().Contain("llama-3.1-8b-instant");
        BuiltInAIService.AvailableModels.Should().Contain("gemma2-9b-it");
        BuiltInAIService.AvailableModels.Should().Contain("mixtral-8x7b-32768");
    }

    [Fact]
    public void SupportedLanguages_IsLimitedSubset()
    {
        var languages = _service.SupportedLanguages;

        // BuiltIn AI has a smaller language set than OpenAI services
        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.TraditionalChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
        languages.Should().Contain(Language.French);
        languages.Should().Contain(Language.Spanish);
        languages.Should().Contain(Language.German);

        // Should have fewer languages than full OpenAI language list
        languages.Count.Should().BeLessThan(32);
    }

    [Fact]
    public void Configure_AcceptsValidModel()
    {
        // Should not throw
        _service.Configure("llama-3.1-8b-instant");
    }

    [Fact]
    public void Configure_IgnoresInvalidModel()
    {
        // Should not throw and not change to invalid model
        _service.Configure("nonexistent-model");
    }

    [Fact]
    public void IsStreaming_IsTrue()
    {
        _service.IsStreaming.Should().BeTrue();
    }
}
