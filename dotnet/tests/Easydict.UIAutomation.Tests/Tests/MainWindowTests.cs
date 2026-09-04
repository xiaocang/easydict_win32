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
        ResizeMainWindow(window, 640, 800).Should().BeTrue(
            "the wide-layout control contract requires enough effective width for the desktop layout");
        Thread.Sleep(500);

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

        var sourceLanguageLabel = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SourceLanguageLabel"),
            TimeSpan.FromSeconds(5)).Result;
        var targetLanguageLabel = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "TargetLanguageLabel"),
            TimeSpan.FromSeconds(5)).Result;
        var inputTitle = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "InputTitleText"),
            TimeSpan.FromSeconds(5)).Result;
        var inputTextBox = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "InputTextBox"),
            TimeSpan.FromSeconds(5)).Result;


        window.Should().NotBeNull();
        sourceLangCombo.Should().NotBeNull("source language control should be discoverable before capturing controls");
        targetLangCombo.Should().NotBeNull("target language control should be discoverable before capturing controls");
        translateButton.Should().NotBeNull("translate button should be discoverable before capturing controls");
        sourceLanguageLabel.Should().NotBeNull("the source language label should remain visible in the wide layout");
        targetLanguageLabel.Should().NotBeNull("the target language label should remain visible in the wide layout");
        inputTitle.Should().NotBeNull("the source-text title should remain visible in the wide layout");
        inputTextBox.Should().NotBeNull("the source-text editor should remain visible in the wide layout");
        sourceLanguageLabel!.IsOffscreen.Should().BeFalse("the source language label should accompany its wide-layout combo");
        targetLanguageLabel!.IsOffscreen.Should().BeFalse("the target language label should accompany its wide-layout combo");
        inputTitle!.IsOffscreen.Should().BeFalse("the source-text title should identify the input card in the wide layout");
        inputTextBox!.IsOffscreen.Should().BeFalse("the source-text editor should be immediately available for typing");
        var sourceBounds = sourceLangCombo!.BoundingRectangle;
        var targetBounds = targetLangCombo!.BoundingRectangle;
        var inputBounds = inputTextBox.BoundingRectangle;
        inputBounds.Bottom.Should().BeLessOrEqualTo(Math.Min(sourceBounds.Top, targetBounds.Top),
            "the source-text editor should remain above the language and action controls");
        translateButton!.BoundingRectangle.Left.Should().BeGreaterThan(Math.Max(sourceBounds.Right, targetBounds.Right),
            "the wide translate action should remain inline after both language controls");
    }

    [Fact]
    public void MainWindow_NarrowLayout_ShouldShowPairedLanguageControlsAndTrailingAction()
    {
        var window = _launcher.GetMainWindow();
        ResizeMainWindow(window, 400, 800).Should().BeTrue(
            "the narrow-layout control contract requires the window to reach its supported minimum width");
        Thread.Sleep(1000);

        var sourceLangCombo = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SourceLangComboNarrow"),
            TimeSpan.FromSeconds(10)).Result;
        var targetLangCombo = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "TargetLangComboNarrow"),
            TimeSpan.FromSeconds(5)).Result;
        var translateButton = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "TranslateButtonNarrow"),
            TimeSpan.FromSeconds(5)).Result;
        var sourceLanguageLabel = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "SourceLanguageLabelNarrow"),
            TimeSpan.FromSeconds(5)).Result;
        var targetLanguageLabel = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "TargetLanguageLabelNarrow"),
            TimeSpan.FromSeconds(5)).Result;
        var inputTextBox = Retry.WhileNull(
            () => UITestHelper.FindByAutomationIdOrName(window, "InputTextBox"),
            TimeSpan.FromSeconds(5)).Result;

        sourceLangCombo.Should().NotBeNull("the narrow source language control must be present");
        targetLangCombo.Should().NotBeNull("the narrow target language control must be present");
        translateButton.Should().NotBeNull("the narrow translate action must be present");
        sourceLanguageLabel.Should().NotBeNull("the narrow source language label must be present");
        targetLanguageLabel.Should().NotBeNull("the narrow target language label must be present");
        inputTextBox.Should().NotBeNull("the narrow source-text editor must be present");
        sourceLangCombo!.IsOffscreen.Should().BeFalse("the narrow source language control must be visible");
        targetLangCombo!.IsOffscreen.Should().BeFalse("the narrow target language control must be visible");
        translateButton!.IsOffscreen.Should().BeFalse("the narrow translate action must be visible");
        sourceLanguageLabel!.IsOffscreen.Should().BeFalse("the narrow source language label must be visible");
        targetLanguageLabel!.IsOffscreen.Should().BeFalse("the narrow target language label must be visible");
        inputTextBox!.IsOffscreen.Should().BeFalse("the narrow source-text editor must remain immediately available");

        var sourceBounds = sourceLangCombo.BoundingRectangle;
        var targetBounds = targetLangCombo.BoundingRectangle;
        var inputBounds = inputTextBox.BoundingRectangle;
        var translateBounds = translateButton.BoundingRectangle;
        inputBounds.Bottom.Should().BeLessOrEqualTo(Math.Min(sourceBounds.Top, targetBounds.Top),
            "the narrow source-text editor should remain above the language controls");
        translateBounds.Top.Should().BeGreaterOrEqualTo(Math.Max(sourceBounds.Bottom, targetBounds.Bottom),
            "the narrow action should follow the paired language controls without overlapping them");
        translateBounds.Left.Should().BeGreaterOrEqualTo(targetBounds.Left,
            "the compact narrow action should align with the target-language control");
        translateBounds.Right.Should().BeLessOrEqualTo(targetBounds.Right,
            "the compact narrow action should not dominate the full language-control row");
        translateBounds.Width.Should().BeLessThan(targetBounds.Width,
            "the narrow action should remain visually subordinate to the paired language controls");
    }

    private bool ResizeMainWindow(Window window, int width, int height)
    {
        try
        {
            var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
            var physicalWidth = (int)Math.Ceiling(width * dpiScale);
            var physicalHeight = (int)Math.Ceiling(height * dpiScale);
            _output.WriteLine(
                $"Resizing window to {width}×{height} DIPs ({physicalWidth}×{physicalHeight} physical pixels at {dpiScale:F2} scale)");

            if (ScreenshotHelper.TrySetWindowPhysicalBounds(
                    window,
                    new System.Drawing.Rectangle(0, 0, physicalWidth, physicalHeight)))
            {
                window.SetForeground();
                return true;
            }

            if (!window.Patterns.Transform.IsSupported)
            {
                return false;
            }

            var transform = window.Patterns.Transform.Pattern;
            if (!transform.CanResize.Value)
            {
                return false;
            }

            transform.Resize(physicalWidth, physicalHeight);
            window.SetForeground();
            return true;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Window resize skipped: {ex.Message}");
            return false;
        }
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
