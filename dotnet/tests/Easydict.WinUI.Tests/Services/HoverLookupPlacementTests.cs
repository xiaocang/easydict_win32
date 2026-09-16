using Easydict.WinUI.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the pure popup placement math used by hover word lookup.
/// </summary>
public class HoverLookupPlacementTests
{
    private static readonly OcrRect WorkArea = new(0, 0, 1920, 1080);
    private const int Width = 300;
    private const int Height = 100;

    [Fact]
    public void Compute_PlacesBelowWord_WhenItFits()
    {
        var anchor = new OcrRect(100, 100, 60, 20);

        var (x, y) = HoverLookupPlacement.Compute(anchor, Width, Height, WorkArea);

        x.Should().Be(100);
        y.Should().Be(120 + HoverLookupPlacement.GapPx);
    }

    [Fact]
    public void Compute_FlipsAboveWord_NearBottomEdge()
    {
        var anchor = new OcrRect(100, 1000, 60, 20);

        var (x, y) = HoverLookupPlacement.Compute(anchor, Width, Height, WorkArea);

        x.Should().Be(100);
        y.Should().Be(1000 - HoverLookupPlacement.GapPx - Height);
    }

    [Fact]
    public void Compute_ClampsHorizontally_AtRightAndLeftEdges()
    {
        var (rightX, _) = HoverLookupPlacement.Compute(new OcrRect(1800, 100, 60, 20), Width, Height, WorkArea);
        rightX.Should().Be(1920 - Width);

        var (leftX, _) = HoverLookupPlacement.Compute(new OcrRect(-50, 100, 60, 20), Width, Height, WorkArea);
        leftX.Should().Be(0);
    }

    [Fact]
    public void Compute_WhenNeitherBelowNorAboveFits_ClampsIntoWorkArea()
    {
        var shortWorkArea = new OcrRect(0, 0, 1920, 120);
        var anchor = new OcrRect(100, 50, 60, 20);

        var (_, y) = HoverLookupPlacement.Compute(anchor, Width, Height, shortWorkArea);

        y.Should().Be(20); // work bottom (120) - height (100)
        y.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Compute_HonorsWorkAreaOrigin_OnSecondaryMonitor()
    {
        var secondary = new OcrRect(-1920, 0, 1920, 1040);
        var anchor = new OcrRect(-1910, 100, 60, 20);

        var (x, y) = HoverLookupPlacement.Compute(anchor, Width, Height, secondary);

        x.Should().Be(-1910);
        y.Should().Be(120 + HoverLookupPlacement.GapPx);
    }

    [Fact]
    public void Compute_EmptyWorkArea_DoesNotClamp()
    {
        var anchor = new OcrRect(1800, 1000, 60, 20);

        var (x, y) = HoverLookupPlacement.Compute(anchor, Width, Height, default);

        x.Should().Be(1800);
        y.Should().Be(1020 + HoverLookupPlacement.GapPx);
    }

    [Fact]
    public void Compute_CustomGap_IsApplied()
    {
        var anchor = new OcrRect(100, 100, 60, 20);

        var (_, y) = HoverLookupPlacement.Compute(anchor, Width, Height, WorkArea, gap: 16);

        y.Should().Be(136);
    }

    [Fact]
    public void ComputeSafeZone_UnionsWordAndPopup_ThenInflates()
    {
        var word = new OcrRect(100, 100, 60, 20);
        var popup = new OcrRect(100, 128, 300, 100);

        var zone = HoverLookupPlacement.ComputeSafeZone(word, popup, 12);

        zone.Should().Be(new OcrRect(88, 88, 324, 152));
        zone.Contains(130, 124).Should().BeTrue();  // the gap between word and popup stays inside
    }

    [Fact]
    public void ComputeSafeZone_WithoutPopup_InflatesWordOnly()
    {
        var word = new OcrRect(100, 100, 60, 20);

        HoverLookupPlacement.ComputeSafeZone(word, null, 12).Should().Be(new OcrRect(88, 88, 84, 44));
        HoverLookupPlacement.ComputeSafeZone(word, default(OcrRect), 12).Should().Be(new OcrRect(88, 88, 84, 44));
    }
}
