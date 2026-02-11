using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for OcrTextMerger — pure logic for merging OCR-recognized text lines.
/// No Win32/WinRT dependencies, runs on any platform.
/// </summary>
[Trait("Category", "WinUI")]
public class OcrTextMergerTests
{
    #region MergeWords

    [Fact]
    public void MergeWords_EmptyList_ReturnsEmpty()
    {
        OcrTextMerger.MergeWords([]).Should().BeEmpty();
    }

    [Fact]
    public void MergeWords_SingleWord_ReturnsThatWord()
    {
        OcrTextMerger.MergeWords(["Hello"]).Should().Be("Hello");
    }

    [Fact]
    public void MergeWords_LatinWords_JoinedWithSpaces()
    {
        var words = new[] { "Hello", "World", "Test" };
        OcrTextMerger.MergeWords(words).Should().Be("Hello World Test");
    }

    [Fact]
    public void MergeWords_CjkCharacters_NoSpacesBetween()
    {
        var words = new[] { "你", "好", "世", "界" };
        OcrTextMerger.MergeWords(words).Should().Be("你好世界");
    }

    [Fact]
    public void MergeWords_CjkWords_NoSpacesBetween()
    {
        var words = new[] { "你好", "世界" };
        OcrTextMerger.MergeWords(words).Should().Be("你好世界");
    }

    [Fact]
    public void MergeWords_MixedCjkAndLatin_SpaceBetweenLatinAndCjk()
    {
        // Latin followed by CJK → space
        var words = new[] { "Hello", "你好" };
        OcrTextMerger.MergeWords(words).Should().Be("Hello 你好");
    }

    [Fact]
    public void MergeWords_CjkFollowedByLatin_SpaceBetween()
    {
        // CJK followed by Latin → space
        var words = new[] { "你好", "World" };
        OcrTextMerger.MergeWords(words).Should().Be("你好 World");
    }

    [Fact]
    public void MergeWords_JapaneseHiragana_NoSpaces()
    {
        var words = new[] { "こん", "にち", "は" };
        OcrTextMerger.MergeWords(words).Should().Be("こんにちは");
    }

    [Fact]
    public void MergeWords_JapaneseKatakana_NoSpaces()
    {
        var words = new[] { "カタ", "カナ" };
        OcrTextMerger.MergeWords(words).Should().Be("カタカナ");
    }

    [Fact]
    public void MergeWords_KoreanHangul_NoSpaces()
    {
        var words = new[] { "안녕", "하세요" };
        OcrTextMerger.MergeWords(words).Should().Be("안녕하세요");
    }

    [Fact]
    public void MergeWords_EmptyWordInMiddle_HandledGracefully()
    {
        var words = new[] { "Hello", "", "World" };
        OcrTextMerger.MergeWords(words).Should().Be("HelloWorld");
    }

    [Fact]
    public void MergeWords_FullwidthForms_TreatedAsCjk()
    {
        // Fullwidth parentheses (FF08, FF09) are in the Fullwidth Forms range
        var words = new[] { "（", "测试", "）" };
        OcrTextMerger.MergeWords(words).Should().Be("（测试）");
    }

    [Fact]
    public void MergeWords_CjkPunctuation_NoBrokenSpacing()
    {
        // CJK Symbols and Punctuation range (3000-303F) includes 。and 、
        var words = new[] { "你好", "。", "世界" };
        OcrTextMerger.MergeWords(words).Should().Be("你好。世界");
    }

    #endregion

    #region MergeLines

    [Fact]
    public void MergeLines_EmptyList_ReturnsEmpty()
    {
        OcrTextMerger.MergeLines([]).Should().BeEmpty();
    }

    [Fact]
    public void MergeLines_SingleLine_ReturnsText()
    {
        var lines = new[] { new OcrLine { Text = "Hello World" } };
        OcrTextMerger.MergeLines(lines).Should().Be("Hello World");
    }

    [Fact]
    public void MergeLines_MultipleLines_JoinedWithNewlines()
    {
        var lines = new[]
        {
            new OcrLine { Text = "Line 1" },
            new OcrLine { Text = "Line 2" },
            new OcrLine { Text = "Line 3" }
        };

        var result = OcrTextMerger.MergeLines(lines);
        result.Should().Be($"Line 1{Environment.NewLine}Line 2{Environment.NewLine}Line 3");
    }

    [Fact]
    public void MergeLines_EmptyLine_Preserved()
    {
        var lines = new[]
        {
            new OcrLine { Text = "Before" },
            new OcrLine { Text = "" },
            new OcrLine { Text = "After" }
        };

        var result = OcrTextMerger.MergeLines(lines);
        result.Should().Be($"Before{Environment.NewLine}{Environment.NewLine}After");
    }

    #endregion

    #region GroupAndSortLines

    [Fact]
    public void GroupAndSortLines_EmptyList_ReturnsEmpty()
    {
        OcrTextMerger.GroupAndSortLines([]).Should().BeEmpty();
    }

    [Fact]
    public void GroupAndSortLines_SingleLine_ReturnsSame()
    {
        var lines = new[]
        {
            new OcrLine { Text = "Only", BoundingRect = new OcrRect(10, 10, 100, 20) }
        };
        OcrTextMerger.GroupAndSortLines(lines).Should().HaveCount(1);
        OcrTextMerger.GroupAndSortLines(lines)[0].Text.Should().Be("Only");
    }

    [Fact]
    public void GroupAndSortLines_SameRow_SortedLeftToRight()
    {
        // Two lines on the same Y, but right one comes first in input
        var lines = new[]
        {
            new OcrLine { Text = "Right", BoundingRect = new OcrRect(200, 10, 100, 20) },
            new OcrLine { Text = "Left", BoundingRect = new OcrRect(10, 10, 100, 20) }
        };

        var result = OcrTextMerger.GroupAndSortLines(lines);
        result.Should().HaveCount(2);
        result[0].Text.Should().Be("Left");
        result[1].Text.Should().Be("Right");
    }

    [Fact]
    public void GroupAndSortLines_DifferentRows_SortedTopToBottom()
    {
        // Two lines on different rows, bottom one comes first in input
        var lines = new[]
        {
            new OcrLine { Text = "Bottom", BoundingRect = new OcrRect(10, 100, 100, 20) },
            new OcrLine { Text = "Top", BoundingRect = new OcrRect(10, 10, 100, 20) }
        };

        var result = OcrTextMerger.GroupAndSortLines(lines);
        result[0].Text.Should().Be("Top");
        result[1].Text.Should().Be("Bottom");
    }

    [Fact]
    public void GroupAndSortLines_MultiColumnLayout_CorrectOrder()
    {
        // Simulate a 2-column layout:
        // Row 1: "A" at (10,10) and "B" at (200,10)
        // Row 2: "C" at (10,50) and "D" at (200,50)
        var lines = new[]
        {
            new OcrLine { Text = "D", BoundingRect = new OcrRect(200, 50, 100, 20) },
            new OcrLine { Text = "A", BoundingRect = new OcrRect(10, 10, 100, 20) },
            new OcrLine { Text = "C", BoundingRect = new OcrRect(10, 50, 100, 20) },
            new OcrLine { Text = "B", BoundingRect = new OcrRect(200, 10, 100, 20) }
        };

        var result = OcrTextMerger.GroupAndSortLines(lines);
        result.Select(l => l.Text).Should().BeEquivalentTo(
            new[] { "A", "B", "C", "D" },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void GroupAndSortLines_SlightYVariation_GroupedAsOneRow()
    {
        // Lines with slight Y variation should be on the same row
        // avgHeight = 20, tolerance = 20*0.5 = 10, Y diff = 5 → same row
        var lines = new[]
        {
            new OcrLine { Text = "Word2", BoundingRect = new OcrRect(150, 15, 80, 20) },
            new OcrLine { Text = "Word1", BoundingRect = new OcrRect(10, 10, 80, 20) }
        };

        var result = OcrTextMerger.GroupAndSortLines(lines);
        result[0].Text.Should().Be("Word1", "should be sorted left-to-right on same row");
        result[1].Text.Should().Be("Word2");
    }

    [Fact]
    public void GroupAndSortLines_LargeYGap_DifferentRows()
    {
        // Lines with large Y gap should be different rows
        // avgHeight = 20, tolerance = 20*0.5 = 10, Y diff = 50 → different rows
        var lines = new[]
        {
            new OcrLine { Text = "Second", BoundingRect = new OcrRect(10, 60, 80, 20) },
            new OcrLine { Text = "First", BoundingRect = new OcrRect(10, 10, 80, 20) }
        };

        var result = OcrTextMerger.GroupAndSortLines(lines);
        result[0].Text.Should().Be("First");
        result[1].Text.Should().Be("Second");
    }

    [Fact]
    public void GroupAndSortLines_CustomToleranceFactor()
    {
        // With a very small tolerance, even small Y differences split into rows
        // avgHeight = 20, yTolerance = 20 * 0.1 = 2, Y diff = 5 → different rows
        var lines = new[]
        {
            new OcrLine { Text = "B", BoundingRect = new OcrRect(150, 15, 80, 20) },
            new OcrLine { Text = "A", BoundingRect = new OcrRect(10, 10, 80, 20) }
        };

        var result = OcrTextMerger.GroupAndSortLines(lines, yToleranceFactor: 0.1);
        // With tight tolerance, Y=10 and Y=15 are different rows (diff=5 > tolerance=2)
        result[0].Text.Should().Be("A");
        result[1].Text.Should().Be("B");
    }

    [Fact]
    public void GroupAndSortLines_ZeroHeightLines_UsesDefault()
    {
        // Lines with zero height should fall back to default (20px) for tolerance
        var lines = new[]
        {
            new OcrLine { Text = "B", BoundingRect = new OcrRect(150, 10, 80, 0) },
            new OcrLine { Text = "A", BoundingRect = new OcrRect(10, 10, 80, 0) }
        };

        var result = OcrTextMerger.GroupAndSortLines(lines);
        // Default height=20, tolerance=10, Y diff=0 → same row, sorted by X
        result[0].Text.Should().Be("A");
        result[1].Text.Should().Be("B");
    }

    #endregion
}
