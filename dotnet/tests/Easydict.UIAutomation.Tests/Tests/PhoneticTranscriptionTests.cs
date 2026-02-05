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
/// Tests for phonetic transcription badge display in translation results.
/// After filtering implementation, only target language phonetics (dest, US, UK) are displayed.
/// Source language romanization (src) is no longer shown to avoid confusion when translating
/// Chinese to English (where pinyin would appear next to English translation).
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class PhoneticTranscriptionTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Chinese input text — Google Translate returns src_translit romanization for this.
    /// </summary>
    private const string ChineseInputText = "你好世界";

    /// <summary>
    /// Wait time for translation results to load (includes network round-trip).
    /// </summary>
    private const int TranslationWaitMs = 10000;

    public PhoneticTranscriptionTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void MainWindow_ChineseTranslation_DoesNotShowSourcePhonetics()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Find the input text box
        var inputBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        // Type Chinese text and press Enter to translate
        // Google returns only src_translit (pinyin), which should now be hidden
        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = ChineseInputText;
        Thread.Sleep(500);

        var pathBeforeTranslate = ScreenshotHelper.CaptureWindow(window, "30_phonetic_before_translate");
        _output.WriteLine($"Screenshot saved: {pathBeforeTranslate}");

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

        // Wait for translation results (Chinese requires romanization processing)
        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(window, "31_phonetic_after_translate");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");

        // Assert that source phonetic badges are NOT displayed (filtered out)
        // Chinese→English from Google only returns src phonetics (pinyin), not target phonetics
        var phoneticPanels = window.FindAllDescendants(cf => cf.ByAutomationId("PhoneticPanel"));
        phoneticPanels.Should().NotBeNull("PhoneticPanel elements should exist in DOM");
        
        var visiblePanels = phoneticPanels.Where(p => !p.IsOffscreen && p.FindAllChildren().Length > 0).ToArray();
        visiblePanels.Should().BeEmpty("PhoneticPanel should be empty/hidden when only src phonetics are available");
        
        _output.WriteLine($"Verified: No phonetic badges shown for Chinese→English translation (source romanization filtered)");

        // Visual regression comparison
        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathAfterTranslate, "phonetic_chinese_translation_filtered");

        if (comparison == null)
        {
            _output.WriteLine("No baseline found — screenshot saved as baseline candidate for manual review.");
        }
        else
        {
            _output.WriteLine(comparison.ToString());
        }

        // Capture full screen to see overall state
        ScreenshotHelper.CaptureScreen("32_phonetic_fullscreen");
    }

    [Fact]
    public void MainWindow_EnglishToChineseTranslation_ShowsTargetPhonetic()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Find the input text box
        var inputBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        // Type English text — when translating to Chinese, Google returns translit for the target
        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = "Hello World";
        Thread.Sleep(500);

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results with target phonetics...");
        Thread.Sleep(TranslationWaitMs);

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(window, "33_phonetic_en_to_zh");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");

        // Assert that phonetic badges are displayed for target language
        var phoneticPanels = window.FindAllDescendants(cf => cf.ByAutomationId("PhoneticPanel"));
        var visiblePanels = phoneticPanels?.Where(p => !p.IsOffscreen).ToArray();
        visiblePanels.Should().NotBeNullOrEmpty("At least one PhoneticPanel should be visible for target phonetics");
        
        foreach (var panel in visiblePanels!)
        {
            var children = panel.FindAllChildren();
            children.Should().NotBeEmpty($"PhoneticPanel should contain badge elements");
            _output.WriteLine($"PhoneticPanel has {children.Length} badge(s)");
        }

        // Visual regression comparison
        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathAfterTranslate, "phonetic_english_to_chinese");

        if (comparison == null)
        {
            _output.WriteLine("No baseline found — screenshot saved as baseline candidate for manual review.");
        }
        else
        {
            _output.WriteLine(comparison.ToString());
        }
    }

    [Fact]
    public void MiniWindow_ChineseTranslation_DoesNotShowSourcePhonetics()
    {
        // Ensure app is ready before sending hotkey
        _ = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Open mini window via hotkey: Ctrl+Alt+M
        _output.WriteLine("Opening mini window with Ctrl+Alt+M");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_M);

        // Wait for mini window to appear
        Thread.Sleep(3000);

        var miniWindow = UITestHelper.FindSecondaryWindow(
            _launcher.Application, _launcher.Automation, "Mini", _output);
        miniWindow.Should().NotBeNull("Mini window must open after Ctrl+Alt+M hotkey");

        miniWindow!.SetForeground();
        Thread.Sleep(500);

        // Find input text box in mini window
        var inputBox = Retry.WhileNull(
            () => miniWindow.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist in mini window");

        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = ChineseInputText;
        Thread.Sleep(500);

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathResult = ScreenshotHelper.CaptureWindow(miniWindow, "34_phonetic_mini_chinese");
        _output.WriteLine($"Screenshot saved: {pathResult}");

        // Assert that source phonetic badges are NOT displayed in mini window (filtered out)
        var phoneticPanels = miniWindow.FindAllDescendants(cf => cf.ByAutomationId("PhoneticPanel"));
        var visiblePanels = phoneticPanels?.Where(p => !p.IsOffscreen && p.FindAllChildren().Length > 0).ToArray();
        visiblePanels.Should().BeNullOrEmpty("PhoneticPanel should be empty/hidden when only src phonetics available");

        _output.WriteLine($"Verified: No phonetic badges shown in mini window for Chinese→English translation");

        // Visual regression comparison
        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathResult, "phonetic_mini_chinese_translation_filtered");

        if (comparison == null)
        {
            _output.WriteLine("No baseline found — screenshot saved as baseline candidate for manual review.");
        }
        else
        {
            _output.WriteLine(comparison.ToString());
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
