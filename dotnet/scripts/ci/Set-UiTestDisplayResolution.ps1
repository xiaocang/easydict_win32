param(
    [int]$Width = 1920,
    [int]$Height = 1080
)

$ErrorActionPreference = 'Stop'

# The hosted desktop can start at 1024x768. Windows limits window tracking sizes
# to that desktop, preventing the Saved Items tests from reaching 1280 DIPs.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class UiTestDisplay
{
    // Display fields of the native Unicode DEVMODEW structure (220 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    public struct Mode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(172)] public uint Width;
        [FieldOffset(176)] public uint Height;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", SetLastError = true)]
    private static extern bool EnumDisplaySettings(IntPtr device, int index, ref Mode mode);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsW")]
    private static extern int ChangeDisplaySettings(ref Mode mode, uint flags);

    public static Mode Current()
    {
        var mode = new Mode { Size = 220 };
        if (!EnumDisplaySettings(IntPtr.Zero, -1, ref mode))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return mode;
    }

    public static void Set(int width, int height)
    {
        var mode = Current();
        mode.Fields = 0x00080000 | 0x00100000; // DM_PELSWIDTH | DM_PELSHEIGHT
        mode.Width = checked((uint)width);
        mode.Height = checked((uint)height);
        var result = ChangeDisplaySettings(ref mode, 0); // Apply only to this session.
        if (result != 0)
            throw new InvalidOperationException("ChangeDisplaySettings failed: " + result);
    }
}
'@

$before = [UiTestDisplay]::Current()
Write-Host "Desktop before UI tests: $($before.Width)x$($before.Height)"
[UiTestDisplay]::Set($Width, $Height)
$after = [UiTestDisplay]::Current()
Write-Host "Desktop configured for UI tests: $($after.Width)x$($after.Height)"
if ($after.Width -lt $Width -or $after.Height -lt $Height) {
    throw "UI tests require a desktop of at least ${Width}x${Height}."
}
