using Easydict.TextLayout.FontFitting;
using Easydict.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Easydict.TextLayout.Tests.FontFitting;

public class FontFitSolverTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;

    /// <summary>
    /// Creates a measurer factory where width scales linearly with font size.
    /// At fontSize=12, Latin=6pt. So at any fontSize, Latin = fontSize * 0.5.
    /// </summary>
    private static Func<double, ITextMeasurer> ScalingMeasurerFactory()
    {
        return fontSize => new FixedWidthMeasurer
        {
            LatinWidth = fontSize * 0.5,
            CjkWidth = fontSize,
            SpaceWidth = fontSize * 0.25,
        };
    }

    [Fact]
    public void Solve_TextFitsAtOriginalSize_NoShrink()
    {
        var request = new FontFitRequest
        {
            Text = "Hi",
            StartFontSize = 12,
            MaxWidth = 100,
            MaxHeight = 20,
        };

        var result = FontFitSolver.Solve(request, _engine, ScalingMeasurerFactory());
        result.WasShrunk.Should().BeFalse();
        result.ChosenFontSize.Should().Be(12);
        result.WasTruncated.Should().BeFalse();
    }

    [Fact]
    public void Solve_TextNeedsShrinking_FindsSmallerSize()
    {
        // "Hello world" at fontSize=12: "Hello"(30) + " "(3) + "world"(30) = 63pt
        // MaxWidth=40 => wraps to 2 lines: "Hello" and "world"
        // lineHeight=12*1.2=14.4, maxLines=floor(14/14.4)=0 => doesn't fit
        // Must shrink so that 2 lines fit within maxHeight=14
        var request = new FontFitRequest
        {
            Text = "Hello world",
            StartFontSize = 12,
            MaxWidth = 40,
            MaxHeight = 14,
        };

        var result = FontFitSolver.Solve(request, _engine, ScalingMeasurerFactory());
        result.WasShrunk.Should().BeTrue();
        result.ChosenFontSize.Should().BeLessThan(12);
        result.ChosenFontSize.Should().BeGreaterOrEqualTo(6); // MinFontSize default
    }

    [Fact]
    public void Solve_TextCannotFitEvenAtMinSize_FlagsTruncated()
    {
        // Very long text in tiny box
        var request = new FontFitRequest
        {
            Text = "This is a very long text that cannot possibly fit in a tiny box",
            StartFontSize = 12,
            MinFontSize = 6,
            MaxWidth = 20,
            MaxHeight = 10,
        };

        var result = FontFitSolver.Solve(request, _engine, ScalingMeasurerFactory());
        result.WasTruncated.Should().BeTrue();
        result.ChosenFontSize.Should().Be(6);
    }

    [Fact]
    public void Solve_LineRectMode_RespectLineCount()
    {
        // 3 lines available, text should fit in 3 or fewer lines
        var request = new FontFitRequest
        {
            Text = "Hello world test",
            StartFontSize = 12,
            LineWidths = [40, 40, 40],
        };

        var result = FontFitSolver.Solve(request, _engine, ScalingMeasurerFactory());
        result.LineCount.Should().BeLessOrEqualTo(3);
    }

    [Fact]
    public void Solve_LineRectMode_WithMaxHeight_DoesNotShrinkWithoutLineHeightCeilings()
    {
        var request = new FontFitRequest
        {
            Text = "Hello world",
            StartFontSize = 12,
            LineWidths = [100, 100],
            MaxLineCount = 2,
            MaxHeight = 30,
        };

        var result = FontFitSolver.Solve(request, _engine, ScalingMeasurerFactory());

        result.WasShrunk.Should().BeFalse();
        result.ChosenFontSize.Should().Be(12);
        result.WasTruncated.Should().BeFalse();
    }

    [Fact]
    public void Solve_LineRectMode_WithLineHeights_RespectsHeightConstraint()
    {
        var request = new FontFitRequest
        {
            Text = "Hello world",
            StartFontSize = 12,
            LineWidths = [100, 100],
            LineHeights = [8, 8], // Font must be <= 8 * 0.98 = 7.84
        };

        var result = FontFitSolver.Solve(request, _engine, ScalingMeasurerFactory());
        result.ChosenFontSize.Should().BeLessOrEqualTo(8);
    }

    [Fact]
    public void Solve_BinarySearch_ConvergesWithinTolerance()
    {
        var request = new FontFitRequest
        {
            Text = "Hello world this is a moderately long text",
            StartFontSize = 24,
            MinFontSize = 6,
            MaxWidth = 80,
            MaxHeight = 40,
        };

        var result = FontFitSolver.Solve(request, _engine, ScalingMeasurerFactory());
        // Binary search should converge — result should be between min and start
        result.ChosenFontSize.Should().BeInRange(6, 24);
        result.ChosenLineHeight.Should().BeApproximately(
            result.ChosenFontSize * 1.2, 0.01);
    }
}
