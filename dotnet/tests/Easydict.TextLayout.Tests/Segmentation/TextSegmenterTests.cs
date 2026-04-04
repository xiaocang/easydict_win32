using Easydict.TextLayout.Segmentation;
using FluentAssertions;

namespace Easydict.TextLayout.Tests.Segmentation;

public class TextSegmenterTests
{
    [Fact]
    public void Segment_EmptyString_ReturnsEmpty()
    {
        var (segments, kinds) = TextSegmenter.Segment("");
        segments.Should().BeEmpty();
        kinds.Should().BeEmpty();
    }

    [Fact]
    public void Segment_NullString_ReturnsEmpty()
    {
        var (segments, kinds) = TextSegmenter.Segment(null!);
        segments.Should().BeEmpty();
        kinds.Should().BeEmpty();
    }

    [Fact]
    public void Segment_LatinWord_ReturnsSingleWordSegment()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello");
        segments.Should().Equal("Hello");
        kinds.Should().Equal(SegmentKind.Word);
    }

    [Fact]
    public void Segment_LatinSentence_SplitsOnSpaces()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello world");
        segments.Should().Equal("Hello", " ", "world");
        kinds.Should().Equal(SegmentKind.Word, SegmentKind.Space, SegmentKind.Word);
    }

    [Fact]
    public void Segment_CjkCharacters_SplitsPerCharacter()
    {
        var (segments, kinds) = TextSegmenter.Segment("你好世界");
        segments.Should().Equal("你", "好", "世", "界");
        kinds.Should().AllBe(SegmentKind.CjkGrapheme);
    }

    [Fact]
    public void Segment_MixedCjkLatin_SplitsCorrectly()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello你好world");
        segments.Should().Equal("Hello", "你", "好", "world");
        kinds.Should().Equal(SegmentKind.Word, SegmentKind.CjkGrapheme, SegmentKind.CjkGrapheme, SegmentKind.Word);
    }

    [Fact]
    public void Segment_HardBreak_CreatesSeparateSegment()
    {
        var (segments, kinds) = TextSegmenter.Segment("line1\nline2");
        segments.Should().Equal("line1", "\n", "line2");
        kinds.Should().Equal(SegmentKind.Word, SegmentKind.HardBreak, SegmentKind.Word);
    }

    [Fact]
    public void Segment_ConsecutiveSpaces_CollapsedWithNormalization()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello   world");
        segments.Should().Equal("Hello", " ", "world");
        kinds.Should().Equal(SegmentKind.Word, SegmentKind.Space, SegmentKind.Word);
    }

    [Fact]
    public void Segment_ConsecutiveSpaces_PreservedWithoutNormalization()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello   world", normalizeWhitespace: false);
        segments.Should().Equal("Hello", "   ", "world");
        kinds.Should().Equal(SegmentKind.Word, SegmentKind.Space, SegmentKind.Word);
    }

    [Fact]
    public void Segment_ClosingPunctuation_AttachesToPrecedingWord()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello.");
        segments.Should().Equal("Hello.");
        kinds.Should().Equal(SegmentKind.Word);
    }

    [Fact]
    public void Segment_ClosingPunctuationAfterSpace_StandAlone()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello .");
        segments.Should().Equal("Hello", " ", ".");
        kinds.Should().Equal(SegmentKind.Word, SegmentKind.Space, SegmentKind.ClosePunctuation);
    }

    [Fact]
    public void Segment_OpeningPunctuation_SeparateSegment()
    {
        var (segments, kinds) = TextSegmenter.Segment("(Hello)");
        segments.Should().Equal("(", "Hello)");
        kinds.Should().Equal(SegmentKind.OpenPunctuation, SegmentKind.Word);
    }

    [Fact]
    public void Segment_MultipleClosingPunctuation_GroupedWithWord()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello!!");
        segments.Should().Equal("Hello!!");
        kinds.Should().Equal(SegmentKind.Word);
    }

    [Fact]
    public void Segment_CjkPunctuation_RecognizedCorrectly()
    {
        // U+3001 = IDEOGRAPHIC COMMA, U+3002 = IDEOGRAPHIC FULL STOP
        var (segments, kinds) = TextSegmenter.Segment("你好\u3002");
        segments.Should().Equal("你", "好", "\u3002");
        kinds.Should().Equal(SegmentKind.CjkGrapheme, SegmentKind.CjkGrapheme, SegmentKind.ClosePunctuation);
    }

    [Fact]
    public void Segment_CjkOpenBracket_RecognizedCorrectly()
    {
        // U+300C = LEFT CORNER BRACKET
        var (segments, kinds) = TextSegmenter.Segment("\u300C你好\u300D");
        segments.Should().Equal("\u300C", "你", "好", "\u300D");
        kinds.Should().Equal(SegmentKind.OpenPunctuation, SegmentKind.CjkGrapheme, SegmentKind.CjkGrapheme, SegmentKind.ClosePunctuation);
    }

    [Fact]
    public void Segment_Hiragana_TreatedAsCjk()
    {
        // U+3053 = こ, U+3093 = ん, U+306B = に, U+3061 = ち, U+306F = は
        var (segments, kinds) = TextSegmenter.Segment("こんにちは");
        segments.Should().HaveCount(5);
        kinds.Should().AllBe(SegmentKind.CjkGrapheme);
    }

    [Fact]
    public void Segment_Katakana_TreatedAsCjk()
    {
        // U+30AB = カ, U+30BF = タ, U+30AB = カ, U+30CA = ナ
        var (segments, kinds) = TextSegmenter.Segment("カタカナ");
        segments.Should().HaveCount(4);
        kinds.Should().AllBe(SegmentKind.CjkGrapheme);
    }

    [Fact]
    public void Segment_HangulSyllables_TreatedAsCjk()
    {
        var (segments, kinds) = TextSegmenter.Segment("한국어");
        segments.Should().HaveCount(3);
        kinds.Should().AllBe(SegmentKind.CjkGrapheme);
    }

    [Fact]
    public void Segment_TabNormalized_CollapsedToSpace()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello\tworld");
        segments.Should().Equal("Hello", " ", "world");
        kinds.Should().Equal(SegmentKind.Word, SegmentKind.Space, SegmentKind.Word);
    }

    [Fact]
    public void Segment_TrailingSpaceBeforeNewline_Trimmed()
    {
        var (segments, kinds) = TextSegmenter.Segment("Hello \nworld");
        segments.Should().Equal("Hello", "\n", "world");
        kinds.Should().Equal(SegmentKind.Word, SegmentKind.HardBreak, SegmentKind.Word);
    }

    [Fact]
    public void NormalizeWhitespace_CollapsesAndTrims()
    {
        var result = TextSegmenter.NormalizeWhitespace("  Hello   world  \n  next  ");
        result.Should().Be(" Hello world\n next ");
    }
}
