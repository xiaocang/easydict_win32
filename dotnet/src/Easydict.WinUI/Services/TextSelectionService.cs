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
    private static readonly SemaphoreSlim _automationSemaphore = new(1, 1);
    private const int UiaSemaphoreTimeoutMs = 200;
    private const int UiaExecutionTimeoutMs = 800;

    /// <summary>
    /// Runs a function on the dispatcher thread and awaits the result.
    /// Unlike TryEnqueue + Task.Delay, this guarantees the work completes before continuing.
    /// </summary>
    private static Task<T?> RunOnDispatcherAsync<T>(DispatcherQueue dispatcher, Func<T?> func)
    {
        var tcs = new TaskCompletionSource<T?>();
        if (!dispatcher.TryEnqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue on dispatcher"));
        }
        return tcs.Task;
    }

    /// <summary>
    /// Runs an action on the dispatcher thread and awaits completion.
    /// </summary>
    private static Task RunOnDispatcherAsync(DispatcherQueue dispatcher, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!dispatcher.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue on dispatcher"));
        }
        return tcs.Task;
    }

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
    public static async Task<string?> GetSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        // Identify the foreground process once to avoid redundant P/Invoke + Process.GetProcessById
        // calls. Previously, GetForegroundWindow was called 3+ times and Process.GetProcessById
        // 2-3 times per selection, each allocating process handles.
        string? processName = null;
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd != IntPtr.Zero && GetWindowThreadProcessId(hWnd, out uint processId) != 0)
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
                Debug.WriteLine($"[TextSelectionService] Target app: {processName}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextSelectionService] Failed to get process name: {ex.Message}");
        }

        var isElectron = IsElectronApp(processName);
        var isTerminal = IsTerminalApp(processName);

        // Track if we already tried clipboard for Electron to avoid double Ctrl+C
        bool clipboardAlreadyAttempted = false;

        // For Electron apps, use clipboard method first since UIA doesn't work reliably
        if (isElectron)
        {
            Debug.WriteLine("[TextSelectionService] Detected Electron app, using clipboard method");
            clipboardAlreadyAttempted = true;
            var clipboardText = await GetSelectedTextViaClipboardAsync(cancellationToken, isElectron);
            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                Debug.WriteLine($"[TextSelectionService] Got {clipboardText.Length} chars via clipboard");
                return clipboardText;
            }
        }

        // Use UIA for non-Electron apps (or as fallback if clipboard failed for Electron)
        // Use SemaphoreSlim + timeout to prevent UIA from hanging indefinitely on Chromium apps
        string? uiaText = null;
        bool semaphoreAcquired = false;
        try
        {
            semaphoreAcquired = await _automationSemaphore.WaitAsync(UiaSemaphoreTimeoutMs, cancellationToken);
            if (!semaphoreAcquired)
            {
                Debug.WriteLine("[TextSelectionService] UIA busy, skipping to clipboard fallback");
            }
            else
            {
                try
                {
                    var uiaTask = Task.Run(() => GetSelectedTextViaUIA(), cancellationToken);
                    if (await Task.WhenAny(uiaTask, Task.Delay(UiaExecutionTimeoutMs, cancellationToken)) == uiaTask)
                    {
                        uiaText = await uiaTask;
                        if (!string.IsNullOrWhiteSpace(uiaText))
                            Debug.WriteLine($"[TextSelectionService] Got {uiaText.Length} chars via UIA");
                    }
                    else
                    {
                        Debug.WriteLine("[TextSelectionService] UIA timed out, skipping to clipboard fallback");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TextSelectionService] UIA failed: {ex.Message}");
                }
            }
        }
        finally
        {
            if (semaphoreAcquired) _automationSemaphore.Release();
        }

        if (!string.IsNullOrWhiteSpace(uiaText))
        {
            return uiaText;
        }

        // UIA failed or no selection - fallback to clipboard method (Ctrl+C)
        // This is required for modern UI apps (UWP, WinUI, etc.) that don't support TextPattern
        // BUT: Do NOT use clipboard method for terminal apps (Ctrl+C = SIGINT)
        // AND: Skip if we already tried clipboard for Electron (avoid double Ctrl+C in one call)
        if (isTerminal)
        {
            Debug.WriteLine("[TextSelectionService] Terminal app detected, skipping clipboard fallback to avoid SIGINT");
            return null;
        }

        if (clipboardAlreadyAttempted)
        {
            Debug.WriteLine("[TextSelectionService] Clipboard already attempted, skipping fallback to avoid double Ctrl+C");
            return null;
        }

        Debug.WriteLine("[TextSelectionService] UIA returned no text, falling back to clipboard method");
        var fallbackText = await GetSelectedTextViaClipboardAsync(cancellationToken, false);
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
    /// Checks if the given process name belongs to an Electron app.
    /// </summary>
    private static bool IsElectronApp(string? processName)
        => processName != null && ElectronProcessNames.Contains(processName);

    /// <summary>
    /// Checks if the given process name belongs to a terminal app.
    /// Ctrl+C sends SIGINT in terminal apps, so we should not use clipboard method.
    /// </summary>
    private static bool IsTerminalApp(string? processName)
        => processName != null && TerminalProcessNames.Contains(processName);

    /// <summary>
    /// Gets selected text using clipboard method (Ctrl+C).
    /// Saves and restores original clipboard content.
    /// </summary>
    private static async Task<string?> GetSelectedTextViaClipboardAsync(CancellationToken cancellationToken = default, bool isElectronApp = false)
    {
        const int pollIntervalMs = 30;
        const int timeoutMsStandard = 450;
        const int timeoutMsElectron = 1200; // Electron apps need more time for clipboard propagation

        var timeoutMs = isElectronApp ? timeoutMsElectron : timeoutMsStandard;

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

            // 1. Save current clipboard content (awaitable — guarantees completion)
            string? originalClipboard = null;
            try
            {
                originalClipboard = await RunOnDispatcherAsync<string?>(dispatcherQueue, () =>
                {
                    var dataPackage = Clipboard.GetContent();
                    if (dataPackage.Contains(StandardDataFormats.Text))
                    {
                        var text = dataPackage.GetTextAsync().AsTask().GetAwaiter().GetResult();
                        Debug.WriteLine($"[TextSelectionService] Original clipboard: '{text?.Substring(0, Math.Min(50, text?.Length ?? 0))}...'");
                        return text;
                    }
                    Debug.WriteLine("[TextSelectionService] Original clipboard has no text");
                    return null;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TextSelectionService] Failed to save clipboard: {ex.Message}");
                return null;
            }

            // 2. Capture baseline clipboard sequence BEFORE any modifications
            var baselineSequence = GetClipboardSequenceNumber();
            Debug.WriteLine($"[TextSelectionService] Baseline clipboard sequence: {baselineSequence}");

            // 3. Attach to target thread and send Ctrl+C (with try/finally for guaranteed detach)
            uint currentThreadId = 0;
            uint targetThreadId = 0;
            bool attached = false;

            try
            {
                if (targetWindow != IntPtr.Zero)
                {
                    targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
                    currentThreadId = GetCurrentThreadId();

                    Debug.WriteLine($"[TextSelectionService] Current thread: {currentThreadId}, Target thread: {targetThreadId}");

                    // Attach input threads
                    if (targetThreadId != currentThreadId && targetThreadId != 0)
                    {
                        attached = AttachThreadInput(currentThreadId, targetThreadId, true);
                        Debug.WriteLine($"[TextSelectionService] AttachThreadInput result: {attached}");
                    }

                    var focusResult = SetForegroundWindow(targetWindow);
                    Debug.WriteLine($"[TextSelectionService] SetForegroundWindow result: {focusResult}");

                    // Verify foreground actually changed
                    var actualForeground = GetForegroundWindow();
                    if (actualForeground != targetWindow)
                    {
                        Debug.WriteLine($"[TextSelectionService] Focus verification failed: expected {targetWindow}, got {actualForeground}");
                        return null;
                    }

                    await Task.Delay(50, cancellationToken); // Wait for focus to settle
                }

                SendCtrlC();

                // Use ClipWait with baseline sequence - polls for clipboard readiness
                var clipboardReady = await WaitForClipboardTextAsync(dispatcherQueue, timeoutMs, pollIntervalMs, baselineSequence, cancellationToken);
                if (!clipboardReady)
                {
                    Debug.WriteLine($"[TextSelectionService] ClipWait failed after {timeoutMs}ms, clipboard not ready");
                    return null;
                }
            }
            finally
            {
                // CRITICAL: Always detach input threads to prevent target app freeze
                if (attached && currentThreadId != 0 && targetThreadId != 0)
                {
                    var detached = AttachThreadInput(currentThreadId, targetThreadId, false);
                    Debug.WriteLine($"[TextSelectionService] AttachThreadInput detach result: {detached}");
                }
            }

            // 4. Read copied text from clipboard (awaitable — guarantees completion)
            string? selectedText = null;
            try
            {
                selectedText = await RunOnDispatcherAsync<string?>(dispatcherQueue, () =>
                {
                    var dataPackage = Clipboard.GetContent();
                    if (dataPackage.Contains(StandardDataFormats.Text))
                    {
                        var text = dataPackage.GetTextAsync().AsTask().GetAwaiter().GetResult();
                        Debug.WriteLine($"[TextSelectionService] After SendCtrlC clipboard: '{text?.Substring(0, Math.Min(50, text?.Length ?? 0))}...'");
                        return text;
                    }
                    Debug.WriteLine("[TextSelectionService] After SendCtrlC clipboard has no text");
                    return null;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TextSelectionService] Failed to read clipboard: {ex.Message}");
                return null;
            }

            Debug.WriteLine($"[TextSelectionService] Clipboard changed: {originalClipboard != selectedText}");

            // 5. Restore original clipboard content
            // If original had text and it's different from what we copied, restore it
            // If original was empty (null) and copy succeeded, clear clipboard to restore empty state
            var shouldRestore = (originalClipboard != null && originalClipboard != selectedText) ||
                                (originalClipboard == null && selectedText != null);
            if (shouldRestore)
            {
                // Fire-and-forget is acceptable for restore — it's non-critical
                _ = RunOnDispatcherAsync(dispatcherQueue, () =>
                {
                    if (originalClipboard != null)
                    {
                        var dataPackage = new DataPackage();
                        dataPackage.SetText(originalClipboard);
                        Clipboard.SetContent(dataPackage);
                    }
                    else
                    {
                        Clipboard.Clear();
                    }
                });
            }

            return selectedText;
        }
        catch (OperationCanceledException)
        {
            // Expected when user performs another action during clipboard wait
            Debug.WriteLine("[TextSelectionService] Clipboard method canceled by user action");
            throw; // Rethrow to match UIA path behavior
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextSelectionService] Clipboard method failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Polls the clipboard for text availability after Ctrl+C with timeout.
    /// Uses GetClipboardSequenceNumber to efficiently detect clipboard changes.
    /// Clipboard reads are marshalled to the UI thread via dispatcherQueue to
    /// ensure WinRT Clipboard APIs are called from the correct thread.
    /// </summary>
    private static async Task<bool> WaitForClipboardTextAsync(DispatcherQueue dispatcherQueue, int timeoutMs, int pollIntervalMs, uint baselineSequence, CancellationToken cancellationToken = default)
    {
        var startTime = Environment.TickCount64;

        while (Environment.TickCount64 - startTime < timeoutMs)
        {
            var currentSequence = GetClipboardSequenceNumber();

            // Clipboard changed from baseline - check if it has text
            if (currentSequence != baselineSequence)
            {
                Debug.WriteLine($"[TextSelectionService] ClipWait: Sequence changed from {baselineSequence} to {currentSequence}");
                try
                {
                    var text = await RunOnDispatcherAsync<string?>(dispatcherQueue, () =>
                    {
                        var content = Clipboard.GetContent();
                        if (content.Contains(StandardDataFormats.Text))
                            return content.GetTextAsync().AsTask().GetAwaiter().GetResult();
                        return null;
                    });

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Debug.WriteLine($"[TextSelectionService] ClipWait: Clipboard ready after {Environment.TickCount64 - startTime}ms with {text.Length} chars");
                        return true;
                    }
                    else
                    {
                        Debug.WriteLine("[TextSelectionService] ClipWait: Sequence changed but no text found, continuing...");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TextSelectionService] ClipWait: Clipboard read failed: {ex.Message}");
                    // Continue polling if clipboard access fails
                }
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }

        Debug.WriteLine($"[TextSelectionService] ClipWait: Timed out after {timeoutMs}ms (final sequence: {GetClipboardSequenceNumber()}, baseline: {baselineSequence})");
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
        inputs[0].U.ki.dwExtraInfo = MouseHookService.EASYDICT_SYNTHETIC_KEY;

        // C down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = VK_C;
        inputs[1].U.ki.dwExtraInfo = MouseHookService.EASYDICT_SYNTHETIC_KEY;

        // C up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].U.ki.wVk = VK_C;
        inputs[2].U.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[2].U.ki.dwExtraInfo = MouseHookService.EASYDICT_SYNTHETIC_KEY;

        // Ctrl up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].U.ki.wVk = VK_CONTROL;
        inputs[3].U.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].U.ki.dwExtraInfo = MouseHookService.EASYDICT_SYNTHETIC_KEY;

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
