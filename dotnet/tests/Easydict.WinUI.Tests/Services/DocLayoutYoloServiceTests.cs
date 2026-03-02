using Easydict.WinUI.Services;
using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class DocLayoutYoloServiceTests
{
    [Fact]
    public void ClassNames_HasExpectedCount()
    {
        DocLayoutYoloService.ClassNames.Should().HaveCount(10);
    }

    [Fact]
    public void ClassToRegionType_HasSameCountAsClassNames()
    {
        DocLayoutYoloService.ClassToRegionType.Should().HaveCount(DocLayoutYoloService.ClassNames.Length);
    }

    [Theory]
    [InlineData(0, LayoutRegionType.Title)]           // title
    [InlineData(1, LayoutRegionType.Body)]             // plain text
    [InlineData(2, LayoutRegionType.Figure)]           // abandon
    [InlineData(3, LayoutRegionType.Figure)]           // figure
    [InlineData(4, LayoutRegionType.Caption)]          // figure_caption
    [InlineData(5, LayoutRegionType.Table)]            // table
    [InlineData(6, LayoutRegionType.Caption)]          // table_caption
    [InlineData(7, LayoutRegionType.Caption)]          // table_footnote
    [InlineData(8, LayoutRegionType.IsolatedFormula)]  // isolate_formula
    [InlineData(9, LayoutRegionType.Caption)]          // formula_caption
    public void MapClassToRegionType_MapsCorrectly(int classIndex, LayoutRegionType expected)
    {
        DocLayoutYoloService.MapClassToRegionType(classIndex).Should().Be(expected);
    }

    [Theory]
    [InlineData(-1, LayoutRegionType.Unknown)]
    [InlineData(10, LayoutRegionType.Unknown)]
    [InlineData(100, LayoutRegionType.Unknown)]
    public void MapClassToRegionType_OutOfRange_ReturnsUnknown(int classIndex, LayoutRegionType expected)
    {
        DocLayoutYoloService.MapClassToRegionType(classIndex).Should().Be(expected);
    }

    [Fact]
    public void PreprocessImage_ProducesCorrectTensorShape()
    {
        // Create a small 10x10 BGRA image (all black)
        var pixels = new byte[10 * 10 * 4];
        var (tensor, scaleX, scaleY, padX, padY) = DocLayoutYoloService.PreprocessImage(pixels, 10, 10);

        tensor.Dimensions.ToArray().Should().HaveCount(4);
        tensor.Dimensions[0].Should().Be(1);
        tensor.Dimensions[1].Should().Be(3);
        tensor.Dimensions[2].Should().Be(1024);
        tensor.Dimensions[3].Should().Be(1024);
    }

    [Fact]
    public void PreprocessImage_LetterboxMaintainsAspectRatio()
    {
        // Create a 200x100 BGRA image (wider than tall)
        var pixels = new byte[200 * 100 * 4];
        var (_, scaleX, scaleY, padX, padY) = DocLayoutYoloService.PreprocessImage(pixels, 200, 100);

        // Scale should be limited by width: 1024/200 = 5.12
        scaleX.Should().BeApproximately(scaleY, 0.01);
        padX.Should().Be(0); // No horizontal padding (width-limited)
        padY.Should().BeGreaterThan(0); // Vertical padding expected
    }

    [Fact]
    public void ComputeIoU_PerfectOverlap_ReturnsOne()
    {
        var a = new LayoutDetection(LayoutRegionType.Body, 0.9f, 0, 0, 100, 100);
        var b = new LayoutDetection(LayoutRegionType.Body, 0.8f, 0, 0, 100, 100);

        DocLayoutYoloService.ComputeIoU(a, b).Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void ComputeIoU_NoOverlap_ReturnsZero()
    {
        var a = new LayoutDetection(LayoutRegionType.Body, 0.9f, 0, 0, 100, 100);
        var b = new LayoutDetection(LayoutRegionType.Body, 0.8f, 200, 200, 100, 100);

        DocLayoutYoloService.ComputeIoU(a, b).Should().Be(0f);
    }

    [Fact]
    public void ComputeIoU_PartialOverlap_ReturnsCorrectValue()
    {
        var a = new LayoutDetection(LayoutRegionType.Body, 0.9f, 0, 0, 100, 100);
        var b = new LayoutDetection(LayoutRegionType.Body, 0.8f, 50, 50, 100, 100);

        // Intersection: 50x50 = 2500
        // Union: 10000 + 10000 - 2500 = 17500
        // IoU: 2500/17500 ≈ 0.1429
        DocLayoutYoloService.ComputeIoU(a, b).Should().BeApproximately(0.1429f, 0.01f);
    }

    [Fact]
    public void ApplyNms_RemovesOverlappingDetectionsOfSameClass()
    {
        var detections = new List<LayoutDetection>
        {
            new(LayoutRegionType.Body, 0.9f, 0, 0, 100, 100),
            new(LayoutRegionType.Body, 0.7f, 5, 5, 100, 100),  // High overlap with first
            new(LayoutRegionType.Figure, 0.8f, 0, 0, 100, 100), // Different class, should survive
        };

        var result = DocLayoutYoloService.ApplyNms(detections, 0.45f);

        result.Should().HaveCount(2);
        result[0].Confidence.Should().Be(0.9f);  // Higher confidence Body survives
        result[1].RegionType.Should().Be(LayoutRegionType.Figure); // Different class survives
    }

    [Fact]
    public void ParseDetections_EmptyTensor_ReturnsEmpty()
    {
        var tensor = new DenseTensor<float>([1, 14, 0]);
        var result = DocLayoutYoloService.ParseDetections(tensor, 1.0, 1.0, 0, 0, 1024, 1024, 0.25f);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseDetections_BelowThreshold_Filtered()
    {
        // Create a tensor with one detection below threshold
        var tensor = new DenseTensor<float>([1, 14, 1]);
        // Set bbox (cx, cy, w, h)
        tensor[0, 0, 0] = 512; // cx
        tensor[0, 1, 0] = 512; // cy
        tensor[0, 2, 0] = 100; // w
        tensor[0, 3, 0] = 100; // h
        // Set class scores (all below threshold)
        for (int c = 0; c < 10; c++)
            tensor[0, 4 + c, 0] = 0.1f;

        var result = DocLayoutYoloService.ParseDetections(tensor, 1.0, 1.0, 0, 0, 1024, 1024, 0.25f);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseDetections_AboveThreshold_ReturnsDetection()
    {
        var tensor = new DenseTensor<float>([1, 14, 1]);
        tensor[0, 0, 0] = 512; // cx
        tensor[0, 1, 0] = 512; // cy
        tensor[0, 2, 0] = 100; // w
        tensor[0, 3, 0] = 100; // h
        // Set class 0 (title) to high confidence
        tensor[0, 4, 0] = 0.95f;

        var result = DocLayoutYoloService.ParseDetections(tensor, 1.0, 1.0, 0, 0, 1024, 1024, 0.25f);
        result.Should().HaveCount(1);
        result[0].RegionType.Should().Be(LayoutRegionType.Title);
        result[0].Confidence.Should().BeApproximately(0.95f, 0.01f);
    }
}
