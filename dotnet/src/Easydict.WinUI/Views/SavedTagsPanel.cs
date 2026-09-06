using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Easydict.WinUI.Views;

/// <summary>Wraps variable-width tags without sizing every tag from the first item.</summary>
public sealed class SavedTagsPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 360;
        foreach (var child in Children) child.Measure(new Size(width, double.PositiveInfinity));
        return Layout(width, false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, used = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            var childWidth = Math.Min(size.Width, width);
            if (x > 0 && x + childWidth > width) { x = 0; y += rowHeight + 4; rowHeight = 0; }
            if (arrange) child.Arrange(new Rect(x, y, childWidth, size.Height));
            used = Math.Max(used, x + childWidth);
            x += childWidth + 6;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(used, y + rowHeight);
    }
}
