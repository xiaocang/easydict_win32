using Easydict.DirectXaml.Theming;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using DxColor = Easydict.DirectXaml.Color;
using DxCornerRadius = Easydict.DirectXaml.CornerRadius;
using DxThickness = Easydict.DirectXaml.Thickness;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// Bridges the direct renderer's resource slots onto <see cref="ThemeResourceService"/>.
///
/// The compiler never folds <c>{ThemeResource}</c> into a literal, so this is the seam that keeps
/// Light / Dark / High Contrast switching working. Resolution deliberately reuses the existing
/// service rather than reimplementing lookup: it already handles the themed-root rule that code
/// created outside the visual tree otherwise gets wrong.
///
/// Note that resource slots back more than colours — the minimal card supplies
/// <c>BorderThickness</c> and <c>CornerRadius</c> that way — so a colour-only resolver would
/// silently flatten the card's border and corners to zero.
/// </summary>
internal sealed class ThemeResourceResolver(FrameworkElement? themeRoot) : IResourceResolver
{
    public FrameworkElement? ThemeRoot { get; set; } = themeRoot;

    public bool TryGetColor(string key, out DxColor color)
    {
        color = default;

        if (ThemeResourceService.GetBrush(key, ThemeRoot) is not SolidColorBrush brush)
        {
            return false;
        }

        Windows.UI.Color value = brush.Color;
        color = new DxColor(value.A, value.R, value.G, value.B);
        return true;
    }

    public bool TryGetThickness(string key, out DxThickness thickness)
    {
        thickness = default;

        if (!ThemeResourceService.TryGetResource(key, ThemeRoot, out Thickness value))
        {
            return false;
        }

        thickness = new DxThickness(value.Left, value.Top, value.Right, value.Bottom);
        return true;
    }

    public bool TryGetCornerRadius(string key, out DxCornerRadius radius)
    {
        radius = default;

        if (!ThemeResourceService.TryGetResource(key, ThemeRoot, out CornerRadius value))
        {
            return false;
        }

        radius = new DxCornerRadius(value.TopLeft, value.TopRight, value.BottomRight, value.BottomLeft);
        return true;
    }

    public bool TryGetDouble(string key, out double value) =>
        ThemeResourceService.TryGetResource(key, ThemeRoot, out value);
}
