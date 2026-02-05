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
/// Phonetic badges (US/UK pronunciation) are only displayed when the target language is English.
/// This provides English pronunciation for words translated TO English.
///
/// Note: Tests verify that phonetics are NOT shown when target is not English.
/// Showing phonetics when target IS English depends on external API (Youdao) availability.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class PhoneticTranscriptionTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Chinese input text for translation to English.
    /// </summary>
    private const string ChineseInputText = "你好";

    /// <summary>
    /// English input text for translation to Chinese.
    /// </summary>
    private const string EnglishInputText = "Hello World";

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
    public void MainWindow_ChineseToEnglish_PhoneticBadgesIfAvailable()
    {
        // When translating Chinese → English, target is English
        // US/UK phonetic badges may be displayed (from Youdao enrichment)
        // Note: This test captures screenshots and logs phonetic panel state
        // Phonetic availability depends on external Youdao API
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Find the input text box
        var inputBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        // Type Chinese text
        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = ChineseInputText;
        Thread.Sleep(500);

        var pathBeforeTranslate = ScreenshotHelper.CaptureWindow(window, "30_phonetic_before_translate");
        _output.WriteLine($"Screenshot saved: {pathBeforeTranslate}");

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

        // Wait for translation results and Youdao phonetic enrichment
        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(window, "31_phonetic_zh_to_en");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");

        // Check phonetic panels (may or may not have badges depending on API availability)
        var phoneticPanels = window.FindAllDescendants(cf => cf.ByAutomationId("PhoneticPanel"));
        phoneticPanels.Should().NotBeNull("PhoneticPanel elements should exist in DOM");

        var visiblePanelsWithChildren = phoneticPanels
            .Where(p => !p.IsOffscreen && p.FindAllChildren().Length > 0)
            .ToArray();

        _output.WriteLine($"Found {visiblePanelsWithChildren.Length} PhoneticPanel(s) with visible badges");
        foreach (var panel in visiblePanelsWithChildren)
        {
            var children = panel.FindAllChildren();
            _output.WriteLine($"PhoneticPanel has {children.Length} badge(s)");
        }

        // Log whether phonetics were found (informational, not a hard assertion)
        if (visiblePanelsWithChildren.Length > 0)
        {
            _output.WriteLine("SUCCESS: Phonetic badges are displayed for Chinese→English translation");
        }
        else
        {
            _output.WriteLine("INFO: No phonetic badges found - Youdao enrichment may not have returned data");
            _output.WriteLine("This is expected if Youdao API is unavailable or returned no phonetics");
        }

        // Visual regression comparison
        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathAfterTranslate, "phonetic_chinese_to_english");

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
    public void MainWindow_EnglishToChinese_DoesNotShowPhoneticBadges()
    {
        // When translating English → Chinese, target is Chinese
        // Phonetic badges should NOT be displayed (phonetics only for English target)
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Find the input text box
        var inputBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        // Type English text
        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = EnglishInputText;
        Thread.Sleep(500);

        // Press Enter to trigger translation
        Keyboard.Type(VirtualKeyShort.ENTER);

        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(window, "32_phonetic_en_to_zh");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");

        // Assert that phonetic badges are NOT displayed for Chinese target
        var phoneticPanels = window.FindAllDescendants(cf => cf.ByAutomationId("PhoneticPanel"));
        var visiblePanelsWithChildren = phoneticPanels?
            .Where(p => !p.IsOffscreen && p.FindAllChildren().Length > 0)
            .ToArray();

        visiblePanelsWithChildren.Should().BeNullOrEmpty(
            "PhoneticPanel should be empty when target language is not English");

        _output.WriteLine("Verified: No phonetic badges shown for English→Chinese translation (target not English)");

        // Visual regression comparison
        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathAfterTranslate, "phonetic_english_to_chinese_no_badges");

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
    public void MiniWindow_ChineseToEnglish_PhoneticBadgesIfAvailable()
    {
        // When translating Chinese → English in mini window, target is English
        // US/UK phonetic badges may be displayed (depends on Youdao API availability)
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

        var pathResult = ScreenshotHelper.CaptureWindow(miniWindow, "33_phonetic_mini_zh_to_en");
        _output.WriteLine($"Screenshot saved: {pathResult}");

        // Check phonetic panels (may or may not have badges depending on API availability)
        var phoneticPanels = miniWindow.FindAllDescendants(cf => cf.ByAutomationId("PhoneticPanel"));
        var visiblePanelsWithChildren = phoneticPanels?
            .Where(p => !p.IsOffscreen && p.FindAllChildren().Length > 0)
            .ToArray();

        _output.WriteLine($"Found {visiblePanelsWithChildren?.Length ?? 0} PhoneticPanel(s) with visible badges in mini window");

        // Log whether phonetics were found (informational, not a hard assertion)
        if (visiblePanelsWithChildren?.Length > 0)
        {
            _output.WriteLine("SUCCESS: Phonetic badges are displayed in mini window for Chinese→English translation");
        }
        else
        {
            _output.WriteLine("INFO: No phonetic badges found in mini window - Youdao enrichment may not have returned data");
        }

        // Visual regression comparison
        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathResult, "phonetic_mini_chinese_to_english");

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
