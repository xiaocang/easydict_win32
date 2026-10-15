using Easydict.WinUI.Services;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class TrayIconOptionLayoutTests
{
    [Theory]
    [InlineData(308, 4, 48)]
    [InlineData(336, 4, 55)]
    [InlineData(672, 4, 139)]
    [InlineData(800, 4, 171)]
    [InlineData(2000, 4, 320)]
    public void Compute_KeepsFourColumnsWhenEachPreviewStaysReadable(double width, int columns, double image)
    {
        var layout = TrayIconOptionLayout.Compute(width);

        Assert.Equal(columns, layout.Columns);
        Assert.Equal(image, layout.ImageSize);
    }

    [Theory]
    [InlineData(307, 2, 127)]
    [InlineData(200, 2, 74)]
    [InlineData(112, 2, 30)]
    [InlineData(40, 2, 1)]
    [InlineData(0, 2, 1)]
    public void Compute_WrapsAndClampsWhenFourPreviewsWouldClip(double width, int columns, double image)
    {
        var layout = TrayIconOptionLayout.Compute(width);

        Assert.Equal(columns, layout.Columns);
        Assert.Equal(image, layout.ImageSize);
    }
}
