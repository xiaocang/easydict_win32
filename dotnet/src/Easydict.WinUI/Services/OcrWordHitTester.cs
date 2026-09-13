using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Picks the OCR word under a point. Pure logic (no Win32/WinRT), unit-testable.
/// </summary>
public static class OcrWordHitTester
{
    /// <summary>
    /// Find the word whose bounding rectangle contains (<paramref name="x"/>, <paramref name="y"/>).
    /// When no rectangle contains the point, the nearest word on the same text row within
    /// <paramref name="maxDistance"/> pixels is returned. Returns null when nothing qualifies.
    /// Coordinates are in the same space as the word rectangles (image pixels).
    /// </summary>
    public static OcrWord? FindWordAt(IReadOnlyList<OcrLine> lines, double x, double y, double maxDistance = 6)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        OcrWord? best = null;
        var bestArea = double.MaxValue;
        foreach (var line in lines)
        {
            foreach (var word in line.Words)
            {
                var rect = word.BoundingRect;
                if (rect.IsEmpty() || !rect.Contains(x, y))
                {
                    continue;
                }

                var area = rect.Width * rect.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = word;
                }
            }
        }

        if (best is not null || maxDistance <= 0)
        {
            return best;
        }

        var bestDistance = double.MaxValue;
        foreach (var line in lines)
        {
            foreach (var word in line.Words)
            {
                var rect = word.BoundingRect;
                if (rect.IsEmpty())
                {
                    continue;
                }

                // Only consider words whose row spans the pointer's Y (with tolerance).
                if (y < rect.Y - maxDistance || y > rect.Bottom() + maxDistance)
                {
                    continue;
                }

                var dx = x < rect.X ? rect.X - x : x > rect.Right() ? x - rect.Right() : 0;
                var dy = y < rect.Y ? rect.Y - y : y > rect.Bottom() ? y - rect.Bottom() : 0;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= maxDistance && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = word;
                }
            }
        }

        return best;
    }
}
