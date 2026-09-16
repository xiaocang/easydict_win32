using System.Diagnostics;
using System.Runtime.InteropServices;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

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
    private bool _refitQueued;
    private OcrRect _anchorRect;
    private OcrRect _currentBounds;
    private HoverLookupFocusSession? _focusSession;
    private bool _hideAfterFailure;
    private DispatcherQueueTimer? _failureFeedbackTimer;
    private string? _focusFailureMessage;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    private bool AnimationsEnabled
    {
        get
        {
#if WINUI_TEST
            if (AnimationsEnabledForTest is { } enabled) return enabled;
#endif
            return _uiSettings.AnimationsEnabled;
        }
    }

#if WINUI_TEST
    internal bool? AnimationsEnabledForTest { get; set; }
#endif

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
        ContentPanel.SizeChanged += OnContentPanelSizeChanged;
        Closed += (_, _) =>
        {
            ResetPresentation();
            _isVisible = false;
        };
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
    /// Show the popup during word recognition. Each animation cycle checks the recognition outcome.
    /// </summary>
    public void ShowFocusing(OcrRect anchorRect)
    {
        if (_focusSession?.State == HoverLookupFocusState.Focusing) return;
        ResetPresentation();
        _focusSession = new HoverLookupFocusSession();
        PrepareLoading(string.Empty, anchorRect);
        FocusReticle.Visibility = Visibility.Visible;
        LoadingText.Text = LocalizationService.Instance.GetString("HoverLookupFocusing");
        FitAndPlace();
        if (AnimationsEnabled) FocusStoryboard.Begin();
    }

    /// <summary>Recognize success, finish the focus cycle and lock animation, then allow querying.</summary>
    public async Task ShowQueryingAsync(string word, OcrRect anchorRect)
    {
        if (_focusSession is { } session)
        {
            session.Resolve(true);
            if (!AnimationsEnabled) OnFocusCycleCompleted(this, EventArgs.Empty);
            if (!await session.QueryReady || !ReferenceEquals(_focusSession, session))
                throw new OperationCanceledException();
        }
        ShowLoading(word, anchorRect);
    }

    /// <summary>Query loading is distinct from recognizing/focusing.</summary>
    public void ShowLoading(string word, OcrRect anchorRect)
    {
        ResetPresentation();
        PrepareLoading(word, anchorRect);
        QueryProgress.Visibility = Visibility.Visible;
        QueryProgress.IsActive = true;
        LoadingText.Text = LocalizationService.Instance.GetString("HoverLookupLoading");
        FitAndPlace();
    }

    private void PrepareLoading(string word, OcrRect anchorRect)
    {
        _anchorRect = anchorRect;
        WordText.Text = word;
        HeaderGrid.Visibility = string.IsNullOrWhiteSpace(word) ? Visibility.Collapsed : Visibility.Visible;
        PhoneticText.Text = string.Empty;
        PhoneticText.Visibility = Visibility.Collapsed;
        BodyText.Text = string.Empty;
        BodyText.Visibility = Visibility.Collapsed;
        ServiceText.Text = string.Empty;
        ServiceText.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Show a lookup result.
    /// </summary>
    public void ShowContent(HoverLookupContent content, OcrRect anchorRect)
    {
        var wasLoading = LoadingPanel.Visibility == Visibility.Visible;
        ResetPresentation();
        _anchorRect = anchorRect;
        WordText.Text = content.Word;
        HeaderGrid.Visibility = Visibility.Visible;
        SetOptionalText(PhoneticText, content.Phonetics);
        LoadingPanel.Visibility = Visibility.Collapsed;
        SetOptionalText(BodyText, content.Body);
        SetOptionalText(ServiceText, content.ServiceName);
        FitAndPlace();
        if (wasLoading && AnimationsEnabled) ResultStoryboard.Begin();
    }

    /// <summary>
    /// Show a plain message (no result / error) for <paramref name="word"/>.
    /// </summary>
    public void ShowMessage(string word, string message, OcrRect anchorRect)
    {
        ResetPresentation();
        _anchorRect = anchorRect;
        WordText.Text = word;
        HeaderGrid.Visibility = Visibility.Visible;
        PhoneticText.Text = string.Empty;
        PhoneticText.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        BodyText.Text = string.Empty;
        BodyText.Visibility = Visibility.Collapsed;
        ServiceText.Text = string.Empty;
        ServiceText.Visibility = Visibility.Collapsed;
        PlayFailure(message, hideAfterAnimation: false);
    }

    /// <summary>Recognition failed. Finish the current cycle, then show failure before dismissing.</summary>
    public void FailFocus(string message)
    {
        if (_focusSession?.State != HoverLookupFocusState.Focusing) return;
        _focusFailureMessage = message;
        _focusSession.Resolve(false);
        if (!AnimationsEnabled) OnFocusCycleCompleted(this, EventArgs.Empty);
    }

    private void OnFocusCycleCompleted(object? sender, object e)
    {
        if (!_isVisible || _focusSession is null) return;
        switch (_focusSession.CompleteCycle())
        {
            case HoverLookupFocusState.Focusing:
                if (AnimationsEnabled) FocusStoryboard.Begin();
                break;
            case HoverLookupFocusState.Succeeded:
                FocusStoryboard.Stop();
                FocusReticle.Visibility = Visibility.Collapsed;
                StatusGlyph.Glyph = "\uE73E";
                StatusGlyph.Foreground = ThemeResourceService.GetBrush("SystemFillColorSuccessBrush", RootGrid);
                StatusGlyph.Visibility = Visibility.Visible;
                LoadingText.Text = LocalizationService.Instance.GetString("HoverLookupFocusSucceeded");
                FitAndPlace();
                if (AnimationsEnabled) SuccessStoryboard.Begin();
                else OnFocusSuccessCompleted(this, EventArgs.Empty);
                break;
            case HoverLookupFocusState.Failed:
                FocusStoryboard.Stop();
                PlayFailure(_focusFailureMessage ?? LocalizationService.Instance.GetString("HoverLookupNoWord"),
                    hideAfterAnimation: true);
                break;
        }
    }

    private void OnFocusSuccessCompleted(object? sender, object e) => _focusSession?.CompleteSuccess();

    private void PlayFailure(string message, bool hideAfterAnimation)
    {
        StopFailureFeedbackTimer();
        _hideAfterFailure = hideAfterAnimation;
        FocusReticle.Visibility = Visibility.Collapsed;
        QueryProgress.IsActive = false;
        QueryProgress.Visibility = Visibility.Collapsed;
        StatusGlyph.Glyph = "\uE711";
        StatusGlyph.Foreground = ThemeResourceService.GetBrush("SystemFillColorCriticalBrush", RootGrid);
        StatusGlyph.Visibility = Visibility.Visible;
        LoadingText.Text = message;
        FitAndPlace();
        if (AnimationsEnabled) FailureStoryboard.Begin();

        // Keep feedback readable for the same duration with or without motion.
        // Dismissal must not depend on a storyboard being enabled or completing.
        _failureFeedbackTimer = DispatcherQueue.CreateTimer();
        _failureFeedbackTimer.Interval = FailureStoryboard.Duration.TimeSpan;
        _failureFeedbackTimer.IsRepeating = false;
        _failureFeedbackTimer.Tick += OnFailureFeedbackElapsed;
        _failureFeedbackTimer.Start();
    }

    private void OnFailureFeedbackElapsed(DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(sender, _failureFeedbackTimer)) return;
        StopFailureFeedbackTimer();
        _focusSession?.CompleteFailure();
        if (_hideAfterFailure) HidePopup();
    }

    private void StopFailureFeedbackTimer()
    {
        if (_failureFeedbackTimer is not { } timer) return;
        _failureFeedbackTimer = null;
        timer.Stop();
        timer.Tick -= OnFailureFeedbackElapsed;
    }

    private void ResetPresentation()
    {
        StopFailureFeedbackTimer();
        FocusStoryboard.Stop();
        SuccessStoryboard.Stop();
        FailureStoryboard.Stop();
        ResultStoryboard.Stop();
        _focusSession?.Cancel();
        _focusSession = null;
        _focusFailureMessage = null;
        _hideAfterFailure = false;
        FocusReticle.Visibility = Visibility.Collapsed;
        StatusGlyph.Visibility = Visibility.Collapsed;
        QueryProgress.IsActive = false;
        QueryProgress.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Hide the popup (the window instance is kept for reuse).
    /// </summary>
    public void HidePopup()
    {
        ResetPresentation();
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
            if (_isVisible) FitAndPlace();
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

    private void OnContentPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isVisible || _refitQueued) return;

        // Actual wrapping can settle after the HWND resize or a DPI change. Refit on the next
        // dispatcher turn, never recursively from inside XAML layout.
        _refitQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _refitQueued = false;
            if (_isVisible) FitAndPlace();
        });
    }

    private void OnContentViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep oversized results inside the card's padding. The hover popup never scrolls.
        ContentViewport.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
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
        var frameWidth = 0;
        var frameHeight = 0;
        if (GetWindowRect(_hwnd, out var outer) && GetClientRect(_hwnd, out var client))
        {
            // SetWindowPos sizes the outer HWND, while XAML uses the client area. Even a
            // titleless non-resizable popup can retain a small native frame on Windows.
            var frameScale = scale / DpiHelper.GetScaleFactorForWindow(_hwnd);
            frameWidth = (int)Math.Ceiling(Math.Max(0, outer.Right - outer.Left - client.Right + client.Left) * frameScale);
            frameHeight = (int)Math.Ceiling(Math.Max(0, outer.Bottom - outer.Top - client.Bottom + client.Top) * frameScale);
        }

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

        var maxWidthDips = workArea.IsEmpty() ? MaxWidthDips : Math.Min(MaxWidthDips, (workArea.Width - frameWidth) / scale);
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
                var measuredWidth = Math.Min(maxWidthDips, Math.Ceiling(contentWidth + horizontalChrome));

                // Measure the inner content, not the window root: the root and clipped viewport
                // are constrained by the previous HWND height. Invalidate the header too, since
                // its children were just measured without wrapping during the width pass.
                HeaderGrid.InvalidateMeasure();
                ContentPanel.InvalidateMeasure();
                ContentPanel.Measure(new Windows.Foundation.Size(
                    Math.Max(1, measuredWidth - horizontalChrome), double.PositiveInfinity));
                var desired = ContentPanel.DesiredSize;
                if (desired.Width > 0 && desired.Height > 0)
                {
                    widthDips = measuredWidth;
                    heightDips = Math.Ceiling(desired.Height + RootGrid.Padding.Top + RootGrid.Padding.Bottom
                        + RootGrid.BorderThickness.Top + RootGrid.BorderThickness.Bottom);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HoverLookupWindow] Measure failed, using default size: {ex.Message}");
            }
        }

        // Round up so fractional DPI scaling cannot clip the last line or bottom border.
        var width = Math.Max(1, (int)Math.Ceiling(widthDips * scale) + frameWidth);
        var height = Math.Max(1, (int)Math.Ceiling(heightDips * scale) + frameHeight);
        if (!workArea.IsEmpty())
        {
            width = Math.Min(width, (int)workArea.Width);
            height = Math.Min(height, (int)workArea.Height);
        }

        var gap = (int)Math.Round(HoverLookupPlacement.GapPx * scale);
        var (x, y) = HoverLookupPlacement.Compute(_anchorRect, width, height, workArea, gap);

        var bounds = new OcrRect(x, y, width, height);
#if WINUI_TEST
        AutomationProperties.SetHelpText(BodyText,
            $"frame={frameWidth}x{frameHeight}; content desired={ContentPanel.DesiredSize}, actual={ContentPanel.ActualWidth}x{ContentPanel.ActualHeight}; " +
            $"viewport={ContentViewport.ActualWidth}x{ContentViewport.ActualHeight}; " +
            $"body desired={BodyText.DesiredSize}, actual={BodyText.ActualWidth}x{BodyText.ActualHeight}; " +
            $"service desired={ServiceText.DesiredSize}, actual={ServiceText.ActualWidth}x{ServiceText.ActualHeight}");
#endif
        if (_isVisible && _currentBounds == bounds) return;

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _currentBounds = bounds;
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
