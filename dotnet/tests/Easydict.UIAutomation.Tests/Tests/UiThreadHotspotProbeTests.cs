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
/// Broad but lightweight UI scenario for DEBUG [UIHotspot] collection.
/// It exercises the surfaces that usually enqueue UI work: quick translation,
/// mode switching, long-document controls, settings tabs, and floating windows.
/// </summary>
[Trait("Category", "UIAutomation")]
[Trait("Category", "UIHotspot")]
[Collection("UIAutomation")]
public sealed class UiThreadHotspotProbeTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;

    public UiThreadHotspotProbeTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void MainSettingsModesAndFloatingWindows_ShouldEmitUiHotspots()
    {
        var window = _launcher.GetMainWindow(TimeSpan.FromSeconds(60));
        window.Should().NotBeNull("main window must be available for hotspot probing");
        window.SetForeground();
        Thread.Sleep(500);

        ExerciseQuickTranslate(window, runTranslation: ResolveFlag("EASYDICT_UI_HOTSPOT_RUN_TRANSLATION", defaultValue: true));
        SwitchToLongDocMode(window);
        ExerciseLongDocControls(window);
        SwitchToQuickTranslateMode(window);
        OpenSettingsExerciseTabsAndReturn(window);
        ExerciseFloatingWindows(window);
        ExerciseQuickTranslate(window, runTranslation: false);
    }

    private static bool ResolveFlag(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private void ExerciseQuickTranslate(Window window, bool runTranslation)
    {
        var inputBox = UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(15));
        inputBox.Should().NotBeNull("InputTextBox must exist on the quick translation page");

        inputBox!.Click();
        Thread.Sleep(200);
        inputBox.Text = "UI hotspot probe text";
        Thread.Sleep(350);
        _output.WriteLine("[UIHotspotProbe] Main quick-translate input updated.");

        if (!runTranslation)
        {
            return;
        }

        Keyboard.Type(VirtualKeyShort.ENTER);
        Thread.Sleep(TimeSpan.FromSeconds(7));
        _output.WriteLine("[UIHotspotProbe] Main quick-translate submitted.");
    }

    private void SwitchToLongDocMode(Window window)
    {
        ClickModeMenuItem(window, "ModeLongDocItem");

        Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, "LongDocSourceLangCombo"),
                TimeSpan.FromSeconds(12))
            .Result
            .Should()
            .NotBeNull("LongDocSourceLangCombo should appear after switching to long document mode");

        Thread.Sleep(500);
    }

    private void ExerciseLongDocControls(Window window)
    {
        TryExerciseComboBox(window, "LongDocInputModeCombo", "Text", 0);
        TryExerciseComboBox(window, "LongDocOutputModeCombo", "Bilingual", 1);
        TryTypeIntoControl(window, "LongDocConcurrencyBox", "4");
        TryTypeIntoControl(window, "LongDocPageRangeBox", "1-3");
        _output.WriteLine("[UIHotspotProbe] Long-document controls exercised.");
    }

    private void SwitchToQuickTranslateMode(Window window)
    {
        ClickModeMenuItem(window, "ModeTranslationItem");

        UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(12))
            .Should()
            .NotBeNull("InputTextBox should appear after switching back to quick translation mode");

        Thread.Sleep(500);
    }

    private void OpenSettingsExerciseTabsAndReturn(Window window)
    {
        var settingsButton = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, "SettingsButton"),
                TimeSpan.FromSeconds(12))
            .Result;
        settingsButton.Should().NotBeNull("SettingsButton must be available before settings navigation");

        InvokeOrClick(settingsButton!);
        Thread.Sleep(1500);

        Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, "BackButton"),
                TimeSpan.FromSeconds(20))
            .Result
            .Should()
            .NotBeNull("settings page should expose a back button after loading");

        foreach (var tab in new[] { "General", "Services", "Views", "Hotkeys", "Advanced", "Language", "About", "General" })
        {
            var automationId = $"SettingsTab_{tab}";
            var tabButton = Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, automationId)
                          ?? FindVisibleByAutomationIdOrName(window, tab),
                    TimeSpan.FromSeconds(10))
                .Result;

            if (tabButton == null)
            {
                _output.WriteLine($"[UIHotspotProbe] Settings tab skipped: {tab}");
                continue;
            }

            InvokeOrClick(tabButton);
            Thread.Sleep(tab is "Advanced" or "Language" or "Views" ? 1000 : 600);
            _output.WriteLine($"[UIHotspotProbe] Settings tab visited: {tab}");
        }

        ReturnFromSettings(window);
    }

    private void ReturnFromSettings(Window window)
    {
        var backButton = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, "FloatingBackButton")
                      ?? FindVisibleByAutomationIdOrName(window, "BackButton"),
                TimeSpan.FromSeconds(12))
            .Result;
        backButton.Should().NotBeNull("settings page must expose a back button");

        InvokeOrClick(backButton!);
        Thread.Sleep(1500);

        var returned = WaitForMainSurface(window, TimeSpan.FromSeconds(12));

        if (returned == null && TryHandleSettingsNavigationDialog(window))
        {
            returned = WaitForMainSurface(window, TimeSpan.FromSeconds(12));
        }

        if (returned == null)
        {
            TryNavigateBackWithKeyboard();
            returned = WaitForMainSurface(window, TimeSpan.FromSeconds(8));
        }

        returned.Should().NotBeNull("main page should be visible after returning from settings");
    }

    private static AutomationElement? WaitForMainSurface(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, "SettingsButton")
                      ?? FindVisibleByAutomationIdOrName(window, "InputTextBox")
                      ?? FindVisibleByAutomationIdOrName(window, "ModeMenuButton"),
                timeout)
            .Result;
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

        _output.WriteLine($"[UIHotspotProbe] Handling settings navigation dialog with '{SafeName(dialogButton)}'.");
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

    private void ExerciseFloatingWindows(Window mainWindow)
    {
        OpenExerciseAndCloseFloatingWindow(mainWindow, "Mini", VirtualKeyShort.KEY_M);
        Thread.Sleep(500);
        OpenExerciseAndCloseFloatingWindow(mainWindow, "Fixed", VirtualKeyShort.KEY_F);
    }

    private void OpenExerciseAndCloseFloatingWindow(Window mainWindow, string windowType, VirtualKeyShort hotkey)
    {
        mainWindow.SetForeground();
        Thread.Sleep(300);

        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, hotkey);
        Thread.Sleep(1500);

        var floatingWindow = Retry.WhileNull(
                () => FindFloatingWindow(mainWindow, windowType),
                TimeSpan.FromSeconds(10))
            .Result;
        floatingWindow.Should().NotBeNull($"{windowType} window must open during the hotspot probe");

        floatingWindow!.SetForeground();
        Thread.Sleep(400);

        var inputBox = UITestHelper.FindInputTextBox(floatingWindow, TimeSpan.FromSeconds(8));
        if (inputBox != null)
        {
            inputBox.Click();
            Thread.Sleep(200);
            inputBox.Text = $"{windowType} hotspot probe text";
            Thread.Sleep(500);
        }
        else
        {
            _output.WriteLine($"[UIHotspotProbe] {windowType} input skipped; InputTextBox not found.");
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
        _output.WriteLine($"[UIHotspotProbe] {windowType} window exercised.");
    }

    private Window? FindFloatingWindow(Window mainWindow, string titlePart)
    {
        try
        {
            return _launcher.Application.GetAllTopLevelWindows(_launcher.Automation)
                .Where(window => !string.Equals(window.Title, mainWindow.Title, StringComparison.Ordinal))
                .FirstOrDefault(window => (window.Title ?? string.Empty).Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                   ?? _launcher.Application.GetAllTopLevelWindows(_launcher.Automation)
                       .Where(window => !string.Equals(window.Title, mainWindow.Title, StringComparison.Ordinal))
                       .OrderBy(window => window.BoundingRectangle.Width * window.BoundingRectangle.Height)
                       .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[UIHotspotProbe] Failed to enumerate floating windows: {ex.Message}");
            return null;
        }
    }

    private void ClickModeMenuItem(Window window, string menuItemAutomationId)
    {
        var expectedSurface = menuItemAutomationId == "ModeLongDocItem"
            ? "LongDocSourceLangCombo"
            : "InputTextBox";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var titleButton = Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, "ModeMenuButton"),
                    TimeSpan.FromSeconds(10))
                .Result;
            titleButton.Should().NotBeNull("mode dropdown button should exist");

            OpenModeMenu(titleButton!);

            var menuItem = Retry.WhileNull(
                    () => FindModeMenuItem(window, menuItemAutomationId),
                    TimeSpan.FromSeconds(5))
                .Result;

            if (menuItem == null)
            {
                _output.WriteLine($"[UIHotspotProbe] Attempt {attempt}: {menuItemAutomationId} not found.");
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Thread.Sleep(250);
                continue;
            }

            ActivateModeMenuItem(menuItem);
            Thread.Sleep(800);

            var switched = Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, expectedSurface),
                    TimeSpan.FromSeconds(5))
                .Result;

            if (switched != null)
            {
                _output.WriteLine($"[UIHotspotProbe] Switched via {menuItemAutomationId}.");
                return;
            }
        }

        FindVisibleByAutomationIdOrName(window, expectedSurface)
            .Should()
            .NotBeNull($"{expectedSurface} should appear after switching via {menuItemAutomationId}");
    }

    private static void OpenModeMenu(AutomationElement titleButton)
    {
        try
        {
            Mouse.Click(titleButton.GetClickablePoint());
            Thread.Sleep(500);
            return;
        }
        catch
        {
            // Fall through to Invoke/Click fallback.
        }

        InvokeOrClick(titleButton);
        Thread.Sleep(500);
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
            // Popup menu tree may still be opening.
        }

        try
        {
            candidates.AddRange(_launcher.Automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)));
        }
        catch
        {
            // Desktop enumeration is best effort.
        }

        return candidates.FirstOrDefault(element => IsModeMenuItemMatch(element, menuItemAutomationId) && IsVisibleOnScreen(element))
            ?? candidates.FirstOrDefault(element => IsModeMenuItemMatch(element, menuItemAutomationId));
    }

    private static bool IsModeMenuItemMatch(AutomationElement element, string menuItemAutomationId)
    {
        if (string.Equals(SafeAutomationId(element), menuItemAutomationId, StringComparison.OrdinalIgnoreCase))
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
            _output.WriteLine($"[UIHotspotProbe] SelectionItem activation failed: {ex.Message}");
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
            _output.WriteLine($"[UIHotspotProbe] Toggle activation failed: {ex.Message}");
        }

        InvokeOrClick(menuItem);
    }

    private void TryExerciseComboBox(Window window, string automationId, string itemName, int fallbackIndex)
    {
        var combo = Retry.WhileNull(
                () => FindByAutomationIdOrName(window, automationId)?.AsComboBox(),
                TimeSpan.FromSeconds(5))
            .Result;
        if (combo == null)
        {
            _output.WriteLine($"[UIHotspotProbe] Combo skipped: {automationId}");
            return;
        }

        try
        {
            combo.Expand();
            Thread.Sleep(400);
            var item = combo.Items.FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.Click();
            }
            else
            {
                combo.Select(fallbackIndex);
            }

            Thread.Sleep(400);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[UIHotspotProbe] Combo skipped: {automationId}; {ex.Message}");
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(200);
        }
    }

    private void TryTypeIntoControl(Window window, string automationId, string text)
    {
        var control = Retry.WhileNull(
                () => FindByAutomationIdOrName(window, automationId),
                TimeSpan.FromSeconds(5))
            .Result;
        if (control == null)
        {
            _output.WriteLine($"[UIHotspotProbe] Text input skipped: {automationId}");
            return;
        }

        try
        {
            control.Click();
            Thread.Sleep(200);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Thread.Sleep(100);
            Keyboard.Type(text);
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(400);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[UIHotspotProbe] Text input skipped: {automationId}; {ex.Message}");
        }
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

    private static void InvokeOrClick(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }

        element.Click();
    }

    private static void TryNavigateBackWithKeyboard()
    {
        try
        {
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

    private static string SafeAutomationId(AutomationElement element)
    {
        try { return element.AutomationId ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
