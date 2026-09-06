using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Easydict.WinUI.Views;

/// <summary>Preserves natural card heights while sizing comparison columns equally.</summary>
public sealed class SavedResultsPanel : Panel
{
    public bool IsComparison
    {
        get => (bool)GetValue(IsComparisonProperty);
        set => SetValue(IsComparisonProperty, value);
    }

    public static readonly DependencyProperty IsComparisonProperty = DependencyProperty.Register(
        nameof(IsComparison), typeof(bool), typeof(SavedResultsPanel),
        new PropertyMetadata(false, (sender, _) => ((SavedResultsPanel)sender).InvalidateMeasure()));

    private int Columns(double width) => Services.SavedItems.SavedItemsPresentation.ComparisonColumns(IsComparison, width);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 640;
        var columns = Columns(width);
        var cardWidth = Math.Max(0, (width - (columns - 1) * 16) / columns);
        var height = 0d;
        for (var i = 0; i < Children.Count; i += columns)
        {
            var rowHeight = 0d;
            for (var j = i; j < Math.Min(i + columns, Children.Count); j++)
            {
                Children[j].Measure(new Size(cardWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[j].DesiredSize.Height);
            }
            height += rowHeight + (i == 0 ? 0 : 16);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var width = Math.Max(0, (finalSize.Width - (columns - 1) * 16) / columns);
        var y = 0d;
        for (var i = 0; i < Children.Count; i += columns)
        {
            var rowHeight = 0d;
            for (var j = i; j < Math.Min(i + columns, Children.Count); j++)
            {
                var height = Children[j].DesiredSize.Height;
                Children[j].Arrange(new Rect((j - i) * (width + 16), y, width, height));
                rowHeight = Math.Max(rowHeight, height);
            }
            y += rowHeight + 16;
        }
        return finalSize;
    }
}
