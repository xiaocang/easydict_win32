using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// A collapsible result item for a single translation service.
/// Mirrors macOS EZResultView behavior with expand/collapse functionality.
/// </summary>
public sealed partial class ServiceResultItem : UserControl
{
    private ServiceQueryResult? _serviceResult;
    private bool _isHovering;
    private string? _cachedServiceId;
    private BitmapImage? _cachedIcon;

    /// <summary>
    /// Event raised when the expand/collapse state is toggled.
    /// </summary>
    public event EventHandler<ServiceQueryResult>? CollapseToggled;

    /// <summary>
    /// Event raised when user clicks to expand a manual-query service that hasn't been queried yet.
    /// The subscriber should trigger the actual translation query for this service.
    /// </summary>
    public event EventHandler<ServiceQueryResult>? QueryRequested;

    public ServiceResultItem()
    {
        this.InitializeComponent();
        ToolTipService.SetToolTip(ReplaceButton, LocalizationService.Instance.GetString("InsertReplace"));
    }

    /// <summary>
    /// The service query result to display.
    /// </summary>
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

    private void OnServiceResultPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => UpdateUI());
    }

    private void UpdateUI()
    {
        if (_serviceResult == null)
        {
            return;
        }

        // Service info
        ServiceNameText.Text = _serviceResult.ServiceDisplayName;

        // Load service icon only when ServiceId changes (avoid repeated allocations during streaming)
        if (_cachedServiceId != _serviceResult.ServiceId)
        {
            _cachedServiceId = _serviceResult.ServiceId;
            try
            {
                _cachedIcon = new BitmapImage(new Uri(_serviceResult.ServiceIconPath));
                ServiceIcon.Source = _cachedIcon;
                ServiceIcon.Visibility = Visibility.Visible;
            }
            catch
            {
                // Icon not found, hide it and release previous image reference
                _cachedIcon = null;
                ServiceIcon.Source = null;
                ServiceIcon.Visibility = Visibility.Collapsed;
            }
        }

        // Loading state
        LoadingIndicator.IsActive = _serviceResult.IsLoading;
        LoadingIndicator.Visibility = _serviceResult.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        // Error state
        var hasError = _serviceResult.HasError && !_serviceResult.IsLoading;
        ErrorIcon.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;

        // Status text - show "Click to query" hint for pending manual-query services
        if (_serviceResult.ShowPendingQueryHint)
        {
            StatusText.Text = "Click to query";
        }
        else
        {
            StatusText.Text = _serviceResult.StatusText;
        }

        // Arrow direction
        ArrowIcon.Glyph = _serviceResult.ArrowGlyph;

        // Content visibility: show during streaming, when result available, or for pending query hint
        var showPendingHint = _serviceResult.IsExpanded && _serviceResult.ShowPendingQueryHint;
        var showContent = _serviceResult.IsExpanded &&
            (_serviceResult.HasResult || _serviceResult.IsStreaming || showPendingHint);
        ContentArea.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;

        // Update header corner radius based on expand state
        HeaderBar.CornerRadius = showContent ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6);

        // Pending query hint visibility
        PendingQueryText.Visibility = showPendingHint ? Visibility.Visible : Visibility.Collapsed;

        // Result text - handle streaming state
        if (_serviceResult.IsStreaming)
        {
            // Show streaming text or placeholder while waiting for first chunk
            ResultText.Text = string.IsNullOrEmpty(_serviceResult.StreamingText)
                ? "Waiting for response..."
                : _serviceResult.StreamingText;
            ResultText.Visibility = Visibility.Visible;
            ErrorText.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed; // Don't show buttons during streaming
        }
        else if (_serviceResult.Result != null)
        {
            ResultText.Text = _serviceResult.Result.TranslatedText;
            ResultText.Visibility = Visibility.Visible;
            ErrorText.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
            ReplaceButton.Visibility = TextInsertionService.HasSourceWindow ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_serviceResult.Error != null)
        {
            ErrorText.Text = _serviceResult.Error.Message;
            ErrorText.Visibility = Visibility.Visible;
            ResultText.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultText.Text = "";
            ResultText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed;
        }
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_serviceResult == null || _serviceResult.IsLoading)
        {
            return;
        }

        // Only handle left click
        var point = e.GetCurrentPoint(HeaderBar);
        if (point.Properties.IsLeftButtonPressed)
        {
            // Check if this is a manual-query service that needs to be queried
            var wasCollapsed = !_serviceResult.IsExpanded;
            var needsQuery = !_serviceResult.EnabledQuery && !_serviceResult.HasQueried && wasCollapsed;

            _serviceResult.ToggleExpanded();
            UpdateUI();
            CollapseToggled?.Invoke(this, _serviceResult);

            // If expanding a manual-query service that hasn't been queried, request query
            if (needsQuery && _serviceResult.IsExpanded)
            {
                QueryRequested?.Invoke(this, _serviceResult);
            }

            e.Handled = true;
        }
    }

    private void OnControlPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = true;

        if (_serviceResult?.Result != null && _serviceResult.IsExpanded)
        {
            ActionButtons.Visibility = Visibility.Visible;
            ReplaceButton.Visibility = TextInsertionService.HasSourceWindow ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnControlPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        HeaderBar.ClearValue(Border.BackgroundProperty);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        ActionButtons.Visibility = Visibility.Collapsed;
    }

    private void OnHeaderBarPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (FindThemeBrush("ButtonHoverBrush") is Brush brush)
            HeaderBar.Background = brush;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private Brush? FindThemeBrush(string key)
    {
        var themeName = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";

        // Check top-level ThemeDictionaries first
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeName, out var topObj))
        {
            var topDict = (ResourceDictionary)topObj;
            if (topDict.ContainsKey(key))
                return (Brush)topDict[key];
        }

        // Check merged dictionaries (Colors.xaml lives here)
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(themeName, out var obj))
            {
                var dict = (ResourceDictionary)obj;
                if (dict.ContainsKey(key))
                    return (Brush)dict[key];
            }
        }

        return null;
    }

    private void OnHeaderBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        HeaderBar.ClearValue(Border.BackgroundProperty);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private async void OnReplaceClicked(object sender, RoutedEventArgs e)
    {
        var text = _serviceResult?.Result?.TranslatedText;
        if (string.IsNullOrEmpty(text))
            return;

        var success = await TextInsertionService.InsertTextAsync(text);

        // Visual feedback
        ReplaceIcon.Glyph = success ? "\uE8FB" : "\uE783"; // Checkmark or error
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1500);
            ReplaceIcon.Glyph = "\uE8AC"; // Reset to replace icon
        });
    }

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        var result = _serviceResult?.Result;
        if (result == null || string.IsNullOrEmpty(result.TranslatedText))
            return;

        var tts = TextToSpeechService.Instance;

        void ResetIcon()
        {
            tts.PlaybackEnded -= ResetIcon;
            DispatcherQueue.TryEnqueue(() => PlayIcon.Glyph = "\uE768");
        }

        PlayIcon.Glyph = "\uE71A"; // Stop icon
        tts.PlaybackEnded += ResetIcon;
        await tts.SpeakAsync(result.TranslatedText, result.TargetLanguage);
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var text = _serviceResult?.Result?.TranslatedText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);

        // Visual feedback
        CopyIcon.Glyph = "\uE8FB"; // Checkmark
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1500);
            CopyIcon.Glyph = "\uE8C8"; // Copy icon
        });
    }
}
