using Easydict.WinUI.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the hover word lookup trigger-key enum helpers (pure logic).
/// </summary>
public class HoverLookupModifierTests
{
    [Theory]
    [InlineData("Ctrl", HoverLookupModifier.Ctrl)]
    [InlineData("ctrl", HoverLookupModifier.Ctrl)]
    [InlineData("SHIFT", HoverLookupModifier.Shift)]
    [InlineData(" Alt ", HoverLookupModifier.Alt)]
    [InlineData("None", HoverLookupModifier.None)]
    public void Parse_KnownNames_AreCaseInsensitive(string value, HoverLookupModifier expected)
    {
        HoverLookupModifierExtensions.Parse(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("7")]
    [InlineData("Ctrl+Alt")]
    public void Parse_UnknownValues_FallBackToCtrl(string? value)
    {
        HoverLookupModifierExtensions.Parse(value).Should().Be(HoverLookupModifier.Ctrl);
        HoverLookupModifierExtensions.Default.Should().Be(HoverLookupModifier.Ctrl);
    }

    [Theory]
    [InlineData(HoverLookupModifier.Ctrl, 0x11u, true)]   // VK_CONTROL
    [InlineData(HoverLookupModifier.Ctrl, 0xA2u, true)]   // VK_LCONTROL
    [InlineData(HoverLookupModifier.Ctrl, 0xA3u, true)]   // VK_RCONTROL
    [InlineData(HoverLookupModifier.Ctrl, 0x10u, false)]  // VK_SHIFT
    [InlineData(HoverLookupModifier.Shift, 0x10u, true)]
    [InlineData(HoverLookupModifier.Shift, 0xA0u, true)]
    [InlineData(HoverLookupModifier.Shift, 0xA1u, true)]
    [InlineData(HoverLookupModifier.Shift, 0x12u, false)]
    [InlineData(HoverLookupModifier.Alt, 0x12u, true)]    // VK_MENU
    [InlineData(HoverLookupModifier.Alt, 0xA4u, true)]
    [InlineData(HoverLookupModifier.Alt, 0xA5u, true)]
    [InlineData(HoverLookupModifier.Alt, 0x43u, false)]   // 'C'
    [InlineData(HoverLookupModifier.None, 0x11u, false)]
    [InlineData(HoverLookupModifier.None, 0x10u, false)]
    [InlineData(HoverLookupModifier.None, 0x12u, false)]
    public void MatchesVirtualKey_MatchesGenericAndSidedCodes(HoverLookupModifier modifier, uint vk, bool expected)
    {
        modifier.MatchesVirtualKey(vk).Should().Be(expected);
    }

    [Theory]
    [InlineData(0x10u, true)]
    [InlineData(0x11u, true)]
    [InlineData(0x12u, true)]
    [InlineData(0x5Bu, true)]  // VK_LWIN
    [InlineData(0x5Cu, true)]  // VK_RWIN
    [InlineData(0xA0u, true)]
    [InlineData(0xA5u, true)]
    [InlineData(0x43u, false)] // 'C'
    [InlineData(0x1Bu, false)] // Escape
    [InlineData(0x20u, false)] // Space
    public void IsAnyModifierVirtualKey_RecognizesModifierKeysOnly(uint vk, bool expected)
    {
        HoverLookupModifierExtensions.IsAnyModifierVirtualKey(vk).Should().Be(expected);
    }

    [Fact]
    public void GetPollVirtualKeys_ReturnsSidedKeys()
    {
        HoverLookupModifier.Ctrl.GetPollVirtualKeys().Should().Equal(0xA2, 0xA3);
        HoverLookupModifier.Shift.GetPollVirtualKeys().Should().Equal(0xA0, 0xA1);
        HoverLookupModifier.Alt.GetPollVirtualKeys().Should().Equal(0xA4, 0xA5);
        HoverLookupModifier.None.GetPollVirtualKeys().Should().BeEmpty();
    }
}
