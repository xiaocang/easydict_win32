namespace Easydict.TextLayout.FontFitting;

/// <summary>
/// Result of font size fitting.
/// </summary>
public sealed record FontFitResult(
    double ChosenFontSize,
    double ChosenLineHeight,
    bool WasShrunk,
    bool WasTruncated,
    int LineCount);
