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
/// Phonetic badges are displayed for dictionary-style word lookups in either direction:
/// a US/UK pronunciation whenever one side of the result is English, plus any romanization
/// a service supplies.
///
/// Note: whether badges actually appear for a word depends on external API (Youdao)
/// availability, so those tests log rather than assert. The hard assertion is the one
/// guarantee that does not depend on the network: sentences never get phonetic badges.
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
    /// English word input for translation to Chinese — the most common dictionary lookup.
    /// </summary>
    private const string EnglishInputText = "hello";

    /// <summary>
    /// Sentence input, which must never produce phonetic badges.
    /// </summary>
    private const string SentenceInputText = "The quick brown fox jumps over the lazy dog.";

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
        var inputBox = UITestHelper.FindInputTextBox(window);

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
    public void MainWindow_EnglishToChinese_PhoneticBadgesIfAvailable()
    {
        // Looking an English word up for its Chinese meaning: the pronunciation belongs to
        // the word the user typed, so badges are expected here just as in the other direction.
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        // Find the input text box
        var inputBox = UITestHelper.FindInputTextBox(window);

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

        // Phonetic availability depends on the external Youdao API, so log rather than assert
        var phoneticPanels = window.FindAllDescendants(cf => cf.ByAutomationId("PhoneticPanel"));
        phoneticPanels.Should().NotBeNull("PhoneticPanel elements should exist in DOM");

        var visiblePanelsWithChildren = phoneticPanels
            .Where(p => !p.IsOffscreen && p.FindAllChildren().Length > 0)
            .ToArray();

        _output.WriteLine($"Found {visiblePanelsWithChildren.Length} PhoneticPanel(s) with visible badges");

        if (visiblePanelsWithChildren.Length > 0)
        {
            _output.WriteLine("SUCCESS: Phonetic badges are displayed for English→Chinese word lookup");
        }
        else
        {
            _output.WriteLine("INFO: No phonetic badges found - Youdao enrichment may not have returned data");
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

        // Find input text box in mini window (helper expands the collapsed source text first)
        var inputBox = UITestHelper.FindInputTextBox(miniWindow);

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

    [Fact]
    public void MainWindow_SentenceTranslation_DoesNotShowPhoneticBadges()
    {
        // Phonetics are a dictionary affordance. A sentence must never get badges,
        // in either direction — the one guarantee that does not depend on the network.
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var inputBox = UITestHelper.FindInputTextBox(window);

        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = SentenceInputText;
        Thread.Sleep(500);

        Keyboard.Type(VirtualKeyShort.ENTER);

        _output.WriteLine($"Waiting {TranslationWaitMs}ms for translation results...");
        Thread.Sleep(TranslationWaitMs);

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(window, "33_phonetic_sentence");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");

        var phoneticPanels = window.FindAllDescendants(cf => cf.ByAutomationId("PhoneticPanel"));
        var visiblePanelsWithChildren = phoneticPanels?
            .Where(p => !p.IsOffscreen && p.FindAllChildren().Length > 0)
            .ToArray();

        visiblePanelsWithChildren.Should().BeNullOrEmpty(
            "PhoneticPanel should be empty when the query is a sentence rather than a word");

        _output.WriteLine("Verified: No phonetic badges shown for sentence translation");

        // Visual regression comparison
        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathAfterTranslate, "phonetic_sentence_no_badges");

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
