using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// Methods specific to the local-AI translation worker.
/// </summary>
public static class LocalAiMethods
{
    public const string TranslateStream = "translate_stream";
    public const string GrammarStream = "grammar_stream";
}

/// <summary>
/// Event names emitted by the local-AI worker during streaming requests.
/// </summary>
public static class LocalAiEvents
{
    /// <summary>One translation chunk — payload: ChunkEventData.</summary>
    public const string Chunk = "chunk";
}

/// <summary>
/// Provider mode discriminator passed in translate_stream / grammar_stream params.
/// </summary>
public static class LocalAiProviderModes
{
    public const string WindowsAI = "WindowsAI";
    public const string FoundryLocal = "FoundryLocal";
    public const string OpenVINO = "OpenVINO";
    public const string Auto = "Auto";
}

/// <summary>
/// Parameters for "translate_stream" / "grammar_stream".
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

    [JsonPropertyName("includeExplanations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeExplanations { get; init; }
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
