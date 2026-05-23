extern alias LongDocWorker;

using Easydict.SidecarClient.Protocol;
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
}
