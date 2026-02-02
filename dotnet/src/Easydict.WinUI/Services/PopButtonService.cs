using System.Diagnostics;
using Easydict.WinUI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages the lifecycle of the floating pop button that appears after text selection.
/// Coordinates between MouseHookService (input detection), PopButtonWindow (UI),
/// TextSelectionService (text extraction), and MiniWindowService (translation display).
/// </summary>
public sealed class PopButtonService : IDisposable
{
    /// <summary>
    /// Delay after mouse-up before querying selected text.
    /// Allows the source application to finalize selection state.
    /// </summary>
    internal const int SelectionDelayMs = 150;

    /// <summary>
    /// Auto-dismiss timeout for the pop button if the user doesn't interact.
    /// </summary>
    internal const int AutoDismissMs = 5000;

    private readonly DispatcherQueue _dispatcherQueue;
    private PopButtonWindow? _popWindow;
    private string? _pendingText;
    private CancellationTokenSource? _selectionCts;
    private CancellationTokenSource? _autoDismissCts;
    private bool _isDisposed;
    private bool _isEnabled = true;

    /// <summary>
    /// Gets or sets whether the pop button feature is enabled.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (!value)
            {
                Dismiss();
            }
        }
    }

    /// <summary>
    /// Gets whether the pop button is currently visible.
    /// </summary>
    public bool IsVisible => _popWindow?.IsPopupVisible ?? false;

    public PopButtonService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// Called when a drag-select gesture ends.
    /// Waits briefly, then checks for selected text and shows the pop button.
    /// </summary>
    public async void OnDragSelectionEnd(MouseHookService.POINT mouseScreenPoint)
    {
        if (!_isEnabled || _isDisposed) return;

        // Cancel any previous pending selection detection
        _selectionCts?.Cancel();
        _selectionCts = new CancellationTokenSource();
        var ct = _selectionCts.Token;

        try
        {
            // Wait for the source app to finalize the selection
            await Task.Delay(SelectionDelayMs, ct);
            if (ct.IsCancellationRequested) return;

            // Get the selected text using the existing TextSelectionService
            var text = await TextSelectionService.GetSelectedTextAsync();
            if (ct.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.WriteLine("[PopButtonService] No selected text found after drag");
                return;
            }

            _pendingText = text;
            Debug.WriteLine($"[PopButtonService] Selected text: '{text.Substring(0, Math.Min(50, text.Length))}...'");

            // Show the pop button on the UI thread
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_isDisposed || ct.IsCancellationRequested) return;

                EnsureWindowCreated();
                _popWindow!.ShowAt(mouseScreenPoint.x, mouseScreenPoint.y);

                // Start auto-dismiss timer
                StartAutoDismissTimer();
            });
        }
        catch (TaskCanceledException)
        {
            // Expected when a new selection starts before the previous one completes
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PopButtonService] Error during selection detection: {ex.Message}");
        }
    }

    /// <summary>
    /// Dismiss the pop button and clear pending state.
    /// Called by MouseHookService on mouse-down, scroll, right-click, etc.
    /// </summary>
    public void Dismiss()
    {
        _selectionCts?.Cancel();
        _autoDismissCts?.Cancel();
        _pendingText = null;

        _dispatcherQueue.TryEnqueue(() =>
        {
            _popWindow?.HidePopup();
        });
    }

    /// <summary>
    /// Called when the pop button is clicked by the user.
    /// Hides the pop button and opens the mini window with the selected text.
    /// </summary>
    private void OnPopButtonClicked()
    {
        var text = _pendingText;
        Dismiss();

        if (string.IsNullOrWhiteSpace(text)) return;

        Debug.WriteLine($"[PopButtonService] Opening MiniWindow with text: '{text.Substring(0, Math.Min(50, text.Length))}...'");

        _dispatcherQueue.TryEnqueue(() =>
        {
            TextInsertionService.CaptureSourceWindow();
            MiniWindowService.Instance.ShowWithText(text);
        });
    }

    private void EnsureWindowCreated()
    {
        if (_popWindow != null) return;

        _popWindow = new PopButtonWindow();
        _popWindow.OnClicked += OnPopButtonClicked;

        // Apply current theme
        var theme = SettingsService.Instance.AppTheme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        _popWindow.ApplyTheme(theme);

        Debug.WriteLine("[PopButtonService] PopButtonWindow created");
    }

    private void StartAutoDismissTimer()
    {
        _autoDismissCts?.Cancel();
        _autoDismissCts = new CancellationTokenSource();
        var ct = _autoDismissCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AutoDismissMs, ct);
                if (!ct.IsCancellationRequested)
                {
                    Debug.WriteLine("[PopButtonService] Auto-dismiss timeout");
                    Dismiss();
                }
            }
            catch (TaskCanceledException)
            {
                // Expected when dismissed before timeout
            }
        });
    }

    /// <summary>
    /// Apply theme to the pop button window.
    /// </summary>
    public void ApplyTheme(ElementTheme theme)
    {
        _popWindow?.ApplyTheme(theme);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _selectionCts?.Cancel();
        _autoDismissCts?.Cancel();

        try
        {
            _popWindow?.Close();
        }
        catch
        {
            // Ignore close errors during shutdown
        }
        _popWindow = null;

        Debug.WriteLine("[PopButtonService] Disposed");
    }
}
