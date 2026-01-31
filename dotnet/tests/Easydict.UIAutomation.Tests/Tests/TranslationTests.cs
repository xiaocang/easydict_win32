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

        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        // Type text and press Enter to translate
        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = TestInputText;
        Thread.Sleep(500);

        var pathBeforeTranslate = ScreenshotHelper.CaptureWindow(window, "20_main_before_translate");
        _output.WriteLine($"Screenshot saved: {pathBeforeTranslate}");

        // Press Enter to trigger translation (Type = press + release, safe)
        Keyboard.Type(VirtualKeyShort.ENTER);

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
        SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);

        // Wait for mini window to appear
        Thread.Sleep(3000);

        var miniWindow = FindSecondaryWindow("Mini");
        miniWindow.Should().NotBeNull("Mini window must open after Ctrl+Alt+M hotkey");

        miniWindow!.SetForeground();
        Thread.Sleep(500);

        var pathInitial = ScreenshotHelper.CaptureWindow(miniWindow, "23_mini_window_initial");
        _output.WriteLine($"Screenshot saved: {pathInitial}");

        // Find input text box in mini window
        var inputBox = Retry.WhileNull(
            () => miniWindow.FindFirstDescendant(cf => cf.ByName("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist in mini window");

        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = TestInputText;
        Thread.Sleep(500);

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

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
        SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_F);

        // Wait for fixed window to appear
        Thread.Sleep(3000);

        var fixedWindow = FindSecondaryWindow("Fixed");
        fixedWindow.Should().NotBeNull("Fixed window must open after Ctrl+Alt+F hotkey");

        fixedWindow!.SetForeground();
        Thread.Sleep(500);

        var pathInitial = ScreenshotHelper.CaptureWindow(fixedWindow, "25_fixed_window_initial");
        _output.WriteLine($"Screenshot saved: {pathInitial}");

        // Find input text box in fixed window
        var inputBox = Retry.WhileNull(
            () => fixedWindow.FindFirstDescendant(cf => cf.ByName("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist in fixed window");

        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = TestInputText;
        Thread.Sleep(500);

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathResult = ScreenshotHelper.CaptureWindow(fixedWindow, "26_fixed_after_translate");
        _output.WriteLine($"Screenshot saved: {pathResult}");
    }

    /// <summary>
    /// Send a hotkey combination safely, ensuring all keys are released even on failure.
    /// </summary>
    private void SendHotkey(VirtualKeyShort modifier1, VirtualKeyShort modifier2, VirtualKeyShort key)
    {
        try
        {
            Keyboard.Press(modifier1);
            Keyboard.Press(modifier2);
            Keyboard.Press(key);
            Thread.Sleep(100);
        }
        finally
        {
            // Always release all keys to prevent stuck modifiers
            try { Keyboard.Release(key); } catch { /* ignore */ }
            try { Keyboard.Release(modifier2); } catch { /* ignore */ }
            try { Keyboard.Release(modifier1); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Find a secondary (non-main) window from the application's top-level windows.
    /// </summary>
    private Window? FindSecondaryWindow(string windowType)
    {
        var allWindows = _launcher.Application.GetAllTopLevelWindows(_launcher.Automation);
        _output.WriteLine($"Found {allWindows.Length} top-level window(s)");

        foreach (var w in allWindows)
        {
            _output.WriteLine($"  Window: \"{w.Title}\" size={w.BoundingRectangle.Width}x{w.BoundingRectangle.Height}");
        }

        if (allWindows.Length <= 1)
        {
            _output.WriteLine($"{windowType} window did not open - only main window found");
            return null;
        }

        // Return the smallest window (mini/fixed are smaller than main)
        return allWindows
            .OrderBy(w => w.BoundingRectangle.Width * w.BoundingRectangle.Height)
            .First();
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
