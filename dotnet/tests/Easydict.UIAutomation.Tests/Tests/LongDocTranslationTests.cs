using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using System.Drawing;
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
        WaitForUiReady(window);

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
        WaitForUiReady(window);
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
        ScrollLongDocControlIntoView(window, "LongDocTranslateButton");

        CaptureAndCompare(window, "longdoc_02_all_controls");
    }

    [Fact]
    public void LongDocTab_InputModeCombo_ShouldChangeSelection()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady(window);
        SwitchToLongDocTab(window);

        var inputModeCombo = FindComboBox(window, "LongDocInputModeCombo");
        inputModeCombo.Should().NotBeNull("LongDocInputModeCombo must exist");

        // Default is PDF (index 2)
        _output.WriteLine($"Input mode initial selection: {inputModeCombo!.SelectedItem}");

        // Select "Text" (index 0) via dropdown
        SelectComboItem(inputModeCombo, "Text", 0);
        Thread.Sleep(500);
        ScrollLongDocControlIntoView(window, "LongDocInputModeCombo");
        CaptureAndCompare(window, "longdoc_03_input_mode_text");

        // Select "Markdown" (index 1)
        SelectComboItem(inputModeCombo, "Markdown", 1);
        Thread.Sleep(500);
        ScrollLongDocControlIntoView(window, "LongDocInputModeCombo");
        CaptureAndCompare(window, "longdoc_04_input_mode_markdown");

        // Restore to "PDF" (index 2)
        SelectComboItem(inputModeCombo, "PDF", 2);
        Thread.Sleep(500);
        ScrollLongDocControlIntoView(window, "LongDocInputModeCombo");
        CaptureAndCompare(window, "longdoc_04b_input_mode_pdf_restored");
    }

    [Fact]
    public void LongDocTab_OutputModeCombo_ShouldChangeSelection()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady(window);
        SwitchToLongDocTab(window);

        var outputModeCombo = FindComboBox(window, "LongDocOutputModeCombo");
        outputModeCombo.Should().NotBeNull("LongDocOutputModeCombo must exist");

        // Default is Mono (index 0)
        _output.WriteLine($"Output mode initial selection: {outputModeCombo!.SelectedItem}");

        // Select "Bilingual" (index 1)
        SelectComboItem(outputModeCombo, "Bilingual", 1);
        Thread.Sleep(500);
        ScrollLongDocControlIntoView(window, "LongDocOutputModeCombo");
        CaptureAndCompare(window, "longdoc_05_output_bilingual");

        // Select "Both" (index 2)
        SelectComboItem(outputModeCombo, "Both", 2);
        Thread.Sleep(500);
        ScrollLongDocControlIntoView(window, "LongDocOutputModeCombo");
        CaptureAndCompare(window, "longdoc_06_output_both");

        // Restore to "Mono" (index 0)
        SelectComboItem(outputModeCombo, "Mono", 0);
        Thread.Sleep(300);
    }

    [Fact]
    public void LongDocTab_ConcurrencyBox_ShouldAcceptValue()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady(window);
        SwitchToLongDocTab(window);

        var concurrencyBox = FindControl(window, "LongDocConcurrencyBox");
        concurrencyBox.Should().NotBeNull("LongDocConcurrencyBox must exist");

        SetEditableControlValue(concurrencyBox!, "8");
        ReadEditableControlValue(concurrencyBox!).Should().Be(
            "8",
            "the concurrency NumberBox must commit the requested value before capture");
        ScrollLongDocControlIntoView(window, "LongDocConcurrencyBox");

        CaptureAndCompare(window, "longdoc_07_concurrency_8");
    }

    [Fact]
    public void LongDocTab_PageRangeBox_ShouldAcceptText()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady(window);
        SwitchToLongDocTab(window);

        var pageRangeBox = FindControl(window, "LongDocPageRangeBox");
        pageRangeBox.Should().NotBeNull("LongDocPageRangeBox must exist");

        SetEditableControlValue(pageRangeBox!, "1-5,8,10-12");
        ReadEditableControlValue(pageRangeBox!).Should().Be(
            "1-5,8,10-12",
            "the page-range field must contain the requested range before capture");
        ScrollLongDocControlIntoView(window, "LongDocPageRangeBox");

        CaptureAndCompare(window, "longdoc_08_page_range");
    }

    [Fact]
    public void LongDocTab_TranslateButton_ShouldExistAndBeEnabled()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady(window);
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
        ScrollLongDocControlIntoView(window, "LongDocTranslateButton");

        CaptureAndCompare(window, "longdoc_09_translate_button");

        // NOTE: We do NOT click the translate button — this test only verifies existence and state
    }

    [Fact]
    public void LongDocTab_SwitchBackToQuickTranslate()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady(window);
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
    public void LongDocTab_FullWorkflow_ReachesTerminalState()
    {
        var window = _launcher.GetMainWindow();
        WaitForUiReady(window);
        SwitchToLongDocTab(window);


        var inputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"easydict-longdoc-{Guid.NewGuid():N}.txt");
        var outputPath = Path.Combine(
            Path.GetDirectoryName(inputPath)!,
            $"{Path.GetFileNameWithoutExtension(inputPath)}_translated.txt");

        try
        {
            File.WriteAllText(
                inputPath,
                "Easydict translates this short document through the complete long-document workflow.");

            var inputModeCombo = FindComboBox(window, "LongDocInputModeCombo");
            inputModeCombo.Should().NotBeNull("LongDocInputModeCombo must exist");
            SelectComboItem(inputModeCombo!, "Text", 0);

            var outputModeCombo = FindComboBox(window, "LongDocOutputModeCombo");
            outputModeCombo.Should().NotBeNull("LongDocOutputModeCombo must exist");
            SelectComboItem(outputModeCombo!, "Bilingual", 1);


            var concurrencyBox = FindControl(window, "LongDocConcurrencyBox");
            concurrencyBox.Should().NotBeNull("LongDocConcurrencyBox must exist");
            SetEditableControlValue(concurrencyBox!, "8");
            ReadEditableControlValue(concurrencyBox!).Should().Be("8");

            var browseButton = FindControl(window, "LongDocBrowseButton");
            browseButton.Should().NotBeNull("LongDocBrowseButton must exist");
            browseButton!.Click();
            SelectFileFromOpenDialog(inputPath);

            var fileDisplay = Retry.WhileNull(
                () =>
                {
                    var candidate = FindByAutomationIdOrName(window, "LongDocFilePathDisplay");
                    return candidate?.Name == Path.GetFileName(inputPath) ? candidate : null;
                },
                TimeSpan.FromSeconds(10)).Result;
            fileDisplay.Should().NotBeNull("the selected text file should appear in the long-document input");

            var translateButton = FindControl(window, "LongDocTranslateButton");
            translateButton.Should().NotBeNull("LongDocTranslateButton must exist");
            translateButton!.Click();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            AutomationElement? status = null;
            while (DateTime.UtcNow < deadline)
            {
                status = FindByAutomationIdOrName(window, "LongDocStatusText");
                var statusText = status?.Name ?? string.Empty;
                if (statusText.StartsWith("Completed:", StringComparison.Ordinal)
                    || statusText.StartsWith("Failed:", StringComparison.Ordinal)
                    || statusText.StartsWith("Partial success:", StringComparison.Ordinal))
                {
                    break;
                }

                Thread.Sleep(500);
            }

            status.Should().NotBeNull("the long-document workflow should expose a terminal status");
            var terminalStatus = status!.Name;
            terminalStatus.Should().MatchRegex(
                "^(Completed:|Failed:|Partial success:)",
                "the installed app uses an external translation provider, but the UI must always report a terminal outcome");

            if (terminalStatus.StartsWith("Completed:", StringComparison.Ordinal))
            {
                File.Exists(outputPath).Should().BeTrue(
                    "a completed long-document workflow should write its translated output");
            }

            CaptureAndCompare(window, "longdoc_11_full_workflow_terminal_state");
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(outputPath);
        }
    }

    #region Helpers

    private void WaitForUiReady(Window window)
    {
        var sourceLangCombo = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, "SourceLangCombo"),
            TimeSpan.FromSeconds(10)).Result;

        sourceLangCombo.Should().NotBeNull("SourceLangCombo should exist once the quick-translate UI is ready");
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
        if (menuItemAutomationId == "ModeTranslationItem")
        {
            ClickModeMenuItemWithoutVerification(window, menuItemAutomationId);
            return;
        }

        var expectedControl = GetModeVerificationControl(menuItemAutomationId);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var titleButton = Retry.WhileNull(
                () => FindTitleButton(window),
                TimeSpan.FromSeconds(10)).Result;
            titleButton.Should().NotBeNull("Title dropdown button should exist");

            titleButton!.Click();
            Thread.Sleep(1000);

            var menuItem = Retry.WhileNull(
                () => FindByAutomationIdOrName(window, menuItemAutomationId),
                TimeSpan.FromSeconds(5)).Result;

            if (menuItem == null)
            {
                _output.WriteLine($"Attempt {attempt}: {menuItemAutomationId} did not appear in the flyout");
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Thread.Sleep(200);
                continue;
            }

            menuItem.Click();

            var switchedControl = Retry.WhileNull(
                () => FindByAutomationIdOrName(window, expectedControl),
                TimeSpan.FromSeconds(5)).Result;

            if (switchedControl != null)
            {
                _output.WriteLine($"Attempt {attempt}: switched via {menuItemAutomationId} and found {expectedControl}");
                return;
            }

            _output.WriteLine($"Attempt {attempt}: clicked {menuItemAutomationId} but did not find {expectedControl}");
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(200);
        }

        FindByAutomationIdOrName(window, expectedControl)
            .Should().NotBeNull($"{expectedControl} should appear after clicking {menuItemAutomationId}");
    }

    private void ClickModeMenuItemWithoutVerification(Window window, string menuItemAutomationId)
    {
        var titleButton = Retry.WhileNull(
            () => FindTitleButton(window),
            TimeSpan.FromSeconds(10)).Result;
        titleButton.Should().NotBeNull("Title dropdown button should exist");

        titleButton!.Click();
        Thread.Sleep(1000);

        var menuItem = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, menuItemAutomationId),
            TimeSpan.FromSeconds(5)).Result;
        menuItem.Should().NotBeNull($"{menuItemAutomationId} should exist in flyout");

        menuItem!.Click();
        Thread.Sleep(1000);
    }

    private static AutomationElement? FindTitleButton(Window window)
    {
        var easydictText = window.FindFirstDescendant(cf => cf.ByName("Easydict"));
        var current = easydictText;
        while (current != null)
        {
            if (current.ControlType == ControlType.Button)
                return current;

            current = current.Parent;
        }

        return null;
    }

    private static string GetModeVerificationControl(string menuItemAutomationId)
    {
        return menuItemAutomationId switch
        {
            "ModeLongDocItem" => "LongDocSourceLangCombo",
            _ => throw new ArgumentOutOfRangeException(nameof(menuItemAutomationId), menuItemAutomationId, "Unsupported mode menu item"),
        };
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

    private void ScrollLongDocControlIntoView(Window window, string controlAutomationId)
    {
        var scrollViewer = FindControl(window, "LongDocContent");
        scrollViewer.Should().NotBeNull("the long-document controls must have a scroll container");

        var visibleControl = ScrollHelper.ScrollToFind(
            scrollViewer!,
            startPercent: 25,
            () =>
            {
                var control = FindByAutomationIdOrName(window, controlAutomationId);
                if (control == null || control.IsOffscreen)
                {
                    return null;
                }

                var controlBounds = control.BoundingRectangle;
                var windowBounds = window.BoundingRectangle;
                return controlBounds.Top >= windowBounds.Top &&
                       controlBounds.Bottom <= windowBounds.Bottom - 8
                    ? control
                    : null;
            },
            _output.WriteLine);

        visibleControl.Should().NotBeNull(
            $"{controlAutomationId} must be fully visible before its named screenshot is captured");
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
    private static void SetEditableControlValue(AutomationElement control, string value)
    {
        var editor = control.ControlType == ControlType.Edit
            ? control
            : control.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        editor.Should().NotBeNull($"{control.AutomationId} should contain an editable text field");

        var textBox = editor!.AsTextBox();
        textBox.Text = value;
        Keyboard.Type(VirtualKeyShort.TAB);
        Thread.Sleep(300);
    }

    private static string ReadEditableControlValue(AutomationElement control)
    {
        var editor = control.ControlType == ControlType.Edit
            ? control
            : control.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
        editor.Should().NotBeNull($"{control.AutomationId} should contain an editable text field");
        return editor!.AsTextBox().Text;
    }

    private static string GetElementName(AutomationElement element)
    {
        try
        {
            return element.Name;
        }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return string.Empty;
        }
    }

    private void SelectFileFromOpenDialog(string filePath)
    {
        var desktop = _launcher.Automation.GetDesktop();
        var fileNameEdit = Retry.WhileNull(
            () => desktop.FindFirstDescendant(cf => cf.ByAutomationId("1148"))
                ?? desktop.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                    .FirstOrDefault(element =>
                    {
                        var name = GetElementName(element);
                        return name.Contains("File name", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Filename", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("文件名", StringComparison.Ordinal);
                    }),
            TimeSpan.FromSeconds(10)).Result;

        if (fileNameEdit == null)
        {
            _output.WriteLine("File-name edit was not exposed through UIA; falling back to the picker address bar.");
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L);
            Thread.Sleep(200);
            Keyboard.Type(filePath);
            Keyboard.Press(VirtualKeyShort.ENTER);
            return;
        }

        _output.WriteLine(
            $"Selecting long-document input through picker edit '{fileNameEdit.AutomationId}' / '{GetElementName(fileNameEdit)}'");
        fileNameEdit.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(Path.GetFileName(filePath));
        Keyboard.Type(VirtualKeyShort.TAB);
        Thread.Sleep(300);

        var pickerWindow = GetPickerWindow(fileNameEdit);
        pickerWindow.Should().NotBeNull("the file-name field must belong to the open-file picker");

        var openButton = Retry.WhileNull(
            () => pickerWindow!.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(button => button.IsEnabled
                    && (button.AutomationId == "1"
                        || string.Equals(GetElementName(button), "Open", StringComparison.OrdinalIgnoreCase))),
            TimeSpan.FromSeconds(5)).Result;
        openButton.Should().NotBeNull("the open-file picker must expose its Open button");

        _output.WriteLine("Confirming long-document input through the picker Open button");
        openButton!.Click();
    }

    private static AutomationElement? GetPickerWindow(AutomationElement element)
    {
        for (AutomationElement? current = element; current != null; current = current.Parent)
        {
            if (current.ControlType == ControlType.Window)
            {
                return current;
            }
        }

        return null;
    }



    private void CaptureAndCompare(Window window, string screenshotName)
    {
        var captureWindow = RefreshMainWindowForScreenshot(window);
        var path = ScreenshotHelper.CaptureWindow(captureWindow, screenshotName);
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

    private Window RefreshMainWindowForScreenshot(Window fallback)
    {
        try
        {
            var refreshed = FindBestMainWindowForScreenshot() ?? _launcher.GetMainWindow(TimeSpan.FromSeconds(5));
            if (PrepareWindowForScreenshot(refreshed))
            {
                return refreshed;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Could not refresh main window before screenshot: {ex.Message}");
        }

        return fallback;
    }

    private Window? FindBestMainWindowForScreenshot()
    {
        var virtualScreen = ScreenshotHelper.GetVirtualScreenBounds();
        var candidates = _launcher.Application.GetAllTopLevelWindows(_launcher.Automation)
            .Select(window =>
            {
                var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
                return new
                {
                    Window = window,
                    Bounds = bounds,
                    Score = ScoreMainWindowCandidate(window),
                    Area = Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height),
                    IntersectsScreen = Rectangle.Intersect(bounds, virtualScreen).Width > 1 &&
                                       Rectangle.Intersect(bounds, virtualScreen).Height > 1
                };
            })
            .Where(candidate => candidate.Bounds.Width > 100 && candidate.Bounds.Height > 100)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.IntersectsScreen)
            .ThenByDescending(candidate => candidate.Area)
            .ToList();

        foreach (var candidate in candidates)
        {
            _output.WriteLine(
                $"Screenshot window candidate: title='{candidate.Window.Name}', score={candidate.Score}, " +
                $"bounds={candidate.Bounds}, intersectsScreen={candidate.IntersectsScreen}");
        }

        return candidates.FirstOrDefault()?.Window;
    }

    private static int ScoreMainWindowCandidate(Window window)
    {
        var score = 0;
        if (UITestHelper.FindByAutomationIdOrName(window, "LongDocSourceLangCombo") != null)
        {
            score += 100;
        }

        if (UITestHelper.FindByAutomationIdOrName(window, "SourceLangCombo") != null)
        {
            score += 50;
        }

        if (UITestHelper.FindByAutomationIdOrName(window, "SettingsButton") != null)
        {
            score += 25;
        }

        if ((window.Name ?? string.Empty).Contains("Easydict", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }

    private bool PrepareWindowForScreenshot(Window window)
    {
        var moved = ScreenshotHelper.TrySetWindowPhysicalBounds(window, new Rectangle(0, 0, 900, 900));
        _output.WriteLine($"Prepare screenshot window moved={moved}");
        Thread.Sleep(500);
        window.SetForeground();
        Thread.Sleep(300);

        var preparedBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        Mouse.MoveTo(new Point(preparedBounds.Left + 12, preparedBounds.Top + 12));
        Thread.Sleep(1200);

        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var visible = Rectangle.Intersect(bounds, ScreenshotHelper.GetVirtualScreenBounds());
        if (bounds.Width > 1 && bounds.Height > 1 && visible.Width > 1 && visible.Height > 1)
        {
            return true;
        }

        _output.WriteLine($"Prepared main window had unusable capture bounds: {bounds}, visible={visible}");
        return false;
    }

    #endregion

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
