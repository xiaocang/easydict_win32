using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// Snapshot of settings (already-decrypted API keys, endpoints, model names,
/// proxy/network settings) that the host sends to a worker via the `configure`
/// request immediately after handshake.
///
/// Workers do NOT read SettingsService directly — that would couple them to the
/// WinUI app's settings persistence layer and force them to know how to decrypt
/// DPAPI/AES-protected secrets. The host owns secret decryption; the worker
/// receives plaintext values over stdin (anonymous pipe, owned by the parent
/// process — unprivileged readers can't snoop) and keeps them in memory only.
///
/// All fields are optional so the snapshot only carries values relevant to the
/// worker it targets. Long-doc worker needs LLM provider credentials for whichever
/// service the user selects; local-AI worker needs only endpoints + model names.
/// </summary>
public sealed class SettingsSnapshot
{
    // ── Cloud LLM provider credentials (long-doc worker) ────────────────────

    [JsonPropertyName("openAIApiKey")]
    public string? OpenAIApiKey { get; init; }

    [JsonPropertyName("openAIEndpoint")]
    public string? OpenAIEndpoint { get; init; }

    [JsonPropertyName("openAIModel")]
    public string? OpenAIModel { get; init; }

    [JsonPropertyName("openAITemperature")]
    public float? OpenAITemperature { get; init; }

    [JsonPropertyName("openAIApiFormatOverride")]
    public string? OpenAIApiFormatOverride { get; init; }

    [JsonPropertyName("deepLApiKey")]
    public string? DeepLApiKey { get; init; }

    [JsonPropertyName("deepLUseFreeApi")]
    public bool? DeepLUseFreeApi { get; init; }

    [JsonPropertyName("deepLUseQualityOptimized")]
    public bool? DeepLUseQualityOptimized { get; init; }

    [JsonPropertyName("deepSeekApiKey")]
    public string? DeepSeekApiKey { get; init; }

    [JsonPropertyName("deepSeekModel")]
    public string? DeepSeekModel { get; init; }

    [JsonPropertyName("geminiApiKey")]
    public string? GeminiApiKey { get; init; }

    [JsonPropertyName("geminiModel")]
    public string? GeminiModel { get; init; }

    [JsonPropertyName("groqApiKey")]
    public string? GroqApiKey { get; init; }

    [JsonPropertyName("groqModel")]
    public string? GroqModel { get; init; }

    [JsonPropertyName("zhipuApiKey")]
    public string? ZhipuApiKey { get; init; }

    [JsonPropertyName("zhipuModel")]
    public string? ZhipuModel { get; init; }

    [JsonPropertyName("doubaoApiKey")]
    public string? DoubaoApiKey { get; init; }

    [JsonPropertyName("doubaoModel")]
    public string? DoubaoModel { get; init; }

    [JsonPropertyName("githubModelsApiKey")]
    public string? GitHubModelsApiKey { get; init; }

    [JsonPropertyName("githubModelsModel")]
    public string? GitHubModelsModel { get; init; }

    [JsonPropertyName("caiyunToken")]
    public string? CaiyunToken { get; init; }

    [JsonPropertyName("niuTransApiKey")]
    public string? NiuTransApiKey { get; init; }

    [JsonPropertyName("customOpenAIApiKey")]
    public string? CustomOpenAIApiKey { get; init; }

    [JsonPropertyName("customOpenAIEndpoint")]
    public string? CustomOpenAIEndpoint { get; init; }

    [JsonPropertyName("customOpenAIModel")]
    public string? CustomOpenAIModel { get; init; }

    [JsonPropertyName("ollamaEndpoint")]
    public string? OllamaEndpoint { get; init; }

    [JsonPropertyName("ollamaModel")]
    public string? OllamaModel { get; init; }

    [JsonPropertyName("builtInAIModel")]
    public string? BuiltInAIModel { get; init; }

    [JsonPropertyName("builtInAIApiKey")]
    public string? BuiltInAIApiKey { get; init; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("deviceToken")]
    public string? DeviceToken { get; init; }

    // ── Local AI provider config (local-AI worker) ──────────────────────────

    [JsonPropertyName("foundryLocalEndpoint")]
    public string? FoundryLocalEndpoint { get; init; }

    [JsonPropertyName("foundryLocalModel")]
    public string? FoundryLocalModel { get; init; }

    [JsonPropertyName("openVinoDevice")]
    public string? OpenVinoDevice { get; init; }

    [JsonPropertyName("localAIProvider")]
    public string? LocalAIProvider { get; init; }

    // ── Network ─────────────────────────────────────────────────────────────

    [JsonPropertyName("proxyEnabled")]
    public bool? ProxyEnabled { get; init; }

    [JsonPropertyName("proxyUri")]
    public string? ProxyUri { get; init; }

    [JsonPropertyName("proxyBypassLocal")]
    public bool? ProxyBypassLocal { get; init; }

    // ── Long-doc specifics ──────────────────────────────────────────────────

    [JsonPropertyName("longDocMaxConcurrency")]
    public int? LongDocMaxConcurrency { get; init; }

    [JsonPropertyName("longDocEnableDocumentContextPass")]
    public bool? LongDocEnableDocumentContextPass { get; init; }

    [JsonPropertyName("enableTatrTableStructure")]
    public bool? EnableTatrTableStructure { get; init; }

    [JsonPropertyName("formulaFontPattern")]
    public string? FormulaFontPattern { get; init; }

    [JsonPropertyName("formulaCharPattern")]
    public string? FormulaCharPattern { get; init; }

    [JsonPropertyName("longDocCustomPrompt")]
    public string? LongDocCustomPrompt { get; init; }

    [JsonPropertyName("layoutDetectionMode")]
    public string? LayoutDetectionMode { get; init; }

    [JsonPropertyName("enableInternationalServices")]
    public bool? EnableInternationalServices { get; init; }

    // ── Resource paths ──────────────────────────────────────────────────────

    /// <summary>
    /// Filesystem path to the ONNX layout detection model bundle (DocLayout-YOLO).
    /// Null if not yet downloaded — worker will surface model_missing error.
    /// </summary>
    [JsonPropertyName("docLayoutYoloPath")]
    public string? DocLayoutYoloPath { get; init; }

    /// <summary>
    /// Filesystem path to the ONNX TATR (table structure recognition) model bundle.
    /// </summary>
    [JsonPropertyName("tatrModelPath")]
    public string? TatrModelPath { get; init; }

    /// <summary>
    /// Filesystem path to the CJK font used for PDF rendering of CJK text.
    /// </summary>
    [JsonPropertyName("cjkFontPath")]
    public string? CjkFontPath { get; init; }

    /// <summary>
    /// Cache directory for worker-local artifacts (intermediate page bitmaps,
    /// translation cache, etc.). Worker writes inside this dir only.
    /// </summary>
    [JsonPropertyName("cacheDir")]
    public string? CacheDir { get; init; }
}
