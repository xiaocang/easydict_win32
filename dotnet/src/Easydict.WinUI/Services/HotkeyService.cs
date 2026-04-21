using System.Runtime.InteropServices;
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
    private const int HOTKEY_ID_OCR_TRANSLATE = 7;
    private const int HOTKEY_ID_SILENT_OCR = 8;

    // Modifier keys
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly Window _window;
    private readonly nint _hwnd;
    private volatile bool _isDisposed;
    private volatile bool _isInitialized;

    // Window subclass delegate - must keep reference to prevent GC
    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);
    private SubclassProc? _subclassProc;

    public event Action? OnShowWindow;
    public event Action? OnTranslateSelection;
    public event Action? OnShowMiniWindow;
    public event Action? OnShowFixedWindow;
    public event Action? OnToggleMiniWindow;
    public event Action? OnToggleFixedWindow;
    public event Action? OnOcrTranslate;
    public event Action? OnSilentOcr;

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
        App.LogToFile("[Hotkey] Constructor entered");
        _window = window;
        _hwnd = WindowNative.GetWindowHandle(window);
        App.LogToFile($"[Hotkey] HWND obtained: {_hwnd}");
        
        if (_hwnd == IntPtr.Zero)
        {
            App.LogToFile("[Hotkey] CRITICAL: HWND is zero in constructor!");
        }
    }

    /// <summary>
    /// Initialize and register global hotkeys.
    /// Reads hotkey configurations from SettingsService.
    /// Toggle hotkeys are derived by adding Shift to the base hotkey.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        App.LogToFile("[Hotkey] Initializing hotkey service...");
        
        if (_hwnd == IntPtr.Zero)
        {
            App.LogToFile("[Hotkey] ABORTING: Cannot initialize with zero HWND");
            return;
        }

        // Set up window subclass to intercept WM_HOTKEY messages
        _subclassProc = SubclassWndProc;
        var subclassResult = SetWindowSubclass(_hwnd, _subclassProc, 1, 0);
        App.LogToFile($"[Hotkey] SetWindowSubclass: {subclassResult}");

        RegisterAllHotkeys();

        _isInitialized = true;
        App.LogToFile("[Hotkey] Hotkey service initialized.");
    }

    /// <summary>
    /// Unregisters all current hotkeys and re-registers them from current settings.
    /// Call this when hotkey settings are changed in the UI.
    /// </summary>
    public void ReloadHotkeys()
    {
        if (!_isInitialized || _hwnd == IntPtr.Zero)
        {
            App.LogToFile("[Hotkey] Reload requested but service is not initialized or HWND is zero");
            return;
        }

        App.LogToFile("[Hotkey] Reloading hotkeys...");

        // Unregister all current hotkeys
        UnregisterHotKey(_hwnd, HOTKEY_ID_SHOW);
        UnregisterHotKey(_hwnd, HOTKEY_ID_TRANSLATE_SELECTION);
        UnregisterHotKey(_hwnd, HOTKEY_ID_SHOW_MINI);
        UnregisterHotKey(_hwnd, HOTKEY_ID_SHOW_FIXED);
        UnregisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_MINI);
        UnregisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_FIXED);
        UnregisterHotKey(_hwnd, HOTKEY_ID_OCR_TRANSLATE);
        UnregisterHotKey(_hwnd, HOTKEY_ID_SILENT_OCR);

        // Re-register with current settings
        RegisterAllHotkeys();

        App.LogToFile("[Hotkey] Hotkey reload complete.");
    }

    /// <summary>
    /// Register all hotkeys defined in settings.
    /// </summary>
    private void RegisterAllHotkeys()
    {
        var settings = SettingsService.Instance;

        // Register Show Window hotkey (default: Ctrl+Alt+T)
        if (settings.EnableShowWindowHotkey)
        {
            RegisterHotkeyFromSetting(HOTKEY_ID_SHOW, settings.ShowWindowHotkey, "SHOW");
        }
        else
        {
            App.LogToFile("[Hotkey] SHOW hotkey skipped (disabled in settings)");
        }

        // Register Translate Selection hotkey (default: Ctrl+Alt+D)
        if (settings.EnableTranslateSelectionHotkey)
        {
            RegisterHotkeyFromSetting(HOTKEY_ID_TRANSLATE_SELECTION, settings.TranslateSelectionHotkey, "TRANSLATE");
        }
        else
        {
            App.LogToFile("[Hotkey] TRANSLATE hotkey skipped (disabled in settings)");
        }

        // Register Show Mini Window hotkey (default: Ctrl+Alt+M)
        if (settings.EnableShowMiniWindowHotkey)
        {
            var miniResult = HotkeyParser.Parse(settings.ShowMiniWindowHotkey);
            if (miniResult.IsValid)
            {
                var result = RegisterHotKey(_hwnd, HOTKEY_ID_SHOW_MINI, miniResult.Modifiers | MOD_NOREPEAT, miniResult.VirtualKey);
                App.LogToFile($"[Hotkey] RegisterHotKey MINI ({settings.ShowMiniWindowHotkey}): {result}, Error: {Marshal.GetLastWin32Error()}");

                // Register Toggle Mini hotkey (base + Shift)
                var toggleMini = HotkeyParser.AddShiftModifier(miniResult);
                var toggleResult = RegisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_MINI, toggleMini.Modifiers | MOD_NOREPEAT, toggleMini.VirtualKey);
                App.LogToFile($"[Hotkey] RegisterHotKey TOGGLE_MINI ({settings.ShowMiniWindowHotkey}+Shift): {toggleResult}, Error: {Marshal.GetLastWin32Error()}");
            }
            else
            {
                App.LogToFile($"[Hotkey] Failed to parse ShowMiniWindowHotkey '{settings.ShowMiniWindowHotkey}': {miniResult.ErrorMessage}");
            }
        }
        else
        {
            App.LogToFile("[Hotkey] MINI hotkey skipped (disabled in settings)");
        }

        // Register Show Fixed Window hotkey (default: Ctrl+Alt+F)
        if (settings.EnableShowFixedWindowHotkey)
        {
            var fixedResult = HotkeyParser.Parse(settings.ShowFixedWindowHotkey);
            if (fixedResult.IsValid)
            {
                var result = RegisterHotKey(_hwnd, HOTKEY_ID_SHOW_FIXED, fixedResult.Modifiers | MOD_NOREPEAT, fixedResult.VirtualKey);
                App.LogToFile($"[Hotkey] RegisterHotKey FIXED ({settings.ShowFixedWindowHotkey}): {result}, Error: {Marshal.GetLastWin32Error()}");

                // Register Toggle Fixed hotkey (base + Shift)
                var toggleFixed = HotkeyParser.AddShiftModifier(fixedResult);
                var toggleResult = RegisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_FIXED, toggleFixed.Modifiers | MOD_NOREPEAT, toggleFixed.VirtualKey);
                App.LogToFile($"[Hotkey] RegisterHotKey TOGGLE_FIXED ({settings.ShowFixedWindowHotkey}+Shift): {toggleResult}, Error: {Marshal.GetLastWin32Error()}");
            }
            else
            {
                App.LogToFile($"[Hotkey] Failed to parse ShowFixedWindowHotkey '{settings.ShowFixedWindowHotkey}': {fixedResult.ErrorMessage}");
            }
        }
        else
        {
            App.LogToFile("[Hotkey] FIXED hotkey skipped (disabled in settings)");
        }

        // Register OCR Translate hotkey (default: Ctrl+Alt+S)
        if (settings.EnableOcrTranslateHotkey)
        {
            RegisterHotkeyFromSetting(HOTKEY_ID_OCR_TRANSLATE, settings.OcrTranslateHotkey, "OCR_TRANSLATE");
        }
        else
        {
            App.LogToFile("[Hotkey] OCR_TRANSLATE hotkey skipped (disabled in settings)");
        }

        // Register Silent OCR hotkey (default: Ctrl+Alt+Shift+S)
        if (settings.EnableSilentOcrHotkey)
        {
            RegisterHotkeyFromSetting(HOTKEY_ID_SILENT_OCR, settings.SilentOcrHotkey, "SILENT_OCR");
        }
        else
        {
            App.LogToFile("[Hotkey] SILENT_OCR hotkey skipped (disabled in settings)");
        }
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
            App.LogToFile($"[Hotkey] RegisterHotKey {debugName} ({hotkeyString}): {result}, Error: {Marshal.GetLastWin32Error()}");
        }
        else
        {
            App.LogToFile($"[Hotkey] Failed to parse {debugName} hotkey '{hotkeyString}': {parseResult.ErrorMessage}");
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
            App.LogToFile($"[Hotkey] HOTKEY DETECTED: WM_HOTKEY received, id={hotkeyId}");
            ForegroundWindowHelper.AllowCurrentProcessToSetForeground("Hotkey");
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
            case HOTKEY_ID_OCR_TRANSLATE:
                OnOcrTranslate?.Invoke();
                break;
            case HOTKEY_ID_SILENT_OCR:
                OnSilentOcr?.Invoke();
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
        UnregisterHotKey(_hwnd, HOTKEY_ID_OCR_TRANSLATE);
        UnregisterHotKey(_hwnd, HOTKEY_ID_SILENT_OCR);

        // Remove window subclass
        if (_subclassProc != null)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, 1);
            _subclassProc = null;
        }

        System.Diagnostics.Debug.WriteLine("[Hotkey] Hotkey service disposed.");
    }
}

