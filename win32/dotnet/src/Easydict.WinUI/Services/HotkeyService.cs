using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages global hotkeys for the application.
/// Uses Win32 window subclassing to intercept WM_HOTKEY messages.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_SHOW = 1;
    private const int HOTKEY_ID_TRANSLATE_SELECTION = 2;
    private const int HOTKEY_ID_SHOW_MINI = 3;

    // Modifier keys
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    // Virtual key codes
    private const uint VK_T = 0x54;  // T key
    private const uint VK_D = 0x44;  // D key
    private const uint VK_M = 0x4D;  // M key

    private readonly Window _window;
    private readonly nint _hwnd;
    private bool _isDisposed;
    private bool _isInitialized;

    // Window subclass delegate - must keep reference to prevent GC
    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);
    private SubclassProc? _subclassProc;

    public event Action? OnShowWindow;
    public event Action? OnTranslateSelection;
    public event Action? OnShowMiniWindow;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    public HotkeyService(Window window)
    {
        _window = window;
        _hwnd = WindowNative.GetWindowHandle(window);
    }

    /// <summary>
    /// Initialize and register global hotkeys.
    /// Default: Ctrl+Alt+T to show window, Ctrl+Alt+D to translate selection, Ctrl+Alt+M to show mini window.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        System.Diagnostics.Debug.WriteLine("[Hotkey] Initializing hotkey service...");

        // Set up window subclass to intercept WM_HOTKEY messages
        _subclassProc = SubclassWndProc;
        var subclassResult = SetWindowSubclass(_hwnd, _subclassProc, 1, 0);
        System.Diagnostics.Debug.WriteLine($"[Hotkey] SetWindowSubclass: {subclassResult}");

        // Register Ctrl+Alt+T to show window
        var result1 = RegisterHotKey(_hwnd, HOTKEY_ID_SHOW, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_T);
        System.Diagnostics.Debug.WriteLine($"[Hotkey] RegisterHotKey SHOW (Ctrl+Alt+T): {result1}, Error: {Marshal.GetLastWin32Error()}");

        // Register Ctrl+Alt+D to translate selection
        var result2 = RegisterHotKey(_hwnd, HOTKEY_ID_TRANSLATE_SELECTION, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_D);
        System.Diagnostics.Debug.WriteLine($"[Hotkey] RegisterHotKey TRANSLATE (Ctrl+Alt+D): {result2}, Error: {Marshal.GetLastWin32Error()}");

        // Register Ctrl+Alt+M to show mini window
        var result3 = RegisterHotKey(_hwnd, HOTKEY_ID_SHOW_MINI, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_M);
        System.Diagnostics.Debug.WriteLine($"[Hotkey] RegisterHotKey MINI (Ctrl+Alt+M): {result3}, Error: {Marshal.GetLastWin32Error()}");

        _isInitialized = true;
        System.Diagnostics.Debug.WriteLine("[Hotkey] Hotkey service initialized.");
    }

    /// <summary>
    /// Window subclass procedure to intercept WM_HOTKEY messages.
    /// </summary>
    private nint SubclassWndProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_HOTKEY)
        {
            int hotkeyId = (int)wParam;
            System.Diagnostics.Debug.WriteLine($"[Hotkey] WM_HOTKEY received, id={hotkeyId}");
            ProcessHotkeyMessage(hotkeyId);
            return 0;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// Process hotkey message from window procedure.
    /// Call this from a subclassed window proc.
    /// </summary>
    public void ProcessHotkeyMessage(int hotkeyId)
    {
        switch (hotkeyId)
        {
            case HOTKEY_ID_SHOW:
                OnShowWindow?.Invoke();
                break;
            case HOTKEY_ID_TRANSLATE_SELECTION:
                OnTranslateSelection?.Invoke();
                break;
            case HOTKEY_ID_SHOW_MINI:
                OnShowMiniWindow?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        System.Diagnostics.Debug.WriteLine("[Hotkey] Disposing hotkey service...");

        // Unregister hotkeys
        UnregisterHotKey(_hwnd, HOTKEY_ID_SHOW);
        UnregisterHotKey(_hwnd, HOTKEY_ID_TRANSLATE_SELECTION);
        UnregisterHotKey(_hwnd, HOTKEY_ID_SHOW_MINI);

        // Remove window subclass
        if (_subclassProc != null)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, 1);
            _subclassProc = null;
        }

        System.Diagnostics.Debug.WriteLine("[Hotkey] Hotkey service disposed.");
    }
}

