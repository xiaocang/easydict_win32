using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Immutable snapshot of OCR engine configuration used to construct services
/// without mutating persisted settings.
/// </summary>
public sealed record OcrServiceOptions
{
    public const string DefaultEndpoint = "http://localhost:11434/api/generate";
    public const string DefaultModel = "glm-ocr";

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
        Endpoint = NormalizeRequired(endpoint, DefaultEndpoint);
        Model = NormalizeRequired(model, DefaultModel);
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

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeRequired(string? value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }
}
