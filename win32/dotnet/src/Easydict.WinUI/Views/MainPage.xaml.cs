using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using TranslationLanguage = Easydict.TranslationService.Models.Language;

namespace Easydict.WinUI.Views
{
    /// <summary>
    /// Main translation page with Fluent Design.
    /// Follows macOS Easydict's user interaction patterns.
    /// </summary>
    public partial class MainPage : Page
    {
        private TranslationManager? _translationManager;
        private CancellationTokenSource? _currentQueryCts;
        private readonly SettingsService _settings = SettingsService.Instance;

        public MainPage()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[MainPage] Constructor starting...");
                this.InitializeComponent();
                System.Diagnostics.Debug.WriteLine("[MainPage] InitializeComponent completed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainPage] InitializeComponent FAILED: {ex}");
                throw;
            }
            this.Loaded += OnPageLoaded;
            this.Unloaded += OnPageUnloaded;

            // Sync selection between Wide and Narrow layout ComboBoxes
            SourceLangCombo.SelectionChanged += (s, e) => SyncComboSelection(SourceLangCombo, SourceLangComboNarrow);
            SourceLangComboNarrow.SelectionChanged += (s, e) => SyncComboSelection(SourceLangComboNarrow, SourceLangCombo);
            TargetLangCombo.SelectionChanged += (s, e) => SyncComboSelection(TargetLangCombo, TargetLangComboNarrow);
            TargetLangComboNarrow.SelectionChanged += (s, e) => SyncComboSelection(TargetLangComboNarrow, TargetLangCombo);

            // Subscribe to clipboard events from App
            App.ClipboardTextReceived += OnClipboardTextReceived;
        }

        private static void SyncComboSelection(ComboBox source, ComboBox target)
        {
            if (target.SelectedIndex != source.SelectedIndex)
            {
                target.SelectedIndex = source.SelectedIndex;
            }
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            InitializeTranslationServices();
            ApplySettings();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            App.ClipboardTextReceived -= OnClipboardTextReceived;
            CleanupResources();
        }

        private void ApplySettings()
        {
            // Apply target language from settings
            var targetLang = _settings.TargetLanguage;
            var targetIndex = targetLang switch
            {
                "en" => 0,
                "zh" => 1,
                "ja" => 2,
                "ko" => 3,
                "fr" => 4,
                "de" => 5,
                "es" => 6,
                _ => 1 // Default to Chinese
            };
            TargetLangCombo.SelectedIndex = targetIndex;
        }

        private void OnClipboardTextReceived(string text)
        {
            // Auto-translate clipboard text
            DispatcherQueue.TryEnqueue(async () =>
            {
                InputTextBox.Text = text;
                await StartQueryAsync();
            });
        }

        private void InitializeTranslationServices()
        {
            try
            {
                UpdateStatus(null, "Initializing...");

                _translationManager = new TranslationManager();

                // Set default service to Google (no API key needed)
                _translationManager.DefaultServiceId = "google";

                UpdateStatus(true, "Ready");
                ServiceText.Text = "Google Translate";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainPage] Init error: {ex.Message}");
                UpdateStatus(false, "Error");
            }
        }

        private void CleanupResources()
        {
            _currentQueryCts?.Cancel();
            _currentQueryCts?.Dispose();
            _translationManager?.Dispose();
            _translationManager = null;
        }

        private void UpdateStatus(bool? connected, string text)
        {
            StatusText.Text = text;

            if (connected == true)
            {
                StatusIndicator.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["StatusConnectedBrush"];
            }
            else if (connected == false)
            {
                StatusIndicator.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["StatusErrorBrush"];
            }
            else
            {
                StatusIndicator.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["StatusDisconnectedBrush"];
            }
        }

        private void SetLoading(bool loading)
        {
            // Update both Wide and Narrow buttons
            TranslateButton.IsEnabled = !loading;
            TranslateButtonNarrow.IsEnabled = !loading;

            LoadingRing.IsActive = loading;
            LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            TranslateIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;

            LoadingRingNarrow.IsActive = loading;
            LoadingRingNarrow.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            TranslateIconNarrow.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Handle translate button click - follows macOS Easydict's pattern:
        /// 1. Cancel any ongoing query
        /// 2. Clear previous results
        /// 3. Start new translation query
        /// </summary>
        private async void OnTranslateClicked(object sender, RoutedEventArgs e)
        {
            await StartQueryAsync();
        }

        /// <summary>
        /// Start a new translation query (similar to macOS's startQueryText:).
        /// </summary>
        private async Task StartQueryAsync()
        {
            if (_translationManager is null)
            {
                OutputTextBox.Text = "Service not initialized. Please wait...";
                InitializeTranslationServices();
                return;
            }

            var inputText = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(inputText))
            {
                return;
            }

            // Cancel previous query (like macOS's stopAllService)
            _currentQueryCts?.Cancel();
            _currentQueryCts?.Dispose();
            _currentQueryCts = new CancellationTokenSource();

            try
            {
                SetLoading(true);
                OutputTextBox.Text = "";
                TimingText.Text = "";

                var targetLanguage = GetTargetLanguage();

                var request = new TranslationRequest
                {
                    Text = inputText,
                    FromLanguage = TranslationLanguage.Auto,
                    ToLanguage = targetLanguage
                };

                var result = await _translationManager.TranslateAsync(
                    request,
                    _currentQueryCts.Token);

                // Update UI with result
                DisplayResult(result);
            }
            catch (OperationCanceledException)
            {
                // Query was cancelled, ignore
            }
            catch (TranslationException ex)
            {
                OutputTextBox.Text = ex.ErrorCode switch
                {
                    TranslationErrorCode.NetworkError => "Network error. Please check your connection.",
                    TranslationErrorCode.Timeout => "Request timed out. Please try again.",
                    TranslationErrorCode.RateLimited => "Rate limited. Please wait a moment.",
                    TranslationErrorCode.InvalidApiKey => "Invalid API key configuration.",
                    _ => $"Translation failed: {ex.Message}"
                };
                UpdateStatus(false, "Error");
            }
            catch (Exception ex)
            {
                OutputTextBox.Text = $"Error: {ex.Message}";
                UpdateStatus(false, "Error");
            }
            finally
            {
                SetLoading(false);
            }
        }

        /// <summary>
        /// Display translation result (like macOS's updateCellWithResult:).
        /// </summary>
        private void DisplayResult(TranslationResult result)
        {
            OutputTextBox.Text = result.TranslatedText;

            var timingInfo = result.FromCache ? "cached" : $"{result.TimingMs}ms";
            TimingText.Text = $"⏱ {timingInfo}";

            // Show detected language if auto-detected
            if (result.DetectedLanguage != TranslationLanguage.Auto)
            {
                var langName = GetLanguageDisplayName(result.DetectedLanguage);
                ServiceText.Text = $"{result.ServiceName} • {langName}";
            }
            else
            {
                ServiceText.Text = result.ServiceName;
            }

            UpdateStatus(true, "Ready");
        }

        private TranslationLanguage GetTargetLanguage()
        {
            return TargetLangCombo.SelectedIndex switch
            {
                0 => TranslationLanguage.English,
                1 => TranslationLanguage.SimplifiedChinese,
                2 => TranslationLanguage.Japanese,
                3 => TranslationLanguage.Korean,
                4 => TranslationLanguage.French,
                5 => TranslationLanguage.German,
                6 => TranslationLanguage.Spanish,
                _ => TranslationLanguage.SimplifiedChinese
            };
        }

        private static string GetLanguageDisplayName(TranslationLanguage language)
        {
            return language switch
            {
                TranslationLanguage.SimplifiedChinese => "Chinese (Simplified)",
                TranslationLanguage.TraditionalChinese => "Chinese (Traditional)",
                TranslationLanguage.English => "English",
                TranslationLanguage.Japanese => "Japanese",
                TranslationLanguage.Korean => "Korean",
                TranslationLanguage.French => "French",
                TranslationLanguage.German => "German",
                TranslationLanguage.Spanish => "Spanish",
                TranslationLanguage.Russian => "Russian",
                TranslationLanguage.Portuguese => "Portuguese",
                TranslationLanguage.Italian => "Italian",
                TranslationLanguage.Arabic => "Arabic",
                _ => language.ToString()
            };
        }

        private void OnCopyClicked(object sender, RoutedEventArgs e)
        {
            var text = OutputTextBox.Text;
            if (!string.IsNullOrEmpty(text))
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);

                // Brief visual feedback
                CopyButton.Content = new FontIcon { Glyph = "\uE8FB", FontSize = 14 }; // Checkmark
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(1500);
                    CopyButton.Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 }; // Copy icon
                });
            }
        }

        private void OnSettingsClicked(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
        }

        /// <summary>
        /// Set text to translate (called from external sources like hotkey).
        /// </summary>
        public void SetTextAndTranslate(string text)
        {
            InputTextBox.Text = text;
            _ = StartQueryAsync();
        }
    }
}
