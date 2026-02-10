using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Groq translation service using OpenAI-compatible API.
/// Provides access to fast inference with various open-source models.
/// </summary>
public sealed class GroqService : BaseOpenAIService
{
    private const string DefaultEndpoint = "https://api.groq.com/openai/v1/chat/completions";
    private const string DefaultModel = "llama-3.3-70b-versatile";

    /// <summary>
    /// Available Groq models.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "llama-3.3-70b-versatile",
        "llama-3.1-8b-instant",
        "qwen/qwen-3-32b"
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public GroqService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "groq";
    public override string DisplayName => "Groq";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// Configure the Groq service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">Groq API key.</param>
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
