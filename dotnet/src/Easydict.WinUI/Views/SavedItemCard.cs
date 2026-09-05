using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Easydict.WinUI.Views;

/// <summary>The card owns its surface states, leaving date headers outside selection.</summary>
public sealed class SavedItemCard : ContentControl
{
    public SavedItemCard()
    {
        Loaded += (_, _) => UpdateFocusBounds();
        SizeChanged += (_, _) => UpdateFocusBounds();
    }

    private void UpdateFocusBounds()
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(this);
        while (parent is not null && parent is not ListViewItem)
            parent = VisualTreeHelper.GetParent(parent);
        if (parent is ListViewItem container)
        {
            var origin = TransformToVisual(container).TransformPoint(new Point(0, 0));
            container.FocusVisualMargin = new Thickness(-2, Math.Max(-2, origin.Y - 2), -2, -2);
        }
    }

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(SavedItemCard),
        new PropertyMetadata(false, (sender, _) => ((SavedItemCard)sender).UpdateState()));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private bool _pointerOver;

    protected override void OnApplyTemplate() { base.OnApplyTemplate(); UpdateState(); }
    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    { base.OnPointerEntered(e); _pointerOver = true; UpdateState(); }
    protected override void OnPointerExited(PointerRoutedEventArgs e)
    { base.OnPointerExited(e); _pointerOver = false; UpdateState(); }

    private void UpdateState() => VisualStateManager.GoToState(this,
        IsSelected ? "Selected" : _pointerOver ? "PointerOver" : "Normal", false);
}
