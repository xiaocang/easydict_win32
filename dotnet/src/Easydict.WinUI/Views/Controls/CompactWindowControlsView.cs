using Easydict.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Easydict.WinUI.Views.Controls;

internal sealed class CompactWindowControlsView
{
    private readonly Border _surface;
    private readonly Border _separator;
    private readonly FontIcon _closeIcon;

    public CompactWindowControlsView()
    {
        Root = new Grid
        {
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0),
            Opacity = 0.52,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(Root, 6);
        Canvas.SetZIndex(Root, 10);

        _surface = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22)
        };
        Root.Children.Add(_surface);

        var layout = new Grid { Margin = new Thickness(1) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.Children.Add(layout);

        _closeIcon = new FontIcon { Glyph = "\uE8BB", FontSize = 9 };
        CloseButton = CreateSegmentButton(_closeIcon);
        Grid.SetRow(CloseButton, 0);
        layout.Children.Add(CloseButton);

        _separator = CreateSeparator();
        Grid.SetRow(_separator, 0);
        layout.Children.Add(_separator);

        DragIsland = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(0, 0, 21, 21)
        };
        Grid.SetRow(DragIsland, 1);

        DragIsland.Child = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-24_altform-unplated.png")),
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Opacity = 0.52,
            Stretch = Stretch.Uniform
        };
        layout.Children.Add(DragIsland);
    }

    public Grid Root { get; }

    public Button CloseButton { get; }

    public Border DragIsland { get; }

    public void RefreshTheme(FrameworkElement? themeRoot)
    {
        var surfaceBrush = ThemeResourceService.GetBrush("ControlFillColorDefaultBrush", themeRoot)
            ?? new SolidColorBrush(Colors.White);
        var secondarySurfaceBrush = ThemeResourceService.GetBrush("ControlFillColorSecondaryBrush", themeRoot)
            ?? surfaceBrush;
        var strokeBrush = ThemeResourceService.GetBrush("ControlStrokeColorDefaultBrush", themeRoot)
            ?? new SolidColorBrush(Colors.Gray);
        var textBrush = ThemeResourceService.GetBrush("TextFillColorSecondaryBrush", themeRoot)
            ?? new SolidColorBrush(Colors.DimGray);
        var transparentBrush = new SolidColorBrush(Colors.Transparent);

        _surface.Background = surfaceBrush;
        _surface.BorderBrush = strokeBrush;
        _separator.Background = strokeBrush;
        DragIsland.Background = secondarySurfaceBrush;
        CloseButton.Background = transparentBrush;
        _closeIcon.Foreground = textBrush;
    }

    private static Button CreateSegmentButton(FontIcon icon)
    {
        return new Button
        {
            Content = icon,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    private static Border CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Opacity = 0.55
        };
    }
}
