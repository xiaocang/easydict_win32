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
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
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
    private bool _isPinned;
    private volatile bool _isClosing;
    private bool _isLoaded;
    private bool _userChangedTargetLanguage;
    private bool _suppressTargetLanguageSelectionChanged;
    private FrameworkElement? _contentElement;

    /// <summary>
    /// Maximum time to wait for in-flight query to complete during cleanup.
    /// </summary>
    private const int QueryShutdownTimeoutSeconds = 2;

    public MiniWindow()
    {
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
            _contentElement = content;
            content.Loaded += OnContentLoaded;
            content.SizeChanged += OnContentSizeChanged;
        }
    }

    private void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        // Set up passthrough regions for interactive controls in title bar
        UpdateTitleBarDragRegions();
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Recalculate drag regions when window size changes
        if (_isLoaded) UpdateTitleBarDragRegions();
    }

    /// <summary>
    /// Sets up drag and passthrough regions for the custom title bar.
    /// In unpackaged WinUI 3 apps, SetTitleBar() doesn't work reliably.
    /// Instead, we use InputNonClientPointerSource to define:
    /// - Caption regions: areas that act as draggable title bar
    /// - Passthrough regions: areas that receive pointer input (buttons, etc.)
    /// </summary>
    private void UpdateTitleBarDragRegions()
    {
        if (_appWindow == null || !_isLoaded) return;

        try
        {
            var nonClientInputSrc = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            var scale = DpiHelper.GetScaleFactorForWindow(WindowNative.GetWindowHandle(this));

            // Set the TitleBarRegion grid as the Caption (draggable) area
            if (TitleBarRegion.ActualWidth > 0 && TitleBarRegion.ActualHeight > 0)
            {
                var captionRect = GetScaledBoundsForElement(TitleBarRegion, scale);
                if (captionRect.Width > 0 && captionRect.Height > 0)
                {
                    nonClientInputSrc.SetRegionRects(NonClientRegionKind.Caption, new[] { captionRect });
                }
            }

            // Collect interactive controls that need passthrough (buttons in title bar)
            var passthroughRects = new List<RectInt32>();

            // PinButton
            if (PinButton.ActualWidth > 0 && PinButton.ActualHeight > 0)
            {
                var rect = GetScaledBoundsForElement(PinButton, scale);
                if (rect.Width > 0 && rect.Height > 0)
                    passthroughRects.Add(rect);
            }

            // CloseButton
            if (CloseButton.ActualWidth > 0 && CloseButton.ActualHeight > 0)
            {
                var rect = GetScaledBoundsForElement(CloseButton, scale);
                if (rect.Width > 0 && rect.Height > 0)
                    passthroughRects.Add(rect);
            }

            // Set the passthrough regions - these areas will be clickable instead of draggable
            if (passthroughRects.Count > 0)
            {
                nonClientInputSrc.SetRegionRects(NonClientRegionKind.Passthrough, passthroughRects.ToArray());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniWindow] UpdateTitleBarDragRegions error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the bounds of a UIElement in physical pixels, scaled for DPI.
    /// </summary>
    private RectInt32 GetScaledBoundsForElement(FrameworkElement element, double scale)
    {
        try
        {
            GeneralTransform transform = element.TransformToVisual(null);
            Rect bounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            return new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)
            );
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniWindow] GetScaledBoundsForElement error: {ex.Message}");
            return default;
        }
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
    /// Position window based on saved position or center on screen.
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

        _settings.MiniWindowXDips = DpiHelper.PhysicalPixelsToDips(position.X, scale);
        _settings.MiniWindowYDips = DpiHelper.PhysicalPixelsToDips(position.Y, scale);
        _settings.MiniWindowIsPinned = _isPinned;
        _settings.Save();
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
            _ => 1
        };
        _suppressTargetLanguageSelectionChanged = true;
        try
        {
            if (targetIndex >= 0 && targetIndex < TargetLangCombo.Items.Count)
            {
                TargetLangCombo.SelectedIndex = targetIndex;
            }
            _userChangedTargetLanguage = false;
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
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiniWindow] Init error: {ex.Message}");
            StatusText.Text = "Error";
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

        // Get enabled services from settings
        var enabledServices = _settings.MiniWindowEnabledServices;

        // Service display names mapping
        var serviceNames = new Dictionary<string, string>
        {
            ["google"] = "Google Translate",
            ["deepl"] = "DeepL",
            ["bing"] = "Microsoft Bing",
            ["apple"] = "Apple Translate",
            ["baidu"] = "Baidu",
            ["youdao"] = "Youdao",
            ["openai"] = "OpenAI",
            ["ollama"] = "Ollama",
            ["builtin"] = "Built-in AI",
            ["deepseek"] = "DeepSeek",
            ["groq"] = "Groq",
            ["zhipu"] = "Zhipu (智谱)",
            ["github"] = "GitHub Models",
            ["custom-openai"] = "Custom OpenAI",
            ["gemini"] = "Gemini"
        };

        foreach (var serviceId in enabledServices)
        {
            var displayName = serviceNames.TryGetValue(serviceId, out var name)
                ? name
                : serviceId;

            var result = new ServiceQueryResult
            {
                ServiceId = serviceId,
                ServiceDisplayName = displayName
            };

            var control = new ServiceResultItem
            {
                ServiceResult = result
            };
            control.CollapseToggled += OnServiceCollapseToggled;

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
    /// Handle window activation changes for auto-close behavior.
    /// </summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Window lost focus
            if (!_isPinned && _settings.MiniWindowAutoClose && !_isClosing)
            {
                // Auto-close when not pinned
                HideWindow();
            }
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
            // Measure desired size
            content.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var desiredHeight = content.DesiredSize.Height;

            // Get DPI scale
            var hWnd = WindowNative.GetWindowHandle(this);
            var scale = DpiHelper.GetScaleFactorForWindow(hWnd);

            // Calculate new height with limits (200-500 DIPs)
            var minHeight = DpiHelper.DipsToPhysicalPixels(200, scale);
            var maxHeight = DpiHelper.DipsToPhysicalPixels(500, scale);
            var newHeight = DpiHelper.DipsToPhysicalPixels(desiredHeight + 16, scale); // +16 for padding
            newHeight = Math.Clamp(newHeight, minHeight, maxHeight);

            // Resize window (avoid micro-resizes)
            var currentSize = _appWindow.Size;
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

    private async Task CleanupResourcesAsync()
    {
        // Unregister event handlers to prevent memory leaks
        if (_contentElement != null)
        {
            _contentElement.Loaded -= OnContentLoaded;
            _contentElement.SizeChanged -= OnContentSizeChanged;
            _contentElement = null;
        }

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
            StatusText.Text = "Service not initialized";
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
                result.IsLoading = true;
            }

            // Detect language
            var detectedLanguage = await detectionService.DetectAsync(inputText, ct);

            _lastDetectedLanguage = detectedLanguage;
            UpdateDetectedLanguageDisplay(detectedLanguage);

            // Determine target language
            TranslationLanguage targetLanguage;
            if (_settings.AutoSelectTargetLanguage && !_userChangedTargetLanguage)
            {
                targetLanguage = detectionService.GetTargetLanguage(detectedLanguage);
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
            var tasks = _serviceResults.Select(async serviceResult =>
            {
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
                            ResizeWindowToContent();
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
                        ResizeWindowToContent();
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
                        ResizeWindowToContent();
                    });
                }
            });

            await Task.WhenAll(tasks);

            // Update status with completed count
            var successCount = _serviceResults.Count(r => r.Result != null);
            var errorCount = _serviceResults.Count(r => r.HasError);
            StatusText.Text = successCount > 0
                ? $"{successCount} service(s) completed"
                : errorCount > 0 ? "Translation failed" : "";
        }
        catch (OperationCanceledException)
        {
            // Cancelled, ignore
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
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
                    ResizeWindowToContent();
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
            ResizeWindowToContent();
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
            DetectedLangText.Text = $"Detected: {detected.GetDisplayName()}";
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
            _userChangedTargetLanguage = false;

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
    }

    private void OnTargetLangChanged(object sender, SelectionChangedEventArgs e)
    {
        // User manually changed target language - respect their choice
        if (_suppressTargetLanguageSelectionChanged)
        {
            return;
        }

        _userChangedTargetLanguage = true;
    }

    /// <summary>
    /// Set text and start translation (called from external sources).
    /// </summary>
    public void SetTextAndTranslate(string text)
    {
        _userChangedTargetLanguage = false; // Reset for new external input
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
