using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class SavedItemsVisualTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Light", 0, false)]
    [InlineData("Light", 30, false)]
    [InlineData("Dark", 30, false)]
    [InlineData("Light", 0, true)]
    public void History_DisabledNotice_ShowsSettingsPathOnlyWhenHistoryIsOff(string theme, int count, bool historyEnabled)
    {
        using var dpiScope = new PerMonitorDpiScope();
        using var fixture = new Fixture(theme, count, historyEnabled: historyEnabled);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        OpenHistory(window);
        foreach (var width in new[] { 1280, 400 })
        {
            Resize(window, width);
            if (historyEnabled)
            {
                Find(window, "SavedItemsHistoryDisabledNotice").Should().BeNull();
                continue;
            }
            var notice = Wait(window, "SavedItemsHistoryDisabledNotice");
            var text = string.Join(" ", notice.FindAllDescendants().Select(element => element.Name));
            text.Should().Contain("历史记录未开启").And.Contain("设置 → 常规 → 历史记录与隐私")
                .And.Contain("保存查询历史");
            notice.IsOffscreen.Should().BeFalse();
            if (count > 0)
                Wait(window, "SavedItemsList").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem))
                    .Should().NotBeNull("turning off history keeps existing records visible");
            output.WriteLine(ScreenshotHelper.CaptureWindow(window,
                $"history_disabled_{theme}_{width}_{count}_settings_path"));
        }
        Resize(window, 1280);
        Invoke(Wait(window, "SavedItemsFavoritesRailButton"));
        Find(window, "SavedItemsHistoryDisabledNotice").Should().BeNull();
        Invoke(Wait(window, "SavedItemsHistoryRailButton"));
        if (!historyEnabled) Wait(window, "SavedItemsHistoryDisabledNotice");
        else Find(window, "SavedItemsHistoryDisabledNotice").Should().BeNull();
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Minimal")]
    public void History_ReturnToMain_KeepsUnfocusedInputReadable(string theme)
    {
        using var dpiScope = new PerMonitorDpiScope();
        using var fixture = new Fixture(theme, 0);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        Resize(window, 1280);
        const string draft = "History return must keep this draft readable.\r\n返回主界面后，未获得焦点的输入文字也应清晰可见。";
        var input = Wait(window, "InputTextBox").AsTextBox();
        input.Text = draft;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (Find(window, "HistoryButton") ?? Wait(window, "SavedItemsMoreButton")).Focus();
            OpenHistory(window);
            Invoke(Wait(window, "SavedItemsReturnToTranslationButton"));
            input = Wait(window, "InputTextBox").AsTextBox();
            FlaUI.Core.Input.Mouse.MoveTo(new Point(window.BoundingRectangle.Left + 120,
                window.BoundingRectangle.Top + 20));
            Thread.Sleep(300); // Let the cached page transition and deferred theme refresh finish.
            input.Text.ReplaceLineEndings("\n").Should().Be(draft.ReplaceLineEndings("\n"));
            input.Properties.HasKeyboardFocus.ValueOrDefault.Should().BeFalse(
                "the return path must be checked before clicking the input");
            AssertReadable("unfocused");
            FlaUI.Core.Input.Mouse.MoveTo(input.GetClickablePoint());
            Thread.Sleep(100);
            AssertReadable("hovered");
            input.Click();
            Thread.Sleep(100);
            AssertReadable("focused");

            void AssertReadable(string state)
            {
                var path = ScreenshotHelper.CaptureElement(input,
                    $"history_return_{theme}_{attempt}_input_{state}");
                output.WriteLine(path);
                using var bitmap = new Bitmap(path);
                var scale = ScreenshotHelper.GetWindowDpiScale(window);
                var inset = (int)Math.Ceiling(4 * scale);
                var bottom = Math.Min(bitmap.Height - inset, (int)(72 * scale));
                var foregroundPixels = 0;
                var totalPixels = 0;
                for (var y = inset; y < bottom; y++)
                for (var x = inset; x < bitmap.Width - inset; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    var brightness = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
                    if (theme == "Dark" ? brightness > 170 : brightness < 130)
                        foregroundPixels++;
                    totalPixels++;
                }
                (foregroundPixels / (double)totalPixels).Should().BeGreaterThan(0.02,
                    $"{theme} input text must remain readable while {state} after history navigation");
            }
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Favorites_ReturnFromNarrowDetail_RestoresCardFocus(string theme)
    {
        using var dpiScope = new PerMonitorDpiScope();
        using var fixture = new Fixture(theme, 30);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        OpenHistory(window);
        Invoke(Wait(window, "SavedItemsFavoritesRailButton"));

        foreach (var width in new[] { 640, 400 })
        {
            Resize(window, width);
            for (var index = 0; index < 2; index++)
            {
                var items = Wait(window, "SavedItemsList")
                    .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
                var item = items[index];
                item.Click();
                Wait(window, "EditFavoriteButton");
                Invoke(Wait(window, "SavedItemsDetailBackButton"));
                Retry.WhileFalse(() => item.Properties.HasKeyboardFocus.ValueOrDefault,
                    TimeSpan.FromSeconds(5)).Result.Should().BeTrue(
                    "returning from details must restore keyboard focus to the selected favorite");
                item.Patterns.SelectionItem.Pattern.IsSelected.Value.Should().BeTrue();
                Thread.Sleep(200); // Capture the settled focus visual after responsive layout.
                output.WriteLine(ScreenshotHelper.CaptureWindow(window,
                    $"fluent2_{theme}_{width}_favorites_return_focus_{index}"));
            }
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Minimal")]
    public void History_ResultFavorite_PointerToggleSurvivesRefreshAndReentry(string theme)
    {
        using var dpiScope = new PerMonitorDpiScope();
        using var fixture = new Fixture(theme, 200);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        Resize(window, 1280);
        OpenHistory(window);
        Wait(window, "SavedItemsList").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem))!.Click();
        string StarName() => Wait(Wait(window, "ServiceResultItem_deepl"), "FavoriteButton").Name;
        var initialName = StarName();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var before = StarName();
            var resultRuntimeId = Wait(window, "ServiceResultItem_deepl").Properties.RuntimeId.Value;
            Wait(Wait(window, "ServiceResultItem_deepl"), "FavoriteButton").Click();
            Retry.WhileFalse(() => StarName() != before, TimeSpan.FromSeconds(8)).Result.Should().BeTrue(
                "a physical click must toggle the result favorite without leaving history");
            Wait(window, "ServiceResultItem_deepl").Properties.RuntimeId.Value.Should().Equal(resultRuntimeId,
                "a favorite change must preserve the live detail controls and their event handlers");
            Invoke(Wait(window, "SavedItemsReturnToTranslationButton"));
            OpenHistory(window);
            StarName().Should().NotBe(before, "the favorite change must persist after navigation");
        }
        StarName().Should().Be(initialName);
    }

    [Theory]
    [InlineData("Light", 200)]
    [InlineData("Dark", 5000)]
    [InlineData("Minimal", 200)]
    public void SeededHistory_ResponsiveDetailsCopyAndCompare(string theme, int count)
    {
        using var dpiScope = new PerMonitorDpiScope();
        using var fixture = new Fixture(theme, count);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        OpenHistory(window);
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_unselected_{count}"));
        var first = Wait(window, "SavedItemsList").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
        first.Should().NotBeNull();
        first!.Click();
        Wait(window, "ServiceResultItem_deepl");

        foreach (var width in new[] { 1280, 960, 640, 400 })
        {
            Resize(window, width);
            if (Find(window, "SavedItemsDetailBackButton") is { } back)
            {
                Invoke(back);
                Thread.Sleep(200);
            }
            var list = Wait(window, "SavedItemsList");
            var item = list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
            item.Should().NotBeNull();
            var before = ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_{width}_history_{count}");
            output.WriteLine(before);
            item!.Click();
            var card = Wait(window, "ServiceResultItem_deepl");
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_{width}_details"));
            var copy = Find(card, theme == "Minimal" ? "CopyButton" : "HeaderCopyButton");
            copy.Should().NotBeNull("saved result copy remains in the header");
            Invoke(copy!);
            Wait(window, "SavedItemsMessage");
            if (width == 1280)
            {
                var expandedHeight = card.BoundingRectangle.Height;
                var collapse = theme == "Minimal" ? Wait(card, "CollapseButton") : Wait(card, "ServiceResultHeader_deepl");
                Invoke(collapse);
                Retry.WhileFalse(() => card.BoundingRectangle.Height < expandedHeight / 2, TimeSpan.FromSeconds(3)).Result.Should().BeTrue();
                Invoke(Wait(card, theme == "Minimal" ? "CopyButton" : "HeaderCopyButton"));
                Wait(window, "SavedItemsMessage");
                output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_collapsed_copy"));
                Invoke(collapse);
                Invoke(Wait(window, "CompareResultsButton"));
                Thread.Sleep(300);
                var providerChoices = Wait(window, "ResultSelector").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
                providerChoices.Should().HaveCount(3);
                providerChoices.Should().OnlyContain(choice => !choice.IsOffscreen && choice.BoundingRectangle.Width > 0);
                var a = Wait(window, "ServiceResultItem_deepl").BoundingRectangle;
                var b = Wait(window, "ServiceResultItem_bing").BoundingRectangle;
                output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_{width}_compare_actual"));
                Math.Abs(a.Top - b.Top).Should().BeLessThan(3, "comparison cards must be in the same row");
                b.Left.Should().BeGreaterThan(a.Right, "comparison must use two separate columns");
                output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_{width}_compare"));
                Resize(window, 960);
                var stackedFirst = Wait(window, "ServiceResultItem_deepl").BoundingRectangle;
                var stackedSecond = Wait(window, "ServiceResultItem_bing").BoundingRectangle;
                stackedSecond.Top.Should().BeGreaterThan(stackedFirst.Bottom, "narrow details stack the same selected providers");
                output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_960_compare_stacked"));
                Resize(window, 1280);
                Invoke(Wait(window, "CompareResultsButton"));
            }
        }

        Resize(window, 1280);
        var pagedList = Wait(window, "SavedItemsList");
        for (var page = 0; page < 5; page++) ScrollHelper.ScrollToPercent(pagedList, 100);
        var realized = pagedList.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
        realized.Length.Should().BeLessThan(100, "the saved list must virtualize 200/5000 rows");
        realized.Any(item => item.Name.Contains("[0050]") || System.Text.RegularExpressions.Regex.IsMatch(item.Name, @"\[0[1-9][0-9]{2}\]")).Should().BeTrue("scrolling must fetch beyond the first two pages");
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_paged_{count}"));
        Invoke(Wait(window, "SavedItemsFavoritesRailButton"));
        var favoriteList = Wait(window, "SavedItemsList");
        favoriteList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem))!.Click();
        Invoke(Wait(window, "EditFavoriteButton"));
        var note = Wait(window, "FavoriteNoteBox").AsTextBox();
        note.Text = "Unsaved note — 中文 👩‍💻";
        Invoke(Wait(window, "SavedItemsReturnToTranslationButton"));
        var dialog = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName("保存修改？")), TimeSpan.FromSeconds(5)).Result;
        dialog.Should().NotBeNull("leaving an edited favorite must offer save/discard/cancel");
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_unsaved_favorite"));
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
        Wait(window, "FavoriteNoteBox").AsTextBox().Text.Should().Contain("Unsaved note");
        Invoke(Wait(window, "CancelFavoriteMetadataButton"));
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_favorites"));
        var favorites = Wait(window, "SavedItemsList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
        favorites[1].Click();
        var other = Wait(window, "OtherResultsExpander");
        Wait(window, "ServiceResultItem_bing");
        Find(window, "ServiceResultItem_deepl").Should().BeNull("a single-result favorite creates other providers only when expanded");
        other.Patterns.ExpandCollapse.Pattern.Expand();
        Wait(window, "ServiceResultItem_deepl");
        favorites[0].Click();
        Thread.Sleep(200);
        favorites[1].Click();
        Wait(window, "OtherResultsExpander").Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.Should().Be(ExpandCollapseState.Collapsed);
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_{theme}_single_favorite"));
        Invoke(Wait(window, "SavedItemsReturnToTranslationButton"));
        Retry.WhileNull(() => Find(window, "HistoryButton") ?? Find(window, "SavedItemsMoreButton"), TimeSpan.FromSeconds(10)).Result
            .Should().NotBeNull("the translation page exposes saved items directly or through its compact menu");
    }

    [HighContrastAcceptanceFact]
    [Trait("DesktopSetting", "HighContrast")]
    public void HighContrast_ResponsiveSavedItems()
    {
        using var contrast = new HighContrastScope();
        using var dpiScope = new PerMonitorDpiScope();
        foreach (var compact in new[] { false, true })
        {
            using var fixture = new Fixture("System", 200, compact);
            using var launcher = new AppLauncher();
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            var window = launcher.GetMainWindow();
            OpenHistory(window);
            Wait(window, "SavedItemsList").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem))!.Click();
            Wait(window, "ServiceResultItem_deepl");
            foreach (var width in new[] { 1280, 960, 640, 400 })
            {
                Resize(window, width);
                output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_HighContrast_{(compact ? "compact" : "standard")}_{width}"));
                Wait(window, "SavedItemsReturnToTranslationButton").IsEnabled.Should().BeTrue();
                Wait(window, "HeaderCopyButton").IsEnabled.Should().BeTrue();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MiniEntry_RestoresMainFromSettingsAndPreservesDraft(bool compact)
    {
        using var dpiScope = new PerMonitorDpiScope();
        using var fixture = new Fixture("Light", 200, compact, enableMini: true);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var main = launcher.GetMainWindow();
        Invoke(Wait(main, "SettingsButton"));
        Wait(main, "MainScrollViewer");
        main.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized);
        UITestHelper.SendHotkey(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT, FlaUI.Core.WindowsAPI.VirtualKeyShort.F10);
        var mini = Retry.WhileNull(() => launcher.Application.GetAllTopLevelWindows(launcher.Automation)
            .FirstOrDefault(candidate => candidate.Properties.NativeWindowHandle.Value != main.Properties.NativeWindowHandle.Value && !candidate.IsOffscreen), TimeSpan.FromSeconds(8)).Result!;
        mini.Should().NotBeNull();
        Wait(mini, "InputTextBox").AsTextBox().Text = "Fluent 2 draft 中文";
        Invoke(Wait(mini, compact ? "MiniCompactMoreButton" : "SavedItemsMoreButton"));
        output.WriteLine(ScreenshotHelper.CaptureWindowWithPopup(mini, $"fluent2_mini_{(compact ? "compact" : "standard")}_menu",
            Wait(mini, "MiniHistoryMenuItem"), Wait(mini, "MiniFavoritesMenuItem")));
        Invoke(Wait(mini, "MiniHistoryMenuItem"));
        Wait(main, "SavedItemsSearchBox");
        main.Patterns.Window.Pattern.WindowVisualState.Value.Should().Be(WindowVisualState.Normal);
        UITestHelper.SendHotkey(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT, FlaUI.Core.WindowsAPI.VirtualKeyShort.F10);
        mini = Retry.WhileNull(() => launcher.Application.GetAllTopLevelWindows(launcher.Automation)
            .FirstOrDefault(candidate => candidate.Properties.NativeWindowHandle.Value != main.Properties.NativeWindowHandle.Value && !candidate.IsOffscreen), TimeSpan.FromSeconds(8)).Result!;
        var input = Find(mini, "InputTextBox") ?? Find(mini, "SourceTextCollapsed");
        input.Should().NotBeNull();
        (input!.Patterns.Value.PatternOrDefault?.Value.Value ?? input.Name).Should().Contain("Fluent 2 draft 中文");
        Invoke(Wait(mini, compact ? "MiniCompactMoreButton" : "SavedItemsMoreButton"));
        Invoke(Wait(mini, "MiniFavoritesMenuItem"));
        Wait(main, "SavedItemsPageTitle").Properties.HelpText.Value.Should().Be("SavedItemsSection:Favorites");
        Invoke(Wait(main, "SavedItemsReturnToTranslationButton"));
        Wait(main, "SettingsButton");
    }

    [Fact]
    public void SearchAndSections_PreservePreviewSelectionAndCompactLayout()
    {
        using var dpiScope = new PerMonitorDpiScope();
        using var fixture = new Fixture("Light", 200, compact: true);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        OpenHistory(window);
        var search = Wait(window, "SavedItemsSearchBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)).AsTextBox();
        search.Text = "innovation";
        Thread.Sleep(500);
        var first = Wait(window, "SavedItemsList").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem))!;
        first.Name.Should().Contain("Bing").And.Contain("innovation");
        first.Click();
        Wait(window, "ServiceResultItem_bing");
        var selectedName = first.Name;
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, "fluent2_Light_compact_search_match"));
        Invoke(Wait(window, "SavedItemsFavoritesRailButton"));
        Wait(window, "SavedItemsSearchBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)).AsTextBox().Text.Should().BeEmpty();
        Invoke(Wait(window, "SavedItemsHistoryRailButton"));
        Wait(window, "ServiceResultItem_bing");
        Wait(window, "SavedItemsSearchBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)).AsTextBox().Text.Should().Be("innovation");
        Wait(window, "SavedItemsList").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem))!.Name.Should().Be(selectedName);
        foreach (var width in new[] { 1280, 960, 640, 400 })
        {
            Resize(window, width);
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_Light_compact_{width}"));
        }
        Invoke(Wait(window, "SavedItemsReturnToTranslationButton"));
        try { OpenHistory(window); }
        catch (System.Runtime.InteropServices.COMException)
        {
            output.WriteLine(ScreenshotHelper.CaptureScreen("fluent2_failed_search_reentry"));
            throw;
        }
        Wait(window, "ServiceResultItem_bing");
        Wait(window, "SavedItemsSearchBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)).AsTextBox().Text.Should().Be("innovation");
        Wait(window, "SavedItemsSearchBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)).AsTextBox().Text = "no-match-unique-917403";
        Wait(window, "SavedItemsEmptyStateText");
        Find(window, "RetryLoadButton").Should().BeNull("no matches are different from a load failure");
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, "fluent2_search_no_results"));
        Invoke(Wait(window, "ClearSearchButton"));
        Wait(window, "SavedItemsList");
    }

    [Fact]
    public void RepeatedDetailsAndNavigation_ReleaseOldResultControls()
    {
        using var dpiScope = new PerMonitorDpiScope();
        var previous = Environment.GetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS");
        Environment.SetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS", "1");
        try
        {
            using var fixture = new Fixture("Light", 200, richHtml: "<p>Lifecycle dictionary 中文 👩‍💻</p>");
            using var launcher = new AppLauncher();
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            var window = launcher.GetMainWindow();
            for (var cycle = 0; cycle < 12; cycle++)
            {
                output.WriteLine($"Saved-items lifecycle cycle {cycle + 1}/12");
                OpenHistory(window);
                var rows = Wait(window, "SavedItemsList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
                rows[cycle % 3].Click();
                Wait(window, "ServiceResultItem_deepl");
                if (cycle < 8)
                {
                    var browser = Wait(window, "DictWebView");
                    Retry.WhileFalse(() => browser.BoundingRectangle.Height > 20, TimeSpan.FromSeconds(5))
                        .Result.Should().BeTrue("exercise rendered WebView content before releasing its result card");
                }
                Invoke(Wait(window, "CompareResultsButton"));
                Invoke(Wait(window, "CompareResultsButton"));
                if (cycle is 3 or 7 or 11)
                {
                    Invoke(Wait(window, "SavedItemsSettingsRailButton"));
                    var scroller = Wait(window, "SettingsDetailsScrollViewer");
                    var theme = ScrollHelper.ScrollToFind(scroller, 70, () => Find(window, "AppThemeCombo"), output.WriteLine)!.AsComboBox();
                    var themeName = cycle == 3 ? "深色" : cycle == 7 ? "极简线框" : "浅色";
                    theme.Select(themeName);
                    Thread.Sleep(300);
                    Invoke(Wait(window, "BackButton"));
                    OpenHistory(window);
                    Wait(window, "ServiceResultItem_deepl");
                    output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent2_theme_switch_{cycle}"));
                }
                if (cycle < 11) Invoke(Wait(window, "SavedItemsReturnToTranslationButton"));
            }
            window.SetForeground();
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT, FlaUI.Core.WindowsAPI.VirtualKeyShort.F12);
            var path = Path.Combine(Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR")!, "saved-items-metrics.json");
            Retry.WhileFalse(() => File.Exists(path), TimeSpan.FromSeconds(10)).Result.Should().BeTrue();
            using var report = JsonDocument.Parse(File.ReadAllText(path));
            var metrics = report.RootElement;
            metrics.GetProperty("ActiveResults").GetInt32().Should().Be(3);
            metrics.GetProperty("AliveResults").GetInt32().Should().BeLessThanOrEqualTo(4, "released result views and their subscriptions must be collected");
            metrics.GetProperty("RealizedRows").GetInt32().Should().BeLessThan(100);
            output.WriteLine(File.ReadAllText(path));
        }
        finally { Environment.SetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS", previous); }
    }

    private sealed class WindowHandle(Window window)
    {
        public IntPtr Value { get; } = window.Properties.NativeWindowHandle.Value;
        public AutomationElement Root { get; set; } = window;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, WindowHandle> WindowHandles = new();

    internal static AutomationElement? Find(AutomationElement parent, string id)
    {
        if (parent is Window window)
        {
            // Cache before navigation: querying NativeWindowHandle on an old UIA
            // provider can itself fail forever after its WebView tree detaches.
            var handle = WindowHandles.GetValue(window, current => new WindowHandle(current));
            try
            {
                return FindDescendant(handle.Root, id);
            }
            catch (Exception ex) when (IsUiaTransitionError(ex))
            {
                // ElementFromHandle itself sends a cross-process request. Only
                // reacquire after invalidation, rather than on every lookup.
                handle.Root = window.Automation.FromHandle(handle.Value);
                return FindDescendant(handle.Root, id);
            }
        }
        return FindDescendant(parent, id);
    }
    private static AutomationElement? FindDescendant(AutomationElement parent, string id) =>
        parent.FindFirstDescendant(cf => cf.ByAutomationId(id)) ?? parent.FindFirstDescendant(cf => cf.ByName(id));

    private static bool IsUiaTransitionError(Exception error) => error switch
    {
        System.Runtime.InteropServices.COMException com => com.HResult is
            unchecked((int)0x8000FFFF) or unchecked((int)0x80131505) or unchecked((int)0x80040201),
        TimeoutException { InnerException: System.Runtime.InteropServices.COMException com } =>
            com.HResult == unchecked((int)0x80131505),
        _ => false
    };
    internal static AutomationElement Wait(AutomationElement parent, string id)
    {
        Exception? lastTransitionError = null;
        var found = Retry.WhileNull(() =>
        {
            try
            {
                return Find(parent, id);
            }
            catch (Exception ex) when (IsUiaTransitionError(ex))
            {
                // WinUI can invalidate an in-flight UIA traversal while Frame
                // navigation detaches the old page and its WebView providers.
                // Re-query within the existing deadline; persistent errors still fail.
                lastTransitionError = ex;
                System.Console.WriteLine($"UIA transition while waiting for {id}: 0x{ex.HResult:X8}; retrying within deadline.");
                return null;
            }
        }, TimeSpan.FromSeconds(12)).Result;
        if (found is not null) return found;
        if (lastTransitionError is not null)
            throw new InvalidOperationException($"Timed out waiting for {id}; UIA failed during navigation.", lastTransitionError);
        if (parent is Window window) ScreenshotHelper.CaptureWindow(window, $"fluent2_failed_missing_{id}");
        var tree = string.Join("\n", parent.FindAllDescendants().Select(element => $"{element.Properties.ControlType.ValueOrDefault} {element.Properties.AutomationId.ValueOrDefault}: {element.Properties.Name.ValueOrDefault}"));
        throw new InvalidOperationException($"Missing {id}\n{tree}");
    }
    internal static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.PatternOrDefault is { } invoke) invoke.Invoke();
        else if (element.Patterns.Toggle.PatternOrDefault is { } toggle) toggle.Toggle();
        else element.Click();
    }
    internal static void Resize(Window window, int width)
    {
        var scale = ScreenshotHelper.GetWindowDpiScale(window);
        if (Environment.GetEnvironmentVariable("EASYDICT_EXPECTED_DPI") is { } expectedDpi)
            scale.Should().BeApproximately(double.Parse(expectedDpi, CultureInfo.InvariantCulture) / 100, 0.01);
        var physicalWidth = (int)(width * scale) + 16;
        ScreenshotHelper.TrySetWindowPhysicalBounds(window, new Rectangle(0, 0, physicalWidth, (int)(700 * scale) + 40)).Should().BeTrue();
        window.SetForeground();
        Thread.Sleep(400);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var metrics = Find(window, "SavedItemsReturnToTranslationButton")?.Properties.ItemStatus.ValueOrDefault ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(metrics, @"PageWidth=([0-9.]+)");
            if (!match.Success) return; // Main has no saved-page diagnostic root.
            var actualWidth = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (Math.Abs(actualWidth - width) < 0.5)
            {
                if (Environment.GetEnvironmentVariable("SCREENSHOT_OUTPUT_DIR") is { } artifactDirectory)
                {
                    Directory.CreateDirectory(artifactDirectory);
                    File.AppendAllText(Path.Combine(artifactDirectory, "layout-metrics.jsonl"),
                        JsonSerializer.Serialize(new { RequestedWidth = width, ActualPageWidth = actualWidth, WindowDpi = scale, Root = metrics }) + Environment.NewLine);
                }
                return;
            }
            physicalWidth += (int)Math.Round((width - actualWidth) * scale);
            ScreenshotHelper.TrySetWindowPhysicalBounds(window, new Rectangle(0, 0, physicalWidth, (int)(700 * scale) + 40)).Should().BeTrue();
            Thread.Sleep(250);
        }
        throw new InvalidOperationException($"Could not reach {width} DIP: {Find(window, "SavedItemsReturnToTranslationButton")?.Properties.ItemStatus.ValueOrDefault}");
    }
    internal static void OpenHistory(Window window)
    {
        Wait(window, "SettingsButton");
        Thread.Sleep(200); // Let the native Frame transition finish before resizing its cached page.
        Resize(window, 1280);
        if (Find(window, "HistoryButton") is { } history) Invoke(history);
        else
        {
            Invoke(Wait(window, "SavedItemsMoreButton"));
            Invoke(Wait(window, "CompactHistoryMenuItem"));
        }
        Wait(window, "SavedItemsSearchBox");
        Resize(window, 1280);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        public Fixture(string theme, int count, bool compact = false, bool enableMini = false, string? richHtml = null, double fontScale = 1.0, bool enableFixed = false, bool historyEnabled = true)
        {
            compact |= Environment.GetEnvironmentVariable("EASYDICT_UIA_COMPACT") == "1";
            var directory = Path.Combine(Path.GetTempPath(), "Easydict.Fluent2.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
            {
                UILanguage = "zh-CN", AppTheme = theme, CompactMode = compact, ResultFontScale = fontScale, HistoryEnabled = historyEnabled, HistoryRetentionDays = 30,
                EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
                EnableShowMiniWindowHotkey = enableMini, EnableShowFixedWindowHotkey = enableFixed,
                ShowMiniWindowHotkey = "Ctrl+Alt+F10", // Isolate from a developer app using Ctrl+Alt+M.
                EnableOcrTranslateHotkey = false, EnableSilentOcrHotkey = false
            }));
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
            // Let the application create its actual schema before seeding deterministic records.
            using (var bootstrap = new AppLauncher())
            {
                bootstrap.LaunchAuto(TimeSpan.FromSeconds(45));
                OpenHistory(bootstrap.GetMainWindow());
                Wait(bootstrap.GetMainWindow(), "SavedItemsEmptyStateText");
            }
            using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "saved_items.db")}");
            connection.Open();
            using var transaction = connection.BeginTransaction();
            var sources = new[] { "Artificial intelligence is intelligence demonstrated by machines.", "气候变化与可持续发展", "Unicode 👩‍💻 café e\u0301 𐐷 👨‍👩‍👧‍👦", "This are a pen.", "OCR：合同金额与日期" };
            var providers = new[] { ("deepl", "DeepL"), ("bing", "Bing"), ("youdao", "有道词典") };
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < count; i++)
            {
                var source = $"{sources[i % sources.Length]} [{i:0000}]";
                var id = Guid.NewGuid().ToString();
                var date = now.AddDays(i < 28 ? 0 : i < 54 ? -1 : i < 100 ? -3 : -10).AddSeconds(-i).ToString("O");
                var mode = i % 5 == 3 ? "grammar" : i % 5 == 4 ? "ocr" : "translation";
                var body = i % 5 == 2 ? string.Concat(Enumerable.Repeat("👩‍💻e\u0301中文Supercalifragilisticexpialidocious", 4)) : "人工智能由机器展现，可帮助人们理解不同的语言。";
                if (mode == "grammar") body = "This is a pen.";
                var boundaries = StringInfo.ParseCombiningCharacters(body);
                var preview = boundaries.Length > 100 ? body[..boundaries[100]] + "…" : body;
                Execute("INSERT INTO saved_queries VALUES (@p0,@p1,@p2,@p3,'en','zh',CASE WHEN @p1='ocr' THEN 'ocr' ELSE 'manual' END,@p4,1,'deepl','DeepL',@p5,3)", id, mode, source, source.ToUpperInvariant(), date, preview);
                var results = new List<string>();
                for (var p = 0; p < providers.Length; p++)
                {
                    var (provider, name) = providers[p];
                    var resultId = Guid.NewGuid().ToString(); results.Add(resultId);
                    var text = mode == "grammar" ? "This is a pen." : body + (p == 1 ? " innovation" : "");
                    var payload = JsonSerializer.Serialize(new { OriginalText = source, TranslatedText = text, CorrectedText = text, ServiceName = name, TimingMs = 123 + p * 31, RawHtml = p == 0 ? richHtml : null });
                    var resultBoundaries = StringInfo.ParseCombiningCharacters(text);
                    var resultPreview = resultBoundaries.Length > 100 ? text[..resultBoundaries[100]] + "…" : text;
                    Execute("INSERT INTO saved_results VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p8,123,@p11)",
                        resultId, id, provider, name, name.ToUpperInvariant(), p, mode == "grammar" ? mode : "translation", text, text.ToUpperInvariant(), resultPreview, payload, date);
                }
                if (i < 15)
                {
                    var favorite = Guid.NewGuid().ToString();
                    Execute("INSERT INTO favorites VALUES (@p0,@p1,@p2,'演示备注','演示备注',@p3,@p4,@p4)", favorite, id, i % 2 == 0 ? DBNull.Value : results[1], i == 0 ? 1 : 0, date);
                    Execute("INSERT INTO favorite_tags VALUES (@p0,'工作','工作')", favorite);
                    Execute("INSERT INTO favorite_tags VALUES (@p0,'AI','AI')", favorite);
                }
            }
            transaction.Commit();

            void Execute(string sql, params object[] values)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
                for (var j = 0; j < values.Length; j++) command.Parameters.AddWithValue($"@p{j}", values[j]);
                command.ExecuteNonQuery();
            }
        }
        public void Dispose() => Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", _previous);
    }
}

public sealed class HighContrastAcceptanceFactAttribute : FactAttribute
{
    public HighContrastAcceptanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EASYDICT_UIA_CONTRAST_ACCEPTANCE") != "1")
            Skip = "Requires EASYDICT_UIA_CONTRAST_ACCEPTANCE=1 on an isolated interactive desktop.";
    }
}
