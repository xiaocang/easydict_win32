using Easydict.WinUI.Models;
using Easydict.WinUI.Services.ScreenCapture;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the pure capture-rectangle math of ScreenRegionCapture (no GDI calls).
/// </summary>
public class ScreenRegionCaptureTests
{
    private static readonly OcrRect FullHd = new(0, 0, 1920, 1080);

    [Fact]
    public void ComputeCaptureRect_CentersBoxOnCursor_At100Percent()
    {
        var rect = ScreenRegionCapture.ComputeCaptureRect(500, 400, 1.0, FullHd, exclude: null);

        rect.Should().Be(new OcrRect(
            500 - ScreenRegionCapture.HalfWidthDips,
            400 - ScreenRegionCapture.HalfHeightDips,
            2 * ScreenRegionCapture.HalfWidthDips,
            2 * ScreenRegionCapture.HalfHeightDips));
    }

    [Fact]
    public void ComputeCaptureRect_ScalesWithDpi()
    {
        var rect = ScreenRegionCapture.ComputeCaptureRect(500, 400, 2.0, FullHd, exclude: null);

        rect.Width.Should().Be(4 * ScreenRegionCapture.HalfWidthDips);
        rect.Height.Should().Be(4 * ScreenRegionCapture.HalfHeightDips);
        rect.X.Should().Be(500 - 2 * ScreenRegionCapture.HalfWidthDips);
    }

    [Fact]
    public void ComputeCaptureRect_ClampsToVirtualScreen()
    {
        var rect = ScreenRegionCapture.ComputeCaptureRect(10, 5, 1.0, FullHd, exclude: null);

        rect.X.Should().Be(0);
        rect.Y.Should().Be(0);
        rect.Width.Should().Be(10 + ScreenRegionCapture.HalfWidthDips);
        rect.Height.Should().Be(5 + ScreenRegionCapture.HalfHeightDips);
        rect.Contains(10, 5).Should().BeTrue();
    }

    [Fact]
    public void ComputeCaptureRect_HonorsNegativeVirtualScreenOrigin()
    {
        var virtualScreen = new OcrRect(-1920, 0, 3840, 1080);
        var rect = ScreenRegionCapture.ComputeCaptureRect(-1900, 400, 1.0, virtualScreen, exclude: null);

        rect.X.Should().Be(-1920);
        rect.Contains(-1900, 400).Should().BeTrue();
    }

    [Fact]
    public void ComputeCaptureRect_CutsOffPopupBelowCursor()
    {
        var popup = new OcrRect(480, 420, 300, 100);
        var rect = ScreenRegionCapture.ComputeCaptureRect(500, 400, 1.0, FullHd, popup);

        rect.Bottom().Should().Be(420);
        rect.Intersects(popup).Should().BeFalse();
        rect.Contains(500, 400).Should().BeTrue();
    }

    [Fact]
    public void ComputeCaptureRect_CutsOffPopupAboveCursor()
    {
        var popup = new OcrRect(480, 300, 300, 80); // bottom edge at 380, above the cursor
        var rect = ScreenRegionCapture.ComputeCaptureRect(500, 400, 1.0, FullHd, popup);

        rect.Y.Should().Be(380);
        rect.Intersects(popup).Should().BeFalse();
        rect.Contains(500, 400).Should().BeTrue();
    }

    [Fact]
    public void ComputeCaptureRect_IgnoresPopupThatDoesNotOverlap()
    {
        var popup = new OcrRect(1000, 900, 300, 100);
        var rect = ScreenRegionCapture.ComputeCaptureRect(500, 400, 1.0, FullHd, popup);

        rect.Should().Be(ScreenRegionCapture.ComputeCaptureRect(500, 400, 1.0, FullHd, exclude: null));
    }

    [Fact]
    public void ComputeCaptureRect_EnforcesMinimumSize_ForTinyScale()
    {
        var rect = ScreenRegionCapture.ComputeCaptureRect(500, 400, 0.05, FullHd, exclude: null);

        rect.Width.Should().Be(ScreenRegionCapture.MinCaptureSize);
        rect.Height.Should().Be(ScreenRegionCapture.MinCaptureSize);
    }

    [Fact]
    public void ComputeCaptureRect_InvalidScale_FallsBackTo100Percent()
    {
        var expected = ScreenRegionCapture.ComputeCaptureRect(500, 400, 1.0, FullHd, exclude: null);

        ScreenRegionCapture.ComputeCaptureRect(500, 400, 0, FullHd, exclude: null).Should().Be(expected);
        ScreenRegionCapture.ComputeCaptureRect(500, 400, double.NaN, FullHd, exclude: null).Should().Be(expected);
    }
}
