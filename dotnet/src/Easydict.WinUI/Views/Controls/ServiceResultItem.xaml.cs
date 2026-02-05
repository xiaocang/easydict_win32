using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using TranslationLanguage = Easydict.TranslationService.Models.Language;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using System.Linq;

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
            PhoneticPanel.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed; // Don't show buttons during streaming
        }
        else if (_serviceResult.Result != null)
        {
            ResultText.Text = _serviceResult.Result.TranslatedText;
            ResultText.Visibility = Visibility.Visible;
            ErrorText.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
            ReplaceButton.Visibility = TextInsertionService.HasSourceWindow ? Visibility.Visible : Visibility.Collapsed;
            UpdatePhonetics(_serviceResult.Result);
        }
        else if (_serviceResult.Error != null)
        {
            ErrorText.Text = GetErrorDisplayText(_serviceResult);
            ErrorText.Visibility = Visibility.Visible;
            ResultText.Visibility = Visibility.Collapsed;
            PhoneticPanel.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
            ReplaceButton.Visibility = Visibility.Collapsed;
            PlayButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultText.Text = "";
            ResultText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            PhoneticPanel.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Populates the phonetic badges panel from WordResult phonetics data.
    /// Each badge shows: [accent label] [phonetic text] [speaker icon].
    /// Only displays phonetics when the target language is English.
    /// </summary>
    private void UpdatePhonetics(TranslationResult result)
    {
        // Only show phonetics when target language is English
        // US/UK phonetics are English pronunciation, only meaningful for English translations
        if (result.TargetLanguage != TranslationLanguage.English)
        {
            PhoneticPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var phonetics = result.WordResult?.Phonetics;
        if (phonetics == null || phonetics.Count == 0)
        {
            PhoneticPanel.Visibility = Visibility.Collapsed;
            return;
        }

        PhoneticPanel.Children.Clear();

        // Get target-related phonetics (dest/US/UK) and then display only US/UK accents
        var displayablePhonetics = PhoneticDisplayHelper.GetTargetPhonetics(result)
            .Where(p => p.Accent == "US" || p.Accent == "UK")
            .ToList();

        foreach (var phonetic in displayablePhonetics)
        {
            PhoneticPanel.Children.Add(CreatePhoneticBadge(phonetic, result));
        }

        PhoneticPanel.Visibility = PhoneticPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Creates a single phonetic badge with accent label, phonetic text, and TTS button.
    /// Includes accessibility properties for screen readers.
    /// </summary>
    private Border CreatePhoneticBadge(Phonetic phonetic, TranslationResult result)
    {
        // Build accessibility description
        var accentLabel = GetAccentDisplayLabel(phonetic.Accent);
        var accentDescription = phonetic.Accent switch
        {
            "US" => "American pronunciation",
            "UK" => "British pronunciation",
            "dest" => "Target language pronunciation",
            "src" => "Source language pronunciation",
            _ => "Pronunciation"
        };
        var accessibleName = $"{accentDescription}: {phonetic.Text}";

        var badge = new Border
        {
            Background = FindThemeBrush("PhoneticBadgeBackgroundBrush")
                ?? new SolidColorBrush(Microsoft.UI.Colors.LightGray),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 4, 2)
        };

        // Set accessibility properties on the badge
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(badge, accessibleName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(badge,
            "Click the speaker button to hear pronunciation");

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };

        // Accent label (e.g., "美", "英", "src", "dest")
        if (!string.IsNullOrEmpty(accentLabel))
        {
            panel.Children.Add(new TextBlock
            {
                Text = accentLabel,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = FindThemeBrush("PhoneticBadgeTextBrush")
                    ?? new SolidColorBrush(Microsoft.UI.Colors.Purple),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        // Phonetic text
        panel.Children.Add(new TextBlock
        {
            Text = PhoneticDisplayHelper.FormatPhoneticText(phonetic.Text!),
            FontSize = 11,
            Foreground = FindThemeBrush("PhoneticBadgeTextBrush")
                ?? new SolidColorBrush(Microsoft.UI.Colors.Purple),
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true
        });

        // TTS speaker button
        var speakerButton = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var speakerIcon = new FontIcon
        {
            Glyph = "\uE767", // Volume icon
            FontSize = 11,
            Foreground = FindThemeBrush("PhoneticBadgeTextBrush")
                ?? new SolidColorBrush(Microsoft.UI.Colors.Purple)
        };
        speakerButton.Content = speakerIcon;

        // Determine which language to use for TTS based on accent
        // Use alias to avoid conflict with FrameworkElement.Language (string)
        TranslationLanguage ttsLanguage;
        string ttsText;

        if (phonetic.Accent == "dest")
        {
            // Destination accent: use translated text in target language
            ttsLanguage = result.TargetLanguage;
            ttsText = result.TranslatedText;
        }
        else if (phonetic.Accent == "US" || phonetic.Accent == "UK")
        {
            // English accents ("US"/"UK"): use English translation
            ttsLanguage = TranslationLanguage.English;
            ttsText = result.TranslatedText;
        }
        else
        {
            // Fallback: use original text with detected language
            ttsLanguage = result.DetectedLanguage != TranslationLanguage.Auto
                ? result.DetectedLanguage
                : TranslationLanguage.English;
            ttsText = result.OriginalText;
        }

        speakerButton.Click += async (s, e) =>
        {
            var tts = TextToSpeechService.Instance;

            // Reset the icon back to the speaker glyph on the UI thread.
            void ResetIconGlyph()
            {
                DispatcherQueue.TryEnqueue(() => speakerIcon.Glyph = "\uE767");
            }

            // Handler for playback completion; unsubscribes itself and resets the icon.
            void OnPlaybackEnded()
            {
                tts.PlaybackEnded -= OnPlaybackEnded;
                ResetIconGlyph();
            }

            speakerIcon.Glyph = "\uE71A"; // Stop icon
            tts.PlaybackEnded += OnPlaybackEnded;

            try
            {
                await tts.SpeakAsync(ttsText, ttsLanguage);
            }
            finally
            {
                // Ensure we always detach the handler and reset the icon,
                // even if SpeakAsync fails, is cancelled, or playback ends early.
                tts.PlaybackEnded -= OnPlaybackEnded;
                ResetIconGlyph();
            }
        };

        panel.Children.Add(speakerButton);
        badge.Child = panel;
        return badge;
    }

    /// <summary>
    /// Maps phonetic accent codes to display labels.
    /// Delegates to PhoneticDisplayHelper for testability.
    /// </summary>
    private static string? GetAccentDisplayLabel(string? accent)
    {
        return PhoneticDisplayHelper.GetAccentDisplayLabel(accent);
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

        if (_serviceResult?.IsExpanded == true &&
            (_serviceResult.Result != null || _serviceResult.Error != null))
        {
            ActionButtons.Visibility = Visibility.Visible;
            ReplaceButton.Visibility = _serviceResult.Result != null && TextInsertionService.HasSourceWindow
                ? Visibility.Visible : Visibility.Collapsed;
            PlayButton.Visibility = _serviceResult.Result != null
                ? Visibility.Visible : Visibility.Collapsed;
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

        // Reset the icon back to the play glyph on the UI thread.
        void ResetIconGlyph()
        {
            DispatcherQueue.TryEnqueue(() => PlayIcon.Glyph = "\uE768");
        }

        // Handler for playback completion; unsubscribes itself and resets the icon.
        void OnPlaybackEnded()
        {
            tts.PlaybackEnded -= OnPlaybackEnded;
            ResetIconGlyph();
        }

        PlayIcon.Glyph = "\uE71A"; // Stop icon
        tts.PlaybackEnded += OnPlaybackEnded;

        try
        {
            await tts.SpeakAsync(result.TranslatedText, result.TargetLanguage);
        }
        finally
        {
            // Ensure we always detach the handler and reset the icon,
            // even if SpeakAsync fails, is cancelled, or playback ends early.
            tts.PlaybackEnded -= OnPlaybackEnded;
            ResetIconGlyph();
        }
    }

    /// <summary>
    /// Returns the error message to display, with a region-aware hint appended
    /// when an international-only service fails with a network error or timeout.
    /// </summary>
    private static string GetErrorDisplayText(ServiceQueryResult serviceResult)
    {
        var error = serviceResult.Error;
        if (error == null)
        {
            return string.Empty;
        }

        var message = error.Message;

        // Append region hint for international services that fail with network errors.
        // Also notify SettingsService so it can lazily migrate defaults (timezone + failure = China network).
        var serviceId = serviceResult.ServiceId;
        if (!string.IsNullOrEmpty(serviceId) &&
            SettingsService.IsInternationalOnlyService(serviceId) &&
            error.ErrorCode is TranslationErrorCode.NetworkError or TranslationErrorCode.Timeout)
        {
            SettingsService.Instance.NotifyInternationalServiceFailed(serviceId, error.ErrorCode);

            var loc = LocalizationService.Instance;
            var hint = loc.GetString("InternationalServiceUnavailableHint");
            if (!string.IsNullOrEmpty(hint))
            {
                message = $"{message}\n{hint}";
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ServiceResultItem] InternationalServiceUnavailableHint localization string is missing");
            }
        }

        return message;
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var text = _serviceResult?.Result?.TranslatedText
                ?? _serviceResult?.Error?.Message;
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
