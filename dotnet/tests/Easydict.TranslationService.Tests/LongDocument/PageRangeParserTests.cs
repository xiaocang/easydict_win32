using Easydict.TranslationService.LongDocument;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LongDocument;

public class PageRangeParserTests
{
    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        PageRangeParser.Parse(null, 10).Should().BeNull();
    }

    [Fact]
    public void Parse_Empty_ReturnsNull()
    {
        PageRangeParser.Parse("", 10).Should().BeNull();
    }

    [Fact]
    public void Parse_Whitespace_ReturnsNull()
    {
        PageRangeParser.Parse("   ", 10).Should().BeNull();
    }

    [Fact]
    public void Parse_All_ReturnsNull()
    {
        PageRangeParser.Parse("all", 10).Should().BeNull();
        PageRangeParser.Parse("ALL", 10).Should().BeNull();
        PageRangeParser.Parse("All", 10).Should().BeNull();
    }

    [Fact]
    public void Parse_SinglePage_ReturnsSet()
    {
        var result = PageRangeParser.Parse("3", 10);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 3 });
    }

    [Fact]
    public void Parse_Range_ReturnsSet()
    {
        var result = PageRangeParser.Parse("1-5", 10);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void Parse_Mixed_ReturnsSet()
    {
        var result = PageRangeParser.Parse("1-3,5,7-10", 10);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 1, 2, 3, 5, 7, 8, 9, 10 });
    }

    [Fact]
    public void Parse_ExceedsTotal_ClampedToTotal()
    {
        var result = PageRangeParser.Parse("1-20", 5);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void Parse_PageBeyondTotal_Excluded()
    {
        var result = PageRangeParser.Parse("100", 5);
        result.Should().BeNull(); // No valid pages, returns null
    }

    [Fact]
    public void Parse_ZeroAndNegative_Excluded()
    {
        var result = PageRangeParser.Parse("0,1,2", 5);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public void Parse_WithSpaces_Trimmed()
    {
        var result = PageRangeParser.Parse(" 1 - 3 , 5 ", 10);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 1, 2, 3, 5 });
    }

    [Fact]
    public void Parse_InvalidFormat_ReturnsNull()
    {
        PageRangeParser.Parse("abc", 10).Should().BeNull();
    }

    [Fact]
    public void Parse_MixedValidInvalid_ReturnsValidOnly()
    {
        var result = PageRangeParser.Parse("1,abc,3", 10);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 1, 3 });
    }

    [Fact]
    public void Parse_SinglePageRange_Works()
    {
        var result = PageRangeParser.Parse("5-5", 10);
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 5 });
    }
}
