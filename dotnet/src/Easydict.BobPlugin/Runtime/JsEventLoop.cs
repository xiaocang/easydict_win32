using System.Collections.Concurrent;

namespace Easydict.BobPlugin.Runtime;

/// <summary>
/// A single-threaded work queue. Every plugin instance owns one: the Jint engine and every
/// JsValue that comes out of it are touched only on this thread, which is what makes host
/// callbacks (HTTP completions, timers) safe to deliver from arbitrary thread-pool threads.
/// </summary>
internal sealed class JsEventLoop : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    public JsEventLoop(string name)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name
        };
        _thread.Start();
    }

    /// <summary>True when the caller is already on the loop thread.</summary>
    public bool IsOnLoopThread => Thread.CurrentThread == _thread;

    /// <summary>Queue work on the loop thread. Ignored once the loop is disposed.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_disposed)
        {
            return;
        }

        try
        {
            _queue.Add(action);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and the add.
        }
        catch (InvalidOperationException)
        {
            // The queue was completed concurrently with this call; dropping the work is correct.
        }
    }

    /// <summary>Run a function on the loop thread and await its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (IsOnLoopThread)
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        // Continuations must not run on the loop thread, or awaiting host code would occupy it.
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (_disposed)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(JsEventLoop)));
        }

        return tcs.Task;
    }

    /// <summary>Run an action on the loop thread and await its completion.</summary>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return true;
        });
    }

    private void Run()
    {
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // A failing callback must not tear down the loop; the owning call reports it.
                    System.Diagnostics.Debug.WriteLine($"[JsEventLoop] Unhandled callback error: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            // The queue was disposed under the enumerator during shutdown, or something equally
            // terminal happened. Either way this thread must not take the process down with it.
            System.Diagnostics.Debug.WriteLine($"[JsEventLoop] Loop ended: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        if (!IsOnLoopThread)
        {
            // Bounded: a plugin stuck in a synchronous loop is cut off rather than blocking shutdown.
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _queue.Dispose();
    }
}
