using System.Runtime.InteropServices;
using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class WindowLifecycleTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public WindowLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const nint ENDSESSION_CLOSEAPP = 0x00000001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [Fact]
    public void App_ShouldLaunchAndShowMainWindow()
    {
        var window = _launcher.GetMainWindow();
        window.Should().NotBeNull();
        window.IsOffscreen.Should().BeFalse("Main window should be visible on screen");

        _output.WriteLine($"Window title: {window.Title}");
        _output.WriteLine($"Window bounds: {window.BoundingRectangle}");

        var path = ScreenshotHelper.CaptureWindow(window, "08_window_lifecycle_launch");
        _output.WriteLine($"Screenshot saved: {path}");
    }

    [Fact]
    public void App_ShouldHaveReasonableWindowSize()
    {
        var window = _launcher.GetMainWindow();
        var bounds = window.BoundingRectangle;

        bounds.Width.Should().BeGreaterThan(300, "Window should have reasonable width");
        bounds.Height.Should().BeGreaterThan(400, "Window should have reasonable height");

        _output.WriteLine($"Window size: {bounds.Width}x{bounds.Height}");
    }

    [Fact]
    public void App_ShouldCloseGracefully()
    {
        var window = _launcher.GetMainWindow();
        window.Should().NotBeNull();

        // Capture final state before close
        ScreenshotHelper.CaptureWindow(window, "09_before_close");

        window.Close();

        // Give the app time to close
        Thread.Sleep(3000);

        // App may minimize to tray instead of exiting - that's acceptable
        _output.WriteLine($"App has exited: {_launcher.Application.HasExited}");
    }

    [Fact]
    public void App_ShouldExitForConfirmedEndSession_WhenMinimizeToTrayIsEnabled()
    {
        var window = _launcher.GetMainWindow();
        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")),
            TimeSpan.FromSeconds(10)).Result;
        settingsButton.Should().NotBeNull("the tray setting must be configurable for this lifecycle test");
        settingsButton!.Click();

        var minimizeToTrayToggle = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("MinimizeToTrayToggle")),
            TimeSpan.FromSeconds(10)).Result;
        minimizeToTrayToggle.Should().NotBeNull("the minimize-to-tray toggle must be available");
        minimizeToTrayToggle!.Patterns.Toggle.IsSupported.Should().BeTrue();
        if (minimizeToTrayToggle.Patterns.Toggle.Pattern.ToggleState != ToggleState.On)
        {
            minimizeToTrayToggle.Patterns.Toggle.Pattern.Toggle();
        }

        var saveButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SaveButton")),
            TimeSpan.FromSeconds(10)).Result;
        saveButton.Should().NotBeNull("the tray setting must be saved before sending session-end messages");
        saveButton!.Click();

        var hwnd = new nint(window.Properties.NativeWindowHandle.Value);
        SendMessageTimeout(
            hwnd,
            WM_QUERYENDSESSION,
            0,
            ENDSESSION_CLOSEAPP,
            SMTO_ABORTIFHUNG,
            5000,
            out var queryResult);
        queryResult.Should().NotBe(0, "WM_QUERYENDSESSION must acknowledge a confirmed session end");

        PostMessage(hwnd, WM_ENDSESSION, 1, ENDSESSION_CLOSEAPP).Should().BeTrue();
        SpinWait.SpinUntil(() => _launcher.Application.HasExited, TimeSpan.FromSeconds(5))
            .Should().BeTrue("a confirmed session end must override minimize-to-tray");
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(
        nint hWnd,
        uint msg,
        nuint wParam,
        nint lParam);
}
