using System.Diagnostics;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages the system tray icon for the application.
/// Provides minimize to tray, context menu, and restore functionality.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private readonly AppWindow? _appWindow;
    private TaskbarIcon? _taskbarIcon;
    private bool _isDisposed;

    /// <summary>
    /// Event fired when "Translate Clipboard" is clicked.
    /// </summary>
    public event Action? OnTranslateClipboard;

    /// <summary>
    /// Event fired when "Settings" is clicked.
    /// </summary>
    public event Action? OnOpenSettings;

    /// <summary>
    /// Event fired when "OCR Translate" is clicked.
    /// </summary>
    public event Action? OnOcrTranslate;

    /// <summary>
    /// Event fired when a browser support install/uninstall action is triggered.
    /// Parameter: ("chrome"|"firefox"|"all", isInstall)
    /// </summary>
    public event Action<string, bool>? OnBrowserSupportAction;

    // Browser support menu items — stored for dynamic enable/disable updates
    private MenuFlyoutItem? _installChromeItem;
    private MenuFlyoutItem? _uninstallChromeItem;
    private MenuFlyoutItem? _installFirefoxItem;
    private MenuFlyoutItem? _uninstallFirefoxItem;
    private MenuFlyoutItem? _installAllItem;
    private MenuFlyoutItem? _uninstallAllItem;

    public TrayIconService(Window window, AppWindow? appWindow)
    {
        _window = window;
        _appWindow = appWindow;
    }

    /// <summary>
    /// Initialize and show the tray icon.
    /// </summary>
    public void Initialize()
    {
        if (_taskbarIcon != null) return;

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Easydict - Dictionary & Translation",
            ContextMenuMode = ContextMenuMode.SecondWindow
        };

        // Set up context menu
        var contextMenu = CreateContextMenu();
        _taskbarIcon.ContextFlyout = contextMenu;

        // Handle left click to show window
        _taskbarIcon.LeftClickCommand = new RelayCommand(ShowWindow);

        // Use .ico file for tray icon - this is more reliable than BitmapImage with H.NotifyIcon
        var iconPath = GetTrayIconPath();
        if (!string.IsNullOrEmpty(iconPath))
        {
            try
            {
                _taskbarIcon.Icon = new System.Drawing.Icon(iconPath);
                Debug.WriteLine($"[TrayIcon] Loaded icon from: {iconPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayIcon] Failed to load icon: {ex.Message}");
                // Fall back to IconSource with BitmapImage
                _taskbarIcon.IconSource = CreateTrayIconSource();
            }
        }
        else
        {
            // Fall back to IconSource with BitmapImage
            _taskbarIcon.IconSource = CreateTrayIconSource();
        }

        // Force create the tray icon when created programmatically (not via XAML).
        // This is required by H.NotifyIcon for the icon to appear in the system tray.
        _taskbarIcon.ForceCreate();

        Debug.WriteLine($"[TrayIcon] Initialized. Icon property set: {_taskbarIcon.Icon != null}, IconSource set: {_taskbarIcon.IconSource != null}");
    }

    /// <summary>
    /// Get the path to the tray icon (.ico file preferred, .png as fallback).
    /// </summary>
    private static string? GetTrayIconPath()
    {
        var baseDir = AppContext.BaseDirectory;
        Debug.WriteLine($"[TrayIcon] BaseDirectory: {baseDir}");

        // Primary: AppIcon.ico (most reliable for system tray)
        var icoPath = Path.Combine(baseDir, "AppIcon.ico");
        if (File.Exists(icoPath))
        {
            Debug.WriteLine($"[TrayIcon] Found AppIcon.ico at: {icoPath}");
            return icoPath;
        }

        Debug.WriteLine($"[TrayIcon] AppIcon.ico not found at: {icoPath}");
        return null;
    }

    private static ImageSource CreateTrayIconSource()
    {
        // Primary path: TrayIcon.png from application directory (unpackaged builds)
        // This file is generated at build time from the same source as AppIcon.ico
        const string trayIconFileName = "TrayIcon.png";
        var trayIconPath = Path.Combine(AppContext.BaseDirectory, trayIconFileName);

        if (File.Exists(trayIconPath))
        {
            try
            {
                var bitmap = new BitmapImage(new Uri(trayIconPath));
                Debug.WriteLine($"[TrayIcon] Loaded from primary: {trayIconPath}");
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayIcon] Failed to load from primary: {ex.Message}");
            }
        }

        // Fallback 1: Try Assets folder with ms-appx URI (packaged builds)
        if (IsPackagedApp())
        {
            try
            {
                var uri = new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-24_altform-unplated.png");
                var bitmap = new BitmapImage(uri);
                Debug.WriteLine("[TrayIcon] Loaded from packaged assets (ms-appx)");
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayIcon] Failed to load from ms-appx: {ex.Message}");
            }
        }

        // Fallback 2: Try Assets folder with file path (unpackaged/F5 debug)
        const string relativeIconPath = "Assets\\Square44x44Logo.targetsize-24_altform-unplated.png";
        var fallbackPath = Path.Combine(AppContext.BaseDirectory, relativeIconPath);

        if (File.Exists(fallbackPath))
        {
            try
            {
                var bitmap = new BitmapImage(new Uri(fallbackPath));
                Debug.WriteLine($"[TrayIcon] Loaded from fallback: {fallbackPath}");
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayIcon] Failed to load from fallback: {ex.Message}");
            }
        }

        // All loading attempts failed - return a placeholder or null
        Debug.WriteLine("[TrayIcon] All icon loading attempts failed, tray icon will use system default");

        // Return a simple bitmap to avoid null reference - it will show as a default icon
        return new BitmapImage();
    }

    private static bool IsPackagedApp()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string L(string key) => LocalizationService.Instance.GetString(key);

    /// <summary>
    /// Set a tooltip on a MenuFlyoutItem so the full text is visible on hover.
    /// </summary>
    private static void SetTip(MenuFlyoutItem item) =>
        ToolTipService.SetToolTip(item, item.Text);

    /// <summary>
    /// Set a tooltip on a MenuFlyoutSubItem so the full text is visible on hover.
    /// </summary>
    private static void SetTip(MenuFlyoutSubItem item) =>
        ToolTipService.SetToolTip(item, item.Text);

    private MenuFlyout CreateContextMenu()
    {
        var menu = new MenuFlyout();

        // Set MinWidth to fit the longest menu item text
        // This ensures proper width on first open (H.NotifyIcon SecondWindow mode quirk)
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 300d));
        menu.MenuFlyoutPresenterStyle = presenterStyle;

        var showItem = new MenuFlyoutItem { Text = L("TrayShow") };
        showItem.Click += (_, _) => ShowWindow();
        SetTip(showItem);
        menu.Items.Add(showItem);

        var translateItem = new MenuFlyoutItem { Text = L("TrayTranslateClipboard") };
        translateItem.Click += (_, _) => OnTranslateClipboard?.Invoke();
        SetTip(translateItem);
        menu.Items.Add(translateItem);

        var ocrItem = new MenuFlyoutItem { Text = $"{L("TrayOcrTranslate")} (Ctrl+Alt+S)" };
        ocrItem.Click += (_, _) => OnOcrTranslate?.Invoke();
        SetTip(ocrItem);
        menu.Items.Add(ocrItem);

        var miniWindowItem = new MenuFlyoutItem { Text = $"{L("TrayShowMini")} (Ctrl+Alt+M)" };
        miniWindowItem.Click += (_, _) => MiniWindowService.Instance.Toggle();
        SetTip(miniWindowItem);
        menu.Items.Add(miniWindowItem);

        var fixedWindowItem = new MenuFlyoutItem { Text = $"{L("TrayShowFixed")} (Ctrl+Alt+F)" };
        fixedWindowItem.Click += (_, _) => FixedWindowService.Instance.Toggle();
        SetTip(fixedWindowItem);
        menu.Items.Add(fixedWindowItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Browser support submenu
        menu.Items.Add(CreateBrowserSupportSubmenu());

        var settingsItem = new MenuFlyoutItem { Text = L("TraySettings") };
        settingsItem.Click += (_, _) => OnOpenSettings?.Invoke();
        SetTip(settingsItem);
        menu.Items.Add(settingsItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = L("TrayExit") };
        exitItem.Click += (_, _) => ExitApplication();
        SetTip(exitItem);
        menu.Items.Add(exitItem);

        return menu;
    }

    private MenuFlyoutSubItem CreateBrowserSupportSubmenu()
    {
        var browserMenu = new MenuFlyoutSubItem { Text = L("TrayBrowserSupport") };
        SetTip(browserMenu);

        // Chrome
        var chromeGroup = new MenuFlyoutSubItem { Text = L("TrayBrowserChrome") };
        SetTip(chromeGroup);

        _installChromeItem = new MenuFlyoutItem { Text = L("TrayBrowserInstallChrome") };
        _installChromeItem.Click += (_, _) => OnBrowserSupportAction?.Invoke("chrome", true);
        SetTip(_installChromeItem);
        chromeGroup.Items.Add(_installChromeItem);

        _uninstallChromeItem = new MenuFlyoutItem { Text = L("TrayBrowserUninstallChrome") };
        _uninstallChromeItem.Click += (_, _) => OnBrowserSupportAction?.Invoke("chrome", false);
        SetTip(_uninstallChromeItem);
        chromeGroup.Items.Add(_uninstallChromeItem);

        browserMenu.Items.Add(chromeGroup);

        // Firefox
        var firefoxGroup = new MenuFlyoutSubItem { Text = L("TrayBrowserFirefox") };
        SetTip(firefoxGroup);

        _installFirefoxItem = new MenuFlyoutItem { Text = L("TrayBrowserInstallFirefox") };
        _installFirefoxItem.Click += (_, _) => OnBrowserSupportAction?.Invoke("firefox", true);
        SetTip(_installFirefoxItem);
        firefoxGroup.Items.Add(_installFirefoxItem);

        _uninstallFirefoxItem = new MenuFlyoutItem { Text = L("TrayBrowserUninstallFirefox") };
        _uninstallFirefoxItem.Click += (_, _) => OnBrowserSupportAction?.Invoke("firefox", false);
        SetTip(_uninstallFirefoxItem);
        firefoxGroup.Items.Add(_uninstallFirefoxItem);

        browserMenu.Items.Add(firefoxGroup);

        browserMenu.Items.Add(new MenuFlyoutSeparator());

        _installAllItem = new MenuFlyoutItem { Text = L("TrayBrowserInstallAll") };
        _installAllItem.Click += (_, _) => OnBrowserSupportAction?.Invoke("all", true);
        SetTip(_installAllItem);
        browserMenu.Items.Add(_installAllItem);

        _uninstallAllItem = new MenuFlyoutItem { Text = L("TrayBrowserUninstallAll") };
        _uninstallAllItem.Click += (_, _) => OnBrowserSupportAction?.Invoke("all", false);
        SetTip(_uninstallAllItem);
        browserMenu.Items.Add(_uninstallAllItem);

        // Set initial enabled states
        UpdateBrowserSupportMenuStates();

        return browserMenu;
    }

    /// <summary>
    /// Refresh the enabled/disabled state of browser support menu items
    /// based on current installation status. Call after install/uninstall actions.
    /// </summary>
    public void UpdateBrowserSupportMenuStates()
    {
        var chromeInstalled = BrowserSupportService.IsChromeSupportInstalled;
        var firefoxInstalled = BrowserSupportService.IsFirefoxSupportInstalled;

        if (_installChromeItem != null)
            _installChromeItem.IsEnabled = !chromeInstalled;
        if (_uninstallChromeItem != null)
            _uninstallChromeItem.IsEnabled = chromeInstalled;

        if (_installFirefoxItem != null)
            _installFirefoxItem.IsEnabled = !firefoxInstalled;
        if (_uninstallFirefoxItem != null)
            _uninstallFirefoxItem.IsEnabled = firefoxInstalled;

        var anyNotInstalled = !chromeInstalled || !firefoxInstalled;
        var anyInstalled = chromeInstalled || firefoxInstalled;

        if (_installAllItem != null)
            _installAllItem.IsEnabled = anyNotInstalled;
        if (_uninstallAllItem != null)
            _uninstallAllItem.IsEnabled = anyInstalled;
    }

    /// <summary>
    /// Show and activate the main window.
    /// </summary>
    public void ShowWindow()
    {
        // If the window was hidden via AppWindow.Hide(), Activate() alone won't restore it.
        _appWindow?.Show();
        _window.Activate();
    }

    /// <summary>
    /// Exit the application completely.
    /// </summary>
    public void ExitApplication()
    {
        Dispose();
        Application.Current.Exit();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
    }
}

/// <summary>
/// Simple relay command for TaskbarIcon commands.
/// </summary>
internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute)
    {
        _execute = execute;
    }

#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
