using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Finds the ScreenCaptureWindow overlay by enumerating top-level windows and matching
/// the window class name "EasydictScreenCapture". The overlay uses WS_EX_TOPMOST | WS_EX_TOOLWINDOW
/// styles and covers the entire virtual screen.
/// </summary>
public static class ScreenCaptureOverlayFinder
{
    private const string CaptureWindowClassName = "EasydictScreenCapture";
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// Find the screen capture overlay window belonging to the specified process.
    /// Returns IntPtr.Zero if not found.
    /// </summary>
    public static IntPtr Find(uint processId)
    {
        IntPtr found = IntPtr.Zero;
        var classNameBuffer = new char[256];

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId) return true;

            var len = GetClassName(hwnd, classNameBuffer, classNameBuffer.Length);
            if (len <= 0) return true;

            var className = new string(classNameBuffer, 0, len);
            if (className == CaptureWindowClassName)
            {
                found = hwnd;
                return false; // Stop enumeration
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Poll for the screen capture overlay to appear, with timeout.
    /// Returns IntPtr.Zero if not found within the timeout.
    /// </summary>
    public static IntPtr WaitForOverlay(uint processId, TimeSpan timeout, int pollIntervalMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var hwnd = Find(processId);
            if (hwnd != IntPtr.Zero) return hwnd;
            Thread.Sleep(pollIntervalMs);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Wait for the screen capture overlay to disappear, with timeout.
    /// Returns true if the overlay disappeared within the timeout.
    /// </summary>
    public static bool WaitForDismiss(uint processId, TimeSpan timeout, int pollIntervalMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var hwnd = Find(processId);
            if (hwnd == IntPtr.Zero) return true;
            Thread.Sleep(pollIntervalMs);
        }
        return false;
    }

    /// <summary>
    /// Get the bounding rectangle of the overlay window.
    /// </summary>
    public static RECT GetRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out RECT rect);
        return rect;
    }

    /// <summary>
    /// Check whether the overlay has the expected window styles (topmost + tool window).
    /// </summary>
    public static (bool HasToolWindow, bool HasTopmost) GetStyleFlags(IntPtr hwnd)
    {
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        return (
            HasToolWindow: (exStyle & WS_EX_TOOLWINDOW) != 0,
            HasTopmost: (exStyle & WS_EX_TOPMOST) != 0
        );
    }
}
