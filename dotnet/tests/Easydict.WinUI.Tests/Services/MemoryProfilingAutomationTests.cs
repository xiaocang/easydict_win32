using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Configuration")]
public sealed class MemoryProfilingAutomationTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void NightlyMemoryProfileScript_CollectsManagedAndNativeArtifacts()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "memory", "Invoke-NightlyMemoryProfile.ps1");
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("dotnet-counters");
        script.Should().Contain("dotnet-trace");
        script.Should().Contain("--profile gc-verbose");
        script.Should().Contain("typeperf.exe");
        script.Should().Contain("wpr -start referenceset");
        script.Should().Contain("procdump.exe");
        script.Should().Contain("vmmap.exe");
        script.Should().Contain("ScenarioCommand");
    }

    [Fact]
    public void NightlyMemoryProfileWorkflow_PublishesArtifacts()
    {
        var workflowPath = Path.Combine(ProjectRoot, "..", ".github", "workflows", "memory-nightly.yml");
        var workflow = File.ReadAllText(Path.GetFullPath(workflowPath));

        workflow.Should().Contain("schedule:");
        workflow.Should().NotContain("workflow_dispatch:");
        workflow.Split('\n')
            .Count(line => line.TrimStart().StartsWith("- cron:", StringComparison.Ordinal))
            .Should()
            .Be(1, "nightly memory profiling must be scheduled at most once per day");
        workflow.Should().Contain("concurrency:");
        workflow.Should().Contain("cancel-in-progress: false");
        workflow.Should().Contain("contents: write");
        workflow.Should().Contain("MEMORY_RESULTS_BRANCH: scratch/memory-nightly");
        workflow.Should().Contain("MEMORY_RESULTS_RETENTION_DAYS: 60");
        workflow.Should().Contain("id: memory_gate");
        workflow.Should().Contain("Test-MemoryProfileShouldRun.ps1");
        workflow.Should().Contain("steps.memory_gate.outputs.should_run == 'true'");
        workflow.Should().Contain("Invoke-NightlyMemoryProfile.ps1");
        workflow.Should().Contain("Publish-MemoryProfileScratchBranch.ps1");
        workflow.Should().Contain("-RetentionDays $env:MEMORY_RESULTS_RETENTION_DAYS");
        workflow.Should().Contain("actions/upload-artifact@v4");
        workflow.Should().Contain("retention-days: 14");
        workflow.Should().Contain("artifacts/memory-gate/nightly");
    }

    [Fact]
    public void NightlyMemoryProfileGate_SkipsWhenCurrentCommitAlreadyProfiled()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "memory", "Test-MemoryProfileShouldRun.ps1");
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("scratch/memory-nightly");
        script.Should().Contain("git");
        script.Should().Contain("fetch origin");
        script.Should().Contain("index.json");
        script.Should().Contain("sourceSha");
        script.Should().Contain("should_run");
        script.Should().Contain("current source sha already has nightly memory results");
    }

    [Fact]
    public void ScratchBranchPublisher_PushesComparableArtifactsOnly()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "memory", "Publish-MemoryProfileScratchBranch.ps1");
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("scratch/memory-nightly");
        script.Should().Contain("git");
        script.Should().Contain("worktree");
        script.Should().Contain("push");
        script.Should().Contain("summary.json");
        script.Should().Contain("typeperf.csv");
        script.Should().Contain("dotnet-counters.json");
        script.Should().Contain(".nettrace");
        script.Should().Contain(".etl");
        script.Should().Contain(".dmp");
        script.Should().Contain("skippedHeavyArtifacts");
        script.Should().Contain("RetentionDays");
        script.Should().Contain("AddDays(-$RetentionDays)");
        script.Should().Contain("capturedAtUtc");
        script.Should().Contain("older than $RetentionDays days");
        script.Should().Contain("RetentionRuns");
    }

    [Fact]
    public void MemoryProfilingGuide_DocumentsNightlyProfileScript()
    {
        var docPath = Path.Combine(ProjectRoot, "memory-profiling.md");
        var doc = File.ReadAllText(docPath);

        doc.Should().Contain("Invoke-NightlyMemoryProfile.ps1");
        doc.Should().Contain("Test-MemoryProfileShouldRun.ps1");
        doc.Should().Contain("Publish-MemoryProfileScratchBranch.ps1");
        doc.Should().Contain("scratch/memory-nightly");
        doc.Should().Contain("60 days");
        doc.Should().Contain("-EnableWprReferenceSet");
        doc.Should().Contain("-ScenarioCommand");
        doc.Should().Contain("dotnet-trace --profile gc-verbose");
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
