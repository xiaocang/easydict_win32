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

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            _window = new Window();

            // Set window title
            _window.Title = "Easydict";

            // Set window size
            ConfigureWindow(_window);

            if (_window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                _window.Content = rootFrame;
            }

            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            _window.Activate();
        }

        private static void ConfigureWindow(Window window)
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
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
