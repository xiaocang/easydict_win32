using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Easydict.WinUI.Services;

/// <summary>
/// Describes a hotkey that failed to register (e.g. the combination is reserved
/// by Windows like Win+Space, or already in use by another application).
/// </summary>
/// <param name="NameKey">Localization key for the hotkey's friendly name.</param>
/// <param name="HotkeyString">The hotkey combination as configured, e.g. "Win+Space".</param>
/// <param name="ErrorCode">The Win32 error code returned by RegisterHotKey.</param>
public sealed record HotkeyRegistrationFailure(string NameKey, string HotkeyString, int ErrorCode);

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
    /// <returns>
    /// The list of hotkeys that could not be registered (e.g. reserved by Windows
    /// or already in use by another application). Empty when everything registered.
    /// </returns>
    public IReadOnlyList<HotkeyRegistrationFailure> ReloadHotkeys()
    {
        if (!_isInitialized || _hwnd == IntPtr.Zero)
        {
            App.LogToFile("[Hotkey] Reload requested but service is not initialized or HWND is zero");
            return Array.Empty<HotkeyRegistrationFailure>();
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
        var failures = RegisterAllHotkeys();

        App.LogToFile($"[Hotkey] Hotkey reload complete. {failures.Count} failure(s).");
        return failures;
    }

    /// <summary>
    /// Register all hotkeys defined in settings.
    /// </summary>
    /// <returns>The list of hotkeys that failed to register.</returns>
    private List<HotkeyRegistrationFailure> RegisterAllHotkeys()
    {
        var settings = SettingsService.Instance;
        var failures = new List<HotkeyRegistrationFailure>();

        // Register Show Window hotkey (default: Ctrl+Alt+T)
        if (settings.EnableShowWindowHotkey)
        {
            RegisterHotkeyFromSetting(HOTKEY_ID_SHOW, settings.ShowWindowHotkey, "SHOW", "ShowWindow", failures);
        }
        else
        {
            App.LogToFile("[Hotkey] SHOW hotkey skipped (disabled in settings)");
        }

        // Register Translate Selection hotkey (default: Ctrl+Alt+D)
        if (settings.EnableTranslateSelectionHotkey)
        {
            RegisterHotkeyFromSetting(HOTKEY_ID_TRANSLATE_SELECTION, settings.TranslateSelectionHotkey, "TRANSLATE", "TranslateSelection", failures);
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
                TryRegister(HOTKEY_ID_SHOW_MINI, miniResult.Modifiers, miniResult.VirtualKey,
                    "MINI", "ShowMiniWindow", settings.ShowMiniWindowHotkey, failures);

                // Register Toggle Mini hotkey (base + Shift)
                var toggleMini = HotkeyParser.AddShiftModifier(miniResult);
                TryRegister(HOTKEY_ID_TOGGLE_MINI, toggleMini.Modifiers, toggleMini.VirtualKey,
                    "TOGGLE_MINI", "ShowMiniWindow", settings.ShowMiniWindowHotkey + "+Shift", failures);
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
                TryRegister(HOTKEY_ID_SHOW_FIXED, fixedResult.Modifiers, fixedResult.VirtualKey,
                    "FIXED", "ShowFixedWindow", settings.ShowFixedWindowHotkey, failures);

                // Register Toggle Fixed hotkey (base + Shift)
                var toggleFixed = HotkeyParser.AddShiftModifier(fixedResult);
                TryRegister(HOTKEY_ID_TOGGLE_FIXED, toggleFixed.Modifiers, toggleFixed.VirtualKey,
                    "TOGGLE_FIXED", "ShowFixedWindow", settings.ShowFixedWindowHotkey + "+Shift", failures);
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
            RegisterHotkeyFromSetting(HOTKEY_ID_OCR_TRANSLATE, settings.OcrTranslateHotkey, "OCR_TRANSLATE", "OcrScreenshotTranslate", failures);
        }
        else
        {
            App.LogToFile("[Hotkey] OCR_TRANSLATE hotkey skipped (disabled in settings)");
        }

        // Register Silent OCR hotkey (default: Ctrl+Alt+Shift+S)
        if (settings.EnableSilentOcrHotkey)
        {
            RegisterHotkeyFromSetting(HOTKEY_ID_SILENT_OCR, settings.SilentOcrHotkey, "SILENT_OCR", "SilentOcr", failures);
        }
        else
        {
            App.LogToFile("[Hotkey] SILENT_OCR hotkey skipped (disabled in settings)");
        }

        return failures;
    }

    /// <summary>
    /// Register a hotkey from a settings string, recording any failure.
    /// </summary>
    private void RegisterHotkeyFromSetting(int hotkeyId, string hotkeyString, string debugName, string nameKey, List<HotkeyRegistrationFailure> failures)
    {
        var parseResult = HotkeyParser.Parse(hotkeyString);
        if (parseResult.IsValid)
        {
            TryRegister(hotkeyId, parseResult.Modifiers, parseResult.VirtualKey, debugName, nameKey, hotkeyString, failures);
        }
        else
        {
            App.LogToFile($"[Hotkey] Failed to parse {debugName} hotkey '{hotkeyString}': {parseResult.ErrorMessage}");
        }
    }

    /// <summary>
    /// Calls RegisterHotKey and records a <see cref="HotkeyRegistrationFailure"/> when it fails.
    /// A failure typically means the combination is reserved by Windows (e.g. Win+Space)
    /// or already claimed by another application.
    /// </summary>
    private void TryRegister(int hotkeyId, uint modifiers, uint virtualKey, string debugName, string nameKey, string hotkeyString, List<HotkeyRegistrationFailure> failures)
    {
        var result = RegisterHotKey(_hwnd, hotkeyId, modifiers | MOD_NOREPEAT, virtualKey);
        var error = Marshal.GetLastWin32Error();
        App.LogToFile($"[Hotkey] RegisterHotKey {debugName} ({hotkeyString}): {result}, Error: {error}");
        if (!result)
        {
            failures.Add(new HotkeyRegistrationFailure(nameKey, hotkeyString, error));
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

