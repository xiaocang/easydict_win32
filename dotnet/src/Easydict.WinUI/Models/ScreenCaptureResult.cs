namespace Easydict.WinUI.Models;

/// <summary>
/// Result of a screen capture operation, containing the captured bitmap bytes
/// and the region that was captured.
/// </summary>
public sealed class ScreenCaptureResult : IDisposable
{
    private OwnedPixelBuffer? _pixelBuffer;

    /// <summary>
    /// Raw BGRX pixel data from a 32-bit BI_RGB DIB. The fourth byte is unused
    /// and must be ignored by image consumers rather than treated as transparency.
    public ReadOnlyMemory<byte> PixelMemory
    {
        get
        {
            var pixelBuffer = _pixelBuffer ?? throw new ObjectDisposedException(nameof(ScreenCaptureResult));
            return pixelBuffer.Memory;
        }
    }

    /// <summary>
    /// Owns the raw BGRA8 pixel data for this capture.
    /// </summary>
    public required OwnedPixelBuffer PixelBuffer
    {
        get => _pixelBuffer ?? throw new ObjectDisposedException(nameof(ScreenCaptureResult));
        init => _pixelBuffer = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Width of the captured image in physical pixels.
    /// </summary>
    public int PixelWidth { get; init; }

    /// <summary>
    /// Height of the captured image in physical pixels.
    /// </summary>
    public int PixelHeight { get; init; }

    /// <summary>
    /// The screen region that was captured, in physical pixels (virtual desktop coordinates).
    /// </summary>
    public OcrRect ScreenRect { get; init; }

    public void Dispose()
    {
        var pixelBuffer = Interlocked.Exchange(ref _pixelBuffer, null);
        pixelBuffer?.Dispose();
    }
}
