using System;
using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services;

/// <summary>
/// Provides utilities for handling DPI scaling in WinUI applications.
/// Converts between DIPs (Device-Independent Pixels) and physical pixels.
/// </summary>
/// <remarks>
/// WinUI 3 uses DIPs for XAML layout, but Win32 APIs (AppWindow.Move, AppWindow.Resize)
/// require physical pixels. This helper bridges the gap.
/// See: https://learn.microsoft.com/en-us/windows/win32/hidpi/
/// </remarks>
public static partial class DpiHelper
{
    private const uint StandardDpi = 96; // 96 DPI = 100% scale

    /// <summary>
    /// Gets the DPI for a window using Win32 API.
    /// </summary>
    /// <param name="hwnd">Window handle obtained from WindowNative.GetWindowHandle()</param>
    /// <returns>DPI value (e.g., 96 for 100%, 144 for 150%, 192 for 200%)</returns>
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Calculates the DPI scale factor for a window.
    /// </summary>
    /// <param name="hwnd">Window handle</param>
    /// <returns>Scale factor (1.0 = 100%, 1.25 = 125%, 1.5 = 150%, 2.0 = 200%)</returns>
    public static double GetScaleFactorForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 1.0; // Fallback to 100% if no valid window handle
        }

        var dpi = GetDpiForWindow(hwnd);
        return DpiToScaleFactor(dpi);
    }

    /// <summary>
    /// Converts a DPI value to a scale factor.
    /// </summary>
    /// <param name="dpi">DPI value (e.g., 96, 120, 144, 192)</param>
    /// <returns>Scale factor (e.g., 1.0, 1.25, 1.5, 2.0)</returns>
    public static double DpiToScaleFactor(uint dpi)
    {
        return (double)dpi / StandardDpi;
    }

    /// <summary>
    /// Converts device-independent pixels (DIPs) to physical pixels based on DPI scale.
    /// </summary>
    /// <param name="dips">Value in DIPs (1 DIP = 1 pixel at 96 DPI / 100% scale)</param>
    /// <param name="scaleFactor">DPI scale factor (1.0 = 100%, 2.0 = 200%)</param>
    /// <returns>Value in physical pixels</returns>
    /// <remarks>
    /// AppWindow APIs (Move, Resize) require physical pixels, while XAML uses DIPs.
    /// This conversion ensures consistent sizing across different DPI monitors.
    /// Example: 600 DIPs at 150% scale = 900 physical pixels
    /// </remarks>
    public static int DipsToPhysicalPixels(double dips, double scaleFactor)
    {
        return (int)Math.Round(dips * scaleFactor);
    }

    /// <summary>
    /// Converts physical pixels to device-independent pixels (DIPs) based on DPI scale.
    /// </summary>
    /// <param name="pixels">Value in physical pixels</param>
    /// <param name="scaleFactor">DPI scale factor (1.0 = 100%, 2.0 = 200%)</param>
    /// <returns>Value in DIPs</returns>
    /// <remarks>
    /// Use this when reading window dimensions from AppWindow.Size to store in DPI-independent format.
    /// Example: 900 physical pixels at 150% scale = 600 DIPs
    /// </remarks>
    public static double PhysicalPixelsToDips(int pixels, double scaleFactor)
    {
        if (scaleFactor == 0)
        {
            return pixels; // Avoid division by zero
        }

        return pixels / scaleFactor;
    }

    /// <summary>
    /// Gets the rasterization scale from XamlRoot.
    /// </summary>
    /// <param name="xamlRoot">XamlRoot from a WinUI element</param>
    /// <returns>Rasterization scale (1.0 = 100%, 2.0 = 200%)</returns>
    /// <remarks>
    /// XamlRoot.RasterizationScale provides the current DPI scale for XAML elements.
    /// This is an alternative to Win32 GetDpiForWindow when you have access to XamlRoot.
    /// </remarks>
    public static double GetRasterizationScale(Microsoft.UI.Xaml.XamlRoot? xamlRoot)
    {
        return xamlRoot?.RasterizationScale ?? 1.0;
    }
}
