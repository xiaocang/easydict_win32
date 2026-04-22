using Easydict.WinUI.Views.Controls;
using FluentAssertions;
using Windows.System;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for <see cref="ResultsInputRouter"/>, the predicate used by Mini/Fixed
/// windows to decide whether a KeyDown on the input TextBox should be consumed
/// to scroll the results ScrollViewer.
/// </summary>
[Trait("Category", "WinUI")]
public class ResultsInputRouterTests
{
    [Theory]
    [InlineData(VirtualKey.PageUp)]
    [InlineData(VirtualKey.PageDown)]
    [InlineData(VirtualKey.Up)]
    [InlineData(VirtualKey.Down)]
    public void IsScrollNavigationKey_ScrollKeys_ReturnTrue(VirtualKey key)
    {
        ResultsInputRouter.IsScrollNavigationKey(key).Should().BeTrue();
    }

    [Theory]
    [InlineData(VirtualKey.Left)]
    [InlineData(VirtualKey.Right)]
    [InlineData(VirtualKey.Home)]
    [InlineData(VirtualKey.End)]
    [InlineData(VirtualKey.Tab)]
    [InlineData(VirtualKey.A)]
    [InlineData(VirtualKey.Z)]
    [InlineData(VirtualKey.Number0)]
    [InlineData(VirtualKey.Space)]
    [InlineData(VirtualKey.Enter)]
    [InlineData(VirtualKey.Back)]
    [InlineData(VirtualKey.Delete)]
    [InlineData(VirtualKey.Escape)]
    [InlineData(VirtualKey.Shift)]
    [InlineData(VirtualKey.Control)]
    public void IsScrollNavigationKey_OtherKeys_ReturnFalse(VirtualKey key)
    {
        ResultsInputRouter.IsScrollNavigationKey(key).Should().BeFalse();
    }
}
