using Easydict.TranslationService.Models;
using Easydict.TranslationService.Security;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Built-in AI translation service with two-tier routing:
///
/// 1. Primary — Zhipu GLM free API (direct, embedded key):
///    Client → open.bigmodel.cn with built-in free API key
///
/// 2. Backup — Cloudflare Worker proxy (DeviceId rate-limited):
///    Client → Worker (X-Device-Id header) → GLM / Groq
///    Used for Groq models and as GLM fallback when free quota is exhausted.
///
/// 3. User API key — direct connection (bypasses both):
///    Client → GLM / Groq with user's own key
/// </summary>
public sealed class BuiltInAIService : BaseOpenAIService
{
    /// <summary>
    /// Provider backends for the built-in AI service.
    /// </summary>
    internal enum Provider { GLM, Groq }

    private const string DefaultModel = "glm-4-flash-250414";

    // Primary: Zhipu GLM direct endpoint (free API key embedded)
    private const string GLMEndpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";

    // Backup: Cloudflare Worker proxy (holds Groq key, also proxies GLM)
    // TODO: Replace with actual Worker URL after deployment.
    internal const string WorkerEndpoint = "https://easydict-ai.example.workers.dev/v1/chat/completions";

    // Direct Groq endpoint (used only with user's own Groq API key)
    private const string GroqEndpoint = "https://api.groq.com/openai/v1/chat/completions";

    /// <summary>
    /// Maps model names to their provider backend.
    /// </summary>
    internal static readonly Dictionary<string, Provider> ModelProviderMap = new()
    {
        // GLM models — primary, direct with embedded free key
        ["glm-4-flash"] = Provider.GLM,
        ["glm-4-flash-250414"] = Provider.GLM,

        // Groq models — via Worker proxy (no embedded Groq key)
        ["llama-3.3-70b-versatile"] = Provider.Groq,
        ["llama-3.1-8b-instant"] = Provider.Groq,
    };

    /// <summary>
    /// Available models for the built-in AI service.
    /// GLM models listed first (default), Groq models as backup.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "glm-4-flash-250414",
        "glm-4-flash",
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
    private string _deviceId = "";

    public BuiltInAIService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "builtin";
    public override string DisplayName => "Built-in AI";
    public override bool RequiresApiKey => false;
    public override IReadOnlyList<Language> SupportedLanguages => _builtInLanguages;

    /// <summary>
    /// Whether the user has provided their own API key (bypasses built-in routing).
    /// </summary>
    internal bool UseDirectConnection => !string.IsNullOrEmpty(_userApiKey);

    /// <summary>
    /// Current provider backend, determined by the selected model.
    /// </summary>
    internal Provider CurrentProvider =>
        ModelProviderMap.GetValueOrDefault(_model, Provider.GLM);

    /// <summary>
    /// Whether this request goes through the Cloudflare Worker proxy.
    /// True for Groq models (no embedded key) when user hasn't provided their own key.
    /// </summary>
    internal bool UsesWorkerProxy => !UseDirectConnection && CurrentProvider == Provider.Groq;

    /// <summary>
    /// Endpoint routing:
    /// - User API key → direct to provider
    /// - GLM model (no user key) → direct to Zhipu with embedded key
    /// - Groq model (no user key) → Cloudflare Worker proxy
    /// </summary>
    public override string Endpoint
    {
        get
        {
            if (UseDirectConnection)
            {
                return CurrentProvider switch
                {
                    Provider.GLM => GLMEndpoint,
                    Provider.Groq => GroqEndpoint,
                    _ => GLMEndpoint
                };
            }

            return CurrentProvider switch
            {
                Provider.GLM => GLMEndpoint,   // Direct with embedded key
                Provider.Groq => WorkerEndpoint, // Via Worker proxy
                _ => GLMEndpoint
            };
        }
    }

    /// <summary>
    /// API key routing:
    /// - User API key → user's key
    /// - GLM (built-in) → embedded free key
    /// - Groq (Worker) → empty (Worker handles auth)
    /// </summary>
    public override string ApiKey
    {
        get
        {
            if (UseDirectConnection) return _userApiKey;

            return CurrentProvider switch
            {
                Provider.GLM => GetEmbeddedGLMKey(),
                Provider.Groq => "",  // Worker handles auth server-side
                _ => ""
            };
        }
    }

    public override string Model => _model;

    public override bool IsConfigured => UseDirectConnection
        ? !string.IsNullOrEmpty(_userApiKey)
        : CurrentProvider == Provider.GLM
            ? !string.IsNullOrEmpty(GetEmbeddedGLMKey())
            : true;  // Worker mode always configured

    /// <summary>
    /// Configure the model selection, optional user API key, and device fingerprint.
    /// </summary>
    public void Configure(string model, string? apiKey = null, string? deviceId = null)
    {
        if (AvailableModels.Contains(model) || ModelProviderMap.ContainsKey(model))
        {
            _model = model;
        }

        _userApiKey = apiKey ?? "";
        _deviceId = deviceId ?? "";
    }

    /// <summary>
    /// Attach X-Device-Id header for Worker proxy requests.
    /// </summary>
    protected override void ConfigureHttpRequest(HttpRequestMessage request)
    {
        if (UsesWorkerProxy && !string.IsNullOrEmpty(_deviceId))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
        }
    }

    /// <summary>
    /// Validate configuration based on connection mode.
    /// </summary>
    protected override void ValidateConfiguration()
    {
        if (UseDirectConnection && string.IsNullOrEmpty(_userApiKey))
        {
            throw new TranslationException(
                "API key is required for direct connection mode. " +
                "Please provide your API key in Settings → Built-in AI.")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }

        if (!UseDirectConnection && CurrentProvider == Provider.GLM && string.IsNullOrEmpty(GetEmbeddedGLMKey()))
        {
            throw new TranslationException(
                "Built-in GLM API key is not available. " +
                "Please provide your own API key in Settings → Built-in AI, " +
                "or select a Groq model to use the proxy.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }
    }

    private static string GetEmbeddedGLMKey() =>
        SecretKeyManager.GetSecret("builtInGLMAPIKey") ?? "";
}
