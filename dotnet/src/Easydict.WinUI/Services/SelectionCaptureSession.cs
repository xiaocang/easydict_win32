namespace Easydict.WinUI.Services;

/// <summary>Locks selection capture to the foreground window at invocation.</summary>
internal sealed class SelectionCaptureSession(
    nint sourceWindow,
    Func<nint> getForegroundWindow,
    CancellationToken cancellationToken)
{
    internal nint SourceWindow => sourceWindow;
    internal CancellationToken CancellationToken => cancellationToken;

    internal void ThrowIfInvalid()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceWindow == 0 || getForegroundWindow() != sourceWindow)
            throw new OperationCanceledException("Selection source is no longer foreground.", cancellationToken);
    }

    internal static async Task<T> WithCleanupAsync<T>(Func<Task<T>> capture, Func<Task> cleanup)
    {
        try { return await capture(); }
        finally { await cleanup(); }
    }

    // Once a synchronous clipboard/input action starts, wait for it to finish before
    // releasing the capture gate. A timed-out queued action must never run later.
    internal static async Task RunDispatchedAsync(
        Func<Action, bool> enqueue, Action action, CancellationToken cancellationToken, int timeoutMs)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = 0; // queued=0, running=1, abandoned=2
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var cancelled = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                completion.TrySetCanceled(cancellationToken);
        });
        using var expired = timeout.Token.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                completion.TrySetException(new TimeoutException("Dispatcher operation timed out."));
        });
        if (!enqueue(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }))
            completion.TrySetException(new InvalidOperationException("Failed to enqueue on dispatcher"));

        await completion.Task;
        // Cancellation after input injection cannot undo the action. The caller must
        // observe its outcome and clean up before honouring cancellation.
    }
}

/// <summary>Serializes entire captures, including non-cancellable clipboard cleanup.</summary>
internal sealed class SelectionCaptureGate
{
    internal static SelectionCaptureGate Shared { get; } = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal Task DrainAsync() => RunAsync(() => Task.FromResult(true), CancellationToken.None);

    internal async Task<T> RunAsync<T>(Func<Task<T>> capture, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try { return await capture(); }
        finally { _semaphore.Release(); }
    }
}
