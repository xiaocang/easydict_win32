using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for BuiltInAIService-specific behavior.
/// Focuses on: model selection, multi-provider routing, user API key fallback, language subset.
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
    public void AvailableModels_ContainsGLMModels()
    {
        BuiltInAIService.AvailableModels.Should().Contain("glm-4-flash-250414");
        BuiltInAIService.AvailableModels.Should().Contain("glm-4-flash");
    }

    [Fact]
    public void AvailableModels_ContainsGroqModels()
    {
        BuiltInAIService.AvailableModels.Should().Contain("llama-3.3-70b-versatile");
        BuiltInAIService.AvailableModels.Should().Contain("llama-3.1-8b-instant");
    }

    [Fact]
    public void AvailableModels_DoesNotContainDeprecatedModels()
    {
        BuiltInAIService.AvailableModels.Should().NotContain("gemma2-9b-it");
        BuiltInAIService.AvailableModels.Should().NotContain("mixtral-8x7b-32768");
    }

    [Fact]
    public void DefaultModel_IsGLM()
    {
        // Default model should be GLM (primary provider)
        _service.Model.Should().Be("glm-4-flash-250414");
    }

    [Fact]
    public void DefaultProvider_IsGLM()
    {
        _service.CurrentProvider.Should().Be(BuiltInAIService.Provider.GLM);
    }

    [Fact]
    public void Endpoint_UsesGLMEndpoint_WhenGLMModelSelected()
    {
        _service.Configure("glm-4-flash");
        _service.Endpoint.Should().Be("https://open.bigmodel.cn/api/paas/v4/chat/completions");
    }

    [Fact]
    public void Endpoint_UsesGroqEndpoint_WhenGroqModelSelected()
    {
        _service.Configure("llama-3.3-70b-versatile");
        _service.Endpoint.Should().Be("https://api.groq.com/openai/v1/chat/completions");
    }

    [Fact]
    public void CurrentProvider_SwitchesWithModel()
    {
        _service.Configure("glm-4-flash-250414");
        _service.CurrentProvider.Should().Be(BuiltInAIService.Provider.GLM);

        _service.Configure("llama-3.1-8b-instant");
        _service.CurrentProvider.Should().Be(BuiltInAIService.Provider.Groq);

        _service.Configure("glm-4-flash");
        _service.CurrentProvider.Should().Be(BuiltInAIService.Provider.GLM);
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
        _service.Model.Should().Be("llama-3.1-8b-instant");
    }

    [Fact]
    public void Configure_IgnoresInvalidModel()
    {
        var originalModel = _service.Model;
        // Should not throw and not change to invalid model
        _service.Configure("nonexistent-model");
        _service.Model.Should().Be(originalModel);
    }

    [Fact]
    public void Configure_WithUserApiKey_OverridesBuiltIn()
    {
        _service.Configure("glm-4-flash", "user-custom-key");
        _service.ApiKey.Should().Be("user-custom-key");
    }

    [Fact]
    public void Configure_WithNullApiKey_ClearsOverride()
    {
        _service.Configure("glm-4-flash", "user-custom-key");
        _service.ApiKey.Should().Be("user-custom-key");

        _service.Configure("glm-4-flash", null);
        // Should fall back to built-in key (not "user-custom-key")
        _service.ApiKey.Should().NotBe("user-custom-key");
    }

    [Fact]
    public void Configure_WithEmptyApiKey_ClearsOverride()
    {
        _service.Configure("glm-4-flash", "user-custom-key");
        _service.Configure("glm-4-flash", "");
        _service.ApiKey.Should().NotBe("user-custom-key");
    }

    [Fact]
    public void IsStreaming_IsTrue()
    {
        _service.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void ModelProviderMap_CoversAllAvailableModels()
    {
        foreach (var model in BuiltInAIService.AvailableModels)
        {
            BuiltInAIService.ModelProviderMap.Should().ContainKey(model,
                $"model '{model}' in AvailableModels should have a provider mapping");
        }
    }
}
