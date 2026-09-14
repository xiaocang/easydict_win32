using System.Collections.Concurrent;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace Easydict.BobPlugin.Runtime.Bridges;

/// <summary>
/// Backs <c>$timer</c>. Timers fire on the JS loop thread and are all cancelled when the plugin
/// instance is disposed, so a plugin cannot keep work alive after it is unloaded.
/// </summary>
internal sealed class TimerBridge : IDisposable
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _timers = new();
    private readonly Action<JsValue?, string> _postCallback;
    private int _nextId;
    private bool _disposed;

    public TimerBridge(Action<JsValue?, string> postCallback) => _postCallback = postCallback;

    /// <summary>Schedule a callback and return its id.</summary>
    public double SetTimeout(JsValue? callback, double delayMs)
    {
        if (_disposed || callback is null || callback.IsNull() || callback.IsUndefined())
        {
            return -1;
        }

        var id = Interlocked.Increment(ref _nextId);
        var cts = new CancellationTokenSource();
        _timers[id] = cts;

        var delay = delayMs is > 0 and < int.MaxValue ? (int)delayMs : 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested)
                {
                    _postCallback(callback, "null");
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (_timers.TryRemove(id, out var removed))
                {
                    removed.Dispose();
                }
            }
        });

        return id;
    }

    /// <summary>Cancel a scheduled callback.</summary>
    public void ClearTimeout(double id)
    {
        if (_timers.TryRemove((int)id, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var id in _timers.Keys)
        {
            ClearTimeout(id);
        }
    }
}
