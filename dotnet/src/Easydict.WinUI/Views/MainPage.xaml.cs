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
        // Owned by StartQueryAsync() - only that method creates and disposes via its finally block.
        // Other code may Cancel() but must NOT Dispose().
        private CancellationTokenSource? _currentQueryCts;
        private Task? _currentQueryTask;
        private readonly SettingsService _settings = SettingsService.Instance;
        private TranslationLanguage _lastDetectedLanguage = TranslationLanguage.Auto;
        private bool _isManualTargetSelection = false; // Track if user manually selected target
        private string _lastQueryText = ""; // Track last query text to detect changes
        private bool _isLoaded;
        private volatile bool _isClosing;
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
            _isClosing = false;
            _isLoaded = true;
            InitializeTranslationServices();
            if (_detectionService is null)
            {
                _detectionService = new LanguageDetectionService(_settings);
            }
            SetLoading(false);
            ApplySettings();
        }

        private async void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _isLoaded = false;
                _isClosing = true;
                App.ClipboardTextReceived -= OnClipboardTextReceived;
                await CleanupResourcesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainPage] OnPageUnloaded error: {ex}");
            }
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
            if (_isClosing)
            {
                return;
            }

            // Auto-translate clipboard text
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (_isClosing)
                {
                    return;
                }
                InputTextBox.Text = text;
                await StartQueryTrackedAsync();
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

        private async Task CleanupResourcesAsync()
        {
            CancelCurrentQuery();

            // Wait for in-flight query to complete (non-blocking with timeout)
            var task = _currentQueryTask;
            bool waitSucceeded = true;
            if (task != null && !task.IsCompleted)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException)
                {
                    // Expected if task was cancelled - wait succeeded (task completed via cancellation)
                }
                catch (TimeoutException)
                {
                    // Timeout - task is still running, do NOT dispose resources
                    waitSucceeded = false;
                }
                catch (Exception)
                {
                    // Task faulted - treat as completed (faulted tasks are done)
                    // Continue with dispose
                }
            }
            _currentQueryTask = null;

            // Only dispose if wait succeeded (task completed or was cancelled)
            if (waitSucceeded)
            {
                _detectionService?.Dispose();
                _detectionService = null;
            }
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
            if (_isClosing) return;

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
            await StartQueryTrackedAsync();
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
                    await StartQueryTrackedAsync();
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
                await StartQueryTrackedAsync();
            }
            catch
            {
                // Fallback: trigger translation on plain Enter if modifier detection fails
                e.Handled = true;
                await StartQueryTrackedAsync();
            }
        }

        /// <summary>
        /// Start a new translation query (similar to macOS's startQueryText:).
        /// </summary>
        private async Task StartQueryAsync()
        {
            if (_isClosing)
            {
                return;
            }

            if (_detectionService is null)
            {
                OutputTextBox.Text = "Service not initialized. Please wait...";
                InitializeTranslationServices();
                return;
            }

            // Capture service locally to avoid races if cleanup nulls the field
            var detectionService = _detectionService;

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

            var currentCts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref _currentQueryCts, currentCts);

            if (previousCts != null)
            {
                try
                {
                    previousCts.Cancel();
                }
                catch
                {
                    // Ignore cancellation exceptions during cleanup
                }
                // Don't dispose - let the query's finally block dispose it
            }

            var ct = currentCts.Token;

            try
            {
                if (_isClosing) return;
                SetLoading(true);
                OutputTextBox.Text = "";
                TimingText.Text = "";

                // Step 1: Detect language
                var detectedLanguage = await detectionService.DetectAsync(
                    inputText,
                    ct);

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
                    targetLanguage = detectionService.GetTargetLanguage(detectedLanguage);
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

                // Acquire handle to prevent manager disposal during translation.
                // Capture DefaultServiceId atomically with manager to prevent race condition.
                using var handle = TranslationManagerService.Instance.AcquireHandle();
                var manager = handle.Manager;
                var serviceId = manager.DefaultServiceId;

                // Check if service supports streaming
                if (manager.IsStreamingService(serviceId))
                {
                    // Streaming path for LLM services
                    await ExecuteStreamingTranslationAsync(manager, request, serviceId, detectedLanguage, ct);
                }
                else
                {
                    // Non-streaming path for traditional services
                    var result = await manager.TranslateAsync(
                        request,
                        ct,
                        serviceId);

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
                if (!_isClosing)
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
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    OutputTextBox.Text = $"Error: {ex.Message}";
                    UpdateStatus(false, "Error");
                }
            }
            finally
            {
                if (!_isClosing) SetLoading(false);
                Interlocked.CompareExchange(ref _currentQueryCts, null, currentCts);
                currentCts.Dispose();
            }
        }

        /// <summary>
        /// Wrapper that always tracks the query task before returning.
        /// Avoids "downgrading" from a running real task to a no-op completed task.
        /// </summary>
        private Task StartQueryTrackedAsync()
        {
            var oldTask = _currentQueryTask;
            var newTask = StartQueryAsync();
            Task trackedTask;

            // Only update _currentQueryTask if:
            // - newTask is still running (it's a real query), OR
            // - oldTask is null or already completed (nothing valuable to preserve)
            if (!newTask.IsCompleted || oldTask == null || oldTask.IsCompleted)
            {
                _currentQueryTask = newTask;
                trackedTask = newTask;
            }
            else
            {
                trackedTask = oldTask;
            }

            return trackedTask;
        }

        /// <summary>
        /// Execute streaming translation for LLM services.
        /// Shows incremental results as they arrive from the API.
        /// The caller must provide a manager from an acquired SafeManagerHandle.
        /// </summary>
        private async Task ExecuteStreamingTranslationAsync(
            TranslationManager manager,
            TranslationRequest request,
            string serviceId,
            TranslationLanguage detectedLanguage,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var sb = new StringBuilder();
            var lastUpdateTime = DateTime.UtcNow;
            const int throttleMs = 50; // UI update throttle for smooth rendering

            var serviceName = manager.Services[serviceId].DisplayName;
            if (_isClosing) return;
            ServiceText.Text = $"{serviceName} • Streaming...";

            await foreach (var chunk in manager.TranslateStreamAsync(
                request,
                cancellationToken,
                serviceId))
            {
                sb.Append(chunk);

                // Throttle UI updates to avoid excessive redraws
                var now = DateTime.UtcNow;
                if ((now - lastUpdateTime).TotalMilliseconds >= throttleMs)
                {
                    if (_isClosing) return;
                    OutputTextBox.Text = sb.ToString();
                    lastUpdateTime = now;
                }
            }

            stopwatch.Stop();

            if (_isClosing) return;

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
                StartQueryTrackedAsync();
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
            StartQueryTrackedAsync();
        }

        private void CancelCurrentQuery()
        {
            var cts = Interlocked.Exchange(ref _currentQueryCts, null);
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
                // Ignore cancellation exceptions during cleanup
            }
            // Don't dispose - let the query's finally block dispose it
        }
    }
}
