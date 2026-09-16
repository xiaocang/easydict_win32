using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for OcrWordHitTester and the OcrRect geometry helpers (pure logic).
/// </summary>
public class OcrWordHitTesterTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h) =>
        new() { Text = text, BoundingRect = new OcrRect(x, y, w, h) };

    private static OcrLine Line(params OcrWord[] words) =>
        new()
        {
            Text = string.Join(' ', words.Select(w => w.Text)),
            BoundingRect = words.Select(w => w.BoundingRect).Aggregate(default(OcrRect), (a, b) => a.Union(b)),
            Words = words
        };

    private static readonly IReadOnlyList<OcrLine> SampleLines =
    [
        Line(Word("Hello", 10, 10, 50, 20), Word("world", 70, 10, 60, 20)),
        Line(Word("second", 10, 40, 70, 20)),
    ];

    [Fact]
    public void FindWordAt_PointInsideWord_ReturnsThatWord()
    {
        OcrWordHitTester.FindWordAt(SampleLines, 20, 15)!.Text.Should().Be("Hello");
        OcrWordHitTester.FindWordAt(SampleLines, 100, 29)!.Text.Should().Be("world");
        OcrWordHitTester.FindWordAt(SampleLines, 15, 50)!.Text.Should().Be("second");
    }

    [Fact]
    public void FindWordAt_PointInGapBetweenWords_ReturnsNearestWithinTolerance()
    {
        // Gap between "Hello" (ends at 60) and "world" (starts at 70)
        OcrWordHitTester.FindWordAt(SampleLines, 67, 15)!.Text.Should().Be("world");
        OcrWordHitTester.FindWordAt(SampleLines, 62, 15)!.Text.Should().Be("Hello");
    }

    [Fact]
    public void FindWordAt_PointFarFromAnyWord_ReturnsNull()
    {
        OcrWordHitTester.FindWordAt(SampleLines, 20, 80).Should().BeNull();   // below every row
        OcrWordHitTester.FindWordAt(SampleLines, 300, 15).Should().BeNull();  // far right of the row
    }

    [Fact]
    public void FindWordAt_ToleranceZero_RequiresContainment()
    {
        OcrWordHitTester.FindWordAt(SampleLines, 65, 15, maxDistance: 0).Should().BeNull();
    }

    [Fact]
    public void FindWordAt_EmptyInput_ReturnsNull()
    {
        OcrWordHitTester.FindWordAt([], 10, 10).Should().BeNull();
        OcrWordHitTester.FindWordAt([new OcrLine { Text = "no words" }], 10, 10).Should().BeNull();
    }

    [Fact]
    public void FindWordAt_OverlappingWords_PrefersSmallestArea()
    {
        var lines = new[]
        {
            Line(Word("outer", 0, 0, 100, 40), Word("inner", 10, 10, 20, 20)),
        };

        OcrWordHitTester.FindWordAt(lines, 15, 15)!.Text.Should().Be("inner");
        OcrWordHitTester.FindWordAt(lines, 80, 15)!.Text.Should().Be("outer");
    }

    [Fact]
    public void OcrRect_ContainsInflateUnionIntersects()
    {
        var rect = new OcrRect(10, 20, 30, 40);

        rect.Right().Should().Be(40);
        rect.Bottom().Should().Be(60);
        rect.Contains(10, 20).Should().BeTrue();
        rect.Contains(40, 60).Should().BeTrue();
        rect.Contains(41, 60).Should().BeFalse();

        rect.Inflate(5, 2).Should().Be(new OcrRect(5, 18, 40, 44));

        rect.Union(new OcrRect(0, 0, 5, 5)).Should().Be(new OcrRect(0, 0, 40, 60));
        rect.Union(default).Should().Be(rect);
        default(OcrRect).Union(rect).Should().Be(rect);

        rect.Intersects(new OcrRect(35, 55, 20, 20)).Should().BeTrue();
        rect.Intersects(new OcrRect(40, 20, 10, 10)).Should().BeFalse(); // touching edge only
        rect.Intersects(default).Should().BeFalse();
        default(OcrRect).IsEmpty().Should().BeTrue();
    }
}
