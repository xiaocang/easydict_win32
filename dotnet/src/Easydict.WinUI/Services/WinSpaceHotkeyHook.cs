using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL) dedicated to capturing the
/// <c>Win+Space</c> combination, which the standard <see cref="HotkeyService"/>
/// cannot register because Windows reserves it for the input-language switcher.
///
/// When Win+Space is pressed, the hook suppresses the keystroke (so the OS does
/// not switch keyboard layout), injects a masking Ctrl tap (so releasing the Win
/// key does not open the Start menu — the same "disguise" technique AutoHotkey
/// uses), and invokes the bound handler.
///
/// The hook callback runs on the thread that installs it (the UI thread, which
/// has a message pump), exactly like <see cref="MouseHookService"/>.
/// </summary>
public sealed partial class WinSpaceHotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint VK_SPACE = 0x20;
    private const ushort VK_CONTROL = 0x11;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // SetWindowsHookEx must use DllImport because LibraryImport source generators
    // do not support delegate (function pointer) parameters.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? lpModuleName);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // INPUT struct must be 40 bytes on 64-bit Windows; the union sits at offset 8.
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

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc; // prevent GC collection of delegate
    private bool _isDisposed;

    // True while a Win+Space key-down has been consumed and we are waiting for the
    // matching key-up; used to debounce auto-repeat and swallow the trailing key-up.
    private bool _spaceConsumed;

    private Action? _handler;

    // Masking action; overridable so unit tests can exercise ProcessKey without P/Invoke.
    private readonly Action _maskAction;

    public WinSpaceHotkeyHook(Action? maskAction = null)
    {
        _maskAction = maskAction ?? SendMaskKey;
    }

    /// <summary>
    /// Set the action fired when Win+Space is pressed. Pass <c>null</c> to clear it.
    /// </summary>
    public void SetHandler(Action? handler) => _handler = handler;

    /// <summary>True when the low-level keyboard hook is currently installed.</summary>
    public bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>
    /// Install the global low-level keyboard hook. Must be called on the UI thread
    /// (requires a message pump). Idempotent.
    /// </summary>
    public bool Install()
    {
        if (_hookId != IntPtr.Zero)
            return true;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        var moduleHandle = GetModuleHandle(curModule.ModuleName);

        _hookProc = HookCallback;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);

        if (_hookId == IntPtr.Zero)
        {
            _hookProc = null;
            Debug.WriteLine($"[WinSpaceHook] SetWindowsHookEx failed, error: {Marshal.GetLastWin32Error()}");
            return false;
        }

        Debug.WriteLine("[WinSpaceHook] Low-level keyboard hook installed");
        return true;
    }

    /// <summary>Uninstall the global keyboard hook.</summary>
    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _hookProc = null;
            _spaceConsumed = false;
            Debug.WriteLine("[WinSpaceHook] Low-level keyboard hook removed");
        }
    }

    private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ks = (KBDLLHOOKSTRUCT*)lParam;
            // Ignore our own injected (masking) keystrokes.
            if (ks->dwExtraInfo != MouseHookService.EASYDICT_SYNTHETIC_KEY)
            {
                bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                            || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                if (ProcessKey((int)wParam, ks->vkCode, winDown))
                {
                    return (IntPtr)1; // suppress
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Pure key-processing logic. Public for unit testing without installing a hook.
    /// Returns <c>true</c> when the event should be suppressed (swallowed).
    /// </summary>
    public bool ProcessKey(int wParam, uint vkCode, bool isWinDown)
    {
        if (vkCode != VK_SPACE)
            return false;

        bool isKeyDown = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isKeyUp = wParam is WM_KEYUP or WM_SYSKEYUP;

        if (isKeyDown)
        {
            if (!isWinDown)
                return false; // plain Space — let it through

            if (_spaceConsumed)
                return true; // auto-repeat while held — suppress without re-firing

            _spaceConsumed = true;
            _maskAction();      // disguise the Win key so the Start menu stays closed
            _handler?.Invoke();
            return true;
        }

        if (isKeyUp && _spaceConsumed)
        {
            _spaceConsumed = false;
            return true; // swallow the trailing key-up so no stray space is delivered
        }

        return false;
    }

    private void SendMaskKey()
    {
        var inputs = new INPUT[]
        {
            new()
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = VK_CONTROL, dwExtraInfo = MouseHookService.EASYDICT_SYNTHETIC_KEY }
                }
            },
            new()
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = MouseHookService.EASYDICT_SYNTHETIC_KEY }
                }
            },
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _handler = null;
        Uninstall();
    }
}
