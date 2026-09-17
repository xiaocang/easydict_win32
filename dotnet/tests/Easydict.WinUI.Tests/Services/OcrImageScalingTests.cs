using Easydict.SidecarClient;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests <see cref="OcrImageScaling"/> (Easydict.SidecarClient), which both the in-process
/// <c>WindowsOcrService</c> and the out-of-process OCR worker call to recover small text
/// (see issue #217). Kept here since there is no dedicated SidecarClient test project.
/// </summary>
[Trait("Category", "WinUI")]
public sealed class OcrImageScalingTests
{
    private const int MaxDimension = 10000;

    [Fact]
    public void ComputeRetryScale_EnlargesBlindly_WhenFirstPassFoundNothing()
    {
        var scale = OcrImageScaling.ComputeRetryScale([], 800, 400, MaxDimension);

        scale.Should().Be(OcrImageScaling.BlindRetryScale);
    }

    [Fact]
    public void ComputeRetryScale_TargetsReadableLineHeight_ForSmallLaptopText()
    {
        // A 13 px tall line is what an unscaled 1080p laptop panel produces for body text.
        var scale = OcrImageScaling.ComputeRetryScale([13, 13, 14], 800, 400, MaxDimension);

        scale.Should().BeApproximately(OcrImageScaling.TargetLineHeight / 13.0, 0.001);
    }

    [Fact]
    public void ComputeRetryScale_SkipsRetry_WhenTextIsAlreadyLargeEnough()
    {
        var scale = OcrImageScaling.ComputeRetryScale([30, 34], 800, 400, MaxDimension);

        scale.Should().Be(1.0);
    }

    [Fact]
    public void ComputeRetryScale_SkipsRetry_WhenTheImageIsTooLargeToEnlargeMeaningfully()
    {
        // A near-budget capture can only grow a few percent, which cannot change the reading.
        var scale = OcrImageScaling.ComputeRetryScale([5], 3800, 3800, MaxDimension);

        scale.Should().Be(1.0);
    }

    [Fact]
    public void ComputeRetryScale_IsCappedByMaxScale()
    {
        var scale = OcrImageScaling.ComputeRetryScale([2], 400, 200, MaxDimension);

        scale.Should().Be(OcrImageScaling.MaxScale);
    }

    [Fact]
    public void ComputeRetryScale_StaysWithinEngineDimensionLimit()
    {
        var scale = OcrImageScaling.ComputeRetryScale([5], 4000, 500, MaxDimension);

        scale.Should().BeApproximately(2.5, 0.001);
        (4000 * scale).Should().BeLessThanOrEqualTo(MaxDimension);
    }

    [Fact]
    public void ComputeRetryScale_StaysWithinPixelBudget()
    {
        var scale = OcrImageScaling.ComputeRetryScale([5], 3000, 2000, MaxDimension);

        scale.Should().BeGreaterThan(1.0);
        ((long)(3000 * scale) * (long)(2000 * scale))
            .Should().BeLessThanOrEqualTo(OcrImageScaling.MaxScaledPixelCount);
    }

    [Fact]
    public void ComputeRetryScale_SkipsRetry_WhenSourceAlreadyExceedsEngineLimit()
    {
        var scale = OcrImageScaling.ComputeRetryScale([5], MaxDimension + 1, 200, MaxDimension);

        scale.Should().Be(1.0);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(200, 0)]
    [InlineData(200, -1)]
    public void ComputeRetryScale_SkipsRetry_ForDegenerateImageSize(int width, int height)
    {
        OcrImageScaling.ComputeRetryScale([5], width, height, MaxDimension)
            .Should().Be(1.0);
    }

    [Fact]
    public void MedianLineHeight_IgnoresDegenerateRectangles()
    {
        double[] heights = [0, 10, 20, 30];

        OcrImageScaling.MedianLineHeight(heights).Should().Be(20);
    }

    [Fact]
    public void MedianLineHeight_ReturnsZero_WhenNoUsableHeight()
    {
        OcrImageScaling.MedianLineHeight([0]).Should().Be(0);
        OcrImageScaling.MedianLineHeight([]).Should().Be(0);
    }

    [Fact]
    public void ShouldPreferRetry_PrefersThePassThatRecoveredMoreCharacters()
    {
        const string original = "He o";
        const string retry = "Hello world";

        OcrImageScaling.ShouldPreferRetry(original, retry).Should().BeTrue();
        OcrImageScaling.ShouldPreferRetry(retry, original).Should().BeFalse();
    }

    [Fact]
    public void ShouldPreferRetry_IgnoresWhitespaceDifferences()
    {
        const string original = "你好世界";
        const string retry = "你 好\n世 界";

        OcrImageScaling.ShouldPreferRetry(original, retry).Should().BeFalse();
    }

    [Fact]
    public void MapRect_RestoresOriginalCaptureCoordinates()
    {
        var (x, y, width, height) = OcrImageScaling.MapRect(20, 40, 60, 80, 2.0, 4.0);

        x.Should().Be(10);
        y.Should().Be(10);
        width.Should().Be(30);
        height.Should().Be(20);
    }

    [Fact]
    public void ScaledSize_RoundsAndNeverShrinksTheCapture()
    {
        OcrImageScaling.ScaledSize(101, 51, 2.0).Should().Be((202, 102));
        OcrImageScaling.ScaledSize(10, 10, 0.5).Should().Be((10, 10));
    }

    [Fact]
    public void ScaleBgra_KeepsSolidColorUnchanged()
    {
        var source = new byte[2 * 2 * 4];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 10;
            source[i + 1] = 20;
            source[i + 2] = 30;
            source[i + 3] = 255;
        }

        var scaled = OcrImageScaling.ScaleBgra(source, 2, 2, 6, 6);

        scaled.Should().HaveCount(6 * 6 * 4);
        for (var i = 0; i < scaled.Length; i += 4)
        {
            scaled[i].Should().Be(10);
            scaled[i + 1].Should().Be(20);
            scaled[i + 2].Should().Be(30);
            scaled[i + 3].Should().Be(255);
        }
    }

    [Fact]
    public void ScaleBgra_InterpolatesBetweenNeighbouringPixels()
    {
        // 2x1 image: black then white. Enlarged 4x, the middle samples must ramp
        // between the two instead of staying blocky.
        var source = new byte[]
        {
            0, 0, 0, 255,
            255, 255, 255, 255
        };

        var scaled = OcrImageScaling.ScaleBgra(source, 2, 1, 8, 1);

        scaled[0].Should().Be(0);                     // leftmost clamps to the black pixel
        scaled[^4].Should().Be(255);                  // rightmost clamps to the white pixel
        var middleLeft = scaled[3 * 4];
        var middleRight = scaled[4 * 4];
        middleLeft.Should().BeInRange(1, 254);
        middleRight.Should().BeGreaterThan(middleLeft);
    }

    [Fact]
    public void ScaleBgra_PreservesPixelCentreAlignment_WhenScalingByWholeFactor()
    {
        // A single white pixel at (1,0) of a 3x1 row must stay centred in the middle third.
        var source = new byte[3 * 4];
        source[4] = 255; source[5] = 255; source[6] = 255; source[7] = 255;
        source[3] = 255; source[11] = 255;

        var scaled = OcrImageScaling.ScaleBgra(source, 3, 1, 9, 1);

        scaled[4 * 4].Should().Be(255); // centre of the enlarged middle pixel
        scaled[0].Should().Be(0);
        scaled[8 * 4].Should().Be(0);
    }

    [Fact]
    public void ScaleBgra_RejectsBuffersShorterThanTheDeclaredImage()
    {
        var act = () => OcrImageScaling.ScaleBgra(new byte[8], 2, 2, 4, 4);

        act.Should().Throw<ArgumentException>();
    }
}
