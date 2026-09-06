using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Easydict.WinUI.Views.Controls;

/// <summary>Reflows the host's existing selectors and query button; owns no query state.</summary>
public sealed class FluentLanguageBar : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 360;
        var controls = Children.OfType<FrameworkElement>().OrderBy(child => Grid.GetColumn(child)).ToArray();
        var stacked = width < 360;
        double fixedWidth = 0, height = 32, actionHeight = 0;
        foreach (var child in controls.Where(child => child is not ComboBox))
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            if (stacked && ReferenceEquals(child, controls.LastOrDefault())) actionHeight = child.DesiredSize.Height + 8;
            else fixedWidth += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        foreach (var combo in controls.OfType<ComboBox>())
        {
            combo.MinWidth = 0;
            combo.Measure(new Size(Math.Max(0, (width - fixedWidth) / 2), double.PositiveInfinity));
            height = Math.Max(height, combo.DesiredSize.Height);
        }
        return new Size(width, height + actionHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var controls = Children.OfType<FrameworkElement>().OrderBy(child => Grid.GetColumn(child)).ToArray();
        var stacked = finalSize.Width < 360;
        var action = controls.LastOrDefault();
        var row = controls.Where(child => !stacked || !ReferenceEquals(child, action)).ToArray();
        var fixedWidth = row.Where(child => child is not ComboBox).Sum(child => child.DesiredSize.Width);
        var comboWidth = Math.Max(0, (finalSize.Width - fixedWidth) / 2);
        var height = row.Length == 0 ? 32 : row.Max(child => child.DesiredSize.Height);
        double x = 0;
        foreach (var child in row)
        {
            var width = child is ComboBox ? comboWidth : child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, height));
            x += width;
        }
        if (stacked && action is not null)
            action.Arrange(new Rect(Math.Max(0, finalSize.Width - action.DesiredSize.Width), height + 8, action.DesiredSize.Width, action.DesiredSize.Height));
        return finalSize;
    }
}
