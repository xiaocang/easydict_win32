using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>Finds actual secondary windows owned by the app, including NOACTIVATE tool windows.</summary>
internal static class OnboardingNativeWindowFinder
{
    internal static IntPtr WaitForCapture(AppLauncher app)
    {
        var handle = IntPtr.Zero;
        var result = Retry.WhileFalse(() =>
        {
            handle = VisibleWindows(app.Application.ProcessId)
                .FirstOrDefault(hwnd => ClassName(hwnd).StartsWith("EasydictScreenCapture_", StringComparison.Ordinal));
            return handle != IntPtr.Zero;
        }, TimeSpan.FromSeconds(8));
        result.Success.Should().BeTrue("the real fullscreen ScreenCaptureWindow must be visible");
        return handle;
    }

    internal static AutomationElement? FindMiniPreview(AppLauncher app)
    {
        foreach (var hwnd in VisibleWindows(app.Application.ProcessId))
        {
            try
            {
                var window = app.Automation.FromHandle(hwnd);
                var preview = window.FindFirstDescendant(c => c.ByAutomationId("OnboardingMiniPreviewSource"));
                if (preview is not null && !preview.IsOffscreen) return window;
            }
            catch (COMException) { /* Window can close between enumeration and the UIA call. */ }
        }
        return null;
    }

    internal static AutomationElement WaitForMiniPreview(AppLauncher app, string expectedTranslation)
    {
        var result = Retry.WhileNull(() =>
        {
            var window = FindMiniPreview(app);
            var text = window?.FindFirstDescendant(c => c.ByName(expectedTranslation));
            return text is { IsOffscreen: false } ? window : null;
        }, TimeSpan.FromSeconds(3));
        result.Success.Should().BeTrue("the actual MiniWindow must show the offline translated text, not just an empty window");
        return result.Result!;
    }

    internal static bool HasCapture(AppLauncher app) => VisibleWindows(app.Application.ProcessId)
        .Any(hwnd => ClassName(hwnd).StartsWith("EasydictScreenCapture_", StringComparison.Ordinal));

    private static List<IntPtr> VisibleWindows(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == processId && IsWindowVisible(hwnd)) windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int capacity);
}
