using Easydict.TranslationService.Services;
using Easydict.TranslationService.Services.AgentCli;
using Easydict.WinUI.Services;

namespace Easydict.WinUI.Models;

/// <summary>One provider-specific option and its legacy settings property.</summary>
public sealed record AiProviderFieldDefinition(
    string Id,
    string LegacySettingName,
    string DefaultValue,
    bool IsSecret = false,
    IReadOnlyList<string>? Options = null);

/// <summary>
/// Describes an AI service type independently of any configured account/profile.
/// The composite Windows Local AI engine is intentionally not represented here.
/// </summary>
public sealed record AiProviderDefinition(
    string Id,
    string LegacyServiceId,
    string DisplayName,
    IReadOnlyList<AiProviderFieldDefinition> Fields,
    string? LegacyEnabledSettingName,
    bool SupportsStreaming,
    bool SupportsCorrection,
    bool SupportsModelListing,
    bool HasLegacyConfiguration = true)
{
    public bool RequiresApiKey => Id is not ("custom-openai" or "magpie") && Fields.Any(item => item.IsSecret);
    public bool RequiresModel => Fields.Any(item => item.Id == "model");
    public bool RequiresAgentCliAcknowledgment => Id is "claude-code" or "codex";
    public bool NewProfilesEnabledByDefault => !RequiresAgentCliAcknowledgment;
    public IReadOnlyList<AiProviderFieldDefinition> SecretFields => Fields.Where(item => item.IsSecret).ToArray();
}

/// <summary>
/// Supported standalone AI provider types. Ordinary translation engines and the composite Local
/// AI engine remain outside this catalog.
/// </summary>
public static class AiProviderCatalog
{
    private static readonly IReadOnlyList<AiProviderDefinition> Items =
    [
        Provider("openai", "openai", "OpenAI", true, true, false,
            Secret("apiKey", nameof(SettingsService.OpenAIApiKey)),
            Field("endpoint", nameof(SettingsService.OpenAIEndpoint), OpenAIService.DefaultEndpoint),
            Field("model", nameof(SettingsService.OpenAIModel), OpenAIService.DefaultModel,
                "gpt-5-mini", "gpt-5-nano", "gpt-5", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-4o-mini", "gpt-4o"),
            Field("temperature", nameof(SettingsService.OpenAITemperature), "0.3"),
            Field("apiFormat", nameof(SettingsService.OpenAIApiFormatOverride), "Auto")),
        Provider("ollama", "ollama", "Ollama", true, true, true,
            Field("endpoint", nameof(SettingsService.OllamaEndpoint), "http://localhost:11434/v1/chat/completions"),
            Field("model", nameof(SettingsService.OllamaModel), "llama3.2", "llama3.2"),
            Field("promptStyle", nameof(SettingsService.OllamaPromptStyle), "Standard"),
            Field("customPrompt", nameof(SettingsService.OllamaCustomPrompt), "")),
        Provider("deepseek", "deepseek", "DeepSeek", true, true, true,
            Secret("apiKey", nameof(SettingsService.DeepSeekApiKey)),
            Field("model", nameof(SettingsService.DeepSeekModel), "deepseek-chat", "deepseek-chat", "deepseek-reasoner")),
        Provider("groq", "groq", "Groq", true, true, false,
            Secret("apiKey", nameof(SettingsService.GroqApiKey)),
            Field("model", nameof(SettingsService.GroqModel), "llama-3.3-70b-versatile",
                "llama-3.3-70b-versatile", "llama-3.1-8b-instant", "qwen/qwen-3-32b")),
        Provider("zhipu", "zhipu", "Zhipu AI", true, true, true,
            Secret("apiKey", nameof(SettingsService.ZhipuApiKey)),
            Field("model", nameof(SettingsService.ZhipuModel), "glm-4.5-flash",
                "glm-4.5-flash", "glm-4-flash-250414", "glm-4.7", "glm-4.5-air")),
        Provider("kimi", "kimi", "Kimi", true, true, true,
            Secret("apiKey", nameof(SettingsService.KimiApiKey)),
            Field("model", nameof(SettingsService.KimiModel), "kimi-k2-turbo-preview",
                "kimi-k2-turbo-preview", "kimi-k2-0905-preview", "kimi-latest", "moonshot-v1-8k")),
        Provider("github", "github", "GitHub Models", true, true, false,
            Secret("token", nameof(SettingsService.GitHubModelsToken)),
            Field("model", nameof(SettingsService.GitHubModelsModel), "gpt-4.1",
                "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-4o", "gpt-4o-mini", "deepseek-v3-0324")),
        Provider("custom-openai", "custom-openai", "Custom OpenAI", true, true, false,
            Field("endpoint", nameof(SettingsService.CustomOpenAIEndpoint), ""),
            Secret("apiKey", nameof(SettingsService.CustomOpenAIApiKey)),
            Field("model", nameof(SettingsService.CustomOpenAIModel), "gpt-3.5-turbo")),
        Provider("magpie", "magpie", "Magpie", true, true, true,
            Field("endpoint", "", "http://127.0.0.1:3425/v1/chat/completions"),
            Field("allowRemote", "", "false"),
            Secret("apiKey", ""),
            Field("model", "", "")) with { HasLegacyConfiguration = false },
        Provider("openrouter", "openrouter", "OpenRouter", true, true, true,
            Secret("apiKey", nameof(SettingsService.OpenRouterApiKey)),
            Field("model", nameof(SettingsService.OpenRouterModel), "openrouter/free", "openrouter/free", "openrouter/auto")),
        Provider("orcarouter", "orcarouter", "OrcaRouter", true, true, true,
            Secret("apiKey", nameof(SettingsService.OrcaRouterApiKey)),
            Field("model", nameof(SettingsService.OrcaRouterModel), "orcarouter/free", "orcarouter/free", "orcarouter/auto",
                "deepseek/deepseek-v4-flash-free", "deepseek/deepseek-v4-pro-free"),
            Field("signedInViaSso", nameof(SettingsService.OrcaRouterSignedInViaSso), "false")),
        Provider("gemini", "gemini", "Google Gemini", true, true, false,
            Secret("apiKey", nameof(SettingsService.GeminiApiKey)),
            Field("model", nameof(SettingsService.GeminiModel), "gemini-2.5-flash",
                "gemini-2.5-flash", "gemini-2.5-flash-lite", "gemini-2.5-pro", "gemini-2.0-flash",
                "gemini-1.5-flash", "gemini-1.5-pro", "gemini-3-flash-preview", "gemini-3-pro-preview")),
        Provider("doubao", "doubao", "Doubao", true, false, false,
            Secret("apiKey", nameof(SettingsService.DoubaoApiKey)),
            Field("endpoint", nameof(SettingsService.DoubaoEndpoint), "https://ark.cn-beijing.volces.com/api/v3/responses"),
            Field("model", nameof(SettingsService.DoubaoModel), "doubao-seed-translation-250915")),
        new("claude-code", ClaudeCodeService.ServiceIdValue, "Claude Code",
            [
                Field("model", nameof(SettingsService.ClaudeCodeModel), ClaudeCodeService.DefaultModel,
                    "sonnet", "haiku", "opus"),
                Field("executablePath", nameof(SettingsService.ClaudeCodeExecutablePath), "")
            ], nameof(SettingsService.ClaudeCodeEnabled), true, true, true),
        new("codex", CodexCliService.ServiceIdValue, "Codex CLI",
            [
                Field("model", nameof(SettingsService.CodexModel), CodexCliService.DefaultModel,
                    "gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol"),
                Field("reasoningEffort", nameof(SettingsService.CodexReasoningEffort), CodexCliService.DefaultReasoningEffort,
                    "none", "minimal", "low", "medium", "high", "xhigh")
            ], nameof(SettingsService.CodexEnabled), true, true, true)
    ];

    private static readonly IReadOnlyDictionary<string, AiProviderDefinition> ById =
        Items.ToDictionary(provider => provider.Id, StringComparer.Ordinal);

    public static IReadOnlyList<AiProviderDefinition> All => Items;

    public static bool TryGet(string? id, out AiProviderDefinition definition)
    {
        if (id is not null && ById.TryGetValue(id, out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    private static AiProviderDefinition Provider(
        string id,
        string serviceId,
        string displayName,
        bool supportsStreaming,
        bool supportsCorrection,
        bool supportsModelListing,
        params AiProviderFieldDefinition[] fields)
        => new(id, serviceId, displayName, fields, null, supportsStreaming, supportsCorrection, supportsModelListing);

    private static AiProviderFieldDefinition Field(string id, string legacyName, string defaultValue, params string[] options)
        => new(id, legacyName, defaultValue, Options: options.Length == 0 ? null : options);

    private static AiProviderFieldDefinition Secret(string id, string legacyName)
        => new(id, legacyName, "", IsSecret: true);
}
