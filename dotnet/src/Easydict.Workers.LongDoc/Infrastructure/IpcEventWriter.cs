using System.Text.Json;
using Easydict.SidecarClient.Protocol;

namespace Easydict.Workers.LongDoc.Infrastructure;

/// <summary>
/// Serializes IPC messages (responses and events) to a TextWriter (stdout).
/// All writes are serialized through a single SemaphoreSlim so concurrent
/// handlers don't interleave their JSON lines and corrupt the protocol stream.
/// </summary>
internal sealed class IpcEventWriter
{
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public IpcEventWriter(TextWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public async Task WriteResponseAsync(string requestId, object? result)
    {
        var response = new IpcResponse { Id = requestId, Result = SerializeToElement(result) };
        var line = JsonLineSerializer.SerializeLine(response);
        await WriteLineAsync(line);
    }

    public async Task WriteErrorAsync(string requestId, string code, string message, object? details = null)
    {
        var response = new IpcResponse
        {
            Id = requestId,
            Error = new IpcError
            {
                Code = code,
                Message = message,
                Details = details is null ? null : SerializeToElement(details),
            },
        };
        var line = JsonLineSerializer.SerializeLine(response);
        await WriteLineAsync(line);
    }

    public async Task WriteEventAsync(string eventName, object? data, string? requestId = null)
    {
        var evt = new IpcEvent
        {
            Event = eventName,
            Id = requestId,
            Data = data is null ? null : SerializeToElement(data),
        };
        var line = JsonLineSerializer.SerializeLine(evt);
        await WriteLineAsync(line);
    }

    private async Task WriteLineAsync(string line)
    {
        await _lock.WaitAsync();
        try
        {
            await _writer.WriteAsync(line);
            await _writer.FlushAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static JsonElement SerializeToElement(object? value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        return doc.RootElement.Clone();
    }
}
