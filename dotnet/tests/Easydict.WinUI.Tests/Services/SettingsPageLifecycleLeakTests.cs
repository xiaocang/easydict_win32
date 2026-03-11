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
    public void SettingsPage_UsesPageLifetimeCancellationForDeferredWork()
    {
        var content = File.ReadAllText(SettingsPagePath);
        content.Should().Contain("private readonly CancellationTokenSource _lifetimeCts = new();",
            "SettingsPage should own a page-scoped cancellation source");
        content.Should().Contain("UpdateCacheStatusAsync(CancellationToken cancellationToken = default)",
            "Deferred cache status update should be cancelable");
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
