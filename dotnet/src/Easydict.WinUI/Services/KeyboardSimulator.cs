using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services;

/// <summary>
/// Provides keyboard simulation functionality using Win32 SendInput API.
/// </summary>
public static class KeyboardSimulator
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
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

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;

    /// <summary>
    /// Simulates Ctrl+C to copy selected text.
    /// </summary>
    public static void SimulateCopy()
    {
        var inputs = new INPUT[4];

        // Ctrl down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_CONTROL;

        // C down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_C;

        // C up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = VK_C;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;

        // Ctrl up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].u.ki.wVk = VK_CONTROL;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;

        var result = SendInput(4, inputs, Marshal.SizeOf<INPUT>());
        if (result != 4)
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardSimulator] SendInput failed: only {result}/4 events sent, Error: {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// Copies selected text by simulating Ctrl+C and returns the copied text.
    /// Returns null if no text was selected or copy failed.
    /// </summary>
    public static async Task<string?> CopySelectedTextAsync()
    {
        // Save original clipboard content
        var originalText = await ClipboardService.GetTextAsync();
        System.Diagnostics.Debug.WriteLine($"[KeyboardSimulator] Original clipboard length: {originalText?.Length ?? 0}");

        // Simulate Ctrl+C
        SimulateCopy();

        // Wait for system to process the copy (100ms is usually sufficient)
        await Task.Delay(100);

        // Get new clipboard content
        var newText = await ClipboardService.GetTextAsync();
        System.Diagnostics.Debug.WriteLine($"[KeyboardSimulator] New clipboard length: {newText?.Length ?? 0}");

        // If clipboard content changed, return the new content
        if (newText != originalText && !string.IsNullOrWhiteSpace(newText))
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardSimulator] Successfully copied {newText.Length} chars");
            return newText;
        }

        System.Diagnostics.Debug.WriteLine("[KeyboardSimulator] No new text copied (clipboard unchanged or empty)");
        return null;
    }
}
