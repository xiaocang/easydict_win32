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
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    /// <summary>
    /// Marker value set in dwExtraInfo of synthetic keyboard events sent by TextSelectionService.
    /// The keyboard hook skips firing OnKeyDown for events with this marker, preventing
    /// our own Ctrl+C from dismissing the pop button and cancelling the clipboard read.
    /// </summary>
    internal const nint EASYDICT_SYNTHETIC_KEY = 0x4541_5344; // "EASD"

    /// <summary>
    /// Minimum drag distance in pixels to consider a mouse gesture as text selection.
    /// Prevents short clicks from being misidentified as drags.
    /// </summary>
    public const int MinDragDistance = 10;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // SetWindowsHookEx must use DllImport because LibraryImport source generators
    // do not support delegate (function pointer) parameters.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

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
    private static partial IntPtr WindowFromPoint(POINT pt);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    private const uint GA_ROOT = 2; // Get root window

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
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
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
    private bool _firstCallbackLogged;

    /// <summary>
    /// Handles of our own popup windows (pop button, hover lookup popup). Left clicks that land
    /// on one of these windows do not raise <see cref="OnMouseDown"/>, so the popup is not
    /// dismissed before its own click handler runs. Only touched on the UI thread.
    /// </summary>
    private readonly List<IntPtr> _ownedWindowHandles = new(2);

    /// <summary>
    /// Cached system double-click time to avoid P/Invoke on every click.
    /// Refreshed on Install() since it rarely changes (only when user changes mouse settings).
    /// </summary>
    private uint _cachedDoubleClickTime;

    /// <summary>
    /// Upper bound for the multi-click settle wait, in milliseconds.
    /// The system double-click time (500ms by default) is how long Windows allows between
    /// clicks, but real double/triple clicks land far closer together. Waiting the full
    /// interval delayed the pop button by roughly a second; capping the wait keeps
    /// triple-click detection working while the icon appears promptly. A later third click
    /// simply dismisses the icon and restarts the timer with the wider selection.
    /// </summary>
    public const int MaxMultiClickWaitMs = 220;

    /// <summary>
    /// Time to wait after a multi-click before querying the selection.
    /// Long enough for an additional click (double -> triple), capped at
    /// <paramref name="capMs"/> (the user-configurable pop delay, default
    /// <see cref="MaxMultiClickWaitMs"/>). Internal for unit testing.
    /// </summary>
    internal static int GetMultiClickWaitMs(uint doubleClickTimeMs, int capMs = MaxMultiClickWaitMs)
    {
        var settleMs = (long)doubleClickTimeMs + 50;
        return (int)Math.Max(
            SettingsService.MinMouseSelectionPopDelayMs, Math.Min(settleMs, capMs));
    }

    /// <summary>
    /// Optional provider for the user-configured pop delay (Settings -> Behavior -> Advanced).
    /// Read on every multi-click so a changed setting takes effect without re-wiring.
    /// When unset, <see cref="MaxMultiClickWaitMs"/> applies.
    /// </summary>
    public Func<int>? MultiClickWaitCapProvider { get; set; }

    private int ResolveMultiClickWaitCapMs()
    {
        var provider = MultiClickWaitCapProvider;
        if (provider is null) return MaxMultiClickWaitMs;

        try { return provider(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MouseHook] Pop delay provider failed, using default: {ex.Message}");
            return MaxMultiClickWaitMs;
        }
    }

    /// <summary>
    /// Drag detection state machine. Public for unit testing.
    /// </summary>
    public DragDetector Detector { get; } = new();

    /// <summary>
    /// Multi-click (double/triple) detection. Public for unit testing.
    /// </summary>
    public MultiClickDetector ClickDetector { get; } = new();

    /// <summary>
    /// Set the PopButton window handle to prevent dismissing it when clicking on it.
    /// </summary>
    public void SetPopButtonWindowHandle(IntPtr hwnd)
    {
        AddOwnedWindowHandle(hwnd);
        Debug.WriteLine($"[MouseHook] PopButton window handle registered: 0x{hwnd:X}");
    }

    /// <summary>
    /// Register one of our own popup windows so that left clicks landing on it do not raise
    /// <see cref="OnMouseDown"/> (which would dismiss the popup before its click handler runs).
    /// </summary>
    public void AddOwnedWindowHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _ownedWindowHandles.Contains(hwnd)) return;
        _ownedWindowHandles.Add(hwnd);
        Debug.WriteLine($"[MouseHook] Owned window handle registered: 0x{hwnd:X}");
    }

    private bool IsOwnedWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && _ownedWindowHandles.Contains(hwnd);

    /// <summary>
    /// Whether the low-level mouse hook is currently installed.
    /// </summary>
    public bool IsInstalled => _mouseHookId != IntPtr.Zero;

    /// <summary>
    /// Optional callback to check if the current foreground app is excluded.
    /// When set and returns true, drag/multi-click selection events are suppressed.
    /// </summary>
    public Func<bool>? IsCurrentAppExcluded { get; set; }

    /// <summary>
    /// Fired when a drag-select gesture ends (mouse up after dragging).
    /// Parameter is the screen coordinate of the mouse release point.
    /// </summary>
    public event Action<POINT>? OnDragSelectionEnd;

    /// <summary>
    /// Fired when a double/triple-click selection settles. Parameters are the screen
    /// coordinate of the last click and how long the settle wait actually took.
    /// Separate from <see cref="OnDragSelectionEnd"/> because this path already waited
    /// out the multi-click settle window: listeners can query the selection sooner, and
    /// the elapsed wait lets them keep the user's pop delay as a whole-wait budget.
    /// </summary>
    public event Action<POINT, int>? OnMultiClickSelectionEnd;

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
    /// Fired on every mouse move with the current screen coordinate (physical pixels).
    /// Used by hover word lookup for dwell detection; handlers must be cheap.
    /// </summary>
    public event Action<POINT>? OnMouseMove;

    /// <summary>
    /// A low-level keyboard event with its virtual-key code.
    /// </summary>
    public readonly record struct KeyboardHookEvent(bool IsKeyDown, uint VkCode);

    /// <summary>
    /// Fired on every key down AND key up with the virtual-key code
    /// (used by hover word lookup to track the trigger modifier key).
    /// </summary>
    public event Action<KeyboardHookEvent>? OnKeyboardEvent;

    /// <summary>
    /// Install the global low-level mouse and keyboard hooks.
    /// Must be called from the UI thread (requires a message pump).
    /// Returns true if both hooks were installed successfully.
    /// </summary>
    public bool Install()
    {
        // Cache the system double-click time to avoid P/Invoke on every click.
        _cachedDoubleClickTime = GetDoubleClickTime();

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

    private unsafe IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                // Read the POINT directly from unmanaged memory to avoid the marshalling overhead
                // of Marshal.PtrToStructure<MSLLHOOKSTRUCT>, which copies the entire struct on every
                // call. Since this callback fires on every mouse message (including WM_MOUSEMOVE at
                // high frequency), the repeated marshalling adds measurable overhead to the hot path.
                var pt = ((MSLLHOOKSTRUCT*)lParam)->pt;
                ProcessMouseMessage((int)wParam, pt);
            }
        }
        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
        {
            CrashDiagnostics.LogException(
                "MouseHookService.MouseHookCallback",
                ex,
                isTerminating: false,
                isHandled: true);
        }

        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private unsafe IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var hookStruct = (KBDLLHOOKSTRUCT*)lParam;
                if (hookStruct->dwExtraInfo != EASYDICT_SYNTHETIC_KEY)
                {
                    ProcessKeyboardMessage((int)wParam, hookStruct->vkCode);
                }
            }
        }
        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
        {
            CrashDiagnostics.LogException(
                "MouseHookService.KeyboardHookCallback",
                ex,
                isTerminating: false,
                isHandled: true);
        }

        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Process a mouse message. Public for unit testing without installing a real hook.
    /// </summary>
    public void ProcessMouseMessage(int message, POINT pt)
    {
        if (!_firstCallbackLogged)
        {
            _firstCallbackLogged = true;
            Debug.WriteLine($"[MouseHook] First mouse callback received (msg=0x{message:X4})");
        }

        switch (message)
        {
            case WM_LBUTTONDOWN:
                // Only do the expensive WindowFromPoint + GetAncestor calls when one of our
                // popup windows is actually registered (i.e. has been shown at least once).
                // Before that, every click would pay two P/Invoke calls for nothing.
                if (_ownedWindowHandles.Count > 0)
                {
                    var windowAtPoint = WindowFromPoint(pt);
                    var rootWindow = windowAtPoint != IntPtr.Zero ? GetAncestor(windowAtPoint, GA_ROOT) : IntPtr.Zero;
                    bool isOwnedWindowClick = IsOwnedWindow(rootWindow) || IsOwnedWindow(windowAtPoint);

                    if (!isOwnedWindowClick)
                    {
                        Debug.WriteLine($"[MouseHook] Click on window 0x{windowAtPoint:X} (root=0x{rootWindow:X}), dismissing");
                        NativeCallbackGuard.Invoke("MouseHookService.OnMouseDown", OnMouseDown);
                    }
                    else
                    {
                        Debug.WriteLine($"[MouseHook] Click on owned popup window 0x{windowAtPoint:X} (root=0x{rootWindow:X}), not dismissing");
                    }
                }
                else
                {
                    NativeCallbackGuard.Invoke("MouseHookService.OnMouseDown", OnMouseDown);
                }
                Detector.OnLeftButtonDown(pt);
                break;

            case WM_MOUSEMOVE:
                Detector.OnMouseMove(pt);
                NativeCallbackGuard.Invoke("MouseHookService.OnMouseMove", OnMouseMove, pt);
                break;

            case WM_LBUTTONUP:
                var result = Detector.OnLeftButtonUp(pt);
                if (result.IsDragSelection)
                {
                    // Drag selection — cancel any pending multi-click
                    ClickDetector.Reset();
                    CancelMultiClickTimer();
                    if (!IsCurrentAppExcludedSafely())
                    {
                        Debug.WriteLine($"[MouseHook] Drag selection detected at ({pt.x}, {pt.y})");
                        NativeCallbackGuard.Invoke(
                            "MouseHookService.OnDragSelectionEnd",
                            OnDragSelectionEnd,
                            pt);
                    }
                }
                else
                {
                    // Non-drag click — check for multi-click (double/triple).
                    // Use cached double-click time; fall back to P/Invoke if Install() wasn't called
                    // (e.g. unit tests calling ProcessMouseMessage directly).
                    var dct = _cachedDoubleClickTime != 0 ? _cachedDoubleClickTime : GetDoubleClickTime();
                    var clickResult = ClickDetector.OnClick(pt, Environment.TickCount64, dct);
                    if (clickResult.ClickCount >= 2 && !IsCurrentAppExcludedSafely())
                    {
                        // Start/restart a short timer to allow for additional clicks
                        // (e.g. triple-click after double-click)
                        StartMultiClickTimer(pt, clickResult.ClickCount);
                    }
                }
                break;

            case WM_MOUSEWHEEL:
                NativeCallbackGuard.Invoke("MouseHookService.OnMouseScroll", OnMouseScroll);
                break;

            case WM_RBUTTONDOWN:
                NativeCallbackGuard.Invoke("MouseHookService.OnRightMouseDown", OnRightMouseDown);
                break;
        }
    }

    /// <summary>
    /// Process a keyboard message without a virtual-key code (legacy overload).
    /// Public for unit testing without installing a real hook.
    /// </summary>
    public void ProcessKeyboardMessage(int message) => ProcessKeyboardMessage(message, 0);

    /// <summary>
    /// Process a keyboard message. Public for unit testing without installing a real hook.
    /// </summary>
    public void ProcessKeyboardMessage(int message, uint vkCode)
    {
        if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
        {
            NativeCallbackGuard.Invoke("MouseHookService.OnKeyDown", OnKeyDown);
            NativeCallbackGuard.Invoke(
                "MouseHookService.OnKeyboardEvent",
                OnKeyboardEvent,
                new KeyboardHookEvent(true, vkCode));
        }
        else if (message == WM_KEYUP || message == WM_SYSKEYUP)
        {
            NativeCallbackGuard.Invoke(
                "MouseHookService.OnKeyboardEvent",
                OnKeyboardEvent,
                new KeyboardHookEvent(false, vkCode));
        }
    }

    private bool IsCurrentAppExcludedSafely()
    {
        var predicate = IsCurrentAppExcluded;
        return predicate is not null &&
            NativeCallbackGuard.Invoke(
                "MouseHookService.IsCurrentAppExcluded",
                predicate,
                fallback: true);
    }

    private CancellationTokenSource? _multiClickCts;

    private void StartMultiClickTimer(POINT pt, int clickCount)
    {
        CancelMultiClickTimer();

        var cts = new CancellationTokenSource();
        _multiClickCts = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait just long enough for an additional click (double → triple),
                // capped so the pop button is not held back by the full double-click time.
                var dctTimer = _cachedDoubleClickTime != 0 ? _cachedDoubleClickTime : GetDoubleClickTime();
                var settleWaitMs = GetMultiClickWaitMs(dctTimer, ResolveMultiClickWaitCapMs());
                await Task.Delay(settleWaitMs, ct);

                if (!ct.IsCancellationRequested && !IsCurrentAppExcludedSafely())
                {
                    Debug.WriteLine($"[MouseHook] Multi-click selection detected (clicks={clickCount}) at ({pt.x}, {pt.y})");
                    NativeCallbackGuard.Invoke(
                        "MouseHookService.OnMultiClickSelectionEnd",
                        OnMultiClickSelectionEnd,
                        pt,
                        settleWaitMs);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when another click arrives or a drag starts
            }
        });
    }

    private void CancelMultiClickTimer()
    {
        var cts = _multiClickCts;
        _multiClickCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CancelMultiClickTimer();
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

    /// <summary>
    /// Detects multi-click gestures (double-click, triple-click) by tracking
    /// consecutive non-drag clicks within the system double-click time and distance.
    /// WH_MOUSE_LL does not receive WM_LBUTTONDBLCLK, so we must detect it manually.
    /// </summary>
    public sealed class MultiClickDetector
    {
        /// <summary>
        /// Maximum distance in pixels between consecutive clicks to count as multi-click.
        /// Uses the same value as the system double-click distance (typically 4px).
        /// </summary>
        public const int MaxClickDistance = 4;

        private int _clickCount;
        private long _lastClickTicks;
        private POINT _lastClickPoint;

        public int ClickCount => _clickCount;

        /// <summary>
        /// Record a non-drag click. Returns the updated click count.
        /// Call this on WM_LBUTTONUP when no drag was detected.
        /// </summary>
        public ClickResult OnClick(POINT pt)
        {
            return OnClick(pt, Environment.TickCount64, GetDoubleClickTime());
        }

        /// <summary>
        /// Testable overload with explicit timing parameters.
        /// </summary>
        public ClickResult OnClick(POINT pt, long currentTicks, uint doubleClickTimeMs)
        {
            var elapsed = currentTicks - _lastClickTicks;
            var dx = pt.x - _lastClickPoint.x;
            var dy = pt.y - _lastClickPoint.y;
            var withinDistance = dx * dx + dy * dy <= MaxClickDistance * MaxClickDistance;

            if (elapsed <= doubleClickTimeMs && withinDistance)
            {
                _clickCount++;
            }
            else
            {
                _clickCount = 1;
            }

            _lastClickTicks = currentTicks;
            _lastClickPoint = pt;

            return new ClickResult(_clickCount);
        }

        public void Reset()
        {
            _clickCount = 0;
            _lastClickTicks = 0;
            _lastClickPoint = default;
        }
    }

    public readonly record struct ClickResult(int ClickCount);
}
