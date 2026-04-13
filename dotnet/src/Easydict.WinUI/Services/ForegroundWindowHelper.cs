using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Easydict.WinUI.Services;

internal static class ForegroundWindowHelper
{
    private const byte VkMenu = 0x12;
    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

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

        var foregroundHwnd = GetForegroundWindow();
        if (foregroundHwnd != targetHwnd)
        {
            PrimeForegroundActivationContext(owner);
        }

        var foregroundSet = SetForegroundWindow(targetHwnd);
        Debug.WriteLine($"[{owner}] SetForegroundWindow: {foregroundSet}");
        return foregroundSet;
    }

    private static void PrimeForegroundActivationContext(string owner)
    {
        keybd_event(VkMenu, 0, KeyeventfExtendedkey, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyeventfExtendedkey | KeyeventfKeyup, UIntPtr.Zero);
        Debug.WriteLine($"[{owner}] Primed foreground activation with keybd_event(VK_MENU)");
    }
}
