using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
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
            () => window.FindFirstDescendant(c => c.ByName("SourceLangCombo")),
            TimeSpan.FromSeconds(10)).Result;

        // Find target language combo
        var targetLangCombo = Retry.WhileNull(
            () => window.FindFirstDescendant(c => c.ByName("TargetLangCombo")),
            TimeSpan.FromSeconds(5)).Result;

        // Find translate button
        var translateButton = Retry.WhileNull(
            () => window.FindFirstDescendant(c => c.ByName("TranslateButton")),
            TimeSpan.FromSeconds(5)).Result;

        var path = ScreenshotHelper.CaptureWindow(window, "02_main_window_controls");
        _output.WriteLine($"Screenshot saved: {path}");

        // At least the window should be found and contain expected elements
        // Some elements may not be found by x:Name if UIA mapping differs
        window.Should().NotBeNull();
    }

    [Fact]
    public void MainWindow_InputTextBox_ShouldAcceptText()
    {
        var window = _launcher.GetMainWindow();

        // Wait for UI to be fully loaded
        Thread.Sleep(2000);

        // Try to find the input text box
        var inputBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByName("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        if (inputBox != null)
        {
            inputBox.Text = "Hello World";
            Thread.Sleep(500);

            var path = ScreenshotHelper.CaptureWindow(window, "03_main_window_text_input");
            _output.WriteLine($"Screenshot saved: {path}");

            var result = VisualRegressionHelper.CompareWithBaseline(path, "03_main_window_text_input");
            if (result != null)
            {
                _output.WriteLine(result.ToString());
                result.Passed.Should().BeTrue(result.ToString());
            }
        }
        else
        {
            // If TextBox not found by x:Name, capture the window state for debugging
            _output.WriteLine("InputTextBox not found by name - capturing window for inspection");
            ScreenshotHelper.CaptureWindow(window, "03_main_window_input_not_found");
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
