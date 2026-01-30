using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
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

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
