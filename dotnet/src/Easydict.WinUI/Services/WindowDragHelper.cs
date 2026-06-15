using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace Easydict.WinUI.Services;

internal static class WindowDragHelper
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public static bool TryBeginLeftButtonDrag(
        Window window,
        UIElement dragElement,
        PointerRoutedEventArgs args,
        out WindowDragSession? session)
    {
        session = null;

        var point = args.GetCurrentPoint(dragElement);
        if (point.PointerDeviceType != PointerDeviceType.Mouse
            || !point.Properties.IsLeftButtonPressed
            || !TryGetAppWindow(window, out var appWindow)
            || !TryGetCursorPosition(out var cursorPosition)
            || !dragElement.CapturePointer(args.Pointer))
        {
            return false;
        }

        session = new WindowDragSession(
            args.Pointer.PointerId,
            appWindow,
            cursorPosition,
            appWindow.Position);
        return true;
    }

    public static bool TryUpdateLeftButtonDrag(
        WindowDragSession session,
        UIElement dragElement,
        PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerId != session.PointerId)
        {
            return false;
        }

        var point = args.GetCurrentPoint(dragElement);
        if (point.PointerDeviceType != PointerDeviceType.Mouse
            || !point.Properties.IsLeftButtonPressed
            || !TryGetCursorPosition(out var cursorPosition))
        {
            return false;
        }

        var deltaX = cursorPosition.X - session.StartCursorPosition.X;
        var deltaY = cursorPosition.Y - session.StartCursorPosition.Y;
        session.AppWindow.Move(new PointInt32(
            session.StartWindowPosition.X + deltaX,
            session.StartWindowPosition.Y + deltaY));
        return true;
    }

    public static bool IsSessionPointer(WindowDragSession session, PointerRoutedEventArgs args)
    {
        return args.Pointer.PointerId == session.PointerId;
    }

    private static bool TryGetAppWindow(Window window, out AppWindow appWindow)
    {
        appWindow = null!;
        var hWnd = WindowNative.GetWindowHandle(window);
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        appWindow = AppWindow.GetFromWindowId(windowId);
        return appWindow is not null;
    }

    private static bool TryGetCursorPosition(out PointInt32 cursorPosition)
    {
        if (!GetCursorPos(out var point))
        {
            cursorPosition = default;
            return false;
        }

        cursorPosition = new PointInt32(point.X, point.Y);
        return true;
    }
}

internal sealed record WindowDragSession(
    uint PointerId,
    AppWindow AppWindow,
    PointInt32 StartCursorPosition,
    PointInt32 StartWindowPosition);
