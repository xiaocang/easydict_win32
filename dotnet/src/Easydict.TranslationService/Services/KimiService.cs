using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services.ModelCatalog;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Kimi (Moonshot AI, 月之暗面) translation service using OpenAI-compatible API.
/// </summary>
public sealed class KimiService : BaseOpenAIService, IModelCatalogProvider
{
    private const string DefaultEndpoint = "https://api.moonshot.cn/v1/chat/completions";
    private const string DefaultModel = "kimi-k2-turbo-preview";

    /// <summary>
    /// Available Kimi models.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "kimi-k2-turbo-preview",
        "kimi-k2-0905-preview",
        "kimi-latest",
        "moonshot-v1-8k"
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public KimiService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "kimi";
    public override string DisplayName => "Kimi (Moonshot AI)";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    public string ModelsEndpoint => "https://api.moonshot.cn/v1/models";

    public Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(
        CancellationToken cancellationToken = default)
    {
        return OpenAiCompatibleModelCatalog.FetchAsync(
            HttpClient, ModelsEndpoint, _apiKey, cancellationToken);
    }

    /// <summary>
    /// Configure the Kimi service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">Kimi (Moonshot AI) API key.</param>
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
}
