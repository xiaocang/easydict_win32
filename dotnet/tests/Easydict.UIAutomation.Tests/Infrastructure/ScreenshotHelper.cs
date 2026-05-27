using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Captures screenshots during UI automation tests.
/// Saves to a configurable output directory (default: artifacts/ui-screenshots).
/// </summary>
public static class ScreenshotHelper
{
    private static string? _outputDir;

    /// <summary>
    /// Get or set the screenshot output directory.
    /// Defaults to SCREENSHOT_OUTPUT_DIR env var or artifacts/ui-screenshots
    /// under the repository root.
    /// </summary>
    public static string OutputDir
    {
        get
        {
            if (_outputDir == null)
            {
                _outputDir = ResolveOutputDir();
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
        return CaptureWindowPhysical(window, name);
    }

    /// <summary>
    /// Capture a top-level window using its Win32 physical pixel bounds.
    /// FlaUI element capture can be offset on DPI-scaled desktops because UIA
    /// bounds are logical while screen capture APIs use physical pixels.
    /// </summary>
    public static string CaptureWindowPhysical(Window window, string name)
    {
        EnsureWindowReadyForCapture(window, name);

        var bounds = GetWindowPhysicalBounds(window);
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            var fallback = CaptureWindowViaUia(window, name);
            ValidateSavedScreenshot(fallback, name);
            return fallback;
        }

        bounds = MoveWindowIntoVirtualScreenIfNeeded(window, bounds);
        bounds = IntersectWithVirtualScreen(bounds);

        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            var fallback = CaptureWindowViaUia(window, name);
            ValidateSavedScreenshot(fallback, name);
            return fallback;
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

        var path = SaveBitmap(bitmap, name);
        ValidateSavedScreenshot(path, name);
        return path;
    }

    public static Rectangle GetWindowPhysicalBounds(Window window)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd != IntPtr.Zero)
        {
            if (DwmGetWindowAttribute(
                    hwnd,
                    DwmWindowAttributeExtendedFrameBounds,
                    out var frameBounds,
                    Marshal.SizeOf<WindowRect>()) == 0)
            {
                var frame = ToRectangle(frameBounds);
                if (frame.Width > 1 && frame.Height > 1)
                {
                    return frame;
                }
            }

            if (GetWindowRect(hwnd, out var rect))
            {
                return ToRectangle(rect);
            }
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
        var path = SaveCapture(image, name);
        ValidateSavedScreenshot(path, name);
        return path;
    }

    private static string CaptureWindowViaUia(Window window, string name)
    {
        var image = Capture.Element(window);
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

    private static void ValidateSavedScreenshot(string filePath, string name)
    {
        try
        {
            using var image = Image.FromFile(filePath);
            if (image.Width <= 1 || image.Height <= 1)
            {
                throw new InvalidOperationException(
                    $"Screenshot '{name}' captured an unusable {image.Width}x{image.Height} image at {filePath}.");
            }
        }
        catch (OutOfMemoryException ex)
        {
            throw new InvalidOperationException($"Screenshot '{name}' was not a valid image: {filePath}", ex);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ResolveOutputDir()
    {
        var configured = Environment.GetEnvironmentVariable("SCREENSHOT_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(FindRepositoryRoot(), "artifacts", "ui-screenshots");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = Path.GetFullPath(start);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(Path.Combine(current, ".git")) ||
                    File.Exists(Path.Combine(current, ".git")))
                {
                    return current;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent ?? string.Empty;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static Rectangle MoveWindowIntoVirtualScreenIfNeeded(Window window, Rectangle bounds)
    {
        var virtualScreen = GetVirtualScreenBounds();
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0 ||
            bounds.Width <= 0 || bounds.Height <= 0 ||
            virtualScreen.Contains(bounds))
        {
            return bounds;
        }

        var adjustedLeft = bounds.Left;
        var adjustedTop = bounds.Top;

        if (bounds.Width <= virtualScreen.Width)
        {
            adjustedLeft = Math.Min(
                Math.Max(bounds.Left, virtualScreen.Left),
                virtualScreen.Right - bounds.Width);
        }

        if (bounds.Height <= virtualScreen.Height)
        {
            adjustedTop = Math.Min(
                Math.Max(bounds.Top, virtualScreen.Top),
                virtualScreen.Bottom - bounds.Height);
        }

        if (adjustedLeft == bounds.Left && adjustedTop == bounds.Top)
        {
            return bounds;
        }

        if (TrySetWindowPhysicalBounds(
                window,
                new Rectangle(adjustedLeft, adjustedTop, bounds.Width, bounds.Height)))
        {
            Thread.Sleep(300);
            window.SetForeground();
            Thread.Sleep(150);
            return GetWindowPhysicalBounds(window);
        }

        return bounds;
    }

    private static void EnsureWindowReadyForCapture(Window window, string name)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            window.SetForeground();
            Thread.Sleep(300);
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, ShowWindowRestore);
            }

            BringWindowToTop(hwnd);
            SetWindowPos(
                hwnd,
                HwndTopMost,
                0,
                0,
                0,
                0,
                SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
            SetWindowPos(
                hwnd,
                HwndNoTopMost,
                0,
                0,
                0,
                0,
                SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
            SetForegroundWindow(hwnd);

            try
            {
                window.SetForeground();
            }
            catch
            {
                // Native foreground activation above is the primary path.
            }

            Thread.Sleep(250);
            if (IsForegroundWindowOrRoot(hwnd))
            {
                Thread.Sleep(150);
                return;
            }
        }

        var foreground = GetForegroundWindow();
        throw new InvalidOperationException(
            $"Screenshot '{name}' would capture the wrong window: target HWND=0x{hwnd.ToInt64():X}, foreground HWND=0x{foreground.ToInt64():X}.");
    }

    private static bool IsForegroundWindowOrRoot(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd)
        {
            return true;
        }

        return foreground != IntPtr.Zero && GetAncestor(foreground, GetAncestorRoot) == hwnd;
    }

    private static Rectangle IntersectWithVirtualScreen(Rectangle bounds)
    {
        var virtualScreen = GetVirtualScreenBounds();
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            return bounds;
        }

        return Rectangle.Intersect(bounds, virtualScreen);
    }

    public static Rectangle GetVirtualScreenBounds()
    {
        return new Rectangle(
            GetSystemMetrics(SystemMetricVirtualScreenX),
            GetSystemMetrics(SystemMetricVirtualScreenY),
            GetSystemMetrics(SystemMetricVirtualScreenWidth),
            GetSystemMetrics(SystemMetricVirtualScreenHeight));
    }

    private static Rectangle ToRectangle(WindowRect rect)
    {
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rect);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        out WindowRect pvAttribute,
        int cbAttribute);

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
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosShowWindow = 0x0040;
    private const int DwmWindowAttributeExtendedFrameBounds = 9;
    private const int SystemMetricVirtualScreenX = 76;
    private const int SystemMetricVirtualScreenY = 77;
    private const int SystemMetricVirtualScreenWidth = 78;
    private const int SystemMetricVirtualScreenHeight = 79;
    private const int ShowWindowRestore = 9;
    private const uint GetAncestorRoot = 2;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WindowRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
