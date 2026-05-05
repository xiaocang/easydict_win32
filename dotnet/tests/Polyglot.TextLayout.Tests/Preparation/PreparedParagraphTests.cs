using Polyglot.TextLayout.Preparation;
using Polyglot.TextLayout.Segmentation;
using Polyglot.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Polyglot.TextLayout.Tests.Preparation;

public class PreparedParagraphTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;
    private readonly FixedWidthMeasurer _measurer = new();

    [Fact]
    public void Prepare_EmptyText_ReturnsEmptyParagraph()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "" }, _measurer);
        prepared.Count.Should().Be(0);
        prepared.IsSingleChunk.Should().BeTrue();
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
    }

    [Fact]
    public void Prepare_WordSegment_HasGraphemeWidths()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello" }, _measurer);
        prepared.GraphemeWidths[0].Should().NotBeNull();
        prepared.GraphemeWidths[0]!.Length.Should().Be(5);
        prepared.GraphemeWidths[0]!.Should().OnlyContain(w => w == 6.0);
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
        prepared.Count.Should().Be(3);
        prepared.Widths[1].Should().Be(3.0);
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
        prepared.GraphemeWidths[0].Should().BeNull();
    }

    // --- LineEndFitAdvances ---

    [Fact]
    public void Prepare_LineEndFitAdvances_SpaceIsZero()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "a b" }, _measurer);
        // segments: "a" (Word), " " (Space), "b" (Word)
        prepared.LineEndFitAdvances[0].Should().Be(6.0);  // Word: same as Width
        prepared.LineEndFitAdvances[1].Should().Be(0.0);   // Space: 0 (hangs)
        prepared.LineEndFitAdvances[2].Should().Be(6.0);  // Word: same as Width
    }

    [Fact]
    public void Prepare_LineEndFitAdvances_CjkEqualsWidth()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "你好" }, _measurer);
        prepared.LineEndFitAdvances[0].Should().Be(10.0);
        prepared.LineEndFitAdvances[1].Should().Be(10.0);
    }

    [Fact]
    public void Prepare_LineEndFitAdvances_HardBreakIsZero()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "a\nb" }, _measurer);
        var hbIdx = Array.IndexOf(prepared.Kinds, SegmentKind.HardBreak);
        prepared.LineEndFitAdvances[hbIdx].Should().Be(0);
    }

    // --- Kinsoku Flags ---

    [Fact]
    public void Prepare_Kinsoku_IdeographicPeriodProhibitedStart()
    {
        // 。(U+3002) is classified as ClosePunctuation by ScriptClassifier,
        // but it should also be flagged as prohibited line start by kinsoku
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "你\u3002" }, _measurer);
        // Find the 。 segment
        var periodIdx = Array.IndexOf(prepared.Segments, "\u3002");
        periodIdx.Should().BeGreaterOrEqualTo(0);
        prepared.IsProhibitedLineStart[periodIdx].Should().BeTrue();
    }

    [Fact]
    public void Prepare_Kinsoku_IdeographicCommaProhibitedStart()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "你\u3001" }, _measurer);
        var commaIdx = Array.IndexOf(prepared.Segments, "\u3001");
        commaIdx.Should().BeGreaterOrEqualTo(0);
        prepared.IsProhibitedLineStart[commaIdx].Should().BeTrue();
    }

    [Fact]
    public void Prepare_Kinsoku_SmallKanaProhibitedStart()
    {
        // ぁ(U+3041) — small hiragana A, must not start a line
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "あぁ" }, _measurer);
        // "あ" and "ぁ" as separate CjkGrapheme segments
        prepared.IsProhibitedLineStart[1].Should().BeTrue();  // ぁ
        prepared.IsProhibitedLineStart[0].Should().BeFalse(); // あ (normal)
    }

    [Fact]
    public void Prepare_Kinsoku_ProlongedSoundMarkProhibitedStart()
    {
        // ー(U+30FC) — prolonged sound mark, must not start a line
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "カー" }, _measurer);
        prepared.IsProhibitedLineStart[1].Should().BeTrue(); // ー
    }

    [Fact]
    public void Prepare_Kinsoku_OpenBracketProhibitedEnd()
    {
        // 「(U+300C) — left corner bracket, must not end a line
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "\u300C你" }, _measurer);
        prepared.IsProhibitedLineEnd[0].Should().BeTrue(); // 「
    }

    [Fact]
    public void Prepare_Kinsoku_NormalCjkNotProhibited()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "漢字" }, _measurer);
        prepared.IsProhibitedLineStart[0].Should().BeFalse();
        prepared.IsProhibitedLineStart[1].Should().BeFalse();
        prepared.IsProhibitedLineEnd[0].Should().BeFalse();
        prepared.IsProhibitedLineEnd[1].Should().BeFalse();
    }

    // --- Soft-Hyphen ---

    [Fact]
    public void Prepare_SoftHyphen_ZeroWidthWithHyphenFitAdvance()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "hel\u00ADlo" }, _measurer);
        // segments: "hel" (Word), "\u00AD" (SoftHyphen), "lo" (Word)
        prepared.Count.Should().Be(3);
        prepared.Kinds[1].Should().Be(SegmentKind.SoftHyphen);
        prepared.Widths[1].Should().Be(0); // invisible normally
        prepared.LineEndFitAdvances[1].Should().Be(6.0); // "-" width = 6pt (Latin)
        prepared.DiscretionaryHyphenWidth.Should().Be(6.0);
    }

    [Fact]
    public void Prepare_NoSoftHyphen_ZeroDiscretionaryWidth()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello" }, _measurer);
        prepared.DiscretionaryHyphenWidth.Should().Be(0);
    }

    // --- Hard-Break Chunks ---

    [Fact]
    public void Prepare_NoHardBreaks_IsSingleChunk()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "Hello world" }, _measurer);
        prepared.IsSingleChunk.Should().BeTrue();
        prepared.HardBreakIndices.Should().BeEmpty();
    }

    [Fact]
    public void Prepare_OneHardBreak_HasOneChunkBoundary()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "a\nb" }, _measurer);
        prepared.IsSingleChunk.Should().BeFalse();
        prepared.HardBreakIndices.Should().HaveCount(1);
    }

    [Fact]
    public void Prepare_TwoHardBreaks_HasTwoChunkBoundaries()
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = "a\nb\nc" }, _measurer);
        prepared.HardBreakIndices.Should().HaveCount(2);
    }
}
