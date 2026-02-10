using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Built-in AI service that routes through a Cloudflare Worker proxy.
///
/// Default flow (no user API key):
///   Client → Cloudflare Worker (with X-Device-Id header) → GLM / Groq
///   The Worker holds the actual API keys and routes requests by model name.
///   Rate limiting is enforced per device fingerprint on the Worker side.
///
/// User API key flow:
///   Client → GLM / Groq directly (with user's own API key, bypasses Worker)
///
/// Supports two provider backends:
/// - GLM (Zhipu AI): Default, uses free flash models (glm-4-flash, glm-4-flash-250414)
/// - Groq: Backup, uses free models (llama-3.3-70b-versatile, llama-3.1-8b-instant)
/// </summary>
public sealed class BuiltInAIService : BaseOpenAIService
{
    /// <summary>
    /// Provider backends for the built-in AI service.
    /// </summary>
    internal enum Provider { GLM, Groq }

    private const string DefaultModel = "glm-4-flash-250414";

    /// <summary>
    /// Cloudflare Worker proxy endpoint.
    /// All built-in requests (without user API key) go through this proxy.
    /// The Worker holds actual API keys and routes by model name.
    /// TODO: Replace with actual Worker URL after deployment.
    /// </summary>
    private const string WorkerEndpoint = "https://easydict-ai.example.workers.dev/v1/chat/completions";

    // Direct provider endpoints (used only when user provides their own API key)
    private const string GLMEndpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    private const string GroqEndpoint = "https://api.groq.com/openai/v1/chat/completions";

    /// <summary>
    /// Maps model names to their provider backend.
    /// Used for direct connection routing when user provides their own API key.
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
    private string _deviceId = "";

    public BuiltInAIService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "builtin";
    public override string DisplayName => "Built-in AI";
    public override bool RequiresApiKey => false;
    public override IReadOnlyList<Language> SupportedLanguages => _builtInLanguages;

    /// <summary>
    /// Whether the user has provided their own API key (bypasses Worker proxy).
    /// </summary>
    internal bool UseDirectConnection => !string.IsNullOrEmpty(_userApiKey);

    /// <summary>
    /// Current provider backend, determined by the selected model.
    /// Only relevant for direct connection (user API key) routing.
    /// </summary>
    internal Provider CurrentProvider =>
        ModelProviderMap.GetValueOrDefault(_model, Provider.GLM);

    /// <summary>
    /// Endpoint: Worker proxy for built-in mode, direct provider for user API key mode.
    /// </summary>
    public override string Endpoint => UseDirectConnection
        ? CurrentProvider switch
        {
            Provider.GLM => GLMEndpoint,
            Provider.Groq => GroqEndpoint,
            _ => GLMEndpoint
        }
        : WorkerEndpoint;

    /// <summary>
    /// API key: user's key for direct mode, empty for Worker proxy mode
    /// (Worker handles authentication server-side).
    /// </summary>
    public override string ApiKey => UseDirectConnection ? _userApiKey : "";

    public override string Model => _model;

    /// <summary>
    /// Built-in mode is always configured (Worker endpoint is hardcoded).
    /// Direct mode requires a non-empty user API key.
    /// </summary>
    public override bool IsConfigured => UseDirectConnection
        ? !string.IsNullOrEmpty(_userApiKey)
        : true;

    /// <summary>
    /// Configure the model selection, optional user API key, and device fingerprint.
    /// </summary>
    /// <param name="model">Model to use.</param>
    /// <param name="apiKey">Optional user-provided API key (bypasses Worker proxy).</param>
    /// <param name="deviceId">Device fingerprint for Worker rate limiting.</param>
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
    /// Add device fingerprint header for Worker proxy requests.
    /// The Worker uses this for per-device rate limiting.
    /// </summary>
    protected override void ConfigureHttpRequest(HttpRequestMessage request)
    {
        if (!UseDirectConnection && !string.IsNullOrEmpty(_deviceId))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
        }
    }

    /// <summary>
    /// Validate configuration based on connection mode.
    /// </summary>
    protected override void ValidateConfiguration()
    {
        if (UseDirectConnection)
        {
            // Direct mode: need endpoint and API key
            if (string.IsNullOrEmpty(ApiKey))
            {
                throw new TranslationException(
                    "API key is required for direct connection mode. " +
                    "Please provide your API key in Settings → Built-in AI.")
                {
                    ErrorCode = TranslationErrorCode.InvalidApiKey,
                    ServiceId = ServiceId
                };
            }
        }
        // Worker proxy mode: always available (endpoint is hardcoded)
    }
}
