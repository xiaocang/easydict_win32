using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// A simple wrap panel that arranges children left-to-right and wraps to the next row when space runs out.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((WrapPanel)d).InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0;
        double rowHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;

        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var desired = child.DesiredSize;

            if (x > 0 && x + HorizontalSpacing + desired.Width > availableSize.Width)
            {
                // Wrap to next row
                totalHeight += rowHeight + VerticalSpacing;
                totalWidth = Math.Max(totalWidth, x);
                x = 0;
                rowHeight = 0;
            }

            if (x > 0)
                x += HorizontalSpacing;

            x += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }

        totalHeight += rowHeight;
        totalWidth = Math.Max(totalWidth, x);

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;

            if (x > 0 && x + HorizontalSpacing + desired.Width > finalSize.Width)
            {
                // Wrap to next row
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            if (x > 0)
                x += HorizontalSpacing;

            child.Arrange(new Rect(x, y, desired.Width, desired.Height));
            x += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }

        return finalSize;
    }
}
