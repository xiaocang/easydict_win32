using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Easydict.WinUI.Services;

/// <summary>
/// Service to get selected text from any application using UI Automation API.
/// This avoids sending Ctrl+C which can trigger SIGINT in terminal applications.
/// </summary>
public static class TextSelectionService
{
    // PInvoke declarations
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    // INPUT struct must be 40 bytes on 64-bit Windows
    // The union must be at offset 8 for proper alignment
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public InputUnion U;
    }

    // Union must be 32 bytes (size of MOUSEINPUT, the largest member)
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;

    // Known Electron app process names
    private static readonly HashSet<string> ElectronProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "code - insiders",  // VSCode
        "slack", "discord", "teams",
        "notion", "obsidian", "postman",
        "figma", "spotify", "whatsapp",
        "signal", "telegram desktop",
    };

    // Known terminal app process names - Ctrl+C sends SIGINT in these apps
    private static readonly HashSet<string> TerminalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal", "cmd", "powershell", "pwsh",  // Windows built-in
        "conhost",  // Console Host
        "mintty",  // Git Bash, Cygwin, MSYS2
        "alacritty", "wezterm", "hyper", "terminus",  // Third-party terminals
        "wsl", "wslhost",  // WSL
    };

    private static readonly UIA3Automation _automation = new();
    private static readonly object _automationLock = new();

    /// <summary>
    /// Gets the currently selected text using UI Automation API.
    /// Strategy by app type:
    /// - Electron apps: Clipboard first (UIA doesn't support TextPattern)
    /// - Terminal apps: UIA only (never send Ctrl+C; skip clipboard fallback)
    /// - Modern UI apps: UIA first, clipboard fallback with ClipWait
    /// - Regular desktop apps: UIA first, clipboard fallback with ClipWait
    /// Clipboard fallback uses ClipWait (30ms polling + 450ms timeout) for reliability.
    /// Returns null if no text is selected or if all methods fail.
    /// </summary>
    public static async Task<string?> GetSelectedTextAsync()
    {
        // Log process name for diagnostics
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd != IntPtr.Zero && GetWindowThreadProcessId(hWnd, out uint processId) != 0)
            {
                using var process = Process.GetProcessById((int)processId);
                Debug.WriteLine($"[TextSelectionService] Target app: {process.ProcessName}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextSelectionService] Failed to get process name: {ex.Message}");
        }

        // For Electron apps, use clipboard method first since UIA doesn't work reliably
        if (IsElectronApp())
        {
            Debug.WriteLine("[TextSelectionService] Detected Electron app, using clipboard method");
            var clipboardText = await GetSelectedTextViaClipboardAsync();
            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                Debug.WriteLine($"[TextSelectionService] Got {clipboardText.Length} chars via clipboard");
                return clipboardText;
            }
        }

        // Use UIA for non-Electron apps (or as fallback if clipboard failed for Electron)
        string? uiaText = null;
        await Task.Run(() =>
        {
            try
            {
                // Lock to ensure thread-safe access to shared _automation instance
                lock (_automationLock)
                {
                    uiaText = GetSelectedTextViaUIA();
                    if (!string.IsNullOrWhiteSpace(uiaText))
                    {
                        Debug.WriteLine($"[TextSelectionService] Got {uiaText.Length} chars via UIA");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TextSelectionService] UIA failed: {ex.Message}");
            }
        });

        if (!string.IsNullOrWhiteSpace(uiaText))
        {
            return uiaText;
        }

        // UIA failed or no selection - fallback to clipboard method (Ctrl+C)
        // This is required for modern UI apps (UWP, WinUI, etc.) that don't support TextPattern
        // BUT: Do NOT use clipboard method for terminal apps (Ctrl+C = SIGINT)
        if (IsTerminalApp())
        {
            Debug.WriteLine("[TextSelectionService] Terminal app detected, skipping clipboard fallback to avoid SIGINT");
            return null;
        }

        Debug.WriteLine("[TextSelectionService] UIA returned no text, falling back to clipboard method");
        var fallbackText = await GetSelectedTextViaClipboardAsync();
        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            Debug.WriteLine($"[TextSelectionService] Got {fallbackText.Length} chars via clipboard fallback");
            return fallbackText;
        }

        return null;
    }

    private static string? GetSelectedTextViaUIA()
    {
        try
        {
            var focused = _automation.FocusedElement();
            if (focused == null)
            {
                Debug.WriteLine("[TextSelectionService] No focused element");
                return null;
            }

            // Try to get text pattern from focused element
            var text = GetSelectionFromElement(focused);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            Debug.WriteLine("[TextSelectionService] No text pattern available or no selection");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextSelectionService] GetFocusedElement failed: {ex.Message}");
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
            Debug.WriteLine($"[TextSelectionService] TextPattern failed: {ex.Message}");
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
            Debug.WriteLine($"[TextSelectionService] TextPattern2 failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Checks if the foreground window belongs to an Electron app.
    /// </summary>
    private static bool IsElectronApp()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return false;

            if (GetWindowThreadProcessId(hWnd, out uint processId) == 0) return false;

            using var process = Process.GetProcessById((int)processId);
            return ElectronProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if the foreground window belongs to a terminal app.
    /// Ctrl+C sends SIGINT in terminal apps, so we should not use clipboard method.
    /// </summary>
    private static bool IsTerminalApp()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return false;

            if (GetWindowThreadProcessId(hWnd, out uint processId) == 0) return false;

            using var process = Process.GetProcessById((int)processId);
            return TerminalProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets selected text using clipboard method (Ctrl+C).
    /// Saves and restores original clipboard content.
    /// </summary>
    private static async Task<string?> GetSelectedTextViaClipboardAsync()
    {
        const int pollIntervalMs = 30;
        const int timeoutMs = 450; // Hard timeout to prevent indefinite blocking

        try
        {
            // Capture the foreground window FIRST before any UI operations
            var targetWindow = GetForegroundWindow();
            Debug.WriteLine($"[TextSelectionService] Target window handle: {targetWindow}");

            if (App.MainWindow == null)
            {
                Debug.WriteLine("[TextSelectionService] MainWindow not available");
                return null;
            }

            var dispatcherQueue = App.MainWindow.DispatcherQueue;
            if (dispatcherQueue == null)
            {
                Debug.WriteLine("[TextSelectionService] DispatcherQueue not available");
                return null;
            }

            // 1. Save current clipboard content
            string? originalClipboard = null;
            var saveResult = dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var dataPackage = Clipboard.GetContent();
                    if (dataPackage.Contains(StandardDataFormats.Text))
                    {
                        originalClipboard = dataPackage.GetTextAsync().AsTask().GetAwaiter().GetResult();
                        Debug.WriteLine($"[TextSelectionService] Original clipboard: '{originalClipboard?.Substring(0, Math.Min(50, originalClipboard?.Length ?? 0))}...'");
                    }
                    else
                    {
                        Debug.WriteLine("[TextSelectionService] Original clipboard has no text");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TextSelectionService] Failed to save clipboard: {ex.Message}");
                }
            });

            if (!saveResult)
            {
                Debug.WriteLine("[TextSelectionService] Failed to enqueue clipboard save operation");
                return null;
            }

            // Wait for clipboard save to complete
            await Task.Delay(30);

            // 2. Clear clipboard first to detect if copy actually happens
            dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    Clipboard.Clear();
                    Debug.WriteLine("[TextSelectionService] Clipboard cleared");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TextSelectionService] Failed to clear clipboard: {ex.Message}");
                }
            });
            await Task.Delay(30);

            // 3. Attach to target thread and send Ctrl+C
            if (targetWindow != IntPtr.Zero)
            {
                var targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
                var currentThreadId = GetCurrentThreadId();

                Debug.WriteLine($"[TextSelectionService] Current thread: {currentThreadId}, Target thread: {targetThreadId}");

                // Attach input threads
                bool attached = false;
                if (targetThreadId != currentThreadId && targetThreadId != 0)
                {
                    attached = AttachThreadInput(currentThreadId, targetThreadId, true);
                    Debug.WriteLine($"[TextSelectionService] AttachThreadInput result: {attached}");
                }

                var focusResult = SetForegroundWindow(targetWindow);
                Debug.WriteLine($"[TextSelectionService] SetForegroundWindow result: {focusResult}");
                await Task.Delay(50); // Wait for focus to settle

                SendCtrlC();

                // Use ClipWait instead of fixed delay - polls for clipboard readiness
                var clipboardReady = await WaitForClipboardTextAsync(timeoutMs, pollIntervalMs);
                if (!clipboardReady)
                {
                    Debug.WriteLine("[TextSelectionService] ClipWait failed, clipboard not ready");
                    // Detach input threads before returning
                    if (attached)
                    {
                        AttachThreadInput(currentThreadId, targetThreadId, false);
                    }
                    return null;
                }

                // Detach input threads
                if (attached)
                {
                    AttachThreadInput(currentThreadId, targetThreadId, false);
                }
            }
            else
            {
                SendCtrlC();

                // Use ClipWait instead of fixed delay - polls for clipboard readiness
                var clipboardReady = await WaitForClipboardTextAsync(timeoutMs, pollIntervalMs);
                if (!clipboardReady)
                {
                    Debug.WriteLine("[TextSelectionService] ClipWait failed, clipboard not ready");
                    return null;
                }
            }

            // 4. Read copied text from clipboard
            string? selectedText = null;
            var readResult = dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var dataPackage = Clipboard.GetContent();
                    if (dataPackage.Contains(StandardDataFormats.Text))
                    {
                        selectedText = dataPackage.GetTextAsync().AsTask().GetAwaiter().GetResult();
                        Debug.WriteLine($"[TextSelectionService] After SendCtrlC clipboard: '{selectedText?.Substring(0, Math.Min(50, selectedText?.Length ?? 0))}...'");
                    }
                    else
                    {
                        Debug.WriteLine("[TextSelectionService] After SendCtrlC clipboard has no text");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TextSelectionService] Failed to read clipboard: {ex.Message}");
                }
            });

            if (!readResult)
            {
                Debug.WriteLine("[TextSelectionService] Failed to enqueue clipboard read operation");
                return null;
            }

            // Wait for clipboard read to complete
            await Task.Delay(30);

            Debug.WriteLine($"[TextSelectionService] Clipboard changed: {originalClipboard != selectedText}");

            // 5. Restore original clipboard content
            // If original had text and it's different from what we copied, restore it
            // If original was empty (null) and copy succeeded, clear clipboard to restore empty state
            var shouldRestore = (originalClipboard != null && originalClipboard != selectedText) ||
                                (originalClipboard == null && selectedText != null);
            if (shouldRestore)
            {
                dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (originalClipboard != null)
                        {
                            var dataPackage = new DataPackage();
                            dataPackage.SetText(originalClipboard);
                            Clipboard.SetContent(dataPackage);
                        }
                        else
                        {
                            // Original clipboard was empty, restore to empty state
                            Clipboard.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[TextSelectionService] Failed to restore clipboard: {ex.Message}");
                    }
                });
            }

            return selectedText;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextSelectionService] Clipboard method failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Polls the clipboard for text availability after Ctrl+C with timeout.
    /// Uses GetClipboardSequenceNumber to efficiently detect clipboard changes.
    /// </summary>
    /// <param name="timeoutMs">Maximum wait time in milliseconds</param>
    /// <param name="pollIntervalMs">Polling interval in milliseconds</param>
    /// <returns>True if clipboard text is available, false on timeout</returns>
    private static async Task<bool> WaitForClipboardTextAsync(int timeoutMs, int pollIntervalMs)
    {
        var startTime = Environment.TickCount64;
        var initialSequence = GetClipboardSequenceNumber();

        while (Environment.TickCount64 - startTime < timeoutMs)
        {
            var currentSequence = GetClipboardSequenceNumber();

            // Clipboard changed - check if it has text
            if (currentSequence != initialSequence)
            {
                try
                {
                    var text = await ClipboardService.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Debug.WriteLine($"[TextSelectionService] ClipWait: Clipboard ready after {Environment.TickCount64 - startTime}ms");
                        return true;
                    }
                }
                catch
                {
                    // Continue polling if clipboard access fails
                }
            }

            await Task.Delay(pollIntervalMs);
        }

        Debug.WriteLine($"[TextSelectionService] ClipWait: Timed out after {timeoutMs}ms");
        return false;
    }

    /// <summary>
    /// Sends Ctrl+C keystroke to copy selected text using SendInput API.
    /// SendInput is the modern replacement for keybd_event and works reliably
    /// with modern applications including Electron apps like VSCode.
    /// </summary>
    private static void SendCtrlC()
    {
        Debug.WriteLine("[TextSelectionService] SendCtrlC() called");

        var inputs = new INPUT[4];

        // Ctrl down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VK_CONTROL;

        // C down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = VK_C;

        // C up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].U.ki.wVk = VK_C;
        inputs[2].U.ki.dwFlags = KEYEVENTF_KEYUP;

        // Ctrl up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].U.ki.wVk = VK_CONTROL;
        inputs[3].U.ki.dwFlags = KEYEVENTF_KEYUP;

        var inputSize = Marshal.SizeOf<INPUT>();
        Debug.WriteLine($"[TextSelectionService] INPUT struct size: {inputSize}");

        uint result = SendInput(4, inputs, inputSize);
        Debug.WriteLine($"[TextSelectionService] SendInput returned: {result} (expected 4)");

        if (result != 4)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"[TextSelectionService] SendInput error code: {error}");
        }
    }

}
