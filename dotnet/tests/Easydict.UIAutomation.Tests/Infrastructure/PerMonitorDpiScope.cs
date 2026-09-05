using System.Runtime.InteropServices;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>Use physical screen metrics while capturing a per-monitor-aware WinUI window.</summary>
internal sealed class PerMonitorDpiScope : IDisposable
{
    private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
    public void Dispose()
    {
        if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
