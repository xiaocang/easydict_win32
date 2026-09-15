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
    private const uint WM_CLOSE = 0x0010;
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
    public void IsolatedExeLaunches_ShouldOwnDistinctMainWindows()
    {
        var exePath = Environment.GetEnvironmentVariable("EASYDICT_EXE_PATH");
        exePath.Should().NotBeNullOrWhiteSpace("isolated launch tests require the UI automation build's EXE");
        var firstWindow = _launcher.GetMainWindow();
        using var secondLauncher = new AppLauncher();
        secondLauncher.LaunchFromExe(exePath!, TimeSpan.FromSeconds(15));
        var secondWindow = secondLauncher.GetMainWindow();

        secondLauncher.Application.ProcessId.Should().NotBe(_launcher.Application.ProcessId);
        firstWindow.Properties.ProcessId.Value.Should().Be(_launcher.Application.ProcessId);
        secondWindow.Properties.ProcessId.Value.Should().Be(secondLauncher.Application.ProcessId);
        secondWindow.Properties.NativeWindowHandle.Value.Should().NotBe(firstWindow.Properties.NativeWindowHandle.Value);
        _launcher.Application.HasExited.Should().BeFalse("launching another test instance must preserve the first");
    }

    [Fact]
    public void App_ShouldCloseGracefully()
    {
        var window = _launcher.GetMainWindow();
        window.Should().NotBeNull();

        // Capture final state before close
        ScreenshotHelper.CaptureWindow(window, "09_before_close");

        // WindowPattern.Close is a synchronous cross-process UIA call. WinUI can
        // tear down its provider while servicing it, hanging the test host. Post
        // the normal close message and observe the HWND without calling UIA again.
        var hwnd = new nint(window.Properties.NativeWindowHandle.Value);
        PostMessage(hwnd, WM_CLOSE, 0, 0).Should().BeTrue();
        Retry.WhileFalse(
                () => _launcher.Application.HasExited || !IsWindowVisible(hwnd),
                TimeSpan.FromSeconds(5))
            .Result.Should().BeTrue("closing must exit the app or hide its window in the tray within 5s");

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
        UITestHelper.ClickElement(settingsButton!);

        var generalTab = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsTab_General")),
            TimeSpan.FromSeconds(10)).Result;
        generalTab.Should().NotBeNull("the General settings tab must be available");
        UITestHelper.ClickElement(generalTab!);
        Retry.WhileNull(
                () => window.FindFirstDescendant(
                    cf => cf.ByAutomationId("SettingsGeneralBehaviorHeader")),
                TimeSpan.FromSeconds(10))
            .Result
            .Should()
            .NotBeNull("clicking General must reveal the behavior settings");

        var minimizeToTrayToggle = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("MinimizeToTrayToggle")),
            TimeSpan.FromSeconds(10)).Result;
        minimizeToTrayToggle.Should().NotBeNull("the minimize-to-tray toggle must be available");
        minimizeToTrayToggle!.Patterns.Toggle.IsSupported.Should().BeTrue();
        if (minimizeToTrayToggle.Patterns.Toggle.Pattern.ToggleState != ToggleState.On)
        {
            minimizeToTrayToggle.Patterns.Toggle.Pattern.Toggle();

            var saveButton = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("SaveButton")),
                TimeSpan.FromSeconds(10)).Result;
            saveButton.Should().NotBeNull("the tray setting must be saved before sending session-end messages");
            saveButton!.Click();
        }

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);
}
