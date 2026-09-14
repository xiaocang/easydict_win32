using System.Diagnostics;
using System.Runtime.InteropServices;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.TextActions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
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

    // Use GetWindowLongPtr/SetWindowLongPtr for 64-bit compatibility
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

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
    private bool _isVisible;

    // Strip geometry (logical pixels). Width = translate button + optional divider + action buttons.
    private const int ButtonSize = 30;
    private const int ItemSpacing = 2;
    private const int DividerWidth = 1;
    private readonly List<Button> _actionButtons = new();

    // Hover/pressed interaction state
    private const double BaseOpacity = 0.75;
    private const double DarkBaseOpacity = 0.94;
    private const double HoverOpacity = 1.0;
    private const double PressedOpacity = 0.85;

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
        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        exStyle = (IntPtr)((long)exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle);

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

    /// <summary>
    /// Show the pop button at the specified screen coordinates.
    /// The button appears offset from the given point (upper-right of selection end).
    /// </summary>
    /// <param name="screenX">Screen X coordinate of the selection end point</param>
    /// <param name="screenY">Screen Y coordinate of the selection end point</param>
    public void ShowAt(int screenX, int screenY)
    {
        // Reset interaction state when showing
        _isPointerOver = false;
        _isPressed = false;
        RootGrid.Opacity = CurrentBaseOpacity;

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
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _isVisible = true;

        Debug.WriteLine($"[PopButton] Shown at ({x}, {y}), size={physicalWidth}x{physicalHeight}, actions={_actionButtons.Count}, dpi={dpi}");
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
                    ?? ModeIcon.Foreground
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
            Foreground = ModeIcon.Foreground
        };
    }

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

        // Reset interaction state when hiding to avoid stale visuals
        _isPointerOver = false;
        _isPressed = false;
        RootGrid.Opacity = CurrentBaseOpacity;

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

        RootGrid.Opacity = CurrentBaseOpacity;
    }

    /// <summary>
    /// Update the pop button icon and tooltip to reflect the current query mode.
    /// </summary>
    public void UpdateMode(QueryMode mode)
    {
        var loc = LocalizationService.Instance;
        if (mode == QueryMode.GrammarCorrection)
        {
            ModeIcon.Glyph = "\uE70F";   // Edit/Pencil — represents correction
            ToolTipService.SetToolTip(TranslateButton,
                loc.GetString("TranslateButton_Grammar_Tooltip") ?? "Check Grammar");
        }
        else
        {
            ModeIcon.Glyph = "\uE8C1";   // Characters/Translate — original icon
            ToolTipService.SetToolTip(TranslateButton,
                loc.GetString("TranslateTooltip") ?? "Translate");
        }
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
        if (_isPressed)
        {
            // Pressed state: slightly less opaque than hover
            RootGrid.Opacity = PressedOpacity;
            SetCursor(_handCursor);
        }
        else if (_isPointerOver)
        {
            // Hover state: fully opaque with hand cursor
            RootGrid.Opacity = HoverOpacity;
            SetCursor(_handCursor);
        }
        else
        {
            // Default state: semi-transparent with arrow cursor
            RootGrid.Opacity = CurrentBaseOpacity;
            SetCursor(_arrowCursor);
        }
    }
}
