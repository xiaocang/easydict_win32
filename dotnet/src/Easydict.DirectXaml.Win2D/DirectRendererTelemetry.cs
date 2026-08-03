using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Easydict.DirectXaml.Win2D;

/// <summary>
/// Opt-in benchmark telemetry for Direct renderer stages.
/// </summary>
/// <remarks>
/// The hot path does not write files. When the benchmark supplies
/// <c>EASYDICT_RENDERER_BENCHMARK_STAGE_PATH</c>, scopes collect elapsed time and thread-local
/// allocation counts and flush one JSON array at process exit. Production runs remain inert.
/// </remarks>
public static class DirectRendererTelemetry
{
    private const string StagePathVariable = "EASYDICT_RENDERER_BENCHMARK_STAGE_PATH";
    private static readonly ConcurrentQueue<StageSample> Samples = new();
    private static readonly bool Enabled = IsEnabled();
    private static int _flushRegistered;
    private static readonly object FlushLock = new();
    private static int _lastFlushedCount;
    private static long _lastFlushTimestamp;

    static DirectRendererTelemetry()
    {
        if (Enabled && Interlocked.Exchange(ref _flushRegistered, 1) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
    }

    public static Scope Measure(string stage, int itemCount = 0)
    {
        if (Enabled)
        {
            return new Scope(stage, itemCount, enabled: true);
        }
        return default;
    }

    private static bool IsEnabled()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StagePathVariable));
    }

    private static void OnProcessExit(object? sender, EventArgs args) => Flush(force: true);

    /// <summary>Flushes collected samples without waiting for process teardown.</summary>
    public static void Flush(bool force = false)
    {
        if (!Enabled)
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable(StagePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        StageSample[] samples = Samples.ToArray();
        if (samples.Length == 0)
        {
            return;
        }

        lock (FlushLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force
                && samples.Length == _lastFlushedCount
                && now - _lastFlushTimestamp < Stopwatch.Frequency / 4)
            {
                return;
            }

            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(
                        samples,
                        new JsonSerializerOptions { WriteIndented = true }));
                _lastFlushedCount = samples.Length;
                _lastFlushTimestamp = now;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RendererBenchmark] Failed to write stage telemetry: {ex.Message}");
            }
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly string? _stage;
        private readonly int _itemCount;
        private readonly long _started;
        private readonly long _allocated;
        private readonly bool _enabled;

        internal Scope(string stage, int itemCount, bool enabled)
        {
            _stage = stage;
            _itemCount = itemCount;
            _started = Stopwatch.GetTimestamp();
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _enabled = enabled;
        }

        public void Dispose()
        {
            if (!_enabled || _stage is null)
            {
                return;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - _started;
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            Samples.Enqueue(new StageSample(
                _stage,
                elapsedTicks * 1000.0 / Stopwatch.Frequency,
                Math.Max(0, allocatedBytes),
                _itemCount,
                Environment.CurrentManagedThreadId));
        }
    }

    private sealed record StageSample(
        string Stage,
        double ElapsedMilliseconds,
        long AllocatedBytes,
        int ItemCount,
        int ManagedThreadId);
}
