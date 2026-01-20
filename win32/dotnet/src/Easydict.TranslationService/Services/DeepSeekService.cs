using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// DeepSeek translation service using OpenAI-compatible API.
/// </summary>
public sealed class DeepSeekService : BaseOpenAIService
{
    private const string DefaultEndpoint = "https://api.deepseek.com/v1/chat/completions";
    private const string DefaultModel = "deepseek-chat";

    /// <summary>
    /// Available DeepSeek models.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "deepseek-chat",
        "deepseek-reasoner"
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public DeepSeekService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "deepseek";
    public override string DisplayName => "DeepSeek";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// Configure the DeepSeek service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">DeepSeek API key.</param>
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
