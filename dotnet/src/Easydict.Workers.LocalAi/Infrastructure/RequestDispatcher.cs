using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;

namespace Easydict.Workers.LocalAi.Infrastructure;

/// <summary>
/// Routes IPC requests to handlers. Tracks in-flight CancellationTokenSources
/// so "cancel" can target them by id.
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

    public bool TryCancel(string requestId)
    {
        if (!_inflight.TryGetValue(requestId, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    public async Task DispatchAsync(string jsonLine, Action<string>? onCompleted = null)
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
            onCompleted?.Invoke(request.Method);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await _writer.WriteErrorAsync(request.Id, WorkerErrorCodes.Cancelled,
                $"Request {request.Id} cancelled");
            onCompleted?.Invoke(request.Method);
        }
        catch (WorkerHandlerException ex)
        {
            await _writer.WriteErrorAsync(request.Id, ex.Code, ex.Message, ex.Details);
            onCompleted?.Invoke(request.Method);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Worker] Unhandled exception in {request.Method}: {ex}");
            await _writer.WriteErrorAsync(request.Id, WorkerErrorCodes.Internal,
                ex.Message,
                new { exception = ex.GetType().FullName, stackTrace = ex.StackTrace });
            onCompleted?.Invoke(request.Method);
        }
        finally
        {
            _inflight.TryRemove(request.Id, out _);
            cts.Dispose();
        }
    }
}

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
