using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Easydict.WinUI.Views;

/// <summary>
/// A tiny floating window (30x30) that appears near text selection.
/// Clicking the button triggers translation of the selected text.
///
/// Key Win32 properties:
/// - WS_EX_NOACTIVATE: Does not steal focus from the source application
/// - WS_EX_TOOLWINDOW: Does not appear in the taskbar
/// - WS_EX_TOPMOST: Always on top of other windows
/// </summary>
public sealed partial class PopButtonWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;

    // These Win32 P/Invoke calls are required because WinUI 3 does not provide managed APIs for:
    // - WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW extended window styles (prevents focus steal)
    // - SWP_NOACTIVATE positioning (shows window without activating)
    // - Per-window DPI queries
    // The OverlappedPresenter API only covers a subset of window chrome options.

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    private static partial int GetDpiForWindow(IntPtr hWnd);

    private readonly IntPtr _hwnd;
    private readonly AppWindow? _appWindow;
    private bool _isVisible;

    /// <summary>
    /// Fired when the user clicks the translate button.
    /// </summary>
    public event Action? OnClicked;

    /// <summary>
    /// Gets whether the pop button window is currently visible.
    /// </summary>
    public bool IsPopupVisible => _isVisible;

    public PopButtonWindow()
    {
        this.InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindowStyle();
    }

    /// <summary>
    /// Configure Win32 window styles for a floating, non-activating popup.
    /// </summary>
    private void ConfigureWindowStyle()
    {
        if (_appWindow == null) return;

        // Remove title bar and borders
        var presenter = _appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // Set extended window styles
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);

        // Set initial size (30x30 logical pixels, scaled for DPI)
        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi / 96.0;
        var physicalSize = (int)(30 * scale);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(physicalSize, physicalSize));

        // Start hidden
        ShowWindow(_hwnd, SW_HIDE);
        _isVisible = false;

        Debug.WriteLine("[PopButton] Window configured with NOACTIVATE | TOOLWINDOW | TOPMOST");
    }

    /// <summary>
    /// Show the pop button at the specified screen coordinates.
    /// The button appears offset from the given point (upper-right of selection end).
    /// </summary>
    /// <param name="screenX">Screen X coordinate of the selection end point</param>
    /// <param name="screenY">Screen Y coordinate of the selection end point</param>
    public void ShowAt(int screenX, int screenY)
    {
        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi / 96.0;
        var physicalSize = (int)(30 * scale);
        var offsetX = (int)(8 * scale);
        var offsetY = (int)(32 * scale);

        // Position: to the right and above the mouse release point
        var x = screenX + offsetX;
        var y = screenY - offsetY;

        // Clamp to screen bounds so the button never appears off-screen
        try
        {
            var display = DisplayArea.GetFromPoint(
                new PointInt32(screenX, screenY), DisplayAreaFallback.Nearest);
            var workArea = display.WorkArea;

            if (x + physicalSize > workArea.X + workArea.Width)
                x = workArea.X + workArea.Width - physicalSize;
            if (x < workArea.X)
                x = workArea.X;
            if (y < workArea.Y)
                y = workArea.Y;
            if (y + physicalSize > workArea.Y + workArea.Height)
                y = workArea.Y + workArea.Height - physicalSize;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PopButton] Screen bounds check failed, using unclamped position: {ex.Message}");
        }

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, physicalSize, physicalSize,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _isVisible = true;

        Debug.WriteLine($"[PopButton] Shown at ({x}, {y}), size={physicalSize}, dpi={dpi}");
    }

    /// <summary>
    /// Hide the pop button window.
    /// </summary>
    public void HidePopup()
    {
        if (!_isVisible) return;

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        _isVisible = false;

        Debug.WriteLine("[PopButton] Hidden");
    }

    /// <summary>
    /// Apply theme to the pop button window.
    /// </summary>
    public void ApplyTheme(ElementTheme theme)
    {
        if (this.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    private void OnTranslateButtonClick(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PopButton] Translate button clicked");
        OnClicked?.Invoke();
    }
}
