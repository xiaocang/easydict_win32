using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Configuration")]
public sealed class UiThreadHotspotDiagnosticsTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string ServiceResultItemPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml.cs");
    private static readonly string MinimalServiceResultItemPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "MinimalServiceResultItem.xaml.cs");
    private static readonly string ServiceResultViewHostPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultViewHost.cs");
    private static readonly string StreamingTextCoalescerPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Services", "StreamingTextCoalescer.cs");
    private static readonly string DiagnosticsPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Services", "UiThreadHotspotDiagnostics.cs");
    private static readonly string ProbeScriptPath = Path.Combine(ProjectRoot, "scripts", "perf", "Invoke-UiThreadHotspotProbe.ps1");
    private static readonly string UiHotspotProbeTestPath = Path.Combine(ProjectRoot, "tests", "Easydict.UIAutomation.Tests", "Tests", "UiThreadHotspotProbeTests.cs");

    [Fact]
    public void UiThreadHotspotDiagnostics_ExposesScriptParseableOptInMarkers()
    {
        var diagnostics = File.ReadAllText(DiagnosticsPath);

        diagnostics.Should().Contain("EASYDICT_DEBUG_UI_THREAD_HOTSPOTS",
            "hotspot probes must be opt-in so normal UI updates do not write diagnostics");
        diagnostics.Should().Contain("EASYDICT_DEBUG_UI_THREAD_HOTSPOT_THRESHOLD_MS",
            "scripts need a tunable threshold for local and CI machines");
        diagnostics.Should().Contain("EASYDICT_UI_THREAD_HOTSPOT_LOG_PATH",
            "PowerShell probes need a file sink instead of relying on debugger output");
        diagnostics.Should().Contain("[UIHotspot] kind=duration ");
        diagnostics.Should().Contain("op={_operation}",
            "duration events should stay machine-parseable");
        diagnostics.Should().Contain("[UIHotspot] kind=count op=",
            "count events should stay machine-parseable");
        diagnostics.Should().Contain("ConcurrentQueue<string>",
            "file logging should be off the UI thread when diagnostics are enabled");
    }

    [Fact]
    public void ServiceResultItems_CoalesceExplicitRefreshesWithPropertyChangedUpdates()
    {
        var rich = File.ReadAllText(ServiceResultItemPath);
        var minimal = File.ReadAllText(MinimalServiceResultItemPath);

        rich.Should().Contain("private void QueueUpdateUI()");
        rich.Should().Contain("_renderedUpdateUIVersion == _updateUIRequestVersion",
            "direct refreshes should satisfy already-queued property-change updates");
        rich.Should().Contain("public void RefreshDemotionState() => QueueUpdateUI();",
            "demotion refreshes should coalesce with result property changes");

        minimal.Should().Contain("private void QueueUpdateUI()");
        minimal.Should().Contain("_renderedUpdateUIVersion == _updateUIRequestVersion");
        minimal.Should().Contain("public void RefreshDemotionState() => QueueUpdateUI();");
    }

    [Fact]
    public void ServiceResultItem_CachesExpensiveRenderedChildrenAndWebViewNavigation()
    {
        var content = File.ReadAllText(ServiceResultItemPath);

        content.Should().Contain("_phoneticsRenderValid",
            "phonetic badges should not be rebuilt on every full UpdateUI for the same result");
        content.Should().Contain("IsSamePhoneticDeduplication",
            "phonetic dedupe updates should avoid re-rendering when the effective set is unchanged");
        content.Should().Contain("_dictionaryRenderValid",
            "dictionary rows should not be rebuilt on every full UpdateUI for the same result");
        content.Should().Contain("IsCurrentDictionaryWebViewHtml",
            "raw HTML dictionary output should not repeatedly navigate WebView2 for the same payload");
        content.Should().Contain("_dictWebViewRenderedHtmlReady",
            "the plain-text fallback should remain visible until WebView2 has a measured height");
        content.Should().Contain("DispatcherQueuePriority.Low, LoadServiceIconIfCurrent",
            "service icon decoding should be deferred out of the synchronous UpdateUI path");
        content.Should().Contain("ServiceResultItem.LoadServiceIcon",
            "icon loading should have its own hotspot probe");
    }

    [Fact]
    public void ResultHostAndStreamingCoalescer_EmitHotspotProbeMarkers()
    {
        var host = File.ReadAllText(ServiceResultViewHostPath);
        var coalescer = File.ReadAllText(StreamingTextCoalescerPath);

        host.Should().Contain("UiThreadHotspotDiagnostics.Measure(\"ServiceResultViewHost.Reorder\")");
        host.Should().Contain("UiThreadHotspotDiagnostics.Measure(\"ServiceResultViewHost.UpdateStickyHeaders\")");
        host.Should().Contain("UiThreadHotspotDiagnostics.Measure(\"ServiceResultViewHost.UpdatePhoneticDeduplication\")");
        host.Should().Contain("UiThreadHotspotDiagnostics.LogCounter(");

        coalescer.Should().Contain("UiThreadHotspotDiagnostics.Measure(\"StreamingTextCoalescer.OnTick\")");
        coalescer.Should().Contain("UiThreadHotspotDiagnostics.LogCounter(\"StreamingTextCoalescer.Drain\"");
    }

    [Fact]
    public void UiThreadHotspotProbeScript_RunsSimpleWinUiScenarioAndSummarizesMarkers()
    {
        var script = File.ReadAllText(ProbeScriptPath);

        script.Should().Contain("EASYDICT_DEBUG_UI_THREAD_HOTSPOTS");
        script.Should().Contain("EASYDICT_UI_THREAD_HOTSPOT_LOG_PATH");
        script.Should().Contain("Easydict.UIAutomation.Tests.csproj",
            "the probe should exercise an actual WinUI surface through the existing UIA tests");
        script.Should().Contain("UiThreadHotspotProbeTests.MainSettingsModesAndFloatingWindows_ShouldEmitUiHotspots",
            "the default scenario should cover more than a single translation operation");
        script.Should().Contain("kind=duration");
        script.Should().Contain("ui-hotspot-summary");
        script.Should().Contain("Group-Object operation");
    }

    [Fact]
    public void UiThreadHotspotProbeTest_CoversCommonInteractiveSurfaces()
    {
        var test = File.ReadAllText(UiHotspotProbeTestPath);

        test.Should().Contain("ExerciseQuickTranslate(window");
        test.Should().Contain("SwitchToLongDocMode(window)");
        test.Should().Contain("ExerciseLongDocControls(window)");
        test.Should().Contain("OpenSettingsExerciseTabsAndReturn(window)");
        test.Should().Contain("ExerciseFloatingWindows(window)");
        test.Should().Contain("EASYDICT_UI_HOTSPOT_RUN_TRANSLATION",
            "the broad probe should allow skipping real translation when a local environment needs deterministic UI-only coverage");
        test.Should().Contain("SettingsTab_");
        test.Should().Contain("VirtualKeyShort.KEY_M");
        test.Should().Contain("VirtualKeyShort.KEY_F");
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
