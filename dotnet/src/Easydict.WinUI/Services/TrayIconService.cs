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

        // Use icon from resources; fall back to unpackaged path when running F5 without package identity.
        _taskbarIcon.IconSource = CreateTrayIconSource();

        // Force create the tray icon when created programmatically (not via XAML).
        // This is required by H.NotifyIcon for the icon to appear in the system tray.
        _taskbarIcon.ForceCreate();
    }

    private static ImageSource CreateTrayIconSource()
    {
        const string relativeIconPath = "Assets\\Square44x44Logo.targetsize-24_altform-unplated.png";

        // Unpackaged (F5): ms-appx is often unavailable; prefer absolute file path.
        if (!IsPackagedApp())
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, relativeIconPath);
            return new BitmapImage(new Uri(filePath));
        }

        // Packaged: use ms-appx.
        return new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-24_altform-unplated.png"));
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

    private MenuFlyout CreateContextMenu()
    {
        var menu = new MenuFlyout();

        var showItem = new MenuFlyoutItem { Text = "Show Easydict" };
        showItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(showItem);

        var translateItem = new MenuFlyoutItem { Text = "Translate Clipboard" };
        translateItem.Click += (_, _) => OnTranslateClipboard?.Invoke();
        menu.Items.Add(translateItem);

        var miniWindowItem = new MenuFlyoutItem { Text = "Mini Window (Ctrl+Alt+M)" };
        miniWindowItem.Click += (_, _) => MiniWindowService.Instance.Toggle();
        menu.Items.Add(miniWindowItem);

        var fixedWindowItem = new MenuFlyoutItem { Text = "Fixed Window (Ctrl+Alt+F)" };
        fixedWindowItem.Click += (_, _) => FixedWindowService.Instance.Toggle();
        menu.Items.Add(fixedWindowItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var settingsItem = new MenuFlyoutItem { Text = "Settings" };
        settingsItem.Click += (_, _) => OnOpenSettings?.Invoke();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        return menu;
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
