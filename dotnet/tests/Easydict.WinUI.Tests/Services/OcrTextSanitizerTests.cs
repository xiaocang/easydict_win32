using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for stripping reasoning markup out of LLM/VLM OCR responses.
/// </summary>
[Trait("Category", "WinUI")]
public class OcrTextSanitizerTests
{
    [Theory]
    [InlineData("<think>Let me look.</think>Hello world", "Hello world")]
    [InlineData("<thinking>Let me look.</thinking>\nHello world", "Hello world")]
    [InlineData("<reasoning>Let me look.</reasoning> Hello world", "Hello world")]
    [InlineData("< think >Let me look.< / think >Hello world", "Hello world")]
    [InlineData("<THINK>Let me look.</THINK>Hello world", "Hello world")]
    public void StripThinkingMarkup_RemovesCompleteBlocks(string input, string expected)
    {
        OcrTextSanitizer.StripThinkingMarkup(input).Should().Be(expected);
    }

    [Fact]
    public void StripThinkingMarkup_RemovesMultiLineBlocks()
    {
        var input = "<think>\nFirst the sign.\nThen the text.\n</think>\nSTOP\nAHEAD";

        OcrTextSanitizer.StripThinkingMarkup(input).Should().Be("STOP\nAHEAD");
    }

    [Fact]
    public void StripThinkingMarkup_DropsUnclosedBlockAndKeepsTextBeforeIt()
    {
        // The response ran out of tokens mid-thought.
        var input = "STOP\n<think>Now let me double-check the second line";

        OcrTextSanitizer.StripThinkingMarkup(input).Should().Be("STOP");
    }

    [Fact]
    public void StripThinkingMarkup_DropsReasoningBeforeOrphanCloser()
    {
        // Some gateways emit the reasoning without an opening tag.
        var input = "Let me look at the image.</think>\nSTOP";

        OcrTextSanitizer.StripThinkingMarkup(input).Should().Be("STOP");
    }

    [Fact]
    public void StripThinkingMarkup_LeavesPlainTextIntact()
    {
        var input = "Line one\nLine two";

        OcrTextSanitizer.StripThinkingMarkup(input).Should().Be(input);
    }

    [Fact]
    public void StripThinkingMarkup_TrimsSurroundingWhitespace()
    {
        OcrTextSanitizer.StripThinkingMarkup("  Hello world \n").Should().Be("Hello world");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StripThinkingMarkup_ReturnsEmpty_ForBlankInput(string? input)
    {
        OcrTextSanitizer.StripThinkingMarkup(input).Should().BeEmpty();
    }

    [Fact]
    public void StripThinkingMarkup_ReturnsEmpty_WhenResponseIsOnlyReasoning()
    {
        OcrTextSanitizer.StripThinkingMarkup("<think>I cannot read this.</think>")
            .Should().BeEmpty();
    }

    [Fact]
    public void StripThinkingMarkup_DoesNotTouchAngleBracketsInRecognizedText()
    {
        var input = "if (a < b) { return thinking; }";

        OcrTextSanitizer.StripThinkingMarkup(input).Should().Be(input);
    }
}
