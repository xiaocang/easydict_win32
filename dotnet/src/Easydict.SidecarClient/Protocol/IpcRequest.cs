using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// IPC request message sent from UI to sidecar.
/// Format: {"id": "...", "method": "...", "params": {...}}
/// </summary>
public sealed class IpcRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Params { get; init; }
}

