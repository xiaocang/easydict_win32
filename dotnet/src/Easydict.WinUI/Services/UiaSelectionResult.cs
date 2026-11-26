namespace Easydict.WinUI.Services;

/// <summary>Distinguishes an empty selection from an unavailable UIA provider.</summary>
internal sealed record UiaSelectionResult(string? Text, bool IsConfirmedEmpty)
{
    internal static UiaSelectionResult Unavailable { get; } = new(null, false);
    internal static UiaSelectionResult Empty { get; } = new(null, true);

    internal static UiaSelectionResult FromText(string? text)
        => text == null ? Unavailable
            : text.Length == 0 ? Empty
            : new(text, string.IsNullOrWhiteSpace(text));
}
