using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Captures screenshots during UI automation tests.
/// Saves to a configurable output directory (default: ./screenshots).
/// </summary>
public static class ScreenshotHelper
{
    private static string? _outputDir;

    /// <summary>
    /// Get or set the screenshot output directory.
    /// Defaults to SCREENSHOT_OUTPUT_DIR env var or ./screenshots.
    /// </summary>
    public static string OutputDir
    {
        get
        {
            if (_outputDir == null)
            {
                _outputDir = Environment.GetEnvironmentVariable("SCREENSHOT_OUTPUT_DIR")
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "screenshots");
                Directory.CreateDirectory(_outputDir);
            }
            return _outputDir;
        }
        set
        {
            _outputDir = value;
            Directory.CreateDirectory(_outputDir);
        }
    }

    /// <summary>
    /// Capture the entire screen and save with the given name.
    /// </summary>
    public static string CaptureScreen(string name)
    {
        var image = Capture.Screen();
        return SaveCapture(image, name);
    }

    /// <summary>
    /// Capture a specific window and save with the given name.
    /// </summary>
    public static string CaptureWindow(Window window, string name)
    {
        // Focus the window first to ensure it's visible
        window.SetForeground();
        Thread.Sleep(300); // Allow render

        var image = Capture.Element(window);
        return SaveCapture(image, name);
    }

    /// <summary>
    /// Capture a top-level window using its Win32 physical pixel bounds.
    /// FlaUI element capture can be offset on DPI-scaled desktops because UIA
    /// bounds are logical while screen capture APIs use physical pixels.
    /// </summary>
    public static string CaptureWindowPhysical(Window window, string name)
    {
        window.SetForeground();
        Thread.Sleep(300);

        var bounds = GetWindowPhysicalBounds(window);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return CaptureWindow(window, name);
        }

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bounds.Size,
                CopyPixelOperation.SourceCopy);
        }

        return SaveBitmap(bitmap, name);
    }

    public static Rectangle GetWindowPhysicalBounds(Window window)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        var bounds = window.BoundingRectangle;
        return Rectangle.FromLTRB(
            (int)Math.Round(Convert.ToDouble(bounds.Left)),
            (int)Math.Round(Convert.ToDouble(bounds.Top)),
            (int)Math.Round(Convert.ToDouble(bounds.Right)),
            (int)Math.Round(Convert.ToDouble(bounds.Bottom)));
    }

    public static bool TrySetWindowPhysicalBounds(Window window, Rectangle bounds)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        return hwnd != IntPtr.Zero && SetWindowPos(
            hwnd,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SetWindowPosNoZOrder | SetWindowPosNoActivate);
    }

    public static double GetWindowDpiScale(Window window)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return 1d;
        }

        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1d : dpi / 96d;
    }

    /// <summary>
    /// Capture a specific element and save with the given name.
    /// </summary>
    public static string CaptureElement(AutomationElement element, string name)
    {
        var image = Capture.Element(element);
        return SaveCapture(image, name);
    }

    private static string SaveCapture(CaptureImage capture, string name)
    {
        var sanitized = SanitizeFileName(name);
        var filePath = Path.Combine(OutputDir, $"{sanitized}.png");
        capture.ToFile(filePath);
        return filePath;
    }

    private static string SaveBitmap(Bitmap bitmap, string name)
    {
        var sanitized = SanitizeFileName(name);
        var filePath = Path.Combine(OutputDir, $"{sanitized}.png");
        bitmap.Save(filePath, ImageFormat.Png);
        return filePath;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WindowRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
