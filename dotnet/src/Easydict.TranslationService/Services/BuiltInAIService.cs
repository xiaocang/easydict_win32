using Easydict.TranslationService.Models;
using Easydict.TranslationService.Security;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Built-in AI service with pre-configured endpoint and API key.
/// User only selects the model - endpoint and API key are hidden (but user can override API key).
/// This provides a free/low-cost translation option without requiring user configuration.
///
/// Supports two providers:
/// - GLM (Zhipu AI): Default provider, uses free flash models (glm-4-flash, glm-4-flash-250414)
/// - Groq: Backup provider, uses free models (llama-3.3-70b-versatile, llama-3.1-8b-instant)
///
/// The provider is automatically selected based on the model chosen.
/// Users can optionally provide their own API key in settings as a fallback
/// when the built-in keys are exhausted.
/// </summary>
public sealed class BuiltInAIService : BaseOpenAIService
{
    /// <summary>
    /// Provider backends for the built-in AI service.
    /// </summary>
    internal enum Provider { GLM, Groq }

    private const string DefaultModel = "glm-4-flash-250414";

    private const string GLMEndpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    private const string GroqEndpoint = "https://api.groq.com/openai/v1/chat/completions";

    /// <summary>
    /// Maps model names to their provider backend.
    /// </summary>
    internal static readonly Dictionary<string, Provider> ModelProviderMap = new()
    {
        // GLM models (primary, free)
        ["glm-4-flash"] = Provider.GLM,
        ["glm-4-flash-250414"] = Provider.GLM,

        // Groq models (backup)
        ["llama-3.3-70b-versatile"] = Provider.Groq,
        ["llama-3.1-8b-instant"] = Provider.Groq,
    };

    /// <summary>
    /// Available models for the built-in AI service.
    /// GLM models listed first (default), Groq models as backup.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        // GLM models (primary, free via Zhipu AI)
        "glm-4-flash-250414",
        "glm-4-flash",

        // Groq models (backup)
        "llama-3.3-70b-versatile",
        "llama-3.1-8b-instant",
    };

    private static readonly IReadOnlyList<Language> _builtInLanguages = new[]
    {
        Language.SimplifiedChinese,
        Language.TraditionalChinese,
        Language.English,
        Language.Japanese,
        Language.Korean,
        Language.French,
        Language.Spanish,
        Language.German,
        Language.Russian,
        Language.Italian,
        Language.Portuguese,
        Language.Dutch,
        Language.Polish,
        Language.Vietnamese,
        Language.Arabic,
        Language.Turkish
    };

    private string _model = DefaultModel;
    private string _userApiKey = "";

    public BuiltInAIService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "builtin";
    public override string DisplayName => "Built-in AI";
    public override bool RequiresApiKey => false; // API key is built-in
    public override bool IsConfigured => !string.IsNullOrEmpty(GetApiKey());
    public override IReadOnlyList<Language> SupportedLanguages => _builtInLanguages;

    /// <summary>
    /// Current provider backend, determined by the selected model.
    /// </summary>
    internal Provider CurrentProvider =>
        ModelProviderMap.GetValueOrDefault(_model, Provider.GLM);

    public override string Endpoint => CurrentProvider switch
    {
        Provider.GLM => GLMEndpoint,
        Provider.Groq => GroqEndpoint,
        _ => GLMEndpoint
    };

    public override string ApiKey => GetApiKey();
    public override string Model => _model;

    /// <summary>
    /// Get API key with fallback chain:
    /// 1. User-provided API key (from settings)
    /// 2. Environment variable
    /// 3. Built-in encrypted key for the current provider
    /// </summary>
    private string GetApiKey()
    {
        // 1. User-provided API key takes priority
        if (!string.IsNullOrEmpty(_userApiKey))
        {
            return _userApiKey;
        }

        // 2. Allow override via environment variable
        var envKey = Environment.GetEnvironmentVariable("EASYDICT_BUILTIN_AI_KEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            return envKey;
        }

        // 3. Built-in encrypted key for the current provider
        return CurrentProvider switch
        {
            Provider.GLM => SecretKeyManager.GetSecret("builtInGLMAPIKey") ?? "",
            Provider.Groq => SecretKeyManager.GetSecret("builtInGroqAPIKey") ?? "",
            _ => ""
        };
    }

    /// <summary>
    /// Configure the model selection and optional user API key.
    /// </summary>
    /// <param name="model">Model to use.</param>
    /// <param name="apiKey">Optional user-provided API key (overrides built-in key).</param>
    public void Configure(string model, string? apiKey = null)
    {
        if (AvailableModels.Contains(model) || ModelProviderMap.ContainsKey(model))
        {
            _model = model;
        }

        _userApiKey = apiKey ?? "";
    }

    /// <summary>
    /// Override to not require API key validation (it's built-in).
    /// </summary>
    protected override void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(Endpoint))
        {
            throw new TranslationException("Built-in AI endpoint is not configured")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }

        // Check if API key is available (user-provided, env var, or embedded)
        if (string.IsNullOrEmpty(GetApiKey()))
        {
            throw new TranslationException(
                "Built-in AI service is not available. Please provide your own API key in Settings → Built-in AI.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }
    }
}
