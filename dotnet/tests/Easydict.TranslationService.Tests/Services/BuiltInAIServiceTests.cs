using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for BuiltInAIService routing:
/// - Built-in mode → proxy endpoint with embedded key (all models)
/// - User API key → direct to provider (GLM or Groq endpoint)
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
    public void DefaultModel_IsGLM()
    {
        _service.Model.Should().Be("glm-4-flash-250414");
    }

    [Fact]
    public void DefaultProvider_IsGLM()
    {
        _service.CurrentProvider.Should().Be(BuiltInAIService.Provider.GLM);
    }

    // --- Built-in proxy mode (default, no user API key) ---

    [Fact]
    public void BuiltInMode_UsesProxyEndpoint()
    {
        // All models route through the same proxy in built-in mode
        // Endpoint comes from embedded config (may be empty in test env)
        _service.UseDirectConnection.Should().BeFalse();
    }

    [Fact]
    public void BuiltInMode_SameEndpointForAllModels()
    {
        _service.Configure("glm-4-flash");
        var glmEndpoint = _service.Endpoint;

        _service.Configure("llama-3.3-70b-versatile");
        var groqEndpoint = _service.Endpoint;

        // Both models use the same proxy endpoint in built-in mode
        glmEndpoint.Should().Be(groqEndpoint);
    }

    // --- User API key mode (direct to provider) ---

    [Fact]
    public void UserKey_GLM_UsesDirectEndpoint()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.Endpoint.Should().Be("https://open.bigmodel.cn/api/paas/v4/chat/completions");
        _service.ApiKey.Should().Be("user-key");
    }

    [Fact]
    public void UserKey_Groq_UsesDirectGroqEndpoint()
    {
        _service.Configure("llama-3.3-70b-versatile", "user-key");
        _service.Endpoint.Should().Be("https://api.groq.com/openai/v1/chat/completions");
        _service.ApiKey.Should().Be("user-key");
    }

    [Fact]
    public void UserKey_UseDirectConnection_IsTrue()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.UseDirectConnection.Should().BeTrue();
    }

    [Fact]
    public void UserKey_IsConfigured_IsTrue()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.IsConfigured.Should().BeTrue();
    }

    // --- Switching modes ---

    [Fact]
    public void ClearingApiKey_SwitchesBackToBuiltIn()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.UseDirectConnection.Should().BeTrue();

        _service.Configure("glm-4-flash", null);
        _service.UseDirectConnection.Should().BeFalse();
    }

    [Fact]
    public void EmptyApiKey_SwitchesBackToBuiltIn()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.Configure("glm-4-flash", "");
        _service.UseDirectConnection.Should().BeFalse();
    }

    // --- Model selection ---

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
    public void Configure_AcceptsValidModel()
    {
        _service.Configure("llama-3.1-8b-instant");
        _service.Model.Should().Be("llama-3.1-8b-instant");
    }

    [Fact]
    public void Configure_IgnoresInvalidModel()
    {
        var originalModel = _service.Model;
        _service.Configure("nonexistent-model");
        _service.Model.Should().Be(originalModel);
    }

    // --- Model/provider coverage ---

    [Fact]
    public void AvailableModels_ContainsExpectedModels()
    {
        BuiltInAIService.AvailableModels.Should().Contain("glm-4-flash-250414");
        BuiltInAIService.AvailableModels.Should().Contain("glm-4-flash");
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
    public void ModelProviderMap_CoversAllAvailableModels()
    {
        foreach (var model in BuiltInAIService.AvailableModels)
        {
            BuiltInAIService.ModelProviderMap.Should().ContainKey(model,
                $"model '{model}' in AvailableModels should have a provider mapping");
        }
    }

    // --- Other ---

    [Fact]
    public void SupportedLanguages_IsLimitedSubset()
    {
        var languages = _service.SupportedLanguages;
        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.English);
        languages.Count.Should().BeLessThan(32);
    }

    [Fact]
    public void IsStreaming_IsTrue()
    {
        _service.IsStreaming.Should().BeTrue();
    }
}
