using Easydict.TextLayout.Preparation;
using Easydict.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Easydict.TextLayout.Tests.Layout;

public class VariableWidthLayoutTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;
    private readonly FixedWidthMeasurer _measurer = new();

    [Fact]
    public void LayoutWithWidths_DifferentWidthPerLine_RespectedCorrectly()
    {
        // "Hello world test" with widths [35, 35, 100]
        // Line 1 (35pt): "Hello" (30)
        // Line 2 (35pt): "world" (30)
        // Line 3 (100pt): "test" (24)
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello world test" }, _measurer);
        var result = _engine.LayoutWithLines(prepared, new[] { 35.0, 35.0, 100.0 });
        result.Lines.Should().HaveCount(3);
        result.Lines[0].Text.Should().Be("Hello");
        result.Lines[1].Text.Should().Be("world");
        result.Lines[2].Text.Should().Be("test");
    }

    [Fact]
    public void LayoutWithWidths_NarrowFirstWiderSecond()
    {
        // "ab cd ef" with widths [15, 100]
        // "ab" = 12pt, "cd" = 12pt, "ef" = 12pt
        // Line 1 (15pt): "ab" (12)
        // Line 2 (100pt): "cd ef" (12+3+12=27)
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "ab cd ef" }, _measurer);
        var result = _engine.LayoutWithLines(prepared, new[] { 15.0, 100.0 });
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("ab");
        result.Lines[1].Text.Should().Be("cd ef");
    }

    [Fact]
    public void LayoutWithWidths_OverflowUsesLastWidth()
    {
        // More lines needed than widths provided — continues with last width
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "aa bb cc dd ee" }, _measurer);
        var result = _engine.LayoutWithLines(prepared, new[] { 15.0 }); // all lines use 15pt
        // Each word is 12pt, fits in 15pt
        result.Lines.Should().HaveCount(5);
    }

    [Fact]
    public void LayoutWithWidths_EmptyWidths_ReturnsNoLines()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello" }, _measurer);
        var result = _engine.LayoutWithLines(prepared, Array.Empty<double>());
        result.Lines.Should().BeEmpty();
    }

    [Fact]
    public void LayoutWithWidths_CjkWithVaryingWidths()
    {
        // "你好世界" with widths [12, 25]
        // Line 1 (12pt): "你" (10)
        // Line 2 (25pt): "好世" (20)
        // Line 3 (25pt): "界" (10) — uses last width
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "你好世界" }, _measurer);
        var result = _engine.LayoutWithLines(prepared, new[] { 12.0, 25.0 });
        result.Lines.Should().HaveCount(3);
        result.Lines[0].Text.Should().Be("你");
        result.Lines[1].Text.Should().Be("好世");
        result.Lines[2].Text.Should().Be("界");
    }
}
