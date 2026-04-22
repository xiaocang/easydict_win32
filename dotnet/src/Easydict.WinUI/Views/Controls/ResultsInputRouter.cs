using Windows.System;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// Key classification used by Mini/Fixed windows: which keys pressed inside the
/// input TextBox should be consumed to scroll the results ScrollViewer instead
/// of being handled by the TextBox itself.
/// </summary>
internal static class ResultsInputRouter
{
    /// <summary>
    /// Keys that scroll results: PageUp/PageDown (by viewport) and Up/Down
    /// (by line). Home/End/Left/Right keep their normal TextBox meanings so
    /// editing inside the input TextBox is unaffected.
    /// </summary>
    public static bool IsScrollNavigationKey(VirtualKey key) =>
        key is VirtualKey.PageUp
            or VirtualKey.PageDown
            or VirtualKey.Up
            or VirtualKey.Down;
}
