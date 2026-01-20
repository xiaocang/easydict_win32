using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages the system tray icon for the application.
/// Provides minimize to tray, context menu, and restore functionality.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
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

    public TrayIconService(Window window)
    {
        _window = window;
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

        // Use icon from resources (Windows automatically selects appropriate scale variant)
        _taskbarIcon.IconSource = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.png"));
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

