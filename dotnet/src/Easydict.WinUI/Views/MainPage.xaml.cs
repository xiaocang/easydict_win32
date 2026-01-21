using System.Diagnostics;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using TranslationLanguage = Easydict.TranslationService.Models.Language;

namespace Easydict.WinUI.Views
{
    /// <summary>
    /// Main translation page with Fluent Design.
    /// Follows macOS Easydict's user interaction patterns.
    /// </summary>
    public partial class MainPage : Page
    {
        private LanguageDetectionService? _detectionService;
        private CancellationTokenSource? _currentQueryCts;
        private readonly SettingsService _settings = SettingsService.Instance;
        private TranslationLanguage _lastDetectedLanguage = TranslationLanguage.Auto;
        private bool _isManualTargetSelection = false; // Track if user manually selected target
        private string _lastQueryText = ""; // Track last query text to detect changes
        private bool _isLoaded;
        private bool _suppressTargetLanguageSelectionChanged;

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
            _isLoaded = true;
            InitializeTranslationServices();
            _detectionService = new LanguageDetectionService(TranslationManagerService.Instance.Manager, _settings);
            ApplySettings();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
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
            _suppressTargetLanguageSelectionChanged = true;
            try
            {
                if (targetIndex >= 0 && targetIndex < TargetLangCombo.Items.Count)
                {
                    TargetLangCombo.SelectedIndex = targetIndex;
                }
                if (targetIndex >= 0 && targetIndex < TargetLangComboNarrow.Items.Count)
                {
                    TargetLangComboNarrow.SelectedIndex = targetIndex;
                }

                _isManualTargetSelection = false;
            }
            finally
            {
                _suppressTargetLanguageSelectionChanged = false;
            }
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

                var manager = TranslationManagerService.Instance.Manager;

                // DefaultServiceId is now managed centrally by TranslationManagerService
                UpdateStatus(true, "Ready");
                ServiceText.Text = manager.Services[manager.DefaultServiceId].DisplayName;
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
            _currentQueryCts = null;
            _detectionService?.Dispose();
            _detectionService = null;
            // Do NOT dispose shared TranslationManager - it's managed by TranslationManagerService
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
        /// Handle keyboard input in the input text box.
        /// Enter key triggers translation; Shift+Enter or Ctrl+Enter inserts newline.
        /// </summary>
        private async void OnInputTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            try
            {
                // Check if XamlRoot and ContentIsland are available
                if (this.XamlRoot?.ContentIsland == null)
                {
                    // Fallback: trigger translation if we can't check modifiers
                    e.Handled = true;
                    await StartQueryAsync();
                    return;
                }

                // Check if Shift or Ctrl is held down using InputKeyboardSource
                var keyboardSource = InputKeyboardSource.GetForIsland(this.XamlRoot.ContentIsland);
                var shiftState = keyboardSource.GetKeyState(VirtualKey.Shift);
                var ctrlState = keyboardSource.GetKeyState(VirtualKey.Control);

                bool isShiftPressed = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                bool isCtrlPressed = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                // If Shift or Ctrl is held, allow newline (don't handle the event)
                if (isShiftPressed || isCtrlPressed)
                {
                    return; // Let the TextBox handle it normally (insert newline)
                }

                // Plain Enter: trigger translation
                e.Handled = true; // Prevent default behavior (inserting newline)
                await StartQueryAsync();
            }
            catch
            {
                // Fallback: trigger translation on plain Enter if modifier detection fails
                e.Handled = true;
                await StartQueryAsync();
            }
        }

        /// <summary>
        /// Start a new translation query (similar to macOS's startQueryText:).
        /// </summary>
        private async Task StartQueryAsync()
        {
            if (_detectionService is null)
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

            // Reset manual target selection when text changes
            // This ensures auto-detection works for new input (macOS behavior)
            if (inputText != _lastQueryText)
            {
                _isManualTargetSelection = false;
                _lastQueryText = inputText;
            }

            // Cancel previous query (like macOS's stopAllService)
            _currentQueryCts?.Cancel();
            _currentQueryCts?.Dispose();
            _currentQueryCts = new CancellationTokenSource();

            var manager = TranslationManagerService.Instance.Manager;

            try
            {
                SetLoading(true);
                OutputTextBox.Text = "";
                TimingText.Text = "";

                // Step 1: Detect language
                var detectedLanguage = await _detectionService.DetectAsync(
                    inputText,
                    _currentQueryCts.Token);

                _lastDetectedLanguage = detectedLanguage;
                UpdateDetectedLanguageDisplay(detectedLanguage);

                // Step 2: Determine target language
                TranslationLanguage targetLanguage;
                if (_isManualTargetSelection)
                {
                    // User manually selected target language, respect user's choice
                    targetLanguage = GetTargetLanguage();
                }
                else if (_settings.AutoSelectTargetLanguage)
                {
                    // Auto-select target language (apply macOS algorithm)
                    targetLanguage = _detectionService.GetTargetLanguage(detectedLanguage);
                    UpdateTargetLanguageSelector(targetLanguage);
                }
                else
                {
                    // Auto-select disabled, use current selection
                    targetLanguage = GetTargetLanguage();
                }

                // Step 3: Execute translation
                var request = new TranslationRequest
                {
                    Text = inputText,
                    FromLanguage = detectedLanguage,
                    ToLanguage = targetLanguage
                };

                var serviceId = manager.DefaultServiceId;

                // Check if service supports streaming
                if (manager.IsStreamingService(serviceId))
                {
                    // Streaming path for LLM services
                    await ExecuteStreamingTranslationAsync(request, serviceId, detectedLanguage);
                }
                else
                {
                    // Non-streaming path for traditional services
                    var result = await manager.TranslateAsync(
                        request,
                        _currentQueryCts.Token);

                    // Update UI with result
                    DisplayResult(result);
                }
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
        /// Execute streaming translation for LLM services.
        /// Shows incremental results as they arrive from the API.
        /// </summary>
        private async Task ExecuteStreamingTranslationAsync(
            TranslationRequest request,
            string serviceId,
            TranslationLanguage detectedLanguage)
        {
            var stopwatch = Stopwatch.StartNew();
            var sb = new StringBuilder();
            var lastUpdateTime = DateTime.UtcNow;
            const int throttleMs = 50; // UI update throttle for smooth rendering

            var manager = TranslationManagerService.Instance.Manager;
            var serviceName = manager.Services[serviceId].DisplayName;
            ServiceText.Text = $"{serviceName} • Streaming...";

            await foreach (var chunk in manager.TranslateStreamAsync(
                request,
                _currentQueryCts!.Token,
                serviceId))
            {
                sb.Append(chunk);

                // Throttle UI updates to avoid excessive redraws
                var now = DateTime.UtcNow;
                if ((now - lastUpdateTime).TotalMilliseconds >= throttleMs)
                {
                    OutputTextBox.Text = sb.ToString();
                    lastUpdateTime = now;
                }
            }

            stopwatch.Stop();

            // Final update with complete text
            var finalText = sb.ToString().Trim();
            OutputTextBox.Text = finalText;

            TimingText.Text = $"⏱ {stopwatch.ElapsedMilliseconds}ms";

            // Update service text with detected language
            if (detectedLanguage != TranslationLanguage.Auto)
            {
                var langName = GetLanguageDisplayName(detectedLanguage);
                ServiceText.Text = $"{serviceName} • {langName}";
            }
            else
            {
                ServiceText.Text = serviceName;
            }

            UpdateStatus(true, "Ready");
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
            return language.GetDisplayName();
        }

        /// <summary>
        /// Update detected language display label.
        /// </summary>
        private void UpdateDetectedLanguageDisplay(TranslationLanguage detected)
        {
            if (!_isLoaded)
            {
                return;
            }

            if (detected != TranslationLanguage.Auto)
            {
                var displayName = detected.GetDisplayName();
                DetectedLanguageText.Text = $"Detected: {displayName}";
                DetectedLanguageText.Visibility = Visibility.Visible;
            }
            else
            {
                DetectedLanguageText.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Update target language selector based on detected source.
        /// </summary>
        private void UpdateTargetLanguageSelector(TranslationLanguage targetLang)
        {
            if (!_isLoaded)
            {
                return;
            }

            var targetIndex = LanguageToComboIndex(targetLang);

            // Update both Wide and Narrow layout ComboBoxes without triggering SelectionChanged
            _suppressTargetLanguageSelectionChanged = true;
            try
            {
                _isManualTargetSelection = false; // Temporarily disable manual flag
                if (targetIndex >= 0 && targetIndex < TargetLangCombo.Items.Count)
                {
                    TargetLangCombo.SelectedIndex = targetIndex;
                }
                if (targetIndex >= 0 && targetIndex < TargetLangComboNarrow.Items.Count)
                {
                    TargetLangComboNarrow.SelectedIndex = targetIndex;
                }
            }
            finally
            {
                _suppressTargetLanguageSelectionChanged = false;
            }
        }

        /// <summary>
        /// Convert Language enum to ComboBox index.
        /// </summary>
        private static int LanguageToComboIndex(TranslationLanguage lang) => lang switch
        {
            TranslationLanguage.English => 0,
            TranslationLanguage.SimplifiedChinese => 1,
            TranslationLanguage.Japanese => 2,
            TranslationLanguage.Korean => 3,
            TranslationLanguage.French => 4,
            TranslationLanguage.German => 5,
            TranslationLanguage.Spanish => 6,
            _ => 1 // Default to Chinese
        };

        /// <summary>
        /// Handle target language manual selection.
        /// </summary>
        private void OnTargetLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || _suppressTargetLanguageSelectionChanged)
            {
                return;
            }

            // User manually changed target language
            _isManualTargetSelection = true;

            // Re-translate if there's text in the input
            if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
            {
                _ = StartQueryAsync();
            }
        }

        /// <summary>
        /// Handle language swap button click (similar to macOS's ⌘+T).
        /// </summary>
        private void OnSwapLanguagesClicked(object sender, RoutedEventArgs e)
        {
            if (_lastDetectedLanguage == TranslationLanguage.Auto)
            {
                // No detection result, cannot swap
                return;
            }

            // Set target language to detected source language
            var newTargetIndex = LanguageToComboIndex(_lastDetectedLanguage);
            TargetLangCombo.SelectedIndex = newTargetIndex;
            TargetLangComboNarrow.SelectedIndex = newTargetIndex;

            _isManualTargetSelection = true; // Mark as manual selection

            // Note: Since source is always "Auto Detect", we only swap target
            // If source becomes selectable in the future, add source update here
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
