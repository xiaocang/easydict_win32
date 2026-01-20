using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// GitHub Models translation service using OpenAI-compatible API.
/// Provides access to various models hosted on GitHub's AI infrastructure.
/// </summary>
public sealed class GitHubModelsService : BaseOpenAIService
{
    private const string DefaultEndpoint = "https://models.github.ai/inference/chat/completions";
    private const string DefaultModel = "gpt-4.1";

    /// <summary>
    /// Available GitHub Models.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "gpt-4.1",
        "gpt-4.1-mini",
        "gpt-4.1-nano",
        "gpt-4o",
        "gpt-4o-mini",
        "deepseek-v3-0324"
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public GitHubModelsService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "github";
    public override string DisplayName => "GitHub Models";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// Configure the GitHub Models service with API credentials and options.
    /// </summary>
    /// <param name="apiKey">GitHub personal access token.</param>
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
