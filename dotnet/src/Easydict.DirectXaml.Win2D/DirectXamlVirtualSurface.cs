using System.Numerics;
using System.Runtime.InteropServices;
using Easydict.DirectXaml.Layout;
using Easydict.DirectXaml.Render;
using Easydict.DirectXaml.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

using WinHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinRect = Windows.Foundation.Rect;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using DxSize = Easydict.DirectXaml.Size;
using DxColor = Easydict.DirectXaml.Color;
using DxRect = Easydict.DirectXaml.Rect;

namespace Easydict.DirectXaml.Win2D;

/// <summary>
/// Hosts many compiled views on one virtualized Win2D surface.
/// </summary>
/// <remarks>
/// A normal <see cref="CanvasControl"/> allocates a backing surface for its entire size. Result
/// cards live inside an outer <see cref="ScrollViewer"/>, so this host uses
/// <see cref="CanvasVirtualControl"/>: its extent may be tall while its backing store stays tiled.
/// </remarks>
public sealed class DirectXamlVirtualSurface : IDisposable
{
    private const double EstimatedItemHeight = 72;
    private const double LoadingSpinnerSize = 8;
    private const int LoadingSpinnerFrameMilliseconds = 100;

    private readonly Grid _root;
    private readonly CanvasVirtualControl _canvas;
    private readonly Canvas _automationLayer;
    private readonly List<DirectXamlVirtualSurfaceItem> _items = [];

    private Win2DTextMeasurerFactory? _measurers;
    private double _laidOutWidth = -1;
    private double _contentHeight;
    private WinRect _visibleRegion;
    private bool _hasVisibleRegion;
    private bool _invalidateEntireSurface = true;
    private long _nextInvalidationGeneration;
    private bool _updateQueued;
    private bool _deferredLayoutQueued;
    private bool _hasDrawnFirstCard;
    private bool _resourcePrewarmStarted;
    private bool _isCanvasLoaded;
    private bool _disposed;
    private DirectXamlVirtualSurfaceItem? _pressedItem;
    private DirectXamlVirtualSurfaceItem? _priorityItem;
    private DispatcherQueueTimer? _loadingAnimationTimer;
    private bool _loadingAnimationTimerRunning;
    private int _loadingAnimationFrame;

    /// <summary>Creates one virtual drawing surface for a result list.</summary>
    public DirectXamlVirtualSurface()
    {
        _root = new Grid
        {
            HorizontalAlignment = WinHorizontalAlignment.Stretch,
            VerticalAlignment = WinVerticalAlignment.Top,
        };

        _canvas = new CanvasVirtualControl
        {
            HorizontalAlignment = WinHorizontalAlignment.Stretch,
            VerticalAlignment = WinVerticalAlignment.Top,
            ClearColor = Microsoft.UI.Colors.Transparent,
            UseSharedDevice = true,
        };
        _automationLayer = new Canvas
        {
            HorizontalAlignment = WinHorizontalAlignment.Stretch,
            VerticalAlignment = WinVerticalAlignment.Top,
            IsHitTestVisible = false,
        };

        _root.Children.Add(_canvas);
        _root.Children.Add(_automationLayer);

        AutomationProperties.SetAccessibilityView(_canvas, AccessibilityView.Raw);
        AutomationProperties.SetAutomationId(_canvas, "DirectResultsSurfaceCanvas");

        _root.SizeChanged += OnRootSizeChanged;
        _canvas.CreateResources += OnCreateResources;
        _canvas.Loaded += OnCanvasLoaded;
        _canvas.Unloaded += OnCanvasUnloaded;
        _canvas.RegionsInvalidated += OnRegionsInvalidated;
        _canvas.SizeChanged += OnSizeChanged;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCaptureLost += OnPointerCaptureLost;
        _canvas.ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>The one element that the results <see cref="ItemsControl"/> hosts.</summary>
    public FrameworkElement Element => _root;

    /// <summary>Number of cards registered with this surface.</summary>
    public int CardCount => _items.Count;

    /// <summary>Raised when the inherited WinUI theme changes.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Registers an independently stateful compiled card on this shared surface.</summary>
    public DirectXamlVirtualSurfaceItem Add(CompiledView view, FrameworkElement automationPeer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(automationPeer);
        var item = new DirectXamlVirtualSurfaceItem(this, view, automationPeer, EstimatedItemHeight);
        _items.Add(item);
        try
        {
            _automationLayer.Children.Add(automationPeer);
            Reflow();
            RequestUpdate();
            return item;
        }
        catch
        {
            _items.Remove(item);
            _automationLayer.Children.Remove(automationPeer);
            item.Detach();
            Reflow();
            throw;
        }
    }

    /// <summary>Reorders existing cards without allocating a new drawing surface.</summary>
    public void Reorder(IReadOnlyList<DirectXamlVirtualSurfaceItem> ordered)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ordered);

        if (ordered.Count != _items.Count
            || ordered.Any(item => !_items.Contains(item))
            || ordered.Distinct().Count() != ordered.Count)
        {
            throw new ArgumentException("The new order must contain every surface item exactly once.", nameof(ordered));
        }

        _items.Clear();
        _items.AddRange(ordered);
        Reflow();
        RequestUpdate();
    }

    internal void RequestUpdate()
    {
        if (_disposed)
        {
            return;
        }

        _invalidateEntireSurface = true;
        _priorityItem ??= FindFirstVisibleItem();
        RequestUpdateCore();
    }

    internal void RequestUpdate(DirectXamlVirtualSurfaceItem item, bool isUrgent = false)
    {
        if (_disposed)
        {
            return;
        }

        item.CaptureInvalidatedBounds(++_nextInvalidationGeneration);
        if (isUrgent)
        {
            _priorityItem = item;
        }
        else if (_priorityItem is null && IsVisible(item))
        {
            _priorityItem = item;
        }

        RequestUpdateCore();
    }

    internal bool TryEnqueueOnUiThread(Action action)
    {
        if (_disposed)
        {
            return false;
        }

        return _canvas.DispatcherQueue?.TryEnqueue(action.Invoke) == true;
    }

    private void RequestUpdateCore()
    {

        if (_updateQueued)
        {
            return;
        }

        _updateQueued = true;
        try
        {
            if (_canvas.DispatcherQueue?.TryEnqueue(
                    DispatcherQueuePriority.Normal,
                    () =>
                    {
                        _updateQueued = false;
                        LayoutAndInvalidate();
                    }) != true)
            {
                _updateQueued = false;
            }
        }
        catch (COMException)
        {
            // The DispatcherQueue can be released while a WinUI window is tearing down.
            _updateQueued = false;
        }
    }

    private void OnCreateResources(CanvasVirtualControl sender, CanvasCreateResourcesEventArgs args)
    {
        _measurers?.Dispose();
        _measurers = new Win2DTextMeasurerFactory(sender);
        _laidOutWidth = -1;
        _hasDrawnFirstCard = false;
        _resourcePrewarmStarted = false;
        StopLoadingAnimation();
        _priorityItem = FindFirstVisibleItem();

        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            item.ResetDeviceResources(_measurers);
        }

        RequestUpdate();
        QueueResourcePrewarm();
    }

    private void OnCanvasLoaded(object sender, RoutedEventArgs e)
    {
        _isCanvasLoaded = true;
        _invalidateEntireSurface = true;
        RequestUpdateCore();
        UpdateLoadingAnimation();
        QueueResourcePrewarm();
    }

    private void OnCanvasUnloaded(object sender, RoutedEventArgs e)
    {
        _isCanvasLoaded = false;
        StopLoadingAnimation();
    }



    private void OnRegionsInvalidated(
        CanvasVirtualControl sender,
        CanvasRegionsInvalidatedEventArgs args)
    {
        if (_disposed || !_isCanvasLoaded || _measurers is null)
        {
            return;
        }

        _visibleRegion = args.VisibleRegion;
        _hasVisibleRegion = true;
        _priorityItem ??= FindFirstVisibleItem();
        LayoutPriority(sender.ActualWidth);

        bool drewCard = false;
        foreach (WinRect region in args.InvalidatedRegions)
        {
            using CanvasDrawingSession session = sender.CreateDrawingSession(region);
            drewCard |= DrawRegion(session, region);
        }

        if (drewCard)
        {
            _hasDrawnFirstCard = true;
            QueueResourcePrewarm();
        }

        QueueDeferredLayout();

        UpdateLoadingAnimation();
    }

    private void QueueResourcePrewarm()
    {
        if (_resourcePrewarmStarted || _measurers is null || !_isCanvasLoaded)
        {
            return;
        }

        _resourcePrewarmStarted = true;
        if (_canvas.DispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (_disposed || _measurers is not { } measurers)
                    {
                        return;
                    }

                    foreach (DirectXamlVirtualSurfaceItem item in _items)
                    {
                        item.PrewarmTextResources(measurers);
                    }
                }) != true)
        {
            _resourcePrewarmStarted = false;
        }
    }

    private bool DrawRegion(CanvasDrawingSession session, WinRect region)
    {
        int index = FindFirstIntersecting(region.Y);
        double bottom = region.Y + region.Height;
        double right = region.X + region.Width;
        bool widthChanged = Math.Abs(_canvas.ActualWidth - _laidOutWidth) > 0.5;
        bool drewCard = false;

        for (; index < _items.Count; index++)
        {
            DirectXamlVirtualSurfaceItem item = _items[index];
            if (item.Top >= bottom)
            {
                break;
            }

            if (item.Top + item.Height <= region.Y || item.Width <= region.X || right <= 0)
            {
                continue;
            }

            // Never replay a stale display list over a card that still needs measurement or
            // arrange. The deferred layout pass owns those dirty bits and their geometry update.
            if (item.RequiresLayout(_canvas.ActualWidth, widthChanged))
            {
                continue;
            }

            DisplayList? displayList = item.GetDisplayList();
            if (displayList is null)
            {
                continue;
            }

            // Keep the full command stream here: replaying static and dynamic partitions separately
            // would reorder overlapping siblings and change opacity-group compositing.
            if (displayList.Commands.Count > 0)
            {
                DisplayListExecutor.Execute(
                    session,
                    displayList,
                    _measurers!,
                    new Vector2(0, (float)item.Top),
                    DisplayListLayer.All,
                    region);
            }
            DrawLoadingIndicator(session, item);
            item.NotifyDrawn();
            item.MarkCleanAfterDraw();
            item.ClearIssuedInvalidation();
            if (ReferenceEquals(_priorityItem, item) && !item.HasUnissuedInvalidation)
            {
                _priorityItem = null;
            }

            drewCard = true;
        }

        return drewCard;
    }

    private void DrawLoadingIndicator(
        CanvasDrawingSession session,
        DirectXamlVirtualSurfaceItem item)
    {
        if (!item.TryGetLoadingIndicatorBounds(out DxRect bounds))
        {
            return;
        }

        DxColor source = item.LoadingIndicatorColor;
        for (int segment = 0; segment < LoadingSpinnerGeometry.SegmentCount; segment++)
        {
            SpinnerDot dot = LoadingSpinnerGeometry.GetDot(
                bounds,
                _loadingAnimationFrame,
                segment);
            byte alpha = (byte)Math.Round(source.A * dot.Opacity);
            session.FillCircle(
                (float)dot.X,
                (float)(item.Top + dot.Y),
                (float)dot.Radius,
                DisplayListExecutor.ToWinColor(new DxColor(alpha, source.R, source.G, source.B)));
        }
    }

    private int FindFirstIntersecting(double top)
    {
        int low = 0;
        int high = _items.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            DirectXamlVirtualSurfaceItem item = _items[middle];
            if (item.Top + item.Height <= top)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - _laidOutWidth) > 0.5)
        {
            _laidOutWidth = -1;
            RequestUpdate();
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        if (_disposed ||
            width <= 0 ||
            (!double.IsNaN(_canvas.Width) && Math.Abs(_canvas.Width - width) <= 0.5))
        {
            return;
        }

        // CanvasVirtualControl can retain its previous desired width inside an ItemsControl.
        // Keep its tiled draw extent and the automation overlay aligned with the arranged host.
        _canvas.Width = width;
        _automationLayer.Width = width;
        _laidOutWidth = -1;
        RequestUpdate();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        ThemeChanged?.Invoke(this, EventArgs.Empty);

    private void LayoutAndInvalidate()
    {
        if (_disposed)
        {
            return;
        }

        double previousContentHeight = _contentHeight;
        LayoutPriority(_canvas.ActualWidth);

        if (_invalidateEntireSurface || _measurers is null || _canvas.ActualWidth <= 0)
        {
            _invalidateEntireSurface = false;
            MarkUnissuedInvalidationsIssued();
            _canvas.Invalidate();
            return;
        }

        double firstInvalidatedTop = double.PositiveInfinity;
        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            if (item.HasUnissuedInvalidation)
            {
                firstInvalidatedTop = Math.Min(firstInvalidatedTop, Math.Min(item.InvalidatedTop, item.Top));
            }
        }

        if (!double.IsPositiveInfinity(firstInvalidatedTop))
        {
            MarkUnissuedInvalidationsIssued();
            if (Math.Abs(previousContentHeight - _contentHeight) > 0.5)
            {
                // ponytail: CanvasVirtualControl updates its tiled extent on the next WinUI layout
                // pass, so a region that grows or shrinks that extent can be dropped. Keep a full
                // invalidation for that transition; stable-extent updates remain region-based.
                _canvas.Invalidate();
            }
            else
            {
                InvalidateTail(firstInvalidatedTop, previousContentHeight);
            }
        }

        QueueDeferredLayout();
    }


    private void InvalidateTail(double top, double previousContentHeight)
    {
        double bottom = Math.Max(previousContentHeight, _contentHeight);
        if (bottom > top)
        {
            _canvas.Invalidate(new WinRect(0, top, _canvas.ActualWidth, bottom - top));
        }
    }

    private void MarkUnissuedInvalidationsIssued()
    {
        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            item.MarkInvalidationIssued();
        }
    }

    private void LayoutPriority(double width)
    {
        if (_measurers is null || width <= 0)
        {
            Reflow();
            return;
        }

        DirectXamlVirtualSurfaceItem? item = _priorityItem;
        if (item is null)
        {
            return;
        }

        bool widthChanged = Math.Abs(width - _laidOutWidth) > 0.5;
        item.EnsureLayout(width, widthChanged);
        _laidOutWidth = width;
        Reflow();
    }

    private void QueueDeferredLayout()
    {
        if (_disposed
            || !_hasDrawnFirstCard
            || _deferredLayoutQueued
            || _measurers is null
            || _canvas.ActualWidth <= 0)
        {
            return;
        }

        bool widthChanged = Math.Abs(_canvas.ActualWidth - _laidOutWidth) > 0.5;
        if (FindNextDeferredLayoutItem(_canvas.ActualWidth, widthChanged) is null)
        {
            return;
        }

        _deferredLayoutQueued = true;
        // ponytail: one card per low-priority dispatch yields a first paint before the tail
        // competes for UI time; increase the batch only with measured evidence.
        if (_canvas.DispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    _deferredLayoutQueued = false;
                    LayoutDeferred();
                }) != true)
        {
            _deferredLayoutQueued = false;
        }
    }

    private void LayoutDeferred()
    {
        if (_disposed || _measurers is null || _canvas.ActualWidth <= 0)
        {
            return;
        }

        double width = _canvas.ActualWidth;
        bool widthChanged = Math.Abs(width - _laidOutWidth) > 0.5;
        DirectXamlVirtualSurfaceItem? item = FindNextDeferredLayoutItem(width, widthChanged);
        if (item is null)
        {
            return;
        }

        double previousContentHeight = _contentHeight;
        item.EnsureLayout(width, widthChanged);
        _laidOutWidth = width;
        Reflow();

        if (Math.Abs(previousContentHeight - _contentHeight) > 0.5)
        {
            _canvas.Invalidate();
        }
        else
        {
            InvalidateTail(item.Top, previousContentHeight);
        }

        QueueDeferredLayout();
    }

    private DirectXamlVirtualSurfaceItem? FindNextDeferredLayoutItem(double width, bool widthChanged)
    {
        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            if (IsVisible(item) && item.RequiresLayout(width, widthChanged))
            {
                return item;
            }
        }


        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            if (item.RequiresLayout(width, widthChanged))
            {
                return item;
            }
        }

        return null;
    }

    private DirectXamlVirtualSurfaceItem? FindFirstVisibleItem()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        double top = _hasVisibleRegion ? _visibleRegion.Y : 0;
        int index = FindFirstIntersecting(top);
        return index < _items.Count ? _items[index] : null;
    }

    private bool IsVisible(DirectXamlVirtualSurfaceItem item)
    {
        if (!_hasVisibleRegion || _visibleRegion.Height <= 0)
        {
            return true;
        }

        double bottom = _visibleRegion.Y + _visibleRegion.Height;
        return item.Top < bottom && item.Top + item.Height > _visibleRegion.Y;
    }
    private void OnLoadingIndicatorChanged() => UpdateLoadingAnimation();

    private void UpdateLoadingAnimation()
    {
        if (!_isCanvasLoaded || !HasVisibleLoadingIndicator())
        {
            StopLoadingAnimation();
            return;
        }

        DispatcherQueue? dispatcher = _canvas.DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        _loadingAnimationTimer ??= dispatcher.CreateTimer();
        if (_loadingAnimationTimerRunning)
        {
            return;
        }

        _loadingAnimationTimer.Interval = TimeSpan.FromMilliseconds(LoadingSpinnerFrameMilliseconds);
        _loadingAnimationTimer.IsRepeating = true;
        _loadingAnimationTimer.Tick += OnLoadingAnimationTick;
        _loadingAnimationTimer.Start();
        _loadingAnimationTimerRunning = true;
    }

    private bool HasVisibleLoadingIndicator()
    {
        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            if (IsVisible(item) && item.TryGetLoadingIndicatorBounds(out _))
            {
                return true;
            }
        }

        return false;
    }

    private void OnLoadingAnimationTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || !HasVisibleLoadingIndicator())
        {
            StopLoadingAnimation();
            return;
        }

        _loadingAnimationFrame = (_loadingAnimationFrame + 1) % LoadingSpinnerGeometry.SegmentCount;
        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            if (!IsVisible(item) || !item.TryGetLoadingIndicatorBounds(out DxRect bounds))
            {
                continue;
            }

            _canvas.Invalidate(new WinRect(
                bounds.X,
                item.Top + bounds.Y,
                bounds.Width,
                bounds.Height));
        }
    }

    private void StopLoadingAnimation()
    {
        if (!_loadingAnimationTimerRunning || _loadingAnimationTimer is null)
        {
            return;
        }

        _loadingAnimationTimer.Stop();
        _loadingAnimationTimer.Tick -= OnLoadingAnimationTick;
        _loadingAnimationTimerRunning = false;
    }

    private void Reflow()
    {
        double top = 0;
        double width = Math.Max(0, _canvas.ActualWidth);
        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            item.Top = top;
            top += item.Height;
            Canvas.SetLeft(item.AutomationPeer, 0);
            Canvas.SetTop(item.AutomationPeer, item.Top);
            item.AutomationPeer.Width = width;
            item.AutomationPeer.Height = item.Height;
        }

        _contentHeight = top;
        if (Math.Abs(_root.Height - _contentHeight) <= 0.5)
        {
            return;
        }

        _root.Height = _contentHeight;
        _canvas.Height = _contentHeight;
        _automationLayer.Height = _contentHeight;
    }

    private void EnsureLayoutForInput(DirectXamlVirtualSurfaceItem item)
    {
        if (_measurers is null || _canvas.ActualWidth <= 0)
        {
            return;
        }

        double width = _canvas.ActualWidth;
        bool widthChanged = Math.Abs(width - _laidOutWidth) > 0.5;
        if (!item.RequiresLayout(width, widthChanged))
        {
            return;
        }

        double previousContentHeight = _contentHeight;
        item.EnsureLayout(width, widthChanged);
        _laidOutWidth = width;
        Reflow();
        if (Math.Abs(previousContentHeight - _contentHeight) > 0.5)
        {
            _canvas.Invalidate();
        }
        else
        {
            InvalidateTail(item.Top, previousContentHeight);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point position = e.GetCurrentPoint(_canvas).Position;
        DirectXamlVirtualSurfaceItem? item = FindAt(position.X, position.Y);
        if (item is null)
        {
            return;
        }

        EnsureLayoutForInput(item);
        if (!item.Press(position.X, position.Y - item.Top))
        {
            return;
        }

        _pressedItem = item;
        if (item.HasPendingClick)
        {
            _canvas.CapturePointer(e.Pointer);
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        DirectXamlVirtualSurfaceItem? item = _pressedItem;
        _pressedItem = null;
        if (item is null)
        {
            return;
        }

        Windows.Foundation.Point position = e.GetCurrentPoint(_canvas).Position;
        bool handled = item.Release(position.X, position.Y - item.Top);
        _canvas.ReleasePointerCapture(e.Pointer);
        e.Handled = handled;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _pressedItem?.CancelPointer();
        _pressedItem = null;
    }

    private DirectXamlVirtualSurfaceItem? FindAt(double x, double y)
    {
        int low = 0;
        int high = _items.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_items[middle].Top <= y)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low == 0)
        {
            return null;
        }

        DirectXamlVirtualSurfaceItem item = _items[low - 1];
        return x >= 0 && x <= item.Width && y >= item.Top && y <= item.Top + item.Height
            ? item
            : null;
    }

    private void Remove(DirectXamlVirtualSurfaceItem item)
    {
        if (_disposed || !_items.Remove(item))
        {
            return;
        }

        if (ReferenceEquals(_pressedItem, item))
        {
            _pressedItem = null;
        }
        if (ReferenceEquals(_priorityItem, item))
        {
            _priorityItem = null;
        }
        UpdateLoadingAnimation();



        _automationLayer.Children.Remove(item.AutomationPeer);
        Reflow();
        RequestUpdate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopLoadingAnimation();
        _loadingAnimationTimer = null;

        _root.SizeChanged -= OnRootSizeChanged;
        _canvas.CreateResources -= OnCreateResources;
        _canvas.Loaded -= OnCanvasLoaded;
        _canvas.Unloaded -= OnCanvasUnloaded;
        _canvas.RegionsInvalidated -= OnRegionsInvalidated;
        _canvas.SizeChanged -= OnSizeChanged;
        _canvas.PointerPressed -= OnPointerPressed;
        _canvas.PointerReleased -= OnPointerReleased;
        _canvas.PointerCaptureLost -= OnPointerCaptureLost;
        _canvas.ActualThemeChanged -= OnActualThemeChanged;

        foreach (DirectXamlVirtualSurfaceItem item in _items)
        {
            item.Detach();
        }

        _items.Clear();
        _automationLayer.Children.Clear();
        try
        {
            _canvas.RemoveFromVisualTree();
        }
        catch (COMException)
        {
            // Window teardown can release the Win2D control before the host does.
        }
        DirectRendererTelemetry.Flush(force: true);
        _measurers?.Dispose();
        _measurers = null;
    }

    /// <summary>One independently stateful card registered with a shared virtual surface.</summary>
    public sealed class DirectXamlVirtualSurfaceItem : IDisposable
    {
        private readonly DirectXamlVirtualSurface _owner;
        private readonly PointerActionRouter _actions;
        private DisplayList? _displayList;
        private bool _displayListNeedsRebuild;
        private double _laidOutWidth = -1;
        private double _invalidatedTop;
        private long _invalidationGeneration;
        private long _issuedInvalidationGeneration;
        private bool _hasPendingInvalidation;
        private bool _hasLayout;
        private bool _disposed;
        private bool _isLoadingIndicatorVisible;
        private int _loadingIndicatorAnchorNode = -1;
        private DxColor _loadingIndicatorColor;

        internal DirectXamlVirtualSurfaceItem(
            DirectXamlVirtualSurface owner,
            CompiledView view,
            FrameworkElement automationPeer,
            double estimatedHeight)
        {
            _owner = owner;
            View = view;
            AutomationPeer = automationPeer;
            Height = estimatedHeight;
            _actions = new PointerActionRouter(view);
            View.ConfigureUiDispatcher(EnqueueOnUiThread);
            View.Changed += OnViewChanged;
            _actions.ActionInvoked += OnActionInvoked;
        }

        internal CompiledView View { get; }

        internal FrameworkElement AutomationPeer { get; }

        internal LayoutEngine? Layout { get; private set; }

        internal double Top { get; set; }

        internal double Height { get; private set; }

        internal double Width { get; private set; }

        internal bool HasLayout => _hasLayout;

        internal bool HasPendingClick => _actions.HasPendingClick;

        /// <summary>Raised when this card invokes one of its compiled actions.</summary>
        public event EventHandler<DirectXamlActionEventArgs>? ActionInvoked;

        /// <summary>Raised after this card's current display list has been replayed to Win2D.</summary>
        public event EventHandler? Drawn;

        /// <summary>Schedules layout and redraw after slot values change.</summary>
        public void Update(bool isUrgent = false)
        {
            if (!_disposed)
            {
                _owner.RequestUpdate(this, isUrgent);
            }
        }

        /// <summary>Sets the animated loading indicator anchored beside a compiled status node.</summary>
        public void SetLoadingIndicator(bool isVisible, int anchorNode, DxColor color)
        {
            if (_disposed
                || (_isLoadingIndicatorVisible == isVisible
                    && _loadingIndicatorAnchorNode == anchorNode
                    && _loadingIndicatorColor == color))
            {
                return;
            }

            _isLoadingIndicatorVisible = isVisible;
            _loadingIndicatorAnchorNode = anchorNode;
            _loadingIndicatorColor = color;
            _owner.RequestUpdate(this);
            _owner.OnLoadingIndicatorChanged();
        }


        internal void PrewarmTextResources(Win2DTextMeasurerFactory measurers)
        {
            for (int node = 0; node < View.NodeCount; node++)
            {
                if (View.KindOf(node) is not (NodeKind.TextBlock or NodeKind.Button))
                {
                    continue;
                }

                measurers.Prewarm(new FontSpec(
                    View.GetDouble(node, PropertyNames.FontSize, LayoutEngine.DefaultFontSize),
                    View.GetEnum(node, PropertyNames.FontWeight, FontWeight.Normal)));
            }
        }

        internal void ResetDeviceResources(Win2DTextMeasurerFactory measurers)
        {
            _displayList = null;
            _laidOutWidth = -1;
            Layout = new LayoutEngine(View, measurers);
            _hasLayout = false;
            View.Invalidate(Invalidation.Measure | Invalidation.Arrange | Invalidation.Paint);
        }

        internal bool RequiresLayout(double width, bool widthChanged) =>
            Layout is not null
            && (widthChanged
                || Math.Abs(width - _laidOutWidth) > 0.5
                || (View.Dirty & (Invalidation.Measure | Invalidation.Arrange)) != Invalidation.None);

        internal void EnsureLayout(double width, bool widthChanged)
        {
            if (Layout is null)
            {
                return;
            }

            if (!RequiresLayout(width, widthChanged))
            {
                return;
            }

            using DirectRendererTelemetry.Scope telemetry =
                DirectRendererTelemetry.Measure("layout", View.NodeCount);
            DxSize size = Layout.Layout(DxSize.FromWidth(width));
            // Display-list geometry is derived from layout bounds, including width-only relayouts.
            _displayListNeedsRebuild = true;

            _laidOutWidth = width;
            Width = width;
            Height = size.Height;
            View.MarkLayoutClean();
            _hasLayout = true;
        }

        internal DisplayList? GetDisplayList()
        {
            if (Layout is null || !_hasLayout)
            {
                return null;
            }

            if (_displayList is null
                || _displayListNeedsRebuild
                || (View.Dirty & Invalidation.Paint) != Invalidation.None)
            {
                using DirectRendererTelemetry.Scope telemetry =
                    DirectRendererTelemetry.Measure("display-list", View.NodeCount);
                _displayList = DisplayListBuilder.Build(Layout, _displayList);
                _displayListNeedsRebuild = false;
            }

            return _displayList;
        }

        internal DxColor LoadingIndicatorColor => _loadingIndicatorColor;

        internal bool TryGetLoadingIndicatorBounds(out DxRect bounds)
        {
            if (!_isLoadingIndicatorVisible
                || !_hasLayout
                || Layout is null
                || _loadingIndicatorAnchorNode < 0)
            {
                bounds = DxRect.Empty;
                return false;
            }

            DxRect anchor = Layout.BoundsOf(_loadingIndicatorAnchorNode);
            double size = Math.Min(LoadingSpinnerSize, anchor.Height);
            double x = anchor.X - size;
            if (anchor.IsEmpty || size <= 0 || x < 0)
            {
                bounds = DxRect.Empty;
                return false;
            }

            bounds = new DxRect(x, anchor.Y + ((anchor.Height - size) / 2), size, size);
            return true;
        }

        internal void MarkCleanAfterDraw() => View.MarkClean();

        internal void NotifyDrawn() => Drawn?.Invoke(this, EventArgs.Empty);

        internal bool HasUnissuedInvalidation =>
            _hasPendingInvalidation
            && _issuedInvalidationGeneration != _invalidationGeneration;

        internal double InvalidatedTop => _invalidatedTop;

        internal void CaptureInvalidatedBounds(long generation)
        {
            if (HasUnissuedInvalidation)
            {
                return;
            }

            _invalidatedTop = Top;
            _invalidationGeneration = generation;
            _issuedInvalidationGeneration = 0;
            _hasPendingInvalidation = true;
        }

        internal void MarkInvalidationIssued()
        {
            if (HasUnissuedInvalidation)
            {
                _issuedInvalidationGeneration = _invalidationGeneration;
            }
        }

        internal void ClearIssuedInvalidation()
        {
            if (_hasPendingInvalidation
                && _issuedInvalidationGeneration == _invalidationGeneration)
            {
                _hasPendingInvalidation = false;
            }
        }

        internal bool Press(double x, double y) =>
            Layout is not null && _hasLayout && _actions.Press(Layout, x, y);

        internal bool Release(double x, double y) =>
            Layout is not null && _hasLayout && _actions.Release(Layout, x, y);

        internal void CancelPointer() => _actions.Cancel();

        private void OnActionInvoked(int node, string handler) =>
            ActionInvoked?.Invoke(this, new DirectXamlActionEventArgs(handler, node));



        private bool EnqueueOnUiThread(Action action)
        {
            if (_disposed)
            {
                return false;
            }

            return _owner.TryEnqueueOnUiThread(action);
        }

        private void OnViewChanged(object? sender, EventArgs e)
        {
            if (!_disposed)
            {
                _ = EnqueueOnUiThread(() => Update());
            }
        }

        internal void Detach()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            View.Changed -= OnViewChanged;
            View.ClearUiDispatcher();
            _actions.ActionInvoked -= OnActionInvoked;
            _actions.Cancel();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Detach();
            _owner.Remove(this);
        }
    }
}
