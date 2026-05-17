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

    public OcrEngineType Engine { get; }

    public string? ApiKey { get; }

    public string Endpoint { get; }

    public string Model { get; }

    public string SystemPrompt { get; }

    public OcrServiceOptions(
        OcrEngineType engine,
        string? apiKey,
        string? endpoint,
        string? model,
        string? systemPrompt)
    {
        Engine = engine;
        ApiKey = NormalizeOptional(apiKey);
        Endpoint = NormalizeRequired(endpoint, GetDefaultEndpoint(engine));
        Model = NormalizeRequired(model, GetDefaultModel(engine));
        SystemPrompt = systemPrompt?.Trim() ?? string.Empty;
    }

    public static OcrServiceOptions FromSettings(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new OcrServiceOptions(
            settings.OcrEngine,
            settings.OcrApiKey,
            settings.OcrEndpoint,
            settings.OcrModel,
            settings.OcrSystemPrompt);
    }

    public static string GetDefaultEndpoint(OcrEngineType engine) => engine switch
    {
        OcrEngineType.CustomApi => DefaultCustomApiEndpoint,
        _ => DefaultOllamaEndpoint,
    };

    public static string GetDefaultModel(OcrEngineType engine) => engine switch
    {
        OcrEngineType.CustomApi => DefaultCustomApiModel,
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
               string.Equals(normalized, DefaultCustomApiModel, StringComparison.OrdinalIgnoreCase);
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
