using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Easydict.WinUI.Services;

/// <summary>
/// Service to get selected text from any application using UI Automation API.
/// This avoids sending Ctrl+C which can trigger SIGINT in terminal applications.
/// </summary>
public static class TextSelectionService
{
    private static readonly UIA3Automation _automation = new();

    /// <summary>
    /// Gets the currently selected text using UI Automation API.
    /// Returns null if no text is selected or if UIA fails (does NOT fall back to clipboard).
    /// </summary>
    public static Task<string?> GetSelectedTextAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var text = GetSelectedTextViaUIA();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    System.Diagnostics.Debug.WriteLine($"[TextSelectionService] Got {text.Length} chars via UIA");
                    return text;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TextSelectionService] UIA failed: {ex.Message}");
            }

            // UIA failed or no selection - return null, do NOT fall back to clipboard
            return null;
        });
    }

    private static string? GetSelectedTextViaUIA()
    {
        try
        {
            var focused = _automation.FocusedElement();
            if (focused == null)
            {
                System.Diagnostics.Debug.WriteLine("[TextSelectionService] No focused element");
                return null;
            }

            // Try to get text pattern from focused element
            var text = GetSelectionFromElement(focused);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            System.Diagnostics.Debug.WriteLine("[TextSelectionService] No text pattern available or no selection");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextSelectionService] GetFocusedElement failed: {ex.Message}");
            return null;
        }
    }

    private static string? GetSelectionFromElement(AutomationElement element)
    {
        try
        {
            // Try TextPattern first
            if (element.Patterns.Text.IsSupported)
            {
                var textPattern = element.Patterns.Text.Pattern;
                var selection = textPattern.GetSelection();
                if (selection != null && selection.Length > 0)
                {
                    var selectedText = selection[0].GetText(-1);
                    if (!string.IsNullOrEmpty(selectedText))
                    {
                        return selectedText;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextSelectionService] TextPattern failed: {ex.Message}");
        }

        try
        {
            // Try TextPattern2 if TextPattern didn't work
            if (element.Patterns.Text2.IsSupported)
            {
                var textPattern2 = element.Patterns.Text2.Pattern;
                var selection = textPattern2.GetSelection();
                if (selection != null && selection.Length > 0)
                {
                    var selectedText = selection[0].GetText(-1);
                    if (!string.IsNullOrEmpty(selectedText))
                    {
                        return selectedText;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextSelectionService] TextPattern2 failed: {ex.Message}");
        }

        return null;
    }
}
