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

    public float? Confidence { get; init; }

    /// <summary>
    /// Bounding rectangle in physical pixels relative to the source image.
    /// </summary>
    public OcrRect BoundingRect { get; init; }

    /// <summary>
    /// Individual words with their bounding rectangles when the engine provides them
    /// (Windows OCR does); empty for engines that only return whole lines.
    /// </summary>
    public IReadOnlyList<OcrWord> Words { get; init; } = [];
}

/// <summary>
/// A single recognized word with its bounding rectangle.
/// </summary>
public record OcrWord
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
/// Geometry helpers for <see cref="OcrRect"/>. Pure logic, unit-testable.
/// </summary>
public static class OcrRectExtensions
{
    public static double Right(this OcrRect rect) => rect.X + rect.Width;

    public static double Bottom(this OcrRect rect) => rect.Y + rect.Height;

    public static bool IsEmpty(this OcrRect rect) => rect.Width <= 0 || rect.Height <= 0;

    /// <summary>
    /// Whether the point lies inside the rectangle (edges inclusive).
    /// </summary>
    public static bool Contains(this OcrRect rect, double x, double y)
    {
        return x >= rect.X && x <= rect.Right() && y >= rect.Y && y <= rect.Bottom();
    }

    /// <summary>
    /// Grow (or shrink, with negative values) the rectangle by the given amounts on every side.
    /// </summary>
    public static OcrRect Inflate(this OcrRect rect, double dx, double dy)
    {
        return new OcrRect(rect.X - dx, rect.Y - dy, rect.Width + 2 * dx, rect.Height + 2 * dy);
    }

    /// <summary>
    /// Smallest rectangle containing both rectangles. An empty rectangle contributes nothing.
    /// </summary>
    public static OcrRect Union(this OcrRect a, OcrRect b)
    {
        if (a.IsEmpty()) return b;
        if (b.IsEmpty()) return a;

        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.Right(), b.Right());
        var bottom = Math.Max(a.Bottom(), b.Bottom());
        return new OcrRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Whether the two rectangles overlap (touching edges do not count).
    /// </summary>
    public static bool Intersects(this OcrRect a, OcrRect b)
    {
        if (a.IsEmpty() || b.IsEmpty()) return false;
        return a.X < b.Right() && b.X < a.Right() && a.Y < b.Bottom() && b.Y < a.Bottom();
    }
}

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
