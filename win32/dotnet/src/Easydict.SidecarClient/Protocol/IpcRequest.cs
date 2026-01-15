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

/// <summary>
/// Parameters for the "translate" method.
/// </summary>
public sealed class TranslateParams
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; init; }

    [JsonPropertyName("services")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Services { get; init; }
}

