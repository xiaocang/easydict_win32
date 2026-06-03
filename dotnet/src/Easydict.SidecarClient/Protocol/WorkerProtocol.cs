using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// Method names and event names shared across all Easydict worker processes.
/// </summary>
public static class WorkerMethods
{
    /// <summary>
    /// Configure the worker with a SettingsSnapshot. Must be the first request
    /// (after the unprompted "ready" event arrives from the worker).
    /// </summary>
    public const string Configure = "configure";

    /// <summary>
    /// Cancel an in-flight request by its id. Payload: CancelRequestParams.
    /// </summary>
    public const string Cancel = "cancel";

    /// <summary>
    /// Graceful shutdown — worker exits with code 0 after writing the response.
    /// </summary>
    public const string Shutdown = "shutdown";
}

/// <summary>
/// Event names shared across workers. Worker-specific events live in their own
/// protocol classes (LongDocProtocol, LocalAiProtocol).
/// </summary>
public static class WorkerEvents
{
    /// <summary>
    /// Unprompted event emitted exactly once when the worker is ready to accept
    /// requests. Payload: ReadyEventData.
    /// </summary>
    public const string Ready = "ready";
}

/// <summary>
/// Worker-side error codes (string-valued for protocol stability across versions).
/// </summary>
public static class WorkerErrorCodes
{
    public const string Cancelled = "cancelled";
    public const string ModelMissing = "model_missing";
    public const string InvalidParams = "invalid_params";
    public const string ServiceError = "service_error";
    public const string Internal = "internal_error";
    public const string VersionMismatch = "version_mismatch";
}

/// <summary>
/// Schema of the unprompted "ready" event emitted by each worker on startup.
/// Host blocks on this event before sending any requests.
/// </summary>
public sealed class ReadyEventData
{
    [JsonPropertyName("workerKind")]
    public required string WorkerKind { get; init; }

    [JsonPropertyName("workerVersion")]
    public required string WorkerVersion { get; init; }

    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; }
}

/// <summary>
/// Worker kind discriminator used in the "ready" handshake.
/// </summary>
public static class WorkerKinds
{
    public const string LongDoc = "longdoc";
    public const string LocalAi = "localai";
}

/// <summary>
/// Parameters for the shared "configure" method.
/// </summary>
public sealed class ConfigureParams
{
    [JsonPropertyName("settings")]
    public required SettingsSnapshot Settings { get; init; }
}

/// <summary>
/// Result of the shared "configure" method.
/// </summary>
public sealed class ConfigureResult
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }
}

/// <summary>
/// Parameters for the shared "cancel" method.
/// </summary>
public sealed class CancelRequestParams
{
    /// <summary>
    /// Id of the in-flight request to cancel.
    /// </summary>
    [JsonPropertyName("targetRequestId")]
    public required string TargetRequestId { get; init; }
}

/// <summary>
/// Result of the shared "cancel" method.
/// </summary>
public sealed class CancelRequestResult
{
    [JsonPropertyName("cancelled")]
    public required bool Cancelled { get; init; }
}

/// <summary>
/// The current protocol version. Bump when the wire format changes in a
/// non-backward-compatible way. Workers report this in their ready event;
/// the host validates it on handshake and rejects mismatched versions.
/// </summary>
public static class WorkerProtocolVersion
{
    public const int Current = 1;
}
