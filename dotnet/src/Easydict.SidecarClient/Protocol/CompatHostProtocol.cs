using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// Facade methods exposed by the temporary .NET Compat Host while the Rust
/// desktop owner ports behavior module by module.
/// </summary>
public static class CompatHostMethods
{
    public const string Translate = "translate";
    public const string TranslateStream = "translate_stream";
    public const string GrammarCorrect = "grammar_correct";
    public const string OcrRecognize = "ocr_recognize";
    public const string LongDocTranslate = "longdoc_translate";
    public const string LocalAiPrepare = "local_ai_prepare";
    public const string LocalAiTranslate = "local_ai_translate";
    public const string MdxLookup = "mdx_lookup";
    public const string SettingsMigrate = "settings_migrate";
}

/// <summary>
/// Facade translation result used by the Rust shell for non-worker-specific
/// translation calls. The field names mirror TranslationResult enough for UI
/// hydration without coupling the Rust side to the .NET model assembly.
/// </summary>
public sealed class TranslationResultDto
{
    [JsonPropertyName("translatedText")]
    public required string TranslatedText { get; init; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; init; }

    [JsonPropertyName("detectedLanguage")]
    public string? DetectedLanguage { get; init; }

    [JsonPropertyName("resultKind")]
    public string? ResultKind { get; init; }

    [JsonPropertyName("infoMessage")]
    public string? InfoMessage { get; init; }

    [JsonPropertyName("timingMs")]
    public long? TimingMs { get; init; }
}

public sealed class TranslateChunkEventData
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed class GrammarCorrectParams
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    [JsonPropertyName("services")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Services { get; init; }

    [JsonPropertyName("includeExplanations")]
    public bool IncludeExplanations { get; init; } = true;
}

public sealed class GrammarCorrectResultDto
{
    [JsonPropertyName("originalText")]
    public required string OriginalText { get; init; }

    [JsonPropertyName("correctedText")]
    public required string CorrectedText { get; init; }

    [JsonPropertyName("explanation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Explanation { get; init; }

    [JsonPropertyName("rawText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawText { get; init; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("timingMs")]
    public long? TimingMs { get; init; }

    [JsonPropertyName("hasCorrections")]
    public bool HasCorrections { get; init; }
}

public sealed class GrammarChunkEventData
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed class MdxLookupParams
{
    [JsonPropertyName("dictionaryId")]
    public required string DictionaryId { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("fuzzy")]
    public bool Fuzzy { get; init; }
}

public sealed class MdxLookupResult
{
    [JsonPropertyName("entries")]
    public required IReadOnlyList<MdxLookupEntry> Entries { get; init; }
}

public sealed class MdxLookupEntry
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("html")]
    public required string Html { get; init; }

    [JsonPropertyName("dictionaryName")]
    public string? DictionaryName { get; init; }
}

public sealed class SettingsMigrateParams
{
    [JsonPropertyName("legacySettingsPath")]
    public string? LegacySettingsPath { get; init; }

    [JsonPropertyName("targetSettingsPath")]
    public string? TargetSettingsPath { get; init; }
}

public sealed class SettingsMigrateResult
{
    [JsonPropertyName("migrated")]
    public required bool Migrated { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
