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
    private static readonly string ServiceResultStatusTextProviderPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultStatusTextProvider.cs");
    private static readonly string ServiceResultItemXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml");
    private static readonly string ServiceResultItemPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml.cs");
    private static readonly string MinimalServiceResultItemXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "MinimalServiceResultItem.xaml");
    private static readonly string MinimalServiceResultItemPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "MinimalServiceResultItem.xaml.cs");

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
    public void MainPage_ReusesCachedResultControlsWhenServicesAreUnchanged()
    {
        var content = File.ReadAllText(MainPagePath);

        content.Should().Contain("TryReuseServiceResultControls(descriptors, reason)",
            "cached MainPage navigation should avoid releasing and recreating result controls when settings still match");
        content.Should().Contain("InitializeServiceResults reused cached controls",
            "reuse decisions should be explicit in DEBUG output");
    }

    [Fact]
    public void MainPage_EntersTranslatingStateBeforeDetection()
    {
        var content = File.ReadAllText(MainPagePath);

        content.Should().Contain("var translatingStatus = loc.GetString(\"StatusTranslating\");",
            "SetLoading should use the localized translating status");
        content.Should().Contain("UpdateStatus(null, translatingStatus);",
            "the header status should show translating while a query is active");
        content.Should().Contain("PrepareServiceResultsForQueryStart();",
            "service rows should also enter their loading state before language detection can block");
        content.Should().Contain("serviceResult.IsLoading = true;",
            "auto-query service rows should show their StatusText loading label immediately");

        var loadingIndex = content.IndexOf("SetLoading(true);", StringComparison.Ordinal);
        var rowLoadingIndex = content.IndexOf("PrepareServiceResultsForQueryStart();", StringComparison.Ordinal);
        var detectionIndex = content.IndexOf("DetectSourceLanguageForQueryAsync(inputText, detectionService, ct)", StringComparison.Ordinal);
        loadingIndex.Should().BeGreaterThanOrEqualTo(0);
        rowLoadingIndex.Should().BeGreaterThanOrEqualTo(0);
        detectionIndex.Should().BeGreaterThanOrEqualTo(0);
        loadingIndex.Should().BeLessThan(detectionIndex,
            "clicking Translate or pressing Enter should enter the querying UI before language detection can block");
        rowLoadingIndex.Should().BeLessThan(detectionIndex,
            "service rows should show Translating before language detection can block");
    }

    [Fact]
    public void ServiceRowsUseLocalizedStatusTextProvider()
    {
        var richControl = File.ReadAllText(ServiceResultItemPath);
        var richXaml = File.ReadAllText(ServiceResultItemXamlPath);
        var minimalControl = File.ReadAllText(MinimalServiceResultItemPath);
        var minimalXaml = File.ReadAllText(MinimalServiceResultItemXamlPath);
        var provider = File.ReadAllText(ServiceResultStatusTextProviderPath);

        richControl.Should().Contain("StatusText.Text = ServiceResultStatusTextProvider.GetStatusText(_serviceResult);",
            "rich service rows should localize StatusText from the UI layer");
        minimalControl.Should().Contain("ServiceResultStatusTextProvider.GetStatusText(serviceResult)",
            "minimal service rows should share the same localized status mapping");
        provider.Should().Contain("StatusTranslating",
            "translation loading rows should reuse the existing localized translating status");
        provider.Should().Contain("ServiceResult_Checking",
            "grammar checking rows should have a resource-backed status");
        provider.Should().Contain("ServiceResult_WaitingForResponse",
            "streaming placeholders should be resource-backed");
        richControl.Should().NotContain("StatusText.Text = _serviceResult.StatusText;",
            "model-layer status text is English-only and should not be displayed directly");
        minimalControl.Should().NotContain("return serviceResult.StatusText;",
            "model-layer status text is English-only and should not be displayed directly");
        richControl.Should().NotContain("\"Click to query\"");
        minimalControl.Should().NotContain("\"Click to query\"");
        richControl.Should().NotContain("\"Waiting for response...\"");
        minimalControl.Should().NotContain("\"Waiting for response...\"");
        richXaml.Should().NotContain("Click header to query");
        minimalXaml.Should().NotContain("Click header to query");
        richControl.Should().NotContain("StatusText.Text = \"Loading\";",
            "loading rows should not replace Translating... with a generic label");
        minimalControl.Should().NotContain("return \"Loading\";",
            "minimal rows should not replace Translating... with a generic label");
    }

    [Fact]
    public void MainPage_AvoidsImplicitResultRebuildDuringInitialLocalization()
    {
        var content = File.ReadAllText(MainPagePath);
        content.Should().Contain("ApplyLocalization(reinitializeServiceResults: false);",
            "initial page load should not trigger a hidden result-panel rebuild inside localization");
        content.Should().Contain("private void ApplyModeState(",
            "mode-state application should explicitly control whether result controls are rebuilt");
        content.Should().Contain("bool initializeLongDocFeatures = true",
            "mode switches should be able to show the loading overlay before initializing long-document controls");
        content.Should().Contain("if (reinitializeServiceResults && !isLongDoc)",
            "ApplyModeState should only rebuild result controls for explicit mode transitions, not for initial localization");
    }

    [Fact]
    public void MainPage_DefersLongDocInitializationUntilLongDocMode()
    {
        var content = File.ReadAllText(MainPagePath);

        content.Should().Contain("private void PopulateLongDocLanguageCombos(",
            "hidden long-document language combo population should be centralized and callable on demand");
        content.Should().Contain("if (_longDocFeaturesInitialized || _currentMode == QueryMode.LongDocument)",
            "initial quick-translation localization should not populate hidden long-document combo items");
        content.Should().MatchRegex(@"case QueryMode\.LongDocument:[\s\S]*EnsureLongDocFeaturesInitialized\(\);",
            "long-document controls should still be initialized before that mode is shown");
        content.Should().NotContain("if (!MinimalThemeService.IsActive)\r\n            {\r\n                EnsureLongDocFeaturesInitialized();\r\n            }",
            "quick-translation startup should not eagerly initialize hidden long-document controls");
    }

    [Fact]
    public void MainPage_SkipsStaticUiRefreshWhenSettingsSignatureIsUnchanged()
    {
        var content = File.ReadAllText(MainPagePath);

        content.Should().Contain("private string? _staticUiSettingsSignature;",
            "cached navigation should remember whether language and theme settings actually changed");
        content.Should().Contain("BuildStaticUiSettingsSignature()",
            "the skip decision should be based on a stable settings signature");
        content.Should().Contain("skipped ApplyLocalization/ApplySettings",
            "debug sessions should make skipped static UI refreshes visible");
    }

    [Fact]
    public void MainPage_ShowsLoadingOverlayWhenSwitchingBetweenTranslationAndLongDoc()
    {
        var xaml = File.ReadAllText(MainPageXamlPath);
        var content = File.ReadAllText(MainPagePath);

        xaml.Should().Contain("x:Name=\"ModeSwitchLoadingOverlay\"",
            "translation and long-document switches replace the main workspace and should use a content-layer overlay");
        xaml.Should().Contain("x:Name=\"ModeSwitchLoadingRing\"",
            "the mode-switch overlay should expose a visible loading animation");
        content.Should().Contain("private async Task<bool> SwitchModeAsync(QueryMode newMode)",
            "mode switching should be asynchronous so the loading overlay can render before heavy UI initialization");
        content.Should().Contain("await ShowModeSwitchLoadingAsync();",
            "translation and long-document switches should display the loading overlay");
        content.Should().Contain("await ApplyModeStateForModeSwitchAsync(newMode);",
            "heavy mode-switch work should be split after the overlay has rendered");
        content.Should().Contain("await Task.Delay(ModeSwitchMinimumDurationMs);",
            "the loading animation should remain visible long enough to be perceived");
        content.Should().Contain("private async void OnModeMenuItemClick(",
            "mode menu clicks should use the asynchronous switch path");
    }

    [Fact]
    public void MainPage_SettingsNavigationUsesPageLevelLoadingOverlay()
    {
        var xaml = File.ReadAllText(MainPageXamlPath);
        var content = File.ReadAllText(MainPagePath);
        var onSettingsClicked = GetMethodBody(content, "OnSettingsClicked");
        var onNavigatedTo = GetMethodBody(content, "OnNavigatedTo");

        xaml.Should().Contain("x:Name=\"PageNavigationLoadingOverlay\"",
            "cross-page navigation should show a page-level mask while the target page is being created");
        xaml.Should().Contain("x:Name=\"PageNavigationLoadingRing\"");
        xaml.Should().Contain("Grid.RowSpan=\"2\"");
        xaml.Should().Contain("Canvas.ZIndex=\"100\"");
        onSettingsClicked.Should().Contain("await ShowPageNavigationLoadingAsync();");
        onSettingsClicked.Should().Contain("Frame.Navigate(typeof(SettingsPage))");
        onSettingsClicked.Should().Contain("HidePageNavigationLoading();",
            "navigation failure should not leave the cached MainPage masked");
        onNavigatedTo.Should().Contain("HidePageNavigationLoading();",
            "returning from Settings should clear the cached page-level mask");
        content.Should().Contain("await Task.Delay(PageNavigationRenderDelayMs)");
    }

    [Fact]
    public void MainPage_ModeSwitchAvoidsHiddenResultListWork()
    {
        var content = File.ReadAllText(MainPagePath);

        content.Should().Contain("public void ApplyThemeChrome(bool refreshServiceResults = true)",
            "mode switching should be able to apply page chrome without walking hidden result controls");
        content.Should().Contain("if (refreshServiceResults)",
            "result-control theme refresh should be explicitly gated");
        content.Should().Contain("refreshServiceResultChrome: false",
            "mode switches should postpone result-list refresh until after visible state has changed");
        content.Should().Contain("ApplyThemeChrome(refreshServiceResults: false);",
            "long-document mode should refresh visible page chrome without rebuilding hidden result controls");
        content.Should().Contain("await YieldToDispatcherAsync",
            "mode switches should give the UI thread a render turn between visible-state changes and heavy work");
    }

    [Fact]
    public void MainPage_ReusesResultControlsDuringModeSwitch()
    {
        var content = File.ReadAllText(MainPagePath);

        content.Should().Contain("if (TryReuseServiceResultControls(descriptors, reason))",
            "returning from long-document mode should reuse existing result controls when service descriptors are unchanged");
        content.Should().NotContain("if (skipRebuildWhenDebugFlagSet && TryReuseServiceResultControls(descriptors, reason))",
            "result-control reuse should not be limited to debug navigation profiling");
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
        coordinator.Should().Contain("PhiSilicaResources.ProgressKeys.ReusingExisting");
        coordinator.Should().Contain("PhiSilicaResources.ProgressKeys.DeliveryOptimizationEstimate");
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

    private static string GetMethodBody(string codeBehind, string methodName)
    {
        var prefixes = new[]
        {
            "private void",
            "private async void",
            "private async Task",
            "protected override void"
        };
        var start = prefixes
            .Select(prefix => codeBehind.IndexOf($"{prefix} {methodName}(", StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        start.Should().BeGreaterOrEqualTo(0, $"{methodName} should exist");

        var braceStart = codeBehind.IndexOf('{', start);
        braceStart.Should().BeGreaterThan(start, $"{methodName} should have a body");

        var depth = 0;
        for (var i = braceStart; i < codeBehind.Length; i++)
        {
            if (codeBehind[i] == '{')
            {
                depth++;
            }
            else if (codeBehind[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return codeBehind.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not parse {methodName} body.");
    }
}
