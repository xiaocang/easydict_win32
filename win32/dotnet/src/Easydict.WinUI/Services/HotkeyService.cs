using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages global hotkeys for the application.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_SHOW = 1;
    private const int HOTKEY_ID_TRANSLATE_SELECTION = 2;

    // Modifier keys
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    // Virtual key codes
    private const uint VK_T = 0x54;  // T key
    private const uint VK_D = 0x44;  // D key

    private readonly Window _window;
    private readonly nint _hwnd;
    private bool _isDisposed;
    private bool _isInitialized;

    public event Action? OnShowWindow;
    public event Action? OnTranslateSelection;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    public HotkeyService(Window window)
    {
        _window = window;
        _hwnd = WindowNative.GetWindowHandle(window);
    }

    /// <summary>
    /// Initialize and register global hotkeys.
    /// Default: Ctrl+Alt+T to show window, Ctrl+Alt+D to translate selection.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        // Register Ctrl+Alt+T to show window
        RegisterHotKey(_hwnd, HOTKEY_ID_SHOW, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_T);

        // Register Ctrl+Alt+D to translate selection
        RegisterHotKey(_hwnd, HOTKEY_ID_TRANSLATE_SELECTION, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_D);

        _isInitialized = true;
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
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        UnregisterHotKey(_hwnd, HOTKEY_ID_SHOW);
        UnregisterHotKey(_hwnd, HOTKEY_ID_TRANSLATE_SELECTION);
    }
}

