using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class SavedItemsTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public SavedItemsTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void MainHistoryEntry_OpensHistorySection()
    {
        var window = _launcher.GetMainWindow();
        var transform = window.Patterns.Transform.PatternOrDefault;
        transform.Should().NotBeNull("the Main window must support wide-layout verification");
        transform!.Move(40, 40);
        transform.Resize(1000, 700);
        window.SetForeground();
        Thread.Sleep(500);
        var historyButton = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "HistoryButton"),
            TimeSpan.FromSeconds(15)).Result;
        historyButton.Should().NotBeNull("the Main window exposes the History entry");
        UITestHelper.FindByAutomationIdOrName(window, "SavedItemsMoreButton")
            .Should().BeNull("wide Main uses direct History and Favorites buttons");
        if (historyButton!.Patterns.Invoke.PatternOrDefault is { } invoke)
            invoke.Invoke();
        else
            historyButton.Click();

        Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SavedItemsSearchBox"),
            TimeSpan.FromSeconds(15)).Result.Should().NotBeNull();
        var title = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SavedItemsPageTitle"),
            TimeSpan.FromSeconds(10)).Result;
        title!.Properties.HelpText.ValueOrDefault.Should().Be("SavedItemsSection:History");
        UITestHelper.FindByAutomationIdOrName(window, "SavedItemsFilterButton").Should().NotBeNull();
        Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SavedItemsEmptyStateText"),
            TimeSpan.FromSeconds(10)).Result.Should().NotBeNull("an empty database shows the native empty state");
        UITestHelper.FindByAutomationIdOrName(window, "SavedItemsHistoryKindTabs")
            .Should().NotBeNull("wide history uses the category tabs");
        UITestHelper.FindByAutomationIdOrName(window, "SavedItemsBackButton")
            .Should().NotBeNull("saved-items navigation exposes a deterministic Back action");
        var screenshot = ScreenshotHelper.CaptureWindow(window, "saved_items_history_empty_fluent2");
        File.Exists(screenshot).Should().BeTrue("the History surface should be capturable for visual review");
        _output.WriteLine($"History screenshot saved: {screenshot}");
    }

    [Fact]
    public void MainFavoritesEntry_OpensFavoritesSection()
    {
        var window = _launcher.GetMainWindow();
        var transform = window.Patterns.Transform.PatternOrDefault;
        transform.Should().NotBeNull("the Main window must support wide-layout verification");
        transform!.Move(40, 40);
        transform.Resize(1000, 700);
        window.SetForeground();
        Thread.Sleep(500);
        var favoritesButton = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "FavoritesButton"),
            TimeSpan.FromSeconds(15)).Result;
        favoritesButton.Should().NotBeNull("the Main window exposes the Favorites entry");
        if (favoritesButton!.Patterns.Invoke.PatternOrDefault is { } invoke)
            invoke.Invoke();
        else
            favoritesButton.Click();

        var title = Retry.WhileNull(
            () =>
            {
                var candidate = UITestHelper.FindByAutomationIdOrName(window, "SavedItemsPageTitle");
                return candidate?.Properties.HelpText.ValueOrDefault == "SavedItemsSection:Favorites" ? candidate : null;
            },
            TimeSpan.FromSeconds(15)).Result;
        title.Should().NotBeNull("the Favorites entry opens the Favorites-specific saved-items section");
        _output.WriteLine("The Main Favorites entry opened the native Favorites section.");
        var savedItemsList = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SavedItemsList"),
            TimeSpan.FromSeconds(3)).Result;
        var firstItem = savedItemsList?.FindFirstDescendant(
            cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
        if (firstItem is not null)
        {
            firstItem.Click();
            Retry.WhileNull(
                () => UITestHelper.FindByAutomationIdOrName(window, "SavedItemsDetailSourceText"),
                TimeSpan.FromSeconds(10)).Result.Should().NotBeNull();
        }

        var screenshot = ScreenshotHelper.CaptureWindow(window, "saved_items_favorites_fluent2");
        File.Exists(screenshot).Should().BeTrue("the Favorites surface should be capturable for visual review");
        _output.WriteLine($"Favorites screenshot saved: {screenshot}");
    }

    [Fact]
    public void NarrowHistory_UsesCompactSelectorAndSinglePaneLayout()
    {
        var window = _launcher.GetMainWindow();
        var transform = window.Patterns.Transform.PatternOrDefault;
        transform.Should().NotBeNull("the Main window must support responsive resize verification");
        transform!.Move(40, 40);
        transform.Resize(400, 800);
        window.SetForeground();
        Thread.Sleep(500);

        UITestHelper.FindByAutomationIdOrName(window, "HistoryButton")
            .Should().BeNull("compact Main moves History into the More menu");
        UITestHelper.FindByAutomationIdOrName(window, "FavoritesButton")
            .Should().BeNull("compact Main moves Favorites into the More menu");
        var moreButton = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SavedItemsMoreButton"),
            TimeSpan.FromSeconds(10)).Result;
        moreButton.Should().NotBeNull("compact Main exposes the SavedItems More menu");
        if (moreButton!.Patterns.Invoke.PatternOrDefault is { } moreInvoke)
            moreInvoke.Invoke();
        else
            moreButton.Click();

        var compactHistoryItem = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "CompactHistoryMenuItem"),
            TimeSpan.FromSeconds(5)).Result;
        var compactFavoritesItem = UITestHelper.FindByAutomationIdOrName(window, "CompactFavoritesMenuItem");
        compactHistoryItem.Should().NotBeNull();
        compactFavoritesItem.Should().NotBeNull("the compact menu exposes both SavedItems destinations");
        if (compactHistoryItem!.Patterns.Invoke.PatternOrDefault is { } historyInvoke)
            historyInvoke.Invoke();
        else
            compactHistoryItem.Click();

        Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SavedItemsHistoryKindCombo"),
            TimeSpan.FromSeconds(10)).Result.Should().NotBeNull("sub-720-DIP history uses the compact selector");
        UITestHelper.FindByAutomationIdOrName(window, "SavedItemsDetail")
            .Should().BeNull("the detail pane stays collapsed until a narrow-list item is opened");
        var screenshot = ScreenshotHelper.CaptureWindow(window, "saved_items_history_compact_fluent2");
        File.Exists(screenshot).Should().BeTrue("the compact History surface should be capturable for visual review");
        _output.WriteLine($"Compact History screenshot saved: {screenshot}");
        var returnButton = UITestHelper.FindByAutomationIdOrName(window, "SavedItemsReturnToTranslationButton");
        returnButton.Should().NotBeNull("the labeled return action remains visible when the navigation rail is hidden");
        if (returnButton!.Patterns.Invoke.PatternOrDefault is { } returnInvoke)
            returnInvoke.Invoke();
        else
            returnButton.Click();
        Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SavedItemsMoreButton"),
            TimeSpan.FromSeconds(10)).Result.Should().NotBeNull("returning restores the translation page");
    }

    [Fact]
    public void MiniOpensSavedItemsInMainWindowAndFixedHasNoSavedItemsNavigation()
    {
        var mainWindow = _launcher.GetMainWindow();
        mainWindow.SetForeground();

        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);
        Thread.Sleep(2000);
        var miniWindow = UITestHelper.FindSecondaryWindow(
            _launcher.Application,
            _launcher.Automation,
            "Mini",
            _output);
        miniWindow.Should().NotBeNull();
        UITestHelper.FindByAutomationIdOrName(miniWindow!, "HistoryButton").Should().BeNull();
        UITestHelper.FindByAutomationIdOrName(miniWindow!, "FavoritesButton").Should().BeNull();
        UITestHelper.FindByAutomationIdOrName(miniWindow!, "SavedItemsList").Should().BeNull();
        var miniMore = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(miniWindow!, "SavedItemsMoreButton"),
            TimeSpan.FromSeconds(10)).Result;
        miniMore.Should().NotBeNull("Mini exposes saved-items navigation through its More menu");
        if (miniMore!.Patterns.Invoke.PatternOrDefault is { } miniMoreInvoke)
            miniMoreInvoke.Invoke();
        else
            miniMore.Click();

        var miniHistory = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(miniWindow!, "MiniHistoryMenuItem"),
            TimeSpan.FromSeconds(5)).Result;
        miniHistory.Should().NotBeNull("Mini's More menu contains History");
        if (miniHistory!.Patterns.Invoke.PatternOrDefault is { } miniHistoryInvoke)
            miniHistoryInvoke.Invoke();
        else
            miniHistory.Click();
        Retry.WhileNull(
            () =>
            {
                var title = UITestHelper.FindByAutomationIdOrName(mainWindow, "SavedItemsPageTitle");
                return title?.Properties.HelpText.ValueOrDefault == "SavedItemsSection:History" ? title : null;
            },
            TimeSpan.FromSeconds(15)).Result.Should().NotBeNull(
                "Mini history restores and activates the main window's History section");

        mainWindow.SetForeground();
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_F);
        Thread.Sleep(2000);
        var fixedWindow = UITestHelper.FindSecondaryWindow(
            _launcher.Application,
            _launcher.Automation,
            "Fixed",
            _output);
        fixedWindow.Should().NotBeNull();
        UITestHelper.FindByAutomationIdOrName(fixedWindow!, "HistoryButton").Should().BeNull();
        UITestHelper.FindByAutomationIdOrName(fixedWindow!, "FavoritesButton").Should().BeNull();
        UITestHelper.FindByAutomationIdOrName(fixedWindow!, "SavedItemsList").Should().BeNull();
    }

    [Fact]
    public void HistorySettings_PersistImmediatelyAcrossRestart()
    {
        var controls = OpenHistorySettings(_launcher);
        var retentionPattern = controls.Retention.Patterns.RangeValue.PatternOrDefault;
        retentionPattern.Should().NotBeNull();
        retentionPattern!.SetValue(7);
        Thread.Sleep(300);

        var togglePattern = controls.Toggle.Patterns.Toggle.PatternOrDefault;
        togglePattern.Should().NotBeNull();
        if (togglePattern!.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On)
            togglePattern.Toggle();

        Retry.WhileFalse(
            () => !controls.Retention.IsEnabled,
            TimeSpan.FromSeconds(5)).Result.Should().BeTrue("turning history off disables retention immediately");
        controls.Clear.IsEnabled.Should().BeTrue("Clear history remains available while recording is disabled");

        _launcher.Dispose();
        using var relaunched = new AppLauncher();
        relaunched.LaunchAuto(TimeSpan.FromSeconds(45));
        var reloaded = OpenHistorySettings(relaunched);
        var reloadedToggle = reloaded.Toggle.Patterns.Toggle.PatternOrDefault;
        var reloadedRetention = reloaded.Retention.Patterns.RangeValue.PatternOrDefault;

        reloadedToggle.Should().NotBeNull();
        reloadedToggle!.ToggleState.Value.Should().Be(FlaUI.Core.Definitions.ToggleState.Off);
        reloadedRetention.Should().NotBeNull();
        reloadedRetention!.Value.Value.Should().Be(7);
        reloaded.Retention.IsEnabled.Should().BeFalse();
        reloaded.Clear.IsEnabled.Should().BeTrue();

        reloadedToggle.Toggle();
        Retry.WhileTrue(
            () => !reloaded.Retention.IsEnabled,
            TimeSpan.FromSeconds(5));
        reloadedRetention.SetValue(30);
    }

    private HistorySettingsControls OpenHistorySettings(AppLauncher launcher)
    {
        var window = launcher.GetMainWindow();
        window.SetForeground();
        var settingsButton = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SettingsButton"),
            TimeSpan.FromSeconds(15)).Result;
        settingsButton.Should().NotBeNull();
        if (settingsButton!.Patterns.Invoke.PatternOrDefault is { } invoke)
            invoke.Invoke();
        else
            settingsButton.Click();

        var scrollViewer = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "MainScrollViewer"),
            TimeSpan.FromSeconds(15)).Result;
        scrollViewer.Should().NotBeNull();
        var retention = ScrollHelper.ScrollToFind(
            scrollViewer!,
            65,
            () => UITestHelper.FindByAutomationIdOrName(window, "HistoryRetentionDaysBox"),
            _output.WriteLine);
        retention.Should().NotBeNull();
        var toggle = UITestHelper.FindByAutomationIdOrName(window, "HistoryEnabledToggle");
        var clear = UITestHelper.FindByAutomationIdOrName(window, "ClearHistoryButton");
        toggle.Should().NotBeNull();
        clear.Should().NotBeNull();
        return new HistorySettingsControls(toggle!, retention!, clear!);
    }

    private sealed record HistorySettingsControls(
        AutomationElement Toggle,
        AutomationElement Retention,
        AutomationElement Clear);
    public void Dispose() => _launcher.Dispose();
}
