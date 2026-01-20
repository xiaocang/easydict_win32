using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// Raw IPC message for initial parsing (to determine if it's a response or event).
/// </summary>
internal sealed class IpcMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public IpcError? Error { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    /// <summary>
    /// Returns true if this message is an event (has "event" field).
    /// </summary>
    public bool IsEvent => Event is not null;

    /// <summary>
    /// Returns true if this message is a response (has "id" but no "event").
    /// </summary>
    public bool IsResponse => Id is not null && Event is null;

    /// <summary>
    /// Convert to IpcResponse if this is a response message.
    /// </summary>
    public IpcResponse ToResponse() => new()
    {
        Id = Id,
        Result = Result,
        Error = Error
    };

    /// <summary>
    /// Convert to IpcEvent if this is an event message.
    /// </summary>
    public IpcEvent ToEvent() => new()
    {
        Event = Event!,
        Id = Id,
        Data = Data
    };
}

