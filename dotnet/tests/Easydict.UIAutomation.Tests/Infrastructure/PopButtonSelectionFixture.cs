using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using System.Diagnostics;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Shared fixture for PopButtonSelectionTests. Launches Easydict and Notepad once,
/// enables MouseSelectionTranslate, and shares them across all test methods.
/// </summary>
public sealed class PopButtonSelectionFixture : IDisposable
{
    public AppLauncher Launcher { get; }
    public NotepadTestTarget? Notepad { get; }
    public uint EasydictProcessId { get; }
    public bool SettingEnabled { get; }

    /// <summary>
    /// Diagnostic messages collected during fixture setup.
    /// Tests can dump these via ITestOutputHelper for troubleshooting.
    /// </summary>
    public List<string> SetupLog { get; } = new();

    public PopButtonSelectionFixture()
    {
        // 1. Launch Easydict
        Launcher = new AppLauncher();
        Launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        EasydictProcessId = (uint)Launcher.Application.ProcessId;
        Log($"Easydict launched, PID={EasydictProcessId}");

        // 2. Enable MouseSelectionTranslate in Settings
        SettingEnabled = TryEnableMouseSelectionTranslate();

        if (!SettingEnabled)
        {
            Log("WARNING: Could not enable MouseSelectionTranslate setting. " +
                "Selection tests will verify infrastructure but popup may not appear.");
        }

        // 3. Launch Notepad with known text
        try
        {
            Notepad = new NotepadTestTarget("Hello World test selection text for Easydict popup verification");
            Notepad.BringToForeground();
            Log("Notepad launched with test text");
        }
        catch (Exception ex)
        {
            Log($"WARNING: Failed to launch Notepad: {ex.Message}");
        }
    }

    /// <summary>
    /// Navigate to Settings, scroll to the Behavior section using percentage-based
    /// scrolling, and enable the MouseSelectionTranslate toggle.
    /// </summary>
    private bool TryEnableMouseSelectionTranslate()
    {
        try
        {
            var window = Launcher.GetMainWindow();
            Thread.Sleep(2000);

            // Navigate to Settings page via the SettingsButton (AutomationId)
            var settingsButton = Retry.WhileNull(
                () => window.FindFirstDescendant(c => c.ByAutomationId("SettingsButton")),
                TimeSpan.FromSeconds(10)).Result;

            if (settingsButton == null)
            {
                Log("SettingsButton not found by AutomationId");
                return false;
            }

            settingsButton.Click();
            Log("Clicked SettingsButton, waiting for settings page...");
            Thread.Sleep(2000);

            ScreenshotHelper.CaptureWindow(window, "e2e_settings_before_scroll");

            // Find the MainScrollViewer
            var scrollViewer = Retry.WhileNull(
                () => window.FindFirstDescendant(c => c.ByAutomationId("MainScrollViewer")),
                TimeSpan.FromSeconds(5)).Result;

            if (scrollViewer == null)
            {
                Log("MainScrollViewer not found — cannot scroll to Behavior section");
                return false;
            }

            // Scroll to ~70% where the Behavior section is, then scan to find the toggle
            var toggle = ScrollHelper.ScrollToFind(
                scrollViewer, startPercent: 70,
                () => window.FindFirstDescendant(c => c.ByAutomationId("MouseSelectionTranslateToggle"))
                   ?? window.FindFirstDescendant(c => c.ByName("Mouse selection translate")),
                Log);

            if (toggle != null)
            {
                var toggleButton = toggle.AsToggleButton();
                if (toggleButton != null &&
                    toggleButton.ToggleState == FlaUI.Core.Definitions.ToggleState.Off)
                {
                    toggleButton.Toggle();
                    Log("MouseSelectionTranslate toggle enabled (was Off → On)");
                    Thread.Sleep(500);
                }
                else if (toggleButton != null)
                {
                    Log($"MouseSelectionTranslate toggle already On (state={toggleButton.ToggleState})");
                }
                else
                {
                    Log("Element found but could not be used as ToggleButton");
                    return false;
                }

                ScreenshotHelper.CaptureWindow(window, "e2e_settings_toggle_enabled");
                return true;
            }

            Log("MouseSelectionTranslate toggle not found after scrolling");
            ScreenshotHelper.CaptureWindow(window, "e2e_settings_toggle_not_found");
            return false;
        }
        catch (Exception ex)
        {
            Log($"Error enabling MouseSelectionTranslate: {ex.Message}");
            return false;
        }
    }

    private void Log(string message)
    {
        Debug.WriteLine($"[PopButtonSelectionFixture] {message}");
        SetupLog.Add(message);
    }

    public void Dispose()
    {
        Notepad?.Dispose();
        Launcher.Dispose();
    }
}
