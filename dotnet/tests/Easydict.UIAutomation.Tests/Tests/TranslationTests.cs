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
/// Tests that input text, trigger translation (Enter key), and capture
/// translation results in main window, mini window, and fixed window.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class TranslationTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    private const string TestInputText = "Hello";
    private const int TranslationWaitMs = 8000;

    public TranslationTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void MainWindow_TranslateWithEnter()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Find the input text box
        var inputBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByName("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        if (inputBox == null)
        {
            _output.WriteLine("InputTextBox not found - capturing window for inspection");
            ScreenshotHelper.CaptureWindow(window, "20_main_translate_input_not_found");
            return;
        }

        // Type text and press Enter to translate
        inputBox.Click();
        Thread.Sleep(300);
        inputBox.Text = TestInputText;
        Thread.Sleep(500);

        var pathBeforeTranslate = ScreenshotHelper.CaptureWindow(window, "20_main_before_translate");
        _output.WriteLine($"Screenshot saved: {pathBeforeTranslate}");

        // Press Enter to trigger translation
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(100);
        Keyboard.Release(VirtualKeyShort.ENTER);

        // Wait for translation results
        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(window, "21_main_after_translate");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");

        // Capture full screen to see overall state
        ScreenshotHelper.CaptureScreen("22_main_translate_fullscreen");
    }

    [Fact]
    public void MiniWindow_TranslateWithEnter()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Open mini window via hotkey: Ctrl+Alt+M
        _output.WriteLine("Opening mini window with Ctrl+Alt+M");
        Keyboard.Press(VirtualKeyShort.CONTROL);
        Keyboard.Press(VirtualKeyShort.ALT);
        Keyboard.Press(VirtualKeyShort.KEY_M);
        Thread.Sleep(100);
        Keyboard.Release(VirtualKeyShort.KEY_M);
        Keyboard.Release(VirtualKeyShort.ALT);
        Keyboard.Release(VirtualKeyShort.CONTROL);

        // Wait for mini window to appear
        Thread.Sleep(3000);

        // Try to find the mini window - it may be a new top-level window
        FlaUI.Core.AutomationElements.Window? miniWindow = null;
        var allWindows = _launcher.Application.GetAllTopLevelWindows(_launcher.Automation);
        foreach (var w in allWindows)
        {
            _output.WriteLine($"Found window: \"{w.Title}\" ({w.ClassName})");
            if (w.Title?.Contains("Mini", StringComparison.OrdinalIgnoreCase) == true
                || w.Title?.Contains("Easydict", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Pick the smaller window as mini (not the main window)
                if (miniWindow == null || w.BoundingRectangle.Width < miniWindow.BoundingRectangle.Width)
                    miniWindow = w;
            }
        }

        if (miniWindow == null && allWindows.Length > 1)
        {
            // If we can't identify by title, pick the second window
            miniWindow = allWindows.OrderBy(w => w.BoundingRectangle.Width).First();
        }

        if (miniWindow == null)
        {
            _output.WriteLine("Mini window not found after hotkey");
            ScreenshotHelper.CaptureScreen("23_mini_window_not_found");
            return;
        }

        miniWindow.SetForeground();
        Thread.Sleep(500);

        var pathInitial = ScreenshotHelper.CaptureWindow(miniWindow, "23_mini_window_initial");
        _output.WriteLine($"Screenshot saved: {pathInitial}");

        // Find input text box in mini window
        var inputBox = Retry.WhileNull(
            () => miniWindow.FindFirstDescendant(cf => cf.ByName("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        if (inputBox == null)
        {
            _output.WriteLine("InputTextBox not found in mini window");
            ScreenshotHelper.CaptureWindow(miniWindow, "23_mini_input_not_found");
            return;
        }

        inputBox.Click();
        Thread.Sleep(300);
        inputBox.Text = TestInputText;
        Thread.Sleep(500);

        // Press Enter to trigger translation
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(100);
        Keyboard.Release(VirtualKeyShort.ENTER);

        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathResult = ScreenshotHelper.CaptureWindow(miniWindow, "24_mini_after_translate");
        _output.WriteLine($"Screenshot saved: {pathResult}");
    }

    [Fact]
    public void FixedWindow_TranslateWithEnter()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Open fixed window via hotkey: Ctrl+Alt+F
        _output.WriteLine("Opening fixed window with Ctrl+Alt+F");
        Keyboard.Press(VirtualKeyShort.CONTROL);
        Keyboard.Press(VirtualKeyShort.ALT);
        Keyboard.Press(VirtualKeyShort.KEY_F);
        Thread.Sleep(100);
        Keyboard.Release(VirtualKeyShort.KEY_F);
        Keyboard.Release(VirtualKeyShort.ALT);
        Keyboard.Release(VirtualKeyShort.CONTROL);

        // Wait for fixed window to appear
        Thread.Sleep(3000);

        // Find the fixed window among top-level windows
        FlaUI.Core.AutomationElements.Window? fixedWindow = null;
        var allWindows = _launcher.Application.GetAllTopLevelWindows(_launcher.Automation);
        foreach (var w in allWindows)
        {
            _output.WriteLine($"Found window: \"{w.Title}\" ({w.ClassName})");
            if (w.Title?.Contains("Fixed", StringComparison.OrdinalIgnoreCase) == true
                || w.Title?.Contains("Easydict", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (fixedWindow == null || w.BoundingRectangle.Width < fixedWindow.BoundingRectangle.Width)
                    fixedWindow = w;
            }
        }

        if (fixedWindow == null && allWindows.Length > 1)
        {
            fixedWindow = allWindows.OrderBy(w => w.BoundingRectangle.Width).First();
        }

        if (fixedWindow == null)
        {
            _output.WriteLine("Fixed window not found after hotkey");
            ScreenshotHelper.CaptureScreen("25_fixed_window_not_found");
            return;
        }

        fixedWindow.SetForeground();
        Thread.Sleep(500);

        var pathInitial = ScreenshotHelper.CaptureWindow(fixedWindow, "25_fixed_window_initial");
        _output.WriteLine($"Screenshot saved: {pathInitial}");

        // Find input text box in fixed window
        var inputBox = Retry.WhileNull(
            () => fixedWindow.FindFirstDescendant(cf => cf.ByName("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        if (inputBox == null)
        {
            _output.WriteLine("InputTextBox not found in fixed window");
            ScreenshotHelper.CaptureWindow(fixedWindow, "25_fixed_input_not_found");
            return;
        }

        inputBox.Click();
        Thread.Sleep(300);
        inputBox.Text = TestInputText;
        Thread.Sleep(500);

        // Press Enter to trigger translation
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(100);
        Keyboard.Release(VirtualKeyShort.ENTER);

        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathResult = ScreenshotHelper.CaptureWindow(fixedWindow, "26_fixed_after_translate");
        _output.WriteLine($"Screenshot saved: {pathResult}");
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
