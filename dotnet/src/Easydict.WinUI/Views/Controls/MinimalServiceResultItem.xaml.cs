using System.ComponentModel;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Views.Controls;

public sealed partial class MinimalServiceResultItem : UserControl, IServiceResultView
{
    private ServiceQueryResult? _serviceResult;
    private bool _updateUIPending;
    private int _updateUIRequestVersion;
    private int _renderedUpdateUIVersion;

    public MinimalServiceResultItem()
    {
        InitializeComponent();
        PendingQueryText.Text = ServiceResultStatusTextProvider.GetPendingQueryHintText();
    }

    public FrameworkElement Element => this;

    public FrameworkElement? ThemeRoot { get; set; }

    public FrameworkElement HeaderPanel => HeaderBar;

    public FrameworkElement? ActionButtonsPanel => null;

    public bool IsMinimalRenderer => true;

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
            if (_serviceResult != null)
            {
                _serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;
            }

            _serviceResult = value;

            if (_serviceResult != null)
            {
                _serviceResult.PropertyChanged += OnServiceResultPropertyChanged;
            }

            UpdateUI();
        }
    }

    public void RefreshDemotionState() => QueueUpdateUI();

    public IEnumerable<string> GetDisplayedPhoneticKeys() => Array.Empty<string>();

    public void Cleanup()
    {
        if (_serviceResult != null)
        {
            _serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;
        }

        _serviceResult = null;
        _updateUIPending = false;
        _updateUIRequestVersion = 0;
        _renderedUpdateUIVersion = 0;
        ThemeRoot = null;
        ServiceNameText.Text = string.Empty;
        StatusText.Text = string.Empty;
        ResultText.Text = string.Empty;
        ErrorText.Text = string.Empty;
        ContentArea.Visibility = Visibility.Collapsed;
    }

    private void OnServiceResultPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueUpdateUI();
    }

    private void QueueUpdateUI()
    {
        unchecked
        {
            _updateUIRequestVersion++;
        }

        if (_updateUIPending)
        {
            return;
        }

        _updateUIPending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _updateUIPending = false;
                if (_renderedUpdateUIVersion == _updateUIRequestVersion)
                {
                    return;
                }

                UpdateUI();
            }))
        {
            _updateUIPending = false;
        }
    }

    private void UpdateUI()
    {
        using var hotspot = UiThreadHotspotDiagnostics.Measure("MinimalServiceResultItem.UpdateUI");
        _renderedUpdateUIVersion = _updateUIRequestVersion;

        if (_serviceResult is null)
        {
            return;
        }

        var demoted = ServiceResultDemotionHelper.IsDemoted(_serviceResult);
        if (demoted)
        {
            _serviceResult.IsExpanded = false;
        }
        else if ((_serviceResult.HasResult || _serviceResult.HasError || _serviceResult.IsStreaming)
                 && !_serviceResult.IsExpanded)
        {
            _serviceResult.IsExpanded = true;
        }

        RootBorder.Opacity = demoted ? 0.5 : 1.0;
        ServiceNameText.Text = _serviceResult.ServiceDisplayName;

        StatusText.Text = GetStatusText(_serviceResult);
        var showStatus = !string.IsNullOrWhiteSpace(StatusText.Text);
        StatusText.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;

        var showPendingHint = !demoted && _serviceResult.ShowPendingQueryHint;
        PendingQueryText.Visibility = showPendingHint ? Visibility.Visible : Visibility.Collapsed;

        ResultText.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;

        if (!demoted)
        {
            if (_serviceResult.HasError && !_serviceResult.IsLoading)
            {
                ErrorText.Text = _serviceResult.Error?.Message
                    ?? ServiceResultStatusTextProvider.GetErrorFallbackText();
                ErrorText.Visibility = Visibility.Visible;
            }
            else if (_serviceResult.IsStreaming)
            {
                var displayText = _serviceResult.DisplayText;
                ResultText.Text = string.IsNullOrWhiteSpace(displayText)
                    ? ServiceResultStatusTextProvider.GetWaitingForResponseText()
                    : displayText;
                ResultText.Foreground = ResolveTextBrush(isInfoResult: false)
                    ?? ResultText.Foreground;
                ResultText.Visibility = Visibility.Visible;
            }
            else if (_serviceResult.HasResult)
            {
                var displayText = GetMinimalDisplayText(_serviceResult);
                if (!string.IsNullOrWhiteSpace(displayText))
                {
                    ResultText.Text = displayText;
                    if (ResolveTextBrush(_serviceResult.IsInfoResult) is Brush textBrush)
                    {
                        ResultText.Foreground = textBrush;
                    }
                    ResultText.Visibility = Visibility.Visible;
                }
            }
        }

        var hasVisibleContent = showPendingHint
            || ResultText.Visibility == Visibility.Visible
            || ErrorText.Visibility == Visibility.Visible;
        ContentArea.Visibility = hasVisibleContent ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetStatusText(ServiceQueryResult serviceResult) =>
        ServiceResultStatusTextProvider.GetStatusText(serviceResult);

    private static string GetMinimalDisplayText(ServiceQueryResult serviceResult)
    {
        var displayText = serviceResult.DisplayText;
        if (!string.IsNullOrWhiteSpace(displayText))
        {
            return displayText;
        }

        var result = serviceResult.Result;
        if (result?.WordResult?.Definitions is { Count: > 0 } definitions)
        {
            var lines = definitions
                .Select(definition =>
                {
                    var meanings = definition.Meanings is { Count: > 0 }
                        ? string.Join("; ", definition.Meanings.Where(meaning => !string.IsNullOrWhiteSpace(meaning)))
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(meanings))
                    {
                        return string.Empty;
                    }

                    return string.IsNullOrWhiteSpace(definition.PartOfSpeech)
                        ? meanings
                        : $"{definition.PartOfSpeech}: {meanings}";
                })
                .Where(line => !string.IsNullOrWhiteSpace(line));

            return string.Join(Environment.NewLine, lines);
        }

        if (result?.Alternatives is { Count: > 0 } alternatives)
        {
            return string.Join("; ", alternatives.Where(alternative => !string.IsNullOrWhiteSpace(alternative)));
        }

        return string.Empty;
    }

    private Brush? ResolveTextBrush(bool isInfoResult)
    {
        return ThemeResourceService.GetBrush(
            isInfoResult ? "TextFillColorSecondaryBrush" : "QueryTextBrush",
            ThemeRoot ?? this);
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_serviceResult is null || _serviceResult.IsLoading)
        {
            return;
        }

        if (ServiceResultDemotionHelper.IsDemoted(_serviceResult))
        {
            e.Handled = true;
            return;
        }

        var point = e.GetCurrentPoint(HeaderBar);
        if (point.Properties.IsLeftButtonPressed)
        {
            ToggleCollapse();
            e.Handled = true;
        }
    }

    private void ToggleCollapse()
    {
        if (_serviceResult is null)
        {
            return;
        }

        if (!_serviceResult.ShowPendingQueryHint)
        {
            return;
        }

        _serviceResult.IsExpanded = true;
        UpdateUI();
        CollapseToggled?.Invoke(this, _serviceResult);
        QueryRequested?.Invoke(this, _serviceResult);
    }
}
