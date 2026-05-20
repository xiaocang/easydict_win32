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
/// Lightweight scenario used by the PR memory gate.
/// The default path exercises launch, idle, quick-translate text entry,
/// mode switching, settings navigation, close, and post-close idle without
/// invoking real translation services.
/// </summary>
[Trait("Category", "UIAutomation")]
[Trait("Category", "MemoryGate")]
[Collection("UIAutomation")]
public sealed class MemoryGateTests : IDisposable
{
    private const string GateInputText = "Memory gate mock selection text";

    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public MemoryGateTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(60));
        WriteProcessIdMarker();
        WritePhaseMarker("00-process-started");
    }

    [Fact]
    public void PrMemoryGate_LightweightWindowAndSelectionScenario()
    {
        var initialIdle = ResolveDelaySeconds("EASYDICT_MEMORY_GATE_INITIAL_IDLE_SECONDS", 30);
        var postCloseIdle = ResolveDelaySeconds("EASYDICT_MEMORY_GATE_POST_CLOSE_IDLE_SECONDS", 15);
        var runRealTranslation = ResolveFlag("EASYDICT_MEMORY_GATE_RUN_TRANSLATION");

        var window = _launcher.GetMainWindow(TimeSpan.FromSeconds(60));
        window.Should().NotBeNull("main window must be available for the memory gate scenario");
        WritePhaseMarker("01-main-window-observed");

        _output.WriteLine($"[MemoryGate] Initial idle: {initialIdle}s");
        Thread.Sleep(TimeSpan.FromSeconds(initialIdle));
        WritePhaseMarker("02-initial-idle-complete");

        window.SetForeground();
        Thread.Sleep(500);
        WritePhaseMarker("03-main-window-focused");

        var inputBox = UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(15));
        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        inputBox!.Click();
        Thread.Sleep(250);
        WritePhaseMarker("04-input-focused");

        inputBox.Text = GateInputText;
        Thread.Sleep(500);
        WritePhaseMarker("05-input-text-entered");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(300);
        WritePhaseMarker("06-input-text-selected");

        if (runRealTranslation)
        {
            _output.WriteLine("[MemoryGate] EASYDICT_MEMORY_GATE_RUN_TRANSLATION enabled; pressing Enter.");
            Keyboard.Type(VirtualKeyShort.ENTER);
            Thread.Sleep(TimeSpan.FromSeconds(5));
            WritePhaseMarker("07-translation-submitted");
        }
        else
        {
            WritePhaseMarker("07-translation-submit-skipped");
        }

        SwitchToLongDocMode(window);
        ExerciseLongDocControls(window);
        SwitchToQuickTranslateMode(window);
        OpenSettingsExerciseTabsAndReturn(window);
        ExerciseFloatingWindows(window);

        _output.WriteLine("[MemoryGate] Closing main window");
        window.Close();
        Thread.Sleep(TimeSpan.FromSeconds(1));
        WritePhaseMarker("18-main-window-closed");

        _output.WriteLine($"[MemoryGate] Post-close idle: {postCloseIdle}s");
        WriteMarker("EASYDICT_MEMORY_GATE_CLOSED_MARKER_PATH");
        WaitForReleaseOrIdle(postCloseIdle);
        WritePhaseMarker("19-post-close-idle-complete");
    }

    private static int ResolveDelaySeconds(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(value, out var seconds))
        {
            return defaultValue;
        }

        return Math.Clamp(seconds, 0, 300);
    }

    private static bool ResolveFlag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMarker(string envName)
    {
        WriteMarker(envName, DateTimeOffset.UtcNow.ToString("O"));
    }

    private static void WriteMarker(string envName, string content)
    {
        var path = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }

    private void WritePhaseMarker(string phaseName)
    {
        var phaseDir = Environment.GetEnvironmentVariable("EASYDICT_MEMORY_GATE_PHASE_DIR");
        if (string.IsNullOrWhiteSpace(phaseDir))
        {
            return;
        }

        Directory.CreateDirectory(phaseDir);
        var markerPath = Path.Combine(phaseDir, $"{phaseName}.marker");
        File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
        _output.WriteLine($"[MemoryGate] Phase: {phaseName}");
    }

    private void WriteProcessIdMarker()
    {
        try
        {
            WriteMarker("EASYDICT_MEMORY_GATE_PROCESS_ID_PATH", _launcher.Application.ProcessId.ToString());
        }
        catch
        {
            // The script still has process-name fallback if FlaUI cannot expose the PID.
        }
    }

    private static void WaitForReleaseOrIdle(int seconds)
    {
        var timeout = TimeSpan.FromSeconds(seconds);
        var releasePath = Environment.GetEnvironmentVariable("EASYDICT_MEMORY_GATE_RELEASE_MARKER_PATH");
        if (string.IsNullOrWhiteSpace(releasePath))
        {
            Thread.Sleep(timeout);
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(releasePath))
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    private void SwitchToLongDocMode(Window window)
    {
        WritePhaseMarker("08-long-doc-switch-start");
        ClickModeMenuItem(window, "ModeLongDocItem");

        var longDocSourceCombo = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, "LongDocSourceLangCombo"),
            TimeSpan.FromSeconds(10)).Result;

        longDocSourceCombo.Should().NotBeNull("LongDocSourceLangCombo should appear after switching to long document mode");
        Thread.Sleep(500);
        WritePhaseMarker("09-long-doc-mode-ready");
    }

    private void ExerciseLongDocControls(Window window)
    {
        TryExerciseComboBox(window, "LongDocInputModeCombo", "Text", 0, "10-long-doc-input-mode-text");
        TryExerciseComboBox(window, "LongDocOutputModeCombo", "Bilingual", 1, "11-long-doc-output-mode-bilingual");

        TryTypeIntoControl(window, "LongDocConcurrencyBox", "4", "12-long-doc-concurrency-set");
        TryTypeIntoControl(window, "LongDocPageRangeBox", "1-3", "13-long-doc-page-range-set");
    }

    private void SwitchToQuickTranslateMode(Window window)
    {
        WritePhaseMarker("14-quick-translate-switch-start");
        ClickModeMenuItem(window, "ModeTranslationItem");

        var inputBox = UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(10));
        inputBox.Should().NotBeNull("InputTextBox should appear after switching back to quick translation mode");
        Thread.Sleep(500);
        WritePhaseMarker("15-quick-translate-mode-ready");
    }

    private void OpenSettingsExerciseTabsAndReturn(Window window)
    {
        OpenSettingsPage(window, "16-settings-opened");
        ExerciseSettingsTabs(window);
        ReturnFromSettings(window, "17-settings-returned");

        OpenSettingsPage(window, "17a-settings-reopened");
        ReturnFromSettings(window, "17b-settings-returned-again");
    }

    private void OpenSettingsPage(Window window, string phaseName)
    {
        var settingsButton = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, "SettingsButton"),
            TimeSpan.FromSeconds(10)).Result;

        settingsButton.Should().NotBeNull("SettingsButton must be available before settings navigation");
        InvokeOrClick(settingsButton!);
        Thread.Sleep(1500);

        var settingsScrollViewer = Retry.WhileNull(
            () => FindVisibleByAutomationIdOrName(window, "MainScrollViewer"),
            TimeSpan.FromSeconds(30)).Result;

        settingsScrollViewer.Should().NotBeNull("settings page should expose visible MainScrollViewer after navigation");

        var backButton = Retry.WhileNull(
            () => FindVisibleByAutomationIdOrName(window, "BackButton"),
            TimeSpan.FromSeconds(15)).Result;

        backButton.Should().NotBeNull("settings page should expose a visible back button after loading");
        WritePhaseMarker(phaseName);
    }

    private void ExerciseSettingsTabs(Window window)
    {
        var tabs = new[]
        {
            "General",
            "Services",
            "Views",
            "Hotkeys",
            "Advanced",
            "Language",
            "About",
            "General"
        };

        foreach (var tab in tabs)
        {
            var automationId = $"SettingsTab_{tab}";
            var tabButton = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, automationId)
                      ?? FindVisibleByAutomationIdOrName(window, tab),
                TimeSpan.FromSeconds(15)).Result;

            if (tabButton == null)
            {
                _output.WriteLine($"[MemoryGate] Skipping settings tab {tab}; {automationId} not found.");
                WritePhaseMarker($"16a-settings-tab-{tab.ToLowerInvariant()}-skipped");
                continue;
            }

            InvokeOrClick(tabButton);
            Thread.Sleep(TimeSpan.FromMilliseconds(tab is "Advanced" or "Language" or "Views" ? 1200 : 700));
            WritePhaseMarker($"16a-settings-tab-{tab.ToLowerInvariant()}");
        }
    }

    private void ReturnFromSettings(Window window, string phaseName)
    {
        var settingsScrollViewer = FindByAutomationIdOrName(window, "MainScrollViewer");
        if (settingsScrollViewer != null)
        {
            try
            {
                settingsScrollViewer.Patterns.Scroll.Pattern.SetScrollPercent(
                    settingsScrollViewer.Patterns.Scroll.Pattern.HorizontalScrollPercent,
                    0);
                Thread.Sleep(300);
            }
            catch
            {
                // Settings tab switches normally reset the scroll position. This is only a best-effort aid.
            }
        }

        var backButton = Retry.WhileNull(
            () => FindVisibleByAutomationIdOrName(window, "FloatingBackButton")
                  ?? FindVisibleByAutomationIdOrName(window, "BackButton"),
            TimeSpan.FromSeconds(15)).Result;

        backButton.Should().NotBeNull("settings page must expose a back button");
        InvokeOrClick(backButton!);
        Thread.Sleep(1500);

        var returnedSettingsButton = WaitForMainPage(window, TimeSpan.FromSeconds(10));

        if (returnedSettingsButton == null && TryHandleSettingsNavigationDialog(window))
        {
            returnedSettingsButton = WaitForMainPage(window, TimeSpan.FromSeconds(15));
        }

        if (returnedSettingsButton == null)
        {
            TryNavigateBackWithKeyboard(window);
            returnedSettingsButton = WaitForMainPage(window, TimeSpan.FromSeconds(10));
        }

        returnedSettingsButton.Should().NotBeNull("main page should be visible after returning from settings");
        WritePhaseMarker(phaseName);
    }

    private static AutomationElement? WaitForMainPage(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => FindVisibleByAutomationIdOrName(window, "SettingsButton"),
            timeout).Result;
    }

    private bool TryHandleSettingsNavigationDialog(Window window)
    {
        var dialogButton = FindDialogButton(
            window,
            "SecondaryButton",
            "Don't Save",
            "Dont Save",
            "Discard",
            "不保存",
            "不儲存")
            ?? FindDialogButton(
                window,
                "PrimaryButton",
                "Save Settings",
                "Save",
                "保存");

        if (dialogButton == null)
        {
            return false;
        }

        _output.WriteLine($"[MemoryGate] Handling settings navigation dialog with '{SafeName(dialogButton)}'.");
        InvokeOrClick(dialogButton);
        Thread.Sleep(1000);
        return true;
    }

    private static AutomationElement? FindDialogButton(Window window, string automationId, params string[] names)
    {
        var byAutomationId = FindVisibleByAutomationIdOrName(window, automationId);
        if (byAutomationId != null)
        {
            return byAutomationId;
        }

        try
        {
            return window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(button =>
                {
                    var name = SafeName(button);
                    return !string.IsNullOrWhiteSpace(name) &&
                           names.Any(candidate => name.Contains(candidate, StringComparison.OrdinalIgnoreCase));
                });
        }
        catch
        {
            return null;
        }
    }

    private static void TryNavigateBackWithKeyboard(Window window)
    {
        try
        {
            window.SetForeground();
            Thread.Sleep(250);
            Keyboard.Press(VirtualKeyShort.ALT);
            Thread.Sleep(50);
            Keyboard.Press(VirtualKeyShort.LEFT);
            Thread.Sleep(50);
            Keyboard.Release(VirtualKeyShort.LEFT);
            Keyboard.Release(VirtualKeyShort.ALT);
            Thread.Sleep(1000);
        }
        catch
        {
            try { Keyboard.Release(VirtualKeyShort.LEFT); } catch { /* ignore */ }
            try { Keyboard.Release(VirtualKeyShort.ALT); } catch { /* ignore */ }
        }
    }

    private void ExerciseFloatingWindows(Window mainWindow)
    {
        mainWindow.SetForeground();
        Thread.Sleep(500);

        OpenExerciseAndCloseFloatingWindow(
            mainWindow,
            windowType: "Mini",
            hotkey: VirtualKeyShort.KEY_M,
            openedPhase: "17c-mini-window-opened",
            inputPhase: "17d-mini-window-input-entered",
            closedPhase: "17e-mini-window-closed");

        mainWindow.SetForeground();
        Thread.Sleep(500);

        OpenExerciseAndCloseFloatingWindow(
            mainWindow,
            windowType: "Fixed",
            hotkey: VirtualKeyShort.KEY_F,
            openedPhase: "17f-fixed-window-opened",
            inputPhase: "17g-fixed-window-input-entered",
            closedPhase: "17h-fixed-window-closed");
    }

    private void OpenExerciseAndCloseFloatingWindow(
        Window mainWindow,
        string windowType,
        VirtualKeyShort hotkey,
        string openedPhase,
        string inputPhase,
        string closedPhase)
    {
        _output.WriteLine($"[MemoryGate] Opening {windowType} window with Ctrl+Alt+{hotkey}.");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, hotkey);
        Thread.Sleep(1500);

        var floatingWindow = Retry.WhileNull(
            () => FindFloatingWindow(mainWindow, windowType),
            TimeSpan.FromSeconds(10)).Result;

        floatingWindow.Should().NotBeNull($"{windowType} window must open during the memory gate scenario");
        WritePhaseMarker(openedPhase);

        floatingWindow!.SetForeground();
        Thread.Sleep(500);

        var inputBox = UITestHelper.FindInputTextBox(floatingWindow, TimeSpan.FromSeconds(8));
        if (inputBox != null)
        {
            inputBox.Click();
            Thread.Sleep(200);
            inputBox.Text = $"{windowType} memory gate text";
            Thread.Sleep(500);
            WritePhaseMarker(inputPhase);
        }
        else
        {
            _output.WriteLine($"[MemoryGate] Skipping {windowType} window text entry; InputTextBox not found.");
            WritePhaseMarker($"{inputPhase}-skipped");
        }

        var closeButton = FindByAutomationIdOrName(floatingWindow, "CloseButton");
        if (closeButton != null)
        {
            InvokeOrClick(closeButton);
        }
        else
        {
            floatingWindow.Close();
        }

        Thread.Sleep(1000);
        WritePhaseMarker(closedPhase);
    }

    private Window? FindFloatingWindow(Window mainWindow, string titlePart)
    {
        try
        {
            var windows = _launcher.Application.GetAllTopLevelWindows(_launcher.Automation);
            foreach (var window in windows)
            {
                _output.WriteLine(
                    $"[MemoryGate] Top-level window: \"{window.Title}\" " +
                    $"bounds={window.BoundingRectangle.Width}x{window.BoundingRectangle.Height}");
            }

            return windows.FirstOrDefault(w =>
                       !string.Equals(w.Title, mainWindow.Title, StringComparison.Ordinal) &&
                       (w.Title ?? string.Empty).Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                   ?? windows
                       .Where(w => !string.Equals(w.Title, mainWindow.Title, StringComparison.Ordinal))
                       .OrderBy(w => w.BoundingRectangle.Width * w.BoundingRectangle.Height)
                       .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[MemoryGate] Failed to enumerate floating windows: {ex.Message}");
            return null;
        }
    }

    private void ClickModeMenuItem(Window window, string menuItemAutomationId)
    {
        var expectedModeName = GetExpectedModeButtonName(menuItemAutomationId);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var titleButton = Retry.WhileNull(
                () => FindTitleButton(window),
                TimeSpan.FromSeconds(10)).Result;

            titleButton.Should().NotBeNull("title mode dropdown button should exist");
            _output.WriteLine(
                $"[MemoryGate] Attempt {attempt}: mode button Name='{SafeName(titleButton!)}' " +
                $"AutomationId='{SafeAutomationId(titleButton!)}' Bounds={SafeBounds(titleButton!)}");
            var menuOpened = OpenModeMenu(window, titleButton!, menuItemAutomationId);
            if (menuOpened)
            {
                SelectModeMenuItemWithKeyboard(menuItemAutomationId);
            }

            var modeSurface = Retry.WhileNull(
                () => FindModeButtonWithName(window, expectedModeName)
                      ?? FindModeSurfaceMarker(window, menuItemAutomationId),
                TimeSpan.FromSeconds(5)).Result;

            if (modeSurface != null)
            {
                _output.WriteLine($"[MemoryGate] Attempt {attempt}: switched via {menuItemAutomationId} keyboard selection.");
                return;
            }

            titleButton = Retry.WhileNull(
                () => FindTitleButton(window),
                TimeSpan.FromSeconds(5)).Result;

            if (titleButton == null)
            {
                _output.WriteLine($"[MemoryGate] Attempt {attempt}: title mode dropdown button disappeared.");
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Thread.Sleep(250);
                continue;
            }

            OpenModeMenu(window, titleButton, menuItemAutomationId);

            var menuItem = Retry.WhileNull(
                () => FindModeMenuItem(window, menuItemAutomationId),
                TimeSpan.FromSeconds(5)).Result;

            if (menuItem == null)
            {
                _output.WriteLine($"[MemoryGate] Attempt {attempt}: {menuItemAutomationId} did not appear.");
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Thread.Sleep(250);
                continue;
            }

            _output.WriteLine(
                $"[MemoryGate] Attempt {attempt}: target {menuItemAutomationId} " +
                $"Name='{SafeName(menuItem)}' AutomationId='{SafeAutomationId(menuItem)}' " +
                $"Bounds={SafeBounds(menuItem)} Offscreen={SafeOffscreen(menuItem)} Enabled={SafeIsEnabled(menuItem)}");
            ActivateModeMenuItem(menuItem);
            Thread.Sleep(700);

            modeSurface = Retry.WhileNull(
                () => FindModeButtonWithName(window, expectedModeName)
                      ?? FindModeSurfaceMarker(window, menuItemAutomationId),
                TimeSpan.FromSeconds(5)).Result;

            if (modeSurface != null)
            {
                _output.WriteLine($"[MemoryGate] Attempt {attempt}: switched via {menuItemAutomationId}.");
                return;
            }

            _output.WriteLine($"[MemoryGate] Attempt {attempt}: clicked {menuItemAutomationId}, mode button did not become '{expectedModeName}'.");
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(250);
        }

        (FindModeButtonWithName(window, expectedModeName) ?? FindModeSurfaceMarker(window, menuItemAutomationId))
            .Should()
            .NotBeNull($"mode surface should switch to '{expectedModeName}' after clicking {menuItemAutomationId}");
    }

    private static void SelectModeMenuItemWithKeyboard(string menuItemAutomationId)
    {
        var targetKey = menuItemAutomationId == "ModeTranslationItem"
            ? VirtualKeyShort.HOME
            : VirtualKeyShort.END;

        Keyboard.Press(targetKey);
        Thread.Sleep(100);
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(700);
    }

    private bool OpenModeMenu(Window window, AutomationElement titleButton, string targetMenuItemAutomationId)
    {
        ClickElementAtPoint(titleButton);
        Thread.Sleep(500);
        if (FindModeMenuItem(window, targetMenuItemAutomationId) != null)
        {
            return true;
        }

        try
        {
            if (titleButton.Patterns.Invoke.IsSupported)
            {
                titleButton.Patterns.Invoke.Pattern.Invoke();
                Thread.Sleep(500);
            }
        }
        catch
        {
            // Fall through to the caller's normal retry path.
        }

        return FindModeMenuItem(window, targetMenuItemAutomationId) != null;
    }

    private void ActivateModeMenuItem(AutomationElement menuItem)
    {
        try
        {
            if (menuItem.Patterns.SelectionItem.IsSupported)
            {
                menuItem.Patterns.SelectionItem.Pattern.Select();
                return;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[MemoryGate] SelectionItem activation failed: {ex.Message}");
        }

        try
        {
            if (menuItem.Patterns.Toggle.IsSupported)
            {
                menuItem.Patterns.Toggle.Pattern.Toggle();
                return;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[MemoryGate] Toggle activation failed: {ex.Message}");
        }

        InvokeOrClick(menuItem);
    }

    private AutomationElement? FindModeMenuItem(Window window, string menuItemAutomationId)
    {
        var candidates = new List<AutomationElement>();

        try
        {
            candidates.AddRange(window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)));
        }
        catch
        {
            // Popup menus can move in the UIA tree while the flyout is opening.
        }

        try
        {
            candidates.AddRange(_launcher.Automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)));
        }
        catch
        {
            // Desktop enumeration is best-effort; the window subtree is the primary source.
        }

        var matches = candidates
            .Where(e => IsModeMenuItemMatch(e, menuItemAutomationId))
            .DistinctBy(e => $"{SafeAutomationId(e)}|{SafeName(e)}|{SafeBounds(e)}")
            .ToArray();

        return matches.FirstOrDefault(IsVisibleOnScreen)
            ?? matches.FirstOrDefault();
    }

    private static bool IsModeMenuItemMatch(AutomationElement element, string menuItemAutomationId)
    {
        var automationId = SafeAutomationId(element);
        if (string.Equals(automationId, menuItemAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = SafeName(element);
        return menuItemAutomationId switch
        {
            "ModeTranslationItem" => name.Contains("Translation", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("Translate", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("翻译", StringComparison.OrdinalIgnoreCase),
            "ModeLongDocItem" => name.Contains("Long", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("Document", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("文档", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsVisibleOnScreen(AutomationElement element)
    {
        try
        {
            var bounds = element.BoundingRectangle;
            return !element.IsOffscreen && bounds.Width > 0 && bounds.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeAutomationId(AutomationElement element)
    {
        try
        {
            return element.AutomationId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeBounds(AutomationElement element)
    {
        try
        {
            return element.BoundingRectangle.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeOffscreen(AutomationElement element)
    {
        try
        {
            return element.IsOffscreen.ToString();
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string SafeIsEnabled(AutomationElement element)
    {
        try
        {
            return element.IsEnabled.ToString();
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string GetExpectedModeButtonName(string menuItemAutomationId)
    {
        return menuItemAutomationId switch
        {
            "ModeLongDocItem" => "Long Document",
            "ModeTranslationItem" => "Translation",
            _ => throw new ArgumentOutOfRangeException(nameof(menuItemAutomationId), menuItemAutomationId, "Unsupported mode menu item"),
        };
    }

    private static AutomationElement? FindModeSurfaceMarker(Window window, string menuItemAutomationId)
    {
        return menuItemAutomationId switch
        {
            "ModeLongDocItem" => FindVisibleByAutomationIdOrName(window, "LongDocSourceLangCombo"),
            "ModeTranslationItem" => FindVisibleByAutomationIdOrName(window, "InputTextBox"),
            _ => null,
        };
    }

    private void TryExerciseComboBox(Window window, string automationId, string itemName, int fallbackIndex, string phaseName)
    {
        var combo = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, automationId)?.AsComboBox(),
            TimeSpan.FromSeconds(5)).Result;

        if (combo == null)
        {
            _output.WriteLine($"[MemoryGate] Skipping {automationId}; combo not found.");
            WritePhaseMarker($"{phaseName}-skipped");
            return;
        }

        try
        {
            combo.Expand();
            Thread.Sleep(500);
            var item = combo.Items.FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.Click();
            }
            else
            {
                combo.Select(fallbackIndex);
            }

            Thread.Sleep(500);
            WritePhaseMarker(phaseName);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[MemoryGate] Skipping {automationId}; selection failed: {ex.Message}");
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(200);
            WritePhaseMarker($"{phaseName}-skipped");
        }
    }

    private void TryTypeIntoControl(Window window, string automationId, string text, string phaseName)
    {
        var control = Retry.WhileNull(
            () => FindByAutomationIdOrName(window, automationId),
            TimeSpan.FromSeconds(5)).Result;

        if (control == null)
        {
            _output.WriteLine($"[MemoryGate] Skipping {automationId}; control not found.");
            WritePhaseMarker($"{phaseName}-skipped");
            return;
        }

        try
        {
            control.Click();
            Thread.Sleep(250);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Thread.Sleep(100);
            Keyboard.Type(text);
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(500);
            WritePhaseMarker(phaseName);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[MemoryGate] Skipping {automationId}; text entry failed: {ex.Message}");
            WritePhaseMarker($"{phaseName}-skipped");
        }
    }

    private static AutomationElement? FindTitleButton(Window window)
    {
        var modeButton = FindVisibleByAutomationIdOrName(window, "ModeMenuButton");
        if (modeButton != null)
        {
            return modeButton;
        }

        var easydictText = FindByAutomationIdOrName(window, "Easydict");
        var current = easydictText;
        while (current != null)
        {
            if (current.ControlType == ControlType.Button)
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static AutomationElement? FindModeButtonWithName(Window window, string expectedNamePart)
    {
        var modeButton = FindVisibleByAutomationIdOrName(window, "ModeMenuButton");
        if (modeButton == null)
        {
            return null;
        }

        var name = SafeName(modeButton);
        return name.Contains(expectedNamePart, StringComparison.OrdinalIgnoreCase)
            ? modeButton
            : null;
    }

    private static AutomationElement? FindByAutomationIdOrName(Window window, string name)
    {
        try
        {
            return window.FindFirstDescendant(cf => cf.ByAutomationId(name))
                ?? window.FindFirstDescendant(cf => cf.ByName(name));
        }
        catch
        {
            return null;
        }
    }

    private static AutomationElement? FindVisibleByAutomationIdOrName(Window window, string name)
    {
        var element = FindByAutomationIdOrName(window, name);
        if (element == null)
        {
            return null;
        }

        try
        {
            return element.IsOffscreen ? null : element;
        }
        catch
        {
            return element;
        }
    }

    private static void InvokeOrClick(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }

        element.Click();
    }

    private static void ClickElementAtPoint(AutomationElement element)
    {
        try
        {
            Mouse.Click(element.GetClickablePoint());
            return;
        }
        catch
        {
            element.Click();
        }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
