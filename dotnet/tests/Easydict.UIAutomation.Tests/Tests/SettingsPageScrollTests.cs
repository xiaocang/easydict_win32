using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using System.Drawing;
using System.Runtime.InteropServices;
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

        var scrollViewer = WaitForVisibleByAutomationId(window, "MainScrollViewer", TimeSpan.FromSeconds(15));
        scrollViewer.Should().NotBeNull("MainScrollViewer must exist on settings page after loading completes");
        WaitForVisibleByAutomationId(window, "SettingsGeneralBehaviorHeader", TimeSpan.FromSeconds(10))
            .Should().NotBeNull("the General tab content must be visible before capturing the General tab screenshot");

        // Capture each tab at its initial top position. Settings is tabbed now, so
        // visual regression should validate tab content instead of scrolling a
        // single long page that no longer exists.
        var path = ScreenshotHelper.CaptureWindow(window, "10_settings_general_tab");
        _output.WriteLine($"Screenshot saved: {path}");

        ClickSettingsTab(window, "SettingsTab_Services");
        path = ScreenshotHelper.CaptureWindow(window, "11_settings_services_tab");
        _output.WriteLine($"Screenshot saved: {path}");

        ScrollHelper.ScrollToPercent(scrollViewer!, 50, _output.WriteLine);
        path = ScreenshotHelper.CaptureWindow(window, "12_settings_services_api_keys");
        _output.WriteLine($"Screenshot saved: {path}");

        ClickSettingsTab(window, "SettingsTab_Views");
        path = ScreenshotHelper.CaptureWindow(window, "13_settings_views_tab");
        _output.WriteLine($"Screenshot saved: {path}");

        ClickSettingsTab(window, "SettingsTab_Language");
        path = ScreenshotHelper.CaptureWindow(window, "14_settings_language_tab");
        _output.WriteLine($"Screenshot saved: {path}");

        ClickSettingsTab(window, "SettingsTab_Hotkeys");
        path = ScreenshotHelper.CaptureWindow(window, "15_settings_hotkeys_tab");
        _output.WriteLine($"Screenshot saved: {path}");

        ClickSettingsTab(window, "SettingsTab_Advanced");
        path = ScreenshotHelper.CaptureWindow(window, "16_settings_advanced_tab");
        _output.WriteLine($"Screenshot saved: {path}");

        ClickSettingsTab(window, "SettingsTab_About");
        path = ScreenshotHelper.CaptureWindow(window, "17_settings_about_tab");
        _output.WriteLine($"Screenshot saved: {path}");

        _output.WriteLine("Settings page scroll-through completed with 8 screenshots");
    }

    private static void ClickSettingsTab(Window window, string automationId)
    {
        var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
        if (scrollViewer != null)
        {
            ScrollHelper.ScrollToPercent(scrollViewer, 0);
        }

        var tab = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(10)).Result;
        tab.Should().NotBeNull($"{automationId} must exist on settings page");
        var invoke = tab!.Patterns.Invoke.PatternOrDefault;
        if (invoke != null)
        {
            invoke.Invoke();
        }
        else
        {
            tab.Click();
        }

        DismissTransientSettingsTooltip(window);
    }

    private static void DismissTransientSettingsTooltip(Window window)
    {
        try
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }
        catch
        {
            // Screenshot assertions do not depend on keyboard focus.
        }

        try
        {
            var bounds = window.BoundingRectangle;
            Mouse.MoveTo(new Point(bounds.Right + 32, bounds.Bottom + 32));
        }
        catch
        {
            // The tooltip also dismisses on Escape; pointer movement is best-effort.
        }

        Thread.Sleep(800);
    }

    private static AutomationElement? WaitForVisibleByAutomationId(
        Window window,
        string automationId,
        TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => FindVisibleByAutomationId(window, automationId),
            timeout).Result;
    }

    private static AutomationElement? FindVisibleByAutomationId(Window window, string automationId)
    {
        try
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return element != null && IsOnScreenOrUnknown(element)
                ? element
                : null;
        }
        catch (Exception ex) when (ex is COMException or TimeoutException)
        {
            return null;
        }
    }

    private static bool IsOnScreenOrUnknown(AutomationElement element)
    {
        try
        {
            return !element.IsOffscreen;
        }
        catch (PropertyNotSupportedException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
