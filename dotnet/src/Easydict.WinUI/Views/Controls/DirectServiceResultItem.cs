using System.ComponentModel;
using System.Reflection;
using Easydict.DirectXaml;
using Easydict.DirectXaml.Ir;
using Easydict.DirectXaml.Win2D;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using DxColor = Easydict.DirectXaml.Color;
using DxVisibility = Easydict.DirectXaml.Visibility;
using WinHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// A translation-result card rendered directly onto a Win2D canvas from compiled IR, instead of a
/// <c>FrameworkElement</c> tree.
///
/// This is the third implementation of <see cref="IServiceResultView"/>; the host already switches
/// between renderers, so nothing above this class changes. Structure comes from
/// <c>MinimalServiceResultItem.xaml</c> compiled by <c>dxamlc</c>, and the update logic below is a
/// direct port of <c>MinimalServiceResultItem.UpdateUI</c> with each
/// <c>Element.Property = value</c> rewritten as a slot write.
/// </summary>
internal sealed partial class DirectServiceResultItem : IServiceResultView, IDisposable
{
    /// <remarks>
    /// Default manifest name for <c>Views/Controls/MinimalServiceResultItem.dxir.json</c> marked
    /// as an EmbeddedResource.
    /// </remarks>
    private const string IrResourceName =
        "Easydict.WinUI.Views.Controls.MinimalServiceResultItem.dxir.json";

    private static readonly Lazy<IrDocument> _ir = new(
        () => IrLoader.LoadFromResource(typeof(DirectServiceResultItem).Assembly, IrResourceName));

    private readonly DirectXamlVirtualSurface _surface;
    private readonly DirectXamlVirtualSurface.DirectXamlVirtualSurfaceItem _surfaceItem;
    private readonly Grid _root;
    private readonly Border _header;
    private readonly CompiledView _view;
    private readonly MinimalServiceResultItemDirectBindings _bindings;
    private readonly ThemeResourceResolver _resources;

    private ServiceQueryResult? _serviceResult;
    private bool _updatePending;
    private bool _awaitingBenchmarkPaint;
    private bool _disposed;

    public DirectServiceResultItem(
        DirectXamlVirtualSurface surface,
        FrameworkElement? themeRoot)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _resources = new ThemeResourceResolver(themeRoot);
        _view = new CompiledView(_ir.Value, _resources);
        _bindings = new MinimalServiceResultItemDirectBindings(_view);

        // The physical peers preserve existing AutomationId-based callers while the card itself
        // is painted once by the shared virtual surface. They are transparent and do not receive
        // input, so pointer routing remains in the Win2D surface.
        _root = new Grid { IsHitTestVisible = false };
        _header = new Border
        {
            HorizontalAlignment = WinHorizontalAlignment.Stretch,
            VerticalAlignment = WinVerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };
        _root.Children.Add(_header);
        AutomationProperties.SetAccessibilityView(_root, AccessibilityView.Control);
        AutomationProperties.SetAccessibilityView(_header, AccessibilityView.Control);

        _surfaceItem = _surface.Add(_view, _root);
        try
        {
            _surfaceItem.ActionInvoked += OnActionInvoked;
            _surfaceItem.Drawn += OnSurfaceItemDrawn;
            _surface.ThemeChanged += OnSurfaceThemeChanged;

            _bindings.SetPendingQueryTextText(ServiceResultStatusTextProvider.GetPendingQueryHintText());
            _bindings.SetCopyButtonContent(LocalizationService.Instance.GetString("Copy"));
        }
        catch
        {
            _surfaceItem.ActionInvoked -= OnActionInvoked;
            _surfaceItem.Drawn -= OnSurfaceItemDrawn;
            _surface.ThemeChanged -= OnSurfaceThemeChanged;
            _surfaceItem.Dispose();
            throw;
        }
    }

    /// <summary>True when the compiled IR is present and loadable in this build.</summary>
    public static bool IsAvailable
    {
        get
        {
            Assembly assembly = typeof(DirectServiceResultItem).Assembly;
            return assembly.GetManifestResourceInfo(IrResourceName) is not null;
        }
    }

    internal DirectXamlVirtualSurface Surface => _surface;

    internal DirectXamlVirtualSurface.DirectXamlVirtualSurfaceItem SurfaceItem => _surfaceItem;

    public FrameworkElement Element => _root;

    public FrameworkElement HeaderPanel => _header;

    public FrameworkElement? ActionButtonsPanel => null;

    public bool IsMinimalRenderer => true;

    public FrameworkElement? ThemeRoot
    {
        get => _resources.ThemeRoot;
        set
        {
            _resources.ThemeRoot = value;
            RefreshThemeResources();
        }
    }

    public HashSet<string>? AlreadyShownPhonetics { get; set; }

    public event EventHandler<ServiceQueryResult>? CollapseToggled;

    public event EventHandler<ServiceQueryResult>? QueryRequested;

    event EventHandler<ServiceQueryResult>? IServiceResultView.FoundryLocalStartRequested
    {
        add { }
        remove { }
    }

    public ServiceQueryResult? ServiceResult
    {
        get => _serviceResult;
        set
        {
            if (_serviceResult is not null)
            {
                _serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;
            }

            _serviceResult = value;

            if (_serviceResult is not null)
            {
                _serviceResult.PropertyChanged += OnServiceResultPropertyChanged;
            }

            UpdateUI();
        }
    }

    public void RefreshDemotionState() => QueueUpdateUI();

    public void RefreshThemeChrome() => RefreshThemeResources();

    public void ApplyAppearance(AppearanceSettings settings)
    {
        _bindings.SetServiceNameTextFontSize(settings.ServiceNameFontSize);
        _bindings.SetStatusTextFontSize(settings.StatusFontSize);
        _bindings.SetResultTextFontSize(settings.ResultFontSize);
        _surfaceItem.Update();
    }

    public IEnumerable<string> GetDisplayedPhoneticKeys() => Array.Empty<string>();

    public void Cleanup()
    {
        if (_serviceResult is not null)
        {
            _serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;
        }

        _serviceResult = null;
        _updatePending = false;
        _awaitingBenchmarkPaint = false;

        _bindings.SetServiceNameTextText(string.Empty);
        _bindings.SetStatusTextText(string.Empty);
        _bindings.SetResultTextText(string.Empty);
        _bindings.SetErrorTextText(string.Empty);
        _bindings.SetContentAreaVisibility(DxVisibility.Collapsed);
        _bindings.SetCopyButtonVisibility(DxVisibility.Collapsed);
        UpdateLoadingIndicator();
        _surfaceItem.Update();
    }

    private void OnServiceResultPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        QueueUpdateUI();

    private void QueueUpdateUI()
    {
        if (_updatePending)
        {
            return;
        }

        _updatePending = true;
        if (!_root.DispatcherQueue.TryEnqueue(() =>
            {
                _updatePending = false;
                UpdateUI();
            }))
        {
            _updatePending = false;
        }
    }

    /// <summary>
    /// Port of <c>MinimalServiceResultItem.UpdateUI</c>. Every property written here is declared
    /// mutable on its slot by the compiler, which the compiler's own
    /// <c>covers_everything_update_ui_writes</c> test pins.
    /// </summary>
    private void UpdateUI()
    {
        using var hotspot = UiThreadHotspotDiagnostics.Measure("DirectServiceResultItem.UpdateUI");

        if (_serviceResult is null)
        {
            return;
        }

        bool demoted = ServiceResultDemotionHelper.IsDemoted(_serviceResult);
        if (demoted)
        {
            _serviceResult.IsExpanded = false;
        }
        else if ((_serviceResult.HasResult || _serviceResult.HasError || _serviceResult.IsStreaming)
                 && !_serviceResult.IsExpanded)
        {
            _serviceResult.IsExpanded = true;
        }

        _bindings.SetRootBorderOpacity(demoted ? 0.5 : 1.0);
        _bindings.SetServiceNameTextText(_serviceResult.ServiceDisplayName);

        string status = ServiceResultStatusTextProvider.GetStatusText(_serviceResult);
        _bindings.SetStatusTextText(status);
        _bindings.SetStatusTextVisibility(
            string.IsNullOrWhiteSpace(status) ? DxVisibility.Collapsed : DxVisibility.Visible);

        bool showPendingHint = !demoted && _serviceResult.ShowPendingQueryHint;
        _bindings.SetPendingQueryTextVisibility(
            showPendingHint ? DxVisibility.Visible : DxVisibility.Collapsed);

        var resultVisibility = DxVisibility.Collapsed;
        var errorVisibility = DxVisibility.Collapsed;

        if (!demoted)
        {
            if (_serviceResult.HasError && !_serviceResult.IsLoading)
            {
                _bindings.SetErrorTextText(
                    _serviceResult.Error?.Message ?? ServiceResultStatusTextProvider.GetErrorFallbackText());
                errorVisibility = DxVisibility.Visible;
            }
            else if (_serviceResult.IsStreaming)
            {
                string displayText = _serviceResult.DisplayText;
                _bindings.SetResultTextText(
                    string.IsNullOrWhiteSpace(displayText)
                        ? ServiceResultStatusTextProvider.GetWaitingForResponseText()
                        : displayText);
                ApplyResultForeground(isInfoResult: false);
                resultVisibility = DxVisibility.Visible;
            }
            else if (_serviceResult.HasResult)
            {
                string displayText = MinimalServiceResultItem.GetMinimalDisplayText(_serviceResult);
                if (!string.IsNullOrWhiteSpace(displayText))
                {
                    _bindings.SetResultTextText(displayText);
                    ApplyResultForeground(_serviceResult.IsInfoResult);
                    resultVisibility = DxVisibility.Visible;
                }
            }
        }

        _bindings.SetResultTextVisibility(resultVisibility);
        _bindings.SetErrorTextVisibility(errorVisibility);
        _bindings.SetCopyButtonVisibility(resultVisibility);

        bool hasVisibleContent = showPendingHint
            || resultVisibility == DxVisibility.Visible
            || errorVisibility == DxVisibility.Visible;
        _bindings.SetContentAreaVisibility(
            hasVisibleContent ? DxVisibility.Visible : DxVisibility.Collapsed);

        if (resultVisibility == DxVisibility.Visible
            && RendererBenchmarkTelemetry.IsFirstResultPending)
        {
            _awaitingBenchmarkPaint = true;
        }

        UpdateLoadingIndicator();
        _surfaceItem.Update(_serviceResult.HasError);
    }

    private void UpdateLoadingIndicator()
    {
        DxColor color = _resources.TryGetColor(
            "ServiceResultHeaderSecondaryForegroundBrush",
            out DxColor resolvedColor)
            ? resolvedColor
            : new DxColor(255, 128, 128, 128);
        _surfaceItem.SetLoadingIndicator(
            _serviceResult?.IsLoading == true,
            MinimalServiceResultItemDirectBindings.StatusTextNode,
            color);
    }

    private void ApplyResultForeground(bool isInfoResult)
    {
        string key = isInfoResult ? "TextFillColorSecondaryBrush" : "QueryTextBrush";
        if (_resources.TryGetColor(key, out DxColor color))
        {
            _bindings.SetResultTextForeground(color);
        }
    }

    private void OnSurfaceThemeChanged(object? sender, EventArgs e) => RefreshThemeResources();

    private void RefreshThemeResources()
    {
        if (_disposed)
        {
            return;
        }

        _resources.Invalidate();
        _view.OnThemeChanged(_resources);
        UpdateLoadingIndicator();
        _surfaceItem.Update();
    }

    private void OnSurfaceItemDrawn(object? sender, EventArgs e)
    {
        if (!_awaitingBenchmarkPaint)
        {
            return;
        }

        _awaitingBenchmarkPaint = false;
        RendererBenchmarkTelemetry.ReportDirectFirstResultDrawn();
    }

    private void OnActionInvoked(object? sender, DirectXamlActionEventArgs e)
    {
        if (e.Handler == "CopyCommand")
        {
            MinimalServiceResultItem.CopyResultToClipboard(_serviceResult);
            return;
        }

        if (e.Handler != "OnHeaderPointerPressed")
        {
            return;
        }

        if (_serviceResult is null || _serviceResult.IsLoading)
        {
            return;
        }

        if (ServiceResultDemotionHelper.IsDemoted(_serviceResult) || !_serviceResult.ShowPendingQueryHint)
        {
            return;
        }

        _serviceResult.IsExpanded = true;
        UpdateUI();
        CollapseToggled?.Invoke(this, _serviceResult);
        QueryRequested?.Invoke(this, _serviceResult);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surfaceItem.ActionInvoked -= OnActionInvoked;
        _surfaceItem.Drawn -= OnSurfaceItemDrawn;
        _surface.ThemeChanged -= OnSurfaceThemeChanged;
        _surfaceItem.Dispose();
    }
}
