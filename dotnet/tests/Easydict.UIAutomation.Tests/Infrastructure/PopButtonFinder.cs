using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Finds the PopButton window by enumerating all top-level windows and filtering by
/// process ID, extended window styles (WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE),
/// and size (≤ 50x50 physical pixels to account for DPI scaling of the 30x30 logical popup).
///
/// FlaUI's GetAllTopLevelWindows() may miss the PopButton because WS_EX_TOOLWINDOW windows
/// are excluded from the taskbar and some enumeration APIs. Using EnumWindows directly
/// ensures reliable discovery.
/// </summary>
public static class PopButtonFinder
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// Maximum physical pixel size for the PopButton window.
    /// At 150% DPI, 30 logical pixels = 45 physical pixels; 50 provides margin.
    /// </summary>
    private const int MaxPopButtonSize = 50;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongPtr(IntPtr hWnd, int nIndex);

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
        public int CenterX => (Left + Right) / 2;
        public int CenterY => (Top + Bottom) / 2;
    }

    /// <summary>
    /// Find the PopButton window belonging to the specified process.
    /// Returns IntPtr.Zero if not found.
    /// </summary>
    public static IntPtr Find(uint processId)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId) return true;

            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            bool hasToolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;
            bool hasTopmost = (exStyle & WS_EX_TOPMOST) != 0;
            bool hasNoActivate = (exStyle & WS_EX_NOACTIVATE) != 0;

            if (hasToolWindow && hasTopmost && hasNoActivate)
            {
                if (GetWindowRect(hwnd, out RECT rect))
                {
                    if (rect.Width > 0 && rect.Height > 0 &&
                        rect.Width <= MaxPopButtonSize && rect.Height <= MaxPopButtonSize)
                    {
                        found = hwnd;
                        return false; // Stop enumeration
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Poll for the PopButton window to appear, with timeout.
    /// Returns IntPtr.Zero if not found within the timeout.
    /// </summary>
    public static IntPtr WaitForPopButton(uint processId, TimeSpan timeout, int pollIntervalMs = 100)
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
    /// Get the bounding rectangle of a window.
    /// </summary>
    public static RECT GetRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out RECT rect);
        return rect;
    }

    /// <summary>
    /// Check whether a window is currently visible.
    /// </summary>
    public static bool IsVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

    /// <summary>
    /// Get the extended window style flags for a window.
    /// </summary>
    public static int GetExtendedStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE);

    /// <summary>
    /// Assert that the PopButton has the expected extended window styles.
    /// Returns (hasNoActivate, hasToolWindow, hasTopmost) for assertions.
    /// </summary>
    public static (bool HasNoActivate, bool HasToolWindow, bool HasTopmost) GetStyleFlags(IntPtr hwnd)
    {
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        return (
            HasNoActivate: (exStyle & WS_EX_NOACTIVATE) != 0,
            HasToolWindow: (exStyle & WS_EX_TOOLWINDOW) != 0,
            HasTopmost: (exStyle & WS_EX_TOPMOST) != 0
        );
    }
}
