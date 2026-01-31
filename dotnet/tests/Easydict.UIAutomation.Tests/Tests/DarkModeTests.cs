using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>
/// Tests that switch to dark mode via Settings and capture screenshots
/// of the main window, mini window, and fixed window in dark theme.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class DarkModeTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public DarkModeTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void DarkMode_MainWindow_ShouldRenderCorrectly()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Capture light mode baseline first
        var pathLight = ScreenshotHelper.CaptureWindow(window, "30_main_light_mode");
        _output.WriteLine($"Light mode screenshot saved: {pathLight}");

        // Switch to dark mode via settings
        SwitchToDarkMode(window);

        // Navigate back to main window
        NavigateBackToMain(window);

        Thread.Sleep(1000);
        var pathDark = ScreenshotHelper.CaptureWindow(window, "31_main_dark_mode");
        _output.WriteLine($"Dark mode screenshot saved: {pathDark}");

        var result = VisualRegressionHelper.CompareWithBaseline(pathDark, "31_main_dark_mode");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate");
        }
    }

    [Fact]
    public void DarkMode_SettingsPage_ShouldRenderCorrectly()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Switch to dark mode
        SwitchToDarkMode(window);

        // Stay on settings page and capture
        Thread.Sleep(1000);
        var path = ScreenshotHelper.CaptureWindow(window, "32_settings_dark_mode");
        _output.WriteLine($"Screenshot saved: {path}");

        // Scroll down to show more settings in dark mode
        var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
        if (scrollViewer != null)
        {
            Mouse.MoveTo(scrollViewer.GetClickablePoint());

            Mouse.Scroll(-8);
            Thread.Sleep(1000);
            var pathServices = ScreenshotHelper.CaptureWindow(window, "33_settings_dark_services");
            _output.WriteLine($"Screenshot saved: {pathServices}");

            Mouse.Scroll(-15);
            Thread.Sleep(1000);
            var pathConfig = ScreenshotHelper.CaptureWindow(window, "34_settings_dark_config");
            _output.WriteLine($"Screenshot saved: {pathConfig}");
        }
    }

    [Fact]
    public void DarkMode_MiniWindow_ShouldRenderCorrectly()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Switch to dark mode first
        SwitchToDarkMode(window);

        NavigateBackToMain(window);
        Thread.Sleep(1000);

        // Open mini window via hotkey: Ctrl+Alt+M
        _output.WriteLine("Opening mini window with Ctrl+Alt+M");
        SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);
        Thread.Sleep(3000);

        var miniWindow = FindSecondaryWindow("Mini");
        miniWindow.Should().NotBeNull("Mini window must open after Ctrl+Alt+M hotkey in dark mode");

        miniWindow!.SetForeground();
        Thread.Sleep(500);

        var path = ScreenshotHelper.CaptureWindow(miniWindow, "35_mini_dark_mode");
        _output.WriteLine($"Screenshot saved: {path}");

        var result = VisualRegressionHelper.CompareWithBaseline(path, "35_mini_dark_mode");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate");
        }
    }

    [Fact]
    public void DarkMode_FixedWindow_ShouldRenderCorrectly()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Switch to dark mode first
        SwitchToDarkMode(window);

        NavigateBackToMain(window);
        Thread.Sleep(1000);

        // Open fixed window via hotkey: Ctrl+Alt+F
        _output.WriteLine("Opening fixed window with Ctrl+Alt+F");
        SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_F);
        Thread.Sleep(3000);

        var fixedWindow = FindSecondaryWindow("Fixed");
        fixedWindow.Should().NotBeNull("Fixed window must open after Ctrl+Alt+F hotkey in dark mode");

        fixedWindow!.SetForeground();
        Thread.Sleep(500);

        var path = ScreenshotHelper.CaptureWindow(fixedWindow, "36_fixed_dark_mode");
        _output.WriteLine($"Screenshot saved: {path}");

        var result = VisualRegressionHelper.CompareWithBaseline(path, "36_fixed_dark_mode");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate");
        }
    }

    /// <summary>
    /// Navigate to settings and switch AppThemeCombo to "Dark".
    /// </summary>
    private void SwitchToDarkMode(Window window)
    {
        // Click settings button
        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")),
            TimeSpan.FromSeconds(10)).Result;

        settingsButton.Should().NotBeNull("SettingsButton must exist");
        settingsButton!.Click();
        Thread.Sleep(2000);

        // Scroll down to Behavior section where AppThemeCombo lives
        var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
        if (scrollViewer != null)
        {
            Mouse.MoveTo(scrollViewer.GetClickablePoint());
            // Scroll far enough to reach behavior/appearance section
            Mouse.Scroll(-30);
            Thread.Sleep(1000);
        }

        // Find and interact with AppThemeCombo
        var themeCombo = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("AppThemeCombo"))?.AsComboBox(),
            TimeSpan.FromSeconds(10)).Result;

        themeCombo.Should().NotBeNull("AppThemeCombo must exist on settings page");

        _output.WriteLine("Found AppThemeCombo");
        themeCombo!.Click();
        Thread.Sleep(500);

        // Look for "Dark" option in the dropdown
        var darkItem = Retry.WhileNull(
            () => themeCombo.FindFirstDescendant(cf => cf.ByName("Dark")),
            TimeSpan.FromSeconds(5)).Result;

        if (darkItem != null)
        {
            darkItem.Click();
            _output.WriteLine("Selected Dark theme");
        }
        else
        {
            // Try selecting by index (System=0, Light=1, Dark=2)
            _output.WriteLine("Dark item not found by name, trying index selection");
            themeCombo.Select(2);
        }

        Thread.Sleep(1000);
    }

    /// <summary>
    /// Navigate back to main page from settings using the back button.
    /// </summary>
    private void NavigateBackToMain(Window window)
    {
        // Try floating back button first
        var backButton = window.FindFirstDescendant(cf => cf.ByAutomationId("FloatingBackButton"));
        if (backButton != null)
        {
            backButton.Click();
            Thread.Sleep(1000);
            return;
        }

        // Try any back button
        backButton = window.FindFirstDescendant(cf => cf.ByAutomationId("BackButton"));
        if (backButton != null)
        {
            backButton.Click();
            Thread.Sleep(1000);
            return;
        }

        _output.WriteLine("Back button not found - trying Escape key");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(1000);
    }

    /// <summary>
    /// Send a hotkey combination safely, ensuring all keys are released even on failure.
    /// </summary>
    private void SendHotkey(VirtualKeyShort modifier1, VirtualKeyShort modifier2, VirtualKeyShort key)
    {
        try
        {
            Keyboard.Press(modifier1);
            Keyboard.Press(modifier2);
            Keyboard.Press(key);
            Thread.Sleep(100);
        }
        finally
        {
            // Always release all keys to prevent stuck modifiers
            try { Keyboard.Release(key); } catch { /* ignore */ }
            try { Keyboard.Release(modifier2); } catch { /* ignore */ }
            try { Keyboard.Release(modifier1); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Find a secondary (non-main) window from the application's top-level windows.
    /// </summary>
    private Window? FindSecondaryWindow(string windowType)
    {
        var allWindows = _launcher.Application.GetAllTopLevelWindows(_launcher.Automation);
        _output.WriteLine($"Found {allWindows.Length} top-level window(s)");

        foreach (var w in allWindows)
        {
            _output.WriteLine($"  Window: \"{w.Title}\" size={w.BoundingRectangle.Width}x{w.BoundingRectangle.Height}");
        }

        if (allWindows.Length <= 1)
        {
            _output.WriteLine($"{windowType} window did not open - only main window found");
            return null;
        }

        // Return the smallest window (mini/fixed are smaller than main)
        return allWindows
            .OrderBy(w => w.BoundingRectangle.Width * w.BoundingRectangle.Height)
            .First();
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
