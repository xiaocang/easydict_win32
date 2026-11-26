using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// UI regression tests that verify the MiniWindow lookup field receives keyboard focus
/// immediately when the window is brought up via the hotkey (regression for issue #159 —
/// focus was not at the lookup field in v0.7.1).
///
/// Prerequisites:
/// - Real Windows desktop environment (not headless)
/// - Easydict installed (MSIX) or built (exe)
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class MiniWindowFocusTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public MiniWindowFocusTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    /// <summary>
    /// Regression test: when the MiniWindow is opened via Ctrl+Alt+M the InputTextBox
    /// must be visible (not collapsed) so the user can type immediately.
    /// Covers the bug introduced in v0.7.1 where ShowAndActivate called
    /// SetSourceTextState(false) which hid the InputTextBox before QueueInputFocusAndSelectAll
    /// tried to focus it, causing all focus attempts to silently fail.
    /// </summary>
    [Fact]
    public void MiniWindow_OnOpen_InputTextBoxIsVisible()
    {
        // Ensure app is ready
        _ = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        _output.WriteLine("Opening mini window with Ctrl+Alt+M");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);

        Thread.Sleep(3000);

        var miniWindow = UITestHelper.FindSecondaryWindow(
            _launcher.Application, _launcher.Automation, "Mini", _output);

        miniWindow.Should().NotBeNull("Mini window must open after Ctrl+Alt+M hotkey");

        miniWindow!.SetForeground();
        Thread.Sleep(500);

        // The InputTextBox must be directly visible (not collapsed behind SourceTextCollapsed)
        // without requiring a click on the source-text container.
        var inputBox = miniWindow.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox();

        inputBox.Should().NotBeNull("InputTextBox must exist in the mini window");
        inputBox!.IsOffscreen.Should().BeFalse(
            "InputTextBox must be visible when mini window opens so the user can type immediately");

        var path = ScreenshotHelper.CaptureWindow(miniWindow, "50_mini_focus_on_open");
        _output.WriteLine($"Screenshot: {path}");
    }

    /// <summary>
    /// Regression test: when the MiniWindow is re-opened after being used (so that
    /// the source-text container is in collapsed state), the InputTextBox must still
    /// be visible and focusable without requiring a manual click.
    /// </summary>
    [Fact]
    public void MiniWindow_OnReopen_InputTextBoxIsVisible()
    {
        // Ensure app is ready
        _ = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // First open
        _output.WriteLine("First open: Ctrl+Alt+M");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);
        Thread.Sleep(3000);

        var miniWindow = UITestHelper.FindSecondaryWindow(
            _launcher.Application, _launcher.Automation, "Mini", _output);
        miniWindow.Should().NotBeNull("Mini window must open on first hotkey");
        miniWindow!.SetForeground();
        Thread.Sleep(500);

        // Type some text so that the source-text surface collapses on close
        var inputBox = UITestHelper.FindInputTextBox(miniWindow);
        inputBox.Should().NotBeNull();
        inputBox!.Click();
        Thread.Sleep(200);
        Keyboard.Type("hello");
        Thread.Sleep(300);

        _output.WriteLine("Closing mini window with its close button");
        CloseMiniWindow(miniWindow);
        TestRetry.Until(
                () => FindMiniWindowCandidate() == null,
                TimeSpan.FromSeconds(5))
            .Should().BeTrue("Mini window must be hidden before validating the second hotkey opens it again");

        // Re-open
        _output.WriteLine("Second open: Ctrl+Alt+M");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);

        var miniWindowReopen = WaitForMiniWindow(TimeSpan.FromSeconds(5));
        miniWindowReopen.Should().NotBeNull("Mini window must open on second hotkey");
        miniWindowReopen!.SetForeground();
        Thread.Sleep(500);

        // InputTextBox must be visible without a click even after the window was re-shown
        var inputBoxReopen = miniWindowReopen.FindFirstDescendant(
            cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox();

        inputBoxReopen.Should().NotBeNull("InputTextBox must exist after re-open");
        inputBoxReopen!.IsOffscreen.Should().BeFalse(
            "InputTextBox must be visible on re-open so the user can type immediately");

        var path = ScreenshotHelper.CaptureWindow(miniWindowReopen, "51_mini_focus_on_reopen");
        _output.WriteLine($"Screenshot: {path}");
    }

    /// <summary>
    /// Exercise the empty-selection hotkey path with real keyboard input. This test
    /// never activates the mini window or focuses its input on the app's behalf.
    /// Timing is diagnostic: UIA discovery/polling and shortcut delivery contribute
    /// to these observations, so they are not precise product latency measurements.
    /// </summary>
    [Fact]
    public void MiniWindow_EmptySelection_AcceptsTypingWithoutFocusAssistanceOnOpenAndReopen()
    {
        _ = _launcher.GetMainWindow();
        using var source = new NotepadTestTarget(
            "Source text with no selection",
            Environment.GetEnvironmentVariable("EASYDICT_UIA_TEXT_TARGET_EXE"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            source.BringToForeground();
            var sourceWindow = source.GetWindow();
            var editor = sourceWindow.FindFirstDescendant(cf =>
                cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)
                    .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Document)));
            editor.Should().NotBeNull("the source must expose a text editor");
            editor!.Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);

            TestRetry.Until(
                    () => editor.Properties.HasKeyboardFocus.ValueOrDefault &&
                          GetForegroundWindow() == sourceWindow.Properties.NativeWindowHandle.Value,
                    TimeSpan.FromSeconds(5), pollIntervalMs: 20)
                .Should().BeTrue("the shortcut must originate from the source editor");
            editor.Patterns.Text.IsSupported.Should().BeTrue(
                "this regression requires a source with a confirmed empty UIA selection");
            editor.Patterns.Text.Pattern.GetSelection()
                .All(range => string.IsNullOrEmpty(range.GetText(-1))).Should().BeTrue(
                    "Ctrl+End must collapse the source selection before opening the mini window");

            var timer = Stopwatch.StartNew();
            UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);
            Window? miniWindow = null;
            TestRetry.Until(() =>
                {
                    miniWindow = FindMiniWindowCandidate();
                    return miniWindow != null &&
                           IsWindowVisible(miniWindow.Properties.NativeWindowHandle.Value);
                }, TimeSpan.FromSeconds(5), pollIntervalMs: 20)
                .Should().BeTrue("the registered hotkey must show the mini window");
            var visibleObservedMs = timer.ElapsedMilliseconds;
            var miniHwnd = miniWindow!.Properties.NativeWindowHandle.Value;

            TextBox? input = null;
            TestRetry.Until(() =>
                {
                    input ??= miniWindow.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox();
                    return GetForegroundWindow() == miniHwnd && input != null &&
                           !input.IsOffscreen && input.Properties.HasKeyboardFocus.ValueOrDefault;
                }, TimeSpan.FromSeconds(5), pollIntervalMs: 20)
                .Should().BeTrue("Easydict must activate and focus the input without test assistance");
            var inputReadyObservedMs = timer.ElapsedMilliseconds;

            var prefix = $"first{attempt}";
            const string suffix = "continued";
            Keyboard.Type(prefix);
            TestRetry.Until(() => input!.Text == prefix, TimeSpan.FromSeconds(2), pollIntervalMs: 20)
                .Should().BeTrue("the first keystrokes must reach the input as soon as focus is observed");

            // Cross the former 9 x 50 ms retry interval before continuing to type.
            // A late SelectAll would replace the prefix with this suffix.
            Thread.Sleep(600);
            GetForegroundWindow().Should().Be(miniHwnd);
            input!.Properties.HasKeyboardFocus.ValueOrDefault.Should().BeTrue();
            Keyboard.Type(suffix);
            TestRetry.Until(() => input.Text == prefix + suffix, TimeSpan.FromSeconds(2), pollIntervalMs: 20)
                .Should().BeTrue("late focus retries must not reselect or replace text already typed");
            _output.WriteLine(
                $"Empty selection {(attempt == 0 ? "first open" : "warm reopen")}: " +
                $"visible observed={visibleObservedMs}ms, keyboard focus observed={inputReadyObservedMs}ms, " +
                $"observed visible-to-focus={inputReadyObservedMs - visibleObservedMs}ms. " +
                "Includes UIA polling and hotkey delivery overhead; no sub-second timing assertion.");

            UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);
            TestRetry.Until(() => !IsWindowVisible(miniHwnd), TimeSpan.FromSeconds(5), pollIntervalMs: 20)
                .Should().BeTrue("the next iteration must reopen a hidden mini window");
        }
    }

    private void CloseMiniWindow(Window miniWindow)
    {
        var closeButton = miniWindow.FindFirstDescendant(cf => cf.ByAutomationId("MiniWindowCloseButton"))
            ?? miniWindow.FindFirstDescendant(cf => cf.ByName("Close"));
        closeButton.Should().NotBeNull("Mini window must expose a close button for deterministic UIA teardown");
        UITestHelper.ClickElement(closeButton!);
    }

    private Window? WaitForMiniWindow(TimeSpan timeout)
    {
        return Retry.WhileNull(
            FindMiniWindowCandidate,
            timeout).Result;
    }

    private Window? FindMiniWindowCandidate()
    {
        try
        {
            return _launcher.Application
                .GetAllTopLevelWindows(_launcher.Automation)
                .FirstOrDefault(IsMiniWindowCandidate);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMiniWindowCandidate(Window window)
    {
        var title = SafeName(window);
        if (title.Contains("Quick Translate", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Mini", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return UITestHelper.FindByAutomationIdOrName(window, "InputTextBox") != null &&
               UITestHelper.FindByAutomationIdOrName(window, "SettingsButton") == null;
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
}
