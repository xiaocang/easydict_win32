using System.Diagnostics;
using System.Runtime.InteropServices;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.TextActions;
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
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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
    private string? _appliedActionsSignature;
    private ElementTheme _appliedActionsTheme;
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

        MiniWindowService.Instance.QueryModeChanged += OnMiniWindowModeChanged;
    }

    /// <summary>
    /// Called when a drag-select gesture ends.
    /// Waits briefly, then checks for selected text and shows the pop button.
    /// </summary>
    public async void OnDragSelectionEnd(MouseHookService.POINT mouseScreenPoint)
    {
        if (!_isEnabled || _isDisposed || ScreenCaptureService.IsCaptureInProgress) return;

        // Check if the foreground app is excluded BEFORE any delay or clipboard access
        var processName = GetForegroundProcessName();
        if (SettingsService.Instance.IsMouseSelectionExcluded(processName))
        {
            Debug.WriteLine($"[PopButtonService] Excluded app '{processName}', skipping pop button");
            return;
        }

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
                ApplyConfiguredActions();
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

        if (_popWindow?.IsPopupVisible == true)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                _popWindow?.HidePopup();
            });
        }
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
            MiniWindowService.Instance.ShowWithText(text, QuerySourceKind.Selection);
        });
    }

    /// <summary>
    /// Called when the user clicks one of the configured action buttons on the strip.
    /// Hides the strip and runs the action on the selected text.
    /// </summary>
    private void OnPopActionClicked(TextAction action)
    {
        var text = _pendingText;
        Dismiss("ActionClicked");

        if (string.IsNullOrWhiteSpace(text)) return;

        Debug.WriteLine($"[PopButtonService] Running action '{action.Id}' with text: '{text[..Math.Min(50, text.Length)]}...'");

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (action.Type == TextActionType.RunService)
            {
                // Same as the translate button: remember the source window so "replace" can write back.
                TextInsertionService.CaptureSourceWindow();
            }

            _ = TextActionExecutor.ExecuteAsync(action, TextActionExecutor.CreateSelectionContext(text));
        });
    }

    /// <summary>
    /// Sync the strip with the configured pop-button actions. Buttons are only rebuilt when the
    /// action list or theme changed since the last show (UI thread).
    /// </summary>
    private void ApplyConfiguredActions()
    {
        if (_popWindow is null) return;

        try
        {
            var actions = TextActionRegistry.GetPopButtonActions();
            var theme = MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme);
            // Include every field the click handler or button rendering actually reads, not just
            // Id/Title, so editing an action's URL template or target service (with the id and
            // title unchanged) is detected and rebuilds the strip instead of firing stale data.
            var signature = string.Join("|", actions.Select(a =>
                $"{a.Action.Id}\u001f{a.Action.Title}\u001f{a.Action.Type}\u001f{a.Action.UrlTemplate}\u001f{a.Action.ServiceId}\u001f{a.Action.IconGlyph}\u001f{a.Origin.Kind}"));
            if (signature == _appliedActionsSignature && theme == _appliedActionsTheme)
            {
                return;
            }

            _popWindow.SetActions(actions, theme);
            _appliedActionsSignature = signature;
            _appliedActionsTheme = theme;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PopButtonService] Failed to apply actions: {ex.Message}");
        }
    }

    private void OnMiniWindowModeChanged(QueryMode mode)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _popWindow?.UpdateMode(mode);
        });
    }

    private void EnsureWindowCreated()
    {
        if (_popWindow != null) return;

        _popWindow = new PopButtonWindow();
        _popWindow.OnClicked += OnPopButtonClicked;
        _popWindow.OnActionClicked += OnPopActionClicked;

        // Register window handle with mouse hook service to prevent self-dismissal
        if (_mouseHookService != null)
        {
            _mouseHookService.SetPopButtonWindowHandle(_popWindow.WindowHandle);
        }

        // Apply current theme
        _popWindow.ApplyTheme(MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme));

        // Sync to the current mode (in case user already switched before first pop button show)
        _popWindow.UpdateMode(MiniWindowService.Instance.CurrentQueryMode);

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
    public void ApplyTheme(ElementTheme theme, bool forceResourceRefresh = false)
    {
        _popWindow?.ApplyTheme(theme, forceResourceRefresh);
        // Icons are theme-dependent; rebuild the strip on next show.
        _appliedActionsSignature = null;
    }

    internal static string? GetForegroundProcessName()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;
            if (GetWindowThreadProcessId(hWnd, out uint processId) == 0) return null;
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        MiniWindowService.Instance.QueryModeChanged -= OnMiniWindowModeChanged;

        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = null;

        CancelAutoDismissTimer();

        try
        {
            if (_popWindow != null)
            {
                _popWindow.OnClicked -= OnPopButtonClicked;
                _popWindow.OnActionClicked -= OnPopActionClicked;
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
