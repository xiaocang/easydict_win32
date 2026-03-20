using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>
/// UI regression tests for the Long Document Translation mode.
/// Verifies all control buttons and combos work correctly without executing translation.
/// Each test captures screenshots at key states for visual regression comparison.
/// Mode switching is done via the title dropdown (Easydict ▾) menu flyout.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class LongDocTranslationTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public LongDocTranslationTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void LongDocTab_ShouldSwitchFromQuickTranslate()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();

        // Initial state: Quick Translate tab is active
        CaptureAndCompare(window, "longdoc_01a_initial_quick_tab");

        // Switch to Long Doc tab
        SwitchToLongDocTab(window);

        // Verify Long Doc controls are now visible
        var sourceLangCombo = FindControl(window, "LongDocSourceLangCombo");
        sourceLangCombo.Should().NotBeNull("LongDocSourceLangCombo should be visible after tab switch");

        CaptureAndCompare(window, "longdoc_01b_tab_switched");
    }

    [Fact]
    public void LongDocTab_ShouldShowAllControls()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();
        SwitchToLongDocTab(window);

        // Verify all expected controls exist
        var controlNames = new[]
        {
            "LongDocSourceLangCombo",
            "LongDocTargetLangCombo",
            "LongDocServiceCombo",
            "LongDocInputModeCombo",
            "LongDocOutputModeCombo",
            "LongDocConcurrencyBox",
            "LongDocPageRangeBox",
            "LongDocTranslateButton",
            "LongDocBrowseButton",
            "LongDocRetryButton",
            "LongDocOutputBrowseButton",
            "LongDocStatusText",
        };

        foreach (var name in controlNames)
        {
            var control = FindControl(window, name);
            if (control != null)
            {
                _output.WriteLine($"  Found: {name}");
            }
            else
            {
                _output.WriteLine($"  NOT FOUND: {name}");
            }
        }

        // Key controls must exist
        FindControl(window, "LongDocSourceLangCombo").Should().NotBeNull("Source language combo is required");
        FindControl(window, "LongDocTargetLangCombo").Should().NotBeNull("Target language combo is required");
        FindControl(window, "LongDocInputModeCombo").Should().NotBeNull("Input mode combo is required");
        FindControl(window, "LongDocTranslateButton").Should().NotBeNull("Translate button is required");

        CaptureAndCompare(window, "longdoc_02_all_controls");
    }

    [Fact]
    public void LongDocTab_InputModeCombo_ShouldChangeSelection()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();
        SwitchToLongDocTab(window);

        var inputModeCombo = FindComboBox(window, "LongDocInputModeCombo");
        inputModeCombo.Should().NotBeNull("LongDocInputModeCombo must exist");

        // Default is PDF (index 2)
        _output.WriteLine($"Input mode initial selection: {inputModeCombo!.SelectedItem}");

        // Select "Text" (index 0) via dropdown
        SelectComboItem(inputModeCombo, "Text", 0);
        Thread.Sleep(500);
        CaptureAndCompare(window, "longdoc_03_input_mode_text");

        // Select "Markdown" (index 1)
        SelectComboItem(inputModeCombo, "Markdown", 1);
        Thread.Sleep(500);
        CaptureAndCompare(window, "longdoc_04_input_mode_markdown");

        // Restore to "PDF" (index 2)
        SelectComboItem(inputModeCombo, "PDF", 2);
        Thread.Sleep(500);
        CaptureAndCompare(window, "longdoc_04b_input_mode_pdf_restored");
    }

    [Fact]
    public void LongDocTab_OutputModeCombo_ShouldChangeSelection()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();
        SwitchToLongDocTab(window);

        var outputModeCombo = FindComboBox(window, "LongDocOutputModeCombo");
        outputModeCombo.Should().NotBeNull("LongDocOutputModeCombo must exist");

        // Default is Mono (index 0)
        _output.WriteLine($"Output mode initial selection: {outputModeCombo!.SelectedItem}");

        // Select "Bilingual" (index 1)
        SelectComboItem(outputModeCombo, "Bilingual", 1);
        Thread.Sleep(500);
        CaptureAndCompare(window, "longdoc_05_output_bilingual");

        // Select "Both" (index 2)
        SelectComboItem(outputModeCombo, "Both", 2);
        Thread.Sleep(500);
        CaptureAndCompare(window, "longdoc_06_output_both");

        // Restore to "Mono" (index 0)
        SelectComboItem(outputModeCombo, "Mono", 0);
        Thread.Sleep(300);
    }

    [Fact]
    public void LongDocTab_ConcurrencyBox_ShouldAcceptValue()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();
        SwitchToLongDocTab(window);

        var concurrencyBox = FindControl(window, "LongDocConcurrencyBox");
        concurrencyBox.Should().NotBeNull("LongDocConcurrencyBox must exist");

        // Click the control to focus it
        concurrencyBox!.Click();
        Thread.Sleep(300);

        // Select all existing text and type new value
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(100);
        Keyboard.Type("8");
        Thread.Sleep(300);

        // Press Tab to commit the value
        Keyboard.Press(VirtualKeyShort.TAB);
        Thread.Sleep(300);

        CaptureAndCompare(window, "longdoc_07_concurrency_8");
    }

    [Fact]
    public void LongDocTab_PageRangeBox_ShouldAcceptText()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();
        SwitchToLongDocTab(window);

        var pageRangeBox = FindControl(window, "LongDocPageRangeBox");
        pageRangeBox.Should().NotBeNull("LongDocPageRangeBox must exist");

        // Click to focus and type page range
        pageRangeBox!.Click();
        Thread.Sleep(300);

        // Clear any existing text
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(100);
        Keyboard.Type("1-5,8,10-12");
        Thread.Sleep(300);

        CaptureAndCompare(window, "longdoc_08_page_range");
    }

    [Fact]
    public void LongDocTab_TranslateButton_ShouldExistAndBeEnabled()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();
        SwitchToLongDocTab(window);

        var translateButton = FindControl(window, "LongDocTranslateButton");
        translateButton.Should().NotBeNull("LongDocTranslateButton must exist");
        translateButton!.IsEnabled.Should().BeTrue("Translate button should be enabled by default");

        // Also verify the Retry button exists and is disabled by default
        var retryButton = FindControl(window, "LongDocRetryButton");
        if (retryButton != null)
        {
            retryButton.IsEnabled.Should().BeFalse("Retry button should be disabled when no partial result exists");
            _output.WriteLine("RetryButton found and correctly disabled");
        }

        CaptureAndCompare(window, "longdoc_09_translate_button");

        // NOTE: We do NOT click the translate button — this test only verifies existence and state
    }

    [Fact]
    public void LongDocTab_SwitchBackToQuickTranslate()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();
        SwitchToLongDocTab(window);

        // Verify we're on Long Doc
        FindControl(window, "LongDocSourceLangCombo").Should().NotBeNull();
        CaptureAndCompare(window, "longdoc_10a_on_longdoc");

        // Switch back to Quick Translate via title dropdown
        SwitchToQuickTranslateMode(window);

        // Verify Quick Translate controls are visible again
        var inputTextBox = FindControl(window, "InputTextBox");
        if (inputTextBox != null)
        {
            _output.WriteLine("InputTextBox found — Quick Translate tab is active");
        }

        CaptureAndCompare(window, "longdoc_10b_back_to_quick");
    }

    [Fact]
    public void LongDocTab_FullWorkflow_Screenshot()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady();
        SwitchToLongDocTab(window);

        // 1. Change Input Mode to "Text"
        var inputModeCombo = FindComboBox(window, "LongDocInputModeCombo");
        if (inputModeCombo != null)
        {
            SelectComboItem(inputModeCombo, "Text", 0);
            Thread.Sleep(500);
        }

        // 2. Change Output Mode to "Bilingual"
        var outputModeCombo = FindComboBox(window, "LongDocOutputModeCombo");
        if (outputModeCombo != null)
        {
            SelectComboItem(outputModeCombo, "Bilingual", 1);
            Thread.Sleep(500);
        }

        // 3. Set concurrency to 8
        var concurrencyBox = FindControl(window, "LongDocConcurrencyBox");
        if (concurrencyBox != null)
        {
            concurrencyBox.Click();
            Thread.Sleep(200);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Thread.Sleep(100);
            Keyboard.Type("8");
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(300);
        }

        // 4. Set page range
        var pageRangeBox = FindControl(window, "LongDocPageRangeBox");
        if (pageRangeBox != null)
        {
            pageRangeBox.Click();
            Thread.Sleep(200);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Thread.Sleep(100);
            Keyboard.Type("1-3");
            Thread.Sleep(300);
        }

        // Final composite screenshot showing all modified controls
        CaptureAndCompare(window, "longdoc_11_full_workflow");
    }

    #region Helpers

    private static void WaitForUiReady()
    {
        Thread.Sleep(2000);
    }

    private void SwitchToLongDocTab(Window window)
    {
        ClickModeMenuItem(window, "ModeLongDocItem");
    }

    private void SwitchToQuickTranslateMode(Window window)
    {
        ClickModeMenuItem(window, "ModeTranslationItem");
    }

    /// <summary>
    /// Opens the title dropdown flyout and clicks a mode menu item by AutomationId.
    /// </summary>
    private void ClickModeMenuItem(Window window, string menuItemAutomationId)
    {
        // The title dropdown button contains the "Easydict" text; find it by content.
        // The button's AutomationId is empty, so locate via the Easydict text inside it.
        var titleButton = Retry.WhileNull(
            () =>
            {
                var easydictText = window.FindFirstDescendant(cf => cf.ByName("Easydict"));
                // Walk up to find the Button parent that hosts the flyout
                var current = easydictText;
                while (current != null)
                {
                    if (current.ControlType == ControlType.Button)
                        return current;
                    current = current.Parent;
                }
                return null;
            },
            TimeSpan.FromSeconds(10)).Result;
        titleButton.Should().NotBeNull("Title dropdown button should exist");
        titleButton!.Click();
        Thread.Sleep(500);

        // Find and click the menu item in the opened flyout
        var menuItem = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, menuItemAutomationId),
            TimeSpan.FromSeconds(5)).Result;
        menuItem.Should().NotBeNull($"{menuItemAutomationId} should exist in flyout");
        menuItem!.Click();
        Thread.Sleep(1000);
    }

    /// <summary>
    /// Find a control by AutomationId first (preferred for x:Name), then fall back to Name.
    /// </summary>
    private static AutomationElement? FindByAutomationIdOrName(Window window, string name)
    {
        return window.FindFirstDescendant(cf => cf.ByAutomationId(name))
            ?? window.FindFirstDescendant(cf => cf.ByName(name));
    }

    private AutomationElement? FindControl(Window window, string name)
    {
        var control = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, name),
            TimeSpan.FromSeconds(10)).Result;

        if (control == null)
        {
            _output.WriteLine($"Control not found: {name}");
        }

        return control;
    }

    private ComboBox? FindComboBox(Window window, string name)
    {
        var combo = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, name)?.AsComboBox(),
            TimeSpan.FromSeconds(5)).Result;

        if (combo == null)
        {
            _output.WriteLine($"ComboBox not found: {name}");
        }

        return combo;
    }

    /// <summary>
    /// Select a ComboBox item by name first, falling back to index selection.
    /// </summary>
    private void SelectComboItem(ComboBox combo, string itemName, int fallbackIndex)
    {
        combo.Click();
        Thread.Sleep(500);

        var item = Retry.WhileNull(
            () => combo.FindFirstDescendant(cf => cf.ByName(itemName)),
            TimeSpan.FromSeconds(3)).Result;

        if (item != null)
        {
            item.Click();
            _output.WriteLine($"Selected '{itemName}' by name");
        }
        else
        {
            _output.WriteLine($"'{itemName}' not found by name, selecting by index {fallbackIndex}");
            combo.Select(fallbackIndex);
        }

        Thread.Sleep(300);
    }

    private void CaptureAndCompare(Window window, string screenshotName)
    {
        var path = ScreenshotHelper.CaptureWindow(window, screenshotName);
        _output.WriteLine($"Screenshot saved: {path}");

        var result = VisualRegressionHelper.CompareWithBaseline(
            path, screenshotName, VisualRegressionHelper.ThresholdText);
        if (result != null)
        {
            _output.WriteLine(result.ToString());
            result.Passed.Should().BeTrue(result.ToString());
        }
        else
        {
            _output.WriteLine("No baseline found — screenshot saved as candidate");
        }
    }

    #endregion

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
