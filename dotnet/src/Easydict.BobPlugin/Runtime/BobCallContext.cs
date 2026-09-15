using System.Threading.Channels;

namespace Easydict.BobPlugin.Runtime;

/// <summary>One thing a plugin reported during a call: a streamed partial, or the final payload.</summary>
/// <param name="IsCompletion">True for the terminal payload.</param>
/// <param name="PayloadJson">The JSON the plugin passed to onStream / completion.</param>
internal readonly record struct BobCallEvent(bool IsCompletion, string PayloadJson);

/// <summary>
/// State of one in-flight plugin call. A plugin can complete late, twice, or after a timeout, so
/// this is the single place that decides which completion counts.
/// </summary>
internal sealed class BobCallContext : IDisposable
{
    private readonly List<CancellationTokenSource> _httpCancellations = new();
    private readonly object _sync = new();
    private int _finished;
    private bool _disposed;

    public BobCallContext(int id, CancellationTokenSource linkedCts)
    {
        Id = id;
        LinkedCts = linkedCts;
        Channel = System.Threading.Channels.Channel.CreateUnbounded<BobCallEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }

    /// <summary>Identifies the call across the host/JS boundary.</summary>
    public int Id { get; }

    /// <summary>Events streamed back to the adapter.</summary>
    public Channel<BobCallEvent> Channel { get; }

    /// <summary>User cancellation linked with the per-call timeout.</summary>
    public CancellationTokenSource LinkedCts { get; }

    /// <summary>Accept the first terminal outcome only; later ones are ignored.</summary>
    public bool TryBeginCompletion() => Interlocked.Exchange(ref _finished, 1) == 0;

    /// <summary>Register an HTTP request so cancelling the call also cancels it.</summary>
    public void RegisterHttp(CancellationTokenSource cts)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                cts.Cancel();
                return;
            }

            _httpCancellations.Add(cts);
        }
    }

    /// <summary>Forget a finished HTTP request.</summary>
    public void UnregisterHttp(CancellationTokenSource cts)
    {
        lock (_sync)
        {
            _httpCancellations.Remove(cts);
        }
    }

    /// <summary>Cancel every HTTP request still running for this call.</summary>
    public void CancelHttp()
    {
        List<CancellationTokenSource> pending;
        lock (_sync)
        {
            pending = new List<CancellationTokenSource>(_httpCancellations);
        }

        foreach (var cts in pending)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Deliver a streamed partial.</summary>
    public void PublishStream(string payloadJson) => Channel.Writer.TryWrite(new BobCallEvent(false, payloadJson));

    /// <summary>Deliver the final payload and close the stream.</summary>
    public void PublishCompletion(string payloadJson)
    {
        Channel.Writer.TryWrite(new BobCallEvent(true, payloadJson));
        Channel.Writer.TryComplete();
    }

    /// <summary>Close the stream with a failure the adapter will rethrow.</summary>
    public void CompleteWithError(Exception error) => Channel.Writer.TryComplete(error);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CancelHttp();
        Channel.Writer.TryComplete();

        try
        {
            LinkedCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
