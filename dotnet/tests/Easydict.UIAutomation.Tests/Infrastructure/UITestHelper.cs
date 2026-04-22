using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
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
            output.WriteLine($"  Window: \"{w.Title}\" size={w.BoundingRectangle.Width}x{w.BoundingRectangle.Height}");
        }

        if (allWindows.Length <= 1)
        {
            output.WriteLine($"{windowType} window did not open - only main window found");
            return null;
        }

        // Return the smallest window (mini/fixed are smaller than main)
        return allWindows
            .OrderBy(w => w.BoundingRectangle.Width * w.BoundingRectangle.Height)
            .First();
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
        var inputBox = window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox();
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
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox(),
            timeout ?? TimeSpan.FromSeconds(10)).Result;
    }
}
