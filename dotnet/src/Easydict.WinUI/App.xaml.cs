using System.Runtime.InteropServices;
using Easydict.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Win32;
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

        private const uint WM_QUERYENDSESSION = 0x0011;
        private const uint WM_ENDSESSION = 0x0016;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint WM_THEMECHANGED = 0x031A;
        private const nuint AppWindowSubclassId = 2;

        private delegate nint SubclassProc(
            nint hWnd,
            uint uMsg,
            nint wParam,
            nint lParam,
            nuint uIdSubclass,
            nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(
            nint hWnd,
            SubclassProc pfnSubclass,
            nuint uIdSubclass,
            nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(
            nint hWnd,
            SubclassProc pfnSubclass,
            nuint uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern nint DefSubclassProc(
            nint hWnd,
            uint uMsg,
            nint wParam,
            nint lParam);

        private Window? _window;
        private TrayIconService? _trayIconService;
        private HotkeyService? _hotkeyService;
        private ClipboardService? _clipboardService;
        private MouseHookService? _mouseHookService;
        private PopButtonService? _popButtonService;
        private OcrTranslateService? _ocrTranslateService;
        private AppWindow? _appWindow;
        private bool? _lastSystemDark;
        private int _systemThemeRefreshQueued;
        private int _sessionEndQueryLogged;
        private nint _appWindowSubclassHwnd;
        private SubclassProc? _appWindowSubclassProc;
        private volatile bool _isSystemShutdownRequested;
        private bool _servicesInitialized;
        private static int _pendingRedirectedOcrTranslate;

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

        private static bool IsMemoryAbVariantB()
        {
            var mode = Environment.GetEnvironmentVariable("EASYDICT_UIA_MEMORY_AB_MODE");
            return string.Equals(mode, "B", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the main window instance.
        /// </summary>
        public static Window? MainWindow => Instance._window;

        /// <summary>
        /// Gets the HotkeyService instance for dynamic reloading.
        /// </summary>
        internal static HotkeyService? HotkeyService => Instance._hotkeyService;

        /// <summary>
        /// Event fired when clipboard text is received (for auto-translate).
        /// </summary>
        public static event Action<string>? ClipboardTextReceived;

        public App()
        {
            // Language is managed by LocalizationService. SettingsPage.ApplyLocalization()
            // explicitly assigns each user-visible string from LocalizationService at runtime,
            // so x:Uid is intentionally not used in this codebase for app strings.
            this.InitializeComponent();

            // Easydict.WindowsAI lives below the UI layer and cannot reach LocalizationService
            // directly. The hook lets it surface localized hints in exception messages without
            // taking a dependency on UI services. Tests and non-UI consumers leave it null and
            // receive English defaults.
            Easydict.WindowsAI.WindowsLanguageModelClient.HintLocalizer =
                key => LocalizationService.Instance.GetString(key);

            this.UnhandledException += OnUnhandledException;
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var message = e.Exception?.ToString() ?? "Unknown error";
            CrashDiagnostics.LogException(
                "App.OnUnhandledException",
                e.Exception,
                isTerminating: false,
                isHandled: false);
            System.Diagnostics.Debug.WriteLine($"[App] Unhandled exception: {message}");

            if (CrashDiagnostics.IsProcessFatal(e.Exception))
            {
                return;
            }

            e.Handled = true;
            ShowErrorDialog(message);
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
            CrashDiagnostics.Log($"[OnLaunched] Starting - Args: {e.Arguments}");
            if (EasydictConditions.IsPackaged)
            {
                try
                {
                    CrashDiagnostics.Log($"[OnLaunched] Package: {Windows.ApplicationModel.Package.Current.Id.FullName}");
                }
                catch (Exception ex)
                {
                    CrashDiagnostics.Log($"[OnLaunched] Package: (read failed: {ex.Message})");
                }
            }
            else
            {
                CrashDiagnostics.Log("[OnLaunched] Package: (unpackaged)");
            }

            _window = new Window();
            CrashDiagnostics.Log("[OnLaunched] Window created");

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

            var rootFrame = EnsureRootFrame();
            if (rootFrame is null)
            {
                return;
            }

            CrashDiagnostics.Log("[OnLaunched] Navigating to MainPage...");
            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            CrashDiagnostics.Log("[OnLaunched] Navigation complete");

            CrashDiagnostics.Log("[OnLaunched] Activating window...");
            _window.Activate();
            CrashDiagnostics.Log("[OnLaunched] Window activated");

            // Initialize services
            CrashDiagnostics.Log("[OnLaunched] Initializing services...");
            InitializeServices();
            _servicesInitialized = true;
            CrashDiagnostics.Log("[OnLaunched] Launch complete!");

            // If "minimize to tray on startup" is enabled, hide the window immediately
            // after activation (window must be activated first for services to initialize properly)
            var startupSettings = SettingsService.Instance;
            if (startupSettings.MinimizeToTrayOnStartup && startupSettings.MinimizeToTray)
            {
                CrashDiagnostics.Log("[OnLaunched] MinimizeToTrayOnStartup enabled, hiding window");
                HideWindow();
            }

            TriggerStartupOcrTranslateIfNeeded();

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
                CrashDiagnostics.Log($"[App] Starting HotkeyService initialization... HWND: {WindowNative.GetWindowHandle(_window)}");
                CrashDiagnostics.Log($"[App] Hotkey settings: Show={settings.EnableShowWindowHotkey}, Translate={settings.EnableTranslateSelectionHotkey}, Mini={settings.EnableShowMiniWindowHotkey}, Fixed={settings.EnableShowFixedWindowHotkey}, OCR={settings.EnableOcrTranslateHotkey}, Silent={settings.EnableSilentOcrHotkey}");
                
                _hotkeyService = new HotkeyService(_window);
                _hotkeyService.OnShowWindow += OnShowWindowHotkey;
                _hotkeyService.OnTranslateSelection += OnTranslateSelectionHotkey;
                _hotkeyService.OnShowMiniWindow += OnShowMiniWindowHotkey;
                _hotkeyService.OnShowFixedWindow += OnShowFixedWindowHotkey;
                _hotkeyService.OnToggleMiniWindow += OnToggleMiniWindowHotkey;
                _hotkeyService.OnToggleFixedWindow += OnToggleFixedWindowHotkey;
                _hotkeyService.OnOcrTranslate += OnOcrTranslateHotkey;
                _hotkeyService.OnSilentOcr += OnSilentOcrHotkey;
                var hotkeyFailures = _hotkeyService.Initialize();
                QueueHotkeyRegistrationWarning(hotkeyFailures);
                CrashDiagnostics.Log("[App] HotkeyService initialization call completed.");
            }
            catch (Exception ex)
            {
                CrashDiagnostics.Log($"[App] HotkeyService initialization CRITICAL FAILURE: {ex}");
                System.Diagnostics.Debug.WriteLine($"[App] HotkeyService initialization failed: {ex}");
            }

            // OCR translate service is created lazily on first hotkey/tray/signal trigger.
            // Skipping eager construction at startup avoids the OcrTranslateService +
            // ScreenCaptureService allocation when the user never uses OCR. First-trigger
            // latency is negligible (cheap ctors; the real OCR engine is created per-call
            // inside OcrServiceFactory.Create()).
            //
            // The named-event listener still starts eagerly because external processes
            // (shell context menu, browser extension native bridge) signal it at any
            // time. The signal handler ensures the service exists before invoking it.
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
                            _window.DispatcherQueue.TryEnqueue(async () =>
                            {
                                try
                                {
                                    var ocrService = EnsureOcrTranslateService();
                                    if (ocrService != null)
                                    {
                                        await ocrService.OcrTranslateAsync();
                                    }
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
            RegisterSystemMessageHandlers();
            ApplyTheme(settings.AppTheme);

            // Pre-warm TTS service to avoid first-use delay, but only when the user
            // is likely to use it. If AutoPlayTranslation is off the user clicks Speak
            // explicitly — first-click latency is acceptable, and skipping warmup keeps
            // SpeechSynthesizer + voice list out of the working set.
            if (settings.AutoPlayTranslation)
            {
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
                // Wait briefly for the user to release physical keys and for the mouse drag selection to finalize in the OS.
                await Task.Delay(150);

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

                await RaceShowWindowWithSelectionAsync(
                    showEmpty: MiniWindowService.Instance.Show,
                    showWithText: MiniWindowService.Instance.ShowWithText).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] OnShowMiniWindowHotkey error: {ex}");
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

                await RaceShowWindowWithSelectionAsync(
                    showEmpty: FixedWindowService.Instance.Show,
                    showWithText: FixedWindowService.Instance.ShowWithText).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] OnShowFixedWindowHotkey error: {ex.Message}");
            }
        }

        // Budget for the fast path. Long enough to catch clipboard-pre-staged paths
        // (Electron / web apps typically return inside 30-60ms), short enough that a
        // stalled UIA fallback can't make the window feel frozen on Word / PowerPoint /
        // PDF readers (which routinely take 400-1200ms).
        private const int SelectionFastPathBudgetMs = 80;

        /// <summary>
        /// Race a TextSelectionService.GetSelectedTextAsync call against a short timer.
        /// If the selection arrives inside the budget the window opens once with the text
        /// inline (no flash). Otherwise the window opens immediately with no text so the
        /// user sees instant response, and the translation is filled in when the selection
        /// eventually arrives. Keeps the UI thread free of UIA / ClipWait waits during the
        /// hotkey-to-Activated path.
        /// </summary>
        private async Task RaceShowWindowWithSelectionAsync(
            Action showEmpty,
            Action<string> showWithText)
        {
            var dispatcher = _window?.DispatcherQueue;
            if (dispatcher is null) return;

            var textTask = TextSelectionService.GetSelectedTextAsync();
            var winner = await Task.WhenAny(textTask, Task.Delay(SelectionFastPathBudgetMs))
                .ConfigureAwait(false);

            if (winner == textTask)
            {
                string? text = null;
                try { text = await textTask.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Hotkey] GetSelectedTextAsync failed (fast path): {ex.Message}");
                }

                dispatcher.TryEnqueue(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text)) showWithText(text);
                    else showEmpty();
                });
                return;
            }

            // Slow path: open the window now so it appears immediately, then overwrite with
            // the selected text once the selection fetch completes. Wrap in a lambda —
            // DispatcherQueue.TryEnqueue takes a DispatcherQueueHandler, not an Action,
            // and the two delegate types don't implicitly convert even with matching
            // signatures.
            dispatcher.TryEnqueue(() => showEmpty());

            // Fire-and-forget continuation. Exceptions are already logged inside
            // GetSelectedTextAsync; guard with status check anyway.
            _ = textTask.ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                var text = t.Result;
                if (string.IsNullOrWhiteSpace(text)) return;
                dispatcher.TryEnqueue(() => showWithText(text));
            }, TaskScheduler.Default);
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
            var ocrService = EnsureOcrTranslateService();
            if (ocrService is null)
            {
                System.Diagnostics.Debug.WriteLine("[Hotkey] OCR service not available");
                return;
            }

            try
            {
                // Wait briefly for the user to release physical keys and for the mouse drag selection to finalize in the OS.
                await Task.Delay(150);

                await ocrService.OcrTranslateAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] OnOcrTranslateHotkey error: {ex.Message}");
            }
        }

        private async void OnSilentOcrHotkey()
        {
            var ocrService = EnsureOcrTranslateService();
            if (ocrService is null)
            {
                System.Diagnostics.Debug.WriteLine("[Hotkey] OCR service not available");
                return;
            }

            try
            {
                // Wait briefly for the user to release physical keys and for the mouse drag selection to finalize in the OS.
                await Task.Delay(150);

                await ocrService.SilentOcrAsync();
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
            var ocrService = EnsureOcrTranslateService();
            if (ocrService is null)
            {
                System.Diagnostics.Debug.WriteLine("[Tray] OCR service not available");
                return;
            }

            try
            {
                await ocrService.OcrTranslateAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Tray] OnTrayOcrTranslate error: {ex.Message}");
            }
        }

        // Lazily instantiate OcrTranslateService on first use. Must run on UI thread because
        // OcrTranslateService captures DispatcherQueue. Called from all OCR entry points
        // (hotkeys, tray menu, shell context menu signal, browser extension signal,
        // protocol activation).
        private OcrTranslateService? EnsureOcrTranslateService()
        {
            if (_ocrTranslateService != null) return _ocrTranslateService;
            if (_window == null) return null;
            try
            {
                _ocrTranslateService ??= new OcrTranslateService(_window.DispatcherQueue);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] OcrTranslateService lazy init failed: {ex}");
            }
            return _ocrTranslateService;
        }

        private void QueueHotkeyRegistrationWarning(IReadOnlyList<HotkeyRegistrationFailure> failures)
        {
            if (failures.Count == 0)
            {
                return;
            }

            var dispatcher = _window?.DispatcherQueue;
            if (dispatcher == null)
            {
                return;
            }

            _ = dispatcher.TryEnqueue(async () =>
            {
                await Task.Delay(700);

                if (_window?.Content is not FrameworkElement root || root.XamlRoot is null)
                {
                    return;
                }

                var loc = LocalizationService.Instance;
                var lines = failures
                    .Select(f => $"{loc.GetString(f.NameKey)}: {f.HotkeyString}")
                    .Distinct()
                    .ToList();

                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = loc.GetString("HotkeyRegistrationFailedTitle"),
                    Content = loc.GetString("HotkeyRegistrationFailedMessage")
                        + "\n\n"
                        + string.Join("\n", lines),
                    CloseButtonText = loc.GetString("OK"),
                    XamlRoot = root.XamlRoot
                };

                try
                {
                    await dialog.ShowAsync();
                }
                catch (COMException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] Hotkey warning dialog failed: {ex.Message}");
                }
            });
        }

        private void TriggerStartupOcrTranslateIfNeeded()
        {
            // If cold-launched via protocol activation (easydict://ocr-translate) or
            // --ocr-translate when app wasn't running, trigger OCR after initialization.
            if (Program.PendingOcrTranslate)
            {
                QueueOcrTranslate("PendingOcrTranslate", delayMs: 500);
            }

            DrainRedirectedOcrTranslateIfReady(delayMs: 500);
        }

        private void DrainRedirectedOcrTranslateIfReady(int delayMs = 0)
        {
            if (!_servicesInitialized)
            {
                return;
            }

            if (Interlocked.Exchange(ref _pendingRedirectedOcrTranslate, 0) != 0)
            {
                QueueOcrTranslate("RedirectedActivation", delayMs);
            }
        }

        private void QueueOcrTranslate(string source, int delayMs = 0)
        {
            var dispatcher = _window?.DispatcherQueue;
            if (dispatcher == null)
            {
                System.Diagnostics.Debug.WriteLine($"[App] {source}: dispatcher unavailable");
                return;
            }

            var enqueued = dispatcher.TryEnqueue(async () =>
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }

                try
                {
                    var ocrService = EnsureOcrTranslateService();
                    if (ocrService != null)
                    {
                        await ocrService.OcrTranslateAsync();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] {source} error: {ex.Message}");
                }
            });

            if (!enqueued)
            {
                System.Diagnostics.Debug.WriteLine($"[App] {source}: failed to enqueue OCR action");
            }
        }

        /// <summary>
        /// Public entry point for the OCR quick-action button in the translation windows.
        /// Runs the capture → OCR → translate flow on the UI thread (issue #172).
        /// </summary>
        public static Task TriggerOcrTranslateAsync()
        {
            var app = Instance;
            var dispatcher = app._window?.DispatcherQueue;
            if (dispatcher == null)
            {
                return Task.CompletedTask;
            }

            var enqueued = dispatcher.TryEnqueue(async () =>
            {
                var ocrService = app.EnsureOcrTranslateService();
                if (ocrService == null)
                {
                    System.Diagnostics.Debug.WriteLine("[App] OCR service not available for button trigger");
                    return;
                }

                try
                {
                    await ocrService.OcrTranslateAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] TriggerOcrTranslateAsync error: {ex.Message}");
                }
            });

            if (!enqueued)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[App] TriggerOcrTranslateAsync: failed to enqueue OCR action (dispatcher unavailable)");
            }

            return Task.CompletedTask;
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

        /// <summary>
        /// Handles an activation that was redirected here from a second launch (single-instance).
        /// Marshals to the UI thread, surfaces the existing window, and replays OCR intents that
        /// arrived before the named-event listener was ready. Called from <see cref="Program"/>
        /// on the primary instance's <c>Activated</c> event, which fires on a background thread.
        /// </summary>
        internal static void HandleRedirectedActivation(AppActivationArguments activationArgs)
        {
            var shouldTriggerOcr = Program.IsOcrTranslateActivation(activationArgs);
            if (shouldTriggerOcr)
            {
                Interlocked.Exchange(ref _pendingRedirectedOcrTranslate, 1);
            }

            if (Current is not App app)
            {
                return;
            }

            var dispatcher = app._window?.DispatcherQueue;
            dispatcher?.TryEnqueue(() =>
            {
                app.ShowAndActivateWindow();
                app.DrainRedirectedOcrTranslateIfReady();
            });
        }

        private void ShowAndActivateWindow()
        {
            if (_window == null) return;

            EnsureMainPageContent();

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

        private void EnsureMainPageContent()
        {
            var frame = EnsureRootFrame();
            if (frame is null)
            {
                return;
            }

            if (frame.Content is null)
            {
                _ = frame.Navigate(typeof(MainPage));
            }
        }

        private Frame? EnsureRootFrame()
        {
            if (_window is null)
            {
                return null;
            }

            if (_window.Content is Frame frame)
            {
                return frame;
            }

            var rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            rootFrame.Navigated += OnRootFrameNavigated;
            _window.Content = rootFrame;
            return rootFrame;
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
            TextToSpeechService.StopIfInitialized();
            _appWindow?.Hide();
            ReleaseMainWindowContentForMemoryGate();
        }

        private void ReleaseMainWindowContentForMemoryGate()
        {
            if (!IsMemoryAbVariantB())
            {
                return;
            }

            if (_window?.Content is not Frame frame)
            {
                CrashDiagnostics.Log($"[MemoryGate] Root frame release skipped: content={_window?.Content?.GetType().FullName ?? "<null>"}");
                return;
            }

            frame.NavigationFailed -= OnNavigationFailed;
            frame.Navigated -= OnRootFrameNavigated;
            frame.BackStack.Clear();
            frame.ForwardStack.Clear();
            frame.Content = null;
            _window.Content = null;
            CrashDiagnostics.Log("[MemoryGate] Root frame released after hiding main window");
        }

        private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            try
            {
                SaveWindowDimensions();
            }
            catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
            {
                CrashDiagnostics.LogException(
                    "App.OnWindowClosing.SaveWindowDimensions",
                    ex,
                    isTerminating: false,
                    isHandled: true);
            }

            var minimizeToTray = false;
            try
            {
                minimizeToTray = SettingsService.Instance.MinimizeToTray;
            }
            catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
            {
                CrashDiagnostics.LogException(
                    "App.OnWindowClosing.ReadMinimizeToTray",
                    ex,
                    isTerminating: false,
                    isHandled: true);
            }

            if (minimizeToTray && !_isSystemShutdownRequested)
            {
                CrashDiagnostics.Log($"[WindowClosing] MinimizeToTray=True, memoryAbB={IsMemoryAbVariantB()}");
                args.Cancel = true;

                try
                {
                    HideWindow();
                }
                catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
                {
                    CrashDiagnostics.LogException(
                        "App.OnWindowClosing.HideWindow",
                        ex,
                        isTerminating: false,
                        isHandled: true);
                    args.Cancel = false;
                }

                return;
            }

            args.Cancel = false;
            CrashDiagnostics.Log(
                $"[WindowClosing] Closing, shutdownRequested={_isSystemShutdownRequested}, memoryAbB={IsMemoryAbVariantB()}");
            try
            {
                CleanupServices();
            }
            catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
            {
                CrashDiagnostics.LogException(
                    "App.OnWindowClosing.CleanupServices",
                    ex,
                    isTerminating: false,
                    isHandled: true);
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

        public static void CleanupServices()
        {
            var app = Instance;

            // Dispose OCR signal event first — this unblocks the listener thread's WaitOne()
            // which throws ObjectDisposedException, causing the thread to exit gracefully.
            app._ocrSignalEvent?.Dispose();
            app._ocrSignalEvent = null;

            // Wait briefly for the signal thread to finish to avoid races during teardown
            if (app._ocrSignalThread?.IsAlive == true)
            {
                app._ocrSignalThread.Join(TimeSpan.FromSeconds(2));
            }
            app._ocrSignalThread = null;

            app._mouseHookService?.Dispose();
            app._popButtonService?.Dispose();
            app._clipboardService?.Dispose();
            app._hotkeyService?.Dispose();
            app._trayIconService?.Dispose();
            FixedWindowService.Instance.Dispose();
            MiniWindowService.Instance.Dispose();
            TextToSpeechService.StopIfInitialized();
            app.UnregisterSystemMessageHandlers();
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
        /// <param name="theme">Theme name: "System", "Light", "Dark", or "Minimal"</param>
        public static void ApplyTheme(string theme)
        {
            var isMinimal = MinimalThemeService.IsMinimal(theme);
            var wasMinimalResourcesApplied = MinimalThemeService.ResourcesApplied;
            MinimalThemeService.ApplyResources(isMinimal);
            var forceThemeResourceRefresh = wasMinimalResourcesApplied != isMinimal;
            if (IsSystemTheme(theme))
            {
                Instance._lastSystemDark = SystemThemeProbe.IsSystemDark();
            }

            var elementTheme = MinimalThemeService.ToElementTheme(theme);
            ApplyMainWindowTitleBarChrome(theme, elementTheme);

            if (Instance._window?.Content is Frame frame)
            {
                ApplyFrameTheme(frame, theme, forceThemeResourceRefresh);
            }
            else if (Instance._window?.Content is FrameworkElement mainRoot)
            {
                MinimalThemeService.ApplyRequestedTheme(
                    mainRoot,
                    elementTheme,
                    forceThemeResourceRefresh);
            }

            MiniWindowService.Instance.ApplyTheme(elementTheme, forceThemeResourceRefresh);
            FixedWindowService.Instance.ApplyTheme(elementTheme, forceThemeResourceRefresh);
            Instance._popButtonService?.ApplyTheme(elementTheme, forceThemeResourceRefresh);

            System.Diagnostics.Debug.WriteLine($"[App] Applied theme: {theme} (ElementTheme.{elementTheme})");
        }

        /// <summary>
        /// Re-apply appearance settings (result font size) and quick-action button
        /// visibility to all windows. Mirrors the <see cref="ApplyTheme"/> fan-out (issue #172).
        /// </summary>
        public static void ApplyAppearance()
        {
            if (Instance._window?.Content is Frame frame && frame.Content is MainPage mainPage)
            {
                mainPage.ApplyAppearance();
            }

            MiniWindowService.Instance.ApplyAppearance();
            FixedWindowService.Instance.ApplyAppearance();
        }

        private static bool IsSystemTheme(string? theme) =>
            string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase);

        private void RegisterSystemMessageHandlers()
        {
            SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
            SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
            _lastSystemDark = SystemThemeProbe.IsSystemDark();

            if (_appWindowSubclassProc is not null || _window is null)
            {
                return;
            }

            _appWindowSubclassHwnd = WindowNative.GetWindowHandle(_window);
            if (_appWindowSubclassHwnd == 0)
            {
                return;
            }

            _appWindowSubclassProc = AppWindowSubclassWndProc;
            if (!SetWindowSubclass(
                    _appWindowSubclassHwnd,
                    _appWindowSubclassProc,
                    AppWindowSubclassId,
                    0))
            {
                _appWindowSubclassProc = null;
                _appWindowSubclassHwnd = 0;
            }
        }

        private void UnregisterSystemMessageHandlers()
        {
            SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
            if (_appWindowSubclassProc is not null && _appWindowSubclassHwnd != 0)
            {
                RemoveWindowSubclass(
                    _appWindowSubclassHwnd,
                    _appWindowSubclassProc,
                    AppWindowSubclassId);
            }

            _appWindowSubclassProc = null;
            _appWindowSubclassHwnd = 0;
            _systemThemeRefreshQueued = 0;
        }

        private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
            => QueueSystemThemeRefresh();

        private nint AppWindowSubclassWndProc(
            nint hWnd,
            uint uMsg,
            nint wParam,
            nint lParam,
            nuint uIdSubclass,
            nuint dwRefData)
        {
            try
            {
                switch (uMsg)
                {
                    case WM_QUERYENDSESSION:
                        if (Interlocked.Exchange(ref _sessionEndQueryLogged, 1) == 0)
                        {
                            CrashDiagnostics.Log("[AppWindow] WM_QUERYENDSESSION accepted");
                        }

                        return 1;

                    case WM_ENDSESSION when wParam == 0:
                        _isSystemShutdownRequested = false;
                        CrashDiagnostics.Log("[AppWindow] WM_ENDSESSION cancelled");
                        return 0;

                    case WM_ENDSESSION:
                        _isSystemShutdownRequested = true;
                        CrashDiagnostics.Log(
                            $"[AppWindow] WM_ENDSESSION confirmed, reasonFlags=0x{unchecked((nuint)lParam):X}");
                        try
                        {
                            Application.Current.Exit();
                        }
                        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
                        {
                            CrashDiagnostics.LogException(
                                "AppWindowSubclassWndProc.Application.Exit",
                                ex,
                                isTerminating: false,
                                isHandled: true);
                            Environment.Exit(0);
                        }

                        return 0;

                    case WM_SETTINGCHANGE:
                    case WM_THEMECHANGED:
                        QueueSystemThemeRefresh();
                        break;
                }
            }
            catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
            {
                CrashDiagnostics.LogException(
                    "AppWindowSubclassWndProc",
                    ex,
                    isTerminating: false,
                    isHandled: true);
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void QueueSystemThemeRefresh()
        {
            if (!IsSystemTheme(SettingsService.Instance.AppTheme))
            {
                return;
            }

            if (Interlocked.Exchange(ref _systemThemeRefreshQueued, 1) == 1)
            {
                return;
            }

            var dispatcherQueue = _window?.DispatcherQueue;
            if (dispatcherQueue is null ||
                !dispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    RefreshSystemThemeIfChanged))
            {
                _systemThemeRefreshQueued = 0;
            }
        }

        private void RefreshSystemThemeIfChanged()
        {
            _systemThemeRefreshQueued = 0;

            if (!IsSystemTheme(SettingsService.Instance.AppTheme))
            {
                return;
            }

            var currentSystemDark = SystemThemeProbe.IsSystemDark();
            if (currentSystemDark == _lastSystemDark)
            {
                return;
            }

            _lastSystemDark = currentSystemDark;
            ApplyTheme(SettingsService.Instance.AppTheme);
        }

        private static void ApplyFrameTheme(
            Frame frame,
            string theme,
            bool forceThemeResourceRefresh,
            bool deferContentChrome = false)
        {
            var elementTheme = MinimalThemeService.ToElementTheme(theme);
            MinimalThemeService.ApplyRequestedTheme(
                frame,
                elementTheme,
                forceThemeResourceRefresh);

            if (deferContentChrome)
            {
                QueueFrameContentThemeRefresh(frame);
                return;
            }

            ApplyFrameContentTheme(frame, elementTheme, forceThemeResourceRefresh);
            RefreshFrameContentThemeChrome(frame);
            QueueFrameContentThemeRefresh(frame);
        }

        private static void QueueFrameContentThemeRefresh(Frame frame)
        {
            frame.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    var currentTheme = SettingsService.Instance.AppTheme;
                    var currentElementTheme = MinimalThemeService.ToElementTheme(currentTheme);
                    ApplyFrameContentTheme(frame, currentElementTheme, forceThemeResourceRefresh: false);
                    RefreshFrameContentThemeChrome(frame);
                });
        }

        private static void ApplyFrameContentTheme(
            Frame frame,
            ElementTheme elementTheme,
            bool forceThemeResourceRefresh)
        {
            if (frame.Content is not FrameworkElement pageRoot)
            {
                return;
            }

            MinimalThemeService.ApplyRequestedTheme(
                pageRoot,
                elementTheme,
                forceThemeResourceRefresh);
        }

        private static void RefreshFrameContentThemeChrome(Frame frame)
        {
            switch (frame.Content)
            {
                case MainPage mainPage:
                    mainPage.ApplyThemeChrome();
                    break;
                case SettingsPage settingsPage:
                    settingsPage.ApplyThemeChrome();
                    break;
            }
        }

        private static void ApplyMainWindowTitleBarChrome(string theme, ElementTheme elementTheme)
        {
            if (Instance._appWindow is null)
            {
                return;
            }

            try
            {
                var titleBar = Instance._appWindow.TitleBar;

                // Defer to system colors when:
                //  - Minimal mode (we suppress custom title bar painting),
                //  - Element theme is Default (system theme — let WinUI track it),
                //  - High Contrast is active (must respect accessibility palette).
                if (MinimalThemeService.IsMinimal(theme) ||
                    elementTheme == ElementTheme.Default ||
                    ThemeResourceService.IsHighContrastActive())
                {
                    ResetTitleBarColors(titleBar);
                    return;
                }

                var themeName = elementTheme == ElementTheme.Dark ? "Dark" : "Light";
                var background = ResolveTitleBarColor(
                    "TitleBarBackgroundColor",
                    themeName,
                    "FloatingWindowBackgroundColor");
                var foreground = ResolveTitleBarColor(
                    "TitleBarForegroundColor",
                    themeName,
                    "QueryTextColor");
                var inactiveForeground = ResolveTitleBarColor(
                    "TitleBarInactiveForegroundColor",
                    themeName,
                    "ServiceResultHeaderSecondaryForegroundColor");
                var buttonHoverBackground = ResolveTitleBarColor(
                    "TitleBarButtonHoverBackgroundColor",
                    themeName,
                    "ServiceResultHeaderHoverBackgroundColor");
                var buttonPressedBackground = ResolveTitleBarColor(
                    "TitleBarButtonPressedBackgroundColor",
                    themeName,
                    "ServiceResultHeaderHoverBackgroundColor");

                titleBar.BackgroundColor = background;
                titleBar.ForegroundColor = foreground;
                titleBar.InactiveBackgroundColor = background;
                titleBar.InactiveForegroundColor = inactiveForeground;
                titleBar.ButtonBackgroundColor = background;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = buttonHoverBackground;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = buttonPressedBackground;
                titleBar.ButtonPressedForegroundColor = foreground;
                titleBar.ButtonInactiveBackgroundColor = background;
                titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Apply title bar theme failed: {ex.Message}");
            }
        }

        private static Windows.UI.Color ResolveTitleBarColor(
            string key,
            string themeName,
            string fallbackKey)
        {
            if (ThemeResourceService.TryGetResource<Windows.UI.Color>(key, themeName, out var color))
            {
                return color;
            }

            if (ThemeResourceService.TryGetResource<Windows.UI.Color>(fallbackKey, themeName, out var fallbackColor))
            {
                return fallbackColor;
            }

            return default;
        }

        private static void ResetTitleBarColors(AppWindowTitleBar titleBar)
        {
            titleBar.BackgroundColor = null;
            titleBar.ForegroundColor = null;
            titleBar.InactiveBackgroundColor = null;
            titleBar.InactiveForegroundColor = null;
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
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

        private void OnRootFrameNavigated(object sender, NavigationEventArgs e)
        {
            if (sender is Frame frame)
            {
                var deferContentChrome = e.NavigationMode == NavigationMode.Back && frame.Content is MainPage;
                ApplyFrameTheme(
                    frame,
                    SettingsService.Instance.AppTheme,
                    forceThemeResourceRefresh: false,
                    deferContentChrome);
            }
        }
    }
}
