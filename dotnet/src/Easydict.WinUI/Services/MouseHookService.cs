using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services;

/// <summary>
/// Global low-level mouse hook (WH_MOUSE_LL) to detect text selection gestures.
/// Detects drag-select (mouse down → drag → mouse up) and fires events for
/// the PopButtonService to show the floating translate icon.
/// </summary>
public sealed class MouseHookService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_RBUTTONDOWN = 0x0204;

    /// <summary>
    /// Minimum drag distance in pixels to consider a mouse gesture as text selection.
    /// Prevents short clicks from being misidentified as drags.
    /// </summary>
    internal const int MinDragDistance = 10;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

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
    internal struct POINT
    {
        public int x;
        public int y;
    }

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelMouseProc? _hookProc; // prevent GC collection of delegate
    private bool _isDisposed;

    // Drag detection state - exposed internally for testing
    internal DragDetector Detector { get; } = new();

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
    /// Install the global low-level mouse hook.
    /// Must be called from the UI thread (requires a message pump).
    /// </summary>
    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        _hookProc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(curModule.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"[MouseHook] SetWindowsHookEx failed, error: {error}");
        }
        else
        {
            Debug.WriteLine("[MouseHook] Low-level mouse hook installed");
        }
    }

    /// <summary>
    /// Uninstall the global mouse hook.
    /// </summary>
    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _hookProc = null;
            Debug.WriteLine("[MouseHook] Low-level mouse hook removed");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            ProcessMouseMessage((int)wParam, hookStruct.pt);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Process a mouse message. Separated from HookCallback for testability.
    /// </summary>
    internal void ProcessMouseMessage(int message, POINT pt)
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
    internal sealed class DragDetector
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

    internal readonly record struct DragResult(bool IsDragSelection, POINT EndPoint);
}
