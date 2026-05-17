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
    public const string DefaultModel = "gpt-5.4-mini";

    /// <summary>
    /// Suggested OpenAI models for translation, biased toward the cheap "mini"
    /// tier of recent generations.
    /// </summary>
    public static readonly string[] AvailableModels = new[]
    {
        "gpt-5.4-mini",
        "gpt-5.4-nano",
        "gpt-5.4",
        "gpt-5.1",
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
    protected override string? ResponsesReasoningEffort => GetResponsesReasoningEffort(_model);

    // Older GPT-5 reasoning models reject non-default temperature values. Keep
    // the parameter in the request, but use the API-compatible default.
    protected override double GetEffectiveTemperature(OpenAIApiFormat format)
        => IsLegacyGpt5ReasoningModel(_model)
            ? 1.0
            : Temperature;

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

    internal static string? GetResponsesReasoningEffort(string model)
    {
        if (SupportsNoneReasoningEffort(model))
            return "none";

        return IsLegacyGpt5ReasoningModel(model)
            ? "minimal"
            : null;
    }

    private static bool SupportsNoneReasoningEffort(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        const string prefix = "gpt-5.";

        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var suffix = normalized[prefix.Length..];
        var length = 0;
        while (length < suffix.Length && char.IsDigit(suffix[length]))
        {
            length++;
        }

        return length > 0
            && int.TryParse(suffix[..length], out var minorVersion)
            && minorVersion >= 1;
    }

    private static bool IsLegacyGpt5ReasoningModel(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();

        return normalized == "gpt-5"
            || normalized.StartsWith("gpt-5-2025-", StringComparison.Ordinal)
            || normalized == "gpt-5-mini"
            || normalized.StartsWith("gpt-5-mini-", StringComparison.Ordinal)
            || normalized == "gpt-5-nano"
            || normalized.StartsWith("gpt-5-nano-", StringComparison.Ordinal);
    }
}
