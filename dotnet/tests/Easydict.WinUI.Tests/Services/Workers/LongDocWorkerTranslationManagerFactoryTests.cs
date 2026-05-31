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
    public void Build_ConfiguresTraditionalHttpServicesFromSnapshot()
    {
        using var manager = WorkerTranslationManagerFactory.Build(new SettingsSnapshot
        {
            CaiyunToken = "caiyun-token",
            NiuTransApiKey = "niu-key",
            YoudaoAppKey = "youdao-key",
            YoudaoAppSecret = "youdao-secret",
            YoudaoUseOfficialApi = true,
        });

        manager.Services["caiyun"].Should().BeOfType<CaiyunService>()
            .Which.IsConfigured.Should().BeTrue();
        manager.Services["niutrans"].Should().BeOfType<NiuTransService>()
            .Which.IsConfigured.Should().BeTrue();
        manager.Services["youdao"].Should().BeOfType<YoudaoService>()
            .Which.IsConfigured.Should().BeTrue();
    }
}
