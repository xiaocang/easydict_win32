using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class MainWindowTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public MainWindowTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void MainWindow_ShouldAppearOnLaunch()
    {
        var window = _launcher.GetMainWindow();
        window.Should().NotBeNull();
        window.Title.Should().Contain("Easydict");

        var path = ScreenshotHelper.CaptureWindow(window, "01_main_window_initial");
        _output.WriteLine($"Screenshot saved: {path}");

        var result = VisualRegressionHelper.CompareWithBaseline(path, "01_main_window_initial");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate");
        }
    }

    [Fact]
    public void MainWindow_ShouldHaveLanguageControls()
    {
        var window = _launcher.GetMainWindow();

        // Wait for UI to be ready
        Thread.Sleep(2000);

        // Find source language combo (by Name property which maps to x:Name)
        var sourceLangCombo = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SourceLangCombo"),
            TimeSpan.FromSeconds(10)).Result;

        // Find target language combo
        var targetLangCombo = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "TargetLangCombo"),
            TimeSpan.FromSeconds(5)).Result;

        // Find translate button
        var translateButton = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "TranslateButton"),
            TimeSpan.FromSeconds(5)).Result;

        var path = ScreenshotHelper.CaptureWindow(window, "02_main_window_controls");
        _output.WriteLine($"Screenshot saved: {path}");

        window.Should().NotBeNull();
        sourceLangCombo.Should().NotBeNull("source language control should be discoverable before capturing controls");
        targetLangCombo.Should().NotBeNull("target language control should be discoverable before capturing controls");
        translateButton.Should().NotBeNull("translate button should be discoverable before capturing controls");
    }

    [Fact]
    public void MainWindow_InputTextBox_ShouldAcceptText()
    {
        var window = _launcher.GetMainWindow();

        // Wait for UI to be fully loaded
        Thread.Sleep(2000);

        var inputBox = UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(10));
        inputBox.Should().NotBeNull("InputTextBox should be discoverable by AutomationId or Name");

        inputBox!.Text = "Hello World";
        Thread.Sleep(500);

        inputBox.Text.Should().Contain("Hello World", "input text should be committed before the screenshot is captured");

        var path = ScreenshotHelper.CaptureWindow(window, "03_main_window_text_input");
        _output.WriteLine($"Screenshot saved: {path}");

        var result = VisualRegressionHelper.CompareWithBaseline(path, "03_main_window_text_input");
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
    }

    [Fact]
    public void MainWindow_FullScreenshot_ShouldCapture()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Capture full screen to see the app in context
        var path = ScreenshotHelper.CaptureScreen("04_full_screen_with_app");
        _output.WriteLine($"Full screen screenshot saved: {path}");
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
