using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services.ModelCatalog;

namespace Easydict.TranslationService.Services;

/// <summary>
/// OrcaRouter translation service using OpenAI-compatible API. OrcaRouter is an adaptive
/// LLM router exposing 150+ upstream models behind a single API key, including free models
/// (id suffix <c>-free</c>) and a difficulty-routed free router (<c>orcarouter/free</c>).
/// </summary>
public sealed class OrcaRouterService : BaseOpenAIService, IModelCatalogProvider
{
    private const string DefaultEndpoint = "https://api.orcarouter.ai/v1/chat/completions";
    private const string DefaultModel = "orcarouter/free";

    /// <summary>
    /// Referral link used to sign up for OrcaRouter. Shown as an ordinary "get an API key"
    /// link in Settings, matching every other provider's sign-up link.
    /// </summary>
    public const string ReferralUrl = "https://www.orcarouter.ai/ref/ref_a42265f998f62828c4d6";

    /// <summary>
    /// Seed model list used before the live catalog has been fetched.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "orcarouter/free",
        "orcarouter/auto",
        "deepseek/deepseek-v4-flash-free",
        "deepseek/deepseek-v4-pro-free",
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public OrcaRouterService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "orcarouter";
    public override string DisplayName => "OrcaRouter";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// OrcaRouter's <c>/models</c> catalog endpoint. Requires the same Bearer key as translation.
    /// </summary>
    public string ModelsEndpoint => "https://api.orcarouter.ai/v1/models";

    /// <summary>
    /// Configure the OrcaRouter service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">OrcaRouter API key.</param>
    /// <param name="endpoint">Optional custom endpoint URL.</param>
    /// <param name="model">Optional model name.</param>
    /// <param name="temperature">Optional temperature (0.0-2.0).</param>
    public void Configure(string apiKey, string? endpoint = null, string? model = null, double? temperature = null)
    {
        _apiKey = apiKey ?? "";
        if (!string.IsNullOrEmpty(endpoint)) _endpoint = endpoint;
        if (!string.IsNullOrEmpty(model)) _model = model;
        if (temperature.HasValue) _temperature = Math.Clamp(temperature.Value, 0.0, 2.0);
    }

    /// <summary>
    /// Fetch OrcaRouter's live model catalog, free models sorted first.
    /// </summary>
    public Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(CancellationToken cancellationToken = default)
    {
        return OpenAiCompatibleModelCatalog.FetchAsync(HttpClient, ModelsEndpoint, _apiKey, cancellationToken);
    }
}
