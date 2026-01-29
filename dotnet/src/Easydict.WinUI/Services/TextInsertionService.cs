using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace Easydict.WinUI.Services;

/// <summary>
/// Service to insert/replace text in the previously focused application.
/// Copies translated text to clipboard, switches to the source app, and sends Ctrl+V.
/// </summary>
public static class TextInsertionService
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public InputUnion U;
    }

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
    private const ushort VK_V = 0x56;

    /// <summary>
    /// The window handle of the application that was active before Easydict took focus.
    /// Set by hotkey handlers before showing translation windows.
    /// </summary>
    private static IntPtr _sourceWindowHandle;

    /// <summary>
    /// Records the currently focused window as the source window.
    /// Call this before showing any Easydict window.
    /// </summary>
    public static void CaptureSourceWindow()
    {
        _sourceWindowHandle = GetForegroundWindow();
        Debug.WriteLine($"[TextInsertionService] Captured source window: {_sourceWindowHandle}");
    }

    /// <summary>
    /// Gets whether a valid source window is available for text insertion.
    /// </summary>
    public static bool HasSourceWindow =>
        _sourceWindowHandle != IntPtr.Zero && IsWindow(_sourceWindowHandle);

    /// <summary>
    /// Inserts (replaces) text into the source application by:
    /// 1. Copying the translated text to clipboard
    /// 2. Switching to the source window
    /// 3. Sending Ctrl+V to paste
    /// </summary>
    public static async Task<bool> InsertTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        if (!HasSourceWindow)
        {
            Debug.WriteLine("[TextInsertionService] No valid source window");
            return false;
        }

        try
        {
            // 1. Copy translated text to clipboard
            var dispatcherQueue = App.MainWindow?.DispatcherQueue;
            if (dispatcherQueue == null)
                return false;

            var tcs = new TaskCompletionSource<bool>();
            var enqueued = dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // Intentionally overwrite clipboard without save/restore:
                    // the user expects the translated text to remain on the clipboard after insertion.
                    var dataPackage = new DataPackage();
                    dataPackage.SetText(text);
                    Clipboard.SetContent(dataPackage);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TextInsertionService] Failed to set clipboard: {ex.Message}");
                    tcs.TrySetResult(false);
                }
            });

            if (!enqueued)
                return false;

            // Wait for clipboard operation with timeout
            var clipboardSet = await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task
                && tcs.Task.Result;

            if (!clipboardSet)
                return false;

            // 2. Switch to source window
            var targetThreadId = GetWindowThreadProcessId(_sourceWindowHandle, out _);
            var currentThreadId = GetCurrentThreadId();

            bool attached = false;
            try
            {
                if (targetThreadId != currentThreadId && targetThreadId != 0)
                {
                    attached = AttachThreadInput(currentThreadId, targetThreadId, true);
                    Debug.WriteLine($"[TextInsertionService] AttachThreadInput: {attached}");
                }

                var focusResult = SetForegroundWindow(_sourceWindowHandle);
                Debug.WriteLine($"[TextInsertionService] SetForegroundWindow: {focusResult}");

                // Wait for focus to settle
                await Task.Delay(100);

                // Verify foreground window matches source window
                if (GetForegroundWindow() != _sourceWindowHandle)
                {
                    Debug.WriteLine("[TextInsertionService] Foreground window mismatch after SetForegroundWindow");
                    return false;
                }

                // 3. Send Ctrl+V
                if (!SendCtrlV())
                {
                    Debug.WriteLine("[TextInsertionService] SendCtrlV failed (partial send)");
                    return false;
                }
            }
            finally
            {
                // Always detach input threads
                if (attached)
                {
                    AttachThreadInput(currentThreadId, targetThreadId, false);
                }
            }

            Debug.WriteLine("[TextInsertionService] Text inserted successfully");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextInsertionService] InsertTextAsync failed: {ex.Message}");
            return false;
        }
    }

    private static bool SendCtrlV()
    {
        var inputs = new INPUT[4];

        // Ctrl down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VK_CONTROL;

        // V down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = VK_V;

        // V up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].U.ki.wVk = VK_V;
        inputs[2].U.ki.dwFlags = KEYEVENTF_KEYUP;

        // Ctrl up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].U.ki.wVk = VK_CONTROL;
        inputs[3].U.ki.dwFlags = KEYEVENTF_KEYUP;

        var inputSize = Marshal.SizeOf<INPUT>();
        uint result = SendInput(4, inputs, inputSize);
        Debug.WriteLine($"[TextInsertionService] SendInput Ctrl+V returned: {result}");
        return result == 4;
    }
}
