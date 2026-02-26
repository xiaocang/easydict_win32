using Easydict.TranslationService.LongDocument;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfSharpCore.Drawing;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class PdfExportServiceLayoutTests
{
    [Fact]
    public void TryBuildLineRects_WithDistinctBaselines_BuildsRectsTopToBottomAndClampsToBlock()
    {
        var style = new BlockTextStyle
        {
            LineSpacing = 20,
            LinePositions =
            [
                new BlockLinePosition(700, 90, 300),
                new BlockLinePosition(680, 80, 280),  // will be clamped on left
                new BlockLinePosition(660, 100, 320), // will be clamped on right
            ]
        };

        var pageHeight = 800d;
        var blockRect = new XRect(90, 50, 200, 120); // X=[90,290], Y=[50,170]

        var rects = PdfExportService.TryBuildLineRects(pageHeight, blockRect, style, fallbackLineHeight: 14);

        rects.Should().NotBeNull();
        rects!.Count.Should().Be(3);

        // Should be in visual order (top to bottom): y increases downward in PdfSharp.
        rects[0].Y.Should().BeLessThan(rects[1].Y);
        rects[1].Y.Should().BeLessThan(rects[2].Y);

        // All should stay within the block rect.
        foreach (var r in rects)
        {
            r.X.Should().BeGreaterOrEqualTo(blockRect.X);
            r.Right.Should().BeLessOrEqualTo(blockRect.Right + 0.0001);
            r.Y.Should().BeGreaterOrEqualTo(blockRect.Y);
            r.Bottom.Should().BeLessOrEqualTo(blockRect.Bottom + 0.0001);
            r.Width.Should().BeGreaterThan(0);
            r.Height.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void TryBuildLineRects_WithDuplicateBaselines_ReturnsNullToAvoidGridLayouts()
    {
        var style = new BlockTextStyle
        {
            LinePositions =
            [
                new BlockLinePosition(700.0, 100, 200),
                new BlockLinePosition(700.2, 220, 320),
            ]
        };

        var rects = PdfExportService.TryBuildLineRects(800, new XRect(0, 0, 400, 400), style, fallbackLineHeight: 14);
        rects.Should().BeNull();
    }
}
