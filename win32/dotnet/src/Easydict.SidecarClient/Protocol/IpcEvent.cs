using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// IPC event message (server-initiated, optional streaming).
/// Format: {"event": "...", "id": "...", "data": {...}}
/// </summary>
public sealed class IpcEvent
{
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }
}

/// <summary>
/// Well-known event types from the protocol.
/// </summary>
public static class IpcEventTypes
{
    public const string TranslateChunk = "translate_chunk";
    public const string TranslateDone = "translate_done";
}

