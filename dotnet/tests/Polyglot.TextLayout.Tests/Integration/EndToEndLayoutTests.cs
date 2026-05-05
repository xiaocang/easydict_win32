using Polyglot.TextLayout.FontFitting;
using Polyglot.TextLayout.Layout;
using Polyglot.TextLayout.Preparation;
using Polyglot.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Polyglot.TextLayout.Tests.Integration;

public class EndToEndLayoutTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;
    private readonly FixedWidthMeasurer _measurer = new();

    [Fact]
    public void EndToEnd_CjkParagraph_WrapsCorrectly()
    {
        // Simulate a typical CJK paragraph in a PDF block
        var text = "这是一个测试段落用于验证中文文本的自动换行功能";
        // 22 CJK chars * 10pt = 220pt total
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        var result = _engine.LayoutWithLines(prepared, 80); // 8 chars per line
        result.Lines.Should().HaveCount(3); // 8 + 8 + 6
        result.Lines[0].Text.Should().HaveLength(8);
    }

    [Fact]
    public void EndToEnd_EnglishParagraph_WrapsOnWordBoundaries()
    {
        var text = "The quick brown fox jumps over the lazy dog";
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        var result = _engine.LayoutWithLines(prepared, 60);
        // No word should be split
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith(" ");
            line.Text.Should().NotEndWith(" ");
        }
    }

    [Fact]
    public void EndToEnd_MixedCjkEnglish_MaintainsReadability()
    {
        var text = "Hello你好World世界Test测试";
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        var result = _engine.LayoutWithLines(prepared, 50);
        // All text preserved
        string.Join("", result.Lines.Select(l => l.Text)).Should().Be(text);
    }

    [Fact]
    public void EndToEnd_IncrementalLayout_ReconstructsFullText()
    {
        var text = "Line one is here\nLine two follows\nAnd three";
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);

        var lines = new List<string>();
        var cursor = LayoutCursor.Start;
        while (cursor.SegmentIndex < prepared.Count)
        {
            var line = _engine.LayoutNextLine(prepared, cursor, 100);
            if (line is null) break;
            lines.Add(line.Text);
            cursor = new LayoutCursor(line.EndSegment, line.EndGrapheme);
        }

        lines.Should().HaveCount(3);
        lines[0].Should().Be("Line one is here");
        lines[1].Should().Be("Line two follows");
        lines[2].Should().Be("And three");
    }

    [Fact]
    public void EndToEnd_FontFitThenLayout_ProducesConsistentResult()
    {
        Func<double, ITextMeasurer> factory = fontSize => new FixedWidthMeasurer
        {
            LatinWidth = fontSize * 0.5,
            CjkWidth = fontSize,
            SpaceWidth = fontSize * 0.25,
        };

        var fitRequest = new FontFitRequest
        {
            Text = "Hello world this is a test",
            StartFontSize = 14,
            MaxWidth = 60,
            MaxHeight = 40,
        };

        var fitResult = FontFitSolver.Solve(fitRequest, _engine, factory);

        // Now lay out with the chosen font size
        var measurer = factory(fitResult.ChosenFontSize);
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = fitRequest.Text }, measurer);
        var layoutResult = _engine.LayoutWithLines(prepared, fitRequest.MaxWidth.Value);

        // Line count should match what FontFitSolver determined
        layoutResult.Lines.Count.Should().Be(fitResult.LineCount);

        // Should fit in the height constraint
        var totalHeight = layoutResult.Lines.Count * fitResult.ChosenLineHeight;
        if (!fitResult.WasTruncated)
        {
            totalHeight.Should().BeLessOrEqualTo(fitRequest.MaxHeight!.Value + 0.5);
        }
    }

    [Fact]
    public void EndToEnd_VariableWidthWithLineRects_SimulatesPdfLinePositions()
    {
        // Simulate a PDF block where each line has a different width
        // (from BlockLinePosition.Left/Right producing different widths)
        var text = "This is a paragraph that should be laid out with variable line widths";
        var widths = new double[] { 80, 100, 90, 70, 100 };
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        var result = _engine.LayoutWithLines(prepared, widths);

        // Each line should respect its width
        for (var i = 0; i < result.Lines.Count && i < widths.Length; i++)
        {
            result.Lines[i].Width.Should().BeLessOrEqualTo(widths[i] + 0.01,
                $"line {i} should fit within width {widths[i]}");
        }
    }
}
