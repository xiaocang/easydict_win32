using Easydict.DirectXaml.Layout;
using Easydict.DirectXaml.Render;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

using DxSize = Easydict.DirectXaml.Size;

namespace Easydict.DirectXaml.Win2D;

/// <summary>Raised when a pointer gesture reaches a node carrying a compiled action.</summary>
public sealed class DirectXamlActionEventArgs(string handler, int node) : EventArgs
{
    /// <summary>The handler name the XAML declared, e.g. <c>OnHeaderPointerPressed</c>.</summary>
    public string Handler { get; } = handler;

    public int Node { get; } = node;
}

/// <summary>
/// Hosts a <see cref="CompiledView"/> on a Win2D canvas.
///
/// This is the one <c>FrameworkElement</c> a direct-rendered card contributes to the visual tree —
/// the saving is the subtree beneath it, not the element itself.
/// </summary>
public sealed class DirectXamlCanvas : IDisposable
{
    private readonly CompiledView _view;
    private readonly CanvasControl _canvas;

    private Win2DTextMeasurerFactory? _measurers;
    private LayoutEngine? _layout;
    private double _laidOutWidth = -1;
    private double _contentHeight;
    private bool _disposed;

    public DirectXamlCanvas(CompiledView view)
    {
        _view = view;
        _canvas = new CanvasControl
        {
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top,
        };

        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += OnSizeChanged;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>The element to place in the visual tree.</summary>
    public FrameworkElement Element => _canvas;

    public CompiledView View => _view;

    public event EventHandler<DirectXamlActionEventArgs>? ActionInvoked;

    /// <summary>Raised when the host should re-resolve theme resources and hand them back.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Call after writing slots so the card re-lays out and repaints.</summary>
    public void Update()
    {
        EnsureLayout(_canvas.ActualWidth);
        ApplyContentHeight();
        _canvas.Invalidate();
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // Fires on first load and again after a lost device, so every cached device resource has
        // to be rebuilt here rather than in the constructor.
        _measurers?.Dispose();
        _measurers = new Win2DTextMeasurerFactory(sender);
        _layout = new LayoutEngine(_view, _measurers);

        _laidOutWidth = -1;
        _view.Invalidate(Invalidation.Measure | Invalidation.Arrange | Invalidation.Paint);
        Update();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_layout is null || _measurers is null)
        {
            return;
        }

        EnsureLayout(sender.ActualWidth);

        DisplayList displayList = DisplayListBuilder.Build(_layout);
        DisplayListExecutor.Execute(args.DrawingSession, displayList, _measurers);
        _view.MarkClean();

        if (Math.Abs(_canvas.Height - _contentHeight) > 0.5)
        {
            // Never mutate layout synchronously from inside a draw pass; queue it instead.
            _canvas.DispatcherQueue?.TryEnqueue(ApplyContentHeight);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - _laidOutWidth) <= 0.5)
        {
            return;
        }

        EnsureLayout(e.NewSize.Width);
        ApplyContentHeight();
        _canvas.Invalidate();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        ThemeChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Re-resolves resource-backed values after a theme switch.</summary>
    public void OnThemeResourcesChanged(Theming.IResourceResolver resources)
    {
        _view.OnThemeChanged(resources);
        _laidOutWidth = -1;
        Update();
    }

    private void EnsureLayout(double width)
    {
        if (_layout is null || width <= 0)
        {
            return;
        }

        bool widthChanged = Math.Abs(width - _laidOutWidth) > 0.5;
        if (!widthChanged && _view.Dirty == Invalidation.None)
        {
            return;
        }

        DxSize result = _layout.Layout(DxSize.FromWidth(width));
        _laidOutWidth = width;
        _contentHeight = result.Height;
    }

    private void ApplyContentHeight()
    {
        if (_disposed || Math.Abs(_canvas.Height - _contentHeight) <= 0.5)
        {
            return;
        }

        _canvas.Height = _contentHeight;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_layout is null)
        {
            return;
        }

        Windows.Foundation.Point position = e.GetCurrentPoint(_canvas).Position;
        int? node = _layout.HitTest(position.X, position.Y);

        // The pointer usually lands on a leaf, but the handler is declared further up — the card's
        // PointerPressed sits on the header Border, not on the text inside it. Walk up until one
        // of the ancestors owns the action, which is what routed events would have done.
        while (node is not null)
        {
            string? handler = _view.FindActionHandler(node.Value, "pointerPressed");
            if (handler is not null)
            {
                ActionInvoked?.Invoke(this, new DirectXamlActionEventArgs(handler, node.Value));
                e.Handled = true;
                return;
            }

            node = _view.ParentOf(node.Value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _canvas.CreateResources -= OnCreateResources;
        _canvas.Draw -= OnDraw;
        _canvas.SizeChanged -= OnSizeChanged;
        _canvas.PointerPressed -= OnPointerPressed;
        _canvas.ActualThemeChanged -= OnActualThemeChanged;

        _measurers?.Dispose();
        _measurers = null;
        _layout = null;

        // Releases the Win2D device resources the control is holding.
        _canvas.RemoveFromVisualTree();
    }
}
