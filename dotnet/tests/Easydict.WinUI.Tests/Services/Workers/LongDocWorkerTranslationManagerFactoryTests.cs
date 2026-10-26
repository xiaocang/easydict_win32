extern alias LongDocWorker;

using System.Reflection;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;
using WorkerTranslationManagerFactory = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.WorkerTranslationManagerFactory;

namespace Easydict.WinUI.Tests.Services.Workers;

[Trait("Category", "Worker")]
public sealed class LongDocWorkerTranslationManagerFactoryTests
{
    [Fact]
    public void Build_RegistersWindowsLocalAiProxyService()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            LocalAIProvider = LocalAiProviderModes.OpenVINO,
        });

        manager.Services.Should().ContainKey("windows-local-ai");
        manager.Services["windows-local-ai"].DisplayName.Should().Be("Windows Local AI");
    }

    [Fact]
    public void Build_ConfiguresKimiModelWithoutReplacingEndpoint()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            KimiApiKey = "test-key",
            KimiModel = "kimi-latest",
        });

        var service = manager.Services["kimi"].Should().BeOfType<KimiService>().Which;
        service.ApiKey.Should().Be("test-key");
        service.Model.Should().Be("kimi-latest");
        service.Endpoint.Should().Be("https://api.moonshot.cn/v1/chat/completions");
    }

    [Fact]
    public void Build_ConfiguresOpenRouterModelWithoutReplacingEndpoint()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            OpenRouterApiKey = "test-key",
            OpenRouterModel = "openrouter/auto",
        });

        var service = manager.Services["openrouter"].Should().BeOfType<OpenRouterService>().Which;
        service.ApiKey.Should().Be("test-key");
        service.Model.Should().Be("openrouter/auto");
        service.Endpoint.Should().Be("https://openrouter.ai/api/v1/chat/completions");
    }

    [Fact]
    public void Build_ConfiguresOrcaRouterModelWithoutReplacingEndpoint()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            OrcaRouterApiKey = "test-key",
            OrcaRouterModel = "orcarouter/auto",
        });

        var service = manager.Services["orcarouter"].Should().BeOfType<OrcaRouterService>().Which;
        service.ApiKey.Should().Be("test-key");
        service.Model.Should().Be("orcarouter/auto");
        service.Endpoint.Should().Be("https://api.orcarouter.ai/v1/chat/completions");
    }

    [Fact]
    public void Build_ConfiguresOllamaPromptStyle()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            OllamaPromptStyle = "Concise",
        });

        manager.Services["ollama"].Should().BeOfType<OllamaService>()
            .Which.PromptStyle.Should().Be(Easydict.TranslationService.Models.OllamaPromptStyle.Concise);
    }

    [Fact]
    public void Build_RegistersProfileUnderItsRuntimeIdentityAndReplacesLegacyService()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            OpenAIApiKey = "stale-legacy-key",
            OpenAIModel = "stale-legacy-model",
            AiServiceProfiles =
            [
                new AiServiceProfileSnapshot
                {
                    ProfileId = "longdoc-openai-work",
                    ServiceId = "ai-profile:longdoc-openai-work",
                    ProviderType = "openai",
                    LegacyServiceId = "openai",
                    Name = "Work account",
                    IsEnabled = true,
                    ConfigurationRevision = 3,
                    Configuration = new Dictionary<string, string>
                    {
                        ["endpoint"] = "https://worker.invalid/v1/chat/completions",
                        ["model"] = "worker-model",
                        ["temperature"] = "0.4",
                        ["apiFormat"] = "ChatCompletions"
                    },
                    Secrets = new Dictionary<string, string> { ["apiKey"] = "profile-secret" },
                    SupportsStreaming = true,
                    SupportsCorrection = true
                }
            ]
        });

        manager.Services.Should().ContainKey("ai-profile:longdoc-openai-work");
        manager.Services.Should().NotContainKey("openai");
        var profileService = manager.Services["ai-profile:longdoc-openai-work"];
        profileService.DisplayName.Should().Be("Work account");
        profileService.Should().BeAssignableTo<IStreamTranslationService>();
        profileService.Should().BeAssignableTo<IGrammarCorrectionService>();
        profileService.Should().BeAssignableTo<ICacheKeyDiscriminatorProvider>()
            .Which.CacheKeyDiscriminator.Should().Contain("longdoc-openai-work:3");
        var innerService = typeof(ProfiledTranslationService)
            .GetField("InnerService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(profileService)
            .Should().BeOfType<OpenAIService>().Which;
        innerService.ApiKey.Should().Be("profile-secret");
        innerService.Endpoint.Should().Be("https://worker.invalid/v1/chat/completions");
        innerService.Model.Should().Be("worker-model");
    }

    [Fact]
    public void Build_RegistersEnabledAndDisabledMagpieProfilesWithoutBreakingOtherServices()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            AiServiceProfiles =
            [
                new AiServiceProfileSnapshot
                {
                    ProfileId = "longdoc-magpie-local",
                    ServiceId = "ai-profile:longdoc-magpie-local",
                    ProviderType = "magpie",
                    LegacyServiceId = "magpie",
                    Name = "Local Magpie",
                    IsEnabled = true,
                    ConfigurationRevision = 1,
                    Configuration = new Dictionary<string, string>
                    {
                        ["endpoint"] = "http://127.0.0.1:4545/v1/chat/completions",
                        ["model"] = "openai/gpt-6-luna",
                        ["allowRemote"] = "false"
                    },
                    Secrets = new Dictionary<string, string>(),
                    SupportsStreaming = true,
                    SupportsCorrection = true
                },
                new AiServiceProfileSnapshot
                {
                    ProfileId = "longdoc-magpie-disabled",
                    ServiceId = "ai-profile:longdoc-magpie-disabled",
                    ProviderType = "magpie",
                    LegacyServiceId = "magpie",
                    Name = "Disabled Magpie",
                    IsEnabled = false,
                    Configuration = new Dictionary<string, string>(),
                    Secrets = new Dictionary<string, string>(),
                    SupportsStreaming = true,
                    SupportsCorrection = true
                }
            ]
        });

        manager.Services.Should().ContainKey("ai-profile:longdoc-magpie-local");
        manager.Services.Should().ContainKey("ai-profile:longdoc-magpie-disabled");
        manager.Services.Should().NotContainKey("magpie");
        manager.Services.Should().ContainKey("deepl");
        manager.Services["ai-profile:longdoc-magpie-local"].DisplayName.Should().Be("Local Magpie");
        manager.Services["ai-profile:longdoc-magpie-local"].IsConfigured.Should().BeTrue();
        manager.Services["ai-profile:longdoc-magpie-disabled"].IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Build_EmptyProfileSnapshotRemovesLegacyAiButKeepsNonAiAndLocalAi()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            OpenAIApiKey = "stale-legacy-key",
            AiServiceProfiles = []
        });

        manager.Services.Should().NotContainKey("openai");
        manager.Services.Should().ContainKey("deepl");
        manager.Services.Should().ContainKey("windows-local-ai");
        var error = await Assert.ThrowsAsync<TranslationException>(() => manager.TranslateAsync(
            new TranslationRequest { Text = "hello", ToLanguage = Language.SimplifiedChinese },
            serviceId: "openai"));
        error.Message.Should().Contain("Unknown service: openai");
    }

    [Fact]
    public async Task Build_MissingProviderInProfileSnapshotCannotFallBackToLegacyAiId()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            OpenAIApiKey = "stale-legacy-key",
            AiServiceProfiles =
            [
                new AiServiceProfileSnapshot
                {
                    ProfileId = "codex-work",
                    ServiceId = "ai-profile:codex-work",
                    ProviderType = "codex",
                    LegacyServiceId = "codex",
                    Name = "Codex work",
                    IsEnabled = true,
                    Configuration = new Dictionary<string, string>(),
                    Secrets = new Dictionary<string, string>(),
                    SupportsStreaming = true,
                    SupportsCorrection = true
                }
            ]
        });

        manager.Services.Should().ContainKey("ai-profile:codex-work");
        manager.Services.Should().NotContainKey("openai");
        manager.Services.Should().ContainKey("deepl");
        manager.Services.Should().ContainKey("windows-local-ai");
        var error = await Assert.ThrowsAsync<TranslationException>(() => manager.TranslateAsync(
            new TranslationRequest { Text = "hello", ToLanguage = Language.SimplifiedChinese },
            serviceId: "openai"));
        error.Message.Should().Contain("Unknown service: openai");
    }
}
