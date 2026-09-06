using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Easydict.WinUI.Views.Controls;

/// <summary>Label and editor share a row when there is room, otherwise stack.</summary>
public sealed class FluentSettingRow : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return base.MeasureOverride(availableSize);
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 600;
        var stacked = width < 520;
        Children[1].Measure(new Size(stacked ? width : Math.Min(280, width / 2), double.PositiveInfinity));
        Children[0].Measure(new Size(stacked ? width : Math.Max(0, width - Children[1].DesiredSize.Width - 16), double.PositiveInfinity));
        return new Size(width, stacked ? Children[0].DesiredSize.Height + 8 + Children[1].DesiredSize.Height
            : Math.Max(Children[0].DesiredSize.Height, Children[1].DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return finalSize;
        if (finalSize.Width < 520)
        {
            Children[0].Arrange(new Rect(0, 0, finalSize.Width, Children[0].DesiredSize.Height));
            Children[1].Arrange(new Rect(0, Children[0].DesiredSize.Height + 8, Math.Min(finalSize.Width, Children[1].DesiredSize.Width), Children[1].DesiredSize.Height));
        }
        else
        {
            var editorWidth = Children[1].DesiredSize.Width;
            Children[0].Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - editorWidth - 16), finalSize.Height));
            Children[1].Arrange(new Rect(finalSize.Width - editorWidth, 0, editorWidth, finalSize.Height));
        }
        return finalSize;
    }
}
