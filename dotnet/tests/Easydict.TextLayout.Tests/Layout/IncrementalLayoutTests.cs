using Easydict.TextLayout.Layout;
using Easydict.TextLayout.Preparation;
using Easydict.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Easydict.TextLayout.Tests.Layout;

public class IncrementalLayoutTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;
    private readonly FixedWidthMeasurer _measurer = new();

    [Fact]
    public void LayoutNextLine_ProducesAllLines()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello world test" }, _measurer);
        var lines = new List<LayoutLine>();
        var cursor = LayoutCursor.Start;

        while (cursor.SegmentIndex < prepared.Count)
        {
            var line = _engine.LayoutNextLine(prepared, cursor, 35);
            if (line is null) break;
            lines.Add(line);
            cursor = new LayoutCursor(line.EndSegment, line.EndGrapheme);
        }

        lines.Should().HaveCount(3);
        lines[0].Text.Should().Be("Hello");
        lines[1].Text.Should().Be("world");
        lines[2].Text.Should().Be("test");
    }

    [Fact]
    public void LayoutNextLine_MatchesLayoutWithLines()
    {
        var text = "Hello world this is a longer test for incremental layout";
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);

        var fullResult = _engine.LayoutWithLines(prepared, 60);

        var incrementalLines = new List<LayoutLine>();
        var cursor = LayoutCursor.Start;
        while (cursor.SegmentIndex < prepared.Count)
        {
            var line = _engine.LayoutNextLine(prepared, cursor, 60);
            if (line is null) break;
            incrementalLines.Add(line);
            cursor = new LayoutCursor(line.EndSegment, line.EndGrapheme);
        }

        incrementalLines.Should().HaveCount(fullResult.Lines.Count);
        for (var i = 0; i < incrementalLines.Count; i++)
        {
            incrementalLines[i].Text.Should().Be(fullResult.Lines[i].Text);
        }
    }

    [Fact]
    public void LayoutNextLine_PastEnd_ReturnsNull()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hi" }, _measurer);
        var line = _engine.LayoutNextLine(prepared, new LayoutCursor(prepared.Count, 0), 100);
        line.Should().BeNull();
    }

    [Fact]
    public void LayoutNextLine_VariableWidth_PerLine()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello world" }, _measurer);

        // First line narrow (35pt), second line wide (100pt)
        var line1 = _engine.LayoutNextLine(prepared, LayoutCursor.Start, 35);
        line1.Should().NotBeNull();
        line1!.Text.Should().Be("Hello");

        var line2 = _engine.LayoutNextLine(prepared, new LayoutCursor(line1.EndSegment, line1.EndGrapheme), 100);
        line2.Should().NotBeNull();
        line2!.Text.Should().Be("world");
    }
}
