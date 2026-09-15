using System.Runtime.InteropServices;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FluentAssertions;
using Xunit;
using static Easydict.UIAutomation.Tests.Tests.SavedItemsVisualTests;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class HoverLookupSettingsTests
{
    [Theory]
    [InlineData("en-US", "Light")]
    [InlineData("zh-CN", "Dark")]
    public void HoverDelaySlider_UpdatesValue_AndPersists(string language, string theme)
    {
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new SettingsFixture(language, theme);
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        var settingsPath = Path.Combine(Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR")!, "settings.json");
        Invoke(Wait(window, "SettingsButton"));
        Invoke(Wait(window, "SettingsTab_General"));
        Wait(window, "HoverWordLookupToggle").Patterns.Toggle.Pattern.Toggle();
        var expander = Wait(window, "HoverWordLookupAdvancedExpander").Patterns.ExpandCollapse.Pattern;
        expander.ExpandCollapseState.Value.Should().Be(ExpandCollapseState.Collapsed);
        var scroller = Wait(window, "SettingsDetailsScrollViewer");
        // Narrow windows scroll the whole page; wide windows scroll the details pane.
        if (!scroller.Patterns.Scroll.Pattern.VerticallyScrollable.Value)
            scroller = Wait(window, "MainScrollViewer");
        AutomationElement? FindVisibleHoverSettings()
        {
            var viewport = scroller.BoundingRectangle;
            var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
            var headerTop = Math.Max(viewport.Top + 16,
                windowBounds.Top + (int)(80 * ScreenshotHelper.GetWindowDpiScale(window)));
            var headerBounds = Wait(window, "HoverWordLookupModifierHeader").BoundingRectangle;
            var advanced = Wait(window, "HoverWordLookupAdvancedExpander");
            var advancedBounds = advanced.BoundingRectangle;
            return headerBounds.Width > 20 && headerBounds.Top >= headerTop &&
                advancedBounds.Bottom <= Math.Min(viewport.Bottom, windowBounds.Bottom) - 16 ? advanced : null;
        }
        var visibleExpander = ScrollHelper.ScrollToFind(scroller, 70,
            FindVisibleHoverSettings);
        ScreenshotHelper.CaptureWindow(window, $"hover_delay_collapsed_{language}_{theme}");
        visibleExpander.Should().NotBeNull();
        var modifier = Wait(window, "HoverWordLookupModifierCombo");
        modifier.IsOffscreen.Should().BeFalse("the trigger key remains outside advanced settings");
        modifier.BoundingRectangle.Width.Should().BeGreaterThan(20);
        var triggerHint = language == "zh-CN" ? "按住触发键并将鼠标停在单词上" : "hold the trigger key and point at a word";
        Wait(window, "HoverWordLookupModifierHeader").Name.Should().Contain(triggerHint);
        Wait(window, "HoverWordLookupToggle").Name.Should().NotContain(triggerHint);
        expander.Expand();
        var slider = Wait(window, "HoverWordLookupDelaySlider");
        var range = slider.Patterns.RangeValue.Pattern;
        range.Minimum.Value.Should().Be(10);
        range.Maximum.Value.Should().Be(600);
        range.Value.Value.Should().Be(350);

        foreach (var delay in new[] { 10, 120, 600 })
        {
            range.SetValue(delay);
            Retry.WhileFalse(() => Wait(window, "HoverWordLookupDelayValueText").Name == $"{delay} ms",
                TimeSpan.FromSeconds(5)).Result.Should().BeTrue("the displayed latency must follow the slider");
            Invoke(Wait(window, "SaveButton"));
            Retry.WhileFalse(() =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                return document.RootElement.GetProperty("HoverWordLookupDelayMs").GetInt32() == delay;
            }, TimeSpan.FromSeconds(5)).Result.Should().BeTrue("the delay must be saved");
        }

        Invoke(Wait(window, "BackButton"));
        Invoke(Wait(window, "SettingsButton"));
        Invoke(Wait(window, "SettingsTab_General"));
        expander = Wait(window, "HoverWordLookupAdvancedExpander").Patterns.ExpandCollapse.Pattern;
        expander.ExpandCollapseState.Value.Should().Be(ExpandCollapseState.Collapsed);
        expander.Expand();
        slider = Wait(window, "HoverWordLookupDelaySlider");
        slider.Patterns.RangeValue.Pattern.Value.Value.Should().Be(600);
        scroller = Wait(window, "SettingsDetailsScrollViewer");
        if (!scroller.Patterns.Scroll.Pattern.VerticallyScrollable.Value)
            scroller = Wait(window, "MainScrollViewer");
        ScrollHelper.ScrollToFind(scroller, 70, FindVisibleHoverSettings).Should().NotBeNull();
        ScreenshotHelper.CaptureWindow(window, $"hover_delay_slider_{language}_{theme}");
    }

    [Fact]
    public void TrayToggle_SynchronizesOpenSettings_AndSurvivesUnrelatedSave()
    {
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new SettingsFixture();
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        var settingsPath = Path.Combine(Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR")!, "settings.json");
        Invoke(Wait(window, "SettingsButton"));
        Invoke(Wait(window, "SettingsTab_General"));
        var hover = Wait(window, "HoverWordLookupToggle");
        var unrelated = Wait(window, "MinimizeToTrayToggle");

        foreach (var enabled in new[] { true, false })
        {
            // Keep an unrelated edit pending while the real tray handler saves.
            unrelated.Patterns.Toggle.Pattern.Toggle();
            var unrelatedValue = unrelated.Patterns.Toggle.Pattern.ToggleState == ToggleState.On;
            SendMessageTimeout(window.Properties.NativeWindowHandle.Value, 0x8000 + 205,
                enabled ? (nint)1 : 0, 0, 2, 5000, out var handled).Should().NotBe(0);
            handled.Should().Be((nuint)1);
            Retry.WhileFalse(() => (hover.Patterns.Toggle.Pattern.ToggleState == ToggleState.On) == enabled,
                TimeSpan.FromSeconds(5)).Result.Should().BeTrue("the tray change must update the open page");
            unrelated.Patterns.Toggle.Pattern.ToggleState.Value.Should().Be(unrelatedValue ? ToggleState.On : ToggleState.Off);
            Invoke(Wait(window, "SaveButton"));
            Retry.WhileFalse(() => SavedValuesMatch(enabled, unrelatedValue), TimeSpan.FromSeconds(5))
                .Result.Should().BeTrue("saving other settings must preserve the tray choice and pending edits");
        }

        bool SavedValuesMatch(bool enabled, bool minimize)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.GetProperty("HoverWordLookupEnabled").GetBoolean() == enabled
                && document.RootElement.GetProperty("MinimizeToTray").GetBoolean() == minimize;
        }
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string? _previousDirectory = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");

        public SettingsFixture(string language = "en-US", string theme = "Light")
        {
            // This test needs settings only. Bootstrapping a saved-items database also
            // opens History at 1280 DIP, which smaller CI desktops cannot accommodate.
            var directory = Path.Combine(Path.GetTempPath(), "Easydict.HoverSettings.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
            {
                UILanguage = language, AppTheme = theme,
                EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
                EnableShowMiniWindowHotkey = false, EnableShowFixedWindowHotkey = false,
                EnableOcrTranslateHotkey = false, EnableSilentOcrHotkey = false,
                HoverWordLookupEnabled = false, MouseSelectionTranslate = false,
            }));
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", _previousDirectory);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
