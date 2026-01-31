using Easydict.WinUI.Services;
using Easydict.WinUI.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using WinRT.Interop;

namespace Easydict.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private TrayIconService? _trayIconService;
        private HotkeyService? _hotkeyService;
        private ClipboardService? _clipboardService;
        private AppWindow? _appWindow;

        private static App Instance => (App)Current;

        /// <summary>
        /// Gets the main window instance.
        /// </summary>
        public static Window? MainWindow => Instance._window;

        /// <summary>
        /// Event fired when clipboard text is received (for auto-translate).
        /// </summary>
        public static event Action<string>? ClipboardTextReceived;

        public App()
        {
#if DEBUG
            // Log FIRST THING to see if constructor runs
            try
            {
                var tempLog = Path.Combine(Path.GetTempPath(), "Easydict-constructor.log");
                File.AppendAllText(tempLog, $"[{DateTime.UtcNow:O}] App constructor started\n");
            }
            catch { }
#endif

            // NOTE: Language is managed by LocalizationService using ResourceContext.
            // No early initialization needed - ResourceContext can be updated at runtime.
#if DEBUG
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "Easydict-constructor.log"),
                    $"[{DateTime.UtcNow:O}] Calling InitializeComponent\n");
                this.InitializeComponent();
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "Easydict-constructor.log"),
                    $"[{DateTime.UtcNow:O}] InitializeComponent completed\n");
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "Easydict-constructor.log"),
                        $"[{DateTime.UtcNow:O}] InitializeComponent FAILED: {ex}\n");
                }
                catch { }
                throw;
            }
#else
            this.InitializeComponent();
#endif

            this.UnhandledException += OnUnhandledException;
        }

        /// <summary>
        /// Diagnostic logging with fallback locations for MSIX troubleshooting.
        /// </summary>
        private static void LogToFile(string message)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var entry = $"[{timestamp}] {message}\n";

            // Try LocalApplicationData first
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Easydict");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "debug.log");
                File.AppendAllText(logPath, entry);
                return;
            }
            catch { /* Try fallback */ }

            // Fallback: Windows Temp directory
            try
            {
                var tempLog = Path.Combine(Path.GetTempPath(), "Easydict-debug.log");
                File.AppendAllText(tempLog, entry);
            }
            catch { /* Must not throw */ }
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var message = e.Exception?.ToString() ?? "Unknown error";

            // ALWAYS log to debug log first
            LogToFile($"[UnhandledException] {message}");

            // Log to persistent file so crashes are diagnosable in release builds
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Easydict");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "crash.log");
                var entry = $"[{DateTime.UtcNow:O}] {message}\n";
                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // Logging must not throw
            }

            System.Diagnostics.Debug.WriteLine($"[App] Unhandled exception: {message}");

            // Let fatal exceptions (OOM, stack overflow, access violation) crash the process
            // rather than continuing in a corrupted state.
            if (IsFatalException(e.Exception))
                return;

            // For non-fatal exceptions, show an error dialog so the user sees what happened
            e.Handled = true;
            ShowErrorDialog(message);
        }

        private static bool IsFatalException(Exception? ex)
        {
            return ex is OutOfMemoryException
                or StackOverflowException
                or System.Runtime.InteropServices.SEHException
                or AccessViolationException;
        }

        private async void ShowErrorDialog(string message)
        {
            try
            {
                if (_window?.Content is not FrameworkElement root || root.XamlRoot is null)
                    return;

                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = "Unexpected Error",
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = root.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch
            {
                // Dialog display must not throw
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            LogToFile($"[OnLaunched] Starting - Args: {e.Arguments}");
            try
            {
                LogToFile($"[OnLaunched] Package: {Windows.ApplicationModel.Package.Current.Id.FullName}");
            }
            catch
            {
                LogToFile("[OnLaunched] Package: (unpackaged)");
            }

            _window = new Window();
            LogToFile("[OnLaunched] Window created");

            // Set window title
            _window.Title = "Easydict ᵇᵉᵗᵃ";

            // Set window size and get AppWindow reference
            try
            {
                _appWindow = ConfigureWindow(_window);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] ConfigureWindow failed: {ex}");
                // Fallback: get AppWindow without custom configuration
                try
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                    _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] Fallback AppWindow retrieval failed: {ex2.Message}");
                }
            }

            // Set window icon for unpackaged scenarios
            if (_appWindow != null)
            {
                try
                {
                    WindowIconService.SetWindowIcon(_appWindow);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] SetWindowIcon failed: {ex.Message}");
                }

                // Handle window close to minimize to tray instead
                _appWindow.Closing += OnWindowClosing;
            }

            if (_window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                _window.Content = rootFrame;
            }

            LogToFile("[OnLaunched] Navigating to MainPage...");
            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            LogToFile("[OnLaunched] Navigation complete");

            LogToFile("[OnLaunched] Activating window...");
            _window.Activate();
            LogToFile("[OnLaunched] Window activated");

            // Initialize services
            LogToFile("[OnLaunched] Initializing services...");
            InitializeServices();
            LogToFile("[OnLaunched] Launch complete!");
        }

        private void InitializeServices()
        {
            if (_window == null) return;

            var settings = SettingsService.Instance;

            // Initialize system tray icon
            try
            {
                _trayIconService = new TrayIconService(_window, _appWindow);
                _trayIconService.OnTranslateClipboard += OnTrayTranslateClipboard;
                _trayIconService.OnOpenSettings += OnTrayOpenSettings;
                _trayIconService.Initialize();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] TrayIconService initialization failed: {ex}");
            }

            // Initialize hotkey service
            try
            {
                _hotkeyService = new HotkeyService(_window);
                _hotkeyService.OnShowWindow += OnShowWindowHotkey;
                _hotkeyService.OnTranslateSelection += OnTranslateSelectionHotkey;
                _hotkeyService.OnShowMiniWindow += OnShowMiniWindowHotkey;
                _hotkeyService.OnShowFixedWindow += OnShowFixedWindowHotkey;
                _hotkeyService.OnToggleMiniWindow += OnToggleMiniWindowHotkey;
                _hotkeyService.OnToggleFixedWindow += OnToggleFixedWindowHotkey;
                _hotkeyService.Initialize();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] HotkeyService initialization failed: {ex}");
            }

            // Initialize clipboard service
            try
            {
                _clipboardService = new ClipboardService();
                _clipboardService.OnClipboardTextChanged += OnClipboardTextChanged;
                _clipboardService.IsMonitoringEnabled = settings.ClipboardMonitoring;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] ClipboardService initialization failed: {ex}");
            }

            // Apply always-on-top setting
            ApplyAlwaysOnTop(settings.AlwaysOnTop);

            // Apply saved theme setting
            ApplyTheme(settings.AppTheme);

#if DEBUG
            // Debug mode: automatically open mini window on startup
            MiniWindowService.Instance.Show();
#endif
        }

        private void OnShowWindowHotkey()
        {
            TextInsertionService.CaptureSourceWindow();

            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                ShowAndActivateWindow();
            });
        }

        private async void OnTranslateSelectionHotkey()
        {
            TextInsertionService.CaptureSourceWindow();

            // Get selected text from clipboard
            var text = await ClipboardService.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    ShowAndActivateWindow();

                    // Send text to MainPage for translation
                    if (_window?.Content is Frame frame && frame.Content is MainPage mainPage)
                    {
                        mainPage.SetTextAndTranslate(text);
                    }
                });
            }
        }

        private async void OnShowMiniWindowHotkey()
        {
            try
            {
                // Capture source window before getting text (which may change focus)
                TextInsertionService.CaptureSourceWindow();

                // Get selected text via intelligent method (clipboard for Electron, UIA with ClipWait fallback for others)
                var text = await TextSelectionService.GetSelectedTextAsync();

                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        MiniWindowService.Instance.ShowWithText(text);
                    }
                    else
                    {
                        MiniWindowService.Instance.Show();
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] OnShowMiniWindowHotkey error: {ex.Message}");
            }
        }

        private async void OnShowFixedWindowHotkey()
        {
            try
            {
                // Capture source window before getting text (which may change focus)
                TextInsertionService.CaptureSourceWindow();

                // Get selected text via intelligent method (clipboard for Electron, UIA with ClipWait fallback for others)
                var text = await TextSelectionService.GetSelectedTextAsync();

                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        FixedWindowService.Instance.ShowWithText(text);
                    }
                    else
                    {
                        FixedWindowService.Instance.Show();
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] OnShowFixedWindowHotkey error: {ex.Message}");
            }
        }

        private void OnToggleMiniWindowHotkey()
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                MiniWindowService.Instance.Toggle();
            });
        }

        private void OnToggleFixedWindowHotkey()
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                FixedWindowService.Instance.Toggle();
            });
        }

        private async void OnTrayTranslateClipboard()
        {
            var text = await ClipboardService.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    ShowAndActivateWindow();

                    if (_window?.Content is Frame frame && frame.Content is MainPage mainPage)
                    {
                        mainPage.SetTextAndTranslate(text);
                    }
                });
            }
        }

        private void OnTrayOpenSettings()
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                ShowAndActivateWindow();

                if (_window?.Content is Frame frame)
                {
                    frame.Navigate(typeof(SettingsPage));
                }
            });
        }

        private void OnClipboardTextChanged(string text)
        {
            ClipboardTextReceived?.Invoke(text);
        }

        private void ShowAndActivateWindow()
        {
            if (_window == null) return;

            // Show and activate the window
            _appWindow?.Show();
            _window.Activate();
        }

        private void HideWindow()
        {
            _appWindow?.Hide();
        }

        private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            var settings = SettingsService.Instance;

            // Save window dimensions before closing/minimizing
            SaveWindowDimensions();

            if (settings.MinimizeToTray)
            {
                // Minimize to tray instead of closing
                args.Cancel = true;
                HideWindow();
            }
            else
            {
                // Actually close and cleanup
                CleanupServices();
            }
        }

        /// <summary>
        /// Saves the current window dimensions to settings in DIPs (Device-Independent Pixels).
        /// This ensures window size is restored correctly across different DPI monitors.
        /// </summary>
        private void SaveWindowDimensions()
        {
            if (_window == null || _appWindow == null) return;

            var settings = SettingsService.Instance;
            if (!settings.EnableDpiAwareness)
            {
                // Don't save dimensions when DPI awareness is disabled
                return;
            }

            var hWnd = WindowNative.GetWindowHandle(_window);
            var scaleFactor = DpiHelper.GetScaleFactorForWindow(hWnd);

            // Convert physical pixels to DIPs for storage
            var currentSize = _appWindow.Size;
            var widthDips = DpiHelper.PhysicalPixelsToDips(currentSize.Width, scaleFactor);
            var heightDips = DpiHelper.PhysicalPixelsToDips(currentSize.Height, scaleFactor);

            settings.WindowWidthDips = widthDips;
            settings.WindowHeightDips = heightDips;
            settings.Save();
        }

        private void CleanupServices()
        {
            _clipboardService?.Dispose();
            _hotkeyService?.Dispose();
            _trayIconService?.Dispose();
            FixedWindowService.Instance.Dispose();
            MiniWindowService.Instance.Dispose();
        }

        /// <summary>
        /// Apply always-on-top setting to the window.
        /// </summary>
        public static void ApplyAlwaysOnTop(bool alwaysOnTop)
        {
            var appWindow = Instance._appWindow;
            if (appWindow != null)
            {
                var presenter = appWindow.Presenter as OverlappedPresenter;
                if (presenter != null)
                {
                    presenter.IsAlwaysOnTop = alwaysOnTop;
                }
            }
        }

        /// <summary>
        /// Apply clipboard monitoring setting.
        /// </summary>
        public static void ApplyClipboardMonitoring(bool enabled)
        {
            var clipboardService = Instance._clipboardService;
            if (clipboardService != null)
            {
                clipboardService.IsMonitoringEnabled = enabled;
            }
        }

        /// <summary>
        /// Apply app theme setting to all windows.
        /// </summary>
        /// <param name="theme">Theme name: "System", "Light", or "Dark"</param>
        public static void ApplyTheme(string theme)
        {
            var elementTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default // "System" follows system theme
            };

            // Apply to main window
            if (Instance._window?.Content is FrameworkElement mainRoot)
            {
                mainRoot.RequestedTheme = elementTheme;
            }

            // Apply to mini window
            MiniWindowService.Instance.ApplyTheme(elementTheme);

            // Apply to fixed window
            FixedWindowService.Instance.ApplyTheme(elementTheme);

            System.Diagnostics.Debug.WriteLine($"[App] Applied theme: {theme} (ElementTheme.{elementTheme})");
        }

        private static AppWindow ConfigureWindow(Window window)
        {
            var hWnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            var settings = SettingsService.Instance;

            if (settings.EnableDpiAwareness)
            {
                ConfigureWindowDpiAware(window, hWnd, appWindow, windowId);
            }
            else
            {
                ConfigureWindowLegacy(appWindow, windowId);
            }

            // Subscribe to DPI changes via XamlRoot (after content is loaded)
            if (window.Content is Microsoft.UI.Xaml.FrameworkElement frameworkElement)
            {
                frameworkElement.Loaded += (s, e) =>
                {
                    if (frameworkElement.XamlRoot != null && settings.EnableDpiAwareness)
                    {
                        frameworkElement.XamlRoot.Changed += (xamlRoot, args) =>
                        {
                            OnDpiChanged(window, appWindow, xamlRoot);
                        };
                    }
                };
            }

            return appWindow;
        }

        /// <summary>
        /// Configures window with DPI-aware positioning and sizing.
        /// </summary>
        private static void ConfigureWindowDpiAware(Window window, IntPtr hWnd, AppWindow appWindow, WindowId windowId)
        {
            var scaleFactor = DpiHelper.GetScaleFactorForWindow(hWnd);

            // Minimum window dimensions in DIPs
            const int minWidthDips = 400;
            const int minHeightDips = 500;

            var settings = SettingsService.Instance;

            // Choose window dimensions based on DPI scale for optimal visual size
            // High DPI (150%+): Use smaller DIPs to get ~580×700 physical pixels
            // Low DPI (100-125%): Use larger DIPs to get ~500×600 physical pixels
            var targetWidthDips = scaleFactor >= 1.5 ? 290.0 : 500.0;
            var targetHeightDips = scaleFactor >= 1.5 ? 350.0 : 600.0;

            // If user has customized window size, respect it
            var hasCustomSize = settings.WindowWidthDips != 290 && settings.WindowWidthDips != 500;
            if (hasCustomSize)
            {
                targetWidthDips = settings.WindowWidthDips;
                targetHeightDips = settings.WindowHeightDips;
            }

            // Convert DIPs to physical pixels for AppWindow APIs
            var widthPhysical = DpiHelper.DipsToPhysicalPixels(targetWidthDips, scaleFactor);
            var heightPhysical = DpiHelper.DipsToPhysicalPixels(targetHeightDips, scaleFactor);

            // Set initial size
            appWindow.Resize(new Windows.Graphics.SizeInt32(widthPhysical, heightPhysical));

            // Enforce minimum window size with DPI awareness
            var enforcingMinSize = false;
            appWindow.Changed += (_, args) =>
            {
                if (!args.DidSizeChange) return;
                if (enforcingMinSize) return;

                var currentScale = DpiHelper.GetScaleFactorForWindow(hWnd);
                var minWidthPhysical = DpiHelper.DipsToPhysicalPixels(minWidthDips, currentScale);
                var minHeightPhysical = DpiHelper.DipsToPhysicalPixels(minHeightDips, currentScale);

                var size = appWindow.Size;
                var targetWidth = Math.Max(size.Width, minWidthPhysical);
                var targetHeight = Math.Max(size.Height, minHeightPhysical);

                if (targetWidth == size.Width && targetHeight == size.Height) return;

                enforcingMinSize = true;
                try
                {
                    appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
                }
                finally
                {
                    enforcingMinSize = false;
                }
            };

            // Center on screen with DPI awareness
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                // WorkArea is in physical pixels
                var centerX = (displayArea.WorkArea.Width - widthPhysical) / 2;
                var centerY = (displayArea.WorkArea.Height - heightPhysical) / 2;
                appWindow.Move(new Windows.Graphics.PointInt32(centerX, centerY));
            }
        }

        /// <summary>
        /// Configures window using legacy behavior (no DPI awareness).
        /// Fallback for compatibility or troubleshooting.
        /// </summary>
        private static void ConfigureWindowLegacy(AppWindow appWindow, WindowId windowId)
        {
            const int minWidth = 400;
            const int minHeight = 500;

            var settings = SettingsService.Instance;

            // Read window dimensions from settings
            var targetWidth = (int)settings.WindowWidthDips;
            var targetHeight = (int)settings.WindowHeightDips;

            // Set initial size from settings
            appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

            // Enforce minimum window size (safe for unpackaged apps; avoids Win32 WndProc subclassing).
            var enforcingMinSize = false;
            appWindow.Changed += (_, args) =>
            {
                if (!args.DidSizeChange) return;
                if (enforcingMinSize) return;

                var size = appWindow.Size;
                var targetW = Math.Max(size.Width, minWidth);
                var targetH = Math.Max(size.Height, minHeight);

                if (targetW == size.Width && targetH == size.Height) return;

                enforcingMinSize = true;
                try
                {
                    appWindow.Resize(new Windows.Graphics.SizeInt32(targetW, targetH));
                }
                finally
                {
                    enforcingMinSize = false;
                }
            };

            // Center on screen using settings dimensions
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                var centerX = (displayArea.WorkArea.Width - targetWidth) / 2;
                var centerY = (displayArea.WorkArea.Height - targetHeight) / 2;
                appWindow.Move(new Windows.Graphics.PointInt32(centerX, centerY));
            }
        }

        /// <summary>
        /// Handles DPI changes when window moves between monitors with different DPI settings.
        /// Adjusts window size to maintain optimal visual appearance across different DPI scales.
        /// </summary>
        private static void OnDpiChanged(Window window, AppWindow appWindow, Microsoft.UI.Xaml.XamlRoot xamlRoot)
        {
            var hWnd = WindowNative.GetWindowHandle(window);
            var newScaleFactor = DpiHelper.GetScaleFactorForWindow(hWnd);

            // Choose optimal window dimensions for the new DPI scale
            // High DPI (150%+): Use smaller DIPs to get ~580×700 physical pixels
            // Low DPI (100-125%): Use larger DIPs to get ~500×600 physical pixels
            var targetWidthDips = newScaleFactor >= 1.5 ? 290.0 : 500.0;
            var targetHeightDips = newScaleFactor >= 1.5 ? 350.0 : 600.0;

            var targetWidthPhysical = DpiHelper.DipsToPhysicalPixels(targetWidthDips, newScaleFactor);
            var targetHeightPhysical = DpiHelper.DipsToPhysicalPixels(targetHeightDips, newScaleFactor);

            // Resize window to optimal size for new DPI
            appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidthPhysical, targetHeightPhysical));

            // Re-apply minimum size constraints with new DPI
            const int minWidthDips = 400;
            const int minHeightDips = 500;
            var minWidthPhysical = DpiHelper.DipsToPhysicalPixels(minWidthDips, newScaleFactor);
            var minHeightPhysical = DpiHelper.DipsToPhysicalPixels(minHeightDips, newScaleFactor);

            var currentSize = appWindow.Size;
            if (currentSize.Width < minWidthPhysical || currentSize.Height < minHeightPhysical)
            {
                var targetWidth = Math.Max(currentSize.Width, minWidthPhysical);
                var targetHeight = Math.Max(currentSize.Height, minHeightPhysical);
                appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
            }

            // Log DPI change for debugging
            System.Diagnostics.Debug.WriteLine($"[DPI] Scale changed to {newScaleFactor * 100:F0}%, resized to {targetWidthDips}×{targetHeightDips} DIPs");
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
