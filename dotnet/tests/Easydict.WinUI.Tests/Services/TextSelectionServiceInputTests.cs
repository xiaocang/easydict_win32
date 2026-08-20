using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class TextSelectionServiceInputTests
{
    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;
    private const ushort VkRShift = 0xA1;
    private const ushort VkRControl = 0xA3;
    private const ushort VkRMenu = 0xA5;

    [Fact]
    public void BuildCtrlCInputs_WithRightSideModifiers_PreservesExactSides()
    {
        var heldModifiers =
            TextSelectionService.HeldModifierKeys.RightControl |
            TextSelectionService.HeldModifierKeys.RightAlt |
            TextSelectionService.HeldModifierKeys.RightShift;

        var inputs = TextSelectionService.BuildCtrlCInputs(heldModifiers);

        inputs.Select(input => (input.U.ki.wVk, input.U.ki.dwFlags)).Should().Equal(
            (VkRMenu, KeyeventfExtendedkey | KeyeventfKeyup),
            (VkRShift, KeyeventfKeyup),
            (VkC, 0u),
            (VkC, KeyeventfKeyup),
            (VkRMenu, KeyeventfExtendedkey),
            (VkRShift, 0u));
        inputs.Should().NotContain(input => input.U.ki.wVk == VkRControl,
            "a physically held Ctrl side must not receive synthetic down/up events");
    }

    [Fact]
    public void BuildCtrlCInputs_WithoutHeldControl_SynthesizesCtrlAroundC()
    {
        var inputs = TextSelectionService.BuildCtrlCInputs(
            TextSelectionService.HeldModifierKeys.RightAlt);

        inputs.Select(input => (input.U.ki.wVk, input.U.ki.dwFlags)).Should().Equal(
            (VkRMenu, KeyeventfExtendedkey | KeyeventfKeyup),
            (VkControl, 0u),
            (VkC, 0u),
            (VkC, KeyeventfKeyup),
            (VkControl, KeyeventfKeyup),
            (VkRMenu, KeyeventfExtendedkey));
    }
}
