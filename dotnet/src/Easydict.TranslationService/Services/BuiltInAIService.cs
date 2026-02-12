using System.Diagnostics;
using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Security;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Built-in AI translation service — all requests route through the proxy:
///
/// 1. Default — Proxy endpoint with embedded API key:
///    Client → proxy → GLM / Groq
///
/// 2. User API key — direct connection (bypasses proxy):
///    Client → GLM / Groq with user's own key
/// </summary>
public sealed class BuiltInAIService : BaseOpenAIService
{
    /// <summary>
    /// Provider backends for the built-in AI service.
    /// </summary>
    internal enum Provider { GLM, Groq }

    private const string DefaultModel = "glm-4-flash-250414";

    // Direct provider endpoints (used only with user's own API key)
    private const string GLMEndpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    private const string GroqEndpoint = "https://api.groq.com/openai/v1/chat/completions";

    /// <summary>
    /// Maps model names to their provider backend.
    /// </summary>
    internal static readonly Dictionary<string, Provider> ModelProviderMap = new()
    {
        // GLM models (Zhipu AI)
        ["glm-4-flash"] = Provider.GLM,
        ["glm-4-flash-250414"] = Provider.GLM,

        // Groq models
        ["llama-3.3-70b-versatile"] = Provider.Groq,
        ["llama-3.1-8b-instant"] = Provider.Groq,
    };

    /// <summary>
    /// Models allowed through the free proxy (Cloudflare worker).
    /// Groq is not available in proxy mode. Direct connection (user API key) has no restriction.
    /// </summary>
    internal static readonly HashSet<string> AllowedProxyModels = new(StringComparer.Ordinal)
    {
        "glm-4-flash",
        "glm-4-flash-250414",
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
    private string _deviceToken = "";

    public BuiltInAIService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "builtin";
    public override string DisplayName => "Built-in AI";
    public override bool RequiresApiKey => false;
    public override IReadOnlyList<Language> SupportedLanguages => _builtInLanguages;

    /// <summary>
    /// Whether the user has provided their own API key (bypasses proxy).
    /// </summary>
    internal bool UseDirectConnection => !string.IsNullOrEmpty(_userApiKey);

    /// <summary>
    /// Current provider backend, determined by the selected model.
    /// </summary>
    internal Provider CurrentProvider =>
        ModelProviderMap.GetValueOrDefault(_model, Provider.GLM);

    /// <summary>
    /// Endpoint routing:
    /// - User API key → direct to provider (GLM or Groq endpoint)
    /// - Built-in mode → proxy endpoint from embedded config
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

            return GetEmbeddedEndpoint();
        }
    }

    /// <summary>
    /// API key routing:
    /// - User API key → user's key
    /// - Built-in mode → embedded key from config
    /// </summary>
    public override string ApiKey
    {
        get
        {
            if (UseDirectConnection) return _userApiKey;
            return GetEmbeddedApiKey();
        }
    }

    public override string Model => _model;

    public override bool IsConfigured => UseDirectConnection
        ? !string.IsNullOrEmpty(_userApiKey)
        : !string.IsNullOrEmpty(GetEmbeddedApiKey()) && !string.IsNullOrEmpty(GetEmbeddedEndpoint());

    /// <summary>
    /// Configure the model selection, optional user API key, device fingerprint, and device token.
    /// </summary>
    public void Configure(string model, string? apiKey = null, string? deviceId = null, string? deviceToken = null)
    {
        if (AvailableModels.Contains(model) || ModelProviderMap.ContainsKey(model))
        {
            _model = model;
        }

        _userApiKey = apiKey ?? "";
        _deviceId = deviceId ?? "";
        _deviceToken = deviceToken ?? "";
    }

    /// <summary>
    /// Attach X-Device-Id and X-Device-Token headers for proxy requests.
    /// </summary>
    protected override void ConfigureHttpRequest(HttpRequestMessage request)
    {
        if (!UseDirectConnection && !string.IsNullOrEmpty(_deviceId))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

            if (!string.IsNullOrEmpty(_deviceToken))
            {
                request.Headers.TryAddWithoutValidation("X-Device-Token", _deviceToken);
            }
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

        // Validate model is in the proxy allowlist (Cloudflare worker restriction).
        // Direct connection (user API key) has no model restriction.
        if (!UseDirectConnection && !AllowedProxyModels.Contains(_model))
        {
            throw new TranslationException(
                $"Model '{_model}' is not available through the free proxy. " +
                $"Allowed models: {string.Join(", ", AllowedProxyModels)}. " +
                "To use other models, provide your own API key in Settings → Built-in AI.")
            {
                ErrorCode = TranslationErrorCode.InvalidModel,
                ServiceId = ServiceId
            };
        }

        if (!UseDirectConnection && (string.IsNullOrEmpty(GetEmbeddedApiKey()) || string.IsNullOrEmpty(GetEmbeddedEndpoint())))
        {
            throw new TranslationException(
                "Built-in AI is not available. " +
                "Please provide your own API key in Settings → Built-in AI.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }
    }

    /// <summary>
    /// Register the device with the proxy server to obtain an HMAC device token.
    /// Derives the registration URL from the embedded proxy endpoint.
    /// Returns the device token on success, or null on failure.
    /// </summary>
    public async Task<string?> RegisterDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_deviceId))
        {
            Debug.WriteLine("[BuiltInAI] RegisterDeviceAsync: no device ID configured");
            return null;
        }

        var proxyEndpoint = GetEmbeddedEndpoint();
        if (string.IsNullOrEmpty(proxyEndpoint))
        {
            Debug.WriteLine("[BuiltInAI] RegisterDeviceAsync: no proxy endpoint configured");
            return null;
        }

        // Derive registration URL: replace path with /v1/device/register
        string registerUrl;
        try
        {
            var uri = new Uri(proxyEndpoint);
            registerUrl = $"{uri.Scheme}://{uri.Authority}/v1/device/register";
        }
        catch (UriFormatException ex)
        {
            Debug.WriteLine($"[BuiltInAI] RegisterDeviceAsync: invalid proxy endpoint: {ex.Message}");
            return null;
        }

        var apiKey = GetEmbeddedApiKey();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, registerUrl);
            request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }

            var response = await HttpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[BuiltInAI] RegisterDeviceAsync: HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("device_token", out var tokenElement))
            {
                var token = tokenElement.GetString();
                Debug.WriteLine($"[BuiltInAI] RegisterDeviceAsync: success, token length={token?.Length ?? 0}");
                return token;
            }

            Debug.WriteLine("[BuiltInAI] RegisterDeviceAsync: response missing device_token field");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Debug.WriteLine($"[BuiltInAI] RegisterDeviceAsync: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string GetEmbeddedApiKey() =>
        SecretKeyManager.GetSecret("builtInAIAPIKey") ?? "";

    private static string GetEmbeddedEndpoint() =>
        SecretKeyManager.GetSecret("builtInAIEndpoint") ?? "";
}
