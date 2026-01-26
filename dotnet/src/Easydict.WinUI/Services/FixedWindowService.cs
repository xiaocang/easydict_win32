using Easydict.WinUI.Views;

namespace Easydict.WinUI.Services;

/// <summary>
/// Singleton service for managing the Fixed Window lifecycle.
/// The fixed window is created once and reused (shown/hidden) to preserve state.
/// Unlike Mini Window, Fixed Window does not auto-close on focus loss and is always on top.
/// </summary>
public sealed class FixedWindowService : IDisposable
{
    private static FixedWindowService? _instance;
    private FixedWindow? _fixedWindow;
    private bool _isDisposed;

    /// <summary>
    /// Gets the singleton instance of FixedWindowService.
    /// </summary>
    public static FixedWindowService Instance => _instance ??= new FixedWindowService();

    private FixedWindowService()
    {
        // Private constructor for singleton pattern
    }

    /// <summary>
    /// Gets whether the fixed window is currently visible.
    /// </summary>
    public bool IsVisible => _fixedWindow?.IsVisible ?? false;

    /// <summary>
    /// Toggle fixed window visibility (show if hidden, hide if visible).
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
    /// Show the fixed window, creating it if necessary.
    /// </summary>
    public void Show()
    {
        EnsureWindowCreated();
        _fixedWindow?.ShowAndActivate();
    }

    /// <summary>
    /// Hide the fixed window.
    /// </summary>
    public void Hide()
    {
        _fixedWindow?.HideWindow();
    }

    /// <summary>
    /// Show the fixed window with text to translate.
    /// </summary>
    public void ShowWithText(string text)
    {
        EnsureWindowCreated();
        _fixedWindow?.SetTextAndTranslate(text);
        _fixedWindow?.ShowAndActivate();
    }

    /// <summary>
    /// Refresh service results when settings change.
    /// </summary>
    public void RefreshServiceResults()
    {
        _fixedWindow?.RefreshServiceResults();
    }

    /// <summary>
    /// Ensure the fixed window instance exists.
    /// </summary>
    private void EnsureWindowCreated()
    {
        if (_fixedWindow == null)
        {
            _fixedWindow = new FixedWindow();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Close the window if it exists
        try
        {
            _fixedWindow?.Close();
        }
        catch
        {
            // Ignore close errors
        }
        _fixedWindow = null;
    }
}
