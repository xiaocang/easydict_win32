using Easydict.WinUI.Views;

namespace Easydict.WinUI.Services;

/// <summary>
/// Singleton service for managing the Mini Window lifecycle.
/// The mini window is created once and reused (shown/hidden) to preserve state.
/// </summary>
public sealed class MiniWindowService : IDisposable
{
    private static MiniWindowService? _instance;
    private MiniWindow? _miniWindow;
    private bool _isDisposed;

    /// <summary>
    /// Gets the singleton instance of MiniWindowService.
    /// </summary>
    public static MiniWindowService Instance => _instance ??= new MiniWindowService();

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
    /// Ensure the mini window instance exists.
    /// </summary>
    private void EnsureWindowCreated()
    {
        if (_miniWindow == null)
        {
            _miniWindow = new MiniWindow();
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
