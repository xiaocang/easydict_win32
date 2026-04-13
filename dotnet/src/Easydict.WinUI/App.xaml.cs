using System.Runtime.InteropServices;
using Easydict.WinUI.Services;
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
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private Window? _window;
        private TrayIconService? _trayIconService;
        private HotkeyService? _hotkeyService;
        private ClipboardService? _clipboardService;
        private MouseHookService? _mouseHookService;
        private PopButtonService? _popButtonService;
        private OcrTranslateService? _ocrTranslateService;
        private AppWindow? _appWindow;

        // IPC: named event for context menu --ocr-translate signaling
        private EventWaitHandle? _ocrSignalEvent;
        private Thread? _ocrSignalThread;

        private static App Instance => (App)Current;

        private static bool IsDebugEnvFlagEnabled(string variableName)
        {
#if DEBUG
            var value = Environment.GetEnvironmentVariable(variableName);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
#else
            return false;
#endif
        }

        private static bool IsMouseSelectionTranslateDisabledForDebug()
            => IsDebugEnvFlagEnabled("EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE");

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
            // NOTE: Language is managed by LocalizationService using ResourceContext.
            // No early initialization needed - ResourceContext can be updated at runtime.
            this.InitializeComponent();

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

            // If "minimize to tray on startup" is enabled, hide the window immediately
            // after activation (window must be activated first for services to initialize properly)
            var startupSettings = SettingsService.Instance;
            if (startupSettings.MinimizeToTrayOnStartup && startupSettings.MinimizeToTray)
            {
                LogToFile("[OnLaunched] MinimizeToTrayOnStartup enabled, hiding window");
                HideWindow();
            }

            // If cold-launched via protocol activation (easydict://ocr-translate) or
            // --ocr-translate when app wasn't running, trigger OCR after initialization.
            if (Program.PendingOcrTranslate && _ocrTranslateService != null)
            {
                _window.DispatcherQueue.TryEnqueue(async () =>
                {
                    // Small delay to let the window fully render before capturing the screen
                    await Task.Delay(500);
                    try
                    {
                        await _ocrTranslateService.OcrTranslateAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[App] PendingOcrTranslate error: {ex.Message}");
                    }
                });
            }

            // Run region detection asynchronously after startup completes.
            // On first launch this detects China region and switches defaults (Google → Bing).
            // For returning users with saved settings this is a no-op.
            _ = SettingsService.Instance.InitializeRegionDefaultsAsync();
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
                _trayIconService.OnOcrTranslate += OnTrayOcrTranslate;
                _trayIconService.OnOpenSettings += OnTrayOpenSettings;
                _trayIconService.OnBrowserSupportAction += OnBrowserSupportAction;
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
                _hotkeyService.OnOcrTranslate += OnOcrTranslateHotkey;
                _hotkeyService.OnSilentOcr += OnSilentOcrHotkey;
                _hotkeyService.Initialize();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] HotkeyService initialization failed: {ex}");
            }

            // Initialize OCR translate service
            try
            {
                _ocrTranslateService = new OcrTranslateService(_window.DispatcherQueue);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] OcrTranslateService initialization failed: {ex}");
            }

            // Start named-event listener for Shell context menu --ocr-translate IPC.
            // A second process launched from File Explorer signals this event and exits;
            // the background thread wakes up and triggers OCR on the UI thread.
            try
            {
                _ocrSignalEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                    Program.OcrTranslateEventName);

                _ocrSignalThread = new Thread(() =>
                {
                    try
                    {
                        while (_ocrSignalEvent.WaitOne())
                        {
                            System.Diagnostics.Debug.WriteLine("[App] OCR signal received from context menu");
                            var ocrService = _ocrTranslateService;
                            if (ocrService is null)
                            {
                                System.Diagnostics.Debug.WriteLine("[App] OCR service not available, ignoring signal");
                                continue;
                            }

                            _window.DispatcherQueue.TryEnqueue(async () =>
                            {
                                try
                                {
                                    await ocrService.OcrTranslateAsync();
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[App] Context menu OCR error: {ex.Message}");
                                }
                            });
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Event disposed during shutdown — exit gracefully
                    }
                })
                {
                    IsBackground = true,
                    Name = "OcrSignalListener"
                };
                _ocrSignalThread.Start();
                System.Diagnostics.Debug.WriteLine("[App] OCR signal listener started");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] OCR signal listener failed: {ex.Message}");
            }

            // Register Shell context menu if enabled
            try
            {
                if (settings.ShellContextMenu)
                {
                    ContextMenuService.Register();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Shell context menu registration failed: {ex.Message}");
            }

            // Initialize clipboard service
            try
            {
                _clipboardService = new ClipboardService();
                _clipboardService.ShouldSkipClipboardChange = () =>
                {
                    var processName = PopButtonService.GetForegroundProcessName();
                    return SettingsService.Instance.IsMouseSelectionExcluded(processName);
                };
                _clipboardService.OnClipboardTextChanged += OnClipboardTextChanged;
                _clipboardService.IsMonitoringEnabled = settings.ClipboardMonitoring;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] ClipboardService initialization failed: {ex}");
            }

            // Initialize mouse selection translate service
            if (IsMouseSelectionTranslateDisabledForDebug())
            {
                System.Diagnostics.Debug.WriteLine(
                    "[App] Mouse selection translate disabled by EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE");
            }
            else
            {
                try
                {
                    _mouseHookService = new MouseHookService();
                    _popButtonService = new PopButtonService(_window.DispatcherQueue, _mouseHookService);

                    _mouseHookService.IsCurrentAppExcluded = () =>
                    {
                        var processName = PopButtonService.GetForegroundProcessName();
                        return SettingsService.Instance.IsMouseSelectionExcluded(processName);
                    };
                    _mouseHookService.OnDragSelectionEnd += _popButtonService.OnDragSelectionEnd;
                    _mouseHookService.OnMouseDown += () => _popButtonService.Dismiss("MouseDown");
                    _mouseHookService.OnMouseScroll += () => _popButtonService.Dismiss("MouseScroll");
                    _mouseHookService.OnRightMouseDown += () => _popButtonService.Dismiss("RightMouseDown");
                    _mouseHookService.OnKeyDown += () => _popButtonService.Dismiss("KeyDown");

                    if (settings.MouseSelectionTranslate)
                    {
                        if (!_mouseHookService.Install())
                        {
                            System.Diagnostics.Trace.WriteLine("[App] Mouse hook installation failed at startup");
                        }
                    }

                    _popButtonService.IsEnabled = settings.MouseSelectionTranslate;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] MouseSelectionTranslate initialization failed: {ex}");
                }
            }

            // Apply always-on-top setting
            ApplyAlwaysOnTop(settings.AlwaysOnTop);

            // Apply saved theme setting
            ApplyTheme(settings.AppTheme);

            // Pre-warm TTS service to avoid first-use delay
            _ = Task.Run(() =>
            {
                try
                {
                    TextToSpeechService.Instance.WarmUp();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] TTS pre-warm failed: {ex.Message}");
                }
            });

        }

        private void OnShowWindowHotkey()
        {
            TextInsertionService.CaptureSourceWindow();

            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                // Toggle behavior (issue #123): if window is foreground, hide it.
                // If visible-but-background, raise it (#129). Else show it.
                if (IsMainWindowVisible && IsMainWindowForeground)
                {
                    HideWindow();
                }
                else
                {
                    ShowAndActivateWindow();
                    FocusMainWindowInputForTyping();
                }
            });
        }

        private async void OnTranslateSelectionHotkey()
        {
            try
            {
                TextInsertionService.CaptureSourceWindow();

                var text = await TextSelectionService.GetSelectedTextAsync();

                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    ShowAndActivateWindow();

                    if (!string.IsNullOrWhiteSpace(text)
                        && _window?.Content is Frame frame && frame.Content is MainPage mainPage)
                    {
                        mainPage.SetTextAndTranslate(text);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] OnTranslateSelectionHotkey error: {ex.Message}");
            }
        }

        private async void OnShowMiniWindowHotkey()
        {
            try
            {
                if (MiniWindowService.Instance.IsVisible
                    && MiniWindowService.Instance.IsForeground)
                {
                    _window?.DispatcherQueue.TryEnqueue(() =>
                    {
                        MiniWindowService.Instance.Hide();
                    });
                    return;
                }

                // Capture source window before getting text (which may change focus)
                TextInsertionService.CaptureSourceWindow();

                // Get selected text via intelligent method (clipboard for Electron, UIA with ClipWait fallback for others)
                var text = await TextSelectionService.GetSelectedTextAsync();

                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Selected text takes precedence — always show with the new text.
                        MiniWindowService.Instance.ShowWithText(text);
                    }
                    else
                    {
                        // Show or raise from background (#129).
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
                if (FixedWindowService.Instance.IsVisible
                    && FixedWindowService.Instance.IsForeground)
                {
                    _window?.DispatcherQueue.TryEnqueue(() =>
                    {
                        FixedWindowService.Instance.Hide();
                    });
                    return;
                }

                // Capture source window before getting text (which may change focus)
                TextInsertionService.CaptureSourceWindow();

                // Get selected text via intelligent method (clipboard for Electron, UIA with ClipWait fallback for others)
                var text = await TextSelectionService.GetSelectedTextAsync();

                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Selected text takes precedence — always show with the new text.
                        FixedWindowService.Instance.ShowWithText(text);
                    }
                    else
                    {
                        // Show or raise from background (#129).
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

        private async void OnOcrTranslateHotkey()
        {
            if (_ocrTranslateService is null)
            {
                System.Diagnostics.Debug.WriteLine("[Hotkey] OCR service not available");
                return;
            }

            try
            {
                await _ocrTranslateService.OcrTranslateAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] OnOcrTranslateHotkey error: {ex.Message}");
            }
        }

        private async void OnSilentOcrHotkey()
        {
            if (_ocrTranslateService is null)
            {
                System.Diagnostics.Debug.WriteLine("[Hotkey] OCR service not available");
                return;
            }

            try
            {
                await _ocrTranslateService.SilentOcrAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] OnSilentOcrHotkey error: {ex.Message}");
            }
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

        private async void OnTrayOcrTranslate()
        {
            if (_ocrTranslateService is null)
            {
                System.Diagnostics.Debug.WriteLine("[Tray] OCR service not available");
                return;
            }

            try
            {
                await _ocrTranslateService.OcrTranslateAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Tray] OnTrayOcrTranslate error: {ex.Message}");
            }
        }

        private async void OnBrowserSupportAction(string browser, bool isInstall)
        {
            try
            {
                if (isInstall)
                {
                    // Run bundled registrar to register native messaging host.
                    // This works for both MSIX and non-MSIX installs.
                    var success = await BrowserSupportService.InstallWithRegistrarAsync(browser);
                    if (success)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Tray] Browser support installed via registrar for {browser}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Tray] Registrar install failed for {browser}, falling back to local install");

                        // Fallback to local install (works for non-MSIX)
                        switch (browser)
                        {
                            case "chrome":
                                BrowserSupportService.InstallChrome();
                                break;
                            case "firefox":
                                BrowserSupportService.InstallFirefox();
                                break;
                            case "all":
                                BrowserSupportService.InstallAll();
                                break;
                        }
                    }
                }
                else
                {
                    await BrowserSupportService.UninstallWithRegistrarAsync(browser);
                }

                // Refresh menu states after action
                _trayIconService?.UpdateBrowserSupportMenuStates();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Tray] BrowserSupportAction({browser}, install={isInstall}) error: {ex.Message}");

                // Last resort fallback
                try
                {
                    if (isInstall)
                    {
                        switch (browser)
                        {
                            case "chrome":
                                BrowserSupportService.InstallChrome();
                                break;
                            case "firefox":
                                BrowserSupportService.InstallFirefox();
                                break;
                            case "all":
                                BrowserSupportService.InstallAll();
                                break;
                        }
                    }
                    else
                    {
                        switch (browser)
                        {
                            case "chrome":
                                BrowserSupportService.UninstallChrome();
                                break;
                            case "firefox":
                                BrowserSupportService.UninstallFirefox();
                                break;
                            case "all":
                                BrowserSupportService.UninstallAll();
                                break;
                        }
                    }

                    _trayIconService?.UpdateBrowserSupportMenuStates();
                }
                catch (Exception fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Tray] Fallback BrowserSupportAction also failed: {fallbackEx.Message}");
                }
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
            var processName = PopButtonService.GetForegroundProcessName();
            if (SettingsService.Instance.IsMouseSelectionExcluded(processName))
            {
                System.Diagnostics.Debug.WriteLine($"[App] Clipboard from excluded app '{processName}', skipping");
                return;
            }

            ClipboardTextReceived?.Invoke(text);
        }

        private void ShowAndActivateWindow()
        {
            if (_window == null) return;

            // Show the window
            _appWindow?.Show();

            // Try to bring window to front using multiple methods
            try
            {
                _appWindow?.MoveInZOrderAtTop();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"App: MoveInZOrderAtTop failed: {ex.Message}");
            }

            // Use Win32 SetForegroundWindow to forcefully bring window to front
            // (Activate() alone does not raise an already-visible-but-background window)
            var foregroundSet = ForegroundWindowHelper.TryBringToFront(_window, "App");
            if (!foregroundSet)
            {
                System.Diagnostics.Debug.WriteLine("App: SetForegroundWindow failed; relying on Activate()");
            }

            _window.Activate();
        }

        private void FocusMainWindowInputForTyping()
        {
            if (_window?.Content is not Frame frame || frame.Content is not MainPage mainPage)
            {
                return;
            }

            mainPage.QueueInputFocusAndSelectAll();
        }

        /// <summary>
        /// Returns true if the main window is currently visible.
        /// </summary>
        private bool IsMainWindowVisible => _appWindow?.IsVisible ?? false;

        /// <summary>
        /// Returns true if the main window is currently the foreground window.
        /// </summary>
        private bool IsMainWindowForeground
        {
            get
            {
                if (_window == null) return false;
                var hWnd = WindowNative.GetWindowHandle(_window);
                return GetForegroundWindow() == hWnd;
            }
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

            // Don't save dimensions when the window is minimized — the size is meaningless
            if (_appWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                return;
            }

            var hWnd = WindowNative.GetWindowHandle(_window);
            var scaleFactor = DpiHelper.GetScaleFactorForWindow(hWnd);

            // Convert physical pixels to DIPs for storage
            var currentSize = _appWindow.Size;
            var widthDips = DpiHelper.PhysicalPixelsToDips(currentSize.Width, scaleFactor);
            var heightDips = DpiHelper.PhysicalPixelsToDips(currentSize.Height, scaleFactor);

            // Don't save unreasonably small dimensions (e.g. hidden or partially collapsed window)
            const double minWidthDips = 400;
            const double minHeightDips = 500;
            if (widthDips < minWidthDips || heightDips < minHeightDips)
            {
                return;
            }

            settings.WindowWidthDips = widthDips;
            settings.WindowHeightDips = heightDips;
            settings.Save();
        }

        private void CleanupServices()
        {
            // Dispose OCR signal event first — this unblocks the listener thread's WaitOne()
            // which throws ObjectDisposedException, causing the thread to exit gracefully.
            _ocrSignalEvent?.Dispose();
            _ocrSignalEvent = null;

            // Wait briefly for the signal thread to finish to avoid races during teardown
            if (_ocrSignalThread?.IsAlive == true)
            {
                _ocrSignalThread.Join(TimeSpan.FromSeconds(2));
            }
            _ocrSignalThread = null;

            _mouseHookService?.Dispose();
            _popButtonService?.Dispose();
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
        /// Apply mouse selection translate setting.
        /// Installs or uninstalls the global mouse hook at runtime.
        /// </summary>
        public static void ApplyMouseSelectionTranslate(bool enabled)
        {
            var app = Instance;
            if (IsMouseSelectionTranslateDisabledForDebug())
            {
                System.Diagnostics.Debug.WriteLine(
                    "[App] ApplyMouseSelectionTranslate ignored due to EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE");
                if (app._popButtonService != null)
                {
                    app._popButtonService.IsEnabled = false;
                }

                if (app._mouseHookService != null)
                {
                    app._mouseHookService.Uninstall();
                }

                return;
            }

            if (app._popButtonService != null)
            {
                app._popButtonService.IsEnabled = enabled;
            }

            if (app._mouseHookService != null)
            {
                if (enabled)
                {
                    if (!app._mouseHookService.Install())
                    {
                        System.Diagnostics.Trace.WriteLine("[App] Mouse hook installation failed on toggle");
                    }
                }
                else
                {
                    app._mouseHookService.Uninstall();
                }
            }
        }

        /// <summary>
        /// Apply Shell context menu registration setting.
        /// Registers or unregisters the "OCR Translate" entry in Windows File Explorer.
        /// </summary>
        public static void ApplyShellContextMenu(bool enabled)
        {
            if (enabled)
                ContextMenuService.Register();
            else
                ContextMenuService.Unregister();
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

            // Apply to pop button
            Instance._popButtonService?.ApplyTheme(elementTheme);

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

            // Use saved window dimensions (DIPs are already DPI-independent), clamped to minimums
            var targetWidthDips = Math.Max(settings.WindowWidthDips, minWidthDips);
            var targetHeightDips = Math.Max(settings.WindowHeightDips, minHeightDips);

            // Convert DIPs to physical pixels for AppWindow APIs
            var widthPhysical = DpiHelper.DipsToPhysicalPixels(targetWidthDips, scaleFactor);
            var heightPhysical = DpiHelper.DipsToPhysicalPixels(targetHeightDips, scaleFactor);

            // Clamp to work area so the window fits on small screens
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                widthPhysical = Math.Min(widthPhysical, displayArea.WorkArea.Width);
                heightPhysical = Math.Min(heightPhysical, displayArea.WorkArea.Height);
            }

            // Set initial size
            appWindow.Resize(new Windows.Graphics.SizeInt32(widthPhysical, heightPhysical));

            // Enforce minimum window size with DPI awareness, clamped to work area
            var enforcingMinSize = false;
            appWindow.Changed += (_, args) =>
            {
                if (!args.DidSizeChange) return;
                if (enforcingMinSize) return;

                var currentScale = DpiHelper.GetScaleFactorForWindow(hWnd);
                var minWidthPhysical = DpiHelper.DipsToPhysicalPixels(minWidthDips, currentScale);
                var minHeightPhysical = DpiHelper.DipsToPhysicalPixels(minHeightDips, currentScale);

                // Clamp the minimum to the current work area so we never exceed it
                var currentDisplay = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
                if (currentDisplay is not null)
                {
                    minWidthPhysical = Math.Min(minWidthPhysical, currentDisplay.WorkArea.Width);
                    minHeightPhysical = Math.Min(minHeightPhysical, currentDisplay.WorkArea.Height);
                }

                var size = appWindow.Size;
                var targetWidth = Math.Max(size.Width, minWidthPhysical);
                var targetHeight = Math.Max(size.Height, minHeightPhysical);

                if (targetWidth == size.Width && targetHeight == size.Height) return;

                enforcingMinSize = true;
                try
                {
                    appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

                    // Reposition if the enforced size pushes the window out of the work area
                    if (currentDisplay is not null)
                    {
                        var pos = appWindow.Position;
                        var wa = currentDisplay.WorkArea;
                        var newX = Math.Max(wa.X, Math.Min(pos.X, wa.X + wa.Width - targetWidth));
                        var newY = Math.Max(wa.Y, Math.Min(pos.Y, wa.Y + wa.Height - targetHeight));
                        if (newX != pos.X || newY != pos.Y)
                        {
                            appWindow.Move(new Windows.Graphics.PointInt32(newX, newY));
                        }
                    }
                }
                finally
                {
                    enforcingMinSize = false;
                }
            };

            // Center on screen with DPI awareness, ensuring the window stays within the work area
            if (displayArea is not null)
            {
                // WorkArea is in physical pixels
                var centerX = (displayArea.WorkArea.Width - widthPhysical) / 2 + displayArea.WorkArea.X;
                var centerY = (displayArea.WorkArea.Height - heightPhysical) / 2 + displayArea.WorkArea.Y;

                // Clamp position so the window doesn't extend beyond the work area
                centerX = Math.Max(displayArea.WorkArea.X, Math.Min(centerX, displayArea.WorkArea.X + displayArea.WorkArea.Width - widthPhysical));
                centerY = Math.Max(displayArea.WorkArea.Y, Math.Min(centerY, displayArea.WorkArea.Y + displayArea.WorkArea.Height - heightPhysical));

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

            // Read window dimensions from settings, clamped to minimums
            var targetWidth = Math.Max((int)settings.WindowWidthDips, minWidth);
            var targetHeight = Math.Max((int)settings.WindowHeightDips, minHeight);

            // Clamp to work area so the window fits on small screens
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                targetWidth = Math.Min(targetWidth, displayArea.WorkArea.Width);
                targetHeight = Math.Min(targetHeight, displayArea.WorkArea.Height);
            }

            // Set initial size from settings
            appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

            // Enforce minimum window size, clamped to work area
            var enforcingMinSize = false;
            appWindow.Changed += (_, args) =>
            {
                if (!args.DidSizeChange) return;
                if (enforcingMinSize) return;

                var effectiveMinW = minWidth;
                var effectiveMinH = minHeight;

                // Clamp the minimum to the current work area so we never exceed it
                var currentDisplay = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
                if (currentDisplay is not null)
                {
                    effectiveMinW = Math.Min(effectiveMinW, currentDisplay.WorkArea.Width);
                    effectiveMinH = Math.Min(effectiveMinH, currentDisplay.WorkArea.Height);
                }

                var size = appWindow.Size;
                var targetW = Math.Max(size.Width, effectiveMinW);
                var targetH = Math.Max(size.Height, effectiveMinH);

                if (targetW == size.Width && targetH == size.Height) return;

                enforcingMinSize = true;
                try
                {
                    appWindow.Resize(new Windows.Graphics.SizeInt32(targetW, targetH));

                    // Reposition if the enforced size pushes the window out of the work area
                    if (currentDisplay is not null)
                    {
                        var pos = appWindow.Position;
                        var wa = currentDisplay.WorkArea;
                        var newX = Math.Max(wa.X, Math.Min(pos.X, wa.X + wa.Width - targetW));
                        var newY = Math.Max(wa.Y, Math.Min(pos.Y, wa.Y + wa.Height - targetH));
                        if (newX != pos.X || newY != pos.Y)
                        {
                            appWindow.Move(new Windows.Graphics.PointInt32(newX, newY));
                        }
                    }
                }
                finally
                {
                    enforcingMinSize = false;
                }
            };

            // Center on screen, ensuring the window stays within the work area
            if (displayArea is not null)
            {
                var centerX = (displayArea.WorkArea.Width - targetWidth) / 2 + displayArea.WorkArea.X;
                var centerY = (displayArea.WorkArea.Height - targetHeight) / 2 + displayArea.WorkArea.Y;

                // Clamp position so the window doesn't extend beyond the work area
                centerX = Math.Max(displayArea.WorkArea.X, Math.Min(centerX, displayArea.WorkArea.X + displayArea.WorkArea.Width - targetWidth));
                centerY = Math.Max(displayArea.WorkArea.Y, Math.Min(centerY, displayArea.WorkArea.Y + displayArea.WorkArea.Height - targetHeight));

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

            // Clamp to work area so the window fits on small screens
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                targetWidthPhysical = Math.Min(targetWidthPhysical, displayArea.WorkArea.Width);
                targetHeightPhysical = Math.Min(targetHeightPhysical, displayArea.WorkArea.Height);
            }

            // Resize window to optimal size for new DPI
            appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidthPhysical, targetHeightPhysical));

            // Re-apply minimum size constraints with new DPI, clamped to work area
            const int minWidthDips = 400;
            const int minHeightDips = 500;
            var minWidthPhysical = DpiHelper.DipsToPhysicalPixels(minWidthDips, newScaleFactor);
            var minHeightPhysical = DpiHelper.DipsToPhysicalPixels(minHeightDips, newScaleFactor);

            if (displayArea is not null)
            {
                minWidthPhysical = Math.Min(minWidthPhysical, displayArea.WorkArea.Width);
                minHeightPhysical = Math.Min(minHeightPhysical, displayArea.WorkArea.Height);
            }

            var currentSize = appWindow.Size;
            if (currentSize.Width < minWidthPhysical || currentSize.Height < minHeightPhysical)
            {
                var targetWidth = Math.Max(currentSize.Width, minWidthPhysical);
                var targetHeight = Math.Max(currentSize.Height, minHeightPhysical);
                appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
            }

            // Reposition if the window extends beyond the work area
            if (displayArea is not null)
            {
                var pos = appWindow.Position;
                var size = appWindow.Size;
                var wa = displayArea.WorkArea;
                var newX = Math.Max(wa.X, Math.Min(pos.X, wa.X + wa.Width - size.Width));
                var newY = Math.Max(wa.Y, Math.Min(pos.Y, wa.Y + wa.Height - size.Height));
                if (newX != pos.X || newY != pos.Y)
                {
                    appWindow.Move(new Windows.Graphics.PointInt32(newX, newY));
                }
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
