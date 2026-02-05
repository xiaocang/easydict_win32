using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using System.Drawing;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Manages a Notepad instance as a controlled text selection target for E2E tests.
/// Launches Notepad, types known text, and provides the text area bounds for mouse simulation.
/// </summary>
public sealed class NotepadTestTarget : IDisposable
{
    private readonly Application _notepad = null!;
    private readonly UIA3Automation _automation;
    private bool _isDisposed;

    /// <summary>
    /// The text content typed into Notepad.
    /// </summary>
    public string TextContent { get; }

    public Application Application => _notepad;
    public UIA3Automation Automation => _automation;

    public NotepadTestTarget(string textContent)
    {
        TextContent = textContent;
        _automation = new UIA3Automation();

        try
        {
            _notepad = Application.Launch("notepad.exe");

            var window = _notepad.GetMainWindow(_automation, TimeSpan.FromSeconds(10));
            if (window == null)
                throw new InvalidOperationException("Notepad main window did not appear");

            var edit = FindEditElement(window);
            if (edit == null)
                throw new InvalidOperationException("Could not find Notepad text edit area");

            // Focus and type the test text
            edit.Focus();
            Thread.Sleep(300);
            Keyboard.Type(textContent);
            Thread.Sleep(300);
        }
        catch
        {
            // Constructor failed after launching resources — clean up to prevent
            // orphaned Notepad processes and leaked UIA automation handles.
            try { _notepad?.Kill(); } catch { /* Best-effort cleanup of Notepad process */ }
            _automation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Get the main Notepad window.
    /// </summary>
    public Window GetWindow()
    {
        return _notepad.GetMainWindow(_automation, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Get the bounding rectangle of the text edit area in screen coordinates.
    /// </summary>
    public Rectangle GetTextBounds()
    {
        var window = GetWindow();
        var edit = FindEditElement(window)
            ?? throw new InvalidOperationException("Could not find Notepad text edit area");
        return edit.BoundingRectangle;
    }

    /// <summary>
    /// Bring Notepad to the foreground and ensure it has focus.
    /// </summary>
    public void BringToForeground()
    {
        var window = GetWindow();
        window.SetForeground();
        Thread.Sleep(500); // Allow focus transition to complete
    }

    /// <summary>
    /// Find the text edit element in Notepad.
    /// Handles both classic Notepad (Edit control) and Windows 11 Notepad (Document control).
    /// </summary>
    private static AutomationElement? FindEditElement(Window window)
    {
        // Try Edit control type first (classic Notepad)
        var edit = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)),
            TimeSpan.FromSeconds(3)).Result;

        if (edit != null) return edit;

        // Fall back to Document control type (Windows 11 Notepad)
        return Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Document)),
            TimeSpan.FromSeconds(3)).Result;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _notepad.Close();
            // Give Notepad time to close; it may prompt "Save changes?" — kill if stuck
            if (!_notepad.HasExited)
            {
                Thread.Sleep(1000);
                if (!_notepad.HasExited)
                {
                    // Dismiss "Save changes" dialog by killing
                    _notepad.Kill();
                }
            }
        }
        catch
        {
            // Close() may throw if the process already exited or is inaccessible — fall back to Kill()
            try { _notepad.Kill(); } catch { /* Ignore: process may already be terminated */ }
        }

        _automation.Dispose();
    }
}
