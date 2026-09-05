using System.Diagnostics;
using Microsoft.UI.Dispatching;

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
    internal HotkeyWindowShowCoordinator ShowRequests { get; } = new();

    /// <summary>
    /// Gets the singleton instance of FixedWindowService.
    /// Must be accessed from the UI thread.
    /// </summary>
    public static FixedWindowService Instance
    {
        get
        {
            AssertUIThread();
            return _instance ??= new FixedWindowService();
        }
    }

    [Conditional("DEBUG")]
    private static void AssertUIThread()
    {
        try
        {
            Debug.Assert(
                DispatcherQueue.GetForCurrentThread() != null,
                "FixedWindowService.Instance must be accessed from the UI thread");
        }
        catch
        {
            // DispatcherQueue unavailable (e.g., unit tests without Windows App SDK).
        }
    }

    private FixedWindowService()
    {
        ShowRequests.PendingChanged += pending => _fixedWindow?.SetSelectionCapturePending(pending);
    }

    /// <summary>
    /// Gets whether the fixed window is currently visible.
    /// </summary>
    public bool IsVisible => _fixedWindow?.IsVisible ?? false;

    /// <summary>
    /// Gets whether the fixed window is currently the foreground window.
    /// </summary>
    public bool IsForeground => _fixedWindow?.IsForeground ?? false;

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

    /// <summary>Ensure a hidden window instance exists.</summary>
    public void EnsureCreated() => EnsureWindowCreated();

    /// <summary>Show promptly while preserving focus in the selection source.</summary>
    internal void ShowWithoutActivation()
    {
        var stopwatch = Stopwatch.StartNew();
        var created = _fixedWindow == null;
        EnsureWindowCreated();
        CrashDiagnostics.Log($"[WindowShow] Fixed: creation={stopwatch.ElapsedMilliseconds}ms, new={created}");
        _fixedWindow?.SetSelectionCapturePending(ShowRequests.IsPending);
        _fixedWindow?.ShowWithoutActivation();
        CrashDiagnostics.Log($"[WindowShow] Fixed: show requested={stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Show the fixed window, creating it if necessary.
    /// </summary>
    public void Show()
    {
        ShowRequests.Invalidate();
        EnsureWindowCreated();
        _fixedWindow?.ShowAndActivate();
    }

    /// <summary>
    /// Hide the fixed window.
    /// </summary>
    public void Hide()
    {
        ShowRequests.Invalidate();
        _fixedWindow?.HideWindow();
    }

    /// <summary>
    /// Show the fixed window with text to translate.
    /// </summary>
    public void ShowWithText(string text)
    {
        ShowRequests.Invalidate();
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
    /// Refresh language combo boxes when SelectedLanguages changes.
    /// </summary>
    public void RefreshLanguageCombos()
    {
        _fixedWindow?.RefreshLanguageCombos();
    }

    /// <summary>
    /// Apply theme to the fixed window.
    /// </summary>
    public void ApplyTheme(ElementTheme theme, bool forceResourceRefresh = false)
    {
        _fixedWindow?.ApplyTheme(theme, forceResourceRefresh);
    }

    /// <summary>
    /// Re-apply appearance settings (result font size, button visibility) to the fixed window.
    /// </summary>
    public void ApplyAppearance()
    {
        _fixedWindow?.ApplyAppearance();
    }

    /// <summary>
    /// Ensure the fixed window instance exists.
    /// </summary>
    private void EnsureWindowCreated()
    {
        if (_fixedWindow == null)
        {
            _fixedWindow = new FixedWindow();
            _fixedWindow.SelectionCaptureInterrupted += ShowRequests.Invalidate;
            _fixedWindow.Closed += (_, _) =>
            {
                ShowRequests.Invalidate();
                _fixedWindow = null;
            };
            _fixedWindow.ApplyTheme(MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme));
        }
    }

    public void Dispose()
    {
        ShowRequests.Invalidate();
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
