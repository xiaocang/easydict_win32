using System.Runtime.InteropServices;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services.ScreenCapture;

/// <summary>
/// Headless GDI capture of a small screen rectangle (no overlay window), used by hover word
/// lookup's OCR fallback to read the text around the pointer. Safe to call from any thread.
/// Coordinates are physical pixels in virtual-desktop space (the app is PerMonitorV2 aware).
/// </summary>
internal static class ScreenRegionCapture
{
    /// <summary>Half width of the capture box around the pointer, in DIPs.</summary>
    internal const int HalfWidthDips = 160;

    /// <summary>Half height of the capture box around the pointer, in DIPs.</summary>
    internal const int HalfHeightDips = 32;

    /// <summary>Smallest capture box edge, in physical pixels.</summary>
    internal const int MinCaptureSize = 48;

    private const int SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// Compute the capture rectangle around the pointer: a <see cref="HalfWidthDips"/> ×
    /// <see cref="HalfHeightDips"/> DIP box scaled by <paramref name="scale"/>, clamped to the
    /// virtual screen and cut back on the side where <paramref name="exclude"/> (the visible
    /// popup) overlaps it, so the pointer always stays inside the result. Pure logic.
    /// </summary>
    internal static OcrRect ComputeCaptureRect(
        int cursorX,
        int cursorY,
        double scale,
        OcrRect virtualScreen,
        OcrRect? exclude)
    {
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            scale = 1.0;
        }

        var halfWidth = Math.Max(MinCaptureSize / 2, (int)Math.Round(HalfWidthDips * scale));
        var halfHeight = Math.Max(MinCaptureSize / 2, (int)Math.Round(HalfHeightDips * scale));

        double left = cursorX - halfWidth;
        double top = cursorY - halfHeight;
        double right = cursorX + halfWidth;
        double bottom = cursorY + halfHeight;

        if (!virtualScreen.IsEmpty())
        {
            left = Math.Max(left, virtualScreen.X);
            top = Math.Max(top, virtualScreen.Y);
            right = Math.Min(right, virtualScreen.Right());
            bottom = Math.Min(bottom, virtualScreen.Bottom());
        }

        if (exclude is { } excluded && !excluded.IsEmpty())
        {
            var candidate = new OcrRect(left, top, right - left, bottom - top);
            if (candidate.Intersects(excluded))
            {
                // Cut the box on the side the popup is on, keeping the pointer inside.
                if (excluded.Bottom() <= cursorY && excluded.Bottom() > top)
                {
                    top = excluded.Bottom();
                }
                else if (excluded.Y >= cursorY && excluded.Y < bottom)
                {
                    bottom = excluded.Y;
                }
                else if (excluded.Right() <= cursorX && excluded.Right() > left)
                {
                    left = excluded.Right();
                }
                else if (excluded.X >= cursorX && excluded.X < right)
                {
                    right = excluded.X;
                }
            }
        }

        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        return new OcrRect(Math.Floor(left), Math.Floor(top), Math.Ceiling(width), Math.Ceiling(height));
    }

    /// <summary>
    /// Bounds of the virtual screen (all monitors) in physical pixels.
    /// </summary>
    internal static OcrRect GetVirtualScreenBounds()
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new OcrRect(left, top, width, height);
    }

    /// <summary>
    /// Capture <paramref name="rect"/> from the screen into a BGRX buffer, optionally upscaling
    /// by an integer factor (HALFTONE stretch) so small UI text OCRs better.
    /// The returned <see cref="ScreenCaptureResult.ScreenRect"/> is the unscaled source rect;
    /// pixel coordinates in the result are <paramref name="upscale"/> times the source offsets.
    /// </summary>
    internal static ScreenCaptureResult Capture(OcrRect rect, int upscale = 1)
    {
        var srcX = (int)Math.Floor(rect.X);
        var srcY = (int)Math.Floor(rect.Y);
        var srcWidth = Math.Max(1, (int)Math.Ceiling(rect.Width));
        var srcHeight = Math.Max(1, (int)Math.Ceiling(rect.Height));
        upscale = Math.Clamp(upscale, 1, 4);
        var dstWidth = srcWidth * upscale;
        var dstHeight = srcHeight * upscale;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(NULL) failed.");
        }

        try
        {
            using var memDc = SafeCompatibleDcHandle.FromCompatibleDc(screenDc);
            using var bitmap = SafeGdiObjectHandle.FromCompatibleBitmap(screenDc, dstWidth, dstHeight);
            if (memDc.IsInvalid || bitmap.IsInvalid)
            {
                throw new InvalidOperationException("Failed to create GDI capture surfaces.");
            }

            var oldBitmap = SelectObject(memDc.Value, bitmap.Value);
            OwnedPixelBuffer? pixelBuffer = null;
            try
            {
                bool blitOk;
                if (upscale == 1)
                {
                    blitOk = BitBlt(memDc.Value, 0, 0, srcWidth, srcHeight, screenDc, srcX, srcY, SRCCOPY);
                }
                else
                {
                    SetStretchBltMode(memDc.Value, HALFTONE);
                    SetBrushOrgEx(memDc.Value, 0, 0, IntPtr.Zero);
                    blitOk = StretchBlt(
                        memDc.Value, 0, 0, dstWidth, dstHeight,
                        screenDc, srcX, srcY, srcWidth, srcHeight, SRCCOPY);
                }

                if (!blitOk)
                {
                    throw new InvalidOperationException("Screen BitBlt failed.");
                }

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = dstWidth,
                        biHeight = -dstHeight, // Top-down
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0 // BI_RGB
                    }
                };

                pixelBuffer = OwnedPixelBuffer.Rent(dstWidth * dstHeight * 4);
                if (!MemoryMarshal.TryGetArray<byte>(pixelBuffer.Memory, out var segment) ||
                    segment.Array is null ||
                    segment.Offset != 0)
                {
                    throw new InvalidOperationException("Capture pixel buffer must be backed by a zero-offset array.");
                }

                var copiedLines = GetDIBits(memDc.Value, bitmap.Value, 0, (uint)dstHeight, segment.Array, ref bmi, 0);
                if (copiedLines == 0)
                {
                    throw new InvalidOperationException("GetDIBits failed.");
                }

                var result = new ScreenCaptureResult
                {
                    PixelBuffer = pixelBuffer,
                    PixelWidth = dstWidth,
                    PixelHeight = dstHeight,
                    ScreenRect = new OcrRect(srcX, srcY, srcWidth, srcHeight)
                };
                pixelBuffer = null;
                return result;
            }
            finally
            {
                pixelBuffer?.Dispose();
                if (oldBitmap != IntPtr.Zero)
                {
                    SelectObject(memDc.Value, oldBitmap);
                }
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // bmiColors intentionally omitted for 32-bit BGRX
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint hdcDest, int xDest, int yDest, int wDest, int hDest, nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(nint hdc, int x, int y, nint lppt);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);
}
