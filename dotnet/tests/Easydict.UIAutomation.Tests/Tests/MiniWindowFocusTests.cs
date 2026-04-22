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

        // Close the mini window with Escape
        _output.WriteLine("Closing mini window with Escape");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(1500);

        // Re-open
        _output.WriteLine("Second open: Ctrl+Alt+M");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);
        Thread.Sleep(3000);

        var miniWindowReopen = UITestHelper.FindSecondaryWindow(
            _launcher.Application, _launcher.Automation, "Mini", _output);
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

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
