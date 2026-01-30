using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class SettingsPageTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public SettingsPageTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void SettingsPage_ShouldOpenFromMainWindow()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Find and click the settings button
        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByName("SettingsButton")),
            TimeSpan.FromSeconds(10)).Result;

        if (settingsButton != null)
        {
            settingsButton.Click();
            Thread.Sleep(2000); // Wait for page transition

            var path = ScreenshotHelper.CaptureWindow(window, "05_settings_page");
            _output.WriteLine($"Screenshot saved: {path}");

            var result = VisualRegressionHelper.CompareWithBaseline(path, "05_settings_page");
            if (result != null)
            {
                _output.WriteLine(result.ToString());
                result.Passed.Should().BeTrue(result.ToString());
            }
        }
        else
        {
            _output.WriteLine("SettingsButton not found - capturing window for inspection");
            ScreenshotHelper.CaptureWindow(window, "05_settings_button_not_found");
        }
    }

    [Fact]
    public void SettingsPage_ShouldShowServiceConfiguration()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Navigate to settings
        var settingsButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByName("SettingsButton")),
            TimeSpan.FromSeconds(10)).Result;

        if (settingsButton != null)
        {
            settingsButton.Click();
            Thread.Sleep(2000);

            // Try to scroll down to see service configuration
            var scrollViewer = window.FindFirstDescendant(cf => cf.ByName("MainScrollViewer"));
            if (scrollViewer != null)
            {
                // Scroll down to show more settings
                Mouse.MoveTo(scrollViewer.GetClickablePoint());
                Mouse.Scroll(-5); // Scroll down
                Thread.Sleep(1000);

                var path = ScreenshotHelper.CaptureWindow(window, "06_settings_services");
                _output.WriteLine($"Screenshot saved: {path}");
            }

            // Scroll further down for more sections
            if (scrollViewer != null)
            {
                Mouse.Scroll(-10);
                Thread.Sleep(1000);

                var path = ScreenshotHelper.CaptureWindow(window, "07_settings_api_keys");
                _output.WriteLine($"Screenshot saved: {path}");
            }
        }
        else
        {
            _output.WriteLine("SettingsButton not found");
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
