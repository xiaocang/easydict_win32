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
        makefile.Should().Contain("Worker settings default");
        makefile.Should().NotContain("UseLocalAiWorker default false");
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
