using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Models;

/// <summary>
/// Tests for the shared word-query heuristics used by dictionary lookup,
/// phonetic enrichment and phonetic display.
/// </summary>
public class WordQueryHeuristicsTests
{
    [Theory]
    [InlineData("hello", true)]
    [InlineData("hello world", true)]
    [InlineData("well-known", true)]
    [InlineData("don't", true)]
    [InlineData("你好", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Hello.", false)]
    [InlineData("Is this a word?", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("这是一个比较长的句子需要翻译", false)]
    public void IsWordQuery_ReturnsExpectedResult(string text, bool expected)
    {
        WordQueryHeuristics.IsWordQuery(text).Should().Be(expected);
    }

    [Fact]
    public void IsWordQuery_NullText_ReturnsFalse()
    {
        WordQueryHeuristics.IsWordQuery(null).Should().BeFalse();
    }

    [Fact]
    public void IsWordQuery_OverlyLongText_ReturnsFalse()
    {
        WordQueryHeuristics.IsWordQuery(new string('a', 51)).Should().BeFalse();
    }

    [Theory]
    [InlineData("hello", true)]
    [InlineData("hello world", true)]
    [InlineData("well-known", true)]
    [InlineData("don't", true)]
    [InlineData("你好", false)]
    [InlineData("こんにちは", false)]
    [InlineData("Привет", false)]
    [InlineData("hello.", false)]
    [InlineData("", false)]
    public void LooksLikeEnglishWord_ReturnsExpectedResult(string text, bool expected)
    {
        WordQueryHeuristics.LooksLikeEnglishWord(text).Should().Be(expected);
    }

    [Fact]
    public void LooksLikeEnglishWord_DigitsOnly_ReturnsFalse()
    {
        // A bare number is a word query but carries no pronunciation to look up
        WordQueryHeuristics.IsWordQuery("2024").Should().BeFalse();
        WordQueryHeuristics.LooksLikeEnglishWord("2024").Should().BeFalse();
    }

    [Fact]
    public void YoudaoService_IsWordQuery_DelegatesToSharedHeuristics()
    {
        // The dictionary service and the host must agree on what counts as a word.
        foreach (var text in new[] { "hello", "你好", "Is this a word?", "hello world" })
        {
            Easydict.TranslationService.Services.YoudaoService.IsWordQuery(text)
                .Should().Be(WordQueryHeuristics.IsWordQuery(text), $"for '{text}'");
        }
    }
}
