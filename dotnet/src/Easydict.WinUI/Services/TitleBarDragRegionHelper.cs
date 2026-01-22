using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Easydict.WinUI.Services;

/// <summary>
/// Helper class for managing custom title bar drag regions in unpackaged WinUI 3 apps.
/// SetTitleBar() doesn't work reliably in unpackaged mode, so we use
/// InputNonClientPointerSource.SetRegionRects() to define draggable and clickable regions.
/// </summary>
public sealed class TitleBarDragRegionHelper : IDisposable
{
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly FrameworkElement _titleBarRegion;
    private readonly FrameworkElement[] _passthroughElements;
    private readonly string _windowName;
    private FrameworkElement? _contentElement;
    private bool _isLoaded;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new TitleBarDragRegionHelper.
    /// </summary>
    /// <param name="window">The window to manage.</param>
    /// <param name="appWindow">The AppWindow instance.</param>
    /// <param name="titleBarRegion">The element to use as the draggable title bar region.</param>
    /// <param name="passthroughElements">Elements that should receive pointer input (buttons, etc.).</param>
    /// <param name="windowName">Name for debug logging.</param>
    public TitleBarDragRegionHelper(
        Window window,
        AppWindow appWindow,
        FrameworkElement titleBarRegion,
        FrameworkElement[] passthroughElements,
        string windowName = "Window")
    {
        _window = window;
        _appWindow = appWindow;
        _titleBarRegion = titleBarRegion;
        _passthroughElements = passthroughElements;
        _windowName = windowName;
    }

    /// <summary>
    /// Initializes the drag region helper by subscribing to content events.
    /// Call this after the window is constructed.
    /// </summary>
    public void Initialize()
    {
        if (_window.Content is FrameworkElement content)
        {
            _contentElement = content;
            content.Loaded += OnContentLoaded;
            content.SizeChanged += OnContentSizeChanged;
        }
    }

    /// <summary>
    /// Cleans up event handlers. Call this during window cleanup.
    /// </summary>
    public void Cleanup()
    {
        if (_contentElement != null)
        {
            _contentElement.Loaded -= OnContentLoaded;
            _contentElement.SizeChanged -= OnContentSizeChanged;
            _contentElement = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Cleanup();
    }

    private void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        UpdateDragRegions();
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isLoaded) UpdateDragRegions();
    }

    /// <summary>
    /// Updates the drag and passthrough regions.
    /// Can be called manually if the layout changes programmatically.
    /// </summary>
    public void UpdateDragRegions()
    {
        if (_isDisposed || !_isLoaded) return;

        try
        {
            var nonClientInputSrc = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            var scale = DpiHelper.GetScaleFactorForWindow(WindowNative.GetWindowHandle(_window));

            // Set the title bar region as the Caption (draggable) area
            if (_titleBarRegion.ActualWidth > 0 && _titleBarRegion.ActualHeight > 0)
            {
                var captionRect = GetScaledBoundsForElement(_titleBarRegion, scale);
                if (captionRect.Width > 0 && captionRect.Height > 0)
                {
                    nonClientInputSrc.SetRegionRects(NonClientRegionKind.Caption, new[] { captionRect });
                }
            }

            // Collect interactive controls that need passthrough
            var passthroughRects = new List<RectInt32>();

            foreach (var element in _passthroughElements)
            {
                if (element.ActualWidth > 0 && element.ActualHeight > 0)
                {
                    var rect = GetScaledBoundsForElement(element, scale);
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        passthroughRects.Add(rect);
                    }
                }
            }

            // Set the passthrough regions - these areas will be clickable instead of draggable
            if (passthroughRects.Count > 0)
            {
                nonClientInputSrc.SetRegionRects(NonClientRegionKind.Passthrough, passthroughRects.ToArray());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{_windowName}] UpdateDragRegions error: {ex.Message}");
        }
    }

    private RectInt32 GetScaledBoundsForElement(FrameworkElement element, double scale)
    {
        try
        {
            GeneralTransform transform = element.TransformToVisual(null);
            Rect bounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            return new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)
            );
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{_windowName}] GetScaledBoundsForElement error: {ex.Message}");
            return default;
        }
    }
}
