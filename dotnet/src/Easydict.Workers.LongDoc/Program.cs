using System.Diagnostics;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Easydict.Workers.LongDoc.Handlers;
using Easydict.Workers.LongDoc.Infrastructure;

namespace Easydict.Workers.LongDoc;

/// <summary>
/// Long-document translation worker. Runs as a child process spawned by the
/// main Easydict.WinUI process. Communicates over stdin/stdout using the same
/// JSON Lines protocol as Easydict.SidecarClient.
///
/// Lifecycle:
///   1. Worker starts, redirects Debug/Trace output to stderr.
///   2. Worker writes a single "ready" event to stdout announcing protocol version + capabilities.
///   3. Host sends "configure" with a SettingsSnapshot. Worker stashes settings in WorkerState.
///   4. Host sends "translate_document". Worker streams status/progress/block_translated events
///      and returns the final response.
///   5. Worker calls Environment.Exit(0) after the response is written (per the
///      "exit on completion" lifecycle the user selected). MuPDF / ONNX native heap
///      is fully reclaimed because the process exits.
/// </summary>
internal static class Program
{
    /// <summary>Exit code: clean shutdown.</summary>
    private const int ExitClean = 0;

    /// <summary>Exit code: handshake or configure failed.</summary>
    private const int ExitConfigureFailed = 1;

    /// <summary>Exit code: a translate request crashed with an unhandled exception.</summary>
    private const int ExitUnhandledTranslate = 2;

    private static readonly TaskCompletionSource _shutdownRequested = new();

    public static async Task<int> Main(string[] args)
    {
        WorkerSharedAssemblyResolver.Install();

        // Redirect Debug.WriteLine / Trace.WriteLine to stderr so they don't pollute stdout
        // (which is reserved for JSON Lines protocol messages going to the host).
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;

        var writer = new IpcEventWriter(Console.Out);
        var dispatcher = new RequestDispatcher(writer);

        var state = new WorkerState();
        dispatcher.Register(WorkerMethods.Configure, new ConfigureHandler(state).HandleAsync);
        dispatcher.Register(LongDocMethods.TranslateDocument, new TranslateDocumentHandler(state, writer).HandleAsync);
        dispatcher.Register(WorkerMethods.Cancel, new CancelHandler(dispatcher).HandleAsync);
        dispatcher.Register(WorkerMethods.Shutdown, (_, _, _) =>
        {
            _shutdownRequested.TrySetResult();
            return Task.FromResult<object?>(new { ok = true });
        });
        dispatcher.OnRequestCompleted = method =>
        {
            if (method != LongDocMethods.TranslateDocument)
            {
                return;
            }

            Trace.WriteLine("[LongDocWorker] translate_document completed; requesting process shutdown.");
            state.LastExitCode = ResolveExitCode(WorkerExitReason.Clean);
            _shutdownRequested.TrySetResult();
        };

        await writer.WriteEventAsync(WorkerEvents.Ready, new ReadyEventData
        {
            WorkerKind = WorkerKinds.LongDoc,
            WorkerVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ProtocolVersion = WorkerProtocolVersion.Current,
            Capabilities = new[]
            {
                WorkerMethods.Configure,
                LongDocMethods.TranslateDocument,
                WorkerMethods.Cancel,
                WorkerMethods.Shutdown,
            },
        });

        // Read stdin in the main task; dispatch each line concurrently.
        using var reader = new StreamReader(Console.OpenStandardInput());
        var stdinLoop = Task.Run(async () =>
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (_shutdownRequested.Task.IsCompleted) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                _ = dispatcher.DispatchAsync(line); // fire-and-forget; dispatcher serializes writes
            }
            _shutdownRequested.TrySetResult();
        });

        await _shutdownRequested.Task;

        // Give in-flight requests a brief window to finish writing their final response.
        await Task.WhenAny(stdinLoop, Task.Delay(200));

        return state.LastExitCode ?? ExitClean;
    }

    internal static int ResolveExitCode(WorkerExitReason reason) => reason switch
    {
        WorkerExitReason.Clean => ExitClean,
        WorkerExitReason.ConfigureFailed => ExitConfigureFailed,
        WorkerExitReason.UnhandledTranslate => ExitUnhandledTranslate,
        _ => ExitClean,
    };
}

internal enum WorkerExitReason
{
    Clean,
    ConfigureFailed,
    UnhandledTranslate,
}
