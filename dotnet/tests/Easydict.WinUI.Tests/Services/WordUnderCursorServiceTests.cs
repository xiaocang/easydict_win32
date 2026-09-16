using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the pure word normalization / validation helpers of WordUnderCursorService.
/// </summary>
public class WordUnderCursorServiceTests
{
    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("  hello  ", "hello")]
    [InlineData("\"hello\"", "hello")]
    [InlineData("(hello),", "hello")]
    [InlineData("“hello”", "hello")]
    [InlineData("hello.", "hello")]
    [InlineData("don't", "don't")]
    [InlineData("well-known", "well-known")]
    [InlineData("-dash-", "dash")]
    [InlineData("\u200Bzero\uFEFF", "zero")]
    [InlineData("你好。", "你好")]
    public void NormalizeWord_TrimsSurroundingPunctuationAndWhitespace(string raw, string expected)
    {
        WordUnderCursorService.NormalizeWord(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("\"\"")]
    [InlineData("\u200B")]
    public void NormalizeWord_NothingLeft_ReturnsNull(string? raw)
    {
        WordUnderCursorService.NormalizeWord(raw).Should().BeNull();
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("Hello")]
    [InlineData("a")]
    [InlineData("don't")]
    [InlineData("don’t")]
    [InlineData("well-known")]
    [InlineData("mp3")]
    [InlineData("naïve")]
    [InlineData("你好")]
    [InlineData("悬浮取词")]
    [InlineData("こんにちは")]
    [InlineData("안녕하세요")]
    public void LooksLikeWord_AcceptsWords(string word)
    {
        WordUnderCursorService.LooksLikeWord(word).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("hello\nworld")]
    [InlineData("12345")]
    [InlineData("3.14")]
    [InlineData("https://example.com")]
    [InlineData("example.com")]
    [InlineData("user@example.com")]
    [InlineData("a/b")]
    [InlineData("C:\\path")]
    [InlineData("key=value")]
    [InlineData("foo(bar)")]
    [InlineData("--")]
    [InlineData("''")]
    [InlineData("一二三四五六七八九")]      // 9 CJK chars: too long for a lookup unit
    [InlineData("你好world")]               // mixed CJK + Latin is not a single word
    public void LooksLikeWord_RejectsNonWords(string word)
    {
        WordUnderCursorService.LooksLikeWord(word).Should().BeFalse();
    }

    [Fact]
    public void LooksLikeWord_RejectsOverlongWords()
    {
        var tooLong = new string('a', WordUnderCursorService.MaxWordLength + 1);
        WordUnderCursorService.LooksLikeWord(tooLong).Should().BeFalse();

        var maxLength = new string('a', WordUnderCursorService.MaxWordLength);
        WordUnderCursorService.LooksLikeWord(maxLength).Should().BeTrue();
    }
}
