using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Static regression checks for SettingsPage lifecycle teardown and handler cleanup.
/// These checks prevent event-chain regressions that can retain SettingsPage instances.
/// </summary>
[Trait("Category", "Configuration")]
public class SettingsPageLifecycleLeakTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string SettingsPagePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");

    [Fact]
    public void SettingsPage_SubscribesToUnloaded()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("this.Unloaded += OnPageUnloaded;",
            "SettingsPage should subscribe to Unloaded to run teardown");
    }

    [Fact]
    public void SettingsPage_HasTeardownMethod()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("private void TeardownOnUnload()",
            "SettingsPage should centralize unload cleanup in a teardown method");
    }

    [Fact]
    public void SettingsPage_UnregistersHandlersDuringTeardown()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("UnregisterChangeHandlers();",
            "Teardown should unregister UI event handlers");
        content.Should().Contain("UnregisterLanguageCheckboxHandlers();",
            "Teardown should unregister language checkbox handlers");
    }

    [Fact]
    public void SettingsPage_UnsubscribesServiceItemPropertyChanged()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("item.PropertyChanged -= OnServiceItemPropertyChanged;",
            "ServiceCheckItem handlers should be detached to avoid retaining the page");
    }

    [Fact]
    public void SettingsPage_UnsubscribesLanguageItemPropertyChanged()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("item.PropertyChanged -= OnLanguageCheckboxChanged;",
            "Language checkbox handlers should be detached before replacing/clearing items");
    }

    [Fact]
    public void SettingsPage_ClearsItemsSourcesOnUnload()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("MainWindowServicesPanel.ItemsSource = null;",
            "Main window services panel should release old visual tree references");
        content.Should().Contain("MiniWindowServicesPanel.ItemsSource = null;",
            "Mini window services panel should release old visual tree references");
        content.Should().Contain("FixedWindowServicesPanel.ItemsSource = null;",
            "Fixed window services panel should release old visual tree references");
        content.Should().Contain("LanguageCheckboxGrid.ItemsSource = null;",
            "Language checkbox grid should release old item references");
    }

    [Fact]
    public void SettingsPage_ClearsDynamicMdxUiOnUnload()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("ImportedMdxConfigPanel.Children.Clear();",
            "Imported MDX expanders should be detached during teardown");
        content.Should().Contain("_mdxCredentialFields.Clear();",
            "Credential field caches should release TextBox and PasswordBox references during teardown");
    }

    [Fact]
    public void SettingsPage_UsesNamedNavigationPointerHandlers()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("DetachNavigationIconHandlers();",
            "Navigation icon handlers should be detached before clearing icon visuals");
        content.Should().Contain("icon.PointerEntered += OnNavIconPointerEntered;",
            "PointerEntered should use a named handler so teardown and heap analysis stay auditable");
        content.Should().Contain("icon.PointerExited += OnNavIconPointerExited;",
            "PointerExited should use a named handler so teardown and heap analysis stay auditable");
    }

    [Fact]
    public void SettingsPage_TracksInstancesWithWeakReferencesForDebugSessions()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("List<WeakReference<SettingsPage>>",
            "DEBUG sessions should weakly track SettingsPage instances to distinguish retention from process cache growth");
        content.Should().Contain("RegisterDebugInstance(this);",
            "each SettingsPage instance should register itself with the weak-reference tracker");
    }

    [Fact]
    public void SettingsPage_UsesPageLifetimeCancellationForDeferredWork()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("private readonly CancellationTokenSource _lifetimeCts = new();",
            "SettingsPage should own a page-scoped cancellation source");
        content.Should().Contain("WeakReference<SettingsPage> pageReference",
            "Deferred cache status updates should avoid strongly holding the page instance across async work");
        content.Should().Contain("CancellationToken cancellationToken = default",
            "Deferred cache status update should remain cancelable");
    }

    [Fact]
    public void SettingsPage_UsesDebugFlagToSkipDeferredIoDuringProfiling()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("EASYDICT_DEBUG_DISABLE_SETTINGS_DEFERRED_IO",
            "profiling should be able to isolate ONNX and SQLite warm-up costs from page lifecycle behavior");
        content.Should().Contain("Deferred I/O: skipped by EASYDICT_DEBUG_DISABLE_SETTINGS_DEFERRED_IO",
            "skip decisions should be explicit in DEBUG output");
    }

    [Fact]
    public void SettingsPage_SchedulesShortAndLongDelayedLifetimeChecks()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("OnPageUnloaded delayed full GC (250ms)",
            "profiling should keep the short delayed GC check for quick transient-retention signals");
        content.Should().Contain("OnPageUnloaded delayed full GC (1000ms)",
            "profiling should add a longer delayed GC check to separate async tails from real leaks");
    }

    [Fact]
    public void SettingsPage_DelayedLifetimeChecksTrackSpecificUnloadedInstance()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("var pageReference = new WeakReference<SettingsPage>(this);",
            "delayed lifetime checks should hold a weak reference to the specific unloaded SettingsPage instance");
        content.Should().Contain("trackedInstanceAliveAfterDelayedFullGC=",
            "delayed GC logs should report whether the specific unloaded page is still alive");
        content.Should().Contain("globalLiveInstances=",
            "delayed GC logs should still include the global live instance count for context");
    }

    [Fact]
    public void SettingsPage_LifetimeLogsExposeExplicitGlobalFieldNames()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("globalLiveInstances=",
            "lifetime logs should explicitly label global instance counts so they are not confused with instance-level delayed checks");
        content.Should().Contain("globalSurvivorsAfterLastTrackedFullGC=",
            "ordinary lifetime logs should explicitly label the last tracked full-GC survivor count");
        content.Should().Contain("globalSurvivorsAfterDelayedFullGC=",
            "delayed full-GC logs should expose the global survivor count under an explicit global name");
    }

    [Fact]
    public void SettingsPage_TracksDeferredIoStateAcrossLifecycle()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("[SettingsPage][DeferredIO]",
            "deferred I/O state transitions should be logged explicitly during memory profiling");
        content.Should().Contain("deferredIo={_debugDeferredIoState}",
            "object-count output should carry the current deferred I/O state");
        content.Should().Contain("cache-dispatched",
            "deferred I/O logging should distinguish dispatcher scheduling from actual cache execution");
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
