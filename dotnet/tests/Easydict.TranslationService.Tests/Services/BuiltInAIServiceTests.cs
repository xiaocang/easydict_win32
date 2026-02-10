using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for BuiltInAIService two-tier routing:
/// - GLM models → direct with embedded key
/// - Groq models → Cloudflare Worker proxy (DeviceId)
/// - User API key → direct to provider
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

    // --- GLM direct mode (primary, embedded key) ---

    [Fact]
    public void GLMModel_UsesDirectEndpoint()
    {
        _service.Configure("glm-4-flash");
        _service.Endpoint.Should().Be("https://open.bigmodel.cn/api/paas/v4/chat/completions");
    }

    [Fact]
    public void GLMModel_UsesEmbeddedKey()
    {
        _service.Configure("glm-4-flash");
        // Embedded key may be empty in test env (no real secret), but should not be user key
        _service.UseDirectConnection.Should().BeFalse();
        _service.UsesWorkerProxy.Should().BeFalse();
    }

    // --- Groq Worker proxy mode (backup) ---

    [Fact]
    public void GroqModel_UsesWorkerEndpoint()
    {
        _service.Configure("llama-3.3-70b-versatile");
        _service.Endpoint.Should().Contain("workers.dev");
    }

    [Fact]
    public void GroqModel_ApiKey_IsEmpty()
    {
        _service.Configure("llama-3.3-70b-versatile");
        _service.ApiKey.Should().BeEmpty();
    }

    [Fact]
    public void GroqModel_UsesWorkerProxy_IsTrue()
    {
        _service.Configure("llama-3.3-70b-versatile");
        _service.UsesWorkerProxy.Should().BeTrue();
    }

    [Fact]
    public void GroqModel_IsConfigured_IsTrue()
    {
        _service.Configure("llama-3.3-70b-versatile");
        _service.IsConfigured.Should().BeTrue();
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
    public void UserKey_UsesWorkerProxy_IsFalse()
    {
        _service.Configure("llama-3.3-70b-versatile", "user-key");
        _service.UsesWorkerProxy.Should().BeFalse();
    }

    [Fact]
    public void UserKey_UseDirectConnection_IsTrue()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.UseDirectConnection.Should().BeTrue();
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
    public void SwitchingModel_ChangesRoutingMode()
    {
        // GLM → direct
        _service.Configure("glm-4-flash");
        _service.UsesWorkerProxy.Should().BeFalse();
        _service.Endpoint.Should().Contain("bigmodel.cn");

        // Groq → Worker
        _service.Configure("llama-3.3-70b-versatile");
        _service.UsesWorkerProxy.Should().BeTrue();
        _service.Endpoint.Should().Contain("workers.dev");
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
