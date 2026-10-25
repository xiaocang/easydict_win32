using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace Easydict.WinUI.Views;

/// <summary>
/// Supplies a transparent system backdrop and clears the native client background for the
/// selection pop-button window. Native hooks and composition resources follow target lifetime.
/// The DWM/GDI handling follows the approach in WinUIEx TransparentTintBackdrop (MIT).
/// </summary>
internal sealed partial class TransparentCompositionBackdrop : SystemBackdrop
{
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const uint DWM_BB_ENABLE = 0x00000001;
    private const uint DWM_BB_BLURREGION = 0x00000002;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMNCRP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private static readonly nuint SubclassId = 0x45415359;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public nint BlurRegion;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId, nuint refData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out NativeRect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateSolidBrush(uint colorRef);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int FillRect(nint hdc, in NativeRect rect, nint brush);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, in Margins margins);

    [LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int valueSize);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref DwmBlurBehind blurBehind);

    private readonly nint _hwnd;
    private readonly SubclassProc _subclassProc;
    private global::Windows.UI.Composition.Compositor? _compositor;
    private global::Windows.UI.Composition.CompositionColorBrush? _brush;
    private nint _backgroundBrush;
    private bool _subclassInstalled;

    public TransparentCompositionBackdrop(nint hwnd)
    {
        _hwnd = hwnd != 0 ? hwnd : throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
        _subclassProc = OnWindowMessage;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("A WinUI dispatcher queue is required for the transparent pop-button backdrop.");
        dispatcherQueue.EnsureSystemDispatcherQueue();

        try
        {
            _subclassInstalled = SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0);
            if (!_subclassInstalled)
                throw new InvalidOperationException($"Could not monitor the transparent popup window (Win32 {Marshal.GetLastWin32Error()}).");

            ConfigureDwm(_hwnd);
            _compositor = new global::Windows.UI.Composition.Compositor();
            _brush = _compositor.CreateColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            connectedTarget.SystemBackdrop = _brush;
            base.OnTargetConnected(connectedTarget, xamlRoot);

            var hdc = GetDC(_hwnd);
            if (hdc != 0)
            {
                try { ClearBackground(_hwnd, hdc); }
                finally { ReleaseDC(_hwnd, hdc); }
            }
        }
        catch
        {
            ReleaseResources(connectedTarget);
            throw;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        ReleaseResources(disconnectedTarget);
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private void ReleaseResources(ICompositionSupportsSystemBackdrop target)
    {
        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            _subclassInstalled = false;
        }

        target.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        _compositor?.Dispose();
        _compositor = null;
        if (_backgroundBrush != 0)
        {
            DeleteObject(_backgroundBrush);
            _backgroundBrush = 0;
        }
    }

    private nint OnWindowMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (message == WM_ERASEBKGND && ClearBackground(hwnd, wParam))
            return 1;

        if (message == WM_DWMCOMPOSITIONCHANGED)
            ConfigureDwm(hwnd);

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private bool ClearBackground(nint hwnd, nint hdc)
    {
        if (hdc == 0 || !GetClientRect(hwnd, out var rect))
            return false;

        if (_backgroundBrush == 0)
            _backgroundBrush = CreateSolidBrush(0);
        return _backgroundBrush != 0 && FillRect(hdc, in rect, _backgroundBrush) != 0;
    }

    private static void ConfigureDwm(nint hwnd)
    {
        // Zero margins match the WinUIEx transparent backdrop path and avoid a visible
        // frame around the client area while blur-behind provides the transparent surface.
        var glass = new Margins();
        DwmExtendFrameIntoClientArea(hwnd, in glass);

        // Reapply after backdrop connection: presenter operations can restore DWM's rounded
        // corners and border even though the window was initially configured frameless.
        var noRounding = DWMNCRP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, in noRounding, sizeof(int));
        var noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, in noBorder, sizeof(int));

        var blurRegion = CreateRectRgn(-2, -2, -1, -1);
        if (blurRegion == 0)
            return;

        try
        {
            var blurBehind = new DwmBlurBehind
            {
                Flags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
                Enable = true,
                BlurRegion = blurRegion
            };
            DwmEnableBlurBehindWindow(hwnd, ref blurBehind);
        }
        finally
        {
            DeleteObject(blurRegion);
        }
    }
}
