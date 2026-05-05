using Polyglot.TextLayout.Segmentation;

namespace Polyglot.TextLayout.Tests.Helpers;

/// <summary>
/// Deterministic text measurer for testing: CJK characters are 10pt wide,
/// Latin characters are 6pt wide, spaces are 3pt wide.
/// </summary>
internal sealed class FixedWidthMeasurer : ITextMeasurer
{
    public double CjkWidth { get; init; } = 10.0;
    public double LatinWidth { get; init; } = 6.0;
    public double SpaceWidth { get; init; } = 3.0;

    public double MeasureSegment(string text)
    {
        var total = 0.0;
        foreach (var ch in text)
            total += MeasureChar(ch);
        return total;
    }

    public double MeasureGrapheme(string grapheme)
    {
        if (grapheme.Length == 0) return 0;
        return MeasureChar(grapheme[0]);
    }

    private double MeasureChar(char ch)
    {
        if (ch is ' ' or '\t')
            return SpaceWidth;
        if (ScriptClassifier.IsCjk(ch))
            return CjkWidth;
        return LatinWidth;
    }
}
