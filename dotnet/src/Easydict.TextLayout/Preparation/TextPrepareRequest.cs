namespace Easydict.TextLayout.Preparation;

/// <summary>
/// Input for the text preparation phase.
/// </summary>
public sealed record TextPrepareRequest
{
    /// <summary>
    /// The text to prepare for layout.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// When true, whitespace is normalized (collapse runs, trim trailing before \n).
    /// Matches pdf2zh converter.py:488 behavior used in current WrapTextByWidth.
    /// </summary>
    public bool NormalizeWhitespace { get; init; } = true;
}
