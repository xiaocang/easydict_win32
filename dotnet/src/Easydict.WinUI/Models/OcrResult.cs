namespace Easydict.WinUI.Models;

/// <summary>
/// Result of an OCR recognition operation.
/// </summary>
public record OcrResult
{
    /// <summary>
    /// The full recognized text with lines joined by newlines.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Individual lines recognized, preserving spatial layout.
    /// </summary>
    public IReadOnlyList<OcrLine> Lines { get; init; } = [];

    /// <summary>
    /// Detected language of the recognized text, if available.
    /// </summary>
    public OcrLanguage? DetectedLanguage { get; init; }

    /// <summary>
    /// Text angle in degrees detected by the OCR engine (for rotation correction).
    /// Null if the engine did not detect rotation.
    /// </summary>
    public double? TextAngle { get; init; }
}

/// <summary>
/// A single line of recognized text with its bounding rectangle.
/// </summary>
public record OcrLine
{
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Bounding rectangle in physical pixels relative to the source image.
    /// </summary>
    public OcrRect BoundingRect { get; init; }
}

/// <summary>
/// A simple rectangle struct that does not depend on WinUI/WinRT types,
/// so it can be used in unit tests without the Windows App SDK runtime.
/// </summary>
public readonly record struct OcrRect(double X, double Y, double Width, double Height);

/// <summary>
/// Represents an OCR-capable language installed on the system.
/// </summary>
public record OcrLanguage
{
    /// <summary>
    /// BCP-47 language tag (e.g. "zh-Hans-CN", "en-US", "ja").
    /// </summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable display name (e.g. "简体中文", "English").
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;
}
