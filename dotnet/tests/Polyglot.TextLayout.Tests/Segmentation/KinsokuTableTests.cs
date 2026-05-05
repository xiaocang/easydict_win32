using Polyglot.TextLayout.Segmentation;
using FluentAssertions;

namespace Polyglot.TextLayout.Tests.Segmentation;

public class KinsokuTableTests
{
    [Theory]
    [InlineData('\u3001')] // IDEOGRAPHIC COMMA 、
    [InlineData('\u3002')] // IDEOGRAPHIC FULL STOP 。
    [InlineData('\u300D')] // RIGHT CORNER BRACKET 」
    [InlineData('\u300F')] // RIGHT WHITE CORNER BRACKET 』
    [InlineData('\u3011')] // RIGHT BLACK LENTICULAR BRACKET 】
    [InlineData('\uFF09')] // FULLWIDTH RIGHT PARENTHESIS ）
    [InlineData('\uFF01')] // FULLWIDTH EXCLAMATION ！
    [InlineData('\uFF1F')] // FULLWIDTH QUESTION ？
    [InlineData('\u30FC')] // PROLONGED SOUND MARK ー
    [InlineData('\u30FB')] // KATAKANA MIDDLE DOT ・
    public void IsProhibitedLineStart_ClosingAndPunctuation_ReturnsTrue(char ch)
    {
        KinsokuTable.IsProhibitedLineStart(ch).Should().BeTrue();
    }

    [Theory]
    [InlineData('\u3041')] // SMALL ぁ
    [InlineData('\u3043')] // SMALL ぃ
    [InlineData('\u3045')] // SMALL ぅ
    [InlineData('\u3063')] // SMALL っ
    [InlineData('\u30A1')] // SMALL ァ
    [InlineData('\u30C3')] // SMALL ッ
    [InlineData('\u30E3')] // SMALL ャ
    public void IsProhibitedLineStart_SmallKana_ReturnsTrue(char ch)
    {
        KinsokuTable.IsProhibitedLineStart(ch).Should().BeTrue();
    }

    [Theory]
    [InlineData('\u309D')] // ゝ
    [InlineData('\u309E')] // ゞ
    [InlineData('\u30FD')] // ヽ
    [InlineData('\u30FE')] // ヾ
    [InlineData('\u3005')] // 々
    public void IsProhibitedLineStart_IterationMarks_ReturnsTrue(char ch)
    {
        KinsokuTable.IsProhibitedLineStart(ch).Should().BeTrue();
    }

    [Theory]
    [InlineData('\u4E00')] // 一 (normal CJK)
    [InlineData('\u3042')] // あ (normal hiragana)
    [InlineData('\u30AB')] // カ (normal katakana)
    [InlineData('A')]
    [InlineData(' ')]
    public void IsProhibitedLineStart_NormalChars_ReturnsFalse(char ch)
    {
        KinsokuTable.IsProhibitedLineStart(ch).Should().BeFalse();
    }

    [Theory]
    [InlineData('\uFF08')] // FULLWIDTH LEFT PARENTHESIS （
    [InlineData('\u300C')] // LEFT CORNER BRACKET 「
    [InlineData('\u300E')] // LEFT WHITE CORNER BRACKET 『
    [InlineData('\u3010')] // LEFT BLACK LENTICULAR BRACKET 【
    [InlineData('\u3008')] // LEFT ANGLE BRACKET 〈
    [InlineData('\u300A')] // LEFT DOUBLE ANGLE BRACKET 《
    public void IsProhibitedLineEnd_OpeningBrackets_ReturnsTrue(char ch)
    {
        KinsokuTable.IsProhibitedLineEnd(ch).Should().BeTrue();
    }

    [Theory]
    [InlineData('\u4E00')] // normal CJK
    [InlineData('\u3002')] // 。 (closing, not line-end prohibited)
    [InlineData('A')]
    public void IsProhibitedLineEnd_NonOpening_ReturnsFalse(char ch)
    {
        KinsokuTable.IsProhibitedLineEnd(ch).Should().BeFalse();
    }

    [Theory]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData('!')]
    [InlineData('?')]
    [InlineData(')')]
    [InlineData('%')]
    public void IsLeftSticky_AsciiPunct_ReturnsTrue(char ch)
    {
        KinsokuTable.IsLeftSticky(ch).Should().BeTrue();
    }

    [Theory]
    [InlineData('A')]
    [InlineData('1')]
    [InlineData(' ')]
    public void IsLeftSticky_NonPunct_ReturnsFalse(char ch)
    {
        KinsokuTable.IsLeftSticky(ch).Should().BeFalse();
    }
}
