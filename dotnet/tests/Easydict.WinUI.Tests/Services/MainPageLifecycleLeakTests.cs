using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Static regression checks for MainPage result-control lifecycle cleanup and debug instrumentation.
/// </summary>
[Trait("Category", "Configuration")]
public class MainPageLifecycleLeakTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string MainPagePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MainPage.xaml.cs");

    [Fact]
    public void MainPage_ReleasesServiceResultControlsBeforeRebuild()
    {
        var content = File.ReadAllText(MainPagePath);
        content.Should().Contain("private void ReleaseServiceResultControls()",
            "MainPage should centralize result-control cleanup before rebuilding the panel");
        content.Should().Contain("ReleaseServiceResultControls();",
            "InitializeServiceResults and cleanup paths should release previous result controls");
    }

    [Fact]
    public void MainPage_UnsubscribesResultControlEventsDuringRelease()
    {
        var content = File.ReadAllText(MainPagePath);
        content.Should().Contain("control.CollapseToggled -= OnServiceCollapseToggled;",
            "old result controls should detach collapse event handlers before being discarded");
        content.Should().Contain("control.QueryRequested -= OnServiceQueryRequested;",
            "old result controls should detach query-request event handlers before being discarded");
        content.Should().Contain("control.Cleanup();",
            "MainPage should delegate native and binding cleanup to each result control");
    }

    [Fact]
    public void MainPage_UsesDebugFlagToSkipResultRebuildOnReturn()
    {
        var content = File.ReadAllText(MainPagePath);
        content.Should().Contain("EASYDICT_DEBUG_DISABLE_MAINPAGE_RESULT_REBUILD",
            "profiling should be able to isolate MainPage result-panel rebuild costs");
        content.Should().Contain("InitializeServiceResults(skipRebuildWhenDebugFlagSet: true, reason: \"OnPageLoaded\");",
            "MainPage reload should explicitly opt into the debug rebuild-skip behavior");
    }

    [Fact]
    public void MainPage_AvoidsImplicitResultRebuildDuringInitialLocalization()
    {
        var content = File.ReadAllText(MainPagePath);
        content.Should().Contain("ApplyLocalization(reinitializeServiceResults: false);",
            "initial page load should not trigger a hidden result-panel rebuild inside localization");
        content.Should().Contain("private void ApplyModeState(bool reinitializeServiceResults = true)",
            "mode-state application should explicitly control whether result controls are rebuilt");
        content.Should().Contain("if (reinitializeServiceResults && !isLongDoc)",
            "ApplyModeState should only rebuild result controls for explicit mode transitions, not for initial localization");
    }

    [Fact]
    public void MainPage_LogsResultRebuildReason()
    {
        var content = File.ReadAllText(MainPagePath);
        content.Should().Contain("reason = \"Unspecified\"",
            "result-panel rebuild diagnostics should record why the rebuild happened");
        content.Should().Contain("reason={reason}",
            "debug output should distinguish OnPageLoaded rebuilds from mode-switch rebuilds");
    }

    [Fact]
    public void MainPage_LogsObjectStateAcrossLifecycle()
    {
        var content = File.ReadAllText(MainPagePath);
        content.Should().Contain("[MainPage][Objects]",
            "MainPage should emit object-count diagnostics during memory profiling");
        content.Should().Contain("MemoryDiagnostics.LogSnapshot(\"MainPage.OnPageLoaded complete\")",
            "load completion should be logged for correlation with Settings navigation");
        content.Should().Contain("MemoryDiagnostics.LogSnapshot(\"MainPage.OnPageUnloaded complete (A cached)\")",
            "cached unload should still emit diagnostics to distinguish retention from page reuse");
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
