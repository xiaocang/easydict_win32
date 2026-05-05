using Polyglot.TextLayout.Preparation;
using Polyglot.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Polyglot.TextLayout.Tests.Layout;

public class LongSegmentBreakingTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;
    private readonly FixedWidthMeasurer _measurer = new();

    [Fact]
    public void Layout_LongWordExceedingWidth_BreaksAtGrapheme()
    {
        // "Supercalifragilistic" = 20 chars * 6pt = 120pt, maxWidth = 40pt
        var result = _engine.LayoutWithLines(
            _engine.Prepare(new TextPrepareRequest { Text = "Supercalifragilistic" }, _measurer),
            40);
        result.Lines.Should().HaveCountGreaterThan(1);
        // First line should have ~6 chars (6*6=36 fits in 40)
        result.Lines[0].Text.Length.Should().BeLessOrEqualTo(7);
    }

    [Fact]
    public void Layout_LongWordThenNormalWord_ContinuesCorrectly()
    {
        var result = _engine.LayoutWithLines(
            _engine.Prepare(new TextPrepareRequest { Text = "Supercalifragilistic ok" }, _measurer),
            40);
        // Should have multiple lines, last line should contain "ok"
        var lastLine = result.Lines[^1].Text;
        lastLine.Should().Contain("ok");
    }

    [Fact]
    public void Layout_UrlLikeString_BreaksAtGrapheme()
    {
        var url = "https://example.com/very/long/path/to/resource";
        var result = _engine.LayoutWithLines(
            _engine.Prepare(new TextPrepareRequest { Text = url }, _measurer),
            60);
        result.Lines.Should().HaveCountGreaterThan(1);
        // All text should be preserved
        string.Join("", result.Lines.Select(l => l.Text)).Should().Be(url);
    }

    [Fact]
    public void Layout_SingleCharExceedingWidth_StillEmitted()
    {
        // Even if a single character exceeds maxWidth, it must be emitted
        var measurer = new FixedWidthMeasurer { CjkWidth = 100 };
        var result = _engine.LayoutWithLines(
            _engine.Prepare(new TextPrepareRequest { Text = "你" }, measurer),
            50);
        result.Lines.Should().HaveCount(1);
        result.Lines[0].Text.Should().Be("你");
    }
}
