using Easydict.TextLayout.Segmentation;
using FluentAssertions;
using static Easydict.TextLayout.Segmentation.ScriptClassifier;

namespace Easydict.TextLayout.Tests.Segmentation;

public class ScriptClassifierTests
{
    [Theory]
    [InlineData('A', CharCategory.Latin)]
    [InlineData('z', CharCategory.Latin)]
    [InlineData('0', CharCategory.Latin)]
    [InlineData('9', CharCategory.Latin)]
    [InlineData('-', CharCategory.Latin)]
    [InlineData('_', CharCategory.Latin)]
    public void Classify_Latin_ReturnsLatin(char ch, CharCategory expected)
    {
        Classify(ch).Should().Be(expected);
    }

    [Theory]
    [InlineData('\u4E00')] // CJK Unified start
    [InlineData('\u9FFF')] // CJK Unified end
    [InlineData('\u3040')] // Hiragana start
    [InlineData('\u30A0')] // Katakana start
    [InlineData('\uAC00')] // Hangul start
    public void Classify_Cjk_ReturnsCjk(char ch)
    {
        Classify(ch).Should().Be(CharCategory.Cjk);
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    [InlineData('\r')]
    public void Classify_Space_ReturnsSpace(char ch)
    {
        Classify(ch).Should().Be(CharCategory.Space);
    }

    [Fact]
    public void Classify_Newline_ReturnsHardBreak()
    {
        Classify('\n').Should().Be(CharCategory.HardBreak);
    }

    [Theory]
    [InlineData('(')]
    [InlineData('[')]
    [InlineData('{')]
    [InlineData('\u300C')] // LEFT CORNER BRACKET
    [InlineData('\uFF08')] // FULLWIDTH LEFT PARENTHESIS
    public void Classify_OpenPunctuation_ReturnsOpenPunctuation(char ch)
    {
        Classify(ch).Should().Be(CharCategory.OpenPunctuation);
    }

    [Theory]
    [InlineData(')')]
    [InlineData(']')]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData(':')]
    [InlineData('!')]
    [InlineData('?')]
    [InlineData('\u3001')] // IDEOGRAPHIC COMMA
    [InlineData('\u3002')] // IDEOGRAPHIC FULL STOP
    [InlineData('\uFF09')] // FULLWIDTH RIGHT PARENTHESIS
    public void Classify_ClosePunctuation_ReturnsClosePunctuation(char ch)
    {
        Classify(ch).Should().Be(CharCategory.ClosePunctuation);
    }

    [Fact]
    public void IsCjk_CjkUnifiedRange_ReturnsTrue()
    {
        ScriptClassifier.IsCjk('\u4E00').Should().BeTrue();
        ScriptClassifier.IsCjk('\u9FFF').Should().BeTrue();
    }

    [Fact]
    public void IsCjk_LatinChar_ReturnsFalse()
    {
        ScriptClassifier.IsCjk('A').Should().BeFalse();
    }

    [Fact]
    public void EnumerateGraphemes_SimpleText_ReturnsCharacters()
    {
        var graphemes = ScriptClassifier.EnumerateGraphemes("Hello").ToList();
        graphemes.Should().Equal("H", "e", "l", "l", "o");
    }

    [Fact]
    public void EnumerateGraphemes_CjkText_ReturnsCharacters()
    {
        var graphemes = ScriptClassifier.EnumerateGraphemes("你好").ToList();
        graphemes.Should().Equal("你", "好");
    }

    [Fact]
    public void EnumerateGraphemes_Empty_ReturnsEmpty()
    {
        ScriptClassifier.EnumerateGraphemes("").ToList().Should().BeEmpty();
    }
}
