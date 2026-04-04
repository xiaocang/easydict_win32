using Easydict.TextLayout.Preparation;
using Easydict.TextLayout.Segmentation;
using Easydict.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Easydict.TextLayout.Tests.Preparation;

public class PreparedParagraphTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;
    private readonly FixedWidthMeasurer _measurer = new();

    [Fact]
    public void Prepare_EmptyText_ReturnsEmptyParagraph()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "" }, _measurer);
        prepared.Count.Should().Be(0);
        prepared.TotalWidth.Should().Be(0);
    }

    [Fact]
    public void Prepare_LatinWord_MeasuresCorrectly()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello" }, _measurer);
        prepared.Count.Should().Be(1);
        prepared.Segments[0].Should().Be("Hello");
        prepared.Kinds[0].Should().Be(SegmentKind.Word);
        prepared.Widths[0].Should().Be(5 * 6.0); // 5 Latin chars * 6pt
    }

    [Fact]
    public void Prepare_CjkText_MeasuresPerGrapheme()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "你好" }, _measurer);
        prepared.Count.Should().Be(2);
        prepared.Widths[0].Should().Be(10.0);
        prepared.Widths[1].Should().Be(10.0);
        prepared.TotalWidth.Should().Be(20.0);
    }

    [Fact]
    public void Prepare_MixedText_TotalWidthCorrect()
    {
        // "Hi你好" => "Hi" (12) + "你" (10) + "好" (10) = 32
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hi你好" }, _measurer);
        prepared.TotalWidth.Should().Be(32.0);
    }

    [Fact]
    public void Prepare_WordSegment_HasGraphemeWidths()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello" }, _measurer);
        prepared.GraphemeWidths[0].Should().NotBeNull();
        prepared.GraphemeWidths[0]!.Length.Should().Be(5);
        prepared.GraphemeWidths[0]!.Should().AllBe(6.0);
    }

    [Fact]
    public void Prepare_WordSegment_PrefixSumsAreMonotonic()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello" }, _measurer);
        var sums = prepared.GraphemePrefixSums[0]!;
        sums.Should().NotBeNull();
        sums.Length.Should().Be(5);
        for (var i = 1; i < sums.Length; i++)
            sums[i].Should().BeGreaterThan(sums[i - 1]);
    }

    [Fact]
    public void Prepare_SpaceSegment_MeasuredCorrectly()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "a b" }, _measurer);
        // "a" (Word), " " (Space), "b" (Word)
        prepared.Count.Should().Be(3);
        prepared.Widths[1].Should().Be(3.0); // space width
    }

    [Fact]
    public void Prepare_HardBreak_HasZeroWidth()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "a\nb" }, _measurer);
        var hbIdx = Array.IndexOf(prepared.Kinds, SegmentKind.HardBreak);
        hbIdx.Should().BeGreaterOrEqualTo(0);
        prepared.Widths[hbIdx].Should().Be(0);
    }

    [Fact]
    public void Prepare_SingleCharWord_NoGraphemeWidths()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "a" }, _measurer);
        // Single-char words don't need grapheme-level breaking
        prepared.GraphemeWidths[0].Should().BeNull();
    }
}
