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

    public MinimalServiceResultItem()
    {
        InitializeComponent();
    }

    public FrameworkElement Element => this;

    public FrameworkElement HeaderPanel => HeaderBar;

    public FrameworkElement? ActionButtonsPanel => null;

    public bool IsMinimalRenderer => true;

    public HashSet<string>? AlreadyShownPhonetics { get; set; }

    public event EventHandler<ServiceQueryResult>? CollapseToggled;

    public event EventHandler<ServiceQueryResult>? QueryRequested;

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

    public void RefreshDemotionState() => UpdateUI();

    public IEnumerable<string> GetDisplayedPhoneticKeys() => Array.Empty<string>();

    public void Cleanup()
    {
        if (_serviceResult != null)
        {
            _serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;
        }

        _serviceResult = null;
        ServiceNameText.Text = string.Empty;
        StatusText.Text = string.Empty;
        ResultText.Text = string.Empty;
        ErrorText.Text = string.Empty;
        ContentArea.Visibility = Visibility.Collapsed;
    }

    private void OnServiceResultPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updateUIPending)
        {
            return;
        }

        _updateUIPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _updateUIPending = false;
            UpdateUI();
        });
    }

    private void UpdateUI()
    {
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
                ErrorText.Text = _serviceResult.Error?.Message ?? "Error";
                ErrorText.Visibility = Visibility.Visible;
            }
            else if (_serviceResult.IsStreaming)
            {
                var displayText = _serviceResult.DisplayText;
                ResultText.Text = string.IsNullOrWhiteSpace(displayText)
                    ? "Waiting for response..."
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

    private static string GetStatusText(ServiceQueryResult serviceResult)
    {
        if (serviceResult.ShowPendingQueryHint) return "Click to query";
        if (serviceResult.IsLoading) return "Loading";
        return serviceResult.StatusText;
    }

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

    private static Brush? ResolveTextBrush(bool isInfoResult)
    {
        return MinimalThemeService.GetBrush(
            isInfoResult ? "TextFillColorSecondaryBrush" : "QueryTextBrush");
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
