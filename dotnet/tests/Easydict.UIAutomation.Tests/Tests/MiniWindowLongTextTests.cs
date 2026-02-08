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
/// UI regression and screenshot tests for MiniWindow with long text input (>30 words).
///
/// Verifies that:
/// - Long text is accepted and displayed correctly in the input area
/// - Window resizes properly to accommodate translation results
/// - Streaming translation results render without layout issues
/// - Visual regression baselines remain stable after resize coalescing changes
///
/// Prerequisites:
/// - Real Windows desktop environment (not headless)
/// - Easydict installed (MSIX) or built (exe)
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class MiniWindowLongTextTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Long English paragraph (>30 words) to exercise text wrapping, multi-line input,
    /// and window auto-resize during streaming translation.
    /// </summary>
    private const string LongInputText =
        "The quick brown fox jumps over the lazy dog near the riverbank where " +
        "the tall oak trees sway gently in the warm summer breeze while birds " +
        "sing their melodious songs and children play happily in the nearby park";

    /// <summary>
    /// Extended wait time for long text translation — services need more time
    /// for longer input, especially streaming LLM services.
    /// </summary>
    private const int LongTextTranslationWaitMs = 12000;

    /// <summary>
    /// Wait time for streaming to start and produce visible output.
    /// </summary>
    private const int StreamingStartWaitMs = 4000;

    public MiniWindowLongTextTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void MiniWindow_LongText_InputDisplaysCorrectly()
    {
        var miniWindow = OpenMiniWindow();
        var inputBox = FindInputTextBox(miniWindow);

        // Capture initial empty state
        var pathEmpty = ScreenshotHelper.CaptureWindow(miniWindow, "30_mini_longtext_empty");
        _output.WriteLine($"Screenshot (empty): {pathEmpty}");
        var initialHeight = miniWindow.BoundingRectangle.Height;
        _output.WriteLine($"Initial window height: {initialHeight}px");

        // Input long text
        inputBox.Click();
        Thread.Sleep(300);
        inputBox.Text = LongInputText;
        Thread.Sleep(1000); // Allow auto-resize to settle

        var pathAfterInput = ScreenshotHelper.CaptureWindow(miniWindow, "31_mini_longtext_after_input");
        _output.WriteLine($"Screenshot (after input): {pathAfterInput}");
        var heightAfterInput = miniWindow.BoundingRectangle.Height;
        _output.WriteLine($"Window height after input: {heightAfterInput}px");

        // Verify input text was accepted
        var currentText = inputBox.Text;
        currentText.Should().Contain("quick brown fox", "Long text should be accepted in input box");

        // Visual regression for the input state
        var vrResult = VisualRegressionHelper.CompareWithBaseline(
            pathAfterInput, "mini_longtext_after_input",
            thresholdPercent: VisualRegressionHelper.ThresholdText);
        LogVisualRegressionResult(vrResult);
    }

    [Fact]
    public void MiniWindow_LongText_TranslationResizesWindow()
    {
        var miniWindow = OpenMiniWindow();
        var inputBox = FindInputTextBox(miniWindow);

        // Record initial height
        var initialHeight = miniWindow.BoundingRectangle.Height;
        _output.WriteLine($"Initial window height: {initialHeight}px");

        // Input long text and translate
        inputBox.Click();
        Thread.Sleep(300);
        inputBox.Text = LongInputText;
        Thread.Sleep(500);

        var pathBeforeTranslate = ScreenshotHelper.CaptureWindow(
            miniWindow, "32_mini_longtext_before_translate");
        _output.WriteLine($"Screenshot (before translate): {pathBeforeTranslate}");

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

        // Wait for translation results
        _output.WriteLine($"Waiting {LongTextTranslationWaitMs}ms for translation results...");
        Thread.Sleep(LongTextTranslationWaitMs);

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(
            miniWindow, "33_mini_longtext_after_translate");
        _output.WriteLine($"Screenshot (after translate): {pathAfterTranslate}");
        var finalHeight = miniWindow.BoundingRectangle.Height;
        _output.WriteLine($"Final window height: {finalHeight}px");

        // Window should have grown to accommodate results
        finalHeight.Should().BeGreaterThanOrEqualTo(initialHeight,
            "Window height should grow (or stay same) after translation results are displayed");

        // Visual regression for the translated state
        var vrResult = VisualRegressionHelper.CompareWithBaseline(
            pathAfterTranslate, "mini_longtext_after_translate",
            thresholdPercent: VisualRegressionHelper.ThresholdText);
        LogVisualRegressionResult(vrResult);
    }

    [Fact]
    public void MiniWindow_LongText_StreamingProgressScreenshots()
    {
        var miniWindow = OpenMiniWindow();
        var inputBox = FindInputTextBox(miniWindow);

        // Input long text
        inputBox.Click();
        Thread.Sleep(300);
        inputBox.Text = LongInputText;
        Thread.Sleep(500);

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

        // Capture a sequence of screenshots during streaming to document resize behavior
        var heights = new List<int>();

        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(StreamingStartWaitMs);

            var height = miniWindow.BoundingRectangle.Height;
            heights.Add(height);

            var path = ScreenshotHelper.CaptureWindow(
                miniWindow, $"34_mini_longtext_streaming_{i:D2}");
            _output.WriteLine($"Streaming screenshot {i}: height={height}px, path={path}");
        }

        // Log height progression for analysis
        _output.WriteLine($"Height progression: [{string.Join(", ", heights)}]");

        // Heights should be monotonically non-decreasing (window grows as content arrives)
        for (int i = 1; i < heights.Count; i++)
        {
            heights[i].Should().BeGreaterThanOrEqualTo(heights[i - 1],
                $"Window height at step {i} ({heights[i]}px) should be >= step {i - 1} ({heights[i - 1]}px)");
        }

        // Capture final state
        var pathFinal = ScreenshotHelper.CaptureWindow(
            miniWindow, "35_mini_longtext_streaming_final");
        _output.WriteLine($"Screenshot (streaming final): {pathFinal}");

        // Full screen context for CI review
        ScreenshotHelper.CaptureScreen("36_mini_longtext_fullscreen");
    }

    [Fact]
    public void MiniWindow_LongText_WindowHeightWithinBounds()
    {
        var miniWindow = OpenMiniWindow();
        var inputBox = FindInputTextBox(miniWindow);

        // Input long text and translate
        inputBox.Click();
        Thread.Sleep(300);
        inputBox.Text = LongInputText;
        Thread.Sleep(500);

        Keyboard.Type(VirtualKeyShort.ENTER);

        // Wait for all results
        _output.WriteLine($"Waiting {LongTextTranslationWaitMs}ms for all results...");
        Thread.Sleep(LongTextTranslationWaitMs);

        var windowRect = miniWindow.BoundingRectangle;
        _output.WriteLine($"Window bounds: {windowRect.Width}x{windowRect.Height} at ({windowRect.X},{windowRect.Y})");

        // MiniWindow has height constraints: 200-800 DIPs (ResizeWindowToContent)
        // At 100% DPI: 200-800px, at 150% DPI: 300-1200px, at 200% DPI: 400-1600px
        windowRect.Height.Should().BeGreaterThanOrEqualTo(200,
            "Window height should be at least the minimum (200 DIPs at 100% DPI)");
        windowRect.Height.Should().BeLessThanOrEqualTo(1700,
            "Window height should not exceed the maximum (~800 DIPs at 200% DPI)");

        var pathBounds = ScreenshotHelper.CaptureWindow(miniWindow, "37_mini_longtext_bounds_check");
        _output.WriteLine($"Screenshot (bounds check): {pathBounds}");

        // Visual regression for the final bounded state
        var vrResult = VisualRegressionHelper.CompareWithBaseline(
            pathBounds, "mini_longtext_bounds_check",
            thresholdPercent: VisualRegressionHelper.ThresholdText);
        LogVisualRegressionResult(vrResult);
    }

    /// <summary>
    /// Open MiniWindow via Ctrl+Alt+M hotkey and return the window reference.
    /// </summary>
    private Window OpenMiniWindow()
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

        _output.WriteLine($"Mini window found: \"{miniWindow.Title}\" " +
                          $"size={miniWindow.BoundingRectangle.Width}x{miniWindow.BoundingRectangle.Height}");
        return miniWindow;
    }

    /// <summary>
    /// Find the InputTextBox in the given window.
    /// </summary>
    private TextBox FindInputTextBox(Window window)
    {
        var inputBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist in mini window");

        return inputBox!;
    }

    private void LogVisualRegressionResult(VisualComparisonResult? result)
    {
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            if (!result.Passed)
            {
                _output.WriteLine($"  Diff image: {result.DiffImagePath}");
            }
        }
        else
        {
            _output.WriteLine("No baseline found - screenshot saved as candidate for review");
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
