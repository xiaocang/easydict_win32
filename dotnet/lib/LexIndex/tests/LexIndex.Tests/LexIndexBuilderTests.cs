using System.Buffers.Binary;
using FluentAssertions;
using Xunit;

namespace LexIndex.Tests;

public sealed class LexIndexBuilderTests
{
    [Fact]
    public async Task BuildAndOpen_RoundTripsPrefixAndWildcardQueries()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(
            ["apple", "application", "apply", "tealight", "teatime", "teatray"],
            stream);

        stream.Position = 0;
        var index = LexIndex.Open(stream);

        index.Complete("app", 10).Should().Equal("apple", "application", "apply");
        index.Match("tea*t", 10).Should().Equal("tealight");
    }

    [Fact]
    public async Task BuildAsync_PreservesOriginalVariantsForSameNormalizedKey()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(["Apple", "apple", "Ａｐｐｌｅ"], stream);

        stream.Position = 0;
        var index = LexIndex.Open(stream);

        index.Complete("apple", 10).Should().Equal("Apple", "apple", "Ａｐｐｌｅ");
    }

    [Fact]
    public async Task BuildAsync_UsesNormalizationForUnicodeAndCompatibilityForms()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(["café", "CAFÉ", "𝓐pple", "Alpha beta"], stream);

        stream.Position = 0;
        var index = LexIndex.Open(stream);

        index.Complete("café", 10).Should().Equal("CAFÉ", "café");
        index.Complete("apple", 10).Should().Equal("𝓐pple");
        index.Match("alpha?beta", 10).Should().Equal("Alpha beta");
    }

    [Fact]
    public async Task BuildAsync_MetadataReflectsMinimizationBenefit()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(["cat", "car", "cart", "dog", "dot"], stream);

        stream.Position = 0;
        var index = LexIndex.Open(stream);

        index.Metadata.StateCount.Should().BeLessThan(1 + "cat".Length + "car".Length + "cart".Length + "dog".Length + "dot".Length);
        index.Metadata.EntryCount.Should().Be(5);
    }

    [Fact]
    public async Task BuildAsync_EmptyAndWhitespaceKeysAreIgnored()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(["", " ", "\t", "apple"], stream);

        stream.Position = 0;
        var index = LexIndex.Open(stream);

        index.Metadata.EntryCount.Should().Be(1);
        index.Complete("a", 10).Should().Equal("apple");
    }

    [Fact]
    public async Task Match_QuestionAndStarRespectLimit()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(["cat", "cot", "coat", "cut"], stream);

        stream.Position = 0;
        var index = LexIndex.Open(stream);

        index.Match("c?t", 2).Should().Equal("cat", "cot");
        index.Match("c*t", 10).Should().Equal("cat", "coat", "cot", "cut");
    }

    [Fact]
    public void Open_InvalidHeaderThrows()
    {
        using var stream = new MemoryStream([1, 2, 3, 4]);

        var action = () => LexIndex.Open(stream);

        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task BuildAsync_WithNoUsableKeysProducesEmptyIndex()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(["", " ", "\t"], stream);

        stream.Position = 0;
        var index = LexIndex.Open(stream);

        index.Metadata.EntryCount.Should().Be(0);
        index.Complete("a", 10).Should().BeEmpty();
        index.Match("*", 10).Should().BeEmpty();
    }

    [Fact]
    public async Task Open_UnsupportedVersionThrows()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(["apple"], stream);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, sizeof(int)), 99);

        var action = () => LexIndex.Open(new MemoryStream(bytes));

        action.Should().Throw<InvalidDataException>().WithMessage("*version*");
    }

    [Fact]
    public async Task Open_InvalidEdgeTableThrows()
    {
        using var stream = new MemoryStream();
        await LexIndexBuilder.BuildAsync(["apple"], stream);
        var bytes = stream.ToArray();
        const int headerSize = 4 + (sizeof(int) * 9);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(headerSize, sizeof(int)), 1024);

        var action = () => LexIndex.Open(new MemoryStream(bytes));

        action.Should().Throw<InvalidDataException>().WithMessage("*edge bounds*");
    }
}
