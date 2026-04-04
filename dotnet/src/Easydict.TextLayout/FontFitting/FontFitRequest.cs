namespace Easydict.TextLayout.FontFitting;

/// <summary>
/// Input for font size fitting. Supports two modes:
/// block-rect (MaxWidth + MaxHeight) and line-rect (LineWidths + LineHeights).
/// </summary>
public sealed record FontFitRequest
{
    /// <summary>Text to fit.</summary>
    public required string Text { get; init; }

    /// <summary>Starting font size in points (typically from BlockTextStyle.FontSize).</summary>
    public required double StartFontSize { get; init; }

    /// <summary>Minimum font size to try before declaring truncation.</summary>
    public double MinFontSize { get; init; } = 6.0;

    /// <summary>Block rect mode: maximum width for all lines.</summary>
    public double? MaxWidth { get; init; }

    /// <summary>Block rect mode: maximum total height for all lines.</summary>
    public double? MaxHeight { get; init; }

    /// <summary>Line height multiplier applied to font size.</summary>
    public double LineHeightMultiplier { get; init; } = 1.2;

    /// <summary>Line rect mode: per-line maximum widths.</summary>
    public IReadOnlyList<double>? LineWidths { get; init; }

    /// <summary>Line rect mode: per-line heights (for font-size ceiling).</summary>
    public IReadOnlyList<double>? LineHeights { get; init; }

    /// <summary>Whether to normalize whitespace during preparation.</summary>
    public bool NormalizeWhitespace { get; init; } = true;
}
