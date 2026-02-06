using System.Diagnostics;
using Easydict.WinUI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Services;

/// <summary>
/// Singleton service for managing the Mini Window lifecycle.
/// The mini window is created once and reused (shown/hidden) to preserve state.
/// </summary>
public sealed class MiniWindowService : IDisposable
{
    private static MiniWindowService? _instance;
    private MiniWindow? _miniWindow;
    private volatile bool _isDisposed;

    /// <summary>
    /// Gets the singleton instance of MiniWindowService.
    /// Must be accessed from the UI thread.
    /// </summary>
    public static MiniWindowService Instance
    {
        get
        {
            Debug.Assert(
                DispatcherQueue.GetForCurrentThread() != null,
                "MiniWindowService.Instance must be accessed from the UI thread");
            return _instance ??= new MiniWindowService();
        }
    }

    private MiniWindowService()
    {
        // Private constructor for singleton pattern
    }

    /// <summary>
    /// Gets whether the mini window is currently visible.
    /// </summary>
    public bool IsVisible => _miniWindow?.IsVisible ?? false;

    /// <summary>
    /// Toggle mini window visibility (show if hidden, hide if visible).
    /// </summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    /// <summary>
    /// Show the mini window, creating it if necessary.
    /// </summary>
    public void Show()
    {
        EnsureWindowCreated();
        _miniWindow?.ShowAndActivate();
    }

    /// <summary>
    /// Hide the mini window.
    /// </summary>
    public void Hide()
    {
        _miniWindow?.HideWindow();
    }

    /// <summary>
    /// Show the mini window with text to translate.
    /// </summary>
    public void ShowWithText(string text)
    {
        EnsureWindowCreated();
        _miniWindow?.SetTextAndTranslate(text);
        _miniWindow?.ShowAndActivate();
    }

    /// <summary>
    /// Refresh service results when settings change.
    /// </summary>
    public void RefreshServiceResults()
    {
        _miniWindow?.RefreshServiceResults();
    }

    /// <summary>
    /// Apply theme to the mini window.
    /// </summary>
    public void ApplyTheme(ElementTheme theme)
    {
        _miniWindow?.ApplyTheme(theme);
    }

    /// <summary>
    /// Ensure the mini window instance exists.
    /// </summary>
    private void EnsureWindowCreated()
    {
        if (_miniWindow == null)
        {
            _miniWindow = new MiniWindow();
            var theme = SettingsService.Instance.AppTheme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            _miniWindow.ApplyTheme(theme);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Close the window if it exists
        try
        {
            _miniWindow?.Close();
        }
        catch
        {
            // Ignore close errors
        }
        _miniWindow = null;
    }
}
