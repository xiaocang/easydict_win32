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
    private static readonly string MainPageXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MainPage.xaml");
    private static readonly string MainPagePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MainPage.xaml.cs");
    private static readonly string PhiSilicaPromptServicePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Services", "PhiSilicaModelPreparationPromptService.cs");
    private static readonly string PhiSilicaPreparationCoordinatorPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Services", "PhiSilicaModelPreparationCoordinator.cs");
    private static readonly string ServiceResultViewHostPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultViewHost.cs");

    [Fact]
    public void MainPage_ReleasesServiceResultControlsBeforeRebuild()
    {
        var content = File.ReadAllText(MainPagePath);
        content.Should().Contain("private void ReleaseServiceResultControls(",
            "MainPage should centralize result-control cleanup before rebuilding the panel");
        content.Should().Contain("ReleaseServiceResultControls();",
            "InitializeServiceResults and cleanup paths should release previous result controls");
        content.Should().Contain("ServiceResultViewHost.Release(",
            "ReleaseServiceResultControls should delegate to the ServiceResultViewHost helper");
    }

    [Fact]
    public void MainPage_UnsubscribesResultControlEventsDuringRelease()
    {
        var content = File.ReadAllText(ServiceResultViewHostPath);
        content.Should().Contain("control.CollapseToggled -= collapseToggled;",
            "old result controls should detach collapse event handlers before being discarded");
        content.Should().Contain("control.QueryRequested -= queryRequested;",
            "old result controls should detach query-request event handlers before being discarded");
        content.Should().Contain("control.Cleanup();",
            "ServiceResultViewHost.Release should delegate native and binding cleanup to each result control");
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
    public void MainPage_PreservesLanguageSelectionSuppressionWhileSyncingResponsiveCombos()
    {
        var content = File.ReadAllText(MainPagePath);

        content.Should().Contain("var wasSuppressed = _suppressSourceLanguageSelectionChanged;",
            "wide/narrow source combo synchronization must not clear an outer localization/settings suppression scope");
        content.Should().Contain("finally { _suppressSourceLanguageSelectionChanged = wasSuppressed; }",
            "source combo synchronization should restore the previous suppression state");
        content.Should().Contain("var wasSuppressed = _suppressTargetLanguageSelectionChanged;",
            "wide/narrow target combo synchronization must not fire a second manual target-language change");
        content.Should().Contain("finally { _suppressTargetLanguageSelectionChanged = wasSuppressed; }",
            "target combo synchronization should restore the previous suppression state");
        content.Should().NotContain("TargetLangCombo.SelectionChanged += (s, e) => SyncComboSelection",
            "target combo sync should run under suppression instead of directly changing the paired combo");
    }

    [Fact]
    public void MainPage_CancelsTransientQueriesWhenNavigatingAwayInCachedMode()
    {
        var content = File.ReadAllText(MainPagePath);

        content.Should().Contain("private void CancelTransientQueriesForNavigation()",
            "cached page navigation should stop in-flight translation work without disposing the page");
        content.Should().MatchRegex(@"CancelTransientQueriesForNavigation\(\);\s*#if DEBUG\s*MemoryDiagnostics\.LogSnapshot\(""MainPage\.OnPageUnloaded complete \(A cached\)""\)",
            "the cached unload path should cancel transient queries before the page is hidden");
        content.Should().Contain("var manualCts = Interlocked.Exchange(ref _manualQueryCts, null);",
            "manual per-service requests should be cancelled with the normal translation request");
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

    [Fact]
    public void MainPage_ShowsLocalModelPreparationProgressDuringPhiSilicaDownload()
    {
        var xaml = File.ReadAllText(MainPageXamlPath);
        var content = File.ReadAllText(MainPagePath);
        var promptService = File.ReadAllText(PhiSilicaPromptServicePath);
        var coordinator = File.ReadAllText(PhiSilicaPreparationCoordinatorPath);

        xaml.Should().Contain("x:Name=\"LocalModelPreparationProgressPanel\"");
        xaml.Should().Contain("x:Name=\"LocalModelPreparationStatusText\"");
        xaml.Should().Contain("x:Name=\"LocalModelPreparationProgressBar\"");
        xaml.Should().Contain("IsIndeterminate=\"True\"");

        content.Should().Contain("ShowLocalModelPreparationProgress");
        content.Should().Contain("PhiSilicaModelPreparationCoordinator.Instance.CreatePreparingSnapshot",
            "string-only progress callbacks should resume the shared coordinator snapshot after navigation");
        content.Should().Contain("HideLocalModelPreparationProgress");
        content.Should().Contain("PhiSilicaModelPreparationCoordinator.Instance.ProgressChanged += OnPhiSilicaPreparationProgressChanged");
        content.Should().Contain("SyncLocalModelPreparationProgressFromCoordinator");
        content.Should().MatchRegex(@"PromptAndPrepareIfNeededAsync\([\s\S]*ShowDialogAsync,\s*ct,\s*ShowLocalModelPreparationProgress");

        promptService.Should().Contain("Action<string>? reportPreparationProgress");
        promptService.Should().Contain("PhiSilicaModelPreparationCoordinator.Instance.EnsureReadyAsync");

        coordinator.Should().Contain("Get-DeliveryOptimizationStatus");
        coordinator.Should().Contain("DeliveryOptimizationPollInterval");
        coordinator.Should().Contain("CreateDeliveryOptimizationSnapshot");
        coordinator.Should().Contain("ProgressPercent");
        coordinator.Should().Contain("PhiSilicaPreparationProgress_ReusingExisting");
        coordinator.Should().Contain("PhiSilicaPreparationProgress_DeliveryOptimizationEstimate");
        coordinator.Should().Contain("CancellationToken.None",
            "navigation/query cancellation should not cancel the Windows-managed model preparation job");
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
