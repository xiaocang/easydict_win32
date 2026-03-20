using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>
/// Tests that open the settings page and scroll through all sections,
/// capturing screenshots at each major section for visual regression.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class SettingsPageScrollTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public SettingsPageScrollTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void SettingsPage_ScrollThroughAllSections()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Navigate to settings
        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")),
            TimeSpan.FromSeconds(10)).Result;

        settingsButton.Should().NotBeNull("SettingsButton must exist on main window");
        settingsButton!.Click();
        Thread.Sleep(2000);

        // Capture settings page top (Language Preferences)
        var path = ScreenshotHelper.CaptureWindow(window, "10_settings_top_language_prefs");
        _output.WriteLine($"Screenshot saved: {path}");

        var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
        scrollViewer.Should().NotBeNull("MainScrollViewer must exist on settings page");

        // Scroll 1: Enabled Services section (12%)
        ScrollToPercent(scrollViewer!, 12);
        path = ScreenshotHelper.CaptureWindow(window, "11_settings_enabled_services");
        _output.WriteLine($"Screenshot saved: {path}");

        // Scroll 2: More enabled services / Mini/Fixed window services (22%)
        ScrollToPercent(scrollViewer!, 22);
        path = ScreenshotHelper.CaptureWindow(window, "12_settings_mini_fixed_services");
        _output.WriteLine($"Screenshot saved: {path}");

        // Scroll 3: Service Configuration / API keys area (35%)
        ScrollToPercent(scrollViewer!, 35);
        path = ScreenshotHelper.CaptureWindow(window, "13_settings_service_config");
        _output.WriteLine($"Screenshot saved: {path}");

        // Scroll 4: More API configuration (50%)
        ScrollToPercent(scrollViewer!, 50);
        path = ScreenshotHelper.CaptureWindow(window, "14_settings_api_keys_mid");
        _output.WriteLine($"Screenshot saved: {path}");

        // Scroll 5: HTTP Proxy / Behavior section (70%)
        ScrollToPercent(scrollViewer!, 70);
        path = ScreenshotHelper.CaptureWindow(window, "15_settings_behavior_section");
        _output.WriteLine($"Screenshot saved: {path}");

        // Scroll 6: Hotkeys section (85%)
        ScrollToPercent(scrollViewer!, 85);
        path = ScreenshotHelper.CaptureWindow(window, "16_settings_hotkeys_section");
        _output.WriteLine($"Screenshot saved: {path}");

        // Scroll 7: About section (100% — bottom)
        ScrollToPercent(scrollViewer!, 100);
        path = ScreenshotHelper.CaptureWindow(window, "17_settings_about_section");
        _output.WriteLine($"Screenshot saved: {path}");

        _output.WriteLine("Settings page scroll-through completed with 8 screenshots");
    }

    /// <summary>
    /// Scrolls a ScrollViewer to the specified vertical percentage using ScrollPattern.
    /// Falls back to Mouse.Scroll if ScrollPattern is not available.
    /// </summary>
    private void ScrollToPercent(AutomationElement scrollViewer, double verticalPercent)
    {
        if (scrollViewer.Patterns.Scroll.IsSupported)
        {
            var scrollPattern = scrollViewer.Patterns.Scroll.Pattern;
            // -1 means "do not change" for horizontal scroll
            scrollPattern.SetScrollPercent(-1, verticalPercent);
            _output.WriteLine($"ScrollPattern: scrolled to {verticalPercent}%");
        }
        else
        {
            _output.WriteLine("ScrollPattern not available, falling back to Mouse.Scroll");
            Mouse.MoveTo(scrollViewer.GetClickablePoint());
            // Use a large scroll amount as fallback
            Mouse.Scroll(-15);
        }

        Thread.Sleep(800);
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
