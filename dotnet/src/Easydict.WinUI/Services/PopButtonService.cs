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
    public const int SelectionDelayMs = 150;

    /// <summary>
    /// Auto-dismiss timeout for the pop button if the user doesn't interact.
    /// </summary>
    public const int AutoDismissMs = 5000;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly MouseHookService? _mouseHookService;
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

    public PopButtonService(DispatcherQueue dispatcherQueue, MouseHookService? mouseHookService = null)
    {
        _dispatcherQueue = dispatcherQueue;
        _mouseHookService = mouseHookService;
    }

    /// <summary>
    /// Called when a drag-select gesture ends.
    /// Waits briefly, then checks for selected text and shows the pop button.
    /// </summary>
    public async void OnDragSelectionEnd(MouseHookService.POINT mouseScreenPoint)
    {
        if (!_isEnabled || _isDisposed) return;

        // Cancel any previous pending selection detection.
        // Swap field first so previous operation sees cancellation via its own token,
        // then dispose after swap to avoid racing with in-flight awaits.
        var previousCts = Interlocked.Exchange(ref _selectionCts, null);
        previousCts?.Cancel();
        previousCts?.Dispose();

        var currentCts = new CancellationTokenSource();
        _selectionCts = currentCts;
        var ct = currentCts.Token;

        try
        {
            // Wait for the source app to finalize the selection
            await Task.Delay(SelectionDelayMs, ct);

            // Get the selected text using the existing TextSelectionService
            var text = await TextSelectionService.GetSelectedTextAsync(ct);

            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.WriteLine("[PopButtonService] No selected text found after drag");
                return;
            }

            _pendingText = text;
            Debug.WriteLine($"[PopButtonService] Selected text: '{text[..Math.Min(50, text.Length)]}...'");

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
        catch (OperationCanceledException)
        {
            // Expected when a new selection starts before the previous one completes
            Debug.WriteLine("[PopButtonService] Selection detection canceled (user performed another action)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PopButtonService] Error during selection detection: {ex}");
        }
    }

    /// <summary>
    /// Dismiss the pop button and clear pending state.
    /// Called by MouseHookService on mouse-down, scroll, right-click, etc.
    /// </summary>
    public void Dismiss(string reason = "Unknown")
    {
        var oldSelectionCts = Interlocked.Exchange(ref _selectionCts, null);
        if (oldSelectionCts != null)
        {
            Debug.WriteLine($"[PopButtonService] Dismissing due to: {reason}");
            oldSelectionCts.Cancel();
            oldSelectionCts.Dispose();
        }
        CancelAutoDismissTimer();
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

        Debug.WriteLine($"[PopButtonService] Opening MiniWindow with text: '{text[..Math.Min(50, text.Length)]}...'");

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

        // Register window handle with mouse hook service to prevent self-dismissal
        if (_mouseHookService != null)
        {
            _mouseHookService.SetPopButtonWindowHandle(_popWindow.WindowHandle);
        }

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
        CancelAutoDismissTimer();

        var cts = new CancellationTokenSource();
        _autoDismissCts = cts;
        var ct = cts.Token;

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
            catch (OperationCanceledException)
            {
                // Expected when dismissed before timeout
            }
        });
    }

    private void CancelAutoDismissTimer()
    {
        var cts = _autoDismissCts;
        _autoDismissCts = null;
        cts?.Cancel();
        cts?.Dispose();
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
        _selectionCts?.Dispose();
        _selectionCts = null;

        CancelAutoDismissTimer();

        try
        {
            if (_popWindow != null)
            {
                _popWindow.OnClicked -= OnPopButtonClicked;
                _popWindow.Close();
            }
        }
        catch
        {
            // Ignore close errors during shutdown
        }
        _popWindow = null;

        Debug.WriteLine("[PopButtonService] Disposed");
    }
}
