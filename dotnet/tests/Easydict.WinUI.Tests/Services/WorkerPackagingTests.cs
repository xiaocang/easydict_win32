using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Configuration")]
public sealed class WorkerPackagingTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void ReleaseWorkflow_PublishesAllDefaultEnabledWorkers()
    {
        var workflowPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml"));
        var workflow = File.ReadAllText(workflowPath);

        workflow.Should().Contain("Easydict.Workers.LongDoc");
        workflow.Should().Contain("Easydict.Workers.LocalAi");
        workflow.Should().Contain("Easydict.Workers.Ocr");
        workflow.Should().Contain("./publish/${{ matrix.platform }}/workers/ocr");
        workflow.Should().Contain("./publish-msix/${{ matrix.platform }}/workers/ocr");
        workflow.Should().Contain("Dedupe-WorkerSharedFiles.ps1");
    }

    [Fact]
    public void Makefile_PublishesAllDefaultEnabledWorkers()
    {
        var makefilePath = Path.Combine(ProjectRoot, "Makefile");
        var makefile = File.ReadAllText(makefilePath);

        makefile.Should().Contain("Easydict.Workers.LongDoc");
        makefile.Should().Contain("Easydict.Workers.LocalAi");
        makefile.Should().Contain("Easydict.Workers.Ocr");
        makefile.Should().Contain("./publish/x64/workers/ocr");
        makefile.Should().Contain("./publish/arm64/workers/ocr");
        makefile.Should().Contain("./publish-msix/x64/workers/ocr");
        makefile.Should().Contain("./publish-msix/arm64/workers/ocr");
        makefile.Should().Contain("Dedupe-WorkerSharedFiles.ps1");
        makefile.Should().Contain("Worker settings default");
        makefile.Should().NotContain("UseLocalAiWorker default false");
    }

    [Fact]
    public void OpenVinoNativeRuntime_IsNotPublishedWithLocalAiWorker()
    {
        var csprojPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.OpenVINO",
            "Easydict.OpenVINO.csproj");
        var csproj = File.ReadAllText(csprojPath);

        csproj.Should().Contain("Intel.ML.OnnxRuntime.OpenVino");
        csproj.Should().Contain("<ExcludeAssets>runtime;native</ExcludeAssets>");
    }

    [Fact]
    public void LocalAiWorker_DoesNotInjectOpenVinoNativePathByDefault()
    {
        var spawnerPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Services",
            "Workers",
            "WorkerSpawner.cs");
        var spawner = File.ReadAllText(spawnerPath);

        spawner.Should().Contain("IsOpenVinoEpPathInjectionEnabled()");
        spawner.Should().Contain("EASYDICT_OPENVINO_RUNTIME_DIR");
        spawner.Should().NotContain("openVinoRuntimeDir + Path.PathSeparator + existingPath;\r\n        }");
    }

    [Fact]
    public void WorkerSharedDedupeScript_MovesOnlyAllowlistedIdenticalDlls()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "Dedupe-WorkerSharedFiles.ps1");
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("Microsoft.Windows.SDK.NET.dll");
        script.Should().Contain("Join-Path $PublishDir \"workers\"");
        script.Should().Contain("shared");
        script.Should().Contain("Get-FileHash");
        script.Should().Contain("Remove-Item");
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var solutionPath = Path.Combine(current, "Easydict.Win32.sln");
            if (File.Exists(solutionPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
