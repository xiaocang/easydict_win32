using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Views.Controls;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using TranslationLanguage = Easydict.TranslationService.Models.Language;

namespace Easydict.WinUI.Views;

/// <summary>
/// Fixed translation window that stays visible and on top.
/// Unlike Mini Window, it does not auto-close on focus loss and is always on top.
/// </summary>
public sealed partial class FixedWindow : Window
{
    private LanguageDetectionService? _detectionService;
    // Owned by StartQueryAsync() - only that method creates and disposes via its finally block.
    // Other code may Cancel() but must NOT Dispose().
    private CancellationTokenSource? _currentQueryCts;
    private Task? _currentQueryTask;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly List<ServiceQueryResult> _serviceResults = new();
    private readonly List<ServiceResultItem> _resultControls = new();
    private TranslationLanguage _lastDetectedLanguage = TranslationLanguage.Auto;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;
    private bool _isLoaded;
    private volatile bool _isClosing;
    private readonly TargetLanguageSelector _targetLanguageSelector;
    private bool _suppressTargetLanguageSelectionChanged;
    private bool _suppressSourceLanguageSelectionChanged;
    private TitleBarDragRegionHelper? _titleBarHelper;
    private bool _resizePending;

    /// <summary>
    /// Maximum time to wait for in-flight query to complete during cleanup.
    /// </summary>
    private const int QueryShutdownTimeoutSeconds = 2;

    public FixedWindow()
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
        this.Closed += OnWindowClosed;

        // Apply settings
        ApplySettings();

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
                // Apply localization after content is loaded
                ApplyLocalization();
            };
        }

        // Set up title bar drag regions for unpackaged WinUI 3 apps
        if (_appWindow != null)
        {
            _titleBarHelper = new TitleBarDragRegionHelper(
                this,
                _appWindow,
                TitleBarRegion,
                new FrameworkElement[] { CloseButton },
                "FixedWindow");
            _titleBarHelper.Initialize();
        }
    }

    /// <summary>
    /// Apply localization to all UI elements using LocalizationService.
    /// </summary>
    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;

        // Window title - keep "Easydict" brand name, only localize "Fixed"
        this.Title = $"Easydict ᵇᵉᵗᵃ {loc.GetString("FixedTranslate")}";

        // Source Language ComboBox items - 9 items: Auto + 8 languages
        if (SourceLangCombo.Items.Count >= 9)
        {
            ((ComboBoxItem)SourceLangCombo.Items[0]).Content = loc.GetString("Auto");
            for (int i = 0; i < LanguageComboHelper.SelectableLanguages.Length; i++)
            {
                ((ComboBoxItem)SourceLangCombo.Items[i + 1]).Content =
                    loc.GetString(LanguageComboHelper.SelectableLanguages[i].LocalizationKey);
            }
        }

        // Target Language ComboBox items - 8 items (dynamically rebuilt)
        for (int i = 0; i < TargetLangCombo.Items.Count && i < LanguageComboHelper.SelectableLanguages.Length; i++)
        {
            ((ComboBoxItem)TargetLangCombo.Items[i]).Content =
                loc.GetString(LanguageComboHelper.SelectableLanguages[i].LocalizationKey);
        }

        // Placeholders
        InputTextBox.PlaceholderText = loc.GetString("InputPlaceholder");

        // Tooltips
        ToolTipService.SetToolTip(CloseButton, loc.GetString("HideWindow"));
        ToolTipService.SetToolTip(SourceLangCombo, loc.GetString("SourceLanguageTooltip"));
        ToolTipService.SetToolTip(SwapButton, loc.GetString("SwapLanguagesTooltip"));
        ToolTipService.SetToolTip(TargetLangCombo, loc.GetString("TargetLanguageTooltip"));
        ToolTipService.SetToolTip(TranslateButton, loc.GetString("TranslateTooltip"));
    }

    /// <summary>
    /// Configure window to be compact with no title bar buttons, always on top.
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
            _presenter.IsAlwaysOnTop = true;  // Fixed window is always on top
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
    }

    /// <summary>
    /// Position window based on saved position or center on screen.
    /// </summary>
    private void PositionWindow()
    {
        if (_appWindow == null) return;

        var hWnd = WindowNative.GetWindowHandle(this);
        var scale = DpiHelper.GetScaleFactorForWindow(hWnd);

        // Check if we have a saved position
        if (_settings.FixedWindowXDips > 0 || _settings.FixedWindowYDips > 0)
        {
            var x = DpiHelper.DipsToPhysicalPixels(_settings.FixedWindowXDips, scale);
            var y = DpiHelper.DipsToPhysicalPixels(_settings.FixedWindowYDips, scale);
            _appWindow.Move(new PointInt32((int)x, (int)y));
        }
        else
        {
            // Center on primary display
            var displayArea = DisplayArea.Primary;
            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                var windowSize = _appWindow.Size;
                var x = (workArea.Width - windowSize.Width) / 2 + workArea.X;
                var y = (workArea.Height - windowSize.Height) / 2 + workArea.Y;
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

        _settings.FixedWindowXDips = DpiHelper.PhysicalPixelsToDips(position.X, scale);
        _settings.FixedWindowYDips = DpiHelper.PhysicalPixelsToDips(position.Y, scale);
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
        _serviceResults.Clear();
        _resultControls.Clear();
        ResultsPanel.Items.Clear();

        // Get enabled services and EnabledQuery settings from settings
        var enabledServices = _settings.FixedWindowEnabledServices;
        var enabledQuerySettings = _settings.FixedWindowServiceEnabledQuery;

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
        DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
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
                RequestResize();
            }
        }
        catch (TranslationException ex)
        {
            serviceResult.Error = ex;
            serviceResult.IsLoading = false;
            serviceResult.IsStreaming = false;
            serviceResult.ApplyAutoCollapseLogic();
            DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
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
            DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
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
        DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
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

            // Force layout update before measuring
            content.UpdateLayout();

            // Get current window width in DIPs for proper measurement
            var currentSize = _appWindow.Size;
            var currentWidthDips = DpiHelper.PhysicalPixelsToDips(currentSize.Width, scale);

            // Measure desired size with actual width constraint (critical for text wrapping)
            content.Measure(new Windows.Foundation.Size(currentWidthDips, double.PositiveInfinity));
            var desiredHeight = content.DesiredSize.Height;

            // Calculate new height with limits (200-800 DIPs)
            var minHeight = DpiHelper.DipsToPhysicalPixels(200, scale);
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
    /// Request a coalesced resize to prevent multiple queued resize calls.
    /// </summary>
    private void RequestResize()
    {
        if (_resizePending) return;
        _resizePending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _resizePending = false;
            ResizeWindowToContent();
        }))
        {
            // If dispatcher is shutting down, allow future resize attempts
            _resizePending = false;
        }
    }

    private async Task CleanupResourcesAsync()
    {
        // Clean up title bar drag region helper
        _titleBarHelper?.Dispose();
        _titleBarHelper = null;

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

    private void SetLoading(bool loading)
    {
        if (_isClosing) return;

        TranslateButton.IsEnabled = !loading;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        TranslateIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
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

            // Detect language (only when source = Auto)
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
                        var result = await manager.TranslateAsync(
                            request, ct, serviceResult.ServiceId);

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_isClosing) return;
                            serviceResult.Result = result;
                            serviceResult.IsLoading = false;
                            serviceResult.ApplyAutoCollapseLogic();
                            // Coalesced resize so ServiceResultItem.UpdateUI() completes first
                            RequestResize();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelled, ignore
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
                        // Delay resize to next tick so ServiceResultItem.UpdateUI() completes first
                        DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
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
                        // Delay resize to next tick so ServiceResultItem.UpdateUI() completes first
                        DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
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
            // Cancelled, ignore
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{LocalizationService.Instance.GetString("StatusError")}: {ex.Message}";
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
                    // Delay resize to next tick so ServiceResultItem.UpdateUI() completes first
                    DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
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
            // Delay resize to next tick so ServiceResultItem.UpdateUI() completes first
            DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
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

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    private async void OnTranslateClicked(object sender, RoutedEventArgs e)
    {
        await StartQueryTrackedAsync();
    }

    private async void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        try
        {
            if (this.Content is FrameworkElement fe && fe.XamlRoot?.ContentIsland != null)
            {
                var keyboardSource = InputKeyboardSource.GetForIsland(fe.XamlRoot.ContentIsland);
                var shiftState = keyboardSource.GetKeyState(VirtualKey.Shift);
                var ctrlState = keyboardSource.GetKeyState(VirtualKey.Control);

                if (shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
                    ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                {
                    return; // Allow newline
                }
            }
        }
        catch
        {
            // Fallback: trigger translation
        }

        e.Handled = true;
        await StartQueryTrackedAsync();
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
        _appWindow?.Show();
        this.Activate();
        InputTextBox.Focus(FocusState.Programmatic);

        // Resize window to fit existing content (delayed to allow layout to complete)
        DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
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
    /// Refresh service result controls when settings change.
    /// </summary>
    public void RefreshServiceResults()
    {
        InitializeServiceResults();
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
}
