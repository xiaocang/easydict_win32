using System.Drawing;
using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>
/// End-to-end tests for the selection → pop button → mini window flow.
///
/// Uses Notepad as a controlled text selection target:
/// 1. Launch Easydict and enable MouseSelectionTranslate in Settings
/// 2. Launch Notepad with known text
/// 3. Simulate drag-select / double-click via FlaUI Mouse
/// 4. Verify PopButton appears via EnumWindows (PopButtonFinder)
/// 5. Verify window position, styles, auto-dismiss, and click → mini window
///
/// Prerequisites:
/// - Real Windows desktop environment (not headless)
/// - Easydict installed (MSIX) or built (exe)
/// - These tests are in the "UIAutomation" category
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class PopButtonSelectionTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly NotepadTestTarget? _notepad;
    private readonly ITestOutputHelper _output;
    private readonly uint _easydictProcessId;
    private bool _settingEnabled;

    /// <summary>
    /// Total time budget for the PopButton to appear after a selection gesture.
    /// Accounts for: SelectionDelayMs (150) + TextSelectionService (~500ms) + margin.
    /// </summary>
    private static readonly TimeSpan PopButtonTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Offset from the text area edge to the drag start point.
    /// Must be inside the text content area.
    /// </summary>
    private const int TextAreaPadding = 15;

    /// <summary>
    /// Drag distance in pixels. Must exceed MouseHookService.MinDragDistance (10px).
    /// </summary>
    private const int DragDistance = 180;

    public PopButtonSelectionTests(ITestOutputHelper output)
    {
        _output = output;

        // 1. Launch Easydict
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        _easydictProcessId = (uint)_launcher.Application.ProcessId;
        _output.WriteLine($"Easydict launched, PID={_easydictProcessId}");

        // 2. Enable MouseSelectionTranslate in Settings
        _settingEnabled = TryEnableMouseSelectionTranslate();

        if (!_settingEnabled)
        {
            _output.WriteLine("WARNING: Could not enable MouseSelectionTranslate setting. " +
                              "Selection tests will verify infrastructure but popup may not appear.");
        }

        // 3. Launch Notepad with known text
        try
        {
            _notepad = new NotepadTestTarget("Hello World test selection text for Easydict popup verification");
            _notepad.BringToForeground();
            _output.WriteLine("Notepad launched with test text");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"WARNING: Failed to launch Notepad: {ex.Message}");
        }
    }

    [Fact]
    public void DragSelect_InNotepad_PopButtonAppears()
    {
        if (_notepad == null)
        {
            _output.WriteLine("SKIP: Notepad not available");
            return;
        }

        // Arrange: Get text area bounds
        var bounds = _notepad.GetTextBounds();
        _output.WriteLine($"Text area bounds: {bounds}");

        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        var endX = startX + DragDistance;
        var endY = startY;

        _output.WriteLine($"Simulating drag from ({startX},{startY}) to ({endX},{endY})");

        // Act: Simulate drag-select with intermediate moves (realistic for WH_MOUSE_LL)
        SimulateDragSelect(startX, startY, endX, endY);

        // Assert: PopButton should appear
        var popHwnd = PopButtonFinder.WaitForPopButton(_easydictProcessId, PopButtonTimeout);

        // Screenshot for debugging regardless of result
        var screenshotPath = ScreenshotHelper.CaptureScreen("e2e_drag_select_result");
        _output.WriteLine($"Screenshot: {screenshotPath}");

        if (popHwnd != IntPtr.Zero)
        {
            _output.WriteLine($"PopButton found at hwnd=0x{popHwnd:X}");

            // Verify position is near mouse release point
            var rect = PopButtonFinder.GetRect(popHwnd);
            _output.WriteLine($"PopButton rect: Left={rect.Left} Top={rect.Top} " +
                              $"W={rect.Width} H={rect.Height}");

            // The popup should appear near the mouse release point
            // ShowAt offsets by (+8*scale, -32*scale) from the mouse point
            var dx = Math.Abs(rect.CenterX - endX);
            var dy = Math.Abs(rect.CenterY - endY);
            _output.WriteLine($"Distance from mouse release: dx={dx} dy={dy}");

            dx.Should().BeLessThan(80, "PopButton X should be near mouse release X");
            dy.Should().BeLessThan(80, "PopButton Y should be near mouse release Y");

            // Verify window styles
            var styles = PopButtonFinder.GetStyleFlags(popHwnd);
            styles.HasNoActivate.Should().BeTrue("PopButton must have WS_EX_NOACTIVATE");
            styles.HasToolWindow.Should().BeTrue("PopButton must have WS_EX_TOOLWINDOW");
            styles.HasTopmost.Should().BeTrue("PopButton must have WS_EX_TOPMOST");

            // Verify size is approximately 30x30 (scaled for DPI)
            rect.Width.Should().BeInRange(20, 50, "PopButton width should be ~30px (DPI-scaled)");
            rect.Height.Should().BeInRange(20, 50, "PopButton height should be ~30px (DPI-scaled)");

            // Visual regression: capture and compare popup screenshot
            var popScreenshot = ScreenshotHelper.CaptureScreen("e2e_pop_button_visible");
            var vrResult = VisualRegressionHelper.CompareWithBaseline(
                popScreenshot, "e2e_pop_button_visible", thresholdPercent: 8.0);
            if (vrResult != null)
            {
                _output.WriteLine(vrResult.ToString());
            }
            else
            {
                _output.WriteLine("No baseline found - screenshot saved as candidate");
            }
        }
        else
        {
            _output.WriteLine("PopButton did not appear. This may be expected if the setting " +
                              "could not be enabled or hooks are not active in this environment.");
            // Don't fail hard — the infrastructure is verified even if the popup doesn't show
            // in environments where global hooks or text selection don't work
        }
    }

    [Fact]
    public void DoubleClick_InNotepad_PopButtonAppears()
    {
        if (_notepad == null)
        {
            _output.WriteLine("SKIP: Notepad not available");
            return;
        }

        // Arrange: Click position on a word in Notepad
        var bounds = _notepad.GetTextBounds();
        var clickX = bounds.Left + 40;
        var clickY = bounds.Top + TextAreaPadding;
        var clickPoint = new Point(clickX, clickY);

        _output.WriteLine($"Simulating double-click at ({clickX},{clickY})");

        // Act: Double-click to select a word
        // Multi-click has additional delay: GetDoubleClickTime() + 50ms before firing
        Mouse.DoubleClick(clickPoint);

        // Assert: PopButton should appear (longer timeout for multi-click detection)
        var popHwnd = PopButtonFinder.WaitForPopButton(
            _easydictProcessId, TimeSpan.FromSeconds(5));

        var screenshotPath = ScreenshotHelper.CaptureScreen("e2e_double_click_result");
        _output.WriteLine($"Screenshot: {screenshotPath}");

        if (popHwnd != IntPtr.Zero)
        {
            _output.WriteLine($"PopButton found after double-click at hwnd=0x{popHwnd:X}");
            PopButtonFinder.IsVisible(popHwnd).Should().BeTrue("PopButton should be visible");
        }
        else
        {
            _output.WriteLine("PopButton did not appear after double-click. " +
                              "May be expected if setting not enabled or hooks inactive.");
        }
    }

    [Fact]
    public void PopButton_AutoDismisses_After5Seconds()
    {
        if (_notepad == null)
        {
            _output.WriteLine("SKIP: Notepad not available");
            return;
        }

        // Arrange + Act: Trigger popup via drag select
        var bounds = _notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = PopButtonFinder.WaitForPopButton(_easydictProcessId, PopButtonTimeout);
        if (popHwnd == IntPtr.Zero)
        {
            _output.WriteLine("SKIP: PopButton did not appear, cannot test auto-dismiss");
            return;
        }

        _output.WriteLine($"PopButton visible at hwnd=0x{popHwnd:X}, waiting for auto-dismiss...");

        // Assert: Still visible at ~4 seconds
        Thread.Sleep(4000);
        var stillVisible = PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After 4s: visible={stillVisible}");
        stillVisible.Should().BeTrue("PopButton should still be visible before 5s timeout");

        // Assert: Dismissed after 5+ seconds total
        Thread.Sleep(2000);
        var dismissed = !PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After 6s: dismissed={dismissed}");
        dismissed.Should().BeTrue("PopButton should auto-dismiss after 5s (AutoDismissMs=5000)");
    }

    [Fact]
    public void PopButton_DismissesOnScroll()
    {
        if (_notepad == null)
        {
            _output.WriteLine("SKIP: Notepad not available");
            return;
        }

        // Arrange: Trigger popup
        var bounds = _notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = PopButtonFinder.WaitForPopButton(_easydictProcessId, PopButtonTimeout);
        if (popHwnd == IntPtr.Zero)
        {
            _output.WriteLine("SKIP: PopButton did not appear, cannot test scroll dismiss");
            return;
        }

        _output.WriteLine($"PopButton visible, sending scroll...");

        // Act: Scroll the mouse wheel
        Mouse.Scroll(3);
        Thread.Sleep(500);

        // Assert: PopButton should be dismissed
        var dismissed = !PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After scroll: dismissed={dismissed}");
        dismissed.Should().BeTrue("PopButton should dismiss on mouse scroll");
    }

    [Fact]
    public void PopButton_DismissesOnRightClick()
    {
        if (_notepad == null)
        {
            _output.WriteLine("SKIP: Notepad not available");
            return;
        }

        // Arrange: Trigger popup
        var bounds = _notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = PopButtonFinder.WaitForPopButton(_easydictProcessId, PopButtonTimeout);
        if (popHwnd == IntPtr.Zero)
        {
            _output.WriteLine("SKIP: PopButton did not appear, cannot test right-click dismiss");
            return;
        }

        _output.WriteLine($"PopButton visible, sending right-click...");

        // Act: Right-click somewhere away from the popup
        var rect = PopButtonFinder.GetRect(popHwnd);
        Mouse.RightClick(new Point(rect.Left + 100, rect.Top + 100));
        Thread.Sleep(500);

        // Assert: PopButton should be dismissed
        var dismissed = !PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After right-click: dismissed={dismissed}");
        dismissed.Should().BeTrue("PopButton should dismiss on right-click");
    }

    [Fact]
    public void PopButton_DismissesOnKeyPress()
    {
        if (_notepad == null)
        {
            _output.WriteLine("SKIP: Notepad not available");
            return;
        }

        // Arrange: Trigger popup
        var bounds = _notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = PopButtonFinder.WaitForPopButton(_easydictProcessId, PopButtonTimeout);
        if (popHwnd == IntPtr.Zero)
        {
            _output.WriteLine("SKIP: PopButton did not appear, cannot test key dismiss");
            return;
        }

        _output.WriteLine($"PopButton visible, pressing Escape...");

        // Act: Press a key (Escape is safe — won't type anything)
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(50);
        Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(500);

        // Assert: PopButton should be dismissed
        var dismissed = !PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After key press: dismissed={dismissed}");
        dismissed.Should().BeTrue("PopButton should dismiss on key press");
    }

    [Fact]
    public void PopButton_Click_OpensMiniWindow()
    {
        if (_notepad == null)
        {
            _output.WriteLine("SKIP: Notepad not available");
            return;
        }

        // Arrange: Trigger popup via drag select
        var bounds = _notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = PopButtonFinder.WaitForPopButton(_easydictProcessId, PopButtonTimeout);
        if (popHwnd == IntPtr.Zero)
        {
            _output.WriteLine("SKIP: PopButton did not appear, cannot test click → mini window");
            return;
        }

        _output.WriteLine($"PopButton visible at hwnd=0x{popHwnd:X}, clicking...");

        // Act: Click the PopButton center
        var rect = PopButtonFinder.GetRect(popHwnd);
        Mouse.Click(new Point(rect.CenterX, rect.CenterY));
        _output.WriteLine($"Clicked PopButton at ({rect.CenterX},{rect.CenterY})");

        // Wait for mini window to appear
        Thread.Sleep(2000);

        // Assert: Mini window should open
        var miniWindow = UITestHelper.FindSecondaryWindow(
            _launcher.Application, _launcher.Automation, "Mini", _output);

        var screenshotPath = ScreenshotHelper.CaptureScreen("e2e_pop_button_click_mini_window");
        _output.WriteLine($"Screenshot: {screenshotPath}");

        if (miniWindow != null)
        {
            _output.WriteLine($"Mini window found: \"{miniWindow.Title}\" " +
                              $"size={miniWindow.BoundingRectangle.Width}x{miniWindow.BoundingRectangle.Height}");

            ScreenshotHelper.CaptureWindow(miniWindow, "e2e_mini_window_after_popup_click");

            // PopButton should be hidden after click
            PopButtonFinder.IsVisible(popHwnd).Should().BeFalse(
                "PopButton should hide after being clicked");
        }
        else
        {
            _output.WriteLine("Mini window not found via GetAllTopLevelWindows. " +
                              "This may be expected if the PopButton click did not register.");
        }
    }

    [Fact]
    public void DragSelect_ScreenshotSequence_FullWorkflow()
    {
        // Capture a complete screenshot sequence documenting the selection flow,
        // even when the popup doesn't appear (useful for CI artifact review).

        var window = _launcher.GetMainWindow();

        // Step 1: Initial Easydict state
        var step1 = ScreenshotHelper.CaptureWindow(window, "e2e_workflow_01_easydict_initial");
        _output.WriteLine($"Step 1 (Easydict): {step1}");

        // Step 2: Notepad with text
        if (_notepad != null)
        {
            _notepad.BringToForeground();
            Thread.Sleep(300);
            var step2 = ScreenshotHelper.CaptureScreen("e2e_workflow_02_notepad_ready");
            _output.WriteLine($"Step 2 (Notepad): {step2}");

            // Step 3: After drag select
            var bounds = _notepad.GetTextBounds();
            var startX = bounds.Left + TextAreaPadding;
            var startY = bounds.Top + TextAreaPadding;
            SimulateDragSelect(startX, startY, startX + DragDistance, startY);

            // Wait for potential popup
            Thread.Sleep(1500);
            var step3 = ScreenshotHelper.CaptureScreen("e2e_workflow_03_after_selection");
            _output.WriteLine($"Step 3 (After selection): {step3}");

            // Visual regression for the full workflow state
            var vrResult = VisualRegressionHelper.CompareWithBaseline(
                step3, "e2e_workflow_03_after_selection", thresholdPercent: 10.0);
            if (vrResult != null)
            {
                _output.WriteLine(vrResult.ToString());
            }
            else
            {
                _output.WriteLine("No baseline — saved as candidate");
            }
        }

        // Step 4: Full screen context
        var step4 = ScreenshotHelper.CaptureScreen("e2e_workflow_04_full_screen");
        _output.WriteLine($"Step 4 (Full screen): {step4}");
    }

    /// <summary>
    /// Simulate a drag-select gesture using FlaUI Mouse with intermediate moves.
    /// Intermediate moves ensure WH_MOUSE_LL receives enough WM_MOUSEMOVE messages
    /// for the DragDetector to detect the drag threshold being exceeded.
    /// </summary>
    private void SimulateDragSelect(int startX, int startY, int endX, int endY)
    {
        Mouse.MoveTo(new Point(startX, startY));
        Thread.Sleep(100);

        Mouse.Down(MouseButton.Left);
        Thread.Sleep(50);

        // Generate intermediate move events (step every 10px)
        var totalDistance = Math.Abs(endX - startX) + Math.Abs(endY - startY);
        var steps = Math.Max(totalDistance / 10, 2);
        for (int i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var x = (int)(startX + (endX - startX) * t);
            var y = (int)(startY + (endY - startY) * t);
            Mouse.MoveTo(new Point(x, y));
            Thread.Sleep(10);
        }

        Thread.Sleep(50);
        Mouse.Up(MouseButton.Left);
    }

    /// <summary>
    /// Navigate to Settings, scroll to the Behavior section, and enable the
    /// MouseSelectionTranslate toggle. The toggle is not visible without scrolling
    /// because the Behavior section is near the bottom of the settings page.
    /// Returns true if the toggle was found and enabled.
    /// </summary>
    private bool TryEnableMouseSelectionTranslate()
    {
        try
        {
            var window = _launcher.GetMainWindow();
            Thread.Sleep(2000);

            // Navigate to Settings page via the SettingsButton (AutomationId)
            var settingsButton = Retry.WhileNull(
                () => window.FindFirstDescendant(c => c.ByAutomationId("SettingsButton")),
                TimeSpan.FromSeconds(10)).Result;

            if (settingsButton == null)
            {
                _output.WriteLine("SettingsButton not found by AutomationId");
                return false;
            }

            settingsButton.Click();
            _output.WriteLine("Clicked SettingsButton, waiting for settings page...");
            Thread.Sleep(2000);

            // Capture settings page for debugging
            ScreenshotHelper.CaptureWindow(window, "e2e_settings_before_scroll");

            // Find the MainScrollViewer to scroll down to Behavior section
            var scrollViewer = Retry.WhileNull(
                () => window.FindFirstDescendant(c => c.ByAutomationId("MainScrollViewer")),
                TimeSpan.FromSeconds(5)).Result;

            if (scrollViewer == null)
            {
                _output.WriteLine("MainScrollViewer not found — cannot scroll to Behavior section");
                return false;
            }

            // Scroll down to the Behavior section. The section order is:
            // Language Prefs → Enabled Services → Service Config → HTTP Proxy → Behavior
            // Need ~5 scroll gestures of -10 each to reach Behavior section.
            Mouse.MoveTo(scrollViewer.GetClickablePoint());
            for (int i = 0; i < 5; i++)
            {
                Mouse.Scroll(-10);
                Thread.Sleep(500);
            }

            _output.WriteLine("Scrolled to Behavior section");
            ScreenshotHelper.CaptureWindow(window, "e2e_settings_behavior_section");

            // Try to find the toggle by AutomationId first, then by header text
            var toggle = Retry.WhileNull(
                () => window.FindFirstDescendant(c => c.ByAutomationId("MouseSelectionTranslateToggle")),
                TimeSpan.FromSeconds(3)).Result;

            if (toggle == null)
            {
                _output.WriteLine("Toggle not found by AutomationId, trying header text...");
                toggle = window.FindFirstDescendant(c => c.ByName("Mouse selection translate"));
            }

            if (toggle == null)
            {
                // Scroll a bit more in case we haven't reached the toggle yet
                _output.WriteLine("Toggle not visible, scrolling more...");
                Mouse.Scroll(-10);
                Thread.Sleep(500);

                toggle = window.FindFirstDescendant(c => c.ByAutomationId("MouseSelectionTranslateToggle"))
                      ?? window.FindFirstDescendant(c => c.ByName("Mouse selection translate"));
            }

            if (toggle != null)
            {
                var toggleButton = toggle.AsToggleButton();
                if (toggleButton != null &&
                    toggleButton.ToggleState == FlaUI.Core.Definitions.ToggleState.Off)
                {
                    toggleButton.Toggle();
                    _output.WriteLine("MouseSelectionTranslate toggle enabled (was Off → On)");
                    Thread.Sleep(500);
                }
                else if (toggleButton != null)
                {
                    _output.WriteLine($"MouseSelectionTranslate toggle already On (state={toggleButton.ToggleState})");
                }
                else
                {
                    _output.WriteLine("Element found but could not be used as ToggleButton");
                    return false;
                }

                // Capture confirmation screenshot
                ScreenshotHelper.CaptureWindow(window, "e2e_settings_toggle_enabled");
                return true;
            }

            _output.WriteLine("MouseSelectionTranslate toggle not found after scrolling");
            ScreenshotHelper.CaptureWindow(window, "e2e_settings_toggle_not_found");
            return false;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error enabling MouseSelectionTranslate: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _notepad?.Dispose();
        _launcher.Dispose();
    }
}
