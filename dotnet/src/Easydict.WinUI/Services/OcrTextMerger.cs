using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Merges OCR-recognized text lines into coherent text,
/// handling CJK vs Latin spacing and line grouping.
/// This class is pure logic with no Win32/WinRT dependencies, so it is fully unit-testable.
/// </summary>
public static class OcrTextMerger
{
    /// <summary>
    /// Characters in CJK Unified Ideographs and related blocks that should NOT
    /// be separated by spaces when adjacent.
    /// </summary>
    private static bool IsCjkChar(char c)
    {
        // CJK Unified Ideographs
        if (c >= '\u4E00' && c <= '\u9FFF') return true;
        // CJK Unified Ideographs Extension A
        if (c >= '\u3400' && c <= '\u4DBF') return true;
        // CJK Compatibility Ideographs
        if (c >= '\uF900' && c <= '\uFAFF') return true;
        // Hiragana
        if (c >= '\u3040' && c <= '\u309F') return true;
        // Katakana
        if (c >= '\u30A0' && c <= '\u30FF') return true;
        // Hangul Syllables
        if (c >= '\uAC00' && c <= '\uD7AF') return true;
        // CJK Symbols and Punctuation
        if (c >= '\u3000' && c <= '\u303F') return true;
        // Fullwidth Forms
        if (c >= '\uFF00' && c <= '\uFFEF') return true;
        return false;
    }

    /// <summary>
    /// Merge a list of OCR words into a single line string,
    /// inserting spaces between Latin words but not between CJK characters.
    /// </summary>
    public static string MergeWords(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return string.Empty;
        if (words.Count == 1) return words[0];

        var sb = new System.Text.StringBuilder(words.Sum(w => w.Length) + words.Count);
        sb.Append(words[0]);

        for (int i = 1; i < words.Count; i++)
        {
            var prev = words[i - 1];
            var curr = words[i];

            if (prev.Length > 0 && curr.Length > 0)
            {
                var lastChar = prev[^1];
                var firstChar = curr[0];

                // If both characters are CJK, no space needed
                if (IsCjkChar(lastChar) && IsCjkChar(firstChar))
                {
                    sb.Append(curr);
                }
                else
                {
                    sb.Append(' ');
                    sb.Append(curr);
                }
            }
            else
            {
                sb.Append(curr);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Merge recognized OCR lines into final text.
    /// Lines are joined by newlines. Empty lines are preserved.
    /// </summary>
    public static string MergeLines(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return string.Empty;

        return string.Join(Environment.NewLine, lines.Select(l => l.Text));
    }

    /// <summary>
    /// Group raw OCR line data that are on the same visual row
    /// (based on Y-coordinate proximity) and sort left-to-right within each row.
    /// </summary>
    /// <param name="lines">Raw OCR lines with bounding rects.</param>
    /// <param name="yToleranceFactor">
    /// Fraction of average line height used as Y-grouping tolerance (default 0.5).
    /// </param>
    /// <returns>Lines re-ordered by visual layout (top-to-bottom, left-to-right).</returns>
    public static IReadOnlyList<OcrLine> GroupAndSortLines(
        IReadOnlyList<OcrLine> lines,
        double yToleranceFactor = 0.5)
    {
        if (lines.Count <= 1) return lines;

        // Calculate average line height for tolerance
        var avgHeight = lines.Where(l => l.BoundingRect.Height > 0)
                             .Select(l => l.BoundingRect.Height)
                             .DefaultIfEmpty(20)
                             .Average();
        var yTolerance = avgHeight * yToleranceFactor;

        // Sort by Y first, then group lines with similar Y into rows.
        // Use a running average Y for the current row so that slight drift
        // across a wide page doesn't cause incorrect row splits.
        var sorted = lines.OrderBy(l => l.BoundingRect.Y).ToList();
        var rows = new List<List<OcrLine>>();
        var currentRow = new List<OcrLine> { sorted[0] };
        var currentRowYSum = sorted[0].BoundingRect.Y;

        for (int i = 1; i < sorted.Count; i++)
        {
            var line = sorted[i];
            var currentRowYAvg = currentRowYSum / currentRow.Count;
            if (Math.Abs(line.BoundingRect.Y - currentRowYAvg) <= yTolerance)
            {
                currentRow.Add(line);
                currentRowYSum += line.BoundingRect.Y;
            }
            else
            {
                rows.Add(currentRow);
                currentRow = [line];
                currentRowYSum = line.BoundingRect.Y;
            }
        }
        rows.Add(currentRow);

        // Sort each row left-to-right, then flatten
        var result = new List<OcrLine>();
        foreach (var row in rows)
        {
            result.AddRange(row.OrderBy(l => l.BoundingRect.X));
        }

        return result;
    }
}
