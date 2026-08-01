using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Easydict.DirectXaml;
using Easydict.DirectXaml.Ir;
using Easydict.DirectXaml.Win2D;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using DxColor = Easydict.DirectXaml.Color;
using DxVisibility = Easydict.DirectXaml.Visibility;

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

    private const string SlotRoot = "RootBorder";
    private const string SlotHeader = "HeaderBar";
    private const string SlotServiceName = "ServiceNameText";
    private const string SlotStatus = "StatusText";
    private const string SlotContent = "ContentArea";
    private const string SlotPending = "PendingQueryText";
    private const string SlotResult = "ResultText";
    private const string SlotError = "ErrorText";

    private readonly Grid _root;
    private readonly DirectXamlCanvas _canvas;
    private readonly CompiledView _view;
    private readonly ThemeResourceResolver _resources;

    private ServiceQueryResult? _serviceResult;
    private bool _updatePending;
    private bool _disposed;

    public DirectServiceResultItem(FrameworkElement? themeRoot)
    {
        _resources = new ThemeResourceResolver(themeRoot);

        IrDocument ir = IrLoader.LoadFromResource(typeof(DirectServiceResultItem).Assembly, IrResourceName);
        _view = new CompiledView(ir, _resources);
        _canvas = new DirectXamlCanvas(_view);
        _canvas.ActionInvoked += OnActionInvoked;
        _canvas.ThemeChanged += OnCanvasThemeChanged;

        // The canvas is wrapped so that Element and HeaderPanel stay distinct objects: the host
        // stamps a different AutomationId on each, and existing UI automation locates cards by
        // ServiceResultItem_<serviceId>.
        _root = new Grid();
        _root.Children.Add(_canvas.Element);

        _view.SetText(SlotPending, ServiceResultStatusTextProvider.GetPendingQueryHintText());
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

    public FrameworkElement Element => _root;

    public FrameworkElement HeaderPanel => _canvas.Element;

    public FrameworkElement? ActionButtonsPanel => null;

    public bool IsMinimalRenderer => true;

    public FrameworkElement? ThemeRoot
    {
        get => _resources.ThemeRoot;
        set
        {
            _resources.ThemeRoot = value;
            _canvas.OnThemeResourcesChanged(_resources);
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

    public void RefreshThemeChrome() => _canvas.OnThemeResourcesChanged(_resources);

    public void ApplyAppearance(AppearanceSettings settings)
    {
        _view.SetFontSize(SlotServiceName, settings.ServiceNameFontSize);
        _view.SetFontSize(SlotStatus, settings.StatusFontSize);
        _view.SetFontSize(SlotResult, settings.ResultFontSize);
        _canvas.Update();
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

        _view.SetText(SlotServiceName, string.Empty);
        _view.SetText(SlotStatus, string.Empty);
        _view.SetText(SlotResult, string.Empty);
        _view.SetText(SlotError, string.Empty);
        _view.SetVisibility(SlotContent, DxVisibility.Collapsed);
        _canvas.Update();
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

        _view.SetOpacity(SlotRoot, demoted ? 0.5 : 1.0);
        _view.SetText(SlotServiceName, _serviceResult.ServiceDisplayName);

        string status = ServiceResultStatusTextProvider.GetStatusText(_serviceResult);
        _view.SetText(SlotStatus, status);
        _view.SetVisibility(
            SlotStatus,
            string.IsNullOrWhiteSpace(status) ? DxVisibility.Collapsed : DxVisibility.Visible);

        bool showPendingHint = !demoted && _serviceResult.ShowPendingQueryHint;
        _view.SetVisibility(SlotPending, showPendingHint ? DxVisibility.Visible : DxVisibility.Collapsed);

        var resultVisibility = DxVisibility.Collapsed;
        var errorVisibility = DxVisibility.Collapsed;

        if (!demoted)
        {
            if (_serviceResult.HasError && !_serviceResult.IsLoading)
            {
                _view.SetText(
                    SlotError,
                    _serviceResult.Error?.Message ?? ServiceResultStatusTextProvider.GetErrorFallbackText());
                errorVisibility = DxVisibility.Visible;
            }
            else if (_serviceResult.IsStreaming)
            {
                string displayText = _serviceResult.DisplayText;
                _view.SetText(
                    SlotResult,
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
                    _view.SetText(SlotResult, displayText);
                    ApplyResultForeground(_serviceResult.IsInfoResult);
                    resultVisibility = DxVisibility.Visible;
                }
            }
        }

        _view.SetVisibility(SlotResult, resultVisibility);
        _view.SetVisibility(SlotError, errorVisibility);

        bool hasVisibleContent = showPendingHint
            || resultVisibility == DxVisibility.Visible
            || errorVisibility == DxVisibility.Visible;
        _view.SetVisibility(SlotContent, hasVisibleContent ? DxVisibility.Visible : DxVisibility.Collapsed);

        _canvas.Update();
    }

    private void ApplyResultForeground(bool isInfoResult)
    {
        string key = isInfoResult ? "TextFillColorSecondaryBrush" : "QueryTextBrush";
        if (_resources.TryGetColor(key, out DxColor color))
        {
            _view.SetForeground(SlotResult, color);
        }
    }

    private void OnCanvasThemeChanged(object? sender, EventArgs e) =>
        _canvas.OnThemeResourcesChanged(_resources);

    private void OnActionInvoked(object? sender, DirectXamlActionEventArgs e)
    {
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
        _canvas.ActionInvoked -= OnActionInvoked;
        _canvas.ThemeChanged -= OnCanvasThemeChanged;
        _canvas.Dispose();
        Debug.WriteLine("[DirectServiceResultItem] disposed");
    }
}
