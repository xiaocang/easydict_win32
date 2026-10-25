using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.TextActions;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.UI.ViewManagement;
using Windows.Graphics;
using WinRT.Interop;

namespace Easydict.WinUI.Views;

/// <summary>
/// A tiny floating action strip that appears near text selection: the native translate button,
/// followed (after a divider) by the configured text actions — search sites and services such as
/// Bob plugins. Plugin buttons carry a corner mark so third-party actions are visually distinct.
/// Clicking the translate button translates the selected text; clicking an action runs it.
///
/// Key Win32 properties:
/// - WS_EX_NOACTIVATE: Does not steal focus from the source application
/// - WS_EX_TOOLWINDOW: Does not appear in the taskbar
/// - WS_EX_TOPMOST: Always on top of other windows
/// </summary>
public sealed partial class PopButtonWindow : Window
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_WINDOWEDGE = 0x00000100;
    private const int WS_EX_CLIENTEDGE = 0x00000200;
    private const int WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMNCRP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;

    // These Win32 P/Invoke calls are required because WinUI 3 does not provide managed APIs for:
    // - WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW extended window styles (prevents focus steal)
    // - SWP_NOACTIVATE positioning (shows window without activating)
    // - Per-window DPI queries
    // The OverlappedPresenter API only covers a subset of window chrome options.

    // Use GetWindowLongPtr/SetWindowLongPtr for 64-bit compatibility
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmExtendFrameIntoClientArea(IntPtr hWnd, in Margins margins);

    [LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmSetWindowAttribute(IntPtr hWnd, int attribute, in int value, int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    private static partial int GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    private static partial IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetCursor(IntPtr hCursor);

    // Standard cursor constants
    private const int IDC_ARROW = 32512;
    private const int IDC_HAND = 32649;

    private readonly IntPtr _hwnd;
    private readonly AppWindow? _appWindow;
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private TransparentCompositionBackdrop? _transparentBackdrop;
    private Visual? _trayIconVisual;
    private string? _loadedTrayIconPath;
    private bool _isVisible;
    private string _lastStyleWriteDiagnostics = "Native style writes have not run.";

    // Strip geometry (logical pixels). Width = translate button + optional divider + action buttons.
    private const int ButtonSize = 30;
    private const int ItemSpacing = 2;
    private const int DividerWidth = 1;
    private readonly List<Button> _actionButtons = new();

    // Hover/pressed interaction state
    private const double BaseOpacity = 0.75;
    private const double DarkBaseOpacity = 0.82;
    private const double HoverOpacity = 0.9;
    private const double DarkHoverOpacity = 0.94;
    private const double PressedOpacity = 0.68;
    private const double DarkPressedOpacity = 0.76;
    private const double OpacityTransitionMs = 160;

    private double CurrentBaseOpacity
    {
        get
        {
            if (MinimalThemeService.IsActive)
            {
                return 1.0;
            }

            return RootGrid.ActualTheme == ElementTheme.Dark ? DarkBaseOpacity : BaseOpacity;
        }
    }
    private bool _isPointerOver;
    private bool _isPressed;

    // Cached cursor handles
    private static readonly IntPtr _arrowCursor = LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW);
    private static readonly IntPtr _handCursor = LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND);

    /// <summary>
    /// Fired when the user clicks the translate button.
    /// </summary>
    public event Action? OnClicked;

    /// <summary>
    /// Fired when the user clicks one of the configured action buttons.
    /// </summary>
    public event Action<TextAction>? OnActionClicked;

    /// <summary>Number of action buttons currently on the strip.</summary>
    public int ActionCount => _actionButtons.Count;

    private int LogicalWidth => ButtonSize + (_actionButtons.Count > 0
        ? ItemSpacing + DividerWidth + _actionButtons.Count * (ItemSpacing + ButtonSize)
        : 0);

    /// <summary>
    /// Gets whether the pop button window is currently visible.
    /// </summary>
    public bool IsPopupVisible => _isVisible;

    /// <summary>
    /// Gets the window handle (HWND) of the pop button window.
    /// </summary>
    public IntPtr WindowHandle => _hwnd;

    internal string LastStyleWriteDiagnostics => _lastStyleWriteDiagnostics;

    public PopButtonWindow()
    {
        // A WinUI Window starts hidden. Set the host style before creating its XAML tree so
        // the compositor never paints an opaque redirected HWND surface behind transparent XAML.
        _hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(_hwnd, SW_HIDE);
        ApplyTransparentHostStyle();
        this.InitializeComponent();

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("PopButtonWindow must be created on its UI thread.");
        // Windows.UI.Composition.Compositor depends on a system DispatcherQueue. Let the
        // Windows App SDK queue own that queue's lifecycle before creating the compositor.
        _dispatcherQueue.EnsureSystemDispatcherQueue();

        TrayIconImage.Loaded += (_, _) => EnsureTrayIconVisual();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += OnWindowClosed;

        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindowStyle();

        // Attach after presenter/chrome changes so the backdrop's DWM configuration is the
        // final host operation before the window is shown.
        _transparentBackdrop = new TransparentCompositionBackdrop(_hwnd);
        SystemBackdrop = _transparentBackdrop;
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

        ApplyTransparentHostStyle();

        // Set initial size (30x30 logical pixels, scaled for DPI; grows when actions are added)
        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi / 96.0;
        var physicalSize = (int)(ButtonSize * scale);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(physicalSize, physicalSize));

        // Start hidden
        ShowWindow(_hwnd, SW_HIDE);
        _isVisible = false;

        Debug.WriteLine("[PopButton] Window configured with NOACTIVATE | TOOLWINDOW | TOPMOST");
    }

    private void ApplyTransparentHostStyle()
    {
        // Window/AppWindow presenter initialization can restore the standard overlapped
        // frame after the HWND is first created. Strip frame styles as well as extended
        // edge styles every time the popup is prepared for display.
        var originalStyle = GetWindowLongPtr(_hwnd, GWL_STYLE);
        var style = originalStyle;
        style = (IntPtr)((long)style & ~(WS_BORDER | WS_DLGFRAME | WS_THICKFRAME));
        Marshal.SetLastPInvokeError(0);
        var previousStyle = SetWindowLongPtr(_hwnd, GWL_STYLE, style);
        var styleWriteError = Marshal.GetLastPInvokeError();
        var styleReadBack = GetWindowLongPtr(_hwnd, GWL_STYLE);

        var originalExStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        var withoutEdges = (IntPtr)((long)originalExStyle & ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE));
        var edgesWrite = WriteWindowLongAndRead(GWL_EXSTYLE, withoutEdges);

        // Keep each activation-related bit isolated. WS_EX_NOREDIRECTIONBITMAP was not
        // present in the visually accepted state and caused SetWindowLongPtr to reject the
        // whole combined value on this WinUI HWND (ERROR_INVALID_PARAMETER).
        var noActivateRequested = (IntPtr)(edgesWrite.ReadBack.ToInt64() | WS_EX_NOACTIVATE);
        var noActivateWrite = WriteWindowLongAndRead(GWL_EXSTYLE, noActivateRequested);
        var toolWindowRequested = (IntPtr)(noActivateWrite.ReadBack.ToInt64() | WS_EX_TOOLWINDOW);
        var toolWindowWrite = WriteWindowLongAndRead(GWL_EXSTYLE, toolWindowRequested);

        _lastStyleWriteDiagnostics = $"GWL_STYLE before=0x{originalStyle.ToInt64():X8}, previous=0x{previousStyle.ToInt64():X8}, read=0x{styleReadBack.ToInt64():X8}, error={styleWriteError}; " +
                                     $"GWL_EXSTYLE before=0x{originalExStyle.ToInt64():X8}, edge previous=0x{edgesWrite.Previous.ToInt64():X8}, edge read=0x{edgesWrite.ReadBack.ToInt64():X8}, edge error={edgesWrite.Error}; " +
                                     $"NOACTIVATE requested=0x{noActivateRequested.ToInt64():X8}, previous=0x{noActivateWrite.Previous.ToInt64():X8}, read=0x{noActivateWrite.ReadBack.ToInt64():X8}, error={noActivateWrite.Error}; " +
                                     $"TOOLWINDOW requested=0x{toolWindowRequested.ToInt64():X8}, previous=0x{toolWindowWrite.Previous.ToInt64():X8}, read=0x{toolWindowWrite.ReadBack.ToInt64():X8}, error={toolWindowWrite.Error}";
        Debug.WriteLine($"[PopButton] {_lastStyleWriteDiagnostics}");

        // DWM's full glass frame makes the HWND's otherwise empty client area per-pixel
        // transparent. Layered-window alpha only controls whole-window opacity and left a
        // white rounded rectangle on the checkerboard, so the host is deliberately unlayered.
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var frameResult = DwmExtendFrameIntoClientArea(_hwnd, margins);
        if (frameResult != 0)
        {
            Debug.WriteLine($"[PopButton] DwmExtendFrameIntoClientArea failed (HRESULT 0x{frameResult:X8}).");
        }

        var noRounding = DWMNCRP_DONOTROUND;
        var cornerResult = DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, noRounding, sizeof(int));
        if (cornerResult != 0)
        {
            Debug.WriteLine($"[PopButton] Could not disable DWM corner rounding (HRESULT 0x{cornerResult:X8}).");
        }

        var noBorder = DWMWA_COLOR_NONE;
        var borderResult = DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, noBorder, sizeof(int));
        if (borderResult != 0)
        {
            Debug.WriteLine($"[PopButton] Could not disable DWM border (HRESULT 0x{borderResult:X8}).");
        }
    }

    private (IntPtr Previous, IntPtr ReadBack, int Error) WriteWindowLongAndRead(int index, IntPtr value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(_hwnd, index, value);
        var error = Marshal.GetLastPInvokeError();
        var readBack = GetWindowLongPtr(_hwnd, index);
        return (previous, readBack, error);
    }

    /// <summary>
    /// Show the pop button at the specified screen coordinates.
    /// The button appears offset from the given point (upper-right of selection end).
    /// </summary>
    /// <param name="screenX">Screen X coordinate of the selection end point</param>
    /// <param name="screenY">Screen Y coordinate of the selection end point</param>
    public void ShowAt(int screenX, int screenY)
    {
        RefreshTrayIcon();
        // Reset interaction state when showing
        _isPointerOver = false;
        _isPressed = false;
        SetInteractionOpacity(CurrentBaseOpacity);

        var useLegacy = SettingsService.Instance.PopButtonUseLegacyPositioning;
        if (useLegacy)
        {
            ShowAtUsingWin32(screenX, screenY);
        }
        else
        {
            // PopupAnchor opt-in path. If the new positioning fails (e.g. host runtime is older
            // than 2.0.1), fall back to the Win32 path so selection translate keeps working.
            try
            {
                ShowAtUsingPopupAnchor(screenX, screenY);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PopButton] PopupAnchor path failed, falling back to Win32: {ex.Message}");
                ShowAtUsingWin32(screenX, screenY);
            }
        }
    }

    private void ShowAtUsingWin32(int screenX, int screenY)
    {
        // WinUI may reapply its default overlapped frame after construction. Reassert the
        // borderless/non-activating native styles at the last point before showing, and ask
        // USER32 to recalculate the non-client frame from the new style bits.
        ApplyTransparentHostStyle();

        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi / 96.0;
        var physicalWidth = (int)(LogicalWidth * scale);
        var physicalHeight = (int)(ButtonSize * scale);
        var offsetX = (int)(8 * scale);
        var offsetY = (int)(32 * scale);

        // Position: to the right and above the mouse release point
        var x = screenX + offsetX;
        var y = screenY - offsetY;

        // Clamp to screen bounds so the strip never appears off-screen
        try
        {
            var display = DisplayArea.GetFromPoint(
                new PointInt32(screenX, screenY), DisplayAreaFallback.Nearest);
            var workArea = display.WorkArea;

            if (x + physicalWidth > workArea.X + workArea.Width)
                x = workArea.X + workArea.Width - physicalWidth;
            if (x < workArea.X)
                x = workArea.X;
            if (y < workArea.Y)
                y = workArea.Y;
            if (y + physicalHeight > workArea.Y + workArea.Height)
                y = workArea.Y + workArea.Height - physicalHeight;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PopButton] Screen bounds check failed, using unclamped position: {ex.Message}");
        }

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, physicalWidth, physicalHeight,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        _isVisible = true;
        StartTrayIconBob();

        Debug.WriteLine($"[PopButton] Shown at ({x}, {y}), size={physicalWidth}x{physicalHeight}, actions={_actionButtons.Count}, dpi={dpi}, style=0x{GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64():X8}, exStyle=0x{GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64():X8}");
    }

    /// <summary>
    /// Replace the action buttons on the strip. Must be called on the UI thread before <see cref="ShowAt"/>.
    /// Internal because <see cref="PresentedTextAction"/> is an assembly-internal presentation type.
    /// </summary>
    internal void SetActions(IReadOnlyList<PresentedTextAction> actions, ElementTheme theme)
    {
        foreach (var button in _actionButtons)
        {
            button.Click -= OnActionButtonClick;
            DetachInteractionHandlers(button);
            ButtonsPanel.Children.Remove(button);
        }
        _actionButtons.Clear();

        foreach (var entry in actions)
        {
            var button = CreateActionButton(entry, theme);
            _actionButtons.Add(button);
            ButtonsPanel.Children.Add(button);
        }

        ActionsDivider.Visibility = _actionButtons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button CreateActionButton(PresentedTextAction entry, ElementTheme theme)
    {
        var action = entry.Action;
        var badge = entry.IsPluginAction ? ServiceOriginHelper.BadgeText(entry.Origin) : null;
        var tooltip = string.IsNullOrEmpty(badge) ? action.Title : $"{action.Title} · {badge}";

        var button = new Button
        {
            Width = ButtonSize,
            Height = ButtonSize,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = TranslateButton.BorderBrush,
            BorderThickness = TranslateButton.BorderThickness,
            CornerRadius = TranslateButton.CornerRadius,
            Tag = action,
            Content = CreateActionContent(entry, theme)
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, $"PopButtonAction_{action.Id}");
        button.Click += OnActionButtonClick;
        AttachInteractionHandlers(button);
        return button;
    }

    private FrameworkElement CreateActionContent(PresentedTextAction entry, ElementTheme theme)
    {
        var host = new Grid { Width = 20, Height = 20 };
        var action = entry.Action;

        FrameworkElement icon;
        if (action.Type == TextActionType.RunService && !string.IsNullOrEmpty(action.ServiceId))
        {
            var image = new Image
            {
                Width = 16,
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            try
            {
                var bitmap = new BitmapImage(ServiceIconAssetResolver.GetIconUri(action.ServiceId, theme));
                bitmap.ImageFailed += (_, _) =>
                {
                    // No icon shipped for this service: fall back to a neutral glyph.
                    var index = host.Children.IndexOf(image);
                    if (index >= 0)
                    {
                        host.Children[index] = CreateGlyph("\uE8C1");
                    }
                };
                image.Source = bitmap;
                icon = image;
            }
            catch
            {
                icon = CreateGlyph("\uE8C1");
            }
        }
        else
        {
            icon = CreateGlyph(string.IsNullOrEmpty(action.IconGlyph) ? "\uE721" : action.IconGlyph);
        }

        host.Children.Add(icon);

        if (entry.IsPluginAction)
        {
            // Corner mark shared with the result card: identifies third-party plugin actions.
            host.Children.Add(new FontIcon
            {
                Glyph = "\uEA86",
                FontSize = 7,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -2, -1),
                Foreground = ThemeResourceService.GetBrush("PluginAccentBrush", RootGrid)
                    ?? GetPopButtonForeground()
            });
        }

        return host;
    }

    private FontIcon CreateGlyph(string glyph)
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetPopButtonForeground()
        };
    }

    private Microsoft.UI.Xaml.Media.Brush GetPopButtonForeground()
        => ThemeResourceService.GetBrush("PopButtonForegroundBrush", RootGrid)
            ?? new SolidColorBrush(Colors.Black);

    private void AttachInteractionHandlers(Button button)
    {
        button.PointerEntered += OnTranslateButtonPointerEntered;
        button.PointerExited += OnTranslateButtonPointerExited;
        button.PointerPressed += OnTranslateButtonPointerPressed;
        button.PointerReleased += OnTranslateButtonPointerReleased;
        button.PointerCanceled += OnTranslateButtonPointerCanceled;
        button.PointerCaptureLost += OnTranslateButtonPointerCaptureLost;
    }

    private void DetachInteractionHandlers(Button button)
    {
        button.PointerEntered -= OnTranslateButtonPointerEntered;
        button.PointerExited -= OnTranslateButtonPointerExited;
        button.PointerPressed -= OnTranslateButtonPointerPressed;
        button.PointerReleased -= OnTranslateButtonPointerReleased;
        button.PointerCanceled -= OnTranslateButtonPointerCanceled;
        button.PointerCaptureLost -= OnTranslateButtonPointerCaptureLost;
    }

    private void OnActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextAction action })
        {
            Debug.WriteLine($"[PopButton] Action button clicked: {action.Id}");
            OnActionClicked?.Invoke(action);
        }
    }

    /// <summary>
    /// PopupAnchor / DesktopPopupSiteBridge positioning path (WinAppSDK 2.x).
    ///
    /// TODO(WinAppSDK 2.0.1 PopupAnchor): re-architect this Window as a
    /// <c>DesktopPopupSiteBridge</c> hosted via <c>PopupAnchor</c> anchored to the
    /// source app's hwnd at the cursor offset. Benefits: relative-to-owner positioning
    /// (no manual DPI math), automatic multi-monitor edge-clamp, no SetWindowPos.
    ///
    /// The activation hardening (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST)
    /// stays — PopupAnchor positions, it does not control activation.
    ///
    /// Until that work lands and is verified, fall through to the Win32 path so the
    /// feature remains functional when the user opts in.
    /// </summary>
    private void ShowAtUsingPopupAnchor(int screenX, int screenY)
    {
        // Stub: PopupAnchor migration is tracked separately. Defer to Win32 for now so
        // the opt-in flag does not silently break selection translate.
        ShowAtUsingWin32(screenX, screenY);
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

        StopTrayIconMotion();

        // Reset interaction state when hiding to avoid stale visuals
        _isPointerOver = false;
        _isPressed = false;
        SetInteractionOpacity(CurrentBaseOpacity);

        Debug.WriteLine("[PopButton] Hidden");
    }

    /// <summary>
    /// Apply theme to the pop button window.
    /// </summary>
    public void ApplyTheme(ElementTheme theme, bool forceResourceRefresh = false)
    {
        if (this.Content is FrameworkElement root)
        {
            MinimalThemeService.ApplyRequestedTheme(root, theme, forceResourceRefresh);
        }

        SetInteractionOpacity(CurrentBaseOpacity);
        RefreshTrayIcon();
    }

    /// <summary>
    /// Update the pop button icon and tooltip to reflect the current query mode.
    /// </summary>
    public void UpdateMode(QueryMode mode)
    {
        var loc = LocalizationService.Instance;
        if (mode == QueryMode.GrammarCorrection)
        {
            ToolTipService.SetToolTip(TranslateButton,
                loc.GetString("TranslateButton_Grammar_Tooltip") ?? "Check Grammar");
        }
        else
        {
            ToolTipService.SetToolTip(TranslateButton,
                loc.GetString("TranslateTooltip") ?? "Translate");
        }
    }

    /// <summary>Synchronize the floating action icon with the selected tray artwork and taskbar theme.</summary>
    public void RefreshTrayIcon()
    {
        var iconId = TrayIconCatalog.Normalize(SettingsService.Instance.TrayIconId);
        var dark = ThemedIconService.IsTaskbarDark;
        var path = TrayIconCatalog.TryGetPreviewPath(iconId, dark)
            ?? TrayIconCatalog.ResolveExistingPath(iconId, dark)
            ?? TrayIconCatalog.TryGetPreviewPath(TrayIconCatalog.DefaultId, dark);
        if (!string.IsNullOrWhiteSpace(path) && !string.Equals(path, _loadedTrayIconPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                TrayIconImage.Source = new BitmapImage(new Uri(path, UriKind.Absolute));
                _loadedTrayIconPath = path;
            }
            catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
            {
                Debug.WriteLine($"[PopButton] Could not load selected tray artwork: {ex.Message}");
            }
        }

        EnsureTrayIconVisual();
        if (_isVisible) StartTrayIconBob();
    }

    private void EnsureTrayIconVisual()
    {
        if (_trayIconVisual is null && TrayIconImage.IsLoaded)
        {
            try
            {
                _trayIconVisual = ElementCompositionPreview.GetElementVisual(TrayIconImage);
                _trayIconVisual.CenterPoint = new Vector3(9.5f, 9.5f, 0);
            }
            catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
            {
                Debug.WriteLine($"[PopButton] Could not initialize icon motion: {ex.Message}");
            }
        }
    }

    private bool AnimationsEnabled => _uiSettings.AnimationsEnabled;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        => _dispatcherQueue.TryEnqueue(RefreshAnimationPreference);

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        StopTrayIconMotion();
        SystemBackdrop = null;
        _transparentBackdrop = null;
    }

    private void RefreshAnimationPreference()
    {
        if (!AnimationsEnabled)
        {
            StopTrayIconMotion();
            SetInteractionOpacity(_isPressed
                ? (RootGrid.ActualTheme == ElementTheme.Dark ? DarkPressedOpacity : PressedOpacity)
                : _isPointerOver
                    ? (RootGrid.ActualTheme == ElementTheme.Dark ? DarkHoverOpacity : HoverOpacity)
                    : CurrentBaseOpacity);
            return;
        }

        if (_isVisible) UpdateTrayIconMotion();
        UpdateInteractionVisuals();
    }

    private void StartTrayIconBob()
    {
        EnsureTrayIconVisual();
        if (!AnimationsEnabled || _trayIconVisual is null)
        {
            StopTrayIconMotion();
            return;
        }

        try
        {
            var animation = _trayIconVisual.Compositor.CreateVector3KeyFrameAnimation();
            var durationMs = _isPressed ? 1500 : _isPointerOver ? 1100 : 760;
            var distance = _isPressed ? -1.2f : _isPointerOver ? -1.8f : -2.5f;
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.Direction = AnimationDirection.Alternate;
            animation.InsertKeyFrame(0, Vector3.Zero);
            animation.InsertKeyFrame(1, new Vector3(0, distance, 0), CreateBobEase(_trayIconVisual.Compositor));
            _trayIconVisual.StartAnimation(nameof(Visual.Offset), animation);
        }
        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
        {
            Debug.WriteLine($"[PopButton] Could not start icon bob animation: {ex.Message}");
        }
    }

    private static CompositionEasingFunction CreateBobEase(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0), new Vector2(0.58f, 1));

    private void StopTrayIconMotion()
    {
        if (_trayIconVisual is null) return;
        _trayIconVisual.StopAnimation(nameof(Visual.Offset));
        _trayIconVisual.StopAnimation(nameof(Visual.Scale));
        _trayIconVisual.Offset = Vector3.Zero;
        _trayIconVisual.Scale = Vector3.One;
    }

    private void OnTranslateButtonClick(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PopButton] Translate button clicked");
        OnClicked?.Invoke();
    }

    private void OnTranslateButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateInteractionVisuals();
    }

    private void OnTranslateButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        _isPressed = false;
        UpdateInteractionVisuals();
    }

    private void OnTranslateButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = true;
        UpdateInteractionVisuals();
    }

    private void OnTranslateButtonPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = false;
        UpdateInteractionVisuals();
    }

    private void OnTranslateButtonPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = false;
        UpdateInteractionVisuals();
    }

    private void OnTranslateButtonPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = false;
        _isPointerOver = false;
        UpdateInteractionVisuals();
    }

    /// <summary>
    /// Update opacity and cursor based on current pointer state.
    /// </summary>
    private void UpdateInteractionVisuals()
    {
        UpdateTrayIconMotion();
        if (_isPressed)
        {
            SetInteractionOpacity(RootGrid.ActualTheme == ElementTheme.Dark ? DarkPressedOpacity : PressedOpacity);
            SetCursor(_handCursor);
        }
        else if (_isPointerOver)
        {
            SetInteractionOpacity(RootGrid.ActualTheme == ElementTheme.Dark ? DarkHoverOpacity : HoverOpacity);
            SetCursor(_handCursor);
        }
        else
        {
            SetInteractionOpacity(CurrentBaseOpacity);
            SetCursor(_arrowCursor);
        }
    }

    private void SetInteractionOpacity(double opacity)
    {
        var surface = ElementCompositionPreview.GetElementVisual(RootGrid);
        if (!AnimationsEnabled)
        {
            surface.StopAnimation(nameof(Visual.Opacity));
            RootGrid.Opacity = opacity;
            return;
        }

        try
        {
            var animation = surface.Compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(OpacityTransitionMs);
            animation.InsertKeyFrame(1, (float)opacity, CreateBobEase(surface.Compositor));
            surface.StartAnimation(nameof(Visual.Opacity), animation);
        }
        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
        {
            Debug.WriteLine($"[PopButton] Could not update interaction opacity: {ex.Message}");
        }
    }

    private void UpdateTrayIconMotion()
    {
        EnsureTrayIconVisual();
        if (!AnimationsEnabled || _trayIconVisual is null)
        {
            StopTrayIconMotion();
            return;
        }

        StartTrayIconBob();
    }
}
