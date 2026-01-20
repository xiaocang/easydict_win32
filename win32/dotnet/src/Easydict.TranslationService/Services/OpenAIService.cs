using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// OpenAI translation service using the Chat Completions API.
/// Requires API key from user settings.
/// </summary>
public sealed class OpenAIService : BaseOpenAIService
{
    private const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";
    private const string DefaultModel = "gpt-4o-mini";

    /// <summary>
    /// Available OpenAI models for translation.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "gpt-4o-mini",
        "gpt-4o",
        "gpt-4-turbo",
        "gpt-3.5-turbo"
    };

    private static readonly IReadOnlyList<Language> OpenAILanguages = new[]
    {
        Language.SimplifiedChinese,
        Language.TraditionalChinese,
        Language.English,
        Language.Japanese,
        Language.Korean,
        Language.French,
        Language.Spanish,
        Language.Portuguese,
        Language.Italian,
        Language.German,
        Language.Russian,
        Language.Arabic,
        Language.Dutch,
        Language.Polish,
        Language.Vietnamese,
        Language.Thai,
        Language.Indonesian,
        Language.Turkish,
        Language.Swedish,
        Language.Danish,
        Language.Norwegian,
        Language.Finnish,
        Language.Greek,
        Language.Czech,
        Language.Romanian,
        Language.Hungarian,
        Language.Ukrainian,
        Language.Hebrew,
        Language.Hindi,
        Language.Bengali,
        Language.Tamil,
        Language.Persian
    };

    private string _endpoint = DefaultEndpoint;
    private string _apiKey = "";
    private string _model = DefaultModel;
    private double _temperature = 0.3;

    public OpenAIService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => "openai";
    public override string DisplayName => "OpenAI";
    public override bool RequiresApiKey => true;
    public override bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _endpoint;
    public override string ApiKey => _apiKey;
    public override string Model => _model;
    public override double Temperature => _temperature;

    /// <summary>
    /// Configure the OpenAI service.
    /// </summary>
    /// <param name="apiKey">OpenAI API key (required).</param>
    /// <param name="endpoint">Custom endpoint URL (optional, defaults to OpenAI API).</param>
    /// <param name="model">Model to use (optional, defaults to gpt-4o-mini).</param>
    /// <param name="temperature">Generation temperature (optional, defaults to 0.3).</param>
    public void Configure(string apiKey, string? endpoint = null, string? model = null, double? temperature = null)
    {
        _apiKey = apiKey ?? "";
        if (!string.IsNullOrEmpty(endpoint))
            _endpoint = endpoint;
        if (!string.IsNullOrEmpty(model))
            _model = model;
        if (temperature.HasValue)
            _temperature = Math.Clamp(temperature.Value, 0.0, 2.0);
    }
}
