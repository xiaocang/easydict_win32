using Polyglot.TextLayout.Layout;
using Polyglot.TextLayout.Preparation;
using Polyglot.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Polyglot.TextLayout.Tests.Layout;

public class FixedWidthLayoutTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;
    private readonly FixedWidthMeasurer _measurer = new();

    private LayoutLinesResult LayoutLines(string text, double maxWidth)
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        return _engine.LayoutWithLines(prepared, maxWidth);
    }

    private LayoutResult LayoutCount(string text, double maxWidth)
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        return _engine.Layout(prepared, maxWidth);
    }

    [Fact]
    public void Layout_EmptyText_ReturnsNoLines()
    {
        var result = LayoutLines("", 100);
        result.Lines.Should().BeEmpty();
    }

    [Fact]
    public void Layout_SingleWordFits_OneLine()
    {
        // "Hello" = 5*6 = 30pt
        var result = LayoutLines("Hello", 50);
        result.Lines.Should().HaveCount(1);
        result.Lines[0].Text.Should().Be("Hello");
        result.Lines[0].Width.Should().Be(30);
    }

    [Fact]
    public void Layout_TwoWordsFit_OneLine()
    {
        // "Hello world" = "Hello"(30) + " "(3) + "world"(30) = 63
        var result = LayoutLines("Hello world", 70);
        result.Lines.Should().HaveCount(1);
        result.Lines[0].Text.Should().Be("Hello world");
    }

    [Fact]
    public void Layout_TwoWordsOverflow_TwoLines()
    {
        // "Hello world" = "Hello"(30) + " "(3) + "world"(30) = 63
        var result = LayoutLines("Hello world", 40);
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("Hello");
        result.Lines[1].Text.Should().Be("world");
    }

    [Fact]
    public void Layout_LeadingSpaceTrimmed()
    {
        // After wrapping, leading spaces on wrapped lines should be trimmed
        var result = LayoutLines("Hello world", 35);
        result.Lines.Should().HaveCount(2);
        result.Lines[1].Text.Should().Be("world");
        result.Lines[1].Text.Should().NotStartWith(" ");
    }

    [Fact]
    public void Layout_TrailingSpaceTrimmed()
    {
        var result = LayoutLines("Hello ", 50);
        result.Lines.Should().HaveCount(1);
        result.Lines[0].Text.Should().Be("Hello");
    }

    [Fact]
    public void Layout_CjkCharacters_BreaksAnywhere()
    {
        // "你好世界" = 4 * 10 = 40pt
        var result = LayoutLines("你好世界", 25);
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("你好");
        result.Lines[1].Text.Should().Be("世界");
    }

    [Fact]
    public void Layout_CjkSingleCharPerLine_NarrowWidth()
    {
        // Each CJK char = 10pt, maxWidth = 12pt
        var result = LayoutLines("你好世界", 12);
        result.Lines.Should().HaveCount(4);
    }

    [Fact]
    public void Layout_HardBreak_ForcesNewLine()
    {
        var result = LayoutLines("Hello\nworld", 100);
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("Hello");
        result.Lines[1].Text.Should().Be("world");
    }

    [Fact]
    public void Layout_MultipleHardBreaks_PreserveEmptyLines()
    {
        var result = LayoutLines("Hello\n\nworld", 100);
        result.Lines.Should().HaveCount(3);
        result.Lines[0].Text.Should().Be("Hello");
        result.Lines[1].Text.Should().Be("");
        result.Lines[2].Text.Should().Be("world");
    }

    [Fact]
    public void Layout_MixedCjkLatin_WrapsCorrectly()
    {
        // "Hello你好" = "Hello"(30) + "你"(10) + "好"(10) = 50
        var result = LayoutLines("Hello你好", 35);
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("Hello");
        result.Lines[1].Text.Should().Be("你好");
    }

    [Fact]
    public void Layout_CountOnly_MatchesFullLayout()
    {
        var text = "Hello world this is a test";
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        var full = _engine.LayoutWithLines(prepared, 40);
        var count = _engine.Layout(prepared, 40);
        count.LineCount.Should().Be(full.Lines.Count);
    }

    [Fact]
    public void Layout_ClosingPunctuation_AttachedToWord()
    {
        // "Hello." = 30+6 = 36pt (word with trailing period)
        var result = LayoutLines("Hello.", 50);
        result.Lines.Should().HaveCount(1);
        result.Lines[0].Text.Should().Be("Hello.");
    }

    [Fact]
    public void Layout_WalkLineRanges_CountMatchesLayout()
    {
        var text = "Hello world this is a test of line breaking";
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        var lines = _engine.LayoutWithLines(prepared, 60);
        var walkCount = _engine.WalkLineRanges(prepared, 60, _ => { });
        walkCount.Should().Be(lines.Lines.Count);
    }

    [Fact]
    public void Layout_MaxLineWidth_TrackedCorrectly()
    {
        // "Hello world" wraps to "Hello" (30) and "world" (30)
        var result = LayoutLines("Hello world", 35);
        result.MaxLineWidth.Should().Be(30);
    }
}
