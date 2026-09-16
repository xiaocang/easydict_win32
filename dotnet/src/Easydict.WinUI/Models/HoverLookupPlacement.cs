namespace Easydict.WinUI.Models;

/// <summary>
/// Pure placement math for the hover lookup popup (physical pixels).
/// </summary>
public static class HoverLookupPlacement
{
    /// <summary>Gap between the word and the popup, in pixels at 100% scale.</summary>
    public const int GapPx = 8;

    /// <summary>
    /// Compute the popup's top-left corner: below the word (left-aligned) when it fits in the
    /// work area, otherwise above it; finally clamped into the work area. An empty work area
    /// disables clamping.
    /// </summary>
    public static (int X, int Y) Compute(OcrRect anchor, int popupWidth, int popupHeight, OcrRect workArea, int gap = GapPx)
    {
        var x = (int)Math.Round(anchor.X);
        var y = (int)Math.Round(anchor.Bottom()) + gap;

        if (workArea.IsEmpty())
        {
            return (x, y);
        }

        var workLeft = (int)Math.Round(workArea.X);
        var workTop = (int)Math.Round(workArea.Y);
        var workRight = (int)Math.Round(workArea.Right());
        var workBottom = (int)Math.Round(workArea.Bottom());

        if (y + popupHeight > workBottom)
        {
            var above = (int)Math.Round(anchor.Y) - gap - popupHeight;
            y = above >= workTop ? above : Math.Max(workTop, workBottom - popupHeight);
        }

        if (x + popupWidth > workRight)
        {
            x = workRight - popupWidth;
        }

        if (x < workLeft)
        {
            x = workLeft;
        }

        if (y < workTop)
        {
            y = workTop;
        }

        return (x, y);
    }

    /// <summary>
    /// Region the pointer may roam without dismissing the popup: the union of the word
    /// rectangle and the popup rectangle, inflated by <paramref name="margin"/> on every side.
    /// </summary>
    public static OcrRect ComputeSafeZone(OcrRect anchor, OcrRect? popup, double margin)
    {
        var zone = popup is { } popupRect && !popupRect.IsEmpty()
            ? anchor.Union(popupRect)
            : anchor;
        return zone.Inflate(margin, margin);
    }
}
