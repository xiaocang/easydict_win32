using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Services;
using Easydict.WinUI.Views.Controls;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using System.Numerics;
using TranslationLanguage = Easydict.TranslationService.Models.Language;

namespace Easydict.WinUI.Views;

/// <summary>
/// Compact mini window for quick translations.
/// Features: always-on-top when pinned, auto-close on focus loss, compact UI.
/// </summary>
public sealed partial class MiniWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

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
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;
    private bool _isPinned;
    private volatile bool _isClosing;
    private bool _isLoaded;
    private bool _isQuerying;
    private bool _suppressTargetLanguageSelectionChanged;
    private bool _suppressSourceLanguageSelectionChanged;
    private QueryMode _currentMode = QueryMode.Translation;
    private TitleBarDragRegionHelper? _titleBarHelper;
    private bool _hasAutoPlayedCurrentQuery = false;
    private DateTime _lastShowTime = DateTime.MinValue;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeThrottleTimer;
    private bool _resizePending;      // resize requested but not yet executed
    private bool _resizeThrottling;   // inside cooldown window
    private bool _isSourceTextExpanded = false;

    private const int ResizeThrottleMs = 150;
    private const int InputFocusRetryDelayMs = 50;
    private const int InputFocusMaxAttempts = 10;

    /// <summary>
    /// Maximum time to wait for in-flight query to complete during cleanup.
    /// </summary>
    private const int QueryShutdownTimeoutSeconds = 2;

    public MiniWindow()
    {
        _targetLanguageSelector = new TargetLanguageSelector(_settings);
        this.InitializeComponent();

        // Get AppWindow for window management
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Configure window appearance
        ConfigureWindow();

        // Initialize translation services
        InitializeTranslationServices();

        // Handle window events
        this.Activated += OnWindowActivated;
        this.Closed += OnWindowClosed;

        // Initialize service result controls
        InitializeServiceResults();

        // Subscribe to text changes for auto-resize
        InputTextBox.TextChanged += OnTextChanged;

        // Track when content is loaded for safe UI operations
        if (this.Content is FrameworkElement content)
        {
            content.Loaded += (s, e) =>
            {
                _isLoaded = true;
                // Apply localization first (populates combos), then settings (selects saved language)
                ApplyLocalization();
                ApplySettings();
            };
        }

        // Set up title bar drag regions for unpackaged WinUI 3 apps
        if (_appWindow != null)
        {
            _titleBarHelper = new TitleBarDragRegionHelper(
                this,
                _appWindow,
                TitleBarRegion,
                new FrameworkElement[] { PinButton, CloseButton },
                "MiniWindow");
            _titleBarHelper.Initialize();
        }
    }

    /// <summary>
    /// Apply localization to all UI elements using LocalizationService.
    /// </summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;

        // Window title - keep "Easydict" brand name, only localize "Mini"
        this.Title = $"Easydict ᵇᵉᵗᵃ {loc.GetString("QuickTranslate")}";

        // Populate language combos dynamically from user's selected languages
        _suppressSourceLanguageSelectionChanged = true;
        _suppressTargetLanguageSelectionChanged = true;
        try
        {
            LanguageComboHelper.PopulateSourceCombo(SourceLangCombo, loc);
            LanguageComboHelper.PopulateTargetCombo(TargetLangCombo, loc);
        }
        finally
        {
            _suppressSourceLanguageSelectionChanged = false;
            _suppressTargetLanguageSelectionChanged = false;
        }

        // Update query mode button emoji and tooltip
        UpdateQueryModeButton();

        // Placeholders
        InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder");

        // Tooltips
        ToolTipService.SetToolTip(PinButton, loc.GetString("PinWindowTooltip"));
        ToolTipService.SetToolTip(CloseButton, loc.GetString("Close"));
        ToolTipService.SetToolTip(SourceLangCombo, loc.GetString("SourceLanguageTooltip"));
        ToolTipService.SetToolTip(SwapButton, loc.GetString("SwapLanguagesTooltip"));
        ToolTipService.SetToolTip(TargetLangCombo, loc.GetString("TargetLanguageTooltip"));
        ToolTipService.SetToolTip(TranslateButton, loc.GetString("TranslateTooltip"));
    }

    private void UpdateQueryModeButton()
    {
        bool isGrammar = _currentMode == QueryMode.GrammarCorrection;
        QueryModeEmoji.Text = isGrammar ? "✏️" : "🌐";

        var loc = LocalizationService.Instance;
        var currentName = loc.GetString(isGrammar ? "QueryMode_GrammarCorrection" : "QueryMode_Translation")
            ?? (isGrammar ? "Grammar Check" : "Translate");
        var otherName = loc.GetString(!isGrammar ? "QueryMode_GrammarCorrection" : "QueryMode_Translation")
            ?? (!isGrammar ? "Grammar Check" : "Translate");
        var fmt = loc.GetString("QueryModeButton_SwitchTooltip") ?? "{0} — click to switch to {1}";
        ToolTipService.SetToolTip(QueryModeButton, string.Format(fmt, currentName, otherName));
    }

    private void OnQueryModeButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;

        _currentMode = _currentMode == QueryMode.GrammarCorrection
            ? QueryMode.Translation
            : QueryMode.GrammarCorrection;

        UpdateQueryModeButton();
        MiniWindowService.Instance.NotifyQueryModeChanged(_currentMode);

        var loc = LocalizationService.Instance;
        if (_currentMode == QueryMode.GrammarCorrection)
        {
            TargetLangCombo.Visibility = Visibility.Collapsed;
            SwapButton.Visibility = Visibility.Collapsed;

            InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder_Grammar")
                ?? "Enter text to check grammar...";
            ToolTipService.SetToolTip(TranslateButton,
                loc.GetString("TranslateButton_Grammar_Tooltip") ?? "Check Grammar");
        }
        else
        {
            TargetLangCombo.Visibility = Visibility.Visible;
            SwapButton.Visibility = Visibility.Visible;

            InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder");
            ToolTipService.SetToolTip(TranslateButton, loc.GetString("TranslateTooltip"));
        }

        InitializeServiceResults();
    }

    /// <summary>
    /// Configure window to be compact with no title bar buttons.
    /// </summary>
    private void ConfigureWindow()
    {
        if (_appWindow == null) return;

        // Set window icon
        WindowIconService.SetWindowIcon(_appWindow);

        // Get presenter for window behavior control
        _presenter = _appWindow.Presenter as OverlappedPresenter;
        if (_presenter != null)
        {
            _presenter.IsMinimizable = false;
            _presenter.IsMaximizable = false;
            _presenter.IsResizable = true;  // Allow resize for auto-height
            _presenter.SetBorderAndTitleBar(true, false); // Border yes, title bar no
        }

        // Extend content into title bar for custom drag area
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        // Note: SetTitleBar() doesn't work reliably in unpackaged WinUI 3 apps.
        // We use InputNonClientPointerSource.SetRegionRects() instead to define
        // passthrough regions for interactive controls. The rest becomes draggable.

        // Set window size
        var scale = DpiHelper.GetScaleFactorForWindow(WindowNative.GetWindowHandle(this));
        var widthPx = DpiHelper.DipsToPhysicalPixels(_settings.MiniWindowWidthDips, scale);
        var heightPx = DpiHelper.DipsToPhysicalPixels(_settings.MiniWindowHeightDips, scale);
        _appWindow.Resize(new SizeInt32((int)widthPx, (int)heightPx));

        // Position window
        PositionWindow();

        // Apply pinned state
        _isPinned = _settings.MiniWindowIsPinned;
        UpdatePinState();
    }

    /// <summary>
    /// Position window based on saved position or top-right corner of current screen.
    /// </summary>
    private void PositionWindow()
    {
        if (_appWindow == null) return;

        var hWnd = WindowNative.GetWindowHandle(this);
        var scale = DpiHelper.GetScaleFactorForWindow(hWnd);

        // Check if we have a saved position
        if (_settings.MiniWindowXDips > 0 || _settings.MiniWindowYDips > 0)
        {
            var x = DpiHelper.DipsToPhysicalPixels(_settings.MiniWindowXDips, scale);
            var y = DpiHelper.DipsToPhysicalPixels(_settings.MiniWindowYDips, scale);
            _appWindow.Move(new PointInt32((int)x, (int)y));
        }
        else
        {
            // Position at top-right corner of the screen where cursor is located
            var gotCursorPos = GetCursorPos(out var cursorPos);

            // Use cursor position when available; otherwise fall back to display containing current window
            var displayArea = gotCursorPos
                ? DisplayArea.GetFromPoint(
                    new PointInt32(cursorPos.X, cursorPos.Y),
                    DisplayAreaFallback.Primary)
                : DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);

            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                var windowSize = _appWindow.Size;

                // Use DIP-aware margin (20 DIPs) for consistent appearance across DPI settings
                const int marginDips = 20;
                var margin = (int)DpiHelper.DipsToPhysicalPixels(marginDips, scale);

                var x = workArea.X + workArea.Width - windowSize.Width - margin;
                var y = workArea.Y + margin;

                // Clamp to keep window within display work area
                x = Math.Max(workArea.X, Math.Min(x, workArea.X + workArea.Width - windowSize.Width));
                y = Math.Max(workArea.Y, Math.Min(y, workArea.Y + workArea.Height - windowSize.Height));

                _appWindow.Move(new PointInt32(x, y));
            }
        }
    }

    /// <summary>
    /// Save current window position to settings.
    /// </summary>
    private void SaveWindowPosition()
    {
        if (_appWindow == null) return;

        var hWnd = WindowNative.GetWindowHandle(this);
        var scale = DpiHelper.GetScaleFactorForWindow(hWnd);
        var position = _appWindow.Position;

        _settings.MiniWindowXDips = DpiHelper.PhysicalPixelsToDips(position.X, scale);
        _settings.MiniWindowYDips = DpiHelper.PhysicalPixelsToDips(position.Y, scale);
        _settings.MiniWindowIsPinned = _isPinned;
        _settings.Save();
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
            _targetLanguageSelector.Reset();
        }
        finally
        {
            _suppressTargetLanguageSelectionChanged = false;
        }
    }

    private void InitializeTranslationServices()
    {
        try
        {
            _detectionService = new LanguageDetectionService(_settings);
            StatusText.Text = LocalizationService.Instance.GetString("StatusReady");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniWindow] Init error: {ex.Message}");
            StatusText.Text = LocalizationService.Instance.GetString("StatusError");
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
        var enabledServices = _settings.MiniWindowEnabledServices;
        var enabledQuerySettings = _settings.MiniWindowServiceEnabledQuery;

        // Get display names from TranslationManager (single source of truth)
        var manager = TranslationManagerService.Instance.Manager;

        foreach (var serviceId in enabledServices)
        {
            // Use service-provided DisplayName, fallback to serviceId if not found
            var displayName = manager.Services.TryGetValue(serviceId, out var service)
                ? service.DisplayName
                : serviceId;

            // In grammar mode, only show LLM services that implement IGrammarCorrectionService
            if (_currentMode == QueryMode.GrammarCorrection &&
                service is not IGrammarCorrectionService)
                continue;

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
    }

    /// <summary>
    /// Handle collapse/expand toggle from a service result item.
    /// </summary>
    private void OnServiceCollapseToggled(object? sender, ServiceQueryResult result)
    {
        // Trigger window resize when collapse state changes
        RequestResize();
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
                RequestResize();
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
            RequestResize();
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
            RequestResize();
        }
        finally
        {
            Interlocked.CompareExchange(ref _manualQueryCts, null, cts);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Handle window activation changes for auto-close behavior.
    /// </summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            Debug.WriteLine($"[MiniWindow] Activated: state={args.WindowActivationState}, loaded={_isLoaded}");
            QueueInputFocusAndSelectAll();
            return;
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Grace period: don't auto-close within 500ms of showing
            // This prevents a race condition where the window is hidden immediately
            // after being shown due to Windows returning focus to the previous app
            var timeSinceShow = DateTime.UtcNow - _lastShowTime;
            if (timeSinceShow.TotalMilliseconds < 500)
            {
                // Schedule a delayed check to handle the case where focus doesn't return
                ScheduleDelayedAutoClose();
                return;
            }

            // Window lost focus
            if (!_isPinned && _settings.MiniWindowAutoClose && !_isClosing)
            {
                // Auto-close when not pinned
                HideWindow();
            }
        }
    }

    /// <summary>
    /// Schedule a delayed auto-close check for when the grace period expires.
    /// </summary>
    private void ScheduleDelayedAutoClose()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                // Wait for the remainder of the grace period
                var timeSinceShow = DateTime.UtcNow - _lastShowTime;
                var remainingMs = 500 - timeSinceShow.TotalMilliseconds;
                if (remainingMs > 0)
                {
                    await Task.Delay((int)remainingMs);
                }

                // Check if window is still deactivated and should auto-close
                if (!_isPinned && _settings.MiniWindowAutoClose && !_isClosing && _appWindow?.IsVisible == true)
                {
                    // Only hide if we're still not the foreground window
                    var hWnd = WindowNative.GetWindowHandle(this);
                    if (GetForegroundWindow() != hWnd)
                    {
                        HideWindow();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MiniWindow: ScheduleDelayedAutoClose error: {ex.Message}");
            }
        });
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            _isClosing = true;
            
            // Stop any ongoing TTS audio immediately
            TextToSpeechService.Instance.Stop();
            
            SaveWindowPosition();
            await CleanupResourcesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniWindow] OnWindowClosed error: {ex}");
        }
    }

    /// <summary>
    /// Handle text changes to auto-resize window.
    /// </summary>
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // Delay to allow layout to complete
        RequestResize();
    }

    private void OnMainScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_resultControls == null || _resultControls.Count == 0) return;

        const double margin = 4.0;

        foreach (var control in _resultControls)
        {
            if (control.Visibility != Visibility.Visible || control.ActionButtonsPanel == null)
                continue;

            try
            {
                var transform = control.TransformToVisual(MainScrollViewer);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                
                // Y relative to the viewport (MainScrollViewer)
                var y = point.Y;
                
                double offsetY = 0;
                if (y < 0)
                {
                    offsetY = Math.Abs(y);
                }

                // Clamp to item height so headers and buttons don't leave the item container
                var maxOffset = control.ActualHeight - control.HeaderPanel.ActualHeight - margin;
                offsetY = Math.Clamp(offsetY, 0, Math.Max(0, maxOffset));

                control.HeaderPanel.Translation = new Vector3(0, (float)offsetY, 0);
                control.ActionButtonsPanel.Translation = new Vector3(0, (float)offsetY, 0);
            }
            catch (Exception)
            {
                // Ignore transformation errors if elements are being detached
            }
        }
    }


    /// <summary>
    /// Resize window to fit content with min/max height constraints.
    /// </summary>
    private void ResizeWindowToContent()
    {
        if (!_isLoaded || _appWindow == null || this.Content is not FrameworkElement content) return;

        try
        {
            // Get DPI scale
            var hWnd = WindowNative.GetWindowHandle(this);
            var scale = DpiHelper.GetScaleFactorForWindow(hWnd);

            // Get display area for the window to calculate dynamic height limit (80% of work area)
            var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            var workAreaHeightPx = displayArea?.WorkArea.Height ?? (int)DpiHelper.DipsToPhysicalPixels(1000, scale);
            var maxHeightPx = workAreaHeightPx * 0.8;

            // Get current window width in DIPs for proper measurement
            var currentSize = _appWindow.Size;
            var currentWidthDips = DpiHelper.PhysicalPixelsToDips(currentSize.Width, scale);

            // Measure desired size with actual width constraint (critical for text wrapping)
            content.Measure(new Windows.Foundation.Size(currentWidthDips, double.PositiveInfinity));
            var desiredHeightDips = content.DesiredSize.Height;

            // Calculate new height with limits
            var minHeightPx = DpiHelper.DipsToPhysicalPixels(200, scale);
            var desiredHeightPx = DpiHelper.DipsToPhysicalPixels(desiredHeightDips + 16, scale); // +16 for padding
            
            var newHeightPx = Math.Clamp(desiredHeightPx, minHeightPx, maxHeightPx);

            // Resize window (avoid micro-resizes)
            if (Math.Abs(currentSize.Height - newHeightPx) > 5)
            {
                _appWindow.Resize(new SizeInt32(currentSize.Width, (int)newHeightPx));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniWindow] ResizeWindowToContent error: {ex.Message}");
        }
    }

    /// <summary>
    /// Request a throttled resize. Leading edge fires on next dispatcher tick;
    /// subsequent calls within <see cref="ResizeThrottleMs"/> are absorbed,
    /// with a trailing-edge resize when the cooldown expires.
    /// </summary>
    private void RequestResize()
    {
        if (_isClosing) return;
        _resizePending = true;
        if (!_resizeThrottling)
        {
            _resizeThrottling = true;
            DispatcherQueue.TryEnqueue(ExecutePendingResize);
        }
    }

    private void ExecutePendingResize()
    {
        if (_isClosing) return;
        _resizePending = false;
        ResizeWindowToContent();
        EnsureResizeTimer();
        _resizeThrottleTimer!.Start();
    }

    private void EnsureResizeTimer()
    {
        if (_resizeThrottleTimer != null) return;
        _resizeThrottleTimer = DispatcherQueue.CreateTimer();
        _resizeThrottleTimer.Interval = TimeSpan.FromMilliseconds(ResizeThrottleMs);
        _resizeThrottleTimer.IsRepeating = false;
        _resizeThrottleTimer.Tick += OnResizeThrottleTimerTick;
    }

    private void OnResizeThrottleTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        _resizeThrottling = false;
        if (_resizePending && !_isClosing)
        {
            RequestResize();
        }
    }

    private async Task CleanupResourcesAsync()
    {
        // Clean up resize throttle timer
        if (_resizeThrottleTimer != null)
        {
            _resizeThrottleTimer.Stop();
            _resizeThrottleTimer.Tick -= OnResizeThrottleTimerTick;
            _resizeThrottleTimer = null;
        }

        // Clean up title bar drag region helper
        _titleBarHelper?.Dispose();
        _titleBarHelper = null;

        CancelCurrentQuery();

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

    /// <summary>
    /// Update pin button and always-on-top state.
    /// </summary>
    private void UpdatePinState()
    {
        if (_presenter != null)
        {
            _presenter.IsAlwaysOnTop = _isPinned;
        }

        PinButton.IsChecked = _isPinned;
        PinIcon.Glyph = _isPinned ? "\uE840" : "\uE718"; // Pinned vs Unpinned icon
    }

    private void SetLoading(bool loading)
    {
        if (_isClosing) return;

        _isQuerying = loading;

        var loc = LocalizationService.Instance;
        ToolTipService.SetToolTip(TranslateButton,
            loading ? loc.GetString("Cancel") : loc.GetString("TranslateTooltip"));

        // Swap icon: show cancel (X) glyph during query, translate glyph otherwise
        TranslateIcon.Glyph = loading ? "\uE711" : "\uE8C1";

        // Hide progress ring (cancel icon replaces it)
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private async Task StartQueryAsync()
    {
        if (_isClosing)
        {
            return;
        }

        if (_detectionService is null)
        {
            StatusText.Text = LocalizationService.Instance.GetString("ServiceNotInitialized");
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
            SetLoading(true);
            _hasAutoPlayedCurrentQuery = false;

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

            // Route based on mode
            if (_currentMode == QueryMode.GrammarCorrection)
            {
                await StartGrammarCorrectionInternalAsync(inputText, detectionService, ct);
                return;
            }

            // Detect language (only when source = Auto)
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
                DetectedLangText.Text = "";
                DetectedLangText.Visibility = Visibility.Collapsed;
            }
            _lastDetectedLanguage = detectedLanguage;

            // Determine target language
            var currentTarget = GetTargetLanguage();
            var targetLanguage = _targetLanguageSelector.ResolveTargetLanguage(
                detectedLanguage, currentTarget, detectionService);
            if (targetLanguage != currentTarget)
            {
                UpdateTargetLanguageSelector(targetLanguage);
            }

            // Create translation request
            var request = new TranslationRequest
            {
                Text = inputText,
                FromLanguage = detectedLanguage,
                ToLanguage = targetLanguage
            };

            // Execute translation for each enabled service in parallel
            // Only auto-query services with EnabledQuery=true
            var tasks = _serviceResults.Select(async serviceResult =>
            {
                // Skip manual-query services (EnabledQuery=false)
                if (!serviceResult.EnabledQuery)
                {
                    return QueryExecutionOutcome.Cancelled;
                }

                // Mark as queried for auto-query services
                serviceResult.MarkQueried();

                try
                {
                    // Acquire handle once per service to ensure consistent manager instance
                    using var handle = TranslationManagerService.Instance.AcquireHandle();
                    var manager = handle.Manager;
                    QueryExecutionOutcome outcome;

                    // Check if service supports streaming
                    if (manager.IsStreamingService(serviceResult.ServiceId))
                    {
                        // Streaming path for LLM services (pass manager to avoid re-acquiring)
                        await ExecuteStreamingTranslationForServiceAsync(
                            manager, serviceResult, request, detectedLanguage, targetLanguage, ct);
                        outcome = QueryExecutionOutcome.Success;
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

                            if (result.ResultKind == TranslationResultKind.Success &&
                                !_hasAutoPlayedCurrentQuery && SettingsService.Instance.AutoPlayTranslation)
                            {
                                var targetText = result.TranslatedText;
                                if (!string.IsNullOrEmpty(targetText))
                                {
                                    _hasAutoPlayedCurrentQuery = true;
                                    _ = TextToSpeechService.Instance.SpeakAsync(targetText, targetLanguage);
                                }
                            }

                            // Coalesced resize so ServiceResultItem.UpdateUI() completes first
                            RequestResize();
                        });

                        outcome = result.ResultKind == TranslationResultKind.Success
                            ? QueryExecutionOutcome.Success
                            : QueryExecutionOutcome.Neutral;
                    }

                    return outcome;
                }
                catch (OperationCanceledException)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;
                        serviceResult.IsLoading = false;
                        serviceResult.IsStreaming = false;
                        serviceResult.ClearQueried();
                    });
                    return QueryExecutionOutcome.Cancelled;
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
                        RequestResize();
                    });
                    SettingsService.Instance.ClearServiceTestStatus(serviceResult.ServiceId);
                    return QueryExecutionOutcome.Error;
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
                        RequestResize();
                    });
                    SettingsService.Instance.ClearServiceTestStatus(serviceResult.ServiceId);
                    return QueryExecutionOutcome.Error;
                }
            });

            var taskResults = await Task.WhenAll(tasks);
            var summary = QueryOutcomeSummary.From(taskResults);

            // Update status with completed count
            var loc = LocalizationService.Instance;
            StatusText.Text = summary.SuccessCount > 0
                ? string.Format(loc.GetString("ServiceResultsComplete"), summary.SuccessCount)
                : summary.ErrorCount > 0 ? loc.GetString("TranslationFailed") : "";
        }
        catch (OperationCanceledException)
        {
            // Query was cancelled - reset all service results that may be stuck in loading state
            ResetAllServiceResultsLoadingState();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{LocalizationService.Instance.GetString("StatusError")}: {ex.Message}";
            ResetAllServiceResultsLoadingState();
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
    /// Bypasses the translation pipeline entirely.
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
                DetectedLangText.Text = "";
                DetectedLangText.Visibility = Visibility.Collapsed;
            }
        }

        var request = new GrammarCorrectionRequest
        {
            Text = inputText,
            Language = detectedLang,
            IncludeExplanations = _settings.GrammarIncludeExplanations,
        };

        var tasks = _serviceResults
            .Where(sr => sr.EnabledQuery)
            .Select(sr => ExecuteGrammarCorrectionForServiceAsync(sr, request, ct))
            .ToArray();

        var taskResults = await Task.WhenAll(tasks);

        var successCount = taskResults.Count(r => r == true);
        var errorCount = taskResults.Count(r => r == false);

        var loc = LocalizationService.Instance;
        StatusText.Text = successCount > 0
            ? string.Format(loc.GetString("ServiceResultsComplete") ?? "{0} service(s) completed", successCount)
            : errorCount > 0 ? (loc.GetString("TranslationFailed") ?? "Check failed") : "";
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
                        RequestResize();
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
                RequestResize();
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
                RequestResize();
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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
                    // RequestResize() enqueues to next tick so ServiceResultItem.UpdateUI() completes first
                    RequestResize();
                });
                lastUpdateTime = now;
            }
        }

        stopwatch.Stop();

        // Final update with complete result
        var finalText = sb.ToString().Trim();

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

            if (result.ResultKind == TranslationResultKind.Success &&
                !_hasAutoPlayedCurrentQuery && SettingsService.Instance.AutoPlayTranslation)
            {
                var targetText = result.TranslatedText;
                if (!string.IsNullOrEmpty(targetText))
                {
                    _hasAutoPlayedCurrentQuery = true;
                    _ = TextToSpeechService.Instance.SpeakAsync(targetText, targetLanguage);
                }
            }

            // RequestResize() enqueues to next tick so ServiceResultItem.UpdateUI() completes first
            RequestResize();
        });
    }

    private TranslationLanguage GetSourceLanguage()
    {
        return LanguageComboHelper.GetSelectedLanguage(SourceLangCombo);
    }

    private TranslationLanguage GetTargetLanguage()
    {
        return LanguageComboHelper.GetSelectedLanguage(TargetLangCombo);
    }

    private void UpdateDetectedLanguageDisplay(TranslationLanguage detected)
    {
        if (!_isLoaded) return;

        if (detected != TranslationLanguage.Auto)
        {
            var displayName = detected.GetDisplayName();
            DetectedLangText.Text = string.Format(
                LocalizationService.Instance.GetString("DetectedLanguage"),
                displayName);
            DetectedLangText.Visibility = Visibility.Visible;
        }
        else
        {
            DetectedLangText.Text = "";
            DetectedLangText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateTargetLanguageSelector(TranslationLanguage targetLang)
    {
        if (!_isLoaded) return;

        _suppressTargetLanguageSelectionChanged = true;
        try
        {
            var targetIndex = LanguageComboHelper.FindLanguageIndex(TargetLangCombo, targetLang);
            if (targetIndex >= 0)
            {
                TargetLangCombo.SelectedIndex = targetIndex;
            }
        }
        finally
        {
            _suppressTargetLanguageSelectionChanged = false;
        }
    }

    // Event handlers

    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        UpdatePinState();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    private async void OnTranslateClicked(object sender, RoutedEventArgs e)
    {
        if (_isQuerying)
        {
            CancelCurrentQuery();
            return;
        }

        await StartQueryTrackedAsync();
    }

    private async void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
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

    private void OnSourcePlayButtonTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        OnSourcePlayClicked();
    }

    private async void OnSourcePlayClicked()
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

    private void SetSourceTextState(bool expanded)
    {
        _isSourceTextExpanded = expanded;
        
        if (expanded)
        {
            SourceTextCollapsed.Visibility = Visibility.Collapsed;
            InputTextBox.Visibility = Visibility.Visible;
            InputTextBox.Focus(FocusState.Programmatic);
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(SourceTextContainer, null);
            SourceTextContainer.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
        }
        else
        {
            SourceTextCollapsed.Visibility = Visibility.Visible;
            InputTextBox.Visibility = Visibility.Collapsed;
            SourceTextContainer.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
        }
    }

    private void OnSourceTextContainerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_isSourceTextExpanded)
        {
            SetSourceTextState(true);
        }
    }

    private void OnSourceTextContainerPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSourceTextExpanded)
        {
            var cursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(SourceTextContainer, cursor);
            SourceTextContainer.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            SourceTextContainer.BorderThickness = new Microsoft.UI.Xaml.Thickness(1);
        }
    }

    private void OnSourceTextContainerPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSourceTextExpanded)
        {
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(SourceTextContainer, null);
            SourceTextContainer.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
        }
    }

    private void OnInputTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            SetSourceTextState(false);
        }
    }

    private void OnSwapClicked(object sender, RoutedEventArgs e)
    {
        var sourceLanguage = GetSourceLanguage();

        if (sourceLanguage == TranslationLanguage.Auto)
        {
            // Source is Auto: swap target to detected language
            if (_lastDetectedLanguage == TranslationLanguage.Auto)
                return;

            UpdateTargetLanguageSelector(_lastDetectedLanguage);
            _targetLanguageSelector.MarkManualSelection();
        }
        else
        {
            // Source is specific: swap source ↔ target
            var currentTarget = GetTargetLanguage();
            var newSource = currentTarget;
            var newTarget = sourceLanguage;

            _suppressSourceLanguageSelectionChanged = true;
            try
            {
                var srcIdx = LanguageComboHelper.FindLanguageIndex(SourceLangCombo, newSource);
                if (srcIdx >= 0) SourceLangCombo.SelectedIndex = srcIdx;
            }
            finally
            {
                _suppressSourceLanguageSelectionChanged = false;
            }

            RebuildTargetCombo(newSource, newTarget);
            _targetLanguageSelector.MarkManualSelection();

            if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
            {
                _ = StartQueryTrackedAsync();
            }
        }
    }

    /// <summary>
    /// Handle source language selection change.
    /// </summary>
    private void OnSourceLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _suppressSourceLanguageSelectionChanged)
            return;

        var sourceLanguage = GetSourceLanguage();
        var currentTarget = GetTargetLanguage();
        RebuildTargetCombo(sourceLanguage, currentTarget);

        if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            _ = StartQueryTrackedAsync();
        }
    }

    /// <summary>
    /// Rebuild target combo excluding the source language.
    /// </summary>
    private void RebuildTargetCombo(TranslationLanguage sourceLanguage, TranslationLanguage currentTarget)
    {
        var loc = LocalizationService.Instance;
        _suppressTargetLanguageSelectionChanged = true;
        try
        {
            LanguageComboHelper.RebuildTargetCombo(
                TargetLangCombo, sourceLanguage, currentTarget, loc, out var newTarget);
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

    private void OnTargetLangChanged(object sender, SelectionChangedEventArgs e)
    {
        // User manually changed target language - respect their choice
        if (_suppressTargetLanguageSelectionChanged)
        {
            return;
        }

        _targetLanguageSelector.MarkManualSelection();
    }

    /// <summary>
    /// Set text and start translation (called from external sources).
    /// </summary>
    public void SetTextAndTranslate(string text)
    {
        _targetLanguageSelector.Reset();

        // Clear all cached results IMMEDIATELY to prevent showing old data
        foreach (var result in _serviceResults)
        {
            result.Reset();
        }

        InputTextBox.Text = text;
        _ = StartQueryTrackedAsync();
    }

    /// <summary>
    /// Show the window and bring it to front.
    /// </summary>
    public void ShowAndActivate()
    {
        _isClosing = false;
        _lastShowTime = DateTime.UtcNow;
        _appWindow?.Show();

        // Try to bring window to front using multiple methods
        try
        {
            _appWindow?.MoveInZOrderAtTop();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"MiniWindow: MoveInZOrderAtTop failed: {ex.Message}");
        }

        // Use Win32 SetForegroundWindow to forcefully bring window to front
        var foregroundSet = ForegroundWindowHelper.TryBringToFront(this, "MiniWindow");
        if (!foregroundSet)
        {
            System.Diagnostics.Debug.WriteLine("MiniWindow: SetForegroundWindow failed; relying on Activate()");
        }

        this.Activate();
        SetSourceTextState(false);
        QueueInputFocusAndSelectAll();

        // Resize window to fit existing content (delayed to allow layout to complete)
        RequestResize();
    }

    private void QueueInputFocusAndSelectAll(int attemptsRemaining = InputFocusMaxAttempts)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var attempt = InputFocusMaxAttempts - attemptsRemaining + 1;

            if (_isClosing)
            {
                Debug.WriteLine($"[MiniWindow] QueueInputFocusAndSelectAll attempt {attempt}/{InputFocusMaxAttempts}: aborted because window is closing");
                return;
            }

            if (!_isLoaded || InputTextBox.XamlRoot is null || !InputTextBox.IsEnabled)
            {
                Debug.WriteLine(
                    $"[MiniWindow] QueueInputFocusAndSelectAll attempt {attempt}/{InputFocusMaxAttempts}: " +
                    $"loaded={_isLoaded}, xamlRootReady={InputTextBox.XamlRoot is not null}, enabled={InputTextBox.IsEnabled}");
                if (attemptsRemaining > 1)
                {
                    await Task.Delay(InputFocusRetryDelayMs);
                    QueueInputFocusAndSelectAll(attemptsRemaining - 1);
                }
                return;
            }

            if (!IsForeground)
            {
                Debug.WriteLine(
                    $"[MiniWindow] QueueInputFocusAndSelectAll attempt {attempt}/{InputFocusMaxAttempts}: " +
                    "window is not foreground yet");
                if (attemptsRemaining > 1)
                {
                    await Task.Delay(InputFocusRetryDelayMs);
                    QueueInputFocusAndSelectAll(attemptsRemaining - 1);
                }
                return;
            }

            var focusResult = InputTextBox.Focus(FocusState.Programmatic);
            if (focusResult)
            {
                InputTextBox.SelectAll();
            }

            var focusedElement = FocusManager.GetFocusedElement(InputTextBox.XamlRoot);
            var hasInputFocus = ReferenceEquals(focusedElement, InputTextBox);
            Debug.WriteLine(
                $"[MiniWindow] QueueInputFocusAndSelectAll attempt {attempt}/{InputFocusMaxAttempts}: " +
                $"focusResult={focusResult}, hasInputFocus={hasInputFocus}, focusedElement={focusedElement?.GetType().Name ?? "<null>"}");

            if (hasInputFocus)
            {
                return;
            }

            if (attemptsRemaining > 1)
            {
                await Task.Delay(InputFocusRetryDelayMs);
                QueueInputFocusAndSelectAll(attemptsRemaining - 1);
            }
        });
    }

    /// <summary>
    /// Hide the window.
    /// </summary>
    public void HideWindow()
    {
        // Stop any ongoing TTS audio immediately
        TextToSpeechService.Instance.Stop();
        
        SaveWindowPosition();
        _appWindow?.Hide();
    }

    /// <summary>
    /// Check if window is currently visible.
    /// </summary>
    public bool IsVisible => _appWindow?.IsVisible ?? false;

    /// <summary>
    /// Check if this window is currently the foreground window.
    /// </summary>
    public bool IsForeground
    {
        get
        {
            try
            {
                var hWnd = WindowNative.GetWindowHandle(this);
                return GetForegroundWindow() == hWnd;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Refresh service result controls when settings change.
    /// </summary>
    public void RefreshServiceResults()
    {
        InitializeServiceResults();
    }

    /// <summary>
    /// Refresh language combo boxes when SelectedLanguages changes in settings.
    /// Repopulates combos and restores the saved target language selection.
    /// </summary>
    public void RefreshLanguageCombos()
    {
        if (!_isLoaded) return;
        ApplyLocalization();
        ApplySettings();
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
    /// Apply theme to the window content.
    /// </summary>
    public void ApplyTheme(ElementTheme theme)
    {
        if (this.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
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
}
