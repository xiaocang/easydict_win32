using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Easydict.WinUI.Services;

/// <summary>
/// DEBUG opt-in diagnostics for UI-thread hotspot probes.
/// Enable with EASYDICT_DEBUG_UI_THREAD_HOTSPOTS=1. Set
/// EASYDICT_UI_THREAD_HOTSPOT_LOG_PATH to make scripts parse the same
/// [UIHotspot] lines that also go to Debug output.
/// </summary>
internal static class UiThreadHotspotDiagnostics
{
    private const string EnabledEnvVar = "EASYDICT_DEBUG_UI_THREAD_HOTSPOTS";
    private const string ThresholdEnvVar = "EASYDICT_DEBUG_UI_THREAD_HOTSPOT_THRESHOLD_MS";
    private const string LogPathEnvVar = "EASYDICT_UI_THREAD_HOTSPOT_LOG_PATH";
    private const int DefaultThresholdMs = 4;

#if DEBUG
    private static readonly bool Enabled = IsDebugEnvFlagEnabled(EnabledEnvVar);
    private static readonly double ThresholdMs = ResolveThresholdMs();
    private static readonly string? LogPath = Environment.GetEnvironmentVariable(LogPathEnvVar);
    private static readonly ConcurrentQueue<string> PendingLogLines = new();
    private static readonly SemaphoreSlim LogSignal = new(0);
    private static int _writerStarted;
    private static volatile bool _writerDisabled;
#endif

    public static Measurement Measure(string operation)
    {
#if DEBUG
        if (!Enabled)
        {
            return default;
        }

        return new Measurement(operation, Stopwatch.GetTimestamp());
#else
        return default;
#endif
    }

    public static void LogCounter(string operation, int count)
    {
#if DEBUG
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"[UIHotspot] kind=count op={operation} count={count.ToString(CultureInfo.InvariantCulture)} thread={Environment.CurrentManagedThreadId}");
#endif
    }

    public readonly struct Measurement : IDisposable
    {
        private readonly string? _operation;
        private readonly long _startTimestamp;

        internal Measurement(string operation, long startTimestamp)
        {
            _operation = operation;
            _startTimestamp = startTimestamp;
        }

        public void Dispose()
        {
#if DEBUG
            if (_operation is null)
            {
                return;
            }

            var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            if (elapsedMs < ThresholdMs)
            {
                return;
            }

            WriteLine(
                "[UIHotspot] kind=duration " +
                $"op={_operation} " +
                $"elapsedMs={elapsedMs.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"thresholdMs={ThresholdMs.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"thread={Environment.CurrentManagedThreadId}");
#endif
        }
    }

#if DEBUG
    private static double ResolveThresholdMs()
    {
        var value = Environment.GetEnvironmentVariable(ThresholdEnvVar);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) &&
            threshold >= 0)
        {
            return threshold;
        }

        return DefaultThresholdMs;
    }

    private static bool IsDebugEnvFlagEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteLine(string line)
    {
        Debug.WriteLine(line);

        if (string.IsNullOrWhiteSpace(LogPath) || _writerDisabled)
        {
            return;
        }

        EnsureWriterStarted();
        PendingLogLines.Enqueue($"{DateTimeOffset.UtcNow:O} {line}");
        LogSignal.Release();
    }

    private static void EnsureWriterStarted()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(ProcessLogQueueAsync);
    }

    private static async Task ProcessLogQueueAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = new FileStream(
                LogPath!,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };

            while (true)
            {
                await LogSignal.WaitAsync().ConfigureAwait(false);
                while (PendingLogLines.TryDequeue(out var line))
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _writerDisabled = true;
            Debug.WriteLine($"[UIHotspot] kind=error op=LogWriter message={ex.Message}");
        }
    }
#endif
}
