using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for BuiltInAIService-specific behavior.
/// Focuses on: Worker proxy routing, direct connection fallback, device fingerprint, model selection.
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
        _service.Model.Should().Be("glm-4-flash-250414");
    }

    [Fact]
    public void DefaultProvider_IsGLM()
    {
        _service.CurrentProvider.Should().Be(BuiltInAIService.Provider.GLM);
    }

    // --- Worker proxy mode (default, no user API key) ---

    [Fact]
    public void Default_UsesWorkerEndpoint()
    {
        // No user API key → routes through Cloudflare Worker
        _service.Endpoint.Should().Contain("workers.dev");
    }

    [Fact]
    public void Default_ApiKey_IsEmpty()
    {
        // Worker handles authentication server-side
        _service.ApiKey.Should().BeEmpty();
    }

    [Fact]
    public void Default_UseDirectConnection_IsFalse()
    {
        _service.UseDirectConnection.Should().BeFalse();
    }

    [Fact]
    public void Default_IsConfigured_IsTrue()
    {
        // Worker mode is always configured (endpoint is hardcoded)
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void WorkerMode_EndpointSameRegardlessOfModel()
    {
        // In Worker mode, all models route through the same Worker endpoint
        _service.Configure("glm-4-flash");
        var glmEndpoint = _service.Endpoint;

        _service.Configure("llama-3.3-70b-versatile");
        var groqEndpoint = _service.Endpoint;

        glmEndpoint.Should().Be(groqEndpoint);
        glmEndpoint.Should().Contain("workers.dev");
    }

    // --- Direct connection mode (user provides API key) ---

    [Fact]
    public void DirectMode_UsesGLMEndpoint_WhenGLMModelSelected()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.Endpoint.Should().Be("https://open.bigmodel.cn/api/paas/v4/chat/completions");
    }

    [Fact]
    public void DirectMode_UsesGroqEndpoint_WhenGroqModelSelected()
    {
        _service.Configure("llama-3.3-70b-versatile", "user-key");
        _service.Endpoint.Should().Be("https://api.groq.com/openai/v1/chat/completions");
    }

    [Fact]
    public void DirectMode_UseDirectConnection_IsTrue()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.UseDirectConnection.Should().BeTrue();
    }

    [Fact]
    public void DirectMode_ApiKey_ReturnsUserKey()
    {
        _service.Configure("glm-4-flash", "user-custom-key");
        _service.ApiKey.Should().Be("user-custom-key");
    }

    [Fact]
    public void DirectMode_IsConfigured_IsTrue()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.IsConfigured.Should().BeTrue();
    }

    // --- Switching between modes ---

    [Fact]
    public void ClearingApiKey_SwitchesBackToWorkerMode()
    {
        _service.Configure("glm-4-flash", "user-key");
        _service.UseDirectConnection.Should().BeTrue();

        _service.Configure("glm-4-flash", null);
        _service.UseDirectConnection.Should().BeFalse();
        _service.ApiKey.Should().BeEmpty();
        _service.Endpoint.Should().Contain("workers.dev");
    }

    [Fact]
    public void EmptyApiKey_SwitchesBackToWorkerMode()
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

    // --- Device fingerprint ---

    [Fact]
    public void Configure_AcceptsDeviceId()
    {
        // Should not throw; deviceId is stored internally
        _service.Configure("glm-4-flash", null, "test-device-id-123");
    }

    [Fact]
    public void Configure_WithNullDeviceId_DoesNotThrow()
    {
        _service.Configure("glm-4-flash", null, null);
    }

    // --- Other properties ---

    [Fact]
    public void SupportedLanguages_IsLimitedSubset()
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

        // Should have fewer languages than full OpenAI language list
        languages.Count.Should().BeLessThan(32);
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
