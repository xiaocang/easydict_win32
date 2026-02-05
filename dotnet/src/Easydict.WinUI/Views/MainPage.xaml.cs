using System.Diagnostics;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Views.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using TranslationLanguage = Easydict.TranslationService.Models.Language;

namespace Easydict.WinUI.Views
{
    /// <summary>
    /// Main translation page with Fluent Design.
    /// Follows macOS Easydict's user interaction patterns.
    /// Supports multiple translation services displayed simultaneously.
    /// </summary>
    public partial class MainPage : Page
    {
        private LanguageDetectionService? _detectionService;
        // Owned by StartQueryAsync() - only that method creates and disposes via its finally block.
        // Other code may Cancel() but must NOT Dispose().
        private CancellationTokenSource? _currentQueryCts;
        private Task? _currentQueryTask;
        private readonly SettingsService _settings = SettingsService.Instance;
        private readonly List<ServiceQueryResult> _serviceResults = new();
        private readonly TargetLanguageSelector _targetLanguageSelector;
        private TranslationLanguage _lastDetectedLanguage = TranslationLanguage.Auto;
        private bool _isLoaded;
        private volatile bool _isClosing;
        private bool _suppressTargetLanguageSelectionChanged;
        private bool _suppressSourceLanguageSelectionChanged;

        /// <summary>
        /// Maximum time to wait for in-flight query to complete during cleanup.
        /// </summary>
        private const int QueryShutdownTimeoutSeconds = 2;

        public MainPage()
        {
            _targetLanguageSelector = new TargetLanguageSelector(_settings);

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
            SourceLangCombo.SelectionChanged += (s, e) =>
            {
                _suppressSourceLanguageSelectionChanged = true;
                try { SyncComboSelection(SourceLangCombo, SourceLangComboNarrow); }
                finally { _suppressSourceLanguageSelectionChanged = false; }
            };
            SourceLangComboNarrow.SelectionChanged += (s, e) =>
            {
                _suppressSourceLanguageSelectionChanged = true;
                try { SyncComboSelection(SourceLangComboNarrow, SourceLangCombo); }
                finally { _suppressSourceLanguageSelectionChanged = false; }
            };
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

            // Apply localization to all UI elements
            ApplyLocalization();

            // Initialize service result controls based on enabled services
            InitializeServiceResults();
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
            // Apply target language from settings (using FirstLanguage)
            var targetLang = LanguageExtensions.FromCode(_settings.FirstLanguage);

            _suppressTargetLanguageSelectionChanged = true;
            try
            {
                var targetIndex = LanguageComboHelper.FindLanguageIndex(TargetLangCombo, targetLang);
                if (targetIndex >= 0)
                {
                    TargetLangCombo.SelectedIndex = targetIndex;
                }
                var targetIndexNarrow = LanguageComboHelper.FindLanguageIndex(TargetLangComboNarrow, targetLang);
                if (targetIndexNarrow >= 0)
                {
                    TargetLangComboNarrow.SelectedIndex = targetIndexNarrow;
                }

                _targetLanguageSelector.Reset();
            }
            finally
            {
                _suppressTargetLanguageSelectionChanged = false;
            }
        }

        /// <summary>
        /// Apply localization to all UI elements using LocalizationService.
        /// </summary>
        private void ApplyLocalization()
        {
            var loc = LocalizationService.Instance;

            // Source Language ComboBoxes (Wide layout) - 9 items: Auto + 8 languages
            ((ComboBoxItem)SourceLangCombo.Items[0]).Content = loc.GetString("LangAutoDetect");
            for (int i = 0; i < LanguageComboHelper.SelectableLanguages.Length; i++)
            {
                ((ComboBoxItem)SourceLangCombo.Items[i + 1]).Content =
                    loc.GetString(LanguageComboHelper.SelectableLanguages[i].LocalizationKey);
            }

            // Source Language ComboBoxes (Narrow layout) - 9 items: Auto + 8 languages
            ((ComboBoxItem)SourceLangComboNarrow.Items[0]).Content = loc.GetString("LangAutoDetect");
            for (int i = 0; i < LanguageComboHelper.SelectableLanguages.Length; i++)
            {
                ((ComboBoxItem)SourceLangComboNarrow.Items[i + 1]).Content =
                    loc.GetString(LanguageComboHelper.SelectableLanguages[i].LocalizationKey);
            }

            // Target Language ComboBoxes - 8 items (dynamically rebuilt, but localize initial XAML items)
            for (int i = 0; i < TargetLangCombo.Items.Count && i < LanguageComboHelper.SelectableLanguages.Length; i++)
            {
                ((ComboBoxItem)TargetLangCombo.Items[i]).Content =
                    loc.GetString(LanguageComboHelper.SelectableLanguages[i].LocalizationKey);
            }
            for (int i = 0; i < TargetLangComboNarrow.Items.Count && i < LanguageComboHelper.SelectableLanguages.Length; i++)
            {
                ((ComboBoxItem)TargetLangComboNarrow.Items[i]).Content =
                    loc.GetString(LanguageComboHelper.SelectableLanguages[i].LocalizationKey);
            }

            // Input placeholder
            InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder");

            // Output placeholder
            PlaceholderText.Text = loc.GetString("TranslationPlaceholder");

            // Tooltips
            ToolTipService.SetToolTip(SettingsButton, loc.GetString("SettingsTooltip"));
            ToolTipService.SetToolTip(SwapLanguageButton, loc.GetString("SwapLanguagesTooltip"));
            ToolTipService.SetToolTip(TranslateButton, loc.GetString("TranslateTooltip"));
            ToolTipService.SetToolTip(TranslateButtonNarrow, loc.GetString("TranslateTooltip"));
            ToolTipService.SetToolTip(SourceLangCombo, loc.GetString("SourceLanguageTooltip"));
            ToolTipService.SetToolTip(SourceLangComboNarrow, loc.GetString("SourceLanguageTooltip"));
            ToolTipService.SetToolTip(TargetLangCombo, loc.GetString("TargetLanguageTooltip"));
            ToolTipService.SetToolTip(TargetLangComboNarrow, loc.GetString("TargetLanguageTooltip"));
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
                UpdateStatus(null, LocalizationService.Instance.GetString("StatusInitializing"));

                var manager = TranslationManagerService.Instance.Manager;

                // DefaultServiceId is now managed centrally by TranslationManagerService
                UpdateStatus(true, LocalizationService.Instance.GetString("StatusReady"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainPage] Init error: {ex.Message}");
                UpdateStatus(false, LocalizationService.Instance.GetString("StatusError"));
            }
        }

        /// <summary>
        /// Initialize service result controls based on enabled services.
        /// </summary>
        private void InitializeServiceResults()
        {
            _serviceResults.Clear();
            ResultsPanel.Items.Clear();

            // Get enabled services and EnabledQuery settings from settings
            var enabledServices = _settings.MainWindowEnabledServices;
            var enabledQuerySettings = _settings.MainWindowServiceEnabledQuery;

            // If no services are enabled, show placeholder with guidance
            if (enabledServices.Count == 0)
            {
                PlaceholderText.Text = LocalizationService.Instance.GetString("NoServicesEnabled");
                PlaceholderText.Visibility = Visibility.Visible;
                return;
            }

            // Get display names from TranslationManager (single source of truth)
            var manager = TranslationManagerService.Instance.Manager;

            foreach (var serviceId in enabledServices)
            {
                // Use service-provided DisplayName, fallback to serviceId if not found
                var displayName = manager.Services.TryGetValue(serviceId, out var service)
                    ? service.DisplayName
                    : serviceId;

                // Get EnabledQuery setting (default true if not found)
                var enabledQuery = enabledQuerySettings.TryGetValue(serviceId, out var eq) ? eq : true;

                var result = new ServiceQueryResult
                {
                    ServiceId = serviceId,
                    ServiceDisplayName = displayName,
                    EnabledQuery = enabledQuery,
                    IsExpanded = enabledQuery // Manual-query services start collapsed
                };

                var control = new ServiceResultItem
                {
                    ServiceResult = result
                };
                control.CollapseToggled += OnServiceCollapseToggled;
                control.QueryRequested += OnServiceQueryRequested;

                _serviceResults.Add(result);
                ResultsPanel.Items.Add(control);
            }

            // Hide placeholder since we have services
            PlaceholderText.Text = LocalizationService.Instance.GetString("TranslationPlaceholder");
            PlaceholderText.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Handle collapse/expand toggle from a service result item.
        /// </summary>
        private void OnServiceCollapseToggled(object? sender, ServiceQueryResult result)
        {
            // Optional: could trigger layout update if needed
        }

        /// <summary>
        /// Handle query request from a manual-query service that user clicked to expand.
        /// </summary>
        private async void OnServiceQueryRequested(object? sender, ServiceQueryResult serviceResult)
        {
            if (_isClosing || _detectionService is null)
            {
                return;
            }

            var inputText = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(inputText))
            {
                return;
            }

            // Mark as loading and queried
            serviceResult.IsLoading = true;
            serviceResult.MarkQueried();

            try
            {
                // Detect language (use cached if available from recent query)
                var detectedLanguage = _lastDetectedLanguage != TranslationLanguage.Auto
                    ? _lastDetectedLanguage
                    : await _detectionService.DetectAsync(inputText, CancellationToken.None);

                // Get target language
                var targetLanguage = GetTargetLanguage();

                // Create request
                var request = new TranslationRequest
                {
                    Text = inputText,
                    FromLanguage = detectedLanguage,
                    ToLanguage = targetLanguage
                };

                // Execute translation
                using var handle = TranslationManagerService.Instance.AcquireHandle();
                var manager = handle.Manager;

                if (manager.IsStreamingService(serviceResult.ServiceId))
                {
                    await ExecuteStreamingTranslationForServiceAsync(
                        manager, serviceResult, request, detectedLanguage, targetLanguage, CancellationToken.None);
                }
                else
                {
                    var result = await manager.TranslateAsync(request, CancellationToken.None, serviceResult.ServiceId);
                    serviceResult.Result = result;
                    serviceResult.IsLoading = false;
                    serviceResult.ApplyAutoCollapseLogic();
                }
            }
            catch (TranslationException ex)
            {
                serviceResult.Error = ex;
                serviceResult.IsLoading = false;
                serviceResult.IsStreaming = false;
                serviceResult.ApplyAutoCollapseLogic();
            }
            catch (Exception ex)
            {
                serviceResult.Error = new TranslationException(ex.Message)
                {
                    ErrorCode = TranslationErrorCode.Unknown,
                    ServiceId = serviceResult.ServiceId
                };
                serviceResult.IsLoading = false;
                serviceResult.IsStreaming = false;
                serviceResult.ApplyAutoCollapseLogic();
            }
        }

        private async Task CleanupResourcesAsync()
        {
            CancelCurrentQuery();

            var task = _currentQueryTask;
            var detectionService = _detectionService;  // Capture before nulling

            _currentQueryTask = null;
            _detectionService = null;  // Clear immediately to prevent re-use

            if (task == null || task.IsCompleted)
            {
                detectionService?.Dispose();
                return;
            }

            bool waitSucceeded = true;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(QueryShutdownTimeoutSeconds));
            }
            catch (OperationCanceledException)
            {
                // Expected - task completed via cancellation
            }
            catch (TimeoutException)
            {
                waitSucceeded = false;
            }
            catch (Exception)
            {
                // Task faulted - treat as completed
            }

            if (waitSucceeded)
            {
                detectionService?.Dispose();
            }
            else
            {
                // Schedule deferred disposal when task eventually completes
                _ = task.ContinueWith(
                    _ => detectionService?.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
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
        /// Play the source text using text-to-speech with the detected language voice.
        /// </summary>
        private async void OnSourcePlayClicked(object sender, RoutedEventArgs e)
        {
            var text = InputTextBox.Text;
            if (string.IsNullOrWhiteSpace(text))
                return;

            // Use detected language if available, otherwise default to English
            var language = _lastDetectedLanguage != TranslationLanguage.Auto
                ? _lastDetectedLanguage
                : TranslationLanguage.English;

            var tts = TextToSpeechService.Instance;

            void ResetIcon()
            {
                tts.PlaybackEnded -= ResetIcon;
                DispatcherQueue.TryEnqueue(() => SourcePlayIcon.Glyph = "\uE768");
            }

            SourcePlayIcon.Glyph = "\uE71A"; // Stop icon
            tts.PlaybackEnded += ResetIcon;
            await tts.SpeakAsync(text, language);
        }

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
        /// Start a new translation query for all enabled services.
        /// Executes translations in parallel for multiple services.
        /// </summary>
        private async Task StartQueryAsync()
        {
            if (_isClosing)
            {
                return;
            }

            if (_detectionService is null)
            {
                StatusSummaryText.Text = LocalizationService.Instance.GetString("ServiceNotInitialized");
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

            // Early return if no services are enabled
            if (_serviceResults.Count == 0)
            {
                StatusSummaryText.Text = LocalizationService.Instance.GetString("NoServicesEnabled");
                return;
            }

            using var currentCts = new CancellationTokenSource();
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

                // Reset all service results
                foreach (var result in _serviceResults)
                {
                    result.Reset();
                    // Only set loading for auto-query services
                    if (result.EnabledQuery)
                    {
                        result.IsLoading = true;
                    }
                }

                // Hide placeholder
                PlaceholderText.Visibility = Visibility.Collapsed;

                // Step 1: Detect language (only when source = Auto)
                var sourceLanguage = GetSourceLanguage();
                TranslationLanguage detectedLanguage;
                if (sourceLanguage == TranslationLanguage.Auto)
                {
                    detectedLanguage = await detectionService.DetectAsync(inputText, ct);
                    UpdateDetectedLanguageDisplay(detectedLanguage);
                }
                else
                {
                    detectedLanguage = sourceLanguage;
                    DetectedLanguageText.Visibility = Visibility.Collapsed;
                }
                _lastDetectedLanguage = detectedLanguage;

                // Step 2: Determine target language
                var currentTarget = GetTargetLanguage();
                var targetLanguage = _targetLanguageSelector.ResolveTargetLanguage(
                    detectedLanguage, currentTarget, detectionService);
                if (targetLanguage != currentTarget)
                {
                    UpdateTargetLanguageSelector(targetLanguage);
                }

                // Step 3: Execute translation for each enabled service in parallel
                var request = new TranslationRequest
                {
                    Text = inputText,
                    FromLanguage = detectedLanguage,
                    ToLanguage = targetLanguage
                };

                // Task returns: true = success, false = error, null = cancelled/skipped
                // Only auto-query services with EnabledQuery=true
                var tasks = _serviceResults.Select(async serviceResult =>
                {
                    // Skip manual-query services (EnabledQuery=false)
                    if (!serviceResult.EnabledQuery)
                    {
                        return (bool?)null; // Skipped, don't count
                    }

                    // Mark as queried for auto-query services
                    serviceResult.MarkQueried();

                    try
                    {
                        // Acquire handle once per service to ensure consistent manager instance
                        using var handle = TranslationManagerService.Instance.AcquireHandle();
                        var manager = handle.Manager;

                        // Check if service supports streaming
                        if (manager.IsStreamingService(serviceResult.ServiceId))
                        {
                            // Streaming path for LLM services
                            await ExecuteStreamingTranslationForServiceAsync(
                                manager, serviceResult, request, detectedLanguage, targetLanguage, ct);
                        }
                        else
                        {
                            // Non-streaming path for traditional services
                            var result = await manager.TranslateAsync(
                                request, ct, serviceResult.ServiceId);

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_isClosing) return;
                                serviceResult.Result = result;
                                serviceResult.IsLoading = false;
                                serviceResult.ApplyAutoCollapseLogic();
                            });
                        }

                        return (bool?)true; // Success
                    }
                    catch (OperationCanceledException)
                    {
                        // Ensure UI state is reset when the operation is cancelled
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_isClosing) return;
                            serviceResult.IsLoading = false;
                            serviceResult.IsStreaming = false;
                        });
                        return (bool?)null; // Cancelled, don't count
                    }
                    catch (TranslationException ex)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_isClosing) return;
                            serviceResult.Error = ex;
                            serviceResult.IsLoading = false;
                            serviceResult.IsStreaming = false;
                            serviceResult.ApplyAutoCollapseLogic();
                        });
                        return (bool?)false; // Error
                    }
                    catch (Exception ex)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_isClosing) return;
                            serviceResult.Error = new TranslationException(ex.Message)
                            {
                                ErrorCode = TranslationErrorCode.Unknown,
                                ServiceId = serviceResult.ServiceId
                            };
                            serviceResult.IsLoading = false;
                            serviceResult.IsStreaming = false;
                            serviceResult.ApplyAutoCollapseLogic();
                        });
                        return (bool?)false; // Error
                    }
                });

                var taskResults = await Task.WhenAll(tasks);

                // Compute counts from task return values (accurate regardless of DispatcherQueue timing)
                var successCount = taskResults.Count(r => r == true);
                var errorCount = taskResults.Count(r => r == false);

                // Update status on UI thread
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isClosing) return;

                    var loc = LocalizationService.Instance;
                    // Set status based on aggregated outcomes
                    if (successCount > 0)
                    {
                        StatusSummaryText.Text = string.Format(loc.GetString("ServiceResultsComplete"), successCount);
                        UpdateStatus(true, loc.GetString("StatusReady"));
                    }
                    else if (errorCount > 0)
                    {
                        StatusSummaryText.Text = loc.GetString("TranslationFailed");
                        UpdateStatus(false, loc.GetString("StatusError"));
                    }
                    else
                    {
                        StatusSummaryText.Text = "";
                        UpdateStatus(true, loc.GetString("StatusReady"));
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Query was cancelled - reset all service results that may be stuck in loading state
                ResetAllServiceResultsLoadingState();
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    StatusSummaryText.Text = $"Error: {ex.Message}";
                    UpdateStatus(false, LocalizationService.Instance.GetString("StatusError"));

                    // Reset all service results that may be stuck in loading state
                    ResetAllServiceResultsLoadingState();
                }
            }
            finally
            {
                if (!_isClosing) SetLoading(false);
                Interlocked.CompareExchange(ref _currentQueryCts, null, currentCts);
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
        /// Reset all service results to clear loading/streaming state.
        /// Called when an exception occurs before per-service tasks can handle cleanup.
        /// </summary>
        private void ResetAllServiceResultsLoadingState()
        {
            foreach (var serviceResult in _serviceResults)
            {
                serviceResult.IsLoading = false;
                serviceResult.IsStreaming = false;
                serviceResult.StreamingText = "";
            }
        }

        /// <summary>
        /// Execute streaming translation for a single service.
        /// Updates the ServiceQueryResult's StreamingText as chunks arrive.
        /// Manager is passed from caller who already acquired a handle to ensure consistent instance.
        /// </summary>
        private async Task ExecuteStreamingTranslationForServiceAsync(
            TranslationManager manager,
            ServiceQueryResult serviceResult,
            TranslationRequest request,
            TranslationLanguage detectedLanguage,
            TranslationLanguage targetLanguage,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var sb = new StringBuilder();
            var lastUpdateTime = DateTime.UtcNow;
            const int throttleMs = 50;

            // Mark as streaming
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;
                serviceResult.IsLoading = false;
                serviceResult.IsStreaming = true;
                serviceResult.StreamingText = "";
            });

            await foreach (var chunk in manager.TranslateStreamAsync(
                request, ct, serviceResult.ServiceId))
            {
                sb.Append(chunk);

                // Throttle UI updates
                var now = DateTime.UtcNow;
                if ((now - lastUpdateTime).TotalMilliseconds >= throttleMs)
                {
                    var currentText = sb.ToString();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;
                        serviceResult.StreamingText = currentText;
                    });
                    lastUpdateTime = now;
                }
            }

            stopwatch.Stop();

            // Final update with complete result (apply same cleanup as non-streaming path)
            var finalText = CleanupStreamingResult(sb.ToString());

            // Create initial result
            var result = new TranslationResult
            {
                TranslatedText = finalText,
                OriginalText = request.Text,
                DetectedLanguage = detectedLanguage,
                TargetLanguage = targetLanguage,
                ServiceName = serviceResult.ServiceDisplayName,
                TimingMs = stopwatch.ElapsedMilliseconds
            };

            // Enrich with phonetics from Youdao if missing (for word queries)
            try
            {
                result = await manager.EnrichPhoneticsIfMissingAsync(result, request, ct);
            }
            catch
            {
                // Best-effort: continue with original result if enrichment fails
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;
                serviceResult.IsStreaming = false;
                serviceResult.StreamingText = "";
                serviceResult.Result = result;
                serviceResult.ApplyAutoCollapseLogic();
            });
        }

        /// <summary>
        /// Clean up streaming result text, applying the same normalization as non-streaming translations.
        /// Removes common artifacts like surrounding quotes and extra whitespace.
        /// </summary>
        private static string CleanupStreamingResult(string text)
        {
            var result = text.Trim();

            // Remove surrounding quotes if present (LLMs sometimes wrap translations in quotes)
            if (result.Length >= 2 &&
                result.StartsWith('"') && result.EndsWith('"'))
            {
                result = result[1..^1].Trim();
            }

            return result;
        }

        private TranslationLanguage GetSourceLanguage()
        {
            return LanguageComboHelper.GetSelectedLanguage(SourceLangCombo);
        }

        private TranslationLanguage GetTargetLanguage()
        {
            return LanguageComboHelper.GetSelectedLanguage(TargetLangCombo);
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
                DetectedLanguageText.Text = string.Format(
                    LocalizationService.Instance.GetString("DetectedLanguage"),
                    displayName);
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

            // Update both Wide and Narrow layout ComboBoxes without triggering SelectionChanged
            _suppressTargetLanguageSelectionChanged = true;
            try
            {
                var targetIndex = LanguageComboHelper.FindLanguageIndex(TargetLangCombo, targetLang);
                if (targetIndex >= 0)
                {
                    TargetLangCombo.SelectedIndex = targetIndex;
                }
                var targetIndexNarrow = LanguageComboHelper.FindLanguageIndex(TargetLangComboNarrow, targetLang);
                if (targetIndexNarrow >= 0)
                {
                    TargetLangComboNarrow.SelectedIndex = targetIndexNarrow;
                }
            }
            finally
            {
                _suppressTargetLanguageSelectionChanged = false;
            }
        }

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
            _targetLanguageSelector.MarkManualSelection();

            // Re-translate if there's text in the input
            if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
            {
                _ = StartQueryTrackedAsync();
            }
        }

        /// <summary>
        /// Handle language swap button click (similar to macOS's ⌘+T).
        /// </summary>
        private void OnSwapLanguagesClicked(object sender, RoutedEventArgs e)
        {
            var sourceLanguage = GetSourceLanguage();

            if (sourceLanguage == TranslationLanguage.Auto)
            {
                // Source is Auto: swap target to detected language
                if (_lastDetectedLanguage == TranslationLanguage.Auto)
                    return; // No detection result, cannot swap

                UpdateTargetLanguageSelector(_lastDetectedLanguage);
                _targetLanguageSelector.MarkManualSelection();
            }
            else
            {
                // Source is specific: swap source ↔ target
                var currentTarget = GetTargetLanguage();
                var newSource = currentTarget;
                var newTarget = sourceLanguage;

                // Set source to current target
                _suppressSourceLanguageSelectionChanged = true;
                try
                {
                    var srcIdx = LanguageComboHelper.FindLanguageIndex(SourceLangCombo, newSource);
                    if (srcIdx >= 0) SourceLangCombo.SelectedIndex = srcIdx;
                    var srcIdxNarrow = LanguageComboHelper.FindLanguageIndex(SourceLangComboNarrow, newSource);
                    if (srcIdxNarrow >= 0) SourceLangComboNarrow.SelectedIndex = srcIdxNarrow;
                }
                finally
                {
                    _suppressSourceLanguageSelectionChanged = false;
                }

                // Rebuild target combos excluding new source
                RebuildTargetCombos(newSource, newTarget);
                _targetLanguageSelector.MarkManualSelection();

                // Re-translate if text exists
                if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
                {
                    _ = StartQueryTrackedAsync();
                }
            }
        }

        /// <summary>
        /// Handle source language selection change.
        /// Rebuilds target combo to exclude source language (mutual exclusion).
        /// </summary>
        private void OnSourceLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || _suppressSourceLanguageSelectionChanged)
            {
                return;
            }

            var sourceLanguage = GetSourceLanguage();
            var currentTarget = GetTargetLanguage();

            RebuildTargetCombos(sourceLanguage, currentTarget);

            // Re-translate if text exists
            if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
            {
                _ = StartQueryTrackedAsync();
            }
        }

        /// <summary>
        /// Rebuild both Wide and Narrow target combos excluding the source language.
        /// </summary>
        private void RebuildTargetCombos(TranslationLanguage sourceLanguage, TranslationLanguage currentTarget)
        {
            var loc = LocalizationService.Instance;

            _suppressTargetLanguageSelectionChanged = true;
            try
            {
                LanguageComboHelper.RebuildTargetCombo(
                    TargetLangCombo, sourceLanguage, currentTarget, loc, out var newTarget);
                LanguageComboHelper.RebuildTargetCombo(
                    TargetLangComboNarrow, sourceLanguage, currentTarget, loc, out _);

                // If target changed due to reversal, mark manual selection
                if (newTarget != currentTarget)
                {
                    _targetLanguageSelector.MarkManualSelection();
                }
            }
            finally
            {
                _suppressTargetLanguageSelectionChanged = false;
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
            _targetLanguageSelector.Reset();
            InputTextBox.Text = text;
            _ = StartQueryTrackedAsync();
        }

        /// <summary>
        /// Cancel the current query's CTS without disposing it; disposal happens in StartQueryAsync's finally.
        /// </summary>
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
