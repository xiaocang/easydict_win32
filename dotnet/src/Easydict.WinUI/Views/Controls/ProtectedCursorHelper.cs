using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// Helper for setting <c>UIElement.ProtectedCursor</c> on sealed WinUI 3 types
/// (such as <c>Border</c>) where the property cannot be reached by subclassing.
/// Caches the <see cref="PropertyInfo"/> on first access and throws on discovery
/// failure so SDK-shape regressions surface loudly instead of silently stopping.
/// </summary>
internal static class ProtectedCursorHelper
{
    private static readonly PropertyInfo _protectedCursorProperty =
        typeof(UIElement).GetProperty(
            "ProtectedCursor",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException(
            "UIElement.ProtectedCursor not found — WindowsAppSDK API shape may have changed.");

    public static void Set(UIElement element, InputCursor? cursor)
    {
        _protectedCursorProperty.SetValue(element, cursor);
    }
}
