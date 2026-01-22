using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Doubao (豆包) translation service using OpenAI-compatible API.
/// ByteDance's LLM service with streaming support.
/// </summary>
public sealed class DoubaoService : BaseOpenAIService
{
    private const string DefaultEndpoint = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
    private const string DefaultModel = "doubao-seed-1-8-251215";

    /// <summary>
    /// Available Doubao models.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "doubao-seed-1-8-251215",
        "doubao-seed-1-6-250615",
        "doubao-seed-code-preview"
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public DoubaoService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "doubao";
    public override string DisplayName => "Doubao";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// Configure the Doubao service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">Doubao API key.</param>
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
