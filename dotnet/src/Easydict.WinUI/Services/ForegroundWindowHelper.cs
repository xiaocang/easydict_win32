using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Easydict.WinUI.Services;

internal static class ForegroundWindowHelper
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    public static bool AllowCurrentProcessToSetForeground(string owner)
    {
        var allowed = AllowSetForegroundWindow(GetCurrentProcessId());
        Debug.WriteLine($"[{owner}] AllowSetForegroundWindow(currentPid): {allowed}");
        return allowed;
    }

    public static bool TryBringToFront(Window window, string owner)
    {
        var targetHwnd = WindowNative.GetWindowHandle(window);
        if (targetHwnd == IntPtr.Zero)
        {
            return false;
        }

        if (GetForegroundWindow() == targetHwnd)
        {
            Debug.WriteLine($"[{owner}] Window is already in the foreground");
            return true;
        }

        var foregroundSet = SetForegroundWindow(targetHwnd);
        Debug.WriteLine($"[{owner}] SetForegroundWindow: {foregroundSet}");
        return foregroundSet;
    }

}
