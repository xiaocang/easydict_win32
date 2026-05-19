using System.Text.Json.Serialization;

namespace Easydict.SidecarClient.Protocol;

public static class OcrMethods
{
    public const string Recognize = "recognize";
}

public sealed class OcrRecognizeParams
{
    [JsonPropertyName("pixelDataPath")]
    public required string PixelDataPath { get; init; }

    [JsonPropertyName("pixelWidth")]
    public required int PixelWidth { get; init; }

    [JsonPropertyName("pixelHeight")]
    public required int PixelHeight { get; init; }

    [JsonPropertyName("preferredLanguageTag")]
    public string? PreferredLanguageTag { get; init; }
}

public sealed class OcrResultDto
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("lines")]
    public IReadOnlyList<OcrLineDto> Lines { get; init; } = [];

    [JsonPropertyName("detectedLanguage")]
    public OcrLanguageDto? DetectedLanguage { get; init; }

    [JsonPropertyName("textAngle")]
    public double? TextAngle { get; init; }
}

public sealed class OcrLineDto
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("boundingRect")]
    public OcrRectDto BoundingRect { get; init; }
}

public readonly record struct OcrRectDto(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);

public sealed class OcrLanguageDto
{
    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;
}
