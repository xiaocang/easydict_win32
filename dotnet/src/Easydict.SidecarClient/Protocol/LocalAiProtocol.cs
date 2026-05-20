using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// Methods specific to the local-AI translation worker.
/// </summary>
public static class LocalAiMethods
{
    public const string Translate = "translate";
    public const string TranslateStream = "translate_stream";
    public const string PrepareModel = "prepare_model";
    public const string IsAvailable = "is_available";
    public const string ListModels = "list_models";
    public const string GrammarStream = "grammar_stream";
}

/// <summary>
/// Event names emitted by the local-AI worker during streaming requests.
/// </summary>
public static class LocalAiEvents
{
    /// <summary>One translation chunk — payload: ChunkEventData.</summary>
    public const string Chunk = "chunk";

    /// <summary>Model download progress for OpenVINO bundle. Payload: DownloadProgressEventData.</summary>
    public const string DownloadProgress = "download_progress";
}

/// <summary>
/// Provider mode discriminator passed in translate / translate_stream params.
/// </summary>
public static class LocalAiProviderModes
{
    public const string WindowsAI = "WindowsAI";
    public const string FoundryLocal = "FoundryLocal";
    public const string OpenVINO = "OpenVINO";
    public const string Auto = "Auto";
}

/// <summary>
/// Parameters for "translate" / "translate_stream" / "grammar_stream".
/// </summary>
public sealed class LocalAiTranslateParams
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("fromLanguage")]
    public required string FromLanguage { get; init; }

    [JsonPropertyName("toLanguage")]
    public required string ToLanguage { get; init; }

    [JsonPropertyName("providerMode")]
    public required string ProviderMode { get; init; }

    [JsonPropertyName("customPrompt")]
    public string? CustomPrompt { get; init; }
}

/// <summary>
/// Result of "translate" (non-streaming). The fields mirror TranslationResult.
/// </summary>
public sealed class LocalAiTranslateResult
{
    [JsonPropertyName("translatedText")]
    public required string TranslatedText { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    [JsonPropertyName("serviceName")]
    public required string ServiceName { get; init; }

    [JsonPropertyName("detectedLanguage")]
    public string? DetectedLanguage { get; init; }

    [JsonPropertyName("timingMs")]
    public long TimingMs { get; init; }
}

/// <summary>
/// Payload for the "chunk" event.
/// </summary>
public sealed class ChunkEventData
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// Result of "translate_stream" — the final aggregated result returned after all chunks.
/// </summary>
public sealed class TranslateStreamResult
{
    [JsonPropertyName("done")]
    public required bool Done { get; init; }

    [JsonPropertyName("fullText")]
    public string? FullText { get; init; }
}

/// <summary>
/// Parameters for "prepare_model".
/// </summary>
public sealed class PrepareModelParams
{
    /// <summary>
    /// One of LocalAiProviderModes (excluding "Auto").
    /// </summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>
/// Local model status snapshot (mirrors the in-proc LocalModelStatus).
/// </summary>
public sealed class LocalModelStatusDto
{
    /// <summary>
    /// One of: "Ready", "NeedsPreparation", "Preparing", "Failed", "Unsupported".
    /// </summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("statusKey")]
    public string? StatusKey { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// Payload for "download_progress" event (OpenVINO bundle download).
/// </summary>
public sealed class DownloadProgressEventData
{
    [JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; init; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("currentFile")]
    public string? CurrentFile { get; init; }
}

/// <summary>
/// Parameters for "is_available".
/// </summary>
public sealed class IsAvailableParams
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

/// <summary>
/// Result of "is_available".
/// </summary>
public sealed class IsAvailableResult
{
    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// Parameters for "list_models" (currently only Foundry Local).
/// </summary>
public sealed class ListModelsParams
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

/// <summary>
/// Result of "list_models".
/// </summary>
public sealed class ListModelsResult
{
    [JsonPropertyName("models")]
    public required IReadOnlyList<string> Models { get; init; }
}
