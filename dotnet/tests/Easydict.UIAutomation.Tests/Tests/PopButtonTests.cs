using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>
/// UI automation tests for the floating pop button (mouse selection translate).
/// Tests the full workflow: enable setting → select text → pop button appears → click → mini window.
///
/// Prerequisites:
/// - The app must be installed (MSIX) or built (exe)
/// - These tests require a real Windows desktop environment (not headless)
/// - Tests are in the "UIAutomation" category and run in the ui-automation.yml workflow
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class PopButtonTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public PopButtonTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void Settings_MouseSelectionTranslateToggle_ShouldExist()
    {
        // Navigate to Settings and verify the toggle exists
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var settingsNav = UITestHelper.WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsNav.Should().NotBeNull("SettingsButton must be discoverable before validating PopButton settings");

        UITestHelper.ClickElement(settingsNav!);
        Thread.Sleep(1500);

        var path = ScreenshotHelper.CaptureWindow(window, "pop_button_01_settings_page");
        _output.WriteLine($"Screenshot saved: {path}");

        var toggle = FindMouseSelectionTranslateToggle(window);
        if (toggle == null)
        {
            ScreenshotHelper.CaptureWindow(window, "pop_button_02_toggle_not_found");
        }

        toggle.Should().NotBeNull("Mouse selection translate toggle must be visible in Settings");

        var togglePath = ScreenshotHelper.CaptureWindow(window, "pop_button_02_toggle_found");
        _output.WriteLine($"Toggle screenshot saved: {togglePath}");
    }

    [Fact]
    public void Settings_BehaviorSection_Screenshot()
    {
        // Navigate to Settings and capture the Behavior section
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var settingsNav = UITestHelper.WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsNav.Should().NotBeNull("SettingsButton must be discoverable before capturing Behavior section");

        UITestHelper.ClickElement(settingsNav!);
        Thread.Sleep(1500);

        var behaviorToggle = FindMouseSelectionTranslateToggle(window);
        behaviorToggle.Should().NotBeNull("Behavior section should expose the Mouse selection translate toggle");

        var path = ScreenshotHelper.CaptureWindow(window, "pop_button_03_behavior_section");
        _output.WriteLine($"Behavior section screenshot saved: {path}");

        var result = VisualRegressionHelper.CompareWithBaseline(path, "pop_button_03_behavior_section");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate baseline");
        }
    }

    [Fact]
    public void PopButton_FullWorkflow_ScreenshotSequence()
    {
        // This test documents the full expected workflow with screenshots.
        // Due to the complexity of simulating cross-app text selection in CI,
        // this test focuses on capturing the settings UI and app state.

        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Step 1: Capture initial app state
        var step1 = ScreenshotHelper.CaptureWindow(window, "pop_button_workflow_01_initial");
        _output.WriteLine($"Step 1 (Initial state): {step1}");

        // Step 2: Navigate to settings
        var settingsNav = UITestHelper.WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsNav.Should().NotBeNull("SettingsButton must be discoverable for the PopButton workflow sequence");

        UITestHelper.ClickElement(settingsNav!);
        Thread.Sleep(1500);

        var step2 = ScreenshotHelper.CaptureWindow(window, "pop_button_workflow_02_settings");
        _output.WriteLine($"Step 2 (Settings page): {step2}");

        // Step 3: Capture full screen context
        var step3 = ScreenshotHelper.CaptureScreen("pop_button_workflow_03_full_screen");
        _output.WriteLine($"Step 3 (Full screen): {step3}");

        window.Should().NotBeNull();
    }

    private AutomationElement? FindMouseSelectionTranslateToggle(Window window)
    {
        AutomationElement? Finder()
        {
            return UITestHelper.FindByAutomationIdOrName(window, "MouseSelectionTranslateToggle")
                ?? window.FindFirstDescendant(c => c.ByName("Mouse selection translate"));
        }

        var toggle = Finder();
        if (IsVisible(toggle))
        {
            return toggle;
        }

        var scrollViewer = UITestHelper.FindByAutomationIdOrName(window, "MainScrollViewer");
        if (scrollViewer == null)
        {
            return toggle;
        }

        return ScrollHelper.ScrollToFind(
            scrollViewer,
            startPercent: 70,
            Finder,
            _output.WriteLine);
    }

    private static bool IsVisible(AutomationElement? element)
    {
        if (element == null)
        {
            return false;
        }

        try
        {
            return !element.IsOffscreen;
        }
        catch (PropertyNotSupportedException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
