using System.Drawing;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages the system tray icon for the application.
/// Provides minimize to tray, context menu, and restore functionality.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private TrayIcon? _trayIcon;
    private bool _isDisposed;

    public TrayIconService(Window window)
    {
        _window = window;
    }

    /// <summary>
    /// Initialize and show the tray icon.
    /// </summary>
    public void Initialize()
    {
        if (_trayIcon != null) return;

        _trayIcon = new TrayIcon
        {
            ToolTip = "Easydict - Dictionary & Translation",
            Icon = CreateDefaultIcon(),
            ContextMenu = CreateContextMenu()
        };

        // Double-click to show/restore window
        _trayIcon.MessageWindow.MouseEventReceived += OnMouseEvent;

        _trayIcon.Create();
    }

    private PopupMenu CreateContextMenu()
    {
        var menu = new PopupMenu();

        menu.Items.Add(new PopupMenuItem("Show Easydict", (_, _) => ShowWindow()));
        menu.Items.Add(new PopupMenuSeparator());
        menu.Items.Add(new PopupMenuItem("Exit", (_, _) => ExitApplication()));

        return menu;
    }

    private void OnMouseEvent(object? sender, MessageWindow.MouseEventReceivedEventArgs e)
    {
        if (e.MouseEvent == MouseEvent.IconLeftMouseDoubleClick)
        {
            ShowWindow();
        }
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

    private static Icon CreateDefaultIcon()
    {
        // Create a simple colored icon
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);

        // Draw a simple "E" shape with gradient background
        using var brush = new SolidBrush(Color.FromArgb(0, 120, 215)); // Windows blue
        g.FillEllipse(brush, 2, 2, 28, 28);

        using var textBrush = new SolidBrush(Color.White);
        using var font = new Font("Segoe UI", 16, FontStyle.Bold);
        g.DrawString("E", font, textBrush, 6, 4);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_trayIcon != null)
        {
            _trayIcon.MessageWindow.MouseEventReceived -= OnMouseEvent;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }
}

