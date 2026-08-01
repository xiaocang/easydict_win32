using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Easydict.WinUI.Services.Memory;

namespace Easydict.WinUI.Services;

/// <summary>
/// Shared screenshot encoder for API OCR backends (Ollama + Custom API).
/// PNG keeps UI text edges intact and is broadly accepted as a vision data-URL;
/// uncompressed BMP is rejected by some cloud backends (e.g. Ollama Cloud).
/// </summary>
internal static class OcrImageEncoder
{
    public const string MimeType = "image/png";

    public const string DataUrlPrefix = "data:image/png;base64,";

    /// <summary>
    /// Convert BGRA8 pixel data to a base64-encoded PNG string (no data-URL prefix).
    /// </summary>
    public static async Task<string> ToBase64PngAsync(
        ReadOnlyMemory<byte> pixelData,
        int width,
        int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

        byte[]? temporaryPixels = null;
        try
        {
            var pixels = PixelMemory.ToArrayForInterop(pixelData, out var offset, out var length);
            if (offset != 0 || length != pixels.Length)
            {
                temporaryPixels = pixelData.ToArray();
                pixels = temporaryPixels;
            }

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)width,
                (uint)height,
                96,
                96,
                pixels);
        }
        finally
        {
            if (temporaryPixels is not null)
            {
                Array.Clear(temporaryPixels);
            }
        }

        await encoder.FlushAsync();

        var streamSize = stream.Size;
        if (streamSize > int.MaxValue)
        {
            throw new InvalidOperationException("Encoded image is too large to convert to Base64.");
        }

        var size = (int)streamSize;
        stream.Seek(0);

        var bytes = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            await stream.ReadAsync(bytes.AsBuffer(0, size), (uint)size, InputStreamOptions.None);
            return Convert.ToBase64String(bytes, 0, size);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    public static string ToDataUrl(string base64Png) => $"{DataUrlPrefix}{base64Png}";
}
