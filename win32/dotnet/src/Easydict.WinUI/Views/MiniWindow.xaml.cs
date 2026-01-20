using System.Collections.ObjectModel;
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
    private TranslationManager? _translationManager;
    private LanguageDetectionService? _detectionService;
    private CancellationTokenSource? _currentQueryCts;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly List<ServiceQueryResult> _serviceResults = new();
    private readonly List<ServiceResultItem> _resultControls = new();
    private TranslationLanguage _lastDetectedLanguage = TranslationLanguage.Auto;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;
    private bool _isPinned;
    private bool _isClosing;
    private bool _isLoaded;
    private bool _userChangedTargetLanguage;
    private bool _suppressTargetLanguageSelectionChanged;

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
            content.Loaded += (s, e) => _isLoaded = true;
        }
    }

    /// <summary>
    /// Configure window to be compact with no title bar buttons.
    /// </summary>
    private void ConfigureWindow()
    {
        if (_appWindow == null) return;

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
            _translationManager = new TranslationManager();
            _translationManager.DefaultServiceId = "google";
            _detectionService = new LanguageDetectionService(_translationManager, _settings);
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

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        SaveWindowPosition();
        CleanupResources();
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

    private void CleanupResources()
    {
        _currentQueryCts?.Cancel();
        _currentQueryCts?.Dispose();
        _detectionService?.Dispose();
        _translationManager?.Dispose();
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
        TranslateButton.IsEnabled = !loading;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        TranslateIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task StartQueryAsync()
    {
        if (_translationManager is null || _detectionService is null)
        {
            StatusText.Text = "Service not initialized";
            InitializeTranslationServices();
            return;
        }

        var inputText = InputTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(inputText))
        {
            return;
        }

        _currentQueryCts?.Cancel();
        _currentQueryCts?.Dispose();
        _currentQueryCts = new CancellationTokenSource();
        var ct = _currentQueryCts.Token;

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
            var detectedLanguage = await _detectionService.DetectAsync(inputText, ct);

            _lastDetectedLanguage = detectedLanguage;
            UpdateDetectedLanguageDisplay(detectedLanguage);

            // Determine target language
            TranslationLanguage targetLanguage;
            if (_settings.AutoSelectTargetLanguage && !_userChangedTargetLanguage)
            {
                targetLanguage = _detectionService.GetTargetLanguage(detectedLanguage);
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
                    var result = await _translationManager.TranslateAsync(
                        request, ct, serviceResult.ServiceId);

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        serviceResult.Result = result;
                        serviceResult.IsLoading = false;
                        serviceResult.ApplyAutoCollapseLogic();
                        ResizeWindowToContent();
                    });
                }
                catch (OperationCanceledException)
                {
                    // Cancelled, ignore
                }
                catch (TranslationException ex)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        serviceResult.Error = ex;
                        serviceResult.IsLoading = false;
                        serviceResult.ApplyAutoCollapseLogic();
                        ResizeWindowToContent();
                    });
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        serviceResult.Error = new TranslationException(ex.Message)
                        {
                            ErrorCode = TranslationErrorCode.Unknown,
                            ServiceId = serviceResult.ServiceId
                        };
                        serviceResult.IsLoading = false;
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
            SetLoading(false);
        }
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
        await StartQueryAsync();
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
        await StartQueryAsync();
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
        _ = StartQueryAsync();
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
}
