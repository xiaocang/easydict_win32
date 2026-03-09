using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using Easydict.WinUI.Views.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;
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
        // Owned by OnServiceQueryRequested() - only that method creates and disposes via its finally block.
        // Other code may Cancel() but must NOT Dispose().
        private CancellationTokenSource? _manualQueryCts;
        private Task? _currentQueryTask;
        private readonly SettingsService _settings = SettingsService.Instance;
        private readonly List<ServiceQueryResult> _serviceResults = new();
        private readonly List<ServiceResultItem> _resultControls = new();
        private readonly TargetLanguageSelector _targetLanguageSelector;
        private TranslationLanguage _lastDetectedLanguage = TranslationLanguage.Auto;
        private bool _isLoaded;
        private bool _isQuerying;
        private volatile bool _isClosing;
        private bool _suppressTargetLanguageSelectionChanged;
        private bool _suppressSourceLanguageSelectionChanged;
        private QueryMode _currentMode = QueryMode.Translation;
        private readonly Services.LongDocumentTranslationService _longDocumentService = new();
        private readonly LongDocumentDeduplicationService _longDocDedupService = new();
        private LongDocumentTranslationCheckpoint? _longDocCheckpoint;
        private TranslationLanguage _longDocLastFrom = TranslationLanguage.Auto;
        private TranslationLanguage _longDocLastTo = TranslationLanguage.English;
        private string _longDocLastServiceId = string.Empty;
        private string _longDocLastDedupKey = string.Empty;
        private CancellationTokenSource? _longDocSingleTaskCts;
        private CancellationTokenSource? _longDocQueueCts;
        private Task? _longDocQueueTask;
        private readonly ObservableCollection<LongDocFileItem> _longDocFileItems = new();
        private readonly ObservableCollection<LongDocHistoryItem> _longDocHistoryItems = new();
        private string _longDocOutputFolder = "";
        private bool _isLongDocTranslating;
        private ContentDialog? _currentDialog;

        /// <summary>
        /// Maximum time to wait for in-flight query to complete during cleanup.
        /// </summary>
        private const int QueryShutdownTimeoutSeconds = 2;

        /// <summary>
        /// Maximum history items to keep.
        /// </summary>
        private const int MaxHistoryItems = 50;

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
#if DEBUG
            Services.MemoryDiagnostics.LogSnapshot("MainPage.OnPageLoaded");
#endif
            _isClosing = false;
            _isLoaded = true;
            InitializeTranslationServices();
            if (_detectionService is null)
            {
                _detectionService = new LanguageDetectionService(_settings);
            }
            SetLoading(false);

            // Apply localization first (populates combos), then settings (selects saved language)
            ApplyLocalization();
            ApplySettings();

            // Initialize service result controls based on enabled services
            InitializeServiceResults();
            InitializeLongDocServices();
            InitializeLongDocOutputDefaults();
            OnLongDocInputModeChanged(LongDocInputModeCombo, null!);
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
        /// Also dynamically populates language combo boxes from user's selected languages.
        /// </summary>
        private void ApplyLocalization()
        {
            var loc = LocalizationService.Instance;

            // Populate language combos dynamically from user's selected languages
            _suppressSourceLanguageSelectionChanged = true;
            _suppressTargetLanguageSelectionChanged = true;
            try
            {
                LanguageComboHelper.PopulateSourceCombo(SourceLangCombo, loc);
                LanguageComboHelper.PopulateSourceCombo(SourceLangComboNarrow, loc);
                LanguageComboHelper.PopulateTargetCombo(TargetLangCombo, loc);
                LanguageComboHelper.PopulateTargetCombo(TargetLangComboNarrow, loc);

                // Long Doc language combos — default source to Auto, target to user's FirstLanguage
                LanguageComboHelper.PopulateSourceCombo(LongDocSourceLangCombo, loc);
                LanguageComboHelper.PopulateTargetCombo(LongDocTargetLangCombo, loc, _settings.FirstLanguage);
            }
            finally
            {
                _suppressSourceLanguageSelectionChanged = false;
                _suppressTargetLanguageSelectionChanged = false;
            }

            // Apply mode state (emoji, subtitle, menu item texts)
            ApplyModeState();

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

            // Help icon tooltips
            ToolTipService.SetToolTip(LangHelpIcon, loc.GetString("LanguagePickerHelpTip"));
            ToolTipService.SetToolTip(LangHelpIconNarrow, loc.GetString("LanguagePickerHelpTip"));
            ToolTipService.SetToolTip(InputHelpIcon, loc.GetString("InputHelpTip"));

            // Long doc language combo tooltips
            ToolTipService.SetToolTip(LongDocSourceLangCombo, loc.GetString("SourceLanguageTooltip"));
            ToolTipService.SetToolTip(LongDocTargetLangCombo, loc.GetString("TargetLanguageTooltip"));

            // Long doc service hint (shown as tooltip on 🤖? icon next to service combo)
            ToolTipService.SetToolTip(LongDocServiceHint, loc.GetString("LongDoc_ServiceHint"));

            // Long doc control help icons
            ToolTipService.SetToolTip(LongDocInputModeHint, loc.GetString("LongDoc_InputModeHelpTip"));
            ToolTipService.SetToolTip(LongDocOutputModeHint, loc.GetString("LongDoc_OutputModeHelpTip"));
            ToolTipService.SetToolTip(LongDocConcurrencyHint, loc.GetString("LongDoc_ConcurrencyHelpTip"));
            ToolTipService.SetToolTip(LongDocPageRangeHint, loc.GetString("LongDoc_PageRangeHelpTip"));
        }

        /// <summary>
        /// Apply all UI state for the current mode (emoji, subtitle, content visibility, grammar-specific controls).
        /// </summary>
        private void ApplyModeState()
        {
            var loc = LocalizationService.Instance;

            // Update header emoji
            ModeEmojiIcon.Text = _currentMode switch
            {
                QueryMode.GrammarCorrection => "✏️",
                QueryMode.LongDocument => "📄",
                _ => "🌐"
            };

            // Update subtitle
            switch (_currentMode)
            {
                case QueryMode.GrammarCorrection:
                    ModeSubtitle.Text = loc.GetString("QueryMode_GrammarCorrection") ?? "Grammar Correction";
                    ModeSubtitle.Visibility = Visibility.Visible;
                    break;
                case QueryMode.LongDocument:
                    ModeSubtitle.Text = loc.GetString("Mode_LongDocument") ?? "Long Document";
                    ModeSubtitle.Visibility = Visibility.Visible;
                    break;
                default:
                    ModeSubtitle.Visibility = Visibility.Collapsed;
                    break;
            }

            // Toggle main content areas
            var isLongDoc = _currentMode == QueryMode.LongDocument;
            QuickTranslateContent.Visibility = isLongDoc ? Visibility.Collapsed : Visibility.Visible;
            LongDocContent.Visibility = isLongDoc ? Visibility.Visible : Visibility.Collapsed;

            // Grammar-specific UI (only relevant when not in LongDoc mode)
            if (_currentMode == QueryMode.GrammarCorrection)
            {
                TargetLangCombo.Visibility = Visibility.Collapsed;
                SwapLanguageButton.Visibility = Visibility.Collapsed;
                LangHelpIcon.Visibility = Visibility.Collapsed;
                TargetLangComboNarrow.Visibility = Visibility.Collapsed;
                SwapLanguageButtonNarrow.Visibility = Visibility.Collapsed;
                LangHelpIconNarrow.Visibility = Visibility.Collapsed;

                InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder_Grammar")
                    ?? "Enter text to check grammar...";
                ResultsTitleText.Text = loc.GetString("ResultsTitle_Grammar")
                    ?? "Grammar Check Results";
                PlaceholderText.Text = loc.GetString("GrammarPlaceholder")
                    ?? "Grammar check results will appear here...";
                ToolTipService.SetToolTip(TranslateButton,
                    loc.GetString("TranslateButton_Grammar_Tooltip") ?? "Check Grammar");
                ToolTipService.SetToolTip(TranslateButtonNarrow,
                    loc.GetString("TranslateButton_Grammar_Tooltip") ?? "Check Grammar");
            }
            else if (_currentMode == QueryMode.Translation)
            {
                TargetLangCombo.Visibility = Visibility.Visible;
                SwapLanguageButton.Visibility = Visibility.Visible;
                LangHelpIcon.Visibility = Visibility.Visible;
                TargetLangComboNarrow.Visibility = Visibility.Visible;
                SwapLanguageButtonNarrow.Visibility = Visibility.Visible;
                LangHelpIconNarrow.Visibility = Visibility.Visible;

                InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder");
                ResultsTitleText.Text = loc.GetString("TranslationResults")
                    ?? "Translation Results";
                PlaceholderText.Text = loc.GetString("TranslationPlaceholder");
                ToolTipService.SetToolTip(TranslateButton, loc.GetString("TranslateTooltip"));
                ToolTipService.SetToolTip(TranslateButtonNarrow, loc.GetString("TranslateTooltip"));
            }

            // Localize menu item texts
            ModeTranslationItem.Text = "🌐  " + (loc.GetString("QueryMode_Translation") ?? "Quick Translation");
            ModeGrammarItem.Text = "✏️  " + (loc.GetString("QueryMode_GrammarCorrection") ?? "Grammar Correction");
            ModeLongDocItem.Text = "📄  " + (loc.GetString("Mode_LongDocument") ?? "Long Document");

            // Re-initialize service results (grammar mode filters to grammar-capable services)
            if (!isLongDoc)
            {
                InitializeServiceResults();
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

                // Switch out of Long Document mode for quick translate
                if (_currentMode == QueryMode.LongDocument)
                {
                    _currentMode = QueryMode.Translation;
                    ModeTranslationItem.IsChecked = true;
                    ApplyModeState();
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
            _resultControls.Clear();
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
                var displayName = serviceId;
                if (manager.Services.TryGetValue(serviceId, out var service))
                {
                    displayName = service.DisplayName;

                    // In grammar mode, only show LLM services that implement IGrammarCorrectionService
                    if (_currentMode == QueryMode.GrammarCorrection &&
                        service is not IGrammarCorrectionService)
                    {
                        continue;
                    }
                }

                // Get EnabledQuery setting (default true if not found)
                var enabledQuery = enabledQuerySettings.TryGetValue(serviceId, out var eq) ? eq : true;

                var result = new ServiceQueryResult
                {
                    ServiceId = serviceId,
                    ServiceDisplayName = displayName,
                    EnabledQuery = enabledQuery,
                    IsExpanded = enabledQuery, // Manual-query services start collapsed
                    CurrentMode = _currentMode
                };

                var control = new ServiceResultItem
                {
                    ServiceResult = result
                };
                control.CollapseToggled += OnServiceCollapseToggled;
                control.QueryRequested += OnServiceQueryRequested;

                _serviceResults.Add(result);
                _resultControls.Add(control);
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

            // Create new CTS, cancel any previous manual query
            // Don't dispose oldCts - let the owning invocation's finally block dispose it
            var cts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _manualQueryCts, cts);
            try { oldCts?.Cancel(); } catch (ObjectDisposedException) { }

            // Mark as loading and queried
            serviceResult.IsLoading = true;
            serviceResult.MarkQueried();

            try
            {
                var ct = cts.Token;

                // Detect language (use cached if available from recent query)
                // Run detection on thread pool to avoid blocking UI thread
                var detectedLanguage = _lastDetectedLanguage != TranslationLanguage.Auto
                    ? _lastDetectedLanguage
                    : await Task.Run(() => _detectionService.DetectAsync(inputText, ct));

                // Grammar mode: route to grammar correction instead of translation
                if (_currentMode == QueryMode.GrammarCorrection)
                {
                    var grammarRequest = new GrammarCorrectionRequest
                    {
                        Text = inputText,
                        Language = detectedLanguage,
                        IncludeExplanations = _settings.GrammarIncludeExplanations,
                    };
                    await ExecuteGrammarCorrectionForServiceAsync(serviceResult, grammarRequest, ct);
                    return;
                }

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
                        manager, serviceResult, request, detectedLanguage, targetLanguage, ct);
                }
                else
                {
                    // Run on thread pool to avoid blocking UI thread
                    var result = await Task.Run(
                        () => manager.TranslateAsync(request, ct, serviceResult.ServiceId));
                    serviceResult.Result = result;
                    serviceResult.IsLoading = false;
                    serviceResult.ApplyAutoCollapseLogic();
                    UpdatePhoneticDeduplication();
                }
            }
            catch (OperationCanceledException)
            {
                serviceResult.IsLoading = false;
                serviceResult.IsStreaming = false;
                serviceResult.ClearQueried(); // Allow retry after cancellation
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
            finally
            {
                Interlocked.CompareExchange(ref _manualQueryCts, null, cts);
                cts.Dispose();
            }
        }

        private async Task CleanupResourcesAsync()
        {
            CancelCurrentQuery();

            _longDocumentService.Dispose();

            var singleTaskCts = Interlocked.Exchange(ref _longDocSingleTaskCts, null);
            try { singleTaskCts?.Cancel(); } catch (ObjectDisposedException) { }
            singleTaskCts?.Dispose();

            var queueCts = Interlocked.Exchange(ref _longDocQueueCts, null);
            try { queueCts?.Cancel(); } catch (ObjectDisposedException) { }
            queueCts?.Dispose();

            // Cancel any in-flight manual queries
            // Don't dispose - let the owning OnServiceQueryRequested's finally block dispose it
            var manualCts = Interlocked.Exchange(ref _manualQueryCts, null);
            try { manualCts?.Cancel(); } catch (ObjectDisposedException) { }

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

            _isQuerying = loading;

            var loc = LocalizationService.Instance;
            var tooltip = loading ? loc.GetString("Cancel") : loc.GetString("TranslateTooltip");
            ToolTipService.SetToolTip(TranslateButton, tooltip);
            ToolTipService.SetToolTip(TranslateButtonNarrow, tooltip);

            // Swap icon: show cancel (X) glyph during query, translate glyph otherwise
            var glyph = loading ? "\uE711" : "\uE8C1";
            TranslateIcon.Glyph = glyph;
            TranslateIconNarrow.Glyph = glyph;

            // Hide progress rings (cancel icon replaces them)
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            LoadingRingNarrow.IsActive = false;
            LoadingRingNarrow.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Handle translate button click:
        /// - If a query is in progress, cancel it.
        /// - Otherwise, start a new translation query.
        /// </summary>
        private async void OnTranslateClicked(object sender, RoutedEventArgs e)
        {
            if (_isQuerying)
            {
                CancelCurrentQuery();
                return;
            }

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

            // Check if Shift or Ctrl is held — allow newline insertion
            var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);

            if (shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
                ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                return; // Let the TextBox handle it normally (insert newline)
            }

            // Plain Enter: trigger translation
            e.Handled = true;
            await StartQueryTrackedAsync();
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

            // Cancel any in-flight manual queries (stale text)
            // Don't dispose - let the owning OnServiceQueryRequested's finally block dispose it
            var oldManualCts = Interlocked.Exchange(ref _manualQueryCts, null);
            try { oldManualCts?.Cancel(); } catch (ObjectDisposedException) { }

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

                // Route based on mode
                if (_currentMode == QueryMode.GrammarCorrection)
                {
                    await StartGrammarCorrectionInternalAsync(inputText, detectionService, ct);
                    return;
                }

                // Step 1: Detect language (only when source = Auto)
                // Run detection on thread pool to avoid blocking UI thread
                var sourceLanguage = GetSourceLanguage();
                TranslationLanguage detectedLanguage;
                if (sourceLanguage == TranslationLanguage.Auto)
                {
                    detectedLanguage = await Task.Run(() => detectionService.DetectAsync(inputText, ct));
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
                            // Run on thread pool to avoid blocking UI thread with
                            // HTTP response processing, JSON parsing, and retry logic
                            var result = await Task.Run(
                                () => manager.TranslateAsync(request, ct, serviceResult.ServiceId));

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_isClosing) return;
                                serviceResult.Result = result;
                                serviceResult.IsLoading = false;
                                serviceResult.ApplyAutoCollapseLogic();
                                UpdatePhoneticDeduplication();
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
                            serviceResult.ClearQueried();
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
                        SettingsService.Instance.ClearServiceTestStatus(serviceResult.ServiceId);
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
                        SettingsService.Instance.ClearServiceTestStatus(serviceResult.ServiceId);
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
        /// Execute grammar correction for all enabled LLM services in parallel.
        /// Bypasses the translation pipeline entirely — no TargetLanguageSelector, no TranslationManager.
        /// </summary>
        private async Task StartGrammarCorrectionInternalAsync(
            string inputText,
            LanguageDetectionService detectionService,
            CancellationToken ct)
        {
            // Optional: detect language for prompt hint only
            var detectedLang = TranslationLanguage.Auto;
            var sourceLanguage = GetSourceLanguage();
            if (sourceLanguage != TranslationLanguage.Auto)
            {
                detectedLang = sourceLanguage;
            }
            else
            {
                try
                {
                    detectedLang = await Task.Run(() => detectionService.DetectAsync(inputText, ct));
                    UpdateDetectedLanguageDisplay(detectedLang);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Best-effort: continue without language hint
                    DetectedLanguageText.Visibility = Visibility.Collapsed;
                }
            }

            var request = new GrammarCorrectionRequest
            {
                Text = inputText,
                Language = detectedLang,
                IncludeExplanations = _settings.GrammarIncludeExplanations,
            };

            // Parallel-execute all grammar-capable services
            var tasks = _serviceResults
                .Where(sr => sr.EnabledQuery)
                .Select(sr => ExecuteGrammarCorrectionForServiceAsync(sr, request, ct))
                .ToArray();

            var taskResults = await Task.WhenAll(tasks);

            // Update status
            var successCount = taskResults.Count(r => r == true);
            var errorCount = taskResults.Count(r => r == false);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;

                var loc = LocalizationService.Instance;
                if (successCount > 0)
                {
                    StatusSummaryText.Text = string.Format(
                        loc.GetString("ServiceResultsComplete") ?? "{0} service(s) completed",
                        successCount);
                    UpdateStatus(true, loc.GetString("StatusReady"));
                }
                else if (errorCount > 0)
                {
                    StatusSummaryText.Text = loc.GetString("TranslationFailed") ?? "Check failed";
                    UpdateStatus(false, loc.GetString("StatusError"));
                }
                else
                {
                    StatusSummaryText.Text = "";
                    UpdateStatus(true, loc.GetString("StatusReady"));
                }
            });
        }

        /// <summary>
        /// Execute grammar correction for a single service with streaming.
        /// Returns true on success, false on error, null on cancelled/skipped.
        /// </summary>
        private async Task<bool?> ExecuteGrammarCorrectionForServiceAsync(
            ServiceQueryResult serviceResult,
            GrammarCorrectionRequest request,
            CancellationToken ct)
        {
            serviceResult.MarkQueried();

            try
            {
                using var handle = TranslationManagerService.Instance.AcquireHandle();
                if (!handle.Manager.Services.TryGetValue(serviceResult.ServiceId, out var service)
                    || service is not IGrammarCorrectionService grammarService)
                    return null;

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

                await foreach (var chunk in grammarService
                    .CorrectGrammarStreamAsync(request, ct).ConfigureAwait(false))
                {
                    sb.Append(chunk);

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
                var rawOutput = sb.ToString();
                var grammarResult = GrammarCorrectionParser.Parse(
                    rawOutput, request.Text, serviceResult.ServiceDisplayName,
                    stopwatch.ElapsedMilliseconds);

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isClosing) return;
                    serviceResult.IsStreaming = false;
                    serviceResult.StreamingText = "";
                    serviceResult.GrammarResult = grammarResult;
                });

                return true;
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isClosing) return;
                    serviceResult.IsLoading = false;
                    serviceResult.IsStreaming = false;
                    serviceResult.StreamingText = "";
                    serviceResult.ClearQueried();
                });
                return null;
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isClosing) return;
                    serviceResult.Error = new TranslationException(ex.Message, ex)
                    {
                        ErrorCode = TranslationErrorCode.Unknown,
                        ServiceId = serviceResult.ServiceId
                    };
                    serviceResult.IsLoading = false;
                    serviceResult.IsStreaming = false;
                    serviceResult.StreamingText = "";
                });
                return false;
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

            // Use ConfigureAwait(false) to avoid resuming on UI thread for each chunk.
            // DispatcherQueue.TryEnqueue is safe to call from any thread.
            await foreach (var chunk in manager.TranslateStreamAsync(
                request, ct, serviceResult.ServiceId).ConfigureAwait(false))
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
            // Run on thread pool to avoid blocking UI thread
            try
            {
                result = await Task.Run(() => manager.EnrichPhoneticsIfMissingAsync(result, request, ct));
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
                UpdatePhoneticDeduplication();
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

        private TranslationLanguage GetLongDocSourceLanguage()
        {
            return LanguageComboHelper.GetSelectedLanguage(LongDocSourceLangCombo);
        }

        private TranslationLanguage GetLongDocTargetLanguage()
        {
            return LanguageComboHelper.GetSelectedLanguage(LongDocTargetLangCombo);
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


        private void InitializeLongDocServices()
        {
            LongDocServiceCombo.Items.Clear();

            var manager = TranslationManagerService.Instance.Manager;
            foreach (var service in manager.Services.Values.Where(IsLongDocSupportedService).OrderBy(s => s.DisplayName))
            {
                var isReady = service.IsConfigured
                    && _settings.ServiceTestStatus.TryGetValue(service.ServiceId, out var passed)
                    && passed;

                var item = new ComboBoxItem
                {
                    Content = service.DisplayName,
                    Tag = service.ServiceId,
                    FontStyle = isReady ? Windows.UI.Text.FontStyle.Normal : Windows.UI.Text.FontStyle.Italic,
                };
                if (!isReady)
                {
                    item.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                }
                LongDocServiceCombo.Items.Add(item);
            }

            // Prefer first ready (configured + tested) service
            var firstReady = LongDocServiceCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.FontStyle == Windows.UI.Text.FontStyle.Normal);
            LongDocServiceCombo.SelectedItem = firstReady ?? LongDocServiceCombo.Items.FirstOrDefault();

            LongDocHistoryListView.ItemsSource = _longDocHistoryItems;
        }

        private static bool IsLongDocSupportedService(ITranslationService service)
        {
            // Built-in AI uses free proxy and is not stable enough for long document translation.
            if (string.Equals(service.ServiceId, "builtin", StringComparison.OrdinalIgnoreCase))
                return false;

            // Long-document mode focuses on AI/LLM services similar to PDFMathTranslate style pipelines.
            return service is IStreamTranslationService;
        }

        private bool TryGetSelectedLongDocServiceId(out string serviceId)
        {
            serviceId = (LongDocServiceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(serviceId);
        }


        private void InitializeLongDocOutputDefaults()
        {
            // Initialize output folder
            if (string.IsNullOrWhiteSpace(_longDocOutputFolder))
            {
                _longDocOutputFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Easydict",
                    "LongDocOutputs");
                LongDocOutputFolderDisplay.Text = _longDocOutputFolder;
            }

            // Initialize settings controls from SettingsService
            InitializeLongDocSettingsControls();
        }

        private void InitializeLongDocSettingsControls()
        {
            // Initialize Output Mode combo
            var outputMode = _settings.DocumentOutputMode ?? "Monolingual";
            SelectComboByTag(LongDocOutputModeCombo, outputMode);

            // Initialize Concurrency NumberBox
            LongDocConcurrencyBox.Value = Math.Clamp(_settings.LongDocMaxConcurrency, 1, 16);

            // Initialize Page Range TextBox
            LongDocPageRangeBox.Text = _settings.LongDocPageRange ?? "";
        }

        private static void SelectComboByTag(ComboBox combo, string? tag)
        {
            if (combo is null) return;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            // Default to first item if not found
            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private bool TryValidateLongDocOutputFolder(out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(_longDocOutputFolder))
            {
                errorMessage = "Output folder is required. Click Browse to select a folder.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(_longDocOutputFolder);
            }
            catch (Exception ex)
            {
                errorMessage = $"Cannot create output folder: {ex.Message}";
                return false;
            }

            return true;
        }

        private string BuildOutputPath(string sourceFilePath)
        {
            var folder = !string.IsNullOrWhiteSpace(_longDocOutputFolder)
                ? _longDocOutputFolder
                : Path.GetDirectoryName(sourceFilePath)
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var name = Path.GetFileNameWithoutExtension(sourceFilePath);
            if (string.IsNullOrWhiteSpace(name)) name = "translated";
            var ext = GetOutputExtension();
            return Path.Combine(folder, $"{name}_translated{ext}");
        }

        private string GetOutputExtension()
        {
            var modeTag = (LongDocInputModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pdf";
            return modeTag switch
            {
                "plaintext" => ".txt",
                "markdown" => ".md",
                _ => ".pdf"
            };
        }

        private static DocumentOutputMode GetDocumentOutputModeFromSettings()
        {
            var setting = SettingsService.Instance.DocumentOutputMode;
            return setting switch
            {
                "Bilingual" => DocumentOutputMode.Bilingual,
                "Both" => DocumentOutputMode.Both,
                _ => DocumentOutputMode.Monolingual
            };
        }

        private List<string> GetSelectedFilesList()
        {
            return _longDocFileItems
                .Select(item => item.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool IsLongDocTaskRunning()
        {
            return _longDocSingleTaskCts is not null
                   || _longDocQueueCts is not null
                   || _longDocQueueTask is { IsCompleted: false };
        }

        private CancellationToken PrepareLongDocSingleTaskCancellationToken()
        {
            var previous = Interlocked.Exchange(ref _longDocSingleTaskCts, null);
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            previous?.Dispose();

            _longDocSingleTaskCts = new CancellationTokenSource();
            return _longDocSingleTaskCts.Token;
        }

        private void CompleteLongDocSingleTask()
        {
            _longDocSingleTaskCts?.Dispose();
            _longDocSingleTaskCts = null;
        }

        private void SetLongDocTaskUiState(bool running)
        {
            LongDocTranslateButton.IsEnabled = !running || _isLongDocTranslating; // Allow if in cancel mode
            LongDocSourceLangCombo.IsEnabled = !running;
            LongDocTargetLangCombo.IsEnabled = !running;
            LongDocServiceCombo.IsEnabled = !running;
            LongDocInputModeCombo.IsEnabled = !running;
            LongDocBrowseButton.IsEnabled = !running;
            LongDocOutputBrowseButton.IsEnabled = !running;
            LongDocRetryButton.IsEnabled = !running;

            if (running)
            {
                LongDocStatusText.Text = "Task running, settings are locked. Changes will apply to the next task.";
            }
            // When !running, the caller has already set the appropriate status
            // (Completed/Partial success/Failed/Canceled), so don't overwrite it.
        }

        private string BuildQueueOutputPath(string outputFolder, string sourceFilePath, int queueIndex)
        {
            var safeName = Path.GetFileNameWithoutExtension(sourceFilePath);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = $"file-{queueIndex:000}";
            }

            var ext = GetOutputExtension();
            var outputName = $"{safeName}_translated{ext}";
            return Path.Combine(outputFolder, outputName);
        }

        private async Task ProcessLongDocQueueAsync(List<string> filePaths, string serviceId, string outputFolder, LongDocumentInputMode mode, LayoutDetectionMode layoutDetection, CancellationToken cancellationToken)
        {
            var from = GetLongDocSourceLanguage();
            var to = GetLongDocTargetLanguage();

            var completed = 0;
            var skipped = 0;
            var failed = 0;

            for (var i = 0; i < filePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filePath = filePaths[i];
                if (!File.Exists(filePath))
                {
                    failed++;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        LongDocStatusText.Text = $"Queue {i + 1}/{filePaths.Count} failed (file not found): {filePath}";
                    });
                    continue;
                }

                var dedupKey = await _longDocDedupService.CreateDedupKeyAsync(
                    mode,
                    filePath,
                    serviceId,
                    from,
                    to,
                    cancellationToken);

                var existingPath = await _longDocDedupService.TryGetExistingOutputPathAsync(dedupKey, cancellationToken);
                if (!string.IsNullOrWhiteSpace(existingPath))
                {
                    skipped++;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        LongDocStatusText.Text = $"Queue {i + 1}/{filePaths.Count} skipped duplicate: {existingPath}";
                    });
                    continue;
                }

                var outputPath = BuildQueueOutputPath(outputFolder, filePath, i + 1);

                var queueOutputMode = GetDocumentOutputModeFromSettings();
                var result = await _longDocumentService.TranslateToPdfAsync(
                    mode,
                    filePath,
                    from,
                    to,
                    outputPath,
                    serviceId,
                    progress => DispatcherQueue.TryEnqueue(() =>
                    {
                        LongDocStatusText.Text = $"Queue {i + 1}/{filePaths.Count}: {progress}";
                    }),
                    cancellationToken,
                    layoutDetection: layoutDetection,
                    outputMode: queueOutputMode);

                if (result.State == LongDocumentJobState.Completed)
                {
                    await _longDocDedupService.RegisterOutputAsync(dedupKey, result.OutputPath, cancellationToken);
                    completed++;

                    var fileItem = new LongDocFileItem { FilePath = filePath };
                    fileItem.MarkCompleted(result.OutputPath);
                    var svcName = (LongDocServiceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? serviceId;
                    var tgtName = (LongDocTargetLangCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unknown";
                    DispatcherQueue.TryEnqueue(() => AddToHistory(fileItem, svcName, tgtName));
                }
                else
                {
                    failed++;
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                LongDocStatusText.Text = $"Queue finished. Completed: {completed}, Skipped: {skipped}, Failed/Partial: {failed}.";
            });
        }

        private async void OnLongDocStartQueueClicked(object sender, RoutedEventArgs e)
        {
            if (IsLongDocTaskRunning())
            {
                LongDocStatusText.Text = "A task is already running. Please wait or cancel current task.";
                return;
            }

            if (!TryGetSelectedLongDocServiceId(out var serviceId))
            {
                LongDocStatusText.Text = "Please select one translation service.";
                return;
            }

            if (!TryValidateLongDocOutputFolder(out var outputError))
            {
                LongDocStatusText.Text = outputError;
                return;
            }

            var outputFolder = _longDocOutputFolder;
            var queueItems = GetSelectedFilesList();
            if (queueItems.Count == 0)
            {
                LongDocStatusText.Text = "No files selected. Click Browse to select files.";
                return;
            }

            var modeTag = (LongDocInputModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pdf";
            var mode = modeTag switch
            {
                "plaintext" => LongDocumentInputMode.PlainText,
                "markdown" => LongDocumentInputMode.Markdown,
                _ => LongDocumentInputMode.Pdf
            };

            _longDocQueueCts?.Cancel();
            _longDocQueueCts?.Dispose();
            _longDocQueueCts = new CancellationTokenSource();

            SetLongDocTaskUiState(true);

            var targetLang = GetTargetLanguage();

            LayoutDetectionMode layoutMode;
            try
            {
                // Check ONNX layout model availability (once before starting queue)
                layoutMode = await EnsureOnnxReadyAsync(mode, _longDocQueueCts.Token);

                // Check CJK font availability for PDF output (once before starting queue)
                if (mode == LongDocumentInputMode.Pdf)
                    await EnsureCjkFontReadyAsync(targetLang, _longDocQueueCts.Token);
            }
            catch (OperationCanceledException)
            {
                LongDocStatusText.Text = "Queue canceled.";
                SetLongDocButtonState(false);
                SetLongDocTaskUiState(false);
                _longDocQueueCts?.Dispose();
                _longDocQueueCts = null;
                return;
            }

            LongDocStatusText.Text = $"Queue started: {queueItems.Count} file(s).";

            _longDocQueueTask = ProcessLongDocQueueAsync(queueItems, serviceId, outputFolder, mode, layoutMode, _longDocQueueCts.Token);

            // Always run queue in background with continuation
            _ = _longDocQueueTask.ContinueWith(task =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (task.IsCanceled)
                    {
                        LongDocStatusText.Text = "Queue canceled.";
                    }
                    else if (task.IsFaulted)
                    {
                        Debug.WriteLine($"[LongDoc] Queue failed: {task.Exception}");
                        LongDocStatusText.Text = $"Queue failed: {task.Exception?.GetBaseException().Message}";
                    }

                    SetLongDocButtonState(false);
                    SetLongDocTaskUiState(false);
                    _longDocQueueTask = null;
                    _longDocQueueCts?.Dispose();
                    _longDocQueueCts = null;
                });
            }, TaskScheduler.Default);
        }

        private void OnLongDocCancelQueueClicked(object sender, RoutedEventArgs e)
        {
            _longDocSingleTaskCts?.Cancel();
            _longDocQueueCts?.Cancel();
            LongDocStatusText.Text = "Canceling current task...";
        }

        private void OnLongDocCancelClicked(object sender, RoutedEventArgs e)
        {
            _longDocSingleTaskCts?.Cancel();
            LongDocStatusText.Text = "Canceling translation...";
        }

        private void OnLongDocInputModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LongDocFilePanel is null || LongDocOutputTitle is null) return; // Fired during InitializeComponent before controls exist

            var selected = (LongDocInputModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

            // Title updates
            LongDocInputTitle.Text = selected switch
            {
                "plaintext" => "Text Input",
                "markdown" => "Markdown Input",
                _ => "PDF Input"
            };
            LongDocOutputTitle.Text = "Translation Output";

            // Clear file selection when mode changes
            _longDocFileItems.Clear();
            UpdateLongDocFileDisplay();

            // Update output naming hint
            var ext = GetOutputExtension();
            LongDocOutputNamingHint.Text = $"Output: {{filename}}_translated{ext}";
        }

        private void OnLongDocOutputModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LongDocOutputModeCombo is null) return;

            var selectedTag = (LongDocOutputModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (selectedTag != null)
            {
                _settings.DocumentOutputMode = selectedTag;
            }
        }

        private void OnLongDocConcurrencyChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (double.IsNaN(args.NewValue))
            {
                sender.Value = 4; // Reset to default if cleared
            }
            _settings.LongDocMaxConcurrency = (int)Math.Clamp(sender.Value, 1, 16);
        }

        private void OnLongDocPageRangeChanged(object sender, TextChangedEventArgs e)
        {
            if (LongDocPageRangeBox is null) return;
            _settings.LongDocPageRange = LongDocPageRangeBox.Text?.Trim() ?? "";
        }

        private void OnLongDocClearHistoryClicked(object sender, RoutedEventArgs e)
        {
            _longDocHistoryItems.Clear();
        }

        private void SetLongDocButtonState(bool isTranslating)
        {
            _isLongDocTranslating = isTranslating;
            var glyph = isTranslating ? "\uE711" : "\uE8C1"; // X or document
            LongDocTranslateIcon.Glyph = glyph;
            ToolTipService.SetToolTip(LongDocTranslateButton, isTranslating ? "Cancel" : "Translate");
        }

        private void AddToHistory(LongDocFileItem fileItem, string serviceName, string targetLanguage)
        {
            var historyItem = LongDocHistoryItem.FromFileItem(fileItem, serviceName, targetLanguage);
            _longDocHistoryItems.Insert(0, historyItem);

            // Enforce max history size (FIFO)
            while (_longDocHistoryItems.Count > MaxHistoryItems)
            {
                _longDocHistoryItems.RemoveAt(_longDocHistoryItems.Count - 1);
            }
        }

        private async void OnLongDocBrowseClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();

                // WinUI 3 requires HWND initialization
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                // Set file filter based on current mode
                var modeTag = (LongDocInputModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pdf";
                switch (modeTag)
                {
                    case "plaintext":
                        picker.FileTypeFilter.Add(".txt");
                        break;
                    case "markdown":
                        picker.FileTypeFilter.Add(".md");
                        break;
                    default:
                        picker.FileTypeFilter.Add(".pdf");
                        break;
                }

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0) return;

                _longDocFileItems.Clear();
                foreach (var file in files)
                {
                    _longDocFileItems.Add(new LongDocFileItem
                    {
                        FilePath = file.Path,
                        Status = LongDocItemStatus.Pending
                    });
                }

                UpdateLongDocFileDisplay();
                UpdateLongDocOutputFolder();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainPage] File picker error: {ex.Message}");
            }
        }

        private void UpdateLongDocFileDisplay()
        {
            if (_longDocFileItems.Count == 0)
            {
                LongDocFilePathDisplay.Text = "No file selected";
            }
            else if (_longDocFileItems.Count == 1)
            {
                LongDocFilePathDisplay.Text = _longDocFileItems[0].FileName;
            }
            else
            {
                LongDocFilePathDisplay.Text = $"{_longDocFileItems.Count} files selected";
            }

            LongDocFileItemsControl.ItemsSource = _longDocFileItems;
            LongDocFileItemsControl.Visibility = _longDocFileItems.Count >= 2
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateLongDocOutputFolder()
        {
            if (_longDocFileItems.Count > 0)
            {
                var dir = Path.GetDirectoryName(_longDocFileItems[0].FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    _longDocOutputFolder = dir;
                    LongDocOutputFolderDisplay.Text = dir;
                }
            }
        }

        private void OnLongDocRemoveFileClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LongDocFileItem item)
            {
                _longDocFileItems.Remove(item);
                UpdateLongDocFileDisplay();
            }
        }

        private async void OnLongDocOutputBrowseClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add("*");

                var folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;

                _longDocOutputFolder = folder.Path;
                LongDocOutputFolderDisplay.Text = folder.Path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainPage] Folder picker error: {ex.Message}");
            }
        }

        private async void OnLongDocTranslateClicked(object sender, RoutedEventArgs e)
        {
            // Handle toggle button behavior
            if (_isLongDocTranslating)
            {
                // Cancel operation
                _longDocSingleTaskCts?.Cancel();
                _longDocQueueCts?.Cancel();
                LongDocStatusText.Text = "Canceling translation...";
                SetLongDocButtonState(false);
                return;
            }

            if (_longDocFileItems.Count == 0)
            {
                LongDocStatusText.Text = "No file selected. Click Browse to select files.";
                return;
            }

            SetLongDocButtonState(true);

            // Multiple files: auto-redirect to queue processing
            if (_longDocFileItems.Count > 1)
            {
                OnLongDocStartQueueClicked(sender, e);
                return;
            }

            var cancellationToken = PrepareLongDocSingleTaskCancellationToken();

            // Create progress tracker with throttled UI updates (max 4 per second, 1% increments)
            var lastUpdateTime = DateTime.MinValue;
            var lastReportedPercentage = -1.0;
            var progress = new Progress<LongDocumentTranslationProgress>(p =>
            {
                var now = DateTime.UtcNow;
                var timeElapsed = (now - lastUpdateTime).TotalMilliseconds;

                // Only update if at least 250ms elapsed OR percentage changed by at least 1%
                var percentageChanged = Math.Abs(p.Percentage - lastReportedPercentage) >= 1.0;
                if (timeElapsed >= 250 || percentageChanged)
                {
                    lastUpdateTime = now;
                    lastReportedPercentage = p.Percentage;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;

                        // Update status text with progress
                        var stageText = p.GetStageDisplayName();
                        var detailText = p.TotalBlocks > 0
                            ? $"{stageText}: {p.CurrentBlock}/{p.TotalBlocks} blocks (page {p.CurrentPage}/{p.TotalPages})"
                            : stageText;
                        LongDocStatusText.Text = detailText;

                        // Update file item progress if we have a single file
                        if (_longDocFileItems.Count == 1)
                        {
                            var fileItem = _longDocFileItems[0];
                            fileItem.UpdateProgress((int)p.Percentage, detailText);
                        }
                    });
                }
            });

            try
            {
                SetLongDocTaskUiState(true);
                LongDocStatusText.Text = "Preparing...";

                if (!TryGetSelectedLongDocServiceId(out var serviceId))
                {
                    LongDocStatusText.Text = "Please select one translation service.";
                    return;
                }

                var modeTag = (LongDocInputModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pdf";
                var mode = modeTag switch
                {
                    "plaintext" => LongDocumentInputMode.PlainText,
                    "markdown" => LongDocumentInputMode.Markdown,
                    _ => LongDocumentInputMode.Pdf
                };

                var input = _longDocFileItems[0].FilePath;

                if (!TryValidateLongDocOutputFolder(out var outputError))
                {
                    LongDocStatusText.Text = outputError;
                    return;
                }

                var outputPath = BuildOutputPath(input);

                _longDocLastFrom = GetLongDocSourceLanguage();
                _longDocLastTo = GetLongDocTargetLanguage();
                _longDocLastServiceId = serviceId;
                _longDocLastDedupKey = await _longDocDedupService.CreateDedupKeyAsync(
                    mode,
                    input,
                    serviceId,
                    _longDocLastFrom,
                    _longDocLastTo,
                    cancellationToken);

                var existingOutputPath = await _longDocDedupService.TryGetExistingOutputPathAsync(_longDocLastDedupKey, cancellationToken);
                if (!string.IsNullOrWhiteSpace(existingOutputPath))
                {
                    LongDocStatusText.Text = $"Skipped duplicate file. Existing translation: {existingOutputPath}";
                    return;
                }

                // Check ONNX layout model availability (prompt download if needed)
                var layoutMode = await EnsureOnnxReadyAsync(mode, cancellationToken);

                // Check CJK font availability for PDF output
                var outputMode = GetDocumentOutputModeFromSettings();
                if (mode == LongDocumentInputMode.Pdf)
                {
                    await EnsureCjkFontReadyAsync(_longDocLastTo, cancellationToken);
                }

                LongDocStatusText.Text = "Preparing...";
                var result = await _longDocumentService.TranslateToPdfAsync(
                    mode,
                    input,
                    _longDocLastFrom,
                    _longDocLastTo,
                    outputPath,
                    serviceId,
                    progressMsg => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;
                        LongDocStatusText.Text = progressMsg;
                    }),
                    cancellationToken,
                    layoutDetection: layoutMode,
                    outputMode: outputMode,
                    progress: progress);

                _longDocCheckpoint = result.Checkpoint;
                LongDocRetryButton.IsEnabled = result.State == LongDocumentJobState.PartialSuccess;
                LongDocStatusText.Text = result.State == LongDocumentJobState.Completed
                    ? $"Completed: {result.OutputPath}"
                    : $"Partial success: {result.SucceededChunks}/{result.TotalChunks} chunks succeeded, failed chunks: {string.Join(",", result.FailedChunkIndexes.Select(i => i + 1))}.";

                if (result.State == LongDocumentJobState.Completed)
                {
                    await _longDocDedupService.RegisterOutputAsync(_longDocLastDedupKey, result.OutputPath, cancellationToken);

                    if (_longDocFileItems.Count > 0)
                    {
                        _longDocFileItems[0].MarkCompleted(result.OutputPath);
                        var serviceName = (LongDocServiceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? serviceId;
                        var targetName = (LongDocTargetLangCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unknown";
                        AddToHistory(_longDocFileItems[0], serviceName, targetName);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LongDocStatusText.Text = "Task canceled.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LongDoc] Translation failed: {ex}");
                LongDocStatusText.Text = $"Failed: {ex.Message}";
            }
            finally
            {
                CompleteLongDocSingleTask();
                SetLongDocButtonState(false);
                SetLongDocTaskUiState(false);
            }
        }

        private async void OnLongDocRetryClicked(object sender, RoutedEventArgs e)
        {
            if (_longDocCheckpoint is null)
            {
                LongDocStatusText.Text = "No partial task to retry.";
                return;
            }

            if (IsLongDocTaskRunning())
            {
                LongDocStatusText.Text = "A task is already running. Please wait or cancel current task.";
                return;
            }

            var cancellationToken = PrepareLongDocSingleTaskCancellationToken();

            // Create progress tracker with throttled UI updates (max 4 per second, 1% increments)
            var lastUpdateTime = DateTime.MinValue;
            var lastReportedPercentage = -1.0;
            var progress = new Progress<LongDocumentTranslationProgress>(p =>
            {
                var now = DateTime.UtcNow;
                var timeElapsed = (now - lastUpdateTime).TotalMilliseconds;

                // Only update if at least 250ms elapsed OR percentage changed by at least 1%
                var percentageChanged = Math.Abs(p.Percentage - lastReportedPercentage) >= 1.0;
                if (timeElapsed >= 250 || percentageChanged)
                {
                    lastUpdateTime = now;
                    lastReportedPercentage = p.Percentage;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;

                        // Update status text with progress
                        var stageText = p.GetStageDisplayName();
                        var detailText = p.TotalBlocks > 0
                            ? $"{stageText}: {p.CurrentBlock}/{p.TotalBlocks} blocks (page {p.CurrentPage}/{p.TotalPages})"
                            : stageText;
                        LongDocStatusText.Text = detailText;

                        // Update file item progress if we have a single file
                        if (_longDocFileItems.Count == 1)
                        {
                            var fileItem = _longDocFileItems[0];
                            fileItem.UpdateProgress((int)p.Percentage, detailText);
                        }
                    });
                }
            });

            try
            {
                SetLongDocTaskUiState(true);
                LongDocRetryButton.IsEnabled = false;

                if (!TryValidateLongDocOutputFolder(out var outputError))
                {
                    LongDocStatusText.Text = outputError;
                    return;
                }

                var retrySourcePath = _longDocCheckpoint.SourceFilePath ?? "retry";
                var outputPath = BuildOutputPath(retrySourcePath);

                var retryOutputMode = GetDocumentOutputModeFromSettings();
                var result = await _longDocumentService.RetryFailedChunksAsync(
                    _longDocCheckpoint,
                    _longDocLastFrom,
                    _longDocLastTo,
                    outputPath,
                    _longDocLastServiceId,
                    progressMsg => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;
                        LongDocStatusText.Text = progressMsg;
                    }),
                    cancellationToken,
                    outputMode: retryOutputMode,
                    progress: progress);

                _longDocCheckpoint = result.Checkpoint;
                LongDocRetryButton.IsEnabled = result.State == LongDocumentJobState.PartialSuccess;
                LongDocStatusText.Text = result.State == LongDocumentJobState.Completed
                    ? $"Retry completed: {result.OutputPath}"
                    : $"Still partial: {result.SucceededChunks}/{result.TotalChunks} chunks succeeded, remaining failed chunks: {string.Join(",", result.FailedChunkIndexes.Select(i => i + 1))}.";

                if (result.State == LongDocumentJobState.Completed && !string.IsNullOrWhiteSpace(_longDocLastDedupKey))
                {
                    await _longDocDedupService.RegisterOutputAsync(_longDocLastDedupKey, result.OutputPath, cancellationToken);
                }

                if (result.State == LongDocumentJobState.Completed && _longDocFileItems.Count > 0)
                {
                    _longDocFileItems[0].MarkCompleted(result.OutputPath);
                    var serviceName = (LongDocServiceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? _longDocLastServiceId;
                    var targetName = (LongDocTargetLangCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unknown";
                    AddToHistory(_longDocFileItems[0], serviceName, targetName);
                }

            }
            catch (OperationCanceledException)
            {
                LongDocStatusText.Text = "Task canceled.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LongDoc] Retry failed: {ex}");
                LongDocStatusText.Text = $"Retry failed: {ex.Message}";
            }
            finally
            {
                CompleteLongDocSingleTask();
                SetLongDocButtonState(false);
                SetLongDocTaskUiState(false);
            }
        }

        private void OnModeMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;

            QueryMode newMode;
            if (ReferenceEquals(sender, ModeTranslationItem))
                newMode = QueryMode.Translation;
            else if (ReferenceEquals(sender, ModeGrammarItem))
                newMode = QueryMode.GrammarCorrection;
            else if (ReferenceEquals(sender, ModeLongDocItem))
                newMode = QueryMode.LongDocument;
            else
                return;

            if (newMode == _currentMode) return;
            _currentMode = newMode;
            ApplyModeState();
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
            // Switch out of Long Document mode for quick translate
            if (_currentMode == QueryMode.LongDocument)
            {
                _currentMode = QueryMode.Translation;
                ModeTranslationItem.IsChecked = true;
                ApplyModeState();
            }
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

        /// <summary>
        /// Updates phonetic deduplication across all service result controls.
        /// The first service showing a phonetic displays it; subsequent services with
        /// the same phonetic will have it hidden to avoid duplication.
        /// </summary>
        private void UpdatePhoneticDeduplication()
        {
            var shownPhonetics = new HashSet<string>();

            foreach (var control in _resultControls)
            {
                // Set which phonetics have already been shown (before this control)
                control.AlreadyShownPhonetics = shownPhonetics.Count > 0
                    ? new HashSet<string>(shownPhonetics)
                    : null;

                // Collect phonetics displayed by this control for subsequent controls
                foreach (var key in control.GetDisplayedPhoneticKeys())
                {
                    shownPhonetics.Add(key);
                }
            }
        }

        // ═══════════════════════════════════════════════
        //  ContentDialog helpers
        // ═══════════════════════════════════════════════

        /// <summary>
        /// Shows a ContentDialog, hiding any currently-open dialog first.
        /// WinUI 3 allows only one ContentDialog open at a time per XamlRoot.
        /// </summary>
        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            try { _currentDialog?.Hide(); } catch (COMException) { }
            _currentDialog = dialog;

            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                if (_currentDialog == dialog)
                {
                    _currentDialog = null;
                }
            }
        }

        /// <summary>
        /// Checks whether the ONNX layout model is downloaded. If not, prompts the user
        /// to download it. Returns the layout detection mode to use.
        /// Only prompts for PDF mode; non-PDF modes always use heuristic.
        /// If download fails, falls back to heuristic instead of failing the translation.
        /// </summary>
        private async Task<LayoutDetectionMode> EnsureOnnxReadyAsync(
            LongDocumentInputMode inputMode, CancellationToken ct)
        {
            // ONNX layout detection only applies to PDF input
            if (inputMode is not LongDocumentInputMode.Pdf)
                return LayoutDetectionMode.Heuristic;

            var downloadService = _longDocumentService.GetLayoutModelDownloadService();
            if (downloadService.IsReady)
                return LayoutDetectionMode.OnnxLocal;

            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("LongDoc_OnnxDownloadTitle"),
                Content = loc.GetString("LongDoc_OnnxDownloadMessage"),
                PrimaryButtonText = loc.GetString("LongDoc_Download"),
                CloseButtonText = loc.GetString("LongDoc_Skip"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await ShowDialogAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                LongDocStatusText.Text = loc.GetString("LongDoc_OnnxDownloadTitle") + "...";
                var progress = new Progress<ModelDownloadProgress>(p =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;
                        var pct = p.TotalBytes > 0 ? (int)(p.BytesDownloaded * 100 / p.TotalBytes) : 0;
                        LongDocStatusText.Text = $"{loc.GetString("LongDoc_OnnxDownloadTitle")}: {pct}%";
                    });
                });

                try
                {
                    await downloadService.EnsureAvailableAsync(progress, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // User cancellation should abort the entire operation
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MainPage] ONNX download failed, falling back to heuristic: {ex.Message}");
                    LongDocStatusText.Text = loc.GetString("LongDoc_OnnxDownloadFailed_Fallback");
                }
            }

            return downloadService.IsReady ? LayoutDetectionMode.OnnxLocal : LayoutDetectionMode.Heuristic;
        }

        /// <summary>
        /// Checks whether a CJK font is needed for the target language and output is PDF.
        /// If the font is not downloaded, prompts the user to download it.
        /// </summary>
        private async Task EnsureCjkFontReadyAsync(TranslationLanguage targetLang, CancellationToken ct)
        {
            if (!FontDownloadService.RequiresCjkFont(targetLang))
                return;

            using var fontService = new FontDownloadService();
            if (fontService.IsFontDownloaded(targetLang))
                return;

            var loc = LocalizationService.Instance;
            var langEntry = LanguageComboHelper.AllLanguages.FirstOrDefault(l => l.Language == targetLang);
            var langName = langEntry.LocalizationKey != null ? loc.GetString(langEntry.LocalizationKey) : targetLang.ToString();
            var dialog = new ContentDialog
            {
                Title = loc.GetString("LongDoc_CjkFontTitle"),
                Content = string.Format(loc.GetString("LongDoc_CjkFontMessage"), langName),
                PrimaryButtonText = loc.GetString("LongDoc_Download"),
                CloseButtonText = loc.GetString("LongDoc_Skip"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await ShowDialogAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                LongDocStatusText.Text = loc.GetString("LongDoc_CjkFontTitle") + "...";
                var progress = new Progress<ModelDownloadProgress>(p =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;
                        var pct = p.TotalBytes > 0 ? (int)(p.BytesDownloaded * 100 / p.TotalBytes) : 0;
                        LongDocStatusText.Text = $"{loc.GetString("LongDoc_CjkFontTitle")}: {pct}%";
                    });
                });
                await fontService.EnsureFontAsync(targetLang, progress, ct);
            }
        }
    }
}
