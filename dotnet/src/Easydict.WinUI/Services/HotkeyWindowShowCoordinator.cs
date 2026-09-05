using System.Diagnostics;

namespace Easydict.WinUI.Services;

/// <summary>
/// UI-thread-owned lifetime of a window shown while selection capture is pending.
/// RunAsync alone owns and disposes its CTS; interaction may invalidate it at any time.
/// </summary>
internal sealed class HotkeyWindowShowCoordinator
{
    private CancellationTokenSource? _current;

    internal bool IsPending => _current != null;
    internal event Action<bool>? PendingChanged;

    internal void Invalidate()
    {
        var previous = _current;
        if (previous == null) return;
        _current = null;
        try { PendingChanged?.Invoke(false); }
        finally
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    internal async Task RunAsync(
        Action showWithoutActivation,
        Func<CancellationToken, Task<string?>> capture,
        Action<string?> complete,
        Func<bool> canComplete)
    {
        Invalidate();
        using var current = new CancellationTokenSource();
        _current = current;
        try
        {
            PendingChanged?.Invoke(true);
            showWithoutActivation();
            string? text;
            try
            {
                text = await capture(current.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowShow] Selection capture failed: {ex.GetType().Name}");
                text = null;
            }

            if (ReferenceEquals(_current, current) && canComplete())
            {
                // End the pending phase before programmatic text updates and activation.
                _current = null;
                PendingChanged?.Invoke(false);
                complete(text);
            }
        }
        finally
        {
            if (ReferenceEquals(_current, current))
            {
                _current = null;
                PendingChanged?.Invoke(false);
            }
        }
    }
}
