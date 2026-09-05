using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using static Easydict.UIAutomation.Tests.Tests.SavedItemsVisualTests;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class FluentWindowLayoutTests(ITestOutputHelper output)
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [HighContrastAcceptanceFact]
    [Trait("DesktopSetting", "HighContrast")]
    public void HighContrast_MainAndSettingsLayouts()
    {
        using var contrast = new HighContrastScope();
        Main_ReflowsExistingInputAndSettingsCategories("System", false);
        Main_ReflowsExistingInputAndSettingsCategories("System", true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fixed_ReopensDraftAndStaysVisibleWhenUnfocused(bool compact)
    {
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new Fixture("Light", 0, compact: compact, enableFixed: true);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var main = launcher.GetMainWindow();
        Thread.Sleep(1000);
        void OpenFixed()
        {
            using (FlaUI.Core.Input.Keyboard.Pressing(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT, FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT))
                FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_F);
        }
        OpenFixed();
        Thread.Sleep(700);
        var window = UITestHelper.FindSecondaryWindow(launcher.Application, launcher.Automation, "Fixed", output);
        window.Should().NotBeNull();
        var input = Wait(window!, "InputTextBox").AsTextBox();
        input.Patterns.Value.Pattern.SetValue("Fixed draft 中文\nSecond line");
        main.SetForeground();
        Thread.Sleep(600);
        var handle = window!.Properties.NativeWindowHandle.Value;
        IsWindowVisible(handle).Should().BeTrue("Fixed must not hide on blur");
        window.SetForeground();
        Wait(window, "SourceLangCombo").IsOffscreen.Should().BeFalse();
        Wait(window, "TargetLangCombo").IsOffscreen.Should().BeFalse();
        Find(window, "SavedItemsMoreButton").Should().BeNull("Fixed does not add saved-item navigation");
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_fixed_{compact}_draft"));
        // Exercise the user-facing hide operation; WindowPattern.Close destroys
        // the native window and intentionally releases its resources.
        OpenFixed();
        Thread.Sleep(400);
        FlaUI.Core.Tools.Retry.WhileFalse(() => !IsWindowVisible(handle), TimeSpan.FromSeconds(3))
            .Result.Should().BeTrue("the toggle hotkey hides Fixed without destroying it");
        OpenFixed();
        Thread.Sleep(700);
        window = UITestHelper.FindSecondaryWindow(launcher.Application, launcher.Automation, "Fixed", output);
        Wait(window!, "InputTextBox").AsTextBox().Text.Should().Contain("Fixed draft 中文").And.Contain("Second line");
        output.WriteLine(ScreenshotHelper.CaptureWindow(window!, $"fluent_windows_fixed_{compact}_reopened"));
    }

    [Theory]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.4)]
    public void RichDictionary_GrowsAfterDelayedContentAndCopiesCollapsed(double fontScale)
    {
        const string html = """
            <h2>词典 Dictionary 👩‍💻 é</h2><p>Supercalifragilisticexpialidocious — definition and example.</p>
            <div id="late"></div><script>setTimeout(() => {
            const img = document.createElement('img'); img.width=200; img.height=240;
            img.src='data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" width="200" height="240"%3E%3Crect width="200" height="240" fill="%230078D4"/%3E%3C/svg%3E';
            document.getElementById('late').appendChild(img); }, 3000);</script>
            """;
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new Fixture("Light", 200, richHtml: html, fontScale: fontScale);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        OpenHistory(window);
        Wait(window, "SavedItemsList").FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))!.Click();
        var card = Wait(window, "ServiceResultItem_deepl");
        var browser = Wait(card, "DictWebView");
        FlaUI.Core.Tools.Retry.WhileFalse(() => browser.BoundingRectangle.Height > 80,
            TimeSpan.FromSeconds(4)).Result.Should().BeTrue("initial dictionary text must finish sizing first");
        Thread.Sleep(500);
        var before = browser.BoundingRectangle.Height;
        FlaUI.Core.Tools.Retry.WhileFalse(() => browser.BoundingRectangle.Height > before + 100,
            TimeSpan.FromSeconds(8)).Result.Should().BeTrue("delayed images must resize the native dictionary body");
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_dictionary_{fontScale}_delayed"));
        Invoke(Wait(card, "ServiceResultHeader_deepl"));
        Invoke(Wait(card, "HeaderCopyButton"));
        Wait(window, "SavedItemsMessage");
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_dictionary_{fontScale}_collapsed_copy"));
    }

    [Fact]
    public void RichDictionary_UnavailableRuntimeShowsNativeFallback()
    {
        var previous = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new Fixture("Light", 200, richHtml: "<p>Dictionary fixture</p>");
        try
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
                Path.Combine(Path.GetTempPath(), "Easydict.MissingRuntime", Guid.NewGuid().ToString("N")));
            using var launcher = new AppLauncher();
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            var window = launcher.GetMainWindow();
            OpenHistory(window);
            Wait(window, "SavedItemsList").FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))!.Click();
            var card = Wait(window, "ServiceResultItem_deepl");
            Wait(card, "ResultFeedback").IsOffscreen.Should().BeFalse();
            Wait(card, "ResultText").IsOffscreen.Should().BeFalse("plain text remains readable when WebView initialization fails");
            Invoke(Wait(card, "HeaderCopyButton"));
            Wait(window, "SavedItemsMessage");
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, "fluent_windows_dictionary_native_fallback"));
        }
        finally { Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", previous); }
    }

    [Theory]
    [InlineData("Light", false)]
    [InlineData("Dark", true)]
    [InlineData("Minimal", false)]
    public void Main_ReflowsExistingInputAndSettingsCategories(string theme, bool compact)
    {
        using var dpi = new PerMonitorDpiScope();
        var previous = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        var directory = Path.Combine(Path.GetTempPath(), "Easydict.FluentWindows", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            AppTheme = theme, UILanguage = "zh-CN", CompactMode = compact,
            EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
            EnableShowMiniWindowHotkey = false, EnableShowFixedWindowHotkey = false
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
        try
        {
            using var launcher = new AppLauncher();
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            var window = launcher.GetMainWindow();
            var input = UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(12));
            input.Should().NotBeNull();
            const string draft = "Fluent 2 中文 👩‍💻 e\u0301 draft";
            input!.Patterns.Value.Pattern.SetValue(draft);
            foreach (var width in new[] { 1600, 1280, 960, 959, 640, 400, 1600 })
            {
                ResizeMain(window, width);
                input.Text.Should().Be(draft, "resizing must retain the draft");
                var left = Wait(window, "QuickInputScrollViewer").BoundingRectangle;
                var right = Wait(window, "QuickResultsScrollViewer").BoundingRectangle;
                if (width >= 960)
                {
                    right.Left.Should().BeGreaterThan(left.Right);
                    Math.Abs(left.Top - right.Top).Should().BeLessThan(4);
                    var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
                    var contentLeft = Wait(window, "QuickTranslateContent").BoundingRectangle.Left;
                    ((left.Left - contentLeft) / dpiScale).Should()
                        .BeApproximately(24, 2, "the capped translation layout keeps its page padding at the left edge");
                    Math.Abs(left.Width / dpiScale - 360).Should().BeLessThan(2);
                }
                else right.Top.Should().BeGreaterThan(left.Bottom);
                Wait(window, "SourceLangCombo").IsOffscreen.Should().BeFalse();
                Wait(window, "TargetLangCombo").IsOffscreen.Should().BeFalse();
                output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_{theme}_{compact}_{width}_main"));
            }
            Invoke(Wait(window, "ModeMenuButton"));
            Invoke(Wait(window, "ModeLongDocItem"));
            Wait(window, "LongDocSourceLangCombo").IsOffscreen.Should().BeFalse();
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_{theme}_longdoc_default"));
            Wait(window, "LongDocMoreOptions").Patterns.ExpandCollapse.Pattern.Expand();
            var pageRange = Wait(window, "LongDocPageRangeBox").AsTextBox();
            pageRange.Patterns.Value.Pattern.SetValue("1-3");
            Resize(window, 400);
            pageRange.Text.Should().Be("1-3");
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_{theme}_longdoc_narrow_options"));
            Invoke(Wait(window, "ModeMenuButton"));
            Invoke(Wait(window, "ModeTranslationItem"));
            input.Text.Should().Be(draft);
            ResizeMain(window, 1280);
            Invoke(Wait(window, "SettingsButton"));
            Thread.Sleep(1200);
            Wait(window, "SettingsTab_General").IsOffscreen.Should().BeFalse();
            var scale = ScreenshotHelper.GetWindowDpiScale(window);
            var general = Wait(window, "SettingsTab_General").BoundingRectangle;
            var services = Wait(window, "SettingsTab_Services").BoundingRectangle;
            Math.Abs(general.Left - services.Left).Should().BeLessThan(2);
            services.Top.Should().BeGreaterThan(general.Bottom);
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_{theme}_settings_wide"));
            Resize(window, 1600);
            ((double)Wait(window, "BackButton").BoundingRectangle.Left - window.BoundingRectangle.Left)
                .Should().BeLessThan(40 * scale, "the capped settings page stays left aligned");
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_{theme}_settings_left_aligned_1600"));
            Resize(window, 640);
            general = Wait(window, "SettingsTab_General").BoundingRectangle;
            services = Wait(window, "SettingsTab_Services").BoundingRectangle;
            Math.Abs(general.Top - services.Top).Should().BeLessThan(2);
            services.Left.Should().BeGreaterThan(general.Right);
            Find(window, "SettingsCategoryMenu").Should().BeNull();
            Invoke(Wait(window, "SettingsTab_Hotkeys"));
            Thread.Sleep(300);
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_{theme}_settings_narrow"));
            Resize(window, 400);
            Wait(window, "SettingsTab_About").BoundingRectangle.Top.Should()
                .BeGreaterThan(Wait(window, "SettingsTab_General").BoundingRectangle.Bottom,
                    "the original tabs wrap on a narrow page");
            Math.Abs(Wait(window, "SettingsTab_General").BoundingRectangle.Left - Wait(window, "BackButton").BoundingRectangle.Left)
                .Should().BeLessThan(2);
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_{theme}_settings_tabs_400"));
            Resize(window, 1280);
            Wait(window, "HotkeysHeaderText").IsOffscreen.Should().BeFalse("resizing keeps the selected settings category");
            Invoke(Wait(window, "BackButton"));
            Thread.Sleep(500);
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_windows_{theme}_settings_return"));
            UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(10))!.Text.Should().Be(draft);
        }
        finally { Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previous); }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Minimal")]
    public void Main_WideColumnsScrollIndependently(string theme)
    {
        using var dpi = new PerMonitorDpiScope();
        var previous = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        var directory = Path.Combine(Path.GetTempPath(), "Easydict.FluentScroll", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            AppTheme = theme, UILanguage = "zh-CN",
            MainWindowEnabledServices = new[] { "bing", "youdao", "baidu", "tencent", "deepl", "openai",
                "gemini", "groq", "github", "openrouter", "orcarouter", "deepseek", "zhipu", "doubao", "qwen" },
            EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
            EnableShowMiniWindowHotkey = false, EnableShowFixedWindowHotkey = false
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
        try
        {
            using var launcher = new AppLauncher();
            launcher.LaunchAuto(TimeSpan.FromSeconds(45));
            var window = launcher.GetMainWindow();
            ResizeMain(window, 1100);
            var input = Wait(window, "InputTextBox").AsTextBox();
            input.Patterns.Value.Pattern.SetValue("Fixed input 中文 draft");
            var left = Wait(window, "QuickInputScrollViewer");
            var right = Wait(window, "QuickResultsScrollViewer");
            var outer = Wait(window, "QuickTranslateContent");
            var inputTop = input.BoundingRectangle.Top;
            var languageTop = Wait(window, "SourceLangCombo").BoundingRectangle.Top;
            left.Patterns.Scroll.Pattern.VerticallyScrollable.Value.Should().BeFalse("the short input fits the viewport");
            right.Patterns.Scroll.Pattern.VerticallyScrollable.Value.Should().BeTrue("provider cards overflow the result viewport");
            ScrollHelper.ScrollToPercent(right, 100);
            right.Patterns.Scroll.Pattern.VerticalScrollPercent.Value.Should().BeGreaterThan(95);
            input.BoundingRectangle.Top.Should().Be(inputTop);
            Wait(window, "SourceLangCombo").BoundingRectangle.Top.Should().Be(languageTop);
            outer.Patterns.Scroll.Pattern.VerticallyScrollable.Value.Should().BeFalse();
            // Wheel beyond the results boundary must not move the outer page/input.
            var bounds = right.BoundingRectangle;
            FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            FlaUI.Core.Input.Mouse.Scroll(-6);
            Thread.Sleep(500);
            input.BoundingRectangle.Top.Should().Be(inputTop);
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_scroll_{theme}_fixed_input"));
            ResizeMain(window, 640);
            Wait(window, "ModeMenuButton").Focus();
            Thread.Sleep(700);
            ScrollHelper.ScrollToPercent(outer, 100);
            var narrowBounds = outer.BoundingRectangle;
            FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(narrowBounds.Right - 30, narrowBounds.Bottom - 50));
            FlaUI.Core.Input.Mouse.Scroll(-30);
            Thread.Sleep(800);
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_scroll_{theme}_narrow_scrolled"));
            output.WriteLine($"Narrow scroll: {outer.Patterns.Scroll.Pattern.VerticalScrollPercent.Value}; view size: {outer.Patterns.Scroll.Pattern.VerticalViewSize.Value}");
            outer.Patterns.Scroll.Pattern.VerticalScrollPercent.Value.Should().BeGreaterThan(95);
            ResizeMain(window, 1100);
            input.IsOffscreen.Should().BeFalse("returning to columns resets the outer scroll offset");
            input.Text.Should().Be("Fixed input 中文 draft");
            right.Patterns.Scroll.Pattern.VerticalScrollPercent.Value.Should().BeGreaterThan(95);
            input.Patterns.Value.Pattern.SetValue(string.Join("\n", Enumerable.Repeat("Long input 中文", 40)));
            var shortBounds = window.BoundingRectangle;
            shortBounds.Height = (int)(400 * ScreenshotHelper.GetWindowDpiScale(window));
            ScreenshotHelper.TrySetWindowPhysicalBounds(window, shortBounds).Should().BeTrue();
            Thread.Sleep(700);
            left.Patterns.Scroll.Pattern.VerticallyScrollable.Value.Should().BeTrue("overflowing input remains independently accessible");
            var resultOffset = right.Patterns.Scroll.Pattern.VerticalScrollPercent.Value;
            ScrollHelper.ScrollToPercent(left, 100);
            Wait(window, "SourceLangCombo").IsOffscreen.Should().BeFalse();
            right.Patterns.Scroll.Pattern.VerticalScrollPercent.Value.Should().BeApproximately(resultOffset, 0.5);
            output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_scroll_{theme}_short_window"));
        }
        finally { Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previous); }
    }

    private static void ResizeMain(Window window, int width)
    {
        Resize(window, width);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var metrics = Wait(window, "ModeMenuButton").Properties.ItemStatus.ValueOrDefault ?? "";
            var match = Regex.Match(metrics, @"PageWidth=([0-9.]+)");
            match.Success.Should().BeTrue();
            var actual = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (Math.Abs(actual - width) < 0.5) return;
            var bounds = window.BoundingRectangle;
            bounds.Width += (int)Math.Round((width - actual) * ScreenshotHelper.GetWindowDpiScale(window));
            ScreenshotHelper.TrySetWindowPhysicalBounds(window, bounds).Should().BeTrue();
            Thread.Sleep(300);
        }
        throw new InvalidOperationException($"Cannot reach Main width {width} DIP");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Minimal")]
    public void Settings_WideNavigationStaysFixedWhileDetailsScroll(string theme)
    {
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new Fixture(theme, 0);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        Resize(window, 1100);
        Invoke(Wait(window, "SettingsButton"));
        var general = Wait(window, "SettingsTab_General");
        var details = Wait(window, "SettingsDetailsScrollViewer");
        Thread.Sleep(1000);
        var categoryBounds = general.BoundingRectangle;
        var backBounds = Wait(window, "BackButton").BoundingRectangle;
        details.Patterns.Scroll.Pattern.VerticallyScrollable.Value.Should().BeTrue();
        ScrollHelper.ScrollToPercent(details, 100);
        details.Patterns.Scroll.Pattern.VerticalScrollPercent.Value.Should().BeGreaterThan(95);
        general.BoundingRectangle.Should().Be(categoryBounds);
        Wait(window, "BackButton").BoundingRectangle.Should().Be(backBounds);
        Wait(window, "SettingsTab_About").IsOffscreen.Should().BeFalse();
        var bounds = details.BoundingRectangle;
        FlaUI.Core.Input.Mouse.MoveTo(new System.Drawing.Point(bounds.Right - 30, bounds.Bottom - 50));
        FlaUI.Core.Input.Mouse.Scroll(-8);
        Thread.Sleep(500);
        general.BoundingRectangle.Should().Be(categoryBounds);
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_settings_{theme}_fixed_navigation"));
        Resize(window, 640);
        var services = Wait(window, "SettingsTab_Services");
        general = Wait(window, "SettingsTab_General");
        Math.Abs(general.BoundingRectangle.Top - services.BoundingRectangle.Top).Should().BeLessThan(2);
        services.BoundingRectangle.Left.Should().BeGreaterThan(general.BoundingRectangle.Right);
        Resize(window, 1100);
        details.Patterns.Scroll.Pattern.VerticalScrollPercent.Value.Should().BeGreaterThan(95);
        Invoke(Wait(window, "SettingsTab_Hotkeys"));
        Wait(window, "HotkeysHeaderText").IsOffscreen.Should().BeFalse("selecting a category resets only its details scroll");
        Wait(window, "SettingsTab_General").BoundingRectangle.Should().Be(categoryBounds);
        output.WriteLine(ScreenshotHelper.CaptureWindow(window, $"fluent_settings_{theme}_fixed_navigation_hotkeys"));
    }
}
