using Easydict.WinUI.Services;
using FluentAssertions;
using Windows.Graphics;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests the pure clamping logic of <see cref="WindowPositionHelper"/>.
/// Drives <see cref="WindowPositionHelper.TryClampToVisibleWorkArea"/> with synthetic
/// work areas so the production wrapper's <c>DisplayArea.FindAll()</c> dependency stays
/// out of the test (DisplayArea requires the Windows App SDK runtime).
/// </summary>
public class WindowPositionHelperTests
{
    private static readonly RectInt32 PrimaryWorkArea = new(0, 0, 1920, 1080);
    private static readonly RectInt32 SecondaryRightWorkArea = new(1920, 0, 2560, 1440);
    private static readonly SizeInt32 MiniSize = new(400, 200);

    [Fact]
    public void Clamp_PointInsideWorkArea_ReturnsUnchanged()
    {
        var desired = new PointInt32(500, 400);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, [PrimaryWorkArea], out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(500);
        clamped.Y.Should().Be(400);
    }

    [Fact]
    public void Clamp_PointPastRightEdge_ShiftsLeftToFit()
    {
        // Saved x = 1700; window is 400 wide; primary work area max-x = 1920. Window would extend
        // 180px past the right edge — clamp x to 1920 - 400 = 1520.
        var desired = new PointInt32(1700, 100);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, [PrimaryWorkArea], out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(1520);
        clamped.Y.Should().Be(100);
    }

    [Fact]
    public void Clamp_PointPastBottomEdge_ShiftsUpToFit()
    {
        // y = 1000, window is 200 tall, work area bottom = 1080 → clamp y to 1080 - 200 = 880.
        var desired = new PointInt32(100, 1000);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, [PrimaryWorkArea], out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(100);
        clamped.Y.Should().Be(880);
    }

    [Fact]
    public void Clamp_PointBeforeTopLeft_ShiftsToWorkAreaOrigin()
    {
        // Negative top-left but window still overlaps the work area.
        var desired = new PointInt32(-50, -30);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, [PrimaryWorkArea], out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(0);
        clamped.Y.Should().Be(0);
    }

    [Fact]
    public void Clamp_RectFullyOutsideAllAreas_ReturnsFalse()
    {
        // The disconnected-external-monitor case: saved at x=3000 but only the
        // laptop's primary screen (0..1920) is currently attached. No overlap → false.
        var desired = new PointInt32(3000, 200);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, [PrimaryWorkArea], out var clamped);

        ok.Should().BeFalse();
        clamped.X.Should().Be(0);
        clamped.Y.Should().Be(0);
    }

    [Fact]
    public void Clamp_RectStraddlesTwoAdjacentAreas_PreservesPosition()
    {
        // Window spans (1820..2220) — 100px in primary, 300px in secondary.
        // All four corners are covered by the union → user's deliberate straddle is preserved.
        var desired = new PointInt32(1820, 100);
        var workAreas = new[] { PrimaryWorkArea, SecondaryRightWorkArea };

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, workAreas, out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(1820);
        clamped.Y.Should().Be(100);
    }

    [Fact]
    public void Clamp_SecondaryStillAttachedSavedRectFullyOnSecondary_PreservesPosition()
    {
        // Saved on the right-side secondary, both monitors still attached → no clamping.
        var desired = new PointInt32(2400, 300);
        var workAreas = new[] { PrimaryWorkArea, SecondaryRightWorkArea };

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, workAreas, out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(2400);
        clamped.Y.Should().Be(300);
    }

    [Fact]
    public void Clamp_EmptyWorkAreaList_ReturnsFalse()
    {
        var desired = new PointInt32(100, 100);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, [], out var clamped);

        ok.Should().BeFalse();
        clamped.X.Should().Be(0);
        clamped.Y.Should().Be(0);
    }

    [Fact]
    public void Clamp_WindowWiderThanWorkArea_PinsToLeftEdge()
    {
        // Window is wider than work area → can't fit; pin x to work.X.
        var workArea = new RectInt32(0, 0, 300, 1080);
        var bigWindow = new SizeInt32(500, 200);
        var desired = new PointInt32(100, 100);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, bigWindow, [workArea], out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(0);
        clamped.Y.Should().Be(100);
    }

    [Fact]
    public void Clamp_TouchingEdgeOnly_NoOverlap_ReturnsFalse()
    {
        // Top-left exactly at right edge — zero-area intersection counts as no overlap.
        var desired = new PointInt32(1920, 0);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, [PrimaryWorkArea], out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void Clamp_WindowTallerThanWorkArea_PinsToTopEdge()
    {
        // Mirror of WindowWiderThanWorkArea: window taller than work area → pin y to work.Y.
        var workArea = new RectInt32(0, 0, 1920, 300);
        var bigWindow = new SizeInt32(400, 500);
        var desired = new PointInt32(100, 100);

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, bigWindow, [workArea], out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(100);
        clamped.Y.Should().Be(0);
    }

    [Fact]
    public void Clamp_StraddleBreaksAtBottom_BestOverlapPicksSecondaryAndClamps()
    {
        // desired = (3000, 1300), size = 400×200 → window is fully in secondary's X range
        // but extends past primary's bottom (1080) AND past secondary's bottom (1440).
        // Bottom-right corner (3399, 1499) lies outside both work areas → AllCornersCovered
        // is false. Loop picks secondary (only overlap), then clamps Y from 1300 to 1240
        // (= 1440 - 200). Confirms best-overlap-on-secondary + post-clamp inside it.
        var desired = new PointInt32(3000, 1300);
        var workAreas = new[] { PrimaryWorkArea, SecondaryRightWorkArea };

        var ok = WindowPositionHelper.TryClampToVisibleWorkArea(
            desired, MiniSize, workAreas, out var clamped);

        ok.Should().BeTrue();
        clamped.X.Should().Be(3000);
        clamped.Y.Should().Be(1240);
    }
}
