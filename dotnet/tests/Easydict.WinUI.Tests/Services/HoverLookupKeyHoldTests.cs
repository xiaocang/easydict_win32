using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class HoverLookupKeyHoldTests
{
    [Fact]
    public void RestingPointer_WaitsForFiftyMillisecondsOfKeyHold()
    {
        var hold = new HoverLookupKeyHold();
        var dwell = new HoverDwellDetector();
        dwell.OnMouseMove(new MouseHookService.POINT { x = 100, y = 100 }, 0);

        hold.SetDown(true, 1_000);
        dwell.Tick(1_000, hold.IsSatisfied(1_000)).Fired.Should().BeFalse();
        dwell.Tick(1_049, hold.IsSatisfied(1_049)).Fired.Should().BeFalse();
        dwell.Tick(1_050, hold.IsSatisfied(1_050)).Fired.Should().BeTrue();
    }

    [Fact]
    public void ShortPressAndRepress_DoNotAccumulateHoldTime()
    {
        var hold = new HoverLookupKeyHold();
        hold.IsSatisfied(1_000).Should().BeFalse();
        hold.SetDown(true, 1_000);
        hold.SetDown(false, 1_049);
        hold.IsSatisfied(2_000).Should().BeFalse();

        hold.SetDown(true, 2_000);
        hold.IsSatisfied(2_049).Should().BeFalse();
        hold.IsSatisfied(2_050).Should().BeTrue();
    }

    [Fact]
    public void RepeatedKeyDown_DoesNotRestartHoldTime()
    {
        var hold = new HoverLookupKeyHold();
        hold.SetDown(true, 0);
        hold.SetDown(true, 40);
        hold.IsSatisfied(50).Should().BeTrue();
        hold.SetDown(false, 60);
        hold.IsSatisfied(100).Should().BeFalse();
    }

    [Fact]
    public void HeldKey_StillRequiresPointerDwell()
    {
        var hold = new HoverLookupKeyHold();
        var dwell = new HoverDwellDetector();
        hold.SetDown(true, 0);
        dwell.OnMouseMove(new MouseHookService.POINT { x = 100, y = 100 }, 100);

        dwell.Tick(449, hold.IsSatisfied(449)).Fired.Should().BeFalse();
        dwell.Tick(450, hold.IsSatisfied(450)).Fired.Should().BeTrue();
    }
}
