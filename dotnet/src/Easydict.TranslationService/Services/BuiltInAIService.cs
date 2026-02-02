using Easydict.TranslationService.Models;
using Easydict.TranslationService.Security;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Built-in AI service with pre-configured endpoint and API key.
/// User only selects the model - endpoint and API key are hidden.
/// This provides a free/low-cost translation option without requiring user configuration.
/// Uses Groq cloud service with embedded API key.
/// </summary>
public sealed class BuiltInAIService : BaseOpenAIService
{
    private const string DefaultModel = "llama-3.3-70b-versatile";

    /// <summary>
    /// Available models for the built-in AI service.
    /// These are free/low-cost models that don't require user API keys.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "llama-3.3-70b-versatile",
        "llama-3.1-8b-instant",
        "gemma2-9b-it",
        "mixtral-8x7b-32768"
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

    public BuiltInAIService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "builtin";
    public override string DisplayName => "Built-in AI";
    public override bool RequiresApiKey => false; // API key is built-in
    public override bool IsConfigured => !string.IsNullOrEmpty(GetApiKey());
    public override IReadOnlyList<Language> SupportedLanguages => _builtInLanguages;

    public override string Endpoint => SecretKeyManager.GetSecret("builtInAIEndpoint") ?? "";
    public override string ApiKey => GetApiKey();
    public override string Model => _model;

    /// <summary>
    /// Get API key from environment variable or fall back to encrypted embedded key.
    /// </summary>
    private static string GetApiKey()
    {
        // Allow override via environment variable for development/deployment
        var envKey = Environment.GetEnvironmentVariable("EASYDICT_GROQ_API_KEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            return envKey;
        }

        // Fall back to encrypted embedded key
        return SecretKeyManager.GetSecret("builtInAIAPIKey") ?? "";
    }

    /// <summary>
    /// Configure the model selection (only configurable option).
    /// </summary>
    /// <param name="model">Model to use.</param>
    public void Configure(string model)
    {
        if (AvailableModels.Contains(model))
        {
            _model = model;
        }
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

        // Check if API key is available (embedded or from environment)
        if (string.IsNullOrEmpty(GetApiKey()))
        {
            throw new TranslationException("Built-in AI service is not available. Set EASYDICT_GROQ_API_KEY environment variable.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId
            };
        }
    }
}
