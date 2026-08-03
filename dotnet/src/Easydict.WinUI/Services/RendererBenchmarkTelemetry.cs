using System.Diagnostics;
using Easydict.DirectXaml.Win2D;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Services;

/// <summary>
/// Opt-in marker writer used by the paired Direct/XAML renderer benchmark.
/// </summary>
/// <remarks>
/// The production renderer never enables this probe: every operation is inert unless the
/// benchmark driver supplies its marker paths through process environment variables. The
/// submitted marker is emitted on the UI thread immediately before the deterministic result
/// mutation. XAML completes on its next composition rendering callback; Direct completes when
/// the corresponding Win2D card draw has executed.
/// </remarks>
internal static class RendererBenchmarkTelemetry
{
    private const string FirstResultSubmittedPathVariable =
        "EASYDICT_RENDERER_BENCHMARK_FIRST_RESULT_SUBMITTED_MARKER_PATH";
    private const string FirstResultRenderedPathVariable =
        "EASYDICT_RENDERER_BENCHMARK_FIRST_RESULT_RENDERED_MARKER_PATH";
    private const string StreamingStartedPathVariable =
        "EASYDICT_RENDERER_BENCHMARK_STREAMING_STARTED_MARKER_PATH";
    private const string StreamingCompletedPathVariable =
        "EASYDICT_RENDERER_BENCHMARK_STREAMING_COMPLETED_MARKER_PATH";

    private static int _firstResultPending;
    private static int _xamlFirstResultFrameQueued;
    private static int _streamingCompletionPending;
    private static int _streamingCompletionFrameQueued;

    public static bool IsFirstResultPending
    {
        get
        {
            return Volatile.Read(ref _firstResultPending) != 0;
        }
    }

    /// <summary>Starts one first-result timing window if the benchmark configured marker paths.</summary>
    public static void BeginFirstResult()
    {
        string? submittedPath = Environment.GetEnvironmentVariable(FirstResultSubmittedPathVariable);
        string? renderedPath = Environment.GetEnvironmentVariable(FirstResultRenderedPathVariable);
        if (string.IsNullOrWhiteSpace(submittedPath) || string.IsNullOrWhiteSpace(renderedPath))
        {
            return;
        }

        TryDelete(renderedPath);
        Volatile.Write(ref _firstResultPending, 1);
        Interlocked.Exchange(ref _xamlFirstResultFrameQueued, 0);
        if (!TryWriteTimestamp(submittedPath))
        {
            Volatile.Write(ref _firstResultPending, 0);
        }
    }

    /// <summary>Queues the XAML-side first composition rendering marker.</summary>
    public static void QueueXamlFirstResultFrame()
    {
        if (!IsFirstResultPending
            || Interlocked.CompareExchange(ref _xamlFirstResultFrameQueued, 1, 0) != 0)
        {
            return;
        }

        CompositionTarget.Rendering += OnXamlFirstResultRendering;
    }

    /// <summary>Completes the Direct-side timing window after the target card was drawn.</summary>
    public static void ReportDirectFirstResultDrawn()
    {
        CompleteFirstResult();
        DirectRendererTelemetry.Flush();
    }

    /// <summary>Starts the controlled streaming-update CPU window.</summary>
    public static bool BeginStreaming()
    {
        string? startedPath = Environment.GetEnvironmentVariable(StreamingStartedPathVariable);
        string? completedPath = Environment.GetEnvironmentVariable(StreamingCompletedPathVariable);
        if (string.IsNullOrWhiteSpace(startedPath) || string.IsNullOrWhiteSpace(completedPath))
        {
            return false;
        }

        TryDelete(completedPath);
        Volatile.Write(ref _streamingCompletionPending, 1);
        Interlocked.Exchange(ref _streamingCompletionFrameQueued, 0);
        if (TryWriteTimestamp(startedPath))
        {
            return true;
        }

        Volatile.Write(ref _streamingCompletionPending, 0);
        return false;
    }

    /// <summary>
    /// Marks the streaming CPU window after the final coalesced snapshot has been applied and
    /// WinUI receives its following composition rendering callback.
    /// </summary>
    public static void QueueStreamingCompletionFrame()
    {
        if (Interlocked.CompareExchange(ref _streamingCompletionPending, 0, 1) != 1
            || Interlocked.CompareExchange(ref _streamingCompletionFrameQueued, 1, 0) != 0)
        {
            return;
        }

        CompositionTarget.Rendering += OnStreamingCompletionRendering;
    }

    private static void OnXamlFirstResultRendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnXamlFirstResultRendering;
        Interlocked.Exchange(ref _xamlFirstResultFrameQueued, 0);
        CompleteFirstResult();
    }

    private static void OnStreamingCompletionRendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnStreamingCompletionRendering;
        Interlocked.Exchange(ref _streamingCompletionFrameQueued, 0);
        string? completedPath = Environment.GetEnvironmentVariable(StreamingCompletedPathVariable);
        if (!string.IsNullOrWhiteSpace(completedPath))
        {
            TryWriteTimestamp(completedPath);
        }
        DirectRendererTelemetry.Flush();
    }

    private static void CompleteFirstResult()
    {
        if (Interlocked.CompareExchange(ref _firstResultPending, 0, 1) != 1)
        {
            return;
        }

        string? renderedPath = Environment.GetEnvironmentVariable(FirstResultRenderedPathVariable);
        if (!string.IsNullOrWhiteSpace(renderedPath))
        {
            TryWriteTimestamp(renderedPath);
        }
    }

    private static bool TryWriteTimestamp(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RendererBenchmark] Failed to write '{path}': {ex.Message}");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RendererBenchmark] Failed to delete '{path}': {ex.Message}");
        }
    }
}
