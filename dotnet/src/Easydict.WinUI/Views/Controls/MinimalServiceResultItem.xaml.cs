using System.ComponentModel;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Views.Controls;

public sealed partial class MinimalServiceResultItem : UserControl, IServiceResultView
{
    private ServiceQueryResult? _serviceResult;
    private bool _updateUIPending;
    private int _updateUIRequestVersion;
    private int _renderedUpdateUIVersion;
    private bool _favoriteVisible;
    private bool _isFavorited;
    private bool _isSavedItemView;

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

    public bool IsSavedItemView
    {
        get => _isSavedItemView;
        set
        {
            if (_isSavedItemView == value)
                return;

            _isSavedItemView = value;
            QueueUpdateUI();
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

    public event EventHandler<ServiceQueryResult>? FavoriteRequested;

    public event EventHandler? CopyCompleted;
    private int _playGeneration;

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

    public void ApplyAppearance(AppearanceSettings settings)
    {
        RootBorder.Margin = new Thickness(0, 0, 0, _isSavedItemView ? 0 : SettingsService.Instance.CompactMode ? 8 : 16);
        ServiceNameText.FontSize = settings.ServiceNameFontSize;
        StatusText.FontSize = settings.StatusFontSize;
        ResultText.FontSize = _isSavedItemView ? 14 * AppearanceService.FontScale : settings.ResultFontSize;
    }

    public IEnumerable<string> GetDisplayedPhoneticKeys() => Array.Empty<string>();

    public void SetFavoriteState(bool isVisible, bool isFavorited)
    {
        _favoriteVisible = isVisible;
        _isFavorited = isFavorited;
        UpdateFavoriteButton();
    }

    public ResultMessageView Feedback => ResultFeedback;
    // This renderer deliberately uses native text and has no asynchronous rich-content state.
    public event EventHandler<ResultRenderingEventArgs>? RenderingStatusChanged { add { } remove { } }

    public void Cleanup()
    {
        ResultFeedback.Cleanup();
        _playGeneration++;
        CopyButton.Visibility = CollapseButton.Visibility = Visibility.Collapsed;
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
        FavoriteButton.Visibility = Visibility.Collapsed;
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
        var hasSavedResult = _serviceResult?.HasSuccessfulResult == true;
        CopyButton.Visibility = CollapseButton.Visibility = hasSavedResult ? Visibility.Visible : Visibility.Collapsed;
        SavedActionBar.Visibility = hasSavedResult || _favoriteVisible ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Visibility = hasSavedResult && _serviceResult?.Result is not null ? Visibility.Visible : Visibility.Collapsed;
        SavedMoreButton.Visibility = hasSavedResult ? Visibility.Visible : Visibility.Collapsed;
        if (hasSavedResult)
        {
            HeaderBar.Padding = new Thickness(12, 4, 8, 4);
            ContentArea.Padding = new Thickness(SettingsService.Instance.CompactMode ? 8 : 12);
            var loc = LocalizationService.Instance;
            SavedCopySourceMenuItem.Text = loc.GetStringOrDefault("SavedItemsCopySource", "Copy source");
            SavedCollapseMenuItem.Text = loc.GetStringOrDefault("SavedItemsCollapseResult", "Collapse result");
            ToolTipService.SetToolTip(CopyButton, loc.GetStringOrDefault("Copy", "Copy"));
            ToolTipService.SetToolTip(PlayButton, loc.GetStringOrDefault("Play", "Play"));
            ToolTipService.SetToolTip(SavedMoreButton, loc.GetStringOrDefault("SavedItemsMore", "More"));
            ToolTipService.SetToolTip(CollapseButton, loc.GetStringOrDefault("SavedItemsToggleResult", "Expand or collapse result"));
            CollapseIcon.Glyph = _serviceResult!.IsExpanded ? "\uE70E" : "\uE70D";
        }

        if (_serviceResult is null)
        {
            return;
        }

        var demoted = ServiceResultDemotionHelper.IsDemoted(_serviceResult);
        if (demoted)
        {
            _serviceResult.IsExpanded = false;
        }
        else if (!_isSavedItemView && (_serviceResult.HasError || _serviceResult.IsStreaming)
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
                ErrorText.Text = ServiceResultStatusTextProvider.GetErrorText(_serviceResult.Error);
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
        UpdateFavoriteButton();
        if (_serviceResult is not null)
            ContentArea.Visibility = _serviceResult.IsExpanded && hasVisibleContent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFavoriteClicked(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[MinimalServiceResultItem] Favorite click: provider={_serviceResult?.ServiceId}, enabled={FavoriteButton.IsEnabled}, subscribed={FavoriteRequested is not null}");
        if (_serviceResult is not null && FavoriteButton.IsEnabled)
            FavoriteRequested?.Invoke(this, _serviceResult);
    }

    private void UpdateFavoriteButton()
    {
        SavedActionBar.Visibility = _favoriteVisible || (_serviceResult?.HasSuccessfulResult == true)
            ? Visibility.Visible : Visibility.Collapsed;
        var hasSuccessfulText = _serviceResult?.IsGrammarMode == true
            ? !string.IsNullOrWhiteSpace(_serviceResult.GrammarResult?.CorrectedText)
            : _serviceResult?.Result is { ResultKind: TranslationResultKind.Success, TranslatedText.Length: > 0 };
        FavoriteButton.Visibility = _favoriteVisible && hasSuccessfulText
            ? Visibility.Visible
            : Visibility.Collapsed;
        FavoriteButton.IsEnabled = FavoriteButton.Visibility == Visibility.Visible;
        FavoriteIcon.Glyph = _isFavorited ? "\uE735" : "\uE734";
        var localization = LocalizationService.Instance;
        var tooltip = _isFavorited
            ? localization.GetStringOrDefault("SavedItemsRemoveResultFavorite", "Remove result favorite")
            : localization.GetStringOrDefault("SavedItemsAddResultFavorite", "Add result favorite");
        ToolTipService.SetToolTip(FavoriteButton, tooltip);
        AutomationProperties.SetName(FavoriteButton, tooltip);
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

    private void CopySavedText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { ClipboardService.SetText(text); CopyCompleted?.Invoke(this, new ResultCopyEventArgs()); }
        catch (Exception exception) { CopyCompleted?.Invoke(this, new ResultCopyEventArgs(exception)); }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e) => CopySavedText(
        _serviceResult?.IsGrammarMode == true ? _serviceResult.GrammarResult?.CorrectedText : _serviceResult?.Result?.TranslatedText);

    private void OnCopySavedSource(object sender, RoutedEventArgs e) => CopySavedText(
        _serviceResult?.IsGrammarMode == true ? _serviceResult.GrammarResult?.OriginalText : _serviceResult?.Result?.OriginalText);

    private void OnCollapseClicked(object sender, RoutedEventArgs e) => ToggleCollapse();

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        var generation = ++_playGeneration;
        if (PlayIcon.Glyph == "\uE71A")
        {
            TextToSpeechService.Instance.Stop();
            PlayIcon.Glyph = "\uE768";
            return;
        }
        if (_serviceResult?.Result is not { } result) return;
        PlayIcon.Glyph = "\uE71A";
        try { await TextToSpeechService.Instance.SpeakAsync(result.TranslatedText, result.TargetLanguage); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception.Message); }
        finally { if (generation == _playGeneration) PlayIcon.Glyph = "\uE768"; }
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

        if (_isSavedItemView || _serviceResult.HasSuccessfulResult)
        {
            _serviceResult.IsExpanded = !_serviceResult.IsExpanded;
            UpdateUI();
            CollapseToggled?.Invoke(this, _serviceResult);
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
