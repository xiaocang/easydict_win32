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
    private const int HOTKEY_ID_SHOW_FIXED = 4;
    private const int HOTKEY_ID_TOGGLE_MINI = 5;
    private const int HOTKEY_ID_TOGGLE_FIXED = 6;

    // Modifier keys
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

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
    public event Action? OnShowFixedWindow;
    public event Action? OnToggleMiniWindow;
    public event Action? OnToggleFixedWindow;

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
    /// Reads hotkey configurations from SettingsService.
    /// Toggle hotkeys are derived by adding Shift to the base hotkey.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        System.Diagnostics.Debug.WriteLine("[Hotkey] Initializing hotkey service...");

        // Set up window subclass to intercept WM_HOTKEY messages
        _subclassProc = SubclassWndProc;
        var subclassResult = SetWindowSubclass(_hwnd, _subclassProc, 1, 0);
        System.Diagnostics.Debug.WriteLine($"[Hotkey] SetWindowSubclass: {subclassResult}");

        var settings = SettingsService.Instance;

        // Register Show Window hotkey (default: Ctrl+Alt+T)
        RegisterHotkeyFromSetting(HOTKEY_ID_SHOW, settings.ShowWindowHotkey, "SHOW");

        // Register Translate Selection hotkey (default: Ctrl+Alt+D)
        RegisterHotkeyFromSetting(HOTKEY_ID_TRANSLATE_SELECTION, settings.TranslateSelectionHotkey, "TRANSLATE");

        // Register Show Mini Window hotkey (default: Ctrl+Alt+M)
        var miniResult = HotkeyParser.Parse(settings.ShowMiniWindowHotkey);
        if (miniResult.IsValid)
        {
            var result = RegisterHotKey(_hwnd, HOTKEY_ID_SHOW_MINI, miniResult.Modifiers | MOD_NOREPEAT, miniResult.VirtualKey);
            System.Diagnostics.Debug.WriteLine($"[Hotkey] RegisterHotKey MINI ({settings.ShowMiniWindowHotkey}): {result}, Error: {Marshal.GetLastWin32Error()}");

            // Register Toggle Mini hotkey (base + Shift)
            var toggleMini = HotkeyParser.AddShiftModifier(miniResult);
            var toggleResult = RegisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_MINI, toggleMini.Modifiers | MOD_NOREPEAT, toggleMini.VirtualKey);
            System.Diagnostics.Debug.WriteLine($"[Hotkey] RegisterHotKey TOGGLE_MINI ({settings.ShowMiniWindowHotkey}+Shift): {toggleResult}, Error: {Marshal.GetLastWin32Error()}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[Hotkey] Failed to parse ShowMiniWindowHotkey '{settings.ShowMiniWindowHotkey}': {miniResult.ErrorMessage}");
        }

        // Register Show Fixed Window hotkey (default: Ctrl+Alt+F)
        var fixedResult = HotkeyParser.Parse(settings.ShowFixedWindowHotkey);
        if (fixedResult.IsValid)
        {
            var result = RegisterHotKey(_hwnd, HOTKEY_ID_SHOW_FIXED, fixedResult.Modifiers | MOD_NOREPEAT, fixedResult.VirtualKey);
            System.Diagnostics.Debug.WriteLine($"[Hotkey] RegisterHotKey FIXED ({settings.ShowFixedWindowHotkey}): {result}, Error: {Marshal.GetLastWin32Error()}");

            // Register Toggle Fixed hotkey (base + Shift)
            var toggleFixed = HotkeyParser.AddShiftModifier(fixedResult);
            var toggleResult = RegisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_FIXED, toggleFixed.Modifiers | MOD_NOREPEAT, toggleFixed.VirtualKey);
            System.Diagnostics.Debug.WriteLine($"[Hotkey] RegisterHotKey TOGGLE_FIXED ({settings.ShowFixedWindowHotkey}+Shift): {toggleResult}, Error: {Marshal.GetLastWin32Error()}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[Hotkey] Failed to parse ShowFixedWindowHotkey '{settings.ShowFixedWindowHotkey}': {fixedResult.ErrorMessage}");
        }

        _isInitialized = true;
        System.Diagnostics.Debug.WriteLine("[Hotkey] Hotkey service initialized.");
    }

    /// <summary>
    /// Register a hotkey from a settings string.
    /// </summary>
    private void RegisterHotkeyFromSetting(int hotkeyId, string hotkeyString, string debugName)
    {
        var parseResult = HotkeyParser.Parse(hotkeyString);
        if (parseResult.IsValid)
        {
            var result = RegisterHotKey(_hwnd, hotkeyId, parseResult.Modifiers | MOD_NOREPEAT, parseResult.VirtualKey);
            System.Diagnostics.Debug.WriteLine($"[Hotkey] RegisterHotKey {debugName} ({hotkeyString}): {result}, Error: {Marshal.GetLastWin32Error()}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[Hotkey] Failed to parse {debugName} hotkey '{hotkeyString}': {parseResult.ErrorMessage}");
        }
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
            case HOTKEY_ID_SHOW_FIXED:
                OnShowFixedWindow?.Invoke();
                break;
            case HOTKEY_ID_TOGGLE_MINI:
                OnToggleMiniWindow?.Invoke();
                break;
            case HOTKEY_ID_TOGGLE_FIXED:
                OnToggleFixedWindow?.Invoke();
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
        UnregisterHotKey(_hwnd, HOTKEY_ID_SHOW_FIXED);
        UnregisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_MINI);
        UnregisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_FIXED);

        // Remove window subclass
        if (_subclassProc != null)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, 1);
            _subclassProc = null;
        }

        System.Diagnostics.Debug.WriteLine("[Hotkey] Hotkey service disposed.");
    }
}

