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
internal sealed class ThemeResourceResolver : IResourceResolver
{
    private readonly Dictionary<string, DxColor?> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DxThickness?> _thicknesses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DxCornerRadius?> _cornerRadii = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double?> _doubles = new(StringComparer.Ordinal);
    private FrameworkElement? _themeRoot;

    public ThemeResourceResolver(FrameworkElement? themeRoot)
    {
        _themeRoot = themeRoot;
    }

    public FrameworkElement? ThemeRoot
    {
        get => _themeRoot;
        set
        {
            _themeRoot = value;
            Invalidate();
        }
    }

    public void Invalidate()
    {
        _colors.Clear();
        _thicknesses.Clear();
        _cornerRadii.Clear();
        _doubles.Clear();
    }

    public bool TryGetColor(string key, out DxColor color)
    {
        if (_colors.TryGetValue(key, out DxColor? cached))
        {
            color = cached.GetValueOrDefault();
            return cached.HasValue;
        }

        DxColor? resolved = null;
        if (ThemeResourceService.GetBrush(key, _themeRoot) is SolidColorBrush brush)
        {
            Windows.UI.Color value = brush.Color;
            resolved = new DxColor(value.A, value.R, value.G, value.B);
        }

        _colors[key] = resolved;
        color = resolved.GetValueOrDefault();
        return resolved.HasValue;
    }

    public bool TryGetThickness(string key, out DxThickness thickness)
    {
        if (_thicknesses.TryGetValue(key, out DxThickness? cached))
        {
            thickness = cached.GetValueOrDefault();
            return cached.HasValue;
        }

        DxThickness? resolved = null;
        if (ThemeResourceService.TryGetResource(key, _themeRoot, out Thickness value))
        {
            resolved = new DxThickness(value.Left, value.Top, value.Right, value.Bottom);
        }

        _thicknesses[key] = resolved;
        thickness = resolved.GetValueOrDefault();
        return resolved.HasValue;
    }

    public bool TryGetCornerRadius(string key, out DxCornerRadius radius)
    {
        if (_cornerRadii.TryGetValue(key, out DxCornerRadius? cached))
        {
            radius = cached.GetValueOrDefault();
            return cached.HasValue;
        }

        DxCornerRadius? resolved = null;
        if (ThemeResourceService.TryGetResource(key, _themeRoot, out CornerRadius value))
        {
            resolved = new DxCornerRadius(
                value.TopLeft,
                value.TopRight,
                value.BottomRight,
                value.BottomLeft);
        }

        _cornerRadii[key] = resolved;
        radius = resolved.GetValueOrDefault();
        return resolved.HasValue;
    }

    public bool TryGetDouble(string key, out double value)
    {
        if (_doubles.TryGetValue(key, out double? cached))
        {
            value = cached.GetValueOrDefault();
            return cached.HasValue;
        }

        double? resolved = ThemeResourceService.TryGetResource(key, _themeRoot, out double resource)
            ? resource
            : null;
        _doubles[key] = resolved;
        value = resolved.GetValueOrDefault();
        return resolved.HasValue;
    }
}
