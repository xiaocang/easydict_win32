using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;

namespace Easydict.Workers.LongDoc.Infrastructure;

/// <summary>
/// Routes incoming IPC requests to registered handlers. Tracks in-flight
/// CancellationTokenSources so the "cancel" method can target them by id.
/// </summary>
internal sealed class RequestDispatcher
{
    private readonly IpcEventWriter _writer;
    private readonly ConcurrentDictionary<string, HandlerDelegate> _handlers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();

    public RequestDispatcher(IpcEventWriter writer)
    {
        _writer = writer;
    }

    public delegate Task<object?> HandlerDelegate(string requestId, JsonElement? parameters, CancellationToken cancellationToken);

    public void Register(string method, HandlerDelegate handler)
    {
        _handlers[method] = handler;
    }

    /// <summary>
    /// Optional callback invoked after every successfully-dispatched request
    /// completes (after the response is written). Used by LocalAi worker's
    /// "exit-after-translate" lifecycle to signal Program.Main to shut down.
    /// </summary>
    public Action<string>? OnRequestCompleted { get; set; }

    public bool TryCancel(string requestId)
    {
        if (!_inflight.TryGetValue(requestId, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>
    /// Parse a JSON line and dispatch it. Errors are reported as IPC error responses
    /// (or silently dropped if the line is not a parseable IpcRequest, since the
    /// host should not be sending malformed input).
    /// </summary>
    public async Task DispatchAsync(string jsonLine)
    {
        IpcRequest? request;
        try
        {
            request = JsonLineSerializer.Deserialize<IpcRequest>(jsonLine);
        }
        catch (JsonException ex)
        {
            Trace.WriteLine($"[Worker] Malformed JSON on stdin: {ex.Message}");
            return;
        }

        if (request is null || string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.Method))
        {
            Trace.WriteLine("[Worker] Missing id/method on inbound request");
            return;
        }

        if (!_handlers.TryGetValue(request.Method, out var handler))
        {
            await _writer.WriteErrorAsync(request.Id, IpcErrorCodes.MethodNotFound,
                $"Unknown method: {request.Method}");
            return;
        }

        // params arrives as object via System.Text.Json. Re-serialize and parse as JsonElement
        // for ergonomic typed access inside handlers.
        JsonElement? parameters = null;
        if (request.Params is JsonElement el)
        {
            parameters = el;
        }
        else if (request.Params is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request.Params);
            using var doc = JsonDocument.Parse(bytes);
            parameters = doc.RootElement.Clone();
        }

        var cts = new CancellationTokenSource();
        _inflight[request.Id] = cts;

        try
        {
            var result = await handler(request.Id, parameters, cts.Token);
            await _writer.WriteResponseAsync(request.Id, result);
            OnRequestCompleted?.Invoke(request.Method);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await _writer.WriteErrorAsync(request.Id, WorkerErrorCodes.Cancelled,
                $"Request {request.Id} cancelled");
        }
        catch (WorkerHandlerException ex)
        {
            await _writer.WriteErrorAsync(request.Id, ex.Code, ex.Message, ex.Details);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Worker] Unhandled exception in {request.Method}: {ex}");
            await _writer.WriteErrorAsync(request.Id, WorkerErrorCodes.Internal,
                ex.Message,
                new { exception = ex.GetType().FullName, stackTrace = ex.StackTrace });
        }
        finally
        {
            _inflight.TryRemove(request.Id, out _);
            cts.Dispose();
        }
    }
}

/// <summary>
/// Throw from a handler to surface a typed error to the host. Use the
/// WorkerErrorCodes constants for "code".
/// </summary>
internal sealed class WorkerHandlerException : Exception
{
    public string Code { get; }
    public object? Details { get; }

    public WorkerHandlerException(string code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }
}
