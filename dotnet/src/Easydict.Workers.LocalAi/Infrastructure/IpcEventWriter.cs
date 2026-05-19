using System.Text.Json;
using Easydict.SidecarClient.Protocol;

namespace Easydict.Workers.LocalAi.Infrastructure;

/// <summary>
/// Serializes IPC messages to stdout. All writes go through a single SemaphoreSlim
/// so concurrent handlers don't interleave their JSON lines.
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
        await WriteLineAsync(JsonLineSerializer.SerializeLine(response));
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
        await WriteLineAsync(JsonLineSerializer.SerializeLine(response));
    }

    public async Task WriteEventAsync(string eventName, object? data, string? requestId = null)
    {
        var evt = new IpcEvent
        {
            Event = eventName,
            Id = requestId,
            Data = data is null ? null : SerializeToElement(data),
        };
        await WriteLineAsync(JsonLineSerializer.SerializeLine(evt));
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
