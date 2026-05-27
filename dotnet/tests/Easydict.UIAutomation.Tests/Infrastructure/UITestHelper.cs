using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Shared helpers for UI automation tests that interact with
/// keyboard hotkeys and multiple application windows.
/// </summary>
public static class UITestHelper
{
    /// <summary>
    /// Send a hotkey combination safely, ensuring all keys are released even on failure.
    /// </summary>
    public static void SendHotkey(VirtualKeyShort modifier1, VirtualKeyShort modifier2, VirtualKeyShort key)
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
    /// Returns the smallest window, which is typically a mini or fixed window.
    /// </summary>
    public static Window? FindSecondaryWindow(
        Application application,
        AutomationBase automation,
        string windowType,
        ITestOutputHelper output)
    {
        var allWindows = application.GetAllTopLevelWindows(automation);
        output.WriteLine($"Found {allWindows.Length} top-level window(s)");

        foreach (var w in allWindows)
        {
            var bounds = w.BoundingRectangle;
            output.WriteLine(
                $"  Window: \"{SafeName(w)}\" automationId=\"{SafeAutomationId(w)}\" " +
                $"offscreen={SafeIsOffscreen(w)} bounds={bounds}");
        }

        if (allWindows.Length <= 1)
        {
            output.WriteLine($"{windowType} window did not open - only main window found");
            return null;
        }

        var candidates = allWindows
            .Where(IsUsableTopLevelWindow)
            .Select(w => new
            {
                Window = w,
                Area = GetWindowArea(w),
                Score = ScoreSecondaryWindow(w, windowType)
            })
            .ToList();

        var mainWindow = candidates
            .OrderByDescending(candidate => candidate.Area)
            .FirstOrDefault()?.Window;

        var matched = candidates
            .Where(candidate => !ReferenceEquals(candidate.Window, mainWindow))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Area)
            .FirstOrDefault();

        if (matched != null && matched.Score > 0)
        {
            output.WriteLine(
                $"Selected {windowType} window by score={matched.Score}: " +
                $"\"{SafeName(matched.Window)}\" bounds={matched.Window.BoundingRectangle}");
            return matched.Window;
        }

        // Fallback to the smallest visible non-main top-level window, preserving
        // the old behavior when UIA metadata is sparse.
        var fallback = candidates
            .Where(candidate => !ReferenceEquals(candidate.Window, mainWindow))
            .OrderBy(candidate => candidate.Area)
            .FirstOrDefault();

        if (fallback != null)
        {
            output.WriteLine(
                $"Selected {windowType} window by fallback smallest area: " +
                $"\"{SafeName(fallback.Window)}\" bounds={fallback.Window.BoundingRectangle}");
            return fallback.Window;
        }

        output.WriteLine($"{windowType} window did not open - no usable secondary window found");
        return null;
    }

    public static AutomationElement? FindByAutomationIdOrName(Window window, string name)
    {
        try
        {
            return window.FindFirstDescendant(cf => cf.ByAutomationId(name))
                ?? window.FindFirstDescendant(cf => cf.ByName(name));
        }
        catch (Exception ex) when (ex is PropertyNotSupportedException or TimeoutException or COMException)
        {
            return null;
        }
    }

    public static AutomationElement? WaitForSettingsButton(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => FindSettingsButton(window),
            timeout).Result;
    }

    public static AutomationElement? FindSettingsButton(Window window)
    {
        return FindByAutomationIdOrName(window, "SettingsButton")
            ?? FindByAutomationIdOrName(window, "Settings")
            ?? FindTopRightLikelySettingsButton(window);
    }

    public static void ClickElement(AutomationElement element)
    {
        try
        {
            var invokePattern = element.Patterns.Invoke.PatternOrDefault;
            if (invokePattern != null)
            {
                invokePattern.Invoke();
                return;
            }
        }
        catch
        {
            // Fall back to a physical click below.
        }

        element.Click();
    }

    private static int ScoreSecondaryWindow(Window window, string windowType)
    {
        var score = 0;
        var title = SafeName(window);
        if (title.Contains(windowType, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (title.Contains("Easydict", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        if (FindByAutomationIdOrName(window, "InputTextBox") != null)
        {
            score += 50;
        }

        if (FindByAutomationIdOrName(window, "CloseButton") != null)
        {
            score += 10;
        }

        if (FindByAutomationIdOrName(window, "SettingsButton") != null)
        {
            score -= 25;
        }

        return score;
    }

    private static bool IsUsableTopLevelWindow(Window window)
    {
        var bounds = window.BoundingRectangle;
        return bounds.Width >= 80 &&
               bounds.Height >= 80 &&
               IsOnScreenOrUnknown(window);
    }

    private static int GetWindowArea(Window window)
    {
        var bounds = window.BoundingRectangle;
        return Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
    }

    private static bool IsOnScreenOrUnknown(AutomationElement element)
    {
        try
        {
            return !element.IsOffscreen;
        }
        catch (PropertyNotSupportedException)
        {
            return true;
        }
    }

    private static AutomationElement? FindTopRightLikelySettingsButton(Window window)
    {
        var bounds = window.BoundingRectangle;
        var headerTopLimit = bounds.Top + 220;
        var rightLimit = bounds.Right - 160;

        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        return buttons
            .Where(button =>
                IsOnScreenOrUnknown(button) &&
                button.BoundingRectangle.Top <= headerTopLimit &&
                button.BoundingRectangle.Right >= rightLimit &&
                button.BoundingRectangle.Width <= 70 &&
                button.BoundingRectangle.Height <= 70)
            .OrderByDescending(button => button.BoundingRectangle.Right)
            .FirstOrDefault();
    }

    /// <summary>
    /// Find the InputTextBox on a window, expanding the collapsed source-text container
    /// first if present. Since <see cref="MiniWindow.ShowAndActivate"/> now calls
    /// <c>SetSourceTextState(true)</c>, the <c>InputTextBox</c> is visible the moment the
    /// window appears and the preflight click is a harmless no-op in normal usage. It is
    /// kept as a fallback for any path that leaves the container collapsed.
    /// MainPage and FixedWindow expose InputTextBox directly.
    /// </summary>
    public static TextBox? FindInputTextBox(Window window, TimeSpan? timeout = null)
    {
        var inputBox = window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox()
            ?? window.FindFirstDescendant(cf => cf.ByName("InputTextBox"))?.AsTextBox();
        if (inputBox == null || inputBox.IsOffscreen)
        {
            var collapsed = window.FindFirstDescendant(cf => cf.ByAutomationId("SourceTextCollapsed"));
            if (collapsed != null)
            {
                try { Mouse.Click(collapsed.GetClickablePoint()); } catch { /* ignore */ }
                Thread.Sleep(300);
            }
        }

        return Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox()
                ?? window.FindFirstDescendant(cf => cf.ByName("InputTextBox"))?.AsTextBox(),
            timeout ?? TimeSpan.FromSeconds(10)).Result;
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

    private static string SafeIsOffscreen(AutomationElement element)
    {
        try
        {
            return element.IsOffscreen.ToString();
        }
        catch (PropertyNotSupportedException)
        {
            return "unknown";
        }
    }
}
