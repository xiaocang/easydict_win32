namespace Easydict.WinUI.Models;

/// <summary>
/// Result of a screen capture operation, containing the captured bitmap bytes
/// and the region that was captured.
/// </summary>
public sealed class ScreenCaptureResult : IDisposable
{
    /// <summary>
    /// Raw BGRA8 pixel data of the captured region.
    /// </summary>
    public byte[] PixelData { get; init; } = [];

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
        // Currently no unmanaged resources; placeholder for future bitmap handle cleanup.
    }
}
