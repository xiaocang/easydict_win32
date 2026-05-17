using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// OpenAI translation service.
/// Defaults to the Responses API endpoint; auto-detects format if the user
/// configures a custom URL (e.g. Chat Completions for legacy or proxy setups).
/// Requires API key from user settings.
/// </summary>
public sealed class OpenAIService : BaseOpenAIService
{
    public const string DefaultEndpoint = "https://api.openai.com/v1/responses";
    public const string LegacyChatCompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    public const string DefaultModel = "gpt-5-mini";

    /// <summary>
    /// Suggested OpenAI models for translation, biased toward the cheap "mini"
    /// tier of recent generations.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "gpt-5-mini",
        "gpt-5-nano",
        "gpt-5",
        "gpt-4.1-mini",
        "gpt-4.1-nano",
        "gpt-4o-mini",
        "gpt-4o",
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
    /// <param name="endpoint">Custom endpoint URL (optional, defaults to OpenAI Responses API).</param>
    /// <param name="model">Model to use (optional, defaults to <see cref="DefaultModel"/>).</param>
    /// <param name="temperature">Generation temperature (optional, defaults to 0.3).</param>
    /// <param name="formatOverride">
    /// Pin the API format. <see cref="OpenAIApiFormat.Auto"/> (default) infers the
    /// format from the endpoint URL suffix (<c>/responses</c> → Responses, anything
    /// else → Chat Completions). Any other value bypasses URL inspection — the
    /// caller is responsible for ensuring the endpoint accepts the pinned format.
    /// </param>
    public void Configure(
        string apiKey,
        string? endpoint = null,
        string? model = null,
        double? temperature = null,
        OpenAIApiFormat formatOverride = OpenAIApiFormat.Auto)
    {
        _apiKey = apiKey ?? "";
        if (!string.IsNullOrEmpty(endpoint))
            _endpoint = endpoint;
        if (!string.IsNullOrEmpty(model))
            _model = model;
        if (temperature.HasValue)
            _temperature = Math.Clamp(temperature.Value, 0.0, 2.0);

        ResetFormatDetection();
        if (formatOverride != OpenAIApiFormat.Auto)
        {
            PinFormat(formatOverride);
        }
    }
}
