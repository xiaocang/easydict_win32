using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
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
using TranslationLanguage = Easydict.TranslationService.Models.Language;

namespace Easydict.WinUI.Views;

/// <summary>
/// Fixed translation window that stays visible until hidden.
/// Unlike Mini Window, it does not auto-close on focus loss; users can toggle always-on-top.
/// </summary>
public sealed partial class FixedWindow : Window
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private LanguageDetectionService? _detectionService;
    // Owned by StartQueryAsync() - only that method creates and disposes via its finally block.
    // Other code may Cancel() but must NOT Dispose().
    private CancellationTokenSource? _currentQueryCts;
    private Task? _currentQueryTask;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly List<ServiceQueryResult> _serviceResults = new();
    private readonly List<IServiceResultView> _resultControls = new();
    private TranslationLanguage _lastDetectedLanguage = TranslationLanguage.Auto;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;
    private bool _isPinned;
    private bool _isLoaded;
    private bool _isQuerying;
    private volatile bool _isClosing;
    private readonly TargetLanguageSelector _targetLanguageSelector;
    private bool _suppressTargetLanguageSelectionChanged;
    private bool _suppressSourceLanguageSelectionChanged;
    private QueryMode _currentMode = QueryMode.Translation;
    private QuickQueryLanguageResolution? _lastQuickQueryResolution;
    private bool _showingGrammarFallbackNotice;
    private TitleBarDragRegionHelper? _titleBarHelper;
    private CompactWindowControlsView? _compactWindowControls;
    private WindowDragSession? _compactWindowDragSession;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeThrottleTimer;
    private bool _resizePending;      // resize requested but not yet executed
    private bool _resizeThrottling;   // inside cooldown window

    // See StreamingTextCoalescer / MiniWindow for the rationale.
    private StreamingTextCoalescer? _streamingCoalescer;

    private const int ResizeThrottleMs = 150;
    private const int InputFocusRetryDelayMs = 50;
    private const int InputFocusMaxAttempts = 10;
    private const double CompactWindowControlsIdleOpacity = 0.52;

    /// <summary>
    /// Maximum time to wait for in-flight query to complete during cleanup.
    /// </summary>
    private const int QueryShutdownTimeoutSeconds = 2;

    public FixedWindow()
    {
        _targetLanguageSelector = new TargetLanguageSelector(_settings);
        this.InitializeComponent();
        InitializeCompactWindowControls();

        // Frame-rate streaming text applicator — see StreamingTextCoalescer.
        _streamingCoalescer = new StreamingTextCoalescer(DispatcherQueue);

        // Get AppWindow for window management
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Mica gives the persistent floating window a Fluent-native look. Falls through
        // gracefully on hosts that don't support it.
        TryApplyMicaBackdrop();

        // Configure window appearance
        ConfigureWindow();

        // Initialize translation services
        InitializeTranslationServices();

        // Handle window events
        this.Activated += OnWindowActivated;
        this.Closed += OnWindowClosed;

        SettingsService.Instance.HideEmptyServiceResultsChanged += OnHideEmptyServiceResultsChanged;

        // Initialize service result controls
        InitializeServiceResults();

        // Subscribe to text changes for auto-resize
        InputTextBox.TextChanged += OnTextChanged;

        // Track when content is loaded for safe UI operations
        if (this.Content is FrameworkElement content)
        {
            content.ActualThemeChanged += OnContentActualThemeChanged;
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
                new FrameworkElement[] { PinButton, OcrButton, CloseButton },
                "FixedWindow");
            _titleBarHelper.Initialize();
        }
    }

    private void OnContentActualThemeChanged(FrameworkElement sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
            ApplyTheme(MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme), forceResourceRefresh: false));
    }

    /// <summary>
    /// Apply localization to all UI elements using LocalizationService.
    /// </summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;

        // Window title - keep "Easydict" brand name, only localize "Fixed"
        this.Title = $"Easydict ᵇᵉᵗᵃ {loc.GetString("FixedTranslate")}";

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

        // Placeholders
        InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder");

        // Tooltips
        ToolTipService.SetToolTip(PinButton, loc.GetString("PinWindowTooltip"));
        ToolTipService.SetToolTip(OcrButton, loc.GetString("OcrButtonTooltip"));
        ToolTipService.SetToolTip(CloseButton, loc.GetString("HideWindow"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PinButton, loc.GetString("PinWindowTooltip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OcrButton, loc.GetString("OcrButtonTooltip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseButton, loc.GetString("HideWindow"));
        if (_compactWindowControls is { } compactControls)
        {
            ToolTipService.SetToolTip(compactControls.CloseButton, loc.GetString("HideWindow"));
            ToolTipService.SetToolTip(compactControls.DragIsland, loc.GetStringOrDefault("DragWindowTooltip", "Drag window"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(compactControls.CloseButton, loc.GetString("HideWindow"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                compactControls.DragIsland,
                loc.GetStringOrDefault("DragWindowTooltip", "Drag window"));
        }
        ToolTipService.SetToolTip(SourceLangCombo, loc.GetString("SourceLanguageTooltip"));
        ToolTipService.SetToolTip(SwapButton, loc.GetString("SwapLanguagesTooltip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SwapButton, loc.GetString("SwapLanguagesTooltip"));
        ToolTipService.SetToolTip(TargetLangCombo, loc.GetString("TargetLanguageTooltip"));
        ToolTipService.SetToolTip(TranslateButton, loc.GetString("TranslateTooltip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TranslateButton, loc.GetString("TranslateTooltip"));
    }

    private void InitializeCompactWindowControls()
    {
        if (WindowSurface.Child is not Grid surfaceGrid)
        {
            return;
        }

        _compactWindowControls = new CompactWindowControlsView();
        surfaceGrid.Children.Add(_compactWindowControls.Root);
        _compactWindowControls.Root.PointerEntered += OnCompactWindowControlsPointerEntered;
        _compactWindowControls.Root.PointerExited += OnCompactWindowControlsPointerExited;
        _compactWindowControls.DragIsland.PointerPressed += OnCompactDragIslandPointerPressed;
        _compactWindowControls.DragIsland.PointerMoved += OnCompactDragIslandPointerMoved;
        _compactWindowControls.DragIsland.PointerReleased += OnCompactDragIslandPointerReleased;
        _compactWindowControls.DragIsland.PointerCanceled += OnCompactDragIslandPointerCanceled;
        _compactWindowControls.DragIsland.PointerCaptureLost += OnCompactDragIslandPointerCaptureLost;
        _compactWindowControls.CloseButton.Click += OnCompactCloseClicked;
        _compactWindowControls.RefreshTheme(Content as FrameworkElement);
    }

    /// <summary>
    /// Apply a Mica system backdrop. Mica respects ElementTheme automatically and is
    /// supported on Windows 11 (Win10 and unsupported configurations silently keep
    /// the solid theme background).
    /// </summary>
    private void TryApplyMicaBackdrop()
    {
        try
        {
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"[FixedWindow] Mica backdrop unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Configure the compact fixed window chrome and apply the saved always-on-top state.
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
            _presenter.IsAlwaysOnTop = _settings.FixedWindowIsPinned;  // Fixed window topmost (toggleable)
        }

        // Extend content into title bar for custom drag area
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        // Note: SetTitleBar() doesn't work reliably in unpackaged WinUI 3 apps.
        // We use InputNonClientPointerSource.SetRegionRects() instead to define
        // passthrough regions for interactive controls. The rest becomes draggable.

        // Set window size from Fixed Window settings
        var scale = DpiHelper.GetScaleFactorForWindow(WindowNative.GetWindowHandle(this));
        var widthPx = DpiHelper.DipsToPhysicalPixels(_settings.FixedWindowWidthDips, scale);
        var heightPx = DpiHelper.DipsToPhysicalPixels(_settings.FixedWindowHeightDips, scale);
        _appWindow.Resize(new SizeInt32((int)widthPx, (int)heightPx));

        // Position window
        PositionWindow();

        // Apply pin state + quick-action button visibility from settings
        UpdatePinState();
        ApplyButtonVisibility();
        ApplyCompactChromeLayout();
    }

    /// <summary>
    /// Position window based on saved position or center on screen.
    /// </summary>
    private void PositionWindow()
    {
        if (_appWindow == null) return;

        var hWnd = WindowNative.GetWindowHandle(this);
        var scale = DpiHelper.GetScaleFactorForWindow(hWnd);

        // Try saved position first; fall through to the default if it's off-screen
        // (e.g. saved on a now-disconnected external monitor — issue #148).
        // Use the explicit "saved" flag rather than `> 0` so monitors with negative
        // virtual-screen coordinates (placed left/above primary) still restore.
        if (_settings.FixedWindowPositionSaved)
        {
            var savedX = (int)DpiHelper.DipsToPhysicalPixels(_settings.FixedWindowXDips, scale);
            var savedY = (int)DpiHelper.DipsToPhysicalPixels(_settings.FixedWindowYDips, scale);
            if (WindowPositionHelper.TryGetVisiblePosition(
                    new PointInt32(savedX, savedY), _appWindow.Size, out var safe))
            {
                _appWindow.Move(safe);
                return;
            }
        }

        // Default: center on primary display
        var primary = DisplayArea.Primary;
        if (primary != null)
        {
            var workArea = primary.WorkArea;
            var windowSize = _appWindow.Size;
            var x = (workArea.Width - windowSize.Width) / 2 + workArea.X;
            var y = (workArea.Height - windowSize.Height) / 2 + workArea.Y;
            _appWindow.Move(new PointInt32(x, y));
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

        _settings.FixedWindowXDips = DpiHelper.PhysicalPixelsToDips(position.X, scale);
        _settings.FixedWindowYDips = DpiHelper.PhysicalPixelsToDips(position.Y, scale);
        _settings.FixedWindowPositionSaved = true;
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
            System.Diagnostics.Debug.WriteLine($"[FixedWindow] Init error: {ex.Message}");
            StatusText.Text = LocalizationService.Instance.GetString("StatusError");
        }
    }

    /// <summary>
    /// Initialize service result controls based on enabled services.
    /// </summary>
    private void InitializeServiceResults()
    {
        ReleaseServiceResultControls();

        // Get enabled services and EnabledQuery settings from settings
        var enabledServices = _settings.FixedWindowEnabledServices;
        var enabledQuerySettings = _settings.FixedWindowServiceEnabledQuery;

        // Get display names from TranslationManager (single source of truth)
        var manager = TranslationManagerService.Instance.Manager;
        var grammarSourceLanguage = _lastQuickQueryResolution?.EffectiveSourceLanguage
            ?? TranslationLanguage.Auto;

        foreach (var serviceId in enabledServices)
        {
            // Use service-provided DisplayName, fallback to serviceId if not found
            var displayName = manager.Services.TryGetValue(serviceId, out var service)
                ? service.DisplayName
                : serviceId;

            var isGrammarCapable = service is not null
                && GrammarCorrectionServiceAvailability.IsAvailable(service, grammarSourceLanguage);

            // Get EnabledQuery setting (default true if not found)
            var enabledQuery = enabledQuerySettings.TryGetValue(serviceId, out var eq) ? eq : true;

            var result = new ServiceQueryResult
            {
                ServiceId = serviceId,
                ServiceDisplayName = displayName,
                EnabledQuery = enabledQuery,
                IsExpanded = enabledQuery, // Manual-query services start collapsed
                CurrentMode = _currentMode,
                IsGrammarCapable = isGrammarCapable,
            };

            _serviceResults.Add(result);
            ServiceResultViewHost.Add(
                result,
                _resultControls,
                ResultsPanel,
                OnServiceCollapseToggled,
                OnServiceQueryRequested,
                Content as FrameworkElement,
                OnFoundryLocalStartRequested);
        }

        ReorderResultsPanel();
    }

    private bool HasEnabledGrammarCorrectionService(TranslationLanguage sourceLanguage)
    {
        var manager = TranslationManagerService.Instance.Manager;
        return _settings.FixedWindowEnabledServices.Any(serviceId =>
            manager.Services.TryGetValue(serviceId, out var service)
            && GrammarCorrectionServiceAvailability.IsAvailable(service, sourceLanguage));
    }

    private QuickQueryLanguageResolution ResolveQuickQueryLanguage(
        TranslationLanguage effectiveSource,
        bool grammarCorrectionAvailable)
    {
        return _targetLanguageSelector.ResolveQueryLanguage(
            GetSourceLanguage(),
            GetTargetLanguage(),
            effectiveSource,
            grammarCorrectionAvailable);
    }

    private void ApplyQuickQueryResolution(
        QuickQueryLanguageResolution resolution,
        bool reinitializeServiceResults)
    {
        var previousMode = _currentMode;
        _lastQuickQueryResolution = resolution;
        _currentMode = resolution.EffectiveMode;
        _showingGrammarFallbackNotice = resolution.GrammarCorrectionFallback;

        var loc = LocalizationService.Instance;
        InputTextBox.PlaceholderText = _currentMode == QueryMode.GrammarCorrection
            ? loc.GetString("InputPlaceholder_Grammar")
            : loc.GetString("InputPlaceholder");
        var translateTooltip = _currentMode == QueryMode.GrammarCorrection
            ? loc.GetString("TranslateButton_Grammar_Tooltip")
            : loc.GetString("TranslateTooltip");
        ToolTipService.SetToolTip(TranslateButton, translateTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TranslateButton, translateTooltip);

        if (reinitializeServiceResults && previousMode != _currentMode)
        {
            InitializeServiceResults();
        }
        else
        {
            foreach (var serviceResult in _serviceResults)
            {
                serviceResult.CurrentMode = _currentMode;
            }
        }

        UpdateQuickQueryModeStatus(resolution);
    }

    private void UpdateQuickQueryModeStatus(QuickQueryLanguageResolution resolution)
    {
        if (!_isLoaded || MinimalThemeService.IsActive)
        {
            DetectedLangText.Text = "";
            DetectedLangText.Visibility = Visibility.Collapsed;
            return;
        }

        var loc = LocalizationService.Instance;
        if (resolution.GrammarCorrectionFallback)
        {
            DetectedLangText.Text = loc.GetStringOrDefault(
                "GrammarCorrectionFallbackNotice",
                "No grammar-capable AI service is enabled, so this query fell back to translation. Enable an AI service that supports grammar correction to show correction details when source and target are the same.");
            DetectedLangText.Visibility = IsCompactChrome ? Visibility.Collapsed : Visibility.Visible;
            RequestResize();
            return;
        }

        if (resolution.EffectiveMode == QueryMode.GrammarCorrection)
        {
            DetectedLangText.Text = loc.GetStringOrDefault(
                "GrammarCorrectionActiveNotice",
                "Grammar check mode: AI correction services will run. Choose a different target language to translate.");
            DetectedLangText.Visibility = IsCompactChrome ? Visibility.Collapsed : Visibility.Visible;
            RequestResize();
            return;
        }

        _showingGrammarFallbackNotice = false;
        UpdateDetectedLanguageDisplay(resolution.EffectiveSourceLanguage);
    }

    private void RefreshQuickQueryModePreview()
    {
        var selectedSource = GetSourceLanguage();
        var effectiveSource = selectedSource != TranslationLanguage.Auto
            ? selectedSource
            : _lastDetectedLanguage;

        if (effectiveSource == TranslationLanguage.Auto)
        {
            if (!_showingGrammarFallbackNotice)
            {
                UpdateDetectedLanguageDisplay(effectiveSource);
            }
            return;
        }

        var resolution = ResolveQuickQueryLanguage(
            effectiveSource,
            HasEnabledGrammarCorrectionService(effectiveSource));
        ApplyQuickQueryResolution(resolution, reinitializeServiceResults: true);
    }

    private void RebuildServiceResultControlsForCurrentTheme()
    {
        if (_serviceResults.Count == 0)
        {
            return;
        }

        ServiceResultViewHost.RebuildForCurrentTheme(
            _serviceResults,
            _resultControls,
            ResultsPanel,
            OnServiceCollapseToggled,
            OnServiceQueryRequested,
            Content as FrameworkElement,
            OnFoundryLocalStartRequested);

        ReorderResultsPanel();
        RequestResize();
    }

    private void ReleaseServiceResultControls(bool clearResults = true)
    {
        ServiceResultViewHost.Release(
            _resultControls,
            ResultsPanel,
            OnServiceCollapseToggled,
            OnServiceQueryRequested,
            OnFoundryLocalStartRequested);

        if (clearResults)
        {
            _serviceResults.Clear();
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
        await OnServiceQueryRequestedAsync(sender, serviceResult);
    }

    private async Task OnServiceQueryRequestedAsync(object? sender, ServiceQueryResult serviceResult)
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

        try
        {
            var phiSilicaPromptResult = await PhiSilicaModelPreparationPromptService.PromptAndPrepareIfNeededAsync(
                [serviceResult],
                _settings,
                (Content as FrameworkElement)?.XamlRoot,
                async dialog => await dialog.ShowAsync(),
                CancellationToken.None);
            if (phiSilicaPromptResult == PhiSilicaModelPreparationPromptResult.Disabled)
            {
                InitializeServiceResults();
                return;
            }

            if (PhiSilicaModelPreparationPromptService.ShouldSkipServiceForCurrentQuery(serviceResult, phiSilicaPromptResult))
            {
                return;
            }

            // Mark as loading and queried
            serviceResult.IsLoading = true;
            serviceResult.MarkQueried();

            // Detect language (use cached if available from recent query)
            // Run detection on thread pool to avoid blocking UI thread
            var detectedLanguage = _lastDetectedLanguage != TranslationLanguage.Auto
                ? _lastDetectedLanguage
                : await Task.Run(() => _detectionService.DetectAsync(inputText, CancellationToken.None));
            _lastDetectedLanguage = detectedLanguage;
            var resolution = ResolveQuickQueryLanguage(
                detectedLanguage,
                HasEnabledGrammarCorrectionService(detectedLanguage));
            serviceResult.CurrentMode = resolution.EffectiveMode;

            if (resolution.EffectiveMode == QueryMode.GrammarCorrection
                && serviceResult.IsGrammarCapable)
            {
                var grammarRequest = new GrammarCorrectionRequest
                {
                    Text = inputText,
                    Language = detectedLanguage,
                    IncludeExplanations = _settings.GrammarIncludeExplanations,
                };
                await ExecuteGrammarCorrectionForServiceAsync(serviceResult, grammarRequest, CancellationToken.None);
                return;
            }

            // Get target language
            var targetLanguage = resolution.EffectiveTargetLanguage;
            if (targetLanguage == TranslationLanguage.Auto)
            {
                serviceResult.IsLoading = false;
                serviceResult.ClearQueried();
                return;
            }

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
                // Run on thread pool to avoid blocking UI thread
                var result = await Task.Run(
                    () => manager.TranslateAsync(request, CancellationToken.None, serviceResult.ServiceId));
                serviceResult.Result = result;
                serviceResult.IsLoading = false;
                serviceResult.ApplyAutoCollapseLogic();
                UpdatePhoneticDeduplication();
                ReorderResultsPanel();
                RequestResize();
            }
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
    }

    private async void OnFoundryLocalStartRequested(object? sender, ServiceQueryResult serviceResult)
    {
        await FoundryLocalRecoveryCoordinator.StartAndRetryAsync(
            serviceResult,
            ct => TranslationManagerService.Instance.PrepareFoundryLocalAsync(ct),
            (_, ct) => OnServiceQueryRequestedAsync(sender, serviceResult),
            _ => RequestResize(),
            isAborted: () => _isClosing);
    }

    private void OnResultsScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ServiceResultViewHost.UpdateStickyHeaders(_resultControls, ResultsScrollViewer);
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            SettingsService.Instance.HideEmptyServiceResultsChanged -= OnHideEmptyServiceResultsChanged;
            if (Content is FrameworkElement content)
            {
                content.ActualThemeChanged -= OnContentActualThemeChanged;
            }

            _isClosing = true;
            SaveWindowPosition();
            await CleanupResourcesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FixedWindow] OnWindowClosed error: {ex}");
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

            // Get current window width in DIPs for proper measurement
            var currentSize = _appWindow.Size;
            var currentWidthDips = DpiHelper.PhysicalPixelsToDips(currentSize.Width, scale);

            // Measure desired size with actual width constraint (critical for text wrapping)
            content.Measure(new Windows.Foundation.Size(currentWidthDips, double.PositiveInfinity));
            var desiredHeight = content.DesiredSize.Height;

            // Calculate new height with limits (min from AppearanceService, max 800 DIPs)
            var minHeight = DpiHelper.DipsToPhysicalPixels(AppearanceService.MinFloatingWindowHeightDips, scale);
            var maxHeight = DpiHelper.DipsToPhysicalPixels(800, scale);
            var newHeight = DpiHelper.DipsToPhysicalPixels(desiredHeight + 16, scale); // +16 for padding
            newHeight = Math.Clamp(newHeight, minHeight, maxHeight);

            // Resize window (avoid micro-resizes)
            if (Math.Abs(currentSize.Height - newHeight) > 5)
            {
                _appWindow.Resize(new SizeInt32(currentSize.Width, (int)newHeight));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FixedWindow] ResizeWindowToContent error: {ex.Message}");
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

        _streamingCoalescer?.Dispose();
        _streamingCoalescer = null;

        // Clean up title bar drag region helper
        _titleBarHelper?.Dispose();
        _titleBarHelper = null;

        CancelCurrentQuery();

        var task = _currentQueryTask;
        var detectionService = _detectionService;  // Capture before nulling

        _currentQueryTask = null;
        _detectionService = null;  // Clear immediately to prevent re-use
        ReleaseServiceResultControls();

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

    private void SetLoading(bool loading)
    {
        if (_isClosing) return;

        _isQuerying = loading;

        var loc = LocalizationService.Instance;
        var translateTooltip = loading ? loc.GetString("Cancel") : loc.GetString("TranslateTooltip");
        ToolTipService.SetToolTip(TranslateButton, translateTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TranslateButton, translateTooltip);

        // Swap icon: show cancel (X) glyph during query, translate glyph otherwise
        TranslateIcon.Glyph = loading ? "\uE711" : "\uE8C1";
        MinimalThemeService.ApplyAccentIconForeground(TranslateIcon, LoadingRing);

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

        var ct = currentCts.Token;

        try
        {
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

            var resolution = ResolveQuickQueryLanguage(
                detectedLanguage,
                HasEnabledGrammarCorrectionService(detectedLanguage));
            ApplyQuickQueryResolution(resolution, reinitializeServiceResults: true);

            var targetLanguage = resolution.EffectiveTargetLanguage;
            if (resolution.GrammarCorrectionFallback && targetLanguage != TranslationLanguage.Auto)
            {
                UpdateTargetLanguageSelector(targetLanguage);
                StatusText.Text = LocalizationService.Instance.GetString("GrammarCorrectionFallbackNotice");
            }

            if (_serviceResults.Count == 0)
            {
                StatusText.Text = LocalizationService.Instance.GetString("NoServicesEnabled");
                return;
            }

            if (resolution.EffectiveMode == QueryMode.Translation &&
                targetLanguage == TranslationLanguage.Auto)
            {
                StatusText.Text = LocalizationService.Instance.GetString("NoAvailableTargetLanguage");
                return;
            }

            var phiSilicaPromptResult = await PhiSilicaModelPreparationPromptService.PromptAndPrepareIfNeededAsync(
                _serviceResults.Where(result => result.EnabledQuery),
                _settings,
                (Content as FrameworkElement)?.XamlRoot,
                async dialog => await dialog.ShowAsync(),
                ct);
            if (phiSilicaPromptResult == PhiSilicaModelPreparationPromptResult.Disabled)
            {
                InitializeServiceResults();
                if (resolution.EffectiveMode == QueryMode.GrammarCorrection &&
                    !HasEnabledGrammarCorrectionService(detectedLanguage))
                {
                    resolution = ResolveQuickQueryLanguage(
                        detectedLanguage,
                        grammarCorrectionAvailable: false);
                    ApplyQuickQueryResolution(resolution, reinitializeServiceResults: true);
                    targetLanguage = resolution.EffectiveTargetLanguage;
                    if (targetLanguage != TranslationLanguage.Auto)
                    {
                        UpdateTargetLanguageSelector(targetLanguage);
                    }
                    StatusText.Text = LocalizationService.Instance.GetStringOrDefault(
                        "GrammarCorrectionFallbackNotice",
                        "No grammar-capable AI service is enabled, so this query fell back to translation. Enable an AI service that supports grammar correction to show correction details when source and target are the same.");
                }

                if (resolution.EffectiveMode == QueryMode.Translation &&
                    targetLanguage == TranslationLanguage.Auto)
                {
                    StatusText.Text = LocalizationService.Instance.GetStringOrDefault(
                        "NoAvailableTargetLanguage",
                        "No available target language for translation.");
                    return;
                }

                if (!_serviceResults.Any(result => result.EnabledQuery))
                {
                    return;
                }
            }

            SetLoading(true);

            // Reset all service results
            foreach (var result in _serviceResults)
            {
                result.Reset();
                // Only set loading for auto-query services
                if (PhiSilicaModelPreparationPromptService.ShouldQueryServiceForCurrentQuery(result, phiSilicaPromptResult))
                {
                    result.IsLoading = true;
                }
            }

            if (resolution.EffectiveMode == QueryMode.GrammarCorrection)
            {
                await StartGrammarCorrectionInternalAsync(
                    inputText, detectedLanguage, targetLanguage, ct);
                return;
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
                if (!PhiSilicaModelPreparationPromptService.ShouldQueryServiceForCurrentQuery(serviceResult, phiSilicaPromptResult))
                {
                    return;
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
                        // Streaming path for LLM services (pass manager to avoid re-acquiring)
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
                            ReorderResultsPanel();
                            // Coalesced resize so ServiceResultItem.UpdateUI() completes first
                            RequestResize();
                        });
                    }
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
                        DispatcherQueue.TryEnqueue(() => RequestResize());
                    });
                    SettingsService.Instance.ClearServiceTestStatus(serviceResult.ServiceId);
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
                        DispatcherQueue.TryEnqueue(() => RequestResize());
                    });
                    SettingsService.Instance.ClearServiceTestStatus(serviceResult.ServiceId);
                }
            });

            await Task.WhenAll(tasks);

            // Update status with completed count
            var loc = LocalizationService.Instance;
            var successCount = _serviceResults.Count(r => r.Result != null);
            var errorCount = _serviceResults.Count(r => r.HasError);
            StatusText.Text = successCount > 0
                ? string.Format(loc.GetString("ServiceResultsComplete"), successCount)
                : errorCount > 0 ? loc.GetString("TranslationFailed") : "";
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
    /// Execute the correction-mode query in parallel. Grammar-capable services
    /// run grammar correction; remaining services fall back to normal translation
    /// using the user-selected target language.
    /// </summary>
    private async Task StartGrammarCorrectionInternalAsync(
        string inputText,
        TranslationLanguage detectedLang,
        TranslationLanguage targetLanguage,
        CancellationToken ct)
    {
        var grammarRequest = new GrammarCorrectionRequest
        {
            Text = inputText,
            Language = detectedLang,
            IncludeExplanations = _settings.GrammarIncludeExplanations,
        };

        var translationRequest = new TranslationRequest
        {
            Text = inputText,
            FromLanguage = detectedLang,
            ToLanguage = targetLanguage,
        };

        var tasks = _serviceResults
            .Where(sr => sr.EnabledQuery)
            .Select(sr => sr.IsGrammarCapable
                ? ExecuteGrammarCorrectionForServiceAsync(sr, grammarRequest, ct)
                : ExecuteTranslationForServiceAsync(sr, translationRequest, detectedLang, targetLanguage, ct))
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
    /// Execute regular translation for a single service inside the mixed
    /// correction-mode flow. Mirrors <see cref="ExecuteGrammarCorrectionForServiceAsync"/>'s
    /// return contract: true on success, false on error, null on cancelled/skipped.
    /// </summary>
    private async Task<bool?> ExecuteTranslationForServiceAsync(
        ServiceQueryResult serviceResult,
        TranslationRequest request,
        TranslationLanguage detectedLanguage,
        TranslationLanguage targetLanguage,
        CancellationToken ct)
    {
        serviceResult.MarkQueried();

        try
        {
            using var handle = TranslationManagerService.Instance.AcquireHandle();
            var manager = handle.Manager;

            if (manager.IsStreamingService(serviceResult.ServiceId))
            {
                await ExecuteStreamingTranslationForServiceAsync(
                    manager, serviceResult, request, detectedLanguage, targetLanguage, ct);
                return true;
            }

            var result = await Task.Run(
                () => manager.TranslateAsync(request, ct, serviceResult.ServiceId), ct);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;
                serviceResult.Result = result;
                serviceResult.IsLoading = false;
                serviceResult.ApplyAutoCollapseLogic();
                UpdatePhoneticDeduplication();
                ReorderResultsPanel();
                RequestResize();
            });

            return result.ResultKind == TranslationResultKind.Success;
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
            return null;
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
            return false;
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing) return;
                serviceResult.Error = new TranslationException(ex.Message, ex)
                {
                    ErrorCode = TranslationErrorCode.Unknown,
                    ServiceId = serviceResult.ServiceId,
                };
                serviceResult.IsLoading = false;
                serviceResult.IsStreaming = false;
                serviceResult.ApplyAutoCollapseLogic();
                RequestResize();
            });
            SettingsService.Instance.ClearServiceTestStatus(serviceResult.ServiceId);
            return false;
        }
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
                    _streamingCoalescer?.Update(serviceResult, sb.ToString());
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
                _streamingCoalescer?.Forget(serviceResult);
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

        // Snapshots flow through the coalescer so N parallel services collapse to
        // ≤1 UI callback per frame, instead of N×20 dispatcher.TryEnqueue/sec which
        // would invalidate wrapped-TextBlock measures faster than the UI thread can
        // process them. The original double-TryEnqueue + RequestResize per chunk
        // was the worst offender — full content.Measure() per snapshot. Window
        // resize now happens once in the final-state lambda below.
        await foreach (var chunk in manager.TranslateStreamAsync(
            request, ct, serviceResult.ServiceId).ConfigureAwait(false))
        {
            sb.Append(chunk);

            var now = DateTime.UtcNow;
            if ((now - lastUpdateTime).TotalMilliseconds >= throttleMs)
            {
                _streamingCoalescer?.Update(serviceResult, sb.ToString());
                lastUpdateTime = now;
            }
        }

        stopwatch.Stop();

        // Final update with complete result
        var finalText = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(finalText))
        {
            throw new TranslationException("Streaming service returned an empty response")
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
                ServiceId = serviceResult.ServiceId
            };
        }

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
            // Drop any pending streaming snapshot before flipping IsStreaming → false.
            _streamingCoalescer?.Forget(serviceResult);
            serviceResult.IsLoading = false;
            serviceResult.IsStreaming = false;
            serviceResult.StreamingText = "";
            serviceResult.Result = result;
            serviceResult.ApplyAutoCollapseLogic();
            UpdatePhoneticDeduplication();
            ReorderResultsPanel();
            // Delay resize to next tick so ServiceResultItem.UpdateUI() completes first
            DispatcherQueue.TryEnqueue(() => RequestResize());
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

        if (MinimalThemeService.IsActive)
        {
            DetectedLangText.Text = "";
            DetectedLangText.Visibility = Visibility.Collapsed;
            return;
        }

        if (detected != TranslationLanguage.Auto)
        {
            var displayName = detected.GetDisplayName();
            DetectedLangText.Text = string.Format(
                LocalizationService.Instance.GetString("DetectedLanguage"),
                displayName);
            DetectedLangText.Visibility = IsCompactChrome ? Visibility.Collapsed : Visibility.Visible;
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

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    private void OnOcrClicked(object sender, RoutedEventArgs e)
    {
        _ = App.TriggerOcrTranslateAsync();
    }

    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        _settings.FixedWindowIsPinned = _isPinned;
        _settings.Save();
        UpdatePinState();
    }

    private void UpdatePinState()
    {
        _isPinned = _settings.FixedWindowIsPinned;
        if (_presenter != null)
        {
            _presenter.IsAlwaysOnTop = _isPinned;
        }

        PinButton.IsChecked = _isPinned;
        PinIcon.Glyph = _isPinned ? "\uE840" : "\uE718"; // Pinned vs Unpinned icon
    }

    /// <summary>
    /// Re-apply appearance settings (result font size + quick-action button visibility)
    /// to the fixed window. Called from the app-wide appearance broadcast (issue #172).
    /// </summary>
    public void ApplyAppearance()
    {
        ApplyButtonVisibility();
        ApplyCompactChromeLayout();
        ServiceResultViewHost.RefreshAppearance(_resultControls);
        RequestResize();
    }

    private bool IsCompactChrome => MinimalThemeService.IsActive || SettingsService.Instance.CompactMode;

    private bool ShouldShowCompactWindowControls => SettingsService.Instance.CompactMode;

    private void ApplyCompactChromeLayout()
    {
        var compact = IsCompactChrome;
        var showCompactControls = ShouldShowCompactWindowControls;
        TitleBarRegion.Visibility = showCompactControls ? Visibility.Collapsed : Visibility.Visible;
        TitleBarRegion.Margin = showCompactControls ? new Thickness(0) : new Thickness(0, 0, 0, 4);
        if (_compactWindowControls is { } compactControls)
        {
            compactControls.Root.Visibility = showCompactControls ? Visibility.Visible : Visibility.Collapsed;
            compactControls.Root.Opacity = showCompactControls ? CompactWindowControlsIdleOpacity : 0;
            compactControls.RefreshTheme(Content as FrameworkElement);
        }
        TitleText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (compact)
        {
            DetectedLangText.Visibility = Visibility.Collapsed;
        }
        else
        {
            RefreshDetectedLanguageChrome();
        }
        StatusText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => _titleBarHelper?.UpdateDragRegions());
    }

    private void RefreshDetectedLanguageChrome()
    {
        if (_lastQuickQueryResolution is { } resolution)
        {
            UpdateQuickQueryModeStatus(resolution);
            return;
        }

        UpdateDetectedLanguageDisplay(_lastDetectedLanguage);
    }

    private void OnCompactWindowControlsPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ShouldShowCompactWindowControls)
        {
            if (_compactWindowControls is { } compactControls)
            {
                compactControls.Root.Opacity = 1;
            }
        }
    }

    private void OnCompactWindowControlsPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ShouldShowCompactWindowControls)
        {
            if (_compactWindowControls is { } compactControls)
            {
                compactControls.Root.Opacity = CompactWindowControlsIdleOpacity;
            }
        }
    }

    private void OnCompactDragIslandPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ShouldShowCompactWindowControls
            || _compactWindowDragSession is not null
            || sender is not UIElement dragElement
            || !WindowDragHelper.TryBeginLeftButtonDrag(this, dragElement, e, out var session))
        {
            return;
        }

        _compactWindowDragSession = session;
        e.Handled = true;
    }

    private void OnCompactDragIslandPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_compactWindowDragSession is not { } session
            || sender is not UIElement dragElement
            || !WindowDragHelper.IsSessionPointer(session, e))
        {
            return;
        }

        if (WindowDragHelper.TryUpdateLeftButtonDrag(session, dragElement, e))
        {
            e.Handled = true;
            return;
        }

        EndCompactWindowDrag(dragElement, e, releaseCapture: true);
    }

    private void OnCompactDragIslandPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement dragElement)
        {
            EndCompactWindowDrag(dragElement, e, releaseCapture: true);
        }
    }

    private void OnCompactDragIslandPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement dragElement)
        {
            EndCompactWindowDrag(dragElement, e, releaseCapture: true);
        }
    }

    private void OnCompactDragIslandPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement dragElement)
        {
            EndCompactWindowDrag(dragElement, e, releaseCapture: false);
        }
    }

    private void EndCompactWindowDrag(UIElement dragElement, PointerRoutedEventArgs e, bool releaseCapture)
    {
        if (_compactWindowDragSession is not { } session
            || !WindowDragHelper.IsSessionPointer(session, e))
        {
            return;
        }

        _compactWindowDragSession = null;
        if (releaseCapture)
        {
            dragElement.ReleasePointerCapture(e.Pointer);
        }

        e.Handled = true;
    }

    private void OnCompactCloseClicked(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    /// <summary>
    /// Show/hide quick-action buttons per user settings.
    /// </summary>
    private void ApplyButtonVisibility()
    {
        var settings = SettingsService.Instance;
        var compact = IsCompactChrome;
        PinButton.Visibility = !compact && settings.ShowPinButton ? Visibility.Visible : Visibility.Collapsed;
        OcrButton.Visibility = !compact && settings.ShowOcrButton ? Visibility.Visible : Visibility.Collapsed;
        SwapButton.Visibility = !compact && settings.ShowSwapButton ? Visibility.Visible : Visibility.Collapsed;
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
        // PageUp/PageDown/Up/Down scroll the results (issue #137) while the
        // input TextBox keeps keyboard focus so typing continues uninterrupted.
        if (TryScrollResults(e.Key))
        {
            e.Handled = true;
            return;
        }

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
            return; // Allow newline
        }

        e.Handled = true;
        await StartQueryTrackedAsync();
    }

    private void OnSwapClicked(object sender, RoutedEventArgs e)
    {
        var sourceLanguage = GetSourceLanguage();
        var currentTarget = GetTargetLanguage();

        if (sourceLanguage == currentTarget)
        {
            return;
        }

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

        RebuildTargetCombo(newTarget);
        _targetLanguageSelector.MarkManualSelection();
        RefreshQuickQueryModePreview();

        if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            _ = StartQueryTrackedAsync();
        }
    }

    /// <summary>
    /// Handle source language selection change.
    /// </summary>
    private void OnSourceLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _suppressSourceLanguageSelectionChanged)
            return;

        var currentTarget = GetTargetLanguage();
        RebuildTargetCombo(currentTarget);
        RefreshQuickQueryModePreview();

        if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            _ = StartQueryTrackedAsync();
        }
    }

    private void RebuildTargetCombo(TranslationLanguage currentTarget)
    {
        var loc = LocalizationService.Instance;
        _suppressTargetLanguageSelectionChanged = true;
        try
        {
            LanguageComboHelper.RebuildTargetCombo(
                TargetLangCombo, currentTarget, loc, out var newTarget);
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
        RefreshQuickQueryModePreview();
        if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            _ = StartQueryTrackedAsync();
        }
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
        _appWindow?.Show();

        // Try to bring window to front using multiple methods
        try
        {
            _appWindow?.MoveInZOrderAtTop();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"FixedWindow: MoveInZOrderAtTop failed: {ex.Message}");
        }

        // Use Win32 SetForegroundWindow to forcefully bring window to front
        var foregroundSet = ForegroundWindowHelper.TryBringToFront(this, "FixedWindow");
        if (!foregroundSet)
        {
            System.Diagnostics.Debug.WriteLine("FixedWindow: SetForegroundWindow failed; relying on Activate()");
        }

        this.Activate();
        QueueInputFocusAndSelectAll();

        // Resize window to fit existing content
        RequestResize();
    }

    /// <summary>
    /// Scroll the results ScrollViewer programmatically. Lets the user press
    /// PageUp/PageDown/Up/Down on the input TextBox to navigate results (issue
    /// #137) without losing the ability to keep typing into the same TextBox.
    /// Returns true when the key was consumed for scrolling.
    /// </summary>
    private bool TryScrollResults(VirtualKey key)
    {
        if (!ResultsInputRouter.IsScrollNavigationKey(key)) return false;
        if (ResultsScrollViewer?.XamlRoot is null) return false;

        var viewport = ResultsScrollViewer.ViewportHeight;
        const double lineHeight = 48;

        double delta = key switch
        {
            VirtualKey.PageDown => viewport,
            VirtualKey.PageUp => -viewport,
            VirtualKey.Down => lineHeight,
            VirtualKey.Up => -lineHeight,
            _ => 0
        };
        if (delta == 0) return false;

        var newOffset = Math.Clamp(
            ResultsScrollViewer.VerticalOffset + delta,
            0,
            ResultsScrollViewer.ScrollableHeight);
        ResultsScrollViewer.ChangeView(null, newOffset, null);
        return true;
    }

    private void QueueInputFocusAndSelectAll(int attemptsRemaining = InputFocusMaxAttempts)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var attempt = InputFocusMaxAttempts - attemptsRemaining + 1;

            if (_isClosing)
            {
                System.Diagnostics.Debug.WriteLine($"[FixedWindow] QueueInputFocusAndSelectAll attempt {attempt}/{InputFocusMaxAttempts}: aborted because window is closing");
                return;
            }

            if (!_isLoaded || InputTextBox.XamlRoot is null || !InputTextBox.IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FixedWindow] QueueInputFocusAndSelectAll attempt {attempt}/{InputFocusMaxAttempts}: " +
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
                System.Diagnostics.Debug.WriteLine(
                    $"[FixedWindow] QueueInputFocusAndSelectAll attempt {attempt}/{InputFocusMaxAttempts}: " +
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
            System.Diagnostics.Debug.WriteLine(
                $"[FixedWindow] QueueInputFocusAndSelectAll attempt {attempt}/{InputFocusMaxAttempts}: " +
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

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[FixedWindow] Activated: state={args.WindowActivationState}, loaded={_isLoaded}");
        QueueInputFocusAndSelectAll();
    }

    /// <summary>
    /// Hide the window.
    /// </summary>
    public void HideWindow()
    {
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
    public void ApplyTheme(ElementTheme theme, bool forceResourceRefresh = false)
    {
        if (this.Content is FrameworkElement root)
        {
            MinimalThemeService.ApplyRequestedTheme(root, theme, forceResourceRefresh);
            MinimalThemeService.ApplyFloatingWindowRootBackground(root);
        }

        var minimal = MinimalThemeService.IsActive;
        MinimalThemeService.ApplyFloatingChrome(
            Content as Grid,
            WindowSurface,
            SourceTextContainer,
            minimal,
            Content as FrameworkElement);
        ApplyButtonVisibility();
        ApplyCompactChromeLayout();
        var themeRoot = Content as FrameworkElement;
        MinimalThemeService.ApplyAccentIconForeground(TranslateIcon, LoadingRing, themeRoot);

        if (ServiceResultViewHost.NeedsThemeRebuild(_resultControls, minimal))
        {
            RebuildServiceResultControlsForCurrentTheme();
        }
        else
        {
            ServiceResultViewHost.RefreshThemeChrome(_resultControls, themeRoot);
        }

        if (minimal)
        {
            MinimalThemeService.ApplyWindowBackdrop(this);
        }
        else
        {
            TryApplyMicaBackdrop();
        }
    }

    /// <summary>
    /// Reorder <see cref="ResultsPanel"/> so that rows demoted by
    /// <see cref="ServiceResultDemotionHelper.IsDemoted"/> (no-result + hide-empty setting)
    /// appear at the bottom of the list while preserving the configured order within each
    /// bucket. Idempotent: safe to call on every result completion.
    /// </summary>
    private void ReorderResultsPanel()
    {
        ServiceResultViewHost.Reorder(
            _serviceResults,
            _resultControls,
            ResultsPanel,
            SettingsService.Instance.HideEmptyServiceResults,
            pinGrammarCapable: _currentMode == QueryMode.GrammarCorrection);
    }

    private void OnHideEmptyServiceResultsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            foreach (var control in _resultControls)
            {
                control.RefreshDemotionState();
            }
            ReorderResultsPanel();
            RequestResize();
        });
    }

    /// <summary>
    /// Updates phonetic deduplication across all service result controls.
    /// The first service showing a phonetic displays it; subsequent services with
    /// the same phonetic will have it hidden to avoid duplication.
    /// </summary>
    private void UpdatePhoneticDeduplication()
    {
        ServiceResultViewHost.UpdatePhoneticDeduplication(_resultControls);
    }
}
