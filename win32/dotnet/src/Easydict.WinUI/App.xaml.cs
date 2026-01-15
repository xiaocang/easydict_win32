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
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            _window = new Window();

            // Set window title
            _window.Title = "Easydict";

            // Set window size and get AppWindow reference
            _appWindow = ConfigureWindow(_window);

            // Handle window close to minimize to tray instead
            _appWindow.Closing += OnWindowClosing;

            if (_window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                _window.Content = rootFrame;
            }

            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            _window.Activate();

            // Initialize services
            InitializeServices();
        }

        private void InitializeServices()
        {
            if (_window == null) return;

            var settings = SettingsService.Instance;

            // Initialize system tray icon
            _trayIconService = new TrayIconService(_window);
            _trayIconService.OnTranslateClipboard += OnTrayTranslateClipboard;
            _trayIconService.OnOpenSettings += OnTrayOpenSettings;
            _trayIconService.Initialize();

            // Initialize hotkey service
            _hotkeyService = new HotkeyService(_window);
            _hotkeyService.OnShowWindow += OnShowWindowHotkey;
            _hotkeyService.OnTranslateSelection += OnTranslateSelectionHotkey;
            _hotkeyService.Initialize();

            // Initialize clipboard service
            _clipboardService = new ClipboardService();
            _clipboardService.OnClipboardTextChanged += OnClipboardTextChanged;
            _clipboardService.IsMonitoringEnabled = settings.ClipboardMonitoring;


            // Apply always-on-top setting
            ApplyAlwaysOnTop(settings.AlwaysOnTop);
        }

        private void OnShowWindowHotkey()
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                ShowAndActivateWindow();
            });
        }

        private async void OnTranslateSelectionHotkey()
        {
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

        private void CleanupServices()
        {
            _clipboardService?.Dispose();
            _hotkeyService?.Dispose();
            _trayIconService?.Dispose();
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

        private static AppWindow ConfigureWindow(Window window)
        {
            var hWnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            // Set initial size (width: 600, height: 700)
            appWindow.Resize(new Windows.Graphics.SizeInt32(600, 700));

            // Center on screen
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            if (displayArea is not null)
            {
                var centerX = (displayArea.WorkArea.Width - 600) / 2;
                var centerY = (displayArea.WorkArea.Height - 700) / 2;
                appWindow.Move(new Windows.Graphics.PointInt32(centerX, centerY));
            }

            return appWindow;
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
