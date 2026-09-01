extern alias LongDocWorker;

using Easydict.SidecarClient.Protocol;
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
}
