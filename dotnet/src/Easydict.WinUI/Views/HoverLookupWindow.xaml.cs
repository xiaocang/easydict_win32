using System.Diagnostics;
using System.Runtime.InteropServices;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Easydict.WinUI.Views;

/// <summary>
/// Small floating result card for hover word lookup: word, phonetics, meaning and an
/// "open in Mini window" button. Positioned next to the hovered word.
///
/// Key Win32 properties (same as <see cref="PopButtonWindow"/>):
/// - WS_EX_NOACTIVATE: never steals focus from the source application (Activate() is never called)
/// - WS_EX_TOOLWINDOW: does not appear in the taskbar
/// - WS_EX_TOPMOST: always on top
/// - WDA_EXCLUDEFROMCAPTURE: kept out of screen captures (our OCR fallback crop and screenshot overlay)
/// </summary>
public sealed partial class HoverLookupWindow : Window
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

    private const int SW_HIDE = 0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    /// <summary>Maximum popup width in DIPs (matches RootGrid.MaxWidth in XAML).</summary>
    public const double MaxWidthDips = 320;

    private const double DefaultWidthDips = 280;
    private const double DefaultHeightDips = 80;

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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private readonly IntPtr _hwnd;
    private readonly AppWindow? _appWindow;
    private bool _isVisible;
    private bool _isRootLoaded;
    private OcrRect _anchorRect;
    private OcrRect _currentBounds;

    /// <summary>
    /// Fired when the user clicks the "open in Mini window" button.
    /// </summary>
    public event Action? OpenInMiniWindowRequested;

    /// <summary>
    /// Fired after the popup has been (re)positioned or resized while visible.
    /// </summary>
    public event Action? BoundsChanged;

    /// <summary>
    /// Gets the window handle (HWND) of the popup.
    /// </summary>
    public IntPtr WindowHandle => _hwnd;

    /// <summary>
    /// Gets whether the popup is currently visible.
    /// </summary>
    public bool IsPopupVisible => _isVisible;

    /// <summary>
    /// Current popup bounds in physical pixels (empty when hidden).
    /// </summary>
    public OcrRect CurrentBounds => _isVisible ? _currentBounds : default;

    public HoverLookupWindow()
    {
        this.InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindowStyle();
        ApplyLocalization();

        RootGrid.Loaded += OnRootGridLoaded;
    }

    /// <summary>
    /// Configure Win32 window styles for a floating, non-activating popup.
    /// </summary>
    private void ConfigureWindowStyle()
    {
        if (_appWindow == null) return;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        exStyle = (IntPtr)((long)exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle);

        // Best effort: keep the popup out of screen captures so it never lands in the
        // OCR fallback crop (or the app's own screenshot overlay).
        if (!SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE))
        {
            Debug.WriteLine($"[HoverLookupWindow] SetWindowDisplayAffinity failed, error: {Marshal.GetLastWin32Error()}");
        }

        var scale = DpiHelper.GetScaleFactorForWindow(_hwnd);
        _appWindow.Resize(new SizeInt32(
            DpiHelper.DipsToPhysicalPixels(DefaultWidthDips, scale),
            DpiHelper.DipsToPhysicalPixels(DefaultHeightDips, scale)));

        ShowWindow(_hwnd, SW_HIDE);
        _isVisible = false;

        Debug.WriteLine("[HoverLookupWindow] Window configured with NOACTIVATE | TOOLWINDOW | TOPMOST");
    }

    /// <summary>
    /// Apply localized strings (loading text, button tooltip).
    /// </summary>
    public void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        LoadingText.Text = loc.GetString("HoverLookupLoading");
        var openInMini = loc.GetString("HoverLookupOpenInMiniWindow");
        ToolTipService.SetToolTip(OpenInMiniButton, openInMini);
        AutomationProperties.SetName(OpenInMiniButton, openInMini);
    }

    /// <summary>
    /// Show the popup for <paramref name="word"/> in its loading state, anchored to the word's screen rectangle.
    /// </summary>
    public void ShowLoading(string word, OcrRect anchorRect)
    {
        _anchorRect = anchorRect;
        WordText.Text = word;
        PhoneticText.Text = string.Empty;
        PhoneticText.Visibility = Visibility.Collapsed;
        BodyText.Text = string.Empty;
        BodyText.Visibility = Visibility.Collapsed;
        ServiceText.Text = string.Empty;
        ServiceText.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        FitAndPlace();
    }

    /// <summary>
    /// Show a lookup result.
    /// </summary>
    public void ShowContent(HoverLookupContent content, OcrRect anchorRect)
    {
        _anchorRect = anchorRect;
        WordText.Text = content.Word;
        SetOptionalText(PhoneticText, content.Phonetics);
        LoadingPanel.Visibility = Visibility.Collapsed;
        SetOptionalText(BodyText, content.Body);
        SetOptionalText(ServiceText, content.ServiceName);
        FitAndPlace();
    }

    /// <summary>
    /// Show a plain message (no result / error) for <paramref name="word"/>.
    /// </summary>
    public void ShowMessage(string word, string message, OcrRect anchorRect)
    {
        _anchorRect = anchorRect;
        WordText.Text = word;
        PhoneticText.Text = string.Empty;
        PhoneticText.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        SetOptionalText(BodyText, message);
        ServiceText.Text = string.Empty;
        ServiceText.Visibility = Visibility.Collapsed;
        FitAndPlace();
    }

    /// <summary>
    /// Hide the popup (the window instance is kept for reuse).
    /// </summary>
    public void HidePopup()
    {
        if (!_isVisible) return;

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        _isVisible = false;

        Debug.WriteLine("[HoverLookupWindow] Hidden");
    }

    /// <summary>
    /// Apply theme to the popup.
    /// </summary>
    public void ApplyTheme(ElementTheme theme, bool forceResourceRefresh = false)
    {
        if (this.Content is FrameworkElement root)
        {
            MinimalThemeService.ApplyRequestedTheme(root, theme, forceResourceRefresh);
        }
    }

    private static void SetOptionalText(TextBlock block, string? text)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        block.Text = hasText ? text!.Trim() : string.Empty;
        block.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        // Before the first show the XAML tree cannot be measured; once it is live, re-fit the
        // window to the real content size.
        _isRootLoaded = true;
        if (_isVisible)
        {
            FitAndPlace();
        }
    }

    /// <summary>
    /// Measure the content, size the window to it (DPI of the anchor's monitor) and place it
    /// next to the anchor word inside the work area. Never activates the window.
    /// </summary>
    private void FitAndPlace()
    {
        var anchorX = (int)Math.Round(_anchorRect.X);
        var anchorY = (int)Math.Round(_anchorRect.Y);
        var scale = DpiHelper.DpiToScaleFactor(DpiHelper.GetDpiForPoint(anchorX, anchorY));

        var widthDips = DefaultWidthDips;
        var heightDips = DefaultHeightDips;
        if (_isRootLoaded)
        {
            try
            {
                // Measure each row's natural width independently. The header's star column and
                // wrapping result must not constrain each other before we choose the popup width.
                var headerWidth = MeasureNaturalWidth(WordText)
                    + HeaderGrid.ColumnSpacing + MeasureNaturalWidth(OpenInMiniButton);
                var contentWidth = new[]
                {
                    headerWidth,
                    MeasureNaturalWidth(PhoneticText),
                    MeasureNaturalWidth(LoadingPanel),
                    MeasureNaturalWidth(BodyText),
                    MeasureNaturalWidth(ServiceText),
                }.Max();
                var horizontalChrome = RootGrid.Padding.Left + RootGrid.Padding.Right
                    + RootGrid.BorderThickness.Left + RootGrid.BorderThickness.Right;
                var measuredWidth = Math.Min(MaxWidthDips, Math.Ceiling(contentWidth + horizontalChrome));

                // Re-measure at the chosen width so both the source and result can wrap, and
                // derive the height from that final layout rather than the wider first pass.
                RootGrid.InvalidateMeasure();
                RootGrid.Measure(new Windows.Foundation.Size(measuredWidth, double.PositiveInfinity));
                var desired = RootGrid.DesiredSize;
                if (desired.Width > 0 && desired.Height > 0)
                {
                    widthDips = measuredWidth;
                    heightDips = Math.Ceiling(desired.Height);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HoverLookupWindow] Measure failed, using default size: {ex.Message}");
            }
        }

        var width = Math.Max(1, DpiHelper.DipsToPhysicalPixels(widthDips, scale));
        var height = Math.Max(1, DpiHelper.DipsToPhysicalPixels(heightDips, scale));

        OcrRect workArea = default;
        try
        {
            var display = DisplayArea.GetFromPoint(new PointInt32(anchorX, anchorY), DisplayAreaFallback.Nearest);
            var area = display.WorkArea;
            workArea = new OcrRect(area.X, area.Y, area.Width, area.Height);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HoverLookupWindow] Work area lookup failed, using unclamped position: {ex.Message}");
        }

        var gap = (int)Math.Round(HoverLookupPlacement.GapPx * scale);
        var (x, y) = HoverLookupPlacement.Compute(_anchorRect, width, height, workArea, gap);

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _currentBounds = new OcrRect(x, y, width, height);
        _isVisible = true;

        Debug.WriteLine($"[HoverLookupWindow] Shown at ({x}, {y}), size={width}x{height}, scale={scale}");
        BoundsChanged?.Invoke();
    }

    private static double MeasureNaturalWidth(FrameworkElement element)
    {
        if (element.Visibility == Visibility.Collapsed) return 0;

        element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private void OnOpenInMiniButtonClick(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[HoverLookupWindow] Open in Mini window clicked");
        OpenInMiniWindowRequested?.Invoke();
    }
}
