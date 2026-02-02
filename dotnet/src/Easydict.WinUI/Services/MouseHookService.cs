using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services;

/// <summary>
/// Global low-level mouse hook (WH_MOUSE_LL) to detect text selection gestures.
/// Detects drag-select (mouse down → drag → mouse up) and fires events for
/// the PopButtonService to show the floating translate icon.
/// </summary>
public sealed partial class MouseHookService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    /// <summary>
    /// Minimum drag distance in pixels to consider a mouse gesture as text selection.
    /// Prevents short clicks from being misidentified as drags.
    /// </summary>
    public const int MinDragDistance = 10;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    private IntPtr _mouseHookId = IntPtr.Zero;
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private LowLevelMouseProc? _mouseHookProc; // prevent GC collection of delegate
    private LowLevelKeyboardProc? _keyboardHookProc;
    private bool _isDisposed;

    /// <summary>
    /// Drag detection state machine. Public for unit testing.
    /// </summary>
    public DragDetector Detector { get; } = new();

    /// <summary>
    /// Fired when a drag-select gesture ends (mouse up after dragging).
    /// Parameter is the screen coordinate of the mouse release point.
    /// </summary>
    public event Action<POINT>? OnDragSelectionEnd;

    /// <summary>
    /// Fired on any left mouse button down (used to dismiss the pop button).
    /// </summary>
    public event Action? OnMouseDown;

    /// <summary>
    /// Fired on mouse scroll (used to dismiss the pop button).
    /// </summary>
    public event Action? OnMouseScroll;

    /// <summary>
    /// Fired on right mouse button down (used to dismiss the pop button).
    /// </summary>
    public event Action? OnRightMouseDown;

    /// <summary>
    /// Fired on any key press (used to dismiss the pop button).
    /// </summary>
    public event Action? OnKeyDown;

    /// <summary>
    /// Install the global low-level mouse and keyboard hooks.
    /// Must be called from the UI thread (requires a message pump).
    /// Returns true if both hooks were installed successfully.
    /// </summary>
    public bool Install()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        var moduleHandle = GetModuleHandle(curModule.ModuleName);

        if (_mouseHookId == IntPtr.Zero)
        {
            _mouseHookProc = MouseHookCallback;
            _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, moduleHandle, 0);

            if (_mouseHookId == IntPtr.Zero)
                Debug.WriteLine($"[MouseHook] SetWindowsHookEx(MOUSE_LL) failed, error: {Marshal.GetLastWin32Error()}");
            else
                Debug.WriteLine("[MouseHook] Low-level mouse hook installed");
        }

        if (_keyboardHookId == IntPtr.Zero)
        {
            _keyboardHookProc = KeyboardHookCallback;
            _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, moduleHandle, 0);

            if (_keyboardHookId == IntPtr.Zero)
                Debug.WriteLine($"[MouseHook] SetWindowsHookEx(KEYBOARD_LL) failed, error: {Marshal.GetLastWin32Error()}");
            else
                Debug.WriteLine("[MouseHook] Low-level keyboard hook installed");
        }

        return _mouseHookId != IntPtr.Zero && _keyboardHookId != IntPtr.Zero;
    }

    /// <summary>
    /// Uninstall the global mouse and keyboard hooks.
    /// </summary>
    public void Uninstall()
    {
        if (_mouseHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
            _mouseHookProc = null;
            Debug.WriteLine("[MouseHook] Low-level mouse hook removed");
        }

        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
            _keyboardHookProc = null;
            Debug.WriteLine("[MouseHook] Low-level keyboard hook removed");
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            ProcessMouseMessage((int)wParam, hookStruct.pt);
        }
        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            ProcessKeyboardMessage((int)wParam);
        }
        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Process a mouse message. Public for unit testing without installing a real hook.
    /// </summary>
    public void ProcessMouseMessage(int message, POINT pt)
    {
        switch (message)
        {
            case WM_LBUTTONDOWN:
                // Notify pop button to dismiss (user clicked somewhere)
                OnMouseDown?.Invoke();
                Detector.OnLeftButtonDown(pt);
                break;

            case WM_MOUSEMOVE:
                Detector.OnMouseMove(pt);
                break;

            case WM_LBUTTONUP:
                var result = Detector.OnLeftButtonUp(pt);
                if (result.IsDragSelection)
                {
                    Debug.WriteLine($"[MouseHook] Drag selection detected at ({pt.x}, {pt.y})");
                    OnDragSelectionEnd?.Invoke(pt);
                }
                break;

            case WM_MOUSEWHEEL:
                OnMouseScroll?.Invoke();
                break;

            case WM_RBUTTONDOWN:
                OnRightMouseDown?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Process a keyboard message. Public for unit testing without installing a real hook.
    /// </summary>
    public void ProcessKeyboardMessage(int message)
    {
        if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
        {
            OnKeyDown?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Uninstall();
    }

    /// <summary>
    /// Encapsulates drag detection state machine logic.
    /// Separated from MouseHookService for unit testing without actual Win32 hooks.
    /// </summary>
    public sealed class DragDetector
    {
        private POINT _startPoint;
        private bool _isLeftButtonDown;
        private bool _isDragging;

        public bool IsLeftButtonDown => _isLeftButtonDown;
        public bool IsDragging => _isDragging;

        public void OnLeftButtonDown(POINT pt)
        {
            _startPoint = pt;
            _isLeftButtonDown = true;
            _isDragging = false;
        }

        public void OnMouseMove(POINT pt)
        {
            if (!_isLeftButtonDown) return;

            if (!_isDragging)
            {
                var dx = pt.x - _startPoint.x;
                var dy = pt.y - _startPoint.y;
                var distanceSq = dx * dx + dy * dy;
                if (distanceSq >= MinDragDistance * MinDragDistance)
                {
                    _isDragging = true;
                }
            }
        }

        public DragResult OnLeftButtonUp(POINT pt)
        {
            var wasDragging = _isDragging;
            _isLeftButtonDown = false;
            _isDragging = false;
            return new DragResult(wasDragging, pt);
        }

        public void Reset()
        {
            _isLeftButtonDown = false;
            _isDragging = false;
        }
    }

    public readonly record struct DragResult(bool IsDragSelection, POINT EndPoint);
}
