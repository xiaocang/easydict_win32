using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services.ScreenCapture;

internal sealed class SafeGdiObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeGdiObjectHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeGdiObjectHandle(nint handle, bool ownsHandle = true)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    public nint Value => IsClosed || IsInvalid ? IntPtr.Zero : DangerousGetHandle();

    internal static SafeGdiObjectHandle FromCompatibleBitmap(nint hdc, int width, int height)
        => new(CreateCompatibleBitmap(hdc, width, height));

    internal static SafeGdiObjectHandle FromPen(int style, int width, uint color)
        => new(CreatePen(style, width, color));

    internal static SafeGdiObjectHandle FromSolidBrush(uint color)
        => new(CreateSolidBrush(color));

    internal static SafeGdiObjectHandle FromFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName)
    {
        return new SafeGdiObjectHandle(CreateFont(
            height,
            width,
            escapement,
            orientation,
            weight,
            italic,
            underline,
            strikeOut,
            charSet,
            outPrecision,
            clipPrecision,
            quality,
            pitchAndFamily,
            faceName));
    }

    protected override bool ReleaseHandle() => DeleteObject(handle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern nint CreatePen(int iStyle, int cWidth, uint color);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFont(
        int cHeight,
        int cWidth,
        int cEscapement,
        int cOrientation,
        int cWeight,
        uint bItalic,
        uint bUnderline,
        uint bStrikeOut,
        uint iCharSet,
        uint iOutPrecision,
        uint iClipPrecision,
        uint iQuality,
        uint iPitchAndFamily,
        string pszFaceName);
}

internal sealed class SafeCompatibleDcHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeCompatibleDcHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeCompatibleDcHandle(nint handle, bool ownsHandle = true)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    public nint Value => IsClosed || IsInvalid ? IntPtr.Zero : DangerousGetHandle();

    internal static SafeCompatibleDcHandle FromCompatibleDc(nint hdc)
        => new(CreateCompatibleDC(hdc));

    protected override bool ReleaseHandle() => DeleteDC(handle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);
}
