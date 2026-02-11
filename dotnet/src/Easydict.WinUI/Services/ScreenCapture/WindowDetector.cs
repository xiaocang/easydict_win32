using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services.ScreenCapture;

/// <summary>
/// Detects windows and UI elements under the mouse cursor using a Z-order snapshot.
/// Inspired by Snipaste's window detection: on capture start, enumerate all visible
/// windows (and their children) once and cache their rects. Subsequent hit-tests use
/// the cached snapshot, so changes to underlying windows don't cause flicker.
/// </summary>
public sealed class WindowDetector
{
    private readonly List<WindowInfo> _topLevelWindows = new();
    private nint _ownWindowHandle;

    /// <summary>
    /// Information about a detected window.
    /// </summary>
    public sealed class WindowInfo
    {
        public nint Hwnd { get; init; }
        public RECT Rect { get; init; }
        public List<WindowInfo> Children { get; } = new();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;

        public readonly bool Contains(int x, int y)
            => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    /// <summary>
    /// Takes a snapshot of all visible windows on the desktop.
    /// Call this once when entering screenshot mode.
    /// </summary>
    /// <param name="ownWindowHandle">Handle of our overlay window to exclude from detection.</param>
    public void TakeSnapshot(nint ownWindowHandle)
    {
        _ownWindowHandle = ownWindowHandle;
        _topLevelWindows.Clear();

        EnumWindows(EnumWindowProc, IntPtr.Zero);

        Debug.WriteLine($"[WindowDetector] Snapshot: {_topLevelWindows.Count} top-level windows");
    }

    /// <summary>
    /// Finds the best-matching window region at the given screen point.
    /// </summary>
    /// <param name="screenX">X coordinate in physical pixels (virtual desktop).</param>
    /// <param name="screenY">Y coordinate in physical pixels (virtual desktop).</param>
    /// <param name="depth">
    /// Detection depth: 0 = deepest child, positive = go up N levels toward parent.
    /// Use mouse scroll to adjust depth (Snipaste-style).
    /// </param>
    /// <returns>The bounding RECT of the matched window/element, or null if none found.</returns>
    public RECT? FindRegionAtPoint(int screenX, int screenY, int depth = 0)
    {
        // Find the first top-level window (by Z-order) that contains the point
        foreach (var win in _topLevelWindows)
        {
            if (!win.Rect.Contains(screenX, screenY))
                continue;

            // Build a chain from top-level → deepest child
            var chain = new List<WindowInfo> { win };
            BuildChildChain(win.Children, screenX, screenY, chain);

            // depth=0 → deepest; depth=1 → one level up, etc.
            var targetIndex = Math.Max(0, chain.Count - 1 - depth);
            return chain[targetIndex].Rect;
        }

        return null;
    }

    /// <summary>
    /// Gets the maximum depth (number of child levels) at the given point.
    /// Used to clamp the scroll depth value.
    /// </summary>
    public int GetMaxDepthAtPoint(int screenX, int screenY)
    {
        foreach (var win in _topLevelWindows)
        {
            if (!win.Rect.Contains(screenX, screenY))
                continue;

            var chain = new List<WindowInfo> { win };
            BuildChildChain(win.Children, screenX, screenY, chain);
            return chain.Count - 1;
        }
        return 0;
    }

    /// <summary>
    /// Adds a window to the snapshot for testing purposes.
    /// </summary>
    internal void AddWindow(WindowInfo window) => _topLevelWindows.Add(window);

    private static void BuildChildChain(List<WindowInfo> children, int x, int y, List<WindowInfo> chain)
    {
        foreach (var child in children)
        {
            if (child.Rect.Contains(x, y))
            {
                chain.Add(child);
                BuildChildChain(child.Children, x, y, chain);
                return; // Take the first (topmost) matching child
            }
        }
    }

    private bool EnumWindowProc(nint hwnd, nint lParam)
    {
        if (hwnd == _ownWindowHandle) return true;
        if (!IsWindowVisible(hwnd)) return true;

        // Skip desktop background windows (Progman = desktop, WorkerW = wallpaper worker)
        var className = GetWindowClassName(hwnd);
        if (className is "Progman" or "WorkerW")
            return true;

        // Skip windows with zero size
        if (!GetWindowRect(hwnd, out var rect)) return true;
        if (rect.Width <= 0 || rect.Height <= 0) return true;

        var info = new WindowInfo { Hwnd = hwnd, Rect = rect };

        // Enumerate child windows
        EnumChildWindows(hwnd, (childHwnd, _) =>
        {
            if (!IsWindowVisible(childHwnd)) return true;
            if (!GetWindowRect(childHwnd, out var childRect)) return true;
            if (childRect.Width <= 0 || childRect.Height <= 0) return true;

            info.Children.Add(new WindowInfo { Hwnd = childHwnd, Rect = childRect });
            return true;
        }, IntPtr.Zero);

        _topLevelWindows.Add(info);
        return true;
    }

    private static string GetWindowClassName(nint hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // P/Invoke declarations
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);
}
