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
        private TranslationLanguage _lastDetectedLanguage = TranslationLanguage.Auto;
        private bool _isManualTargetSelection = false; // Track if user manually selected target
        private string _lastQueryText = ""; // Track last query text to detect changes
        private bool _isLoaded;
        private volatile bool _isClosing;
        private bool _suppressTargetLanguageSelectionChanged;

        /// <summary>
        /// Maximum time to wait for in-flight query to complete during cleanup.
        /// </summary>
        private const int QueryShutdownTimeoutSeconds = 2;

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
            var targetLang = _settings.FirstLanguage;
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

        /// <summary>
        /// Apply localization to all UI elements using LocalizationService.
        /// </summary>
        private void ApplyLocalization()
        {
            var loc = LocalizationService.Instance;

            // Source Language ComboBoxes (Wide layout) - ONLY 4 items in XAML!
            ((ComboBoxItem)SourceLangCombo.Items[0]).Content = loc.GetString("LangAutoDetect");
            ((ComboBoxItem)SourceLangCombo.Items[1]).Content = loc.GetString("LangEnglish");
            ((ComboBoxItem)SourceLangCombo.Items[2]).Content = loc.GetString("LangChinese");
            ((ComboBoxItem)SourceLangCombo.Items[3]).Content = loc.GetString("LangJapanese");

            // Source Language ComboBoxes (Narrow layout) - ONLY 4 items in XAML!
            ((ComboBoxItem)SourceLangComboNarrow.Items[0]).Content = loc.GetString("LangAutoDetect");
            ((ComboBoxItem)SourceLangComboNarrow.Items[1]).Content = loc.GetString("LangEnglish");
            ((ComboBoxItem)SourceLangComboNarrow.Items[2]).Content = loc.GetString("LangChinese");
            ((ComboBoxItem)SourceLangComboNarrow.Items[3]).Content = loc.GetString("LangJapanese");

            // Target Language ComboBoxes (Wide layout) - 7 items
            ((ComboBoxItem)TargetLangCombo.Items[0]).Content = loc.GetString("LangEnglish");
            ((ComboBoxItem)TargetLangCombo.Items[1]).Content = loc.GetString("LangChinese");
            ((ComboBoxItem)TargetLangCombo.Items[2]).Content = loc.GetString("LangJapanese");
            ((ComboBoxItem)TargetLangCombo.Items[3]).Content = loc.GetString("LangKorean");
            ((ComboBoxItem)TargetLangCombo.Items[4]).Content = loc.GetString("LangFrench");
            ((ComboBoxItem)TargetLangCombo.Items[5]).Content = loc.GetString("LangGerman");
            ((ComboBoxItem)TargetLangCombo.Items[6]).Content = loc.GetString("LangSpanish");

            // Target Language ComboBoxes (Narrow layout) - 7 items
            ((ComboBoxItem)TargetLangComboNarrow.Items[0]).Content = loc.GetString("LangEnglish");
            ((ComboBoxItem)TargetLangComboNarrow.Items[1]).Content = loc.GetString("LangChinese");
            ((ComboBoxItem)TargetLangComboNarrow.Items[2]).Content = loc.GetString("LangJapanese");
            ((ComboBoxItem)TargetLangComboNarrow.Items[3]).Content = loc.GetString("LangKorean");
            ((ComboBoxItem)TargetLangComboNarrow.Items[4]).Content = loc.GetString("LangFrench");
            ((ComboBoxItem)TargetLangComboNarrow.Items[5]).Content = loc.GetString("LangGerman");
            ((ComboBoxItem)TargetLangComboNarrow.Items[6]).Content = loc.GetString("LangSpanish");

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

            // Reset manual target selection when text changes
            // This ensures auto-detection works for new input (macOS behavior)
            if (inputText != _lastQueryText)
            {
                _isManualTargetSelection = false;
                _lastQueryText = inputText;
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
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;
                serviceResult.IsStreaming = false;
                serviceResult.StreamingText = "";
                serviceResult.Result = new TranslationResult
                {
                    TranslatedText = finalText,
                    OriginalText = request.Text,
                    DetectedLanguage = detectedLanguage,
                    TargetLanguage = targetLanguage,
                    ServiceName = serviceResult.ServiceDisplayName,
                    TimingMs = stopwatch.ElapsedMilliseconds
                };
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
                _ = StartQueryTrackedAsync();
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
