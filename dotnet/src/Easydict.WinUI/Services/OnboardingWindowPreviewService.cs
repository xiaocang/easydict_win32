using Easydict.TranslationService.Models;
using Easydict.WinUI.Views;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Services;

/// <summary>
/// Owns isolated instances of the real selection pop button and Mini window for onboarding previews.
/// The Mini window is supplied with fixed sample data and never runs a translation query.
/// Call all methods on the UI thread.
/// </summary>
internal sealed class OnboardingWindowPreviewService : IDisposable
{
    private PopButtonWindow? _popButtonWindow;
    private MiniWindow? _miniWindow;
    private Action? _onTranslate;
    private bool _isDisposed;

    /// <summary>Show the real floating translation button at a physical screen position.</summary>
    public void ShowPopButton(int screenX, int screenY, Action onTranslate)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(onTranslate);

        _onTranslate = onTranslate;
        _miniWindow?.HideWindow();
        if (_popButtonWindow is null)
        {
            _popButtonWindow = new PopButtonWindow();
            _popButtonWindow.OnClicked += OnPopButtonClicked;
            _popButtonWindow.Closed += OnPopButtonWindowClosed;
        }

        _popButtonWindow.ApplyTheme(MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme));
        _popButtonWindow.UpdateMode(QueryMode.Translation);
        _popButtonWindow.ShowAt(screenX, screenY);
    }

    /// <summary>Show the real Mini window with a read-only, fixed sample result.</summary>
    public void ShowMini(string sourceText, string translatedText)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(translatedText);

        _onTranslate = null;
        _popButtonWindow?.HidePopup();

        if (_miniWindow is null)
        {
            _miniWindow = new MiniWindow(isOnboardingPreview: true);
            _miniWindow.Closed += OnMiniWindowClosed;
        }

        _miniWindow.ShowOnboardingPreview(sourceText, translatedText);
    }

    /// <summary>Hide both preview windows while keeping them available for the next step.</summary>
    public void HideAll()
    {
        if (_isDisposed)
        {
            return;
        }

        _onTranslate = null;
        _popButtonWindow?.HidePopup();
        _miniWindow?.HideWindow();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _onTranslate = null;

        var popButtonWindow = _popButtonWindow;
        _popButtonWindow = null;
        var miniWindow = _miniWindow;
        _miniWindow = null;

        if (popButtonWindow is not null)
        {
            popButtonWindow.OnClicked -= OnPopButtonClicked;
            popButtonWindow.Closed -= OnPopButtonWindowClosed;
        }
        if (miniWindow is not null)
        {
            miniWindow.Closed -= OnMiniWindowClosed;
        }

        try
        {
            popButtonWindow?.Close();
        }
        finally
        {
            miniWindow?.Close();
        }
    }

    private void OnPopButtonClicked()
    {
        var callback = _onTranslate;
        _onTranslate = null;
        _popButtonWindow?.HidePopup();
        callback?.Invoke();
    }

    private void OnPopButtonWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not PopButtonWindow window)
        {
            return;
        }

        window.OnClicked -= OnPopButtonClicked;
        window.Closed -= OnPopButtonWindowClosed;
        if (ReferenceEquals(_popButtonWindow, window))
        {
            _popButtonWindow = null;
            _onTranslate = null;
        }
    }

    private void OnMiniWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not MiniWindow window)
        {
            return;
        }

        window.Closed -= OnMiniWindowClosed;
        if (ReferenceEquals(_miniWindow, window))
        {
            _miniWindow = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(OnboardingWindowPreviewService));
        }
    }
}
