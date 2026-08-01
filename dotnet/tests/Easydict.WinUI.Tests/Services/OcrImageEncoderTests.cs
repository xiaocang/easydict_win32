using System.Runtime.InteropServices.WindowsRuntime;
using Easydict.WinUI.Services;
using FluentAssertions;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class OcrImageEncoderTests
{
    [Fact]
    public async Task ToBase64PngAsync_TreatsGdiCapturePixelsAsOpaque_WhenAlphaByteIsUnused()
    {
        // A 32-bit BI_RGB DIB stores BGR plus an unused high-order byte. GDI may leave
        // that byte at zero even though the captured pixel is visually opaque.
        var base64Png = await OcrImageEncoder.ToBase64PngAsync(
            new byte[] { 0x12, 0x34, 0x56, 0x00 },
            width: 1,
            height: 1);

        using var stream = new InMemoryRandomAccessStream();
        var png = Convert.FromBase64String(base64Png);
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixelProvider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        pixelProvider.DetachPixelData().Should().Equal(0x12, 0x34, 0x56, 0xFF);
    }
}
