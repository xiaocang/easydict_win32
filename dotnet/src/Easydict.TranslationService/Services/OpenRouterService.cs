using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services.ModelCatalog;

namespace Easydict.TranslationService.Services;

/// <summary>
/// OpenRouter translation service using OpenAI-compatible API. OpenRouter is a unified
/// gateway to 400+ models from many providers behind a single API key, including several
/// free models (id suffix <c>:free</c>) and a difficulty-routed free router
/// (<c>openrouter/free</c>).
/// </summary>
public sealed class OpenRouterService : BaseOpenAIService, IModelCatalogProvider
{
    private const string DefaultEndpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultModel = "openrouter/free";

    /// <summary>
    /// URL for creating an OpenRouter API key.
    /// </summary>
    public const string SignUpUrl = "https://openrouter.ai/keys";

    /// <summary>
    /// Seed model list used before the live catalog has been fetched.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "openrouter/free",
        "openrouter/auto",
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public OpenRouterService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "openrouter";
    public override string DisplayName => "OpenRouter";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// OpenRouter's <c>/models</c> catalog endpoint. Public, unauthenticated.
    /// </summary>
    public string ModelsEndpoint => "https://openrouter.ai/api/v1/models";

    /// <summary>
    /// Configure the OpenRouter service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">OpenRouter API key.</param>
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
    /// Fetch OpenRouter's live model catalog, free models sorted first.
    /// </summary>
    public Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(CancellationToken cancellationToken = default)
    {
        // The models list is public; no API key is required or sent.
        return OpenAiCompatibleModelCatalog.FetchAsync(HttpClient, ModelsEndpoint, apiKey: null, cancellationToken);
    }

    /// <summary>
    /// Attaches OpenRouter's optional app-attribution headers so Easydict shows up on
    /// OpenRouter's rankings. Purely informational; translation works identically without them.
    /// </summary>
    protected override void ConfigureHttpRequest(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/xiaocang/easydict_win32");
        request.Headers.TryAddWithoutValidation("X-Title", "Easydict for Windows");
    }
}
