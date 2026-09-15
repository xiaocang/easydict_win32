using System.Diagnostics;

namespace Easydict.BobPlugin;

/// <summary>
/// Receives what a plugin writes through <c>$log</c> plus host-side diagnostics for that plugin.
/// The settings UI shows the most recent lines when a plugin misbehaves.
/// </summary>
public interface IBobHostLogger
{
    /// <summary>Record one line. <paramref name="level"/> is "info", "warn" or "error".</summary>
    void Log(string level, string message);
}

/// <summary>Default logger: writes to the debug output, prefixed with the service id.</summary>
public sealed class DebugBobHostLogger : IBobHostLogger
{
    private readonly string _serviceId;

    public DebugBobHostLogger(string serviceId) => _serviceId = serviceId;

    public void Log(string level, string message)
        => Debug.WriteLine($"[BobPlugin:{_serviceId}] [{level}] {message}");
}

/// <summary>
/// Keeps the most recent lines in memory so the settings page can show why a plugin failed,
/// and forwards everything to an inner logger.
/// </summary>
internal sealed class RingBufferLogger : IBobHostLogger
{
    private readonly Queue<string> _lines = new();
    private readonly object _sync = new();
    private readonly IBobHostLogger _inner;
    private readonly int _capacity;

    public RingBufferLogger(IBobHostLogger inner, int capacity = 200)
    {
        _inner = inner;
        _capacity = capacity;
    }

    public void Log(string level, string message)
    {
        lock (_sync)
        {
            _lines.Enqueue($"{DateTime.Now:HH:mm:ss} [{level}] {message}");
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }

        _inner.Log(level, message);
    }

    /// <summary>A snapshot of the retained lines, oldest first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync)
        {
            return _lines.ToList();
        }
    }
}
