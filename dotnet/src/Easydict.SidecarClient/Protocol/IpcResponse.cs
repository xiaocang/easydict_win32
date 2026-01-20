using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// IPC response message received from sidecar.
/// Success: {"id": "...", "result": {...}}
/// Error: {"id": "...", "error": {"code": "...", "message": "..."}}
/// </summary>
public sealed class IpcResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IpcError? Error { get; init; }

    /// <summary>
    /// Returns true if this response indicates success (has result, no error).
    /// </summary>
    public bool IsSuccess => Error is null && Result.HasValue;

    /// <summary>
    /// Returns true if this response indicates an error.
    /// </summary>
    public bool IsError => Error is not null;
}

/// <summary>
/// Error object in IPC response.
/// </summary>
public sealed class IpcError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Details { get; init; }
}

/// <summary>
/// Well-known error codes from the protocol.
/// </summary>
public static class IpcErrorCodes
{
    public const string InvalidJson = "invalid_json";
    public const string MethodNotFound = "method_not_found";
    public const string InvalidParams = "invalid_params";
    public const string InternalError = "internal_error";
    public const string ServiceError = "service_error";
}

