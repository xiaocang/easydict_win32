using System.Drawing;
using Easydict.WinUI.Services;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class ThemedIconServiceTests
{
    [Theory]
    [InlineData(48)]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void OutlineKeepsAllFourArtworkCornersRegardlessOfSourceDpi(float dpi)
    {
        using var source = new Bitmap(32, 32);
        source.SetResolution(dpi, dpi);
        var corners = new[] { new Point(3, 3), new Point(28, 3), new Point(3, 28), new Point(28, 28) };
        var colors = new[] { Color.Red, Color.Lime, Color.Blue, Color.Magenta };
        for (var i = 0; i < corners.Length; i++) source.SetPixel(corners[i].X, corners[i].Y, colors[i]);
        using var result = Easydict.IconTools.IconRasterizer.Render(source, source.Width, true);
        Assert.Equal(source.Size, result.Size);
        for (var i = 0; i < corners.Length; i++)
            Assert.Equal(colors[i].ToArgb(), result.GetPixel(corners[i].X, corners[i].Y).ToArgb());
        Assert.Equal(0, result.GetPixel(0, 0).A);
    }

    [Theory]
    [InlineData("Dark", false, true)]
    [InlineData("Dark", true, true)]
    [InlineData("Light", false, false)]
    [InlineData("Light", true, false)]
    [InlineData("Minimal", true, false)]
    [InlineData("System", false, false)]
    [InlineData("System", true, true)]
    public void WindowIconFollowsExplicitAppTheme(string theme, bool systemDark, bool expected)
        => Assert.Equal(expected, ThemedIconService.UseDarkWindowIcon(theme, systemDark));

    [Fact]
    public void DarkOutlinePreservesArtworkAndTransparentBackground()
    {
        using var source = new Bitmap(9, 9);
        var brand = Color.FromArgb(255, 0, 40, 80);
        source.SetPixel(4, 4, brand);
        using var result = Easydict.IconTools.IconRasterizer.Render(source, source.Width, true);
        Assert.Equal(brand.ToArgb(), result.GetPixel(4, 4).ToArgb());
        Assert.Equal(255, result.GetPixel(3, 4).A);
        Assert.True(result.GetPixel(3, 4).GetBrightness() > 0.9f);
        Assert.Equal(0, result.GetPixel(0, 0).A);
        Assert.Equal(0, result.GetPixel(2, 4).A);
        Assert.Equal(0, source.GetPixel(3, 4).A);
    }
}
