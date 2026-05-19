using System.Diagnostics;
using Easydict.SidecarClient.Protocol;
using Easydict.Workers.LocalAi.Handlers;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi;

/// <summary>
/// Local-AI translation worker. Spawned per translate / prepare request by the
/// host's LocalAiWorkerClient. Wraps PhiSilica / Foundry Local / OpenVINO so
/// their native runtimes (Windows AI, OpenVINO ORT sessions, model bytes) sit
/// in this child process — completion exits the worker and reclaims that memory.
///
/// Lifecycle:
///   1. Worker emits "ready" on startup.
///   2. Host sends "configure" with a SettingsSnapshot.
///   3. Host optionally sends "prepare_model" / "is_available" / "list_models".
///   4. Host sends one of "translate" / "translate_stream" / "grammar_stream".
///   5. After the translate request completes (or stdin EOF / shutdown), the
///      worker calls Environment.Exit(0).
/// </summary>
internal static class Program
{
    private static readonly TaskCompletionSource _shutdownRequested = new();

    public static async Task<int> Main(string[] args)
    {
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;

        var writer = new IpcEventWriter(Console.Out);
        var dispatcher = new RequestDispatcher(writer);
        var state = new WorkerState();

        dispatcher.Register(WorkerMethods.Configure, new ConfigureHandler(state).HandleAsync);
        dispatcher.Register(LocalAiMethods.Translate, new TranslateHandler(state).HandleAsync);
        dispatcher.Register(LocalAiMethods.TranslateStream,
            new TranslateStreamHandler(state, writer).HandleAsync);
        dispatcher.Register(LocalAiMethods.GrammarStream,
            new GrammarStreamHandler(state, writer).HandleAsync);
        dispatcher.Register(LocalAiMethods.PrepareModel, new PrepareModelHandler(state, writer).HandleAsync);
        dispatcher.Register(LocalAiMethods.IsAvailable, new IsAvailableHandler(state).HandleAsync);
        dispatcher.Register(LocalAiMethods.ListModels, new ListModelsHandler(state).HandleAsync);
        dispatcher.Register(WorkerMethods.Cancel, new CancelHandler(dispatcher).HandleAsync);
        dispatcher.Register(WorkerMethods.Shutdown, (_, _, _) =>
        {
            _shutdownRequested.TrySetResult();
            return Task.FromResult<object?>(new { ok = true });
        });

        await writer.WriteEventAsync(WorkerEvents.Ready, new ReadyEventData
        {
            WorkerKind = WorkerKinds.LocalAi,
            WorkerVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ProtocolVersion = WorkerProtocolVersion.Current,
            Capabilities = new[]
            {
                WorkerMethods.Configure,
                LocalAiMethods.Translate,
                LocalAiMethods.TranslateStream,
                LocalAiMethods.GrammarStream,
                LocalAiMethods.PrepareModel,
                LocalAiMethods.IsAvailable,
                LocalAiMethods.ListModels,
                WorkerMethods.Cancel,
                WorkerMethods.Shutdown,
            },
        });

        using var reader = new StreamReader(Console.OpenStandardInput());
        var stdinLoop = Task.Run(async () =>
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (_shutdownRequested.Task.IsCompleted) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                _ = dispatcher.DispatchAsync(line, OnRequestCompleted);
            }
            _shutdownRequested.TrySetResult();
        });

        await _shutdownRequested.Task;
        await Task.WhenAny(stdinLoop, Task.Delay(200));
        return 0;
    }

    /// <summary>
    /// Worker exits after the first non-prepare request completes. This matches
    /// the "exit on completion" lifecycle: host calls prepare_model (or skips it),
    /// then translate / translate_stream / grammar_stream, then closes stdin.
    /// </summary>
    private static void OnRequestCompleted(string method)
    {
        if (method == LocalAiMethods.Translate
            || method == LocalAiMethods.TranslateStream
            || method == LocalAiMethods.GrammarStream)
        {
            _shutdownRequested.TrySetResult();
        }
    }
}
