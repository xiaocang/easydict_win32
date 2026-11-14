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
        script.Should().Contain("Start-TypeperfJob");
        script.Should().Contain("Normalize-TypeperfCounters");
        script.Should().Contain("-Counters $typeperfCounters");
        script.Should().Contain("ConvertTo-Json -InputObject @($counterList) -Compress");
        script.Should().Contain("$parsedCounters = ConvertFrom-Json $CountersJson");
        script.Should().Contain("ConvertFrom-Json $CountersJson");
        script.Should().Contain("Remove-Item -LiteralPath $CsvPath");
        script.Should().Contain("cmd.exe /d /c $command");
        script.Should().Contain("\"-y\"");
        script.Should().Contain("Stop-JobIfRunning $typeperfJob");
        script.Should().Contain("wpr -start referenceset");
        script.Should().Contain("procdump.exe");
        script.Should().Contain("vmmap.exe");
        script.Should().Contain("ScenarioCommand");
    }

    [Fact]
    public void PrMemoryGateScript_PrintsTestLogs_WhenAppProcessIsNotObservable()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "memory", "Invoke-PrMemoryGate.ps1");
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("Write-LogTail");
        script.Should().Contain("Wait-TargetProcess \"Easydict.WinUI\"");
        script.Should().Contain("EASYDICT_MEMORY_GATE_PROCESS_ID_PATH");
        script.Should().Contain("process-id.marker");
        script.Should().Contain("EASYDICT_UIA_MEMORY_AB_MODE");
        script.Should().Contain("Start-TypeperfJob");
        script.Should().Contain("Normalize-TypeperfCounters");
        script.Should().Contain("-Counters $typeperfCounters");
        script.Should().Contain("ConvertTo-Json -InputObject @($counterList) -Compress");
        script.Should().Contain("$parsedCounters = ConvertFrom-Json $CountersJson");
        script.Should().Contain("ConvertFrom-Json $CountersJson");
        script.Should().Contain("Remove-Item -LiteralPath $CsvPath");
        script.Should().Contain("cmd.exe /d /c $command");
        script.Should().Contain("\"-y\"");
        script.Should().Contain("Stop-JobIfRunning $typeperfJob");
        script.Should().Contain("$null = $testProcess.Handle");
        script.Should().Contain("$testProcess.Refresh()");
        script.Should().Contain("ConvertFrom-Json $text");
        script.Should().Contain("$json.Events");
        script.Should().Contain("Test-GcHeapSizeCounterName");
        script.Should().Contain("GC Heap Size(?: \\(MB\\))?");
        script.Should().Contain("Read-GcdumpHeapBytes");
        script.Should().Contain("managedHeapBytes");
        script.Should().Contain("Managed heap bytes exceeded threshold after close");
        script.Should().Contain("PrivateBytesAbsoluteAllowanceMB");
        script.Should().Contain("[int]$PrivateBytesAbsoluteAllowanceMB = 160");
        script.Should().Contain("ManagedHeapAbsoluteAllowanceMB");
        script.Should().Contain("absoluteAllowancesMB");
        script.Should().Contain("[Math]::Max($privateRelativeLimit, $privateAbsoluteLimit)");
        script.Should().Contain("EASYDICT_MEMORY_GATE_PHASE_DIR");
        script.Should().Contain("19-post-close-idle-complete.marker");
        script.Should().Contain("phase-snapshots.json");
        script.Should().Contain("New-PhaseSnapshots");
        script.Should().Contain("Convert-TypeperfTimestampToUtc");
        script.Should().Contain("privateBytesDeltaFromPrevious");
        script.Should().Contain("phaseSnapshots");
        script.Should().Contain("Get-SeriesFromIndex");
        script.Should().Contain("$postCloseHandleValues = Get-SeriesFromIndex $handles $postCloseStartIndex");
        script.Should().Contain("postCloseSampleCount");
        script.Should().Contain("HandleCountPostCloseGrowthAllowance");
        script.Should().Contain("postCloseGrowthAllowance");
        script.Should().Contain("Handle Count is still growing after close");
        script.Should().Contain("Keep final gcdump out of typeperf/dotnet-counters tail metrics.");
        script.IndexOf("Keep final gcdump out of typeperf/dotnet-counters tail metrics.", StringComparison.Ordinal)
            .Should()
            .BeLessThan(script.IndexOf("Invoke-Gcdump $dotnetGcdump $processId $finalGcdump", StringComparison.Ordinal));
        script.Should().Contain("Fatal error\\.|AccessViolationException");
        script.Should().Contain("Memory gate app process emitted a fatal runtime error");
        script.Should().Contain("WaitForExit(5000) | Out-Null");
        script.Should().Contain("catch");
        script.Should().Contain("throw");
    }

    [Fact]
    public void PrMemoryGateWorkflow_UsesExpandedUiNativeAllowance()
    {
        var workflowPath = Path.Combine(ProjectRoot, "..", ".github", "workflows", "memory-gate.yml");
        var workflow = File.ReadAllText(Path.GetFullPath(workflowPath));

        workflow.Should().Contain("Invoke-PrMemoryGate.ps1");
        workflow.Should().Contain("-PrivateBytesAbsoluteAllowanceMB 160");
        workflow.Should().Contain("artifacts/memory-gate/pr");
    }

    [Fact]
    public void UiAutomationLauncher_FailsFast_WhenLaunchedProcessExitsBeforeMainWindow()
    {
        var launcherPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "tests",
            "Easydict.UIAutomation.Tests",
            "Infrastructure",
            "AppLauncher.cs"));
        var launcher = File.ReadAllText(launcherPath);

        launcher.Should().Contain("HasLaunchedApplicationExited");
        launcher.Should().Contain("catch (InvalidOperationException)");
        launcher.Should().Contain("exited before the main window appeared");
    }

    [Fact]
    public void UiAutomationMemoryGate_WritesProcessIdMarker_ForReliableCollection()
    {
        var testPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "tests",
            "Easydict.UIAutomation.Tests",
            "Tests",
            "MemoryGateTests.cs"));
        var test = File.ReadAllText(testPath);

        test.Should().Contain("WriteProcessIdMarker");
        test.Should().Contain("EASYDICT_MEMORY_GATE_PROCESS_ID_PATH");
        test.Should().Contain("_launcher.Application.ProcessId");
        test.Should().Contain("WritePhaseMarker");
        test.Should().Contain("EASYDICT_MEMORY_GATE_PHASE_DIR");
        test.Should().Contain("ModeMenuButton");
        test.Should().Contain("FindModeButtonWithName");
        test.Should().Contain("OpenModeMenu");
        test.Should().Contain("09-long-doc-mode-ready");
        test.Should().Contain("13-long-doc-page-range-set");
        test.Should().Contain("16-settings-opened");
        test.Should().Contain("ExerciseSettingsTabs");
        test.Should().Contain("$\"SettingsTab_{tab}\"");
        test.Should().Contain("\"General\"");
        test.Should().Contain("\"About\"");
        test.Should().Contain("17a-settings-reopened");
        test.Should().Contain("17c-mini-window-opened");
        test.Should().Contain("17f-fixed-window-opened");
        test.Should().Contain("UITestHelper.SendHotkey");
        test.Should().Contain("18-main-window-closed");
    }

    [Fact]
    public void UiAutomationMemoryGate_CoversInterfaceSwitchResponsiveness()
    {
        var testPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "tests",
            "Easydict.UIAutomation.Tests",
            "Tests",
            "MemoryGateTests.cs"));
        var test = File.ReadAllText(testPath);

        test.Should().Contain("InterfaceSwitching_ShouldStayResponsiveAcrossMainSettingsAndFloatingSurfaces");
        test.Should().Contain("MeasureResponsiveStep");
        test.Should().Contain("SwitchToLongDocMode(window)");
        test.Should().Contain("SwitchToQuickTranslateMode(window)");
        test.Should().Contain("OpenSettingsExerciseTabsAndReturn(window)");
        test.Should().Contain("ExerciseFloatingWindows(window)");
        test.Should().Contain("main page final responsiveness probe");
        test.Should().Contain("UI thread should not freeze while switching");
        test.Should().Contain("BeLessThan(");
    }

    [Fact]
    public void MainPage_ModeButton_ExposesStableAutomationState()
    {
        var xamlPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Views",
            "MainPage.xaml"));
        var codePath = Path.ChangeExtension(xamlPath, ".xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);

        xaml.Should().Contain("x:Name=\"ModeSelectorButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"ModeMenuButton\"");
        xaml.Should().Contain("Tapped=\"OnModeMenuItemTapped\"");
        code.Should().Contain("AutomationProperties.SetName");
        code.Should().Contain("Mode: Long Document");
        code.Should().Contain("Mode: Translation");
        code.Should().Contain("OnModeMenuItemTapped");
        code.Should().Contain("SwitchModeFromMenuItemAsync");
        code.Should().Contain("senderName");
        code.Should().Contain("nameof(ModeTranslationItem)");
        code.Should().Contain("nameof(ModeLongDocItem)");
    }

    [Fact]
    public void SettingsPage_TabsExposeStableAutomationIds()
    {
        var xamlPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Views",
            "SettingsPage.xaml"));
        var codePath = Path.ChangeExtension(xamlPath, ".xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);

        xaml.Should().Contain("AutomationProperties.AutomationId=\"{Binding AutomationId}\"");
        xaml.Should().Contain("AutomationProperties.Name=\"{Binding Label}\"");
        xaml.Should().Contain("Loaded=\"OnSettingsTabButtonLoaded\"");
        code.Should().Contain("public string AutomationId => $\"SettingsTab_{Id}\";");
        code.Should().Contain("OnSettingsTabButtonLoaded");
        code.Should().Contain("AutomationProperties.SetAutomationId(button, tab.AutomationId)");
        code.Should().Contain("AutomationProperties.SetName(button, tab.Label)");
        code.Should().Contain("ReleaseViewsTabContent");
        code.Should().Contain("UnloadObject(ViewsTabContent)");
    }

    [Fact]
    public void SettingsPage_UsesInWindowLoadingNavigation()
    {
        var appPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "App.xaml.cs"));
        var mainPagePath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Views",
            "MainPage.xaml.cs"));
        var app = File.ReadAllText(appPath);
        var mainPage = File.ReadAllText(mainPagePath);

        app.Should().NotContain("--settings-worker");
        app.Should().NotContain("OpenSettingsWorkerAsync");
        app.Should().NotContain("ProcessStartInfo");
        app.Should().Contain("ShowAndActivateWindow();");
        app.Should().Contain("frame.Navigate(typeof(SettingsPage))",
            "tray Settings should navigate inside the existing main window");
        mainPage.Should().Contain("await ShowPageNavigationLoadingAsync();");
        mainPage.Should().Contain("Frame.Navigate(typeof(SettingsPage))");
        mainPage.Should().NotContain("await App.OpenSettingsWorkerAsync()");
    }

    [Fact]
    public void App_MemoryAbMode_ReleasesFrameContent_WhenMainWindowIsHidden()
    {
        var codePath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "App.xaml.cs"));
        var code = File.ReadAllText(codePath);

        code.Should().Contain("EASYDICT_UIA_MEMORY_AB_MODE");
        code.Should().Contain("ReleaseMainWindowContentForMemoryGate");
        code.Should().Contain("frame.NavigationFailed -= OnNavigationFailed");
        code.Should().Contain("frame.Navigated -= OnRootFrameNavigated");
        code.Should().Contain("frame.BackStack.Clear()");
        code.Should().Contain("frame.ForwardStack.Clear()");
        code.Should().Contain("frame.Content = null");
        code.Should().Contain("_window.Content = null");
        code.Should().Contain("EnsureRootFrame");
        code.Should().Contain("EnsureMainPageContent");
    }

    [Fact]
    public void NightlyMemoryProfileWorkflow_PublishesArtifacts()
    {
        var workflowPath = Path.Combine(ProjectRoot, "..", ".github", "workflows", "memory-nightly.yml");
        var workflow = File.ReadAllText(Path.GetFullPath(workflowPath));

        workflow.Should().Contain("schedule:");
        workflow.Should().Contain("workflow_dispatch:",
            "a fix has to be verifiable without waiting for the next scheduled run");
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

    /// <summary>
    /// GitHub Actions runs a <c>shell: pwsh</c> step through a wrapper that ends with
    /// <c>if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }</c>, so the
    /// status of the script's last native command becomes the step result. The last native command
    /// here is <c>git fetch</c>, which legitimately fails while the scratch branch does not exist
    /// yet - the normal first run. That made the nightly gate report failure and skip every
    /// profiling step behind it for as long as the scratch branch had never been published, so the
    /// script must settle the decision with an explicit success code. Real failures still throw
    /// first, so this cannot hide them.
    /// </summary>
    [Fact]
    public void NightlyMemoryProfileGate_EndsWithAnExplicitSuccessExit()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "memory", "Test-MemoryProfileShouldRun.ps1");
        var script = File.ReadAllText(scriptPath);

        script.TrimEnd().Should().EndWith("exit 0",
            "the runner wrapper propagates $LASTEXITCODE from the last native command, which is the "
            + "fetch that fails on a first run with no scratch branch yet");
        script.Should().Contain("throw \"Could not resolve git repository root.\"",
            "genuine failures must keep failing the step");
    }

    /// <summary>
    /// A failed <c>git fetch</c> is not by itself proof that the scratch branch does not exist: it
    /// also fails when origin is unreachable or when the ref exists but cannot be fetched. Only the
    /// first case is the normal first run, so the script must ask the remote with an exact ref name
    /// and keep the other two cases failing the step.
    /// </summary>
    [Fact]
    public void NightlyMemoryProfileGate_DistinguishesMissingBranchFromFetchFailures()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "memory", "Test-MemoryProfileShouldRun.ps1");
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("$branchRef = \"refs/heads/$BranchName\"",
            "the lookup must name the full ref so nothing else can match it");
        script.Should().Contain("ls-remote --heads origin $branchRef",
            "a failed fetch has to be classified by asking the remote");
        script.Should().Contain("Could not reach origin to resolve",
            "a network or auth outage must fail the step instead of silently profiling");
        script.Should().Contain("exists on origin but could not be fetched",
            "a ref that exists but cannot be fetched must not be downgraded to a first run");
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

    /// <summary>
    /// A <c>New-Object System.Collections.Generic.List[object]</c> comes back wrapped in a PSObject, and
    /// the array-subexpression operator on that wrapper throws <c>ArgumentException: Argument types do
    /// not match</c>. That is what failed the only real nightly publish attempt at
    /// <c>return @($copied)</c>: the branch never existed before, so the publish path had never run.
    /// The same shape appears three times, so the guard covers each one, plus the call sites that have
    /// to wrap the (possibly empty) result themselves so an empty list does not become a single $null.
    /// </summary>
    [Fact]
    public void ScratchBranchPublisher_AvoidsArrayWrappingListsFromNewObject()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "memory", "Publish-MemoryProfileScratchBranch.ps1");
        var script = File.ReadAllText(scriptPath);

        script.Should().NotContain("return @($copied)",
            "@() over a New-Object list throws Argument types do not match");
        script.Should().NotContain("return @($artifacts)",
            "the heavy-artifact list has the same shape and fails right after the first one is fixed");
        script.Should().NotContain("runs = @($indexRuns)",
            "the published index would hit the same failure while writing index.json");

        script.Should().Contain("return $copied.ToArray()");
        script.Should().Contain("return $artifacts.ToArray()");
        script.Should().Contain("runs = $indexRuns.ToArray()");

        script.Should().Contain("$copiedArtifacts = @(Copy-ComparableArtifacts",
            "the caller has to wrap the result, otherwise an empty run yields a single $null entry");
        script.Should().Contain("$skippedHeavyArtifacts = @(Get-HeavyArtifacts",
            "same for the heavy-artifact list");
    }

    [Fact]
    public void MemoryProfilingGuide_DocumentsNightlyProfileScript()    {
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
