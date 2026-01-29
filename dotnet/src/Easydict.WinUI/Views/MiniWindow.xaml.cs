using System.Runtime.InteropServices;
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
/// Compact mini window for quick translations.
/// Features: always-on-top when pinned, auto-close on focus loss, compact UI.
/// </summary>
public sealed partial class MiniWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
    private bool _suppressTargetLanguageSelectionChanged;
    private TitleBarDragRegionHelper? _titleBarHelper;
    private DateTime _lastShowTime = DateTime.MinValue;
    private bool _resizePending;

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
        this.Title = $"Easydict {loc.GetString("QuickTranslate")}";

        // Source Language ComboBox items
        if (SourceLangCombo.Items.Count >= 4)
        {
            ((ComboBoxItem)SourceLangCombo.Items[0]).Content = loc.GetString("Auto");
            ((ComboBoxItem)SourceLangCombo.Items[1]).Content = loc.GetString("LangEnglish");
            ((ComboBoxItem)SourceLangCombo.Items[2]).Content = loc.GetString("LangChinese");
            ((ComboBoxItem)SourceLangCombo.Items[3]).Content = loc.GetString("LangJapanese");
        }

        // Target Language ComboBox items
        if (TargetLangCombo.Items.Count >= 7)
        {
            ((ComboBoxItem)TargetLangCombo.Items[0]).Content = loc.GetString("LangEnglish");
            ((ComboBoxItem)TargetLangCombo.Items[1]).Content = loc.GetString("LangChinese");
            ((ComboBoxItem)TargetLangCombo.Items[2]).Content = loc.GetString("LangJapanese");
            ((ComboBoxItem)TargetLangCombo.Items[3]).Content = loc.GetString("LangKorean");
            ((ComboBoxItem)TargetLangCombo.Items[4]).Content = loc.GetString("LangFrench");
            ((ComboBoxItem)TargetLangCombo.Items[5]).Content = loc.GetString("LangGerman");
            ((ComboBoxItem)TargetLangCombo.Items[6]).Content = loc.GetString("LangSpanish");
        }

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
            _ => 1
        };
        _suppressTargetLanguageSelectionChanged = true;
        try
        {
            if (targetIndex >= 0 && targetIndex < TargetLangCombo.Items.Count)
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

    /// <summary>
    /// Handle window activation changes for auto-close behavior.
    /// </summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
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
            System.Diagnostics.Debug.WriteLine($"[MiniWindow] ResizeWindowToContent error: {ex.Message}");
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

            // Detect language
            var detectedLanguage = await detectionService.DetectAsync(inputText, ct);

            _lastDetectedLanguage = detectedLanguage;
            UpdateDetectedLanguageDisplay(detectedLanguage);

            // Determine target language
            var autoTarget = _targetLanguageSelector.ResolveTargetLanguage(
                detectedLanguage, detectionService);
            TranslationLanguage targetLanguage;
            if (autoTarget.HasValue)
            {
                targetLanguage = autoTarget.Value;
                UpdateTargetLanguageSelector(targetLanguage);
            }
            else
            {
                targetLanguage = GetTargetLanguage();
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
            // Delay resize to next tick so ServiceResultItem.UpdateUI() completes first
            DispatcherQueue.TryEnqueue(() => ResizeWindowToContent());
        });
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

    private void UpdateDetectedLanguageDisplay(TranslationLanguage detected)
    {
        if (!_isLoaded) return;

        if (detected != TranslationLanguage.Auto)
        {
            var displayName = detected.GetDisplayName();
            DetectedLangText.Text = string.Format(
                LocalizationService.Instance.GetString("DetectedLanguage"),
                displayName);
        }
        else
        {
            DetectedLangText.Text = "";
        }
    }

    private void UpdateTargetLanguageSelector(TranslationLanguage targetLang)
    {
        if (!_isLoaded) return;

        var targetIndex = targetLang switch
        {
            TranslationLanguage.English => 0,
            TranslationLanguage.SimplifiedChinese => 1,
            TranslationLanguage.Japanese => 2,
            TranslationLanguage.Korean => 3,
            TranslationLanguage.French => 4,
            TranslationLanguage.German => 5,
            TranslationLanguage.Spanish => 6,
            _ => 1
        };

        _suppressTargetLanguageSelectionChanged = true;
        try
        {
            if (targetIndex >= 0 && targetIndex < TargetLangCombo.Items.Count)
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
        if (_lastDetectedLanguage == TranslationLanguage.Auto)
        {
            return;
        }

        UpdateTargetLanguageSelector(_lastDetectedLanguage);
        _targetLanguageSelector.MarkManualSelection(); // Swap is a manual language choice
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
        _appWindow?.MoveInZOrderAtTop();

        // Use Win32 SetForegroundWindow to forcefully bring window to front
        var hWnd = WindowNative.GetWindowHandle(this);
        var foregroundSet = SetForegroundWindow(hWnd);
        if (!foregroundSet)
        {
            System.Diagnostics.Debug.WriteLine("MiniWindow: SetForegroundWindow failed; relying on Activate()");
        }

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
