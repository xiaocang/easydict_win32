using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace Easydict.WinUI.Services.ScreenCapture;

public enum ScreenCaptureDemoNavigationKey
{
    Space,
    Right
}

public sealed class ScreenCaptureDemoNavigationKeyEventArgs : EventArgs
{
    public ScreenCaptureDemoNavigationKeyEventArgs(ScreenCaptureDemoNavigationKey key)
    {
        Key = key;
    }

    public ScreenCaptureDemoNavigationKey Key { get; }
}

/// <summary>
/// Owns a real screen-capture overlay for the onboarding preview without producing image data.
/// </summary>
public sealed class ScreenCaptureDemoSession : IAsyncDisposable
{
    private readonly ScreenCaptureWindow _window;
    private readonly Task<RectInt32?> _completion;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private int _confirmRequested;
    private int _navigationDetached;
    private int _disposed;

    private ScreenCaptureDemoSession(
        ScreenCaptureWindow window,
        Task<RectInt32?> completion,
        DispatcherQueue? dispatcherQueue)
    {
        _window = window;
        _completion = completion;
        _dispatcherQueue = dispatcherQueue;
        _window.DemoNavigationRequested += OnDemoNavigationRequested;
        _ = DetachNavigationWhenCompleteAsync();
    }

    public event EventHandler<ScreenCaptureDemoNavigationKeyEventArgs>? NavigationRequested;

    /// <summary>
    /// Completes after the overlay is closed and cleaned up, with the selected physical rectangle or null if cancelled.
    /// </summary>
    public Task<RectInt32?> Completion => _completion;

    /// <summary>
    /// Opens the real dimmed desktop overlay and returns once it is ready for preview commands.
    /// The desktop bitmap remains transient and in-memory for the overlay renderer only.
    /// </summary>
    public static async Task<ScreenCaptureDemoSession> ShowAsync(CancellationToken cancellationToken = default)
    {
        var window = new ScreenCaptureWindow(demoSession: true);
        Task<RectInt32?>? completion = null;
        ScreenCaptureDemoSession? session = null;
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        try
        {
            completion = window.CaptureDemoAsync(cancellationToken);
            session = new ScreenCaptureDemoSession(window, completion, dispatcherQueue);
            await window.WaitForDemoReadyAsync().ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                window.Cancel();
                if (completion is not null)
                {
                    try { await completion.ConfigureAwait(false); }
                    catch { }
                }
                window.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Shows the real orange selection outline for a rectangle in virtual-desktop physical pixels.
    /// No pixels are extracted from the selected region.
    /// </summary>
    public async Task SelectRegionAsync(RectInt32 screenPhysicalRect, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_window.IsDemoCancelled)
            throw new InvalidOperationException("The demo capture session has been cancelled.");
        if (Volatile.Read(ref _confirmRequested) != 0)
            throw new InvalidOperationException("The demo selection has already been confirmed.");

        try
        {
            await _window.SelectDemoRegionAsync(screenPhysicalRect, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancel();
            throw;
        }
    }

    /// <summary>
    /// Closes the overlay and returns the selected physical-pixel rectangle, or null if it was cancelled.
    /// This does not create a ScreenCaptureResult or invoke OCR.
    /// </summary>
    public async Task<RectInt32?> ConfirmAsync(CancellationToken cancellationToken = default)
    {
        await ThrowIfCancellationRequestedAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_completion.IsCompleted)
        {
            var completedResult = await _completion.ConfigureAwait(false);
            await ThrowIfCancellationRequestedAsync(cancellationToken).ConfigureAwait(false);
            return completedResult;
        }

        if (Interlocked.CompareExchange(ref _confirmRequested, 1, 0) == 0)
        {
            try { _window.RequestDemoConfirm(); }
            catch
            {
                Interlocked.Exchange(ref _confirmRequested, 0);
                throw;
            }
        }

        try
        {
            var result = await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            await ThrowIfCancellationRequestedAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAndWaitForCleanupAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>
    /// Cancels the overlay immediately, without the normal screenshot confirmation dialog.
    /// </summary>
    public void Cancel()
    {
        _window.Cancel();
        DetachNavigationHandler();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                DetachNavigationHandler();
                _window.Cancel();
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await _completion.ConfigureAwait(false); }
        catch { }
        _window.Dispose();
    }

    private async Task ThrowIfCancellationRequestedAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
            return;

        await CancelAndWaitForCleanupAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task CancelAndWaitForCleanupAsync()
    {
        Cancel();
        try { await _completion.ConfigureAwait(false); }
        catch { }
    }

    private async Task DetachNavigationWhenCompleteAsync()
    {
        try { await _completion.ConfigureAwait(false); }
        catch { }
        DetachNavigationHandler();
    }

    private void DetachNavigationHandler()
    {
        if (Interlocked.Exchange(ref _navigationDetached, 1) == 0)
            _window.DemoNavigationRequested -= OnDemoNavigationRequested;
    }

    private void OnDemoNavigationRequested(ScreenCaptureDemoNavigationKey key)
    {
        if (Volatile.Read(ref _disposed) != 0 || _window.IsDemoCancelled || _completion.IsCompleted)
            return;

        var handler = NavigationRequested;
        if (handler is null)
            return;

        var args = new ScreenCaptureDemoNavigationKeyEventArgs(key);
        void RaiseIfActive()
        {
            if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _confirmRequested) == 0 &&
                !_window.IsDemoCancelled && !_completion.IsCompleted)
            {
                handler(this, args);
            }
        }

        if (_dispatcherQueue is not null)
        {
            if (_dispatcherQueue.HasThreadAccess)
                RaiseIfActive();
            else
                _dispatcherQueue.TryEnqueue(RaiseIfActive);
            return;
        }

        RaiseIfActive();
    }
}
