using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>Temporarily enables the real Windows contrast theme and restores it.</summary>
internal sealed class HighContrastScope : IDisposable
{
    private HighContrast _original;
    private bool _changed;

    public HighContrastScope()
    {
        _original.Size = (uint)Marshal.SizeOf<HighContrast>();
        if (!SystemParametersInfo(0x42, _original.Size, ref _original, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var enabled = _original;
        enabled.Flags |= 1;
        if ((_original.Flags & 1) == 0)
        {
            if (!SystemParametersInfo(0x43, enabled.Size, ref enabled, 2))
            {
                Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            _changed = true;
            Thread.Sleep(1500);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_changed && !SystemParametersInfo(0x43, _original.Size, ref _original, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to restore Windows contrast settings.");
            _changed = false;
        }
        finally
        {
            if (_original.Scheme != IntPtr.Zero) LocalFree(_original.Scheme);
            _original.Scheme = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast { public uint Size; public uint Flags; public IntPtr Scheme; }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
