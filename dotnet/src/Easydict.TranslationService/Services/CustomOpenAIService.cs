using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Custom OpenAI-compatible translation service.
/// Allows users to connect to self-hosted or third-party OpenAI-compatible endpoints.
/// </summary>
public sealed class CustomOpenAIService : BaseOpenAIService
{
    private const string DefaultModel = "gpt-3.5-turbo";

    private string _endpoint = "";
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;
    private string _displayName = "Custom OpenAI";

    public CustomOpenAIService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "custom-openai";
    public override string DisplayName => _displayName;

    /// <summary>
    /// Custom OpenAI service doesn't require API key (some local endpoints don't need it).
    /// </summary>
    public override bool RequiresApiKey => false;

    /// <summary>
    /// Configured when endpoint is set.
    /// </summary>
    public override bool IsConfigured => !string.IsNullOrEmpty(_endpoint);

    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// Configure the Custom OpenAI service with endpoint and options.
    /// </summary>
    /// <param name="endpoint">Required endpoint URL (e.g., http://localhost:11434/v1/chat/completions).</param>
    /// <param name="apiKey">Optional API key (some endpoints don't require it).</param>
    /// <param name="model">Optional model name.</param>
    /// <param name="temperature">Optional temperature (0.0-2.0).</param>
    /// <param name="displayName">Optional custom display name.</param>
    public void Configure(string endpoint, string? apiKey = null, string? model = null, double? temperature = null, string? displayName = null)
    {
        _endpoint = endpoint ?? "";
        _apiKey = apiKey ?? "";
        if (!string.IsNullOrEmpty(model)) _model = model;
        if (temperature.HasValue) _temperature = Math.Clamp(temperature.Value, 0.0, 2.0);
        if (!string.IsNullOrEmpty(displayName)) _displayName = displayName;
    }
}
