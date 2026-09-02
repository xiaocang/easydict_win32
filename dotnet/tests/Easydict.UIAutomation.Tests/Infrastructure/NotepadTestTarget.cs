using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using System.Diagnostics;
using System.Drawing;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Opens a uniquely named temporary text file in Notepad and provides the text area
/// bounds for mouse simulation. Window discovery supports Windows 11 Notepad's
/// launcher-process handoff as well as classic Notepad.
/// </summary>
public sealed class NotepadTestTarget : IDisposable
{
    private readonly Application _notepad = null!;
    private readonly UIA3Automation _automation;
    private bool _isDisposed;
    private readonly string _targetFilePath;

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
        _targetFilePath = Path.Combine(
            Path.GetTempPath(),
            $"easydict-selection-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_targetFilePath, textContent);

        try
        {
            var startInfo = new ProcessStartInfo("notepad.exe")
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(_targetFilePath);
            using var launcherProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Notepad");

            var targetFileName = Path.GetFileName(_targetFilePath);
            var window = Retry.WhileNull(
                () => _automation
                    .GetDesktop()
                    .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                    .Select(element => element.AsWindow())
                    .FirstOrDefault(candidate =>
                        candidate.Name.Contains(targetFileName, StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(10)).Result;
            if (window == null)
            {
                throw new InvalidOperationException(
                    $"Notepad window for '{targetFileName}' did not appear");
            }

            _notepad = Application.Attach(window.Properties.ProcessId.Value);
            var edit = FindEditElement(window)
                ?? throw new InvalidOperationException("Could not find Notepad text edit area");
            edit.Focus();
            Thread.Sleep(300);
        }
        catch
        {
            try { CloseTargetTab(); } catch { /* Best-effort cleanup of the test-owned tab only */ }
            _automation.Dispose();
            File.Delete(_targetFilePath);
            throw;
        }
    }

    /// <summary>
    /// Get the Notepad window hosting the test-owned tab.
    /// </summary>
    public Window GetWindow()
    {
        return _notepad.GetMainWindow(_automation, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Get the text area bounds in screen coordinates.
    /// </summary>
    public Rectangle GetTextBounds()
    {
        var window = GetWindow();
        var edit = FindEditElement(window)
            ?? throw new InvalidOperationException("Could not find Notepad text edit area");
        var bounds = edit.BoundingRectangle;
        if (edit.ControlType == ControlType.Document)
        {
            // Windows 11 Notepad's Document bounds include a blank toolbar/content inset.
            // Shift the gesture origin onto the first rendered line of text.
            var topInset = Math.Min(32, Math.Max(0, bounds.Height - 1));
            return Rectangle.FromLTRB(bounds.Left, bounds.Top + topInset, bounds.Right, bounds.Bottom);
        }

        return bounds;
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
            CloseTargetTab();
        }
        catch
        {
            // The user may have closed the test tab already.
        }
        finally
        {
            _automation.Dispose();
            File.Delete(_targetFilePath);
        }
    }

    private void CloseTargetTab()
    {
        var window = _notepad.GetMainWindow(_automation, TimeSpan.FromSeconds(2));
        var targetFileName = Path.GetFileName(_targetFilePath);
        var targetTab = window
            .FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
            .FirstOrDefault(candidate =>
                candidate.Name.Contains(targetFileName, StringComparison.OrdinalIgnoreCase));

        if (targetTab != null)
        {
            targetTab.Click();
        }
        else if (!window.Name.Contains(targetFileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        window.SetForeground();
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(
            FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
            FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_W);
        Thread.Sleep(500);
    }
}
