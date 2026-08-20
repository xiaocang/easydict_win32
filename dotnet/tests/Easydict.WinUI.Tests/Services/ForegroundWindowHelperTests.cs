using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class ForegroundWindowHelperTests
{
    [Theory]
    [InlineData(unchecked((short)0x8000))]
    [InlineData(unchecked((short)0x8001))]
    public void ShouldPrimeForegroundActivation_WhileAltIsDown_ReturnsFalse(short altKeyState)
    {
        var shouldPrime = ForegroundWindowHelper.ShouldPrimeForegroundActivation(altKeyState);

        shouldPrime.Should().BeFalse(
            "a synthetic Alt key-up would clear the modifier while the user is still holding it");
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)1)]
    public void ShouldPrimeForegroundActivation_WhileAltIsUp_ReturnsTrue(short altKeyState)
    {
        var shouldPrime = ForegroundWindowHelper.ShouldPrimeForegroundActivation(altKeyState);

        shouldPrime.Should().BeTrue(
            "the foreground activation workaround is still needed when Alt is not held");
    }
}
