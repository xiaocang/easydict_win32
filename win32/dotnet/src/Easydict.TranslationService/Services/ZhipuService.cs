using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Zhipu AI (智谱) translation service using OpenAI-compatible API.
/// </summary>
public sealed class ZhipuService : BaseOpenAIService
{
    private const string DefaultEndpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    private const string DefaultModel = "glm-4-flash-250414";

    /// <summary>
    /// Available Zhipu models.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "glm-4-flash-250414",
        "glm-4.5-flash",
        "glm-4.7",
        "glm-4.5-air"
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public ZhipuService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "zhipu";
    public override string DisplayName => "Zhipu (智谱)";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// Configure the Zhipu service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">Zhipu API key.</param>
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
