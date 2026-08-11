using Easydict.TranslationService.Services;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Immutable snapshot of OCR engine configuration used to construct services
/// without mutating persisted settings.
/// </summary>
public sealed record OcrServiceOptions
{
    public const string DefaultOllamaEndpoint = "http://localhost:11434/api/generate";
    public const string DefaultCustomApiEndpoint = OpenAIService.DefaultEndpoint;
    public const string DefaultEndpoint = DefaultOllamaEndpoint;

    public const string DefaultOllamaModel = "glm-ocr";
    public const string DefaultCustomApiModel = OpenAIService.DefaultModel;
    public const string DefaultModel = DefaultOllamaModel;

    /// <summary>
    /// Initial output token budget for API-based OCR. A screenshot rarely needs more,
    /// and services escalate from here when a response comes back truncated.
    /// </summary>
    public const int DefaultMaxTokens = 512;

    /// <summary>
    /// Initial output token budget when thinking is allowed. Reasoning tokens are
    /// drawn from the same budget, so starting at <see cref="DefaultMaxTokens"/>
    /// would truncate before the model emits any recognized text.
    /// </summary>
    public const int ThinkingMaxTokens = 2048;

    /// <summary>
    /// Upper bound for token escalation.
    /// </summary>
    public const int MaxTokensCeiling = 4096;

    public OcrEngineType Engine { get; }

    public string? ApiKey { get; }

    public string Endpoint { get; }

    public string Model { get; }

    public string SystemPrompt { get; }

    /// <summary>
    /// Whether the model is allowed to think before answering. Off by default:
    /// reasoning adds substantial latency without improving text recognition.
    /// Only has an effect on models that actually support thinking.
    /// </summary>
    public bool EnableThinking { get; }

    public OcrServiceOptions(
        OcrEngineType engine,
        string? apiKey,
        string? endpoint,
        string? model,
        string? systemPrompt,
        bool enableThinking = false)
    {
        Engine = engine;
        ApiKey = NormalizeOptional(apiKey);
        Endpoint = NormalizeRequired(endpoint, GetDefaultEndpoint(engine));
        Model = NormalizeRequired(model, GetDefaultModel(engine));
        SystemPrompt = systemPrompt?.Trim() ?? string.Empty;
        EnableThinking = enableThinking;
    }

    public static OcrServiceOptions FromSettings(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new OcrServiceOptions(
            settings.OcrEngine,
            settings.OcrApiKey,
            settings.OcrEndpoint,
            settings.OcrModel,
            settings.OcrSystemPrompt,
            settings.OcrEnableThinking);
    }

    /// <summary>
    /// Initial output token budget for this configuration.
    /// </summary>
    public int GetInitialMaxTokens() => EnableThinking ? ThinkingMaxTokens : DefaultMaxTokens;

    public static string GetDefaultEndpoint(OcrEngineType engine) => engine switch
    {
        OcrEngineType.CustomApi => DefaultCustomApiEndpoint,
        OcrEngineType.PpOcrV6 => string.Empty,
        _ => DefaultOllamaEndpoint,
    };

    public static string GetDefaultModel(OcrEngineType engine) => engine switch
    {
        OcrEngineType.CustomApi => DefaultCustomApiModel,
        OcrEngineType.PpOcrV6 => Easydict.SidecarClient.Protocol.PpOcrV6ModelCatalog.SmallId,
        _ => DefaultOllamaModel,
    };

    public static bool IsKnownDefaultEndpoint(string? endpoint)
    {
        var normalized = endpoint?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, DefaultOllamaEndpoint, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, DefaultCustomApiEndpoint, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsKnownDefaultModel(string? model)
    {
        var normalized = model?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, DefaultOllamaModel, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, DefaultCustomApiModel, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, Easydict.SidecarClient.Protocol.PpOcrV6ModelCatalog.TinyId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, Easydict.SidecarClient.Protocol.PpOcrV6ModelCatalog.SmallId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, Easydict.SidecarClient.Protocol.PpOcrV6ModelCatalog.MediumId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeRequired(string? value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }
}
