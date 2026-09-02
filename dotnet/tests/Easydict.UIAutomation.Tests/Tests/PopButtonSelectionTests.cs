using System.Drawing;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
///
/// Uses IClassFixture to share a single Easydict + Notepad instance across all tests.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class PopButtonSelectionTests : IClassFixture<PopButtonSelectionFixture>
{
    private readonly PopButtonSelectionFixture _fixture;
    private readonly ITestOutputHelper _output;

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

    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const uint MouseEventAbsolute = 0x8000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public PopButtonSelectionTests(PopButtonSelectionFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;

        // Dump fixture setup log for the first test that runs
        foreach (var msg in _fixture.SetupLog)
        {
            _output.WriteLine($"[Fixture] {msg}");
        }
    }

    [Fact]
    public void DragSelect_InNotepad_PopButtonAppears()
    {
        BringNotepadToForeground();
        var bounds = _fixture.Notepad.GetTextBounds();
        _output.WriteLine($"Text area bounds: {bounds}");

        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        var endX = startX + DragDistance;
        var endY = startY;
        _output.WriteLine($"Simulating drag from ({startX},{startY}) to ({endX},{endY})");

        SimulateDragSelect(startX, startY, endX, endY);
        var releasePoint = PopButtonFinder.GetCursorPosition();

        var popHwnd = RequirePopButton(PopButtonTimeout, "a Notepad drag selection");
        var screenshotPath = ScreenshotHelper.CaptureScreen("e2e_drag_select_result");
        _output.WriteLine($"Screenshot: {screenshotPath}");
        _output.WriteLine($"PopButton found at hwnd=0x{popHwnd:X}");

        var rect = PopButtonFinder.GetRect(popHwnd);
        _output.WriteLine($"PopButton rect: Left={rect.Left} Top={rect.Top} W={rect.Width} H={rect.Height}");

        var dx = Math.Abs(rect.CenterX - releasePoint.X);
        var dy = Math.Abs(rect.CenterY - releasePoint.Y);
        _output.WriteLine($"Distance from physical mouse release {releasePoint}: dx={dx} dy={dy}");
        dx.Should().BeLessThan(80, "PopButton X should be near mouse release X");
        dy.Should().BeLessThan(80, "PopButton Y should be near mouse release Y");

        var styles = PopButtonFinder.GetStyleFlags(popHwnd);
        styles.HasNoActivate.Should().BeTrue("PopButton must have WS_EX_NOACTIVATE");
        styles.HasToolWindow.Should().BeTrue("PopButton must have WS_EX_TOOLWINDOW");
        styles.HasTopmost.Should().BeTrue("PopButton must have WS_EX_TOPMOST");
        rect.Width.Should().BeInRange(20, 128, "PopButton width should be 30 logical pixels at the active DPI");
        rect.Height.Should().BeInRange(20, 128, "PopButton height should be 30 logical pixels at the active DPI");

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

    [Fact]
    public void DoubleClick_InNotepad_PopButtonAppears()
    {
        BringNotepadToForeground();
        var bounds = _fixture.Notepad.GetTextBounds();
        var clickPoint = new Point(bounds.Left + 40, bounds.Top + TextAreaPadding);
        _output.WriteLine($"Simulating double-click at ({clickPoint.X},{clickPoint.Y})");

        Mouse.DoubleClick(clickPoint);

        var popHwnd = RequirePopButton(
            TimeSpan.FromSeconds(5),
            "a Notepad double-click selection");
        var screenshotPath = ScreenshotHelper.CaptureScreen("e2e_double_click_result");
        _output.WriteLine($"Screenshot: {screenshotPath}");
        PopButtonFinder.IsVisible(popHwnd).Should().BeTrue("PopButton should be visible");
    }

    [Fact]
    public void PopButton_AutoDismisses_After5Seconds()
    {
        BringNotepadToForeground();
        var bounds = _fixture.Notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = RequirePopButton(PopButtonTimeout, "the auto-dismiss scenario");
        _output.WriteLine($"PopButton visible at hwnd=0x{popHwnd:X}, waiting for auto-dismiss...");

        Thread.Sleep(4000);
        var stillVisible = PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After 4s: visible={stillVisible}");
        stillVisible.Should().BeTrue("PopButton should still be visible before 5s timeout");

        Thread.Sleep(2000);
        var dismissed = !PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After 6s: dismissed={dismissed}");
        dismissed.Should().BeTrue("PopButton should auto-dismiss after 5s (AutoDismissMs=5000)");
    }

    [Fact]
    public void PopButton_DismissesOnScroll()
    {
        BringNotepadToForeground();
        var bounds = _fixture.Notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = RequirePopButton(PopButtonTimeout, "the scroll-dismiss scenario");
        _output.WriteLine("PopButton visible, sending scroll...");

        Mouse.Scroll(3);
        Thread.Sleep(500);

        var dismissed = !PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After scroll: dismissed={dismissed}");
        dismissed.Should().BeTrue("PopButton should dismiss on mouse scroll");
    }

    [Fact]
    public void PopButton_DismissesOnRightClick()
    {
        BringNotepadToForeground();
        var bounds = _fixture.Notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = RequirePopButton(PopButtonTimeout, "the right-click-dismiss scenario");
        _output.WriteLine("PopButton visible, sending right-click...");

        var rect = PopButtonFinder.GetRect(popHwnd);
        Mouse.RightClick(new Point(rect.Left + 100, rect.Top + 100));
        Thread.Sleep(500);

        var dismissed = !PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After right-click: dismissed={dismissed}");
        dismissed.Should().BeTrue("PopButton should dismiss on right-click");

        // RightClick opens Notepad's native context menu. Close it so the shared
        // selection target starts the next workflow without a menu owning focus.
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
    }

    [Fact]
    public void PopButton_DismissesOnKeyPress()
    {
        BringNotepadToForeground();
        var bounds = _fixture.Notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = RequirePopButton(PopButtonTimeout, "the key-dismiss scenario");
        _output.WriteLine("PopButton visible, pressing Escape...");

        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(50);
        Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(500);

        var dismissed = !PopButtonFinder.IsVisible(popHwnd);
        _output.WriteLine($"After key press: dismissed={dismissed}");
        dismissed.Should().BeTrue("PopButton should dismiss on key press");
    }

    [Fact]
    public void PopButton_Click_OpensMiniWindow()
    {
        BringNotepadToForeground();
        var bounds = _fixture.Notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = RequirePopButton(PopButtonTimeout, "the click-to-mini-window scenario");
        _output.WriteLine($"PopButton visible at hwnd=0x{popHwnd:X}, clicking...");

        var rect = PopButtonFinder.GetRect(popHwnd);
        Mouse.Click(new Point(rect.CenterX, rect.CenterY));
        _output.WriteLine($"Clicked PopButton at ({rect.CenterX},{rect.CenterY})");

        var miniWindow = Retry.WhileNull(
            () => UITestHelper.FindSecondaryWindow(
                _fixture.Launcher.Application,
                _fixture.Launcher.Automation,
                "Mini",
                _output),
            TimeSpan.FromSeconds(5)).Result;

        var screenshotPath = ScreenshotHelper.CaptureScreen("e2e_pop_button_click_mini_window");
        _output.WriteLine($"Screenshot: {screenshotPath}");
        miniWindow.Should().NotBeNull("clicking a visible PopButton should open the Mini window");
        _output.WriteLine($"Mini window found: \"{miniWindow!.Title}\" size={miniWindow.BoundingRectangle.Width}x{miniWindow.BoundingRectangle.Height}");
        ScreenshotHelper.CaptureWindow(miniWindow, "e2e_mini_window_after_popup_click");
        PopButtonFinder.IsVisible(popHwnd).Should().BeFalse("PopButton should hide after being clicked");
    }

    [Fact]
    public void DragSelect_ScreenshotSequence_FullWorkflow()
    {
        var window = _fixture.Launcher.GetMainWindow();
        var step1 = ScreenshotHelper.CaptureWindow(window, "e2e_workflow_01_easydict_initial");
        _output.WriteLine($"Step 1 (Easydict): {step1}");

        BringNotepadToForeground();
        var step2 = ScreenshotHelper.CaptureScreen("e2e_workflow_02_notepad_ready");
        _output.WriteLine($"Step 2 (Notepad): {step2}");

        var bounds = _fixture.Notepad.GetTextBounds();
        var startX = bounds.Left + TextAreaPadding;
        var startY = bounds.Top + TextAreaPadding;
        SimulateDragSelect(startX, startY, startX + DragDistance, startY);

        var popHwnd = RequirePopButton(PopButtonTimeout, "the screenshot workflow");
        var step3 = ScreenshotHelper.CaptureScreen("e2e_workflow_03_pop_button_visible");
        _output.WriteLine($"Step 3 (PopButton visible): {step3}");

        var vrResult = VisualRegressionHelper.CompareWithBaseline(
            step3, "e2e_workflow_03_pop_button_visible", thresholdPercent: 10.0);
        if (vrResult != null)
        {
            _output.WriteLine(vrResult.ToString());
        }
        else
        {
            _output.WriteLine("No baseline — saved as candidate");
        }

        var popRect = PopButtonFinder.GetRect(popHwnd);
        Mouse.Click(new Point(popRect.CenterX, popRect.CenterY));
        var miniWindow = Retry.WhileNull(
            () => UITestHelper.FindSecondaryWindow(
                _fixture.Launcher.Application,
                _fixture.Launcher.Automation,
                "Mini",
                _output),
            TimeSpan.FromSeconds(5)).Result;
        miniWindow.Should().NotBeNull("the full workflow should end in a visible Mini window");

        var step4 = ScreenshotHelper.CaptureScreen("e2e_workflow_04_mini_window_visible");
        _output.WriteLine($"Step 4 (Mini window visible): {step4}");
    }

    private IntPtr RequirePopButton(TimeSpan timeout, string scenario)
    {
        var hwnd = PopButtonFinder.WaitForPopButton(_fixture.EasydictProcessId, timeout);
        if (hwnd == IntPtr.Zero)
        {
            var diagnosticPath = ScreenshotHelper.CaptureScreen("e2e_pop_button_missing_after_selection");
            _output.WriteLine($"PopButton missing diagnostic: {diagnosticPath}");
        }

        hwnd.Should().NotBe(IntPtr.Zero, $"MouseSelectionTranslate is enabled, so {scenario} should show the PopButton");
        return hwnd;
    }

    /// <summary>
    /// Simulate a drag-select gesture using FlaUI Mouse with intermediate moves.
    /// Intermediate moves ensure WH_MOUSE_LL receives enough WM_MOUSEMOVE messages
    /// for the DragDetector to detect the drag threshold being exceeded.
    /// </summary>
    private void BringNotepadToForeground()
    {
        _fixture.Notepad.BringToForeground();
        Thread.Sleep(300);
    }

    private void SimulateDragSelect(int startX, int startY, int endX, int endY)
    {
        // FlaUI documents Mouse.Position/MoveTo as direct cursor positioning. That is
        // appropriate for UIA targeting but does not exercise WH_MOUSE_LL move callbacks.
        // Inject the actual move sequence so the end-to-end test covers the global hook.
        SendMouseMove(startX, startY);
        Thread.Sleep(100);

        SendMouseInput(MouseEventLeftDown);
        Thread.Sleep(50);

        var totalDistance = Math.Abs(endX - startX) + Math.Abs(endY - startY);
        var steps = Math.Max(totalDistance / 10, 2);
        for (int i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            SendMouseMove(
                (int)(startX + (endX - startX) * t),
                (int)(startY + (endY - startY) * t));
            Thread.Sleep(10);
        }

        Thread.Sleep(50);
        SendMouseInput(MouseEventLeftUp);
    }

    private static void SendMouseMove(int x, int y)
    {
        var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        var virtualTop = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);

        if (virtualWidth <= 1 || virtualHeight <= 1)
            throw new InvalidOperationException("Virtual screen dimensions are invalid.");

        var normalizedX = (int)Math.Round((x - virtualLeft) * 65535d / (virtualWidth - 1));
        var normalizedY = (int)Math.Round((y - virtualTop) * 65535d / (virtualHeight - 1));
        SendMouseInput(
            MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk,
            normalizedX,
            normalizedY);
    }

    private static void SendMouseInput(uint flags, int dx = 0, int dy = 0)
    {
        var inputs = new[]
        {
            new Input
            {
                Type = InputMouse,
                Mouse = new MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    Flags = flags
                }
            }
        };

        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed.");
    }
}
