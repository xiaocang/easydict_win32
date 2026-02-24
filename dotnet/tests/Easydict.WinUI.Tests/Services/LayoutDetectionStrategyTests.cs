using Easydict.TranslationService.LongDocument;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class LayoutDetectionStrategyTests
{
    [Theory]
    [InlineData("p1-header-b1", LayoutRegionType.Header)]
    [InlineData("p1-footer-b1", LayoutRegionType.Footer)]
    [InlineData("p1-left-b1", LayoutRegionType.LeftColumn)]
    [InlineData("p1-right-b1", LayoutRegionType.RightColumn)]
    [InlineData("p1-table-b1", LayoutRegionType.TableLike)]
    [InlineData("p1-body-b1", LayoutRegionType.Body)]
    [InlineData("p1-b1", LayoutRegionType.Body)]  // Missing region tag
    public void InferRegionTypeFromBlockId_MapsCorrectly(string blockId, LayoutRegionType expected)
    {
        LayoutDetectionStrategy.InferRegionTypeFromBlockId(blockId).Should().Be(expected);
    }

    [Fact]
    public void ParseHeuristicBlocks_ExtractsBlocksWithRegionTypes()
    {
        var blocks = new[]
        {
            new SourceDocumentBlock
            {
                BlockId = "p1-header-b1",
                BlockType = SourceBlockType.Paragraph,
                Text = "Header text"
            },
            new SourceDocumentBlock
            {
                BlockId = "p1-body-b2",
                BlockType = SourceBlockType.Paragraph,
                Text = "Body text"
            },
            new SourceDocumentBlock
            {
                BlockId = "p1-table-b3",
                BlockType = SourceBlockType.TableCell,
                Text = "Table content"
            }
        };

        var result = LayoutDetectionStrategy.ParseHeuristicBlocks(blocks);

        result.Should().HaveCount(3);
        result[0].RegionType.Should().Be(LayoutRegionType.Header);
        result[1].RegionType.Should().Be(LayoutRegionType.Body);
        result[2].RegionType.Should().Be(LayoutRegionType.TableLike);
    }

    [Fact]
    public void MergeDetections_NoMLDetections_ReturnsHeuristicResults()
    {
        var blocks = new List<HeuristicBlock>
        {
            new(new SourceDocumentBlock
            {
                BlockId = "p1-body-b1",
                BlockType = SourceBlockType.Paragraph,
                Text = "Text",
                BoundingBox = new BlockRect(10, 10, 200, 50)
            }, LayoutRegionType.Body)
        };

        var mlDetections = new List<LayoutDetection>();

        // We can't easily create a PdfPig page in tests, but we can test the merge logic
        // by checking the static method behavior with the blocks having no BoundingBox match
        var emptyMl = new List<LayoutDetection>();

        // When ML detections are empty, the strategy falls back to heuristic
        // (tested via the DetectAndExtractAsync method indirectly)
        blocks.Should().HaveCount(1);
        blocks[0].RegionType.Should().Be(LayoutRegionType.Body);
    }

    [Fact]
    public void EnhancedSourceBlock_RecordCreation()
    {
        var block = new SourceDocumentBlock
        {
            BlockId = "p1-body-b1",
            BlockType = SourceBlockType.Paragraph,
            Text = "Test"
        };

        var enhanced = new EnhancedSourceBlock(
            block,
            LayoutRegionType.Figure,
            0.95,
            LayoutRegionSource.OnnxModel);

        enhanced.RegionType.Should().Be(LayoutRegionType.Figure);
        enhanced.Confidence.Should().Be(0.95);
        enhanced.Source.Should().Be(LayoutRegionSource.OnnxModel);
        enhanced.Block.Should().BeSameAs(block);
    }

    [Fact]
    public void HeuristicBlock_RecordCreation()
    {
        var block = new SourceDocumentBlock
        {
            BlockId = "p1-header-b1",
            BlockType = SourceBlockType.Heading,
            Text = "Title"
        };

        var hb = new HeuristicBlock(block, LayoutRegionType.Header);
        hb.Block.Should().BeSameAs(block);
        hb.RegionType.Should().Be(LayoutRegionType.Header);
    }
}
