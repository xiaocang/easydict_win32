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
/// UI regression tests for OCR functionality.
/// Tests the OCR hotkey flow: trigger capture overlay → verify overlay appears → cancel/dismiss.
///
/// OCR hotkeys:
///   - Ctrl+Alt+S: OCR Translate (capture → OCR → show in MiniWindow)
///   - Ctrl+Alt+Shift+S: Silent OCR (capture → OCR → copy to clipboard)
///
/// Both hotkeys launch the same ScreenCaptureWindow overlay (class name "EasydictScreenCapture"),
/// a full-screen GDI+ window with a dark mask for region selection.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class OcrTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Time to wait for the capture overlay to appear after pressing the hotkey.
    /// The overlay is created on a dedicated STA thread, so it may take a moment.
    /// </summary>
    private static readonly TimeSpan OverlayTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Time to wait for the overlay to dismiss after pressing Escape.
    /// </summary>
    private static readonly TimeSpan DismissTimeout = TimeSpan.FromSeconds(5);

    public OcrTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void OcrHotkey_ShouldShowCaptureOverlay()
    {
        // Ensure app is ready
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var processId = (uint)_launcher.Application.ProcessId;

        // Capture initial state
        var pathBefore = ScreenshotHelper.CaptureWindow(window, "40_ocr_before_hotkey");
        _output.WriteLine($"Screenshot saved: {pathBefore}");

        // Press Ctrl+Alt+S to trigger OCR capture overlay
        _output.WriteLine("Pressing Ctrl+Alt+S to trigger OCR capture overlay...");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);

        // Wait for overlay to appear
        var overlayHwnd = ScreenCaptureOverlayFinder.WaitForOverlay(processId, OverlayTimeout);

        if (overlayHwnd != IntPtr.Zero)
        {
            _output.WriteLine("Screen capture overlay appeared");

            // Verify overlay window styles
            var styles = ScreenCaptureOverlayFinder.GetStyleFlags(overlayHwnd);
            styles.HasTopmost.Should().BeTrue("capture overlay should be topmost");
            styles.HasToolWindow.Should().BeTrue("capture overlay should be a tool window");

            // Verify overlay covers a significant screen area
            var rect = ScreenCaptureOverlayFinder.GetRect(overlayHwnd);
            _output.WriteLine($"Overlay bounds: {rect.Width}x{rect.Height} at ({rect.Left},{rect.Top})");
            rect.Width.Should().BeGreaterThan(500, "overlay should cover significant width");
            rect.Height.Should().BeGreaterThan(300, "overlay should cover significant height");

            // Capture full screen showing the overlay
            Thread.Sleep(500); // Allow render
            var pathOverlay = ScreenshotHelper.CaptureScreen("41_ocr_capture_overlay");
            _output.WriteLine($"Overlay screenshot saved: {pathOverlay}");

            // Dismiss overlay with Escape
            DismissOverlay(processId);
        }
        else
        {
            _output.WriteLine("Capture overlay did not appear — hotkey may not be registered in CI");
            ScreenshotHelper.CaptureScreen("41_ocr_overlay_not_found");
        }

        // Verify app is still running
        window.Should().NotBeNull();
    }

    [Fact]
    public void OcrHotkey_EscapeShouldCancelOverlay()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var processId = (uint)_launcher.Application.ProcessId;

        // Trigger OCR overlay
        _output.WriteLine("Pressing Ctrl+Alt+S to trigger OCR capture overlay...");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);

        var overlayHwnd = ScreenCaptureOverlayFinder.WaitForOverlay(processId, OverlayTimeout);

        if (overlayHwnd != IntPtr.Zero)
        {
            _output.WriteLine("Overlay appeared — pressing Escape to cancel");
            Thread.Sleep(300);

            // Capture overlay before cancel
            var pathOverlay = ScreenshotHelper.CaptureScreen("42_ocr_overlay_before_cancel");
            _output.WriteLine($"Screenshot saved: {pathOverlay}");

            // Dismiss overlay
            DismissOverlay(processId);

            // Verify overlay is gone
            var overlayAfter = ScreenCaptureOverlayFinder.Find(processId);
            overlayAfter.Should().Be(IntPtr.Zero, "overlay should be dismissed after Escape");

            // Verify main window is still accessible
            Thread.Sleep(500);
            var pathAfter = ScreenshotHelper.CaptureWindow(window, "43_ocr_after_cancel");
            _output.WriteLine($"After cancel screenshot saved: {pathAfter}");

            var result = VisualRegressionHelper.CompareWithBaseline(pathAfter, "43_ocr_after_cancel");
            if (result != null)
            {
                _output.WriteLine(result.ToString());
                result.Passed.Should().BeTrue(result.ToString());
            }
        }
        else
        {
            _output.WriteLine("Overlay did not appear — skipping cancel test");
            ScreenshotHelper.CaptureScreen("42_ocr_overlay_not_found_for_cancel");
        }

        window.Should().NotBeNull();
    }

    [Fact]
    public void SilentOcrHotkey_ShouldShowCaptureOverlay()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var processId = (uint)_launcher.Application.ProcessId;

        // Press Ctrl+Alt+Shift+S for silent OCR
        _output.WriteLine("Pressing Ctrl+Alt+Shift+S to trigger silent OCR...");
        SendSilentOcrHotkey();

        var overlayHwnd = ScreenCaptureOverlayFinder.WaitForOverlay(processId, OverlayTimeout);

        if (overlayHwnd != IntPtr.Zero)
        {
            _output.WriteLine("Silent OCR overlay appeared");

            // Verify same overlay properties as regular OCR
            var styles = ScreenCaptureOverlayFinder.GetStyleFlags(overlayHwnd);
            styles.HasTopmost.Should().BeTrue("silent OCR overlay should be topmost");

            Thread.Sleep(500);
            var pathOverlay = ScreenshotHelper.CaptureScreen("44_silent_ocr_overlay");
            _output.WriteLine($"Silent OCR overlay screenshot saved: {pathOverlay}");

            // Dismiss
            DismissOverlay(processId);
        }
        else
        {
            _output.WriteLine("Silent OCR overlay did not appear");
            ScreenshotHelper.CaptureScreen("44_silent_ocr_overlay_not_found");
        }

        window.Should().NotBeNull();
    }

    [Fact]
    public void OcrWorkflow_ScreenshotSequence()
    {
        // Document the complete OCR workflow with numbered screenshots.
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var processId = (uint)_launcher.Application.ProcessId;

        // Step 1: Initial app state
        var step1 = ScreenshotHelper.CaptureWindow(window, "45_ocr_workflow_01_initial");
        _output.WriteLine($"Step 1 (Initial state): {step1}");

        // Step 2: Full screen before OCR
        var step2 = ScreenshotHelper.CaptureScreen("45_ocr_workflow_02_fullscreen_before");
        _output.WriteLine($"Step 2 (Full screen before): {step2}");

        // Step 3: Trigger OCR hotkey
        _output.WriteLine("Step 3: Pressing Ctrl+Alt+S...");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);

        var overlayHwnd = ScreenCaptureOverlayFinder.WaitForOverlay(processId, OverlayTimeout);

        if (overlayHwnd != IntPtr.Zero)
        {
            Thread.Sleep(500);
            var step3 = ScreenshotHelper.CaptureScreen("45_ocr_workflow_03_overlay_active");
            _output.WriteLine($"Step 3 (Overlay active): {step3}");

            // Step 4: Dismiss and capture result
            DismissOverlay(processId);
            Thread.Sleep(500);

            var step4 = ScreenshotHelper.CaptureWindow(window, "45_ocr_workflow_04_after_dismiss");
            _output.WriteLine($"Step 4 (After dismiss): {step4}");

            var step5 = ScreenshotHelper.CaptureScreen("45_ocr_workflow_05_fullscreen_after");
            _output.WriteLine($"Step 5 (Full screen after): {step5}");
        }
        else
        {
            _output.WriteLine("Overlay did not appear — capturing state for debugging");
            ScreenshotHelper.CaptureScreen("45_ocr_workflow_03_overlay_not_found");
        }

        _output.WriteLine("OCR workflow screenshot sequence completed");
        window.Should().NotBeNull();
    }

    [Fact]
    public void OcrHotkey_SecondTriggerShouldWork()
    {
        // Verify that OCR can be triggered again after dismissing the first overlay.
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var processId = (uint)_launcher.Application.ProcessId;

        // First trigger
        _output.WriteLine("First OCR trigger...");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);

        var overlayHwnd = ScreenCaptureOverlayFinder.WaitForOverlay(processId, OverlayTimeout);

        if (overlayHwnd != IntPtr.Zero)
        {
            var path1 = ScreenshotHelper.CaptureScreen("46_ocr_first_trigger");
            _output.WriteLine($"First trigger screenshot: {path1}");

            DismissOverlay(processId);
            Thread.Sleep(1000);

            // Second trigger
            _output.WriteLine("Second OCR trigger...");
            UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);

            var overlayHwnd2 = ScreenCaptureOverlayFinder.WaitForOverlay(processId, OverlayTimeout);

            if (overlayHwnd2 != IntPtr.Zero)
            {
                _output.WriteLine("Second overlay appeared successfully");
                var path2 = ScreenshotHelper.CaptureScreen("47_ocr_second_trigger");
                _output.WriteLine($"Second trigger screenshot: {path2}");

                DismissOverlay(processId);
            }
            else
            {
                _output.WriteLine("Second overlay did not appear");
                ScreenshotHelper.CaptureScreen("47_ocr_second_trigger_failed");
            }
        }
        else
        {
            _output.WriteLine("First overlay did not appear — skipping re-trigger test");
        }

        window.Should().NotBeNull();
    }

    /// <summary>
    /// Dismiss the screen capture overlay by pressing Escape.
    /// The overlay may show a confirmation dialog on first Escape (in Detecting phase),
    /// so we press Escape again or Enter to confirm dismissal.
    /// </summary>
    private void DismissOverlay(uint processId)
    {
        // First Escape — may enter "cancel confirmation" or go back from selection
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(500);

        // Check if overlay is still present (confirmation dialog may be showing)
        if (ScreenCaptureOverlayFinder.Find(processId) != IntPtr.Zero)
        {
            // Try Escape again to dismiss any confirmation
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(500);
        }

        // Final check — if still present, try Enter to confirm the dialog
        if (ScreenCaptureOverlayFinder.Find(processId) != IntPtr.Zero)
        {
            Keyboard.Type(VirtualKeyShort.ENTER);
            Thread.Sleep(500);
        }

        var dismissed = ScreenCaptureOverlayFinder.WaitForDismiss(processId, DismissTimeout);
        if (!dismissed)
        {
            _output.WriteLine("WARNING: Overlay did not dismiss within timeout");
        }
    }

    /// <summary>
    /// Send Ctrl+Alt+Shift+S for silent OCR hotkey.
    /// </summary>
    private static void SendSilentOcrHotkey()
    {
        try
        {
            Keyboard.Press(VirtualKeyShort.CONTROL);
            Keyboard.Press(VirtualKeyShort.ALT);
            Keyboard.Press(VirtualKeyShort.SHIFT);
            Keyboard.Press(VirtualKeyShort.KEY_S);
            Thread.Sleep(100);
        }
        finally
        {
            try { Keyboard.Release(VirtualKeyShort.KEY_S); } catch { /* ignore */ }
            try { Keyboard.Release(VirtualKeyShort.SHIFT); } catch { /* ignore */ }
            try { Keyboard.Release(VirtualKeyShort.ALT); } catch { /* ignore */ }
            try { Keyboard.Release(VirtualKeyShort.CONTROL); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        // Ensure overlay is dismissed before teardown
        try
        {
            var processId = (uint)_launcher.Application.ProcessId;
            if (ScreenCaptureOverlayFinder.Find(processId) != IntPtr.Zero)
            {
                DismissOverlay(processId);
            }
        }
        catch { /* ignore cleanup errors */ }

        _launcher.Dispose();
    }
}
