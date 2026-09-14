using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the pure dwell detector used by hover word lookup.
/// No Win32 dependencies: timing is passed in explicitly.
/// </summary>
public class HoverDwellDetectorTests
{
    private readonly HoverDwellDetector _detector = new();

    private static MouseHookService.POINT Pt(int x, int y) => new() { x = x, y = y };

    [Fact]
    public void Tick_BeforeAnyMove_DoesNotFire()
    {
        _detector.IsResting.Should().BeFalse();
        _detector.Tick(10_000, triggerSatisfied: true).Fired.Should().BeFalse();
    }

    [Fact]
    public void Tick_FiresOnceAfterDwell_WhenTriggerHeld()
    {
        _detector.OnMouseMove(Pt(100, 100), 0);

        _detector.Tick(HoverDwellDetector.DwellMs - 1, true).Fired.Should().BeFalse();

        var result = _detector.Tick(HoverDwellDetector.DwellMs, true);
        result.Fired.Should().BeTrue();
        result.Point.x.Should().Be(100);
        result.Point.y.Should().Be(100);

        // Same rest never fires twice.
        _detector.Tick(HoverDwellDetector.DwellMs + 500, true).Fired.Should().BeFalse();
        _detector.IsArmed.Should().BeFalse();
    }

    [Fact]
    public void Jitter_WithinTolerance_KeepsRestClock()
    {
        _detector.OnMouseMove(Pt(100, 100), 0);
        _detector.OnMouseMove(Pt(102, 101), 200); // ≤ 3 px: still the same rest

        _detector.RestPoint.x.Should().Be(100);
        _detector.Tick(HoverDwellDetector.DwellMs, true).Fired.Should().BeTrue();
    }

    [Fact]
    public void Movement_BeyondTolerance_RestartsRestClock()
    {
        _detector.OnMouseMove(Pt(100, 100), 0);
        _detector.OnMouseMove(Pt(110, 100), 200);

        _detector.Tick(HoverDwellDetector.DwellMs, true).Fired.Should().BeFalse();

        var result = _detector.Tick(200 + HoverDwellDetector.DwellMs, true);
        result.Fired.Should().BeTrue();
        result.Point.x.Should().Be(110);
    }

    [Fact]
    public void TriggerNotSatisfied_NeverFires_ButFiresOnFirstSatisfiedTick()
    {
        _detector.OnMouseMove(Pt(100, 100), 0);

        _detector.Tick(5_000, triggerSatisfied: false).Fired.Should().BeFalse();
        _detector.IsArmed.Should().BeTrue();

        // Once the caller's key-hold requirement is satisfied, an already resting pointer fires.
        _detector.Tick(5_001, triggerSatisfied: true).Fired.Should().BeTrue();
    }

    [Fact]
    public void AfterFiring_DoesNotRefire_UntilPointerLeavesRearmDistance()
    {
        _detector.OnMouseMove(Pt(100, 100), 0);
        _detector.Tick(HoverDwellDetector.DwellMs, true).Fired.Should().BeTrue();

        // Small move (below the re-arm distance) starts a new rest that stays disarmed.
        _detector.OnMouseMove(Pt(105, 100), 1_000);
        _detector.IsArmed.Should().BeFalse();
        _detector.Tick(2_000, true).Fired.Should().BeFalse();

        // Moving far enough (beyond both the jitter tolerance of the current rest and the
        // re-arm distance from the fired point) re-arms.
        var farX = 100 + HoverDwellDetector.RearmDistancePx + HoverDwellDetector.JitterTolerancePx;
        _detector.OnMouseMove(Pt(farX, 100), 2_000);
        _detector.IsArmed.Should().BeTrue();

        var result = _detector.Tick(2_000 + HoverDwellDetector.DwellMs, true);
        result.Fired.Should().BeTrue();
        result.Point.x.Should().Be(farX);
    }

    [Fact]
    public void Rearm_AllowsTheSameRestToFireAgain()
    {
        _detector.OnMouseMove(Pt(100, 100), 0);
        _detector.Tick(HoverDwellDetector.DwellMs, true).Fired.Should().BeTrue();
        _detector.Tick(HoverDwellDetector.DwellMs + 1, true).Fired.Should().BeFalse();

        _detector.Rearm();

        _detector.IsArmed.Should().BeTrue();
        var result = _detector.Tick(HoverDwellDetector.DwellMs + 2, true);
        result.Fired.Should().BeTrue();
        result.Point.x.Should().Be(100);
    }

    [Fact]
    public void Rearm_WithoutRest_StaysDisarmed()
    {
        _detector.Rearm();
        _detector.IsArmed.Should().BeFalse();
        _detector.Tick(10_000, true).Fired.Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsRestAndFiredState()
    {
        _detector.OnMouseMove(Pt(100, 100), 0);
        _detector.Tick(HoverDwellDetector.DwellMs, true).Fired.Should().BeTrue();

        _detector.Reset();

        _detector.IsResting.Should().BeFalse();
        _detector.IsArmed.Should().BeFalse();
        _detector.Tick(10_000, true).Fired.Should().BeFalse();

        // After a reset the previous fired point no longer blocks re-arming.
        _detector.OnMouseMove(Pt(100, 100), 20_000);
        _detector.IsArmed.Should().BeTrue();
    }
}
