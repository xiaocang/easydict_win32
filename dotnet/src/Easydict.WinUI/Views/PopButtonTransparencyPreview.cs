#if WINUI_TEST
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.TextActions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using WinRT.Interop;

namespace Easydict.WinUI.Views;

/// <summary>
/// Isolated host-underlay preview for checking whether the pop-button HWND actually reveals
/// content behind its transparent XAML areas. It returns before any tray, hotkey, clipboard,
/// or global mouse-hook services are created.
/// </summary>
internal static partial class PopButtonTransparencyPreview
{
    private const string EnableVariable = "EASYDICT_UIA_POPBUTTON_TRANSPARENCY_PREVIEW";

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int valueSize);

    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetForegroundWindow();

    public static bool TryLaunch(Window hostWindow, AppWindow? appWindow)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
            return false;

        if (appWindow is null)
            throw new InvalidOperationException("Cannot start transparency preview without the host AppWindow.");

        var underlay = new Grid
        {
            Background = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        const int tileSize = 36;
        const int columns = 24;
        const int rows = 12;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if ((row + column) % 2 == 0) continue;
                underlay.Children.Add(new Rectangle
                {
                    Width = tileSize,
                    Height = tileSize,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(column * tileSize, row * tileSize, 0, 0),
                    Fill = new SolidColorBrush(Colors.Black),
                    IsHitTestVisible = false
                });
            }
        }

        var decoration = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18),
            Spacing = 8,
            IsHitTestVisible = false
        };
        decoration.Children.Add(new TextBlock
        {
            Text = "Pop-button host transparency preview",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.DodgerBlue)
        });
        decoration.Children.Add(new TextBlock
        {
            Text = "Checkerboard and blue guide lines should remain visible around the icon.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Colors.DodgerBlue)
        });
        decoration.Children.Add(new Rectangle
        {
            Width = 440,
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Fill = new SolidColorBrush(Colors.DodgerBlue)
        });
        underlay.Children.Add(decoration);

        var status = new TextBlock
        {
            Text = "Click the floating icon or actions to verify hit testing.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 600
        };
        var diagnostics = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };
        var iconOnlyButton = new Button { Content = "Icon only" };
        var actionsButton = new Button { Content = "Icon + actions" };
        var controls = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18),
            Spacing = 6,
            MaxWidth = 620
        };
        var toggleButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toggleButtons.Children.Add(iconOnlyButton);
        toggleButtons.Children.Add(actionsButton);
        controls.Children.Add(toggleButtons);
        controls.Children.Add(status);
        controls.Children.Add(diagnostics);
        underlay.Children.Add(controls);

        hostWindow.Content = underlay;
        hostWindow.Title = "Easydict — Pop-button transparency preview";
        appWindow.Resize(new SizeInt32(900, 520));
        hostWindow.Activate();

        var popButton = new PopButtonWindow();
        PresentedTextAction[] previewActions =
        [
            new PresentedTextAction(
                new TextAction { Id = "preview-web", Title = "Web search", Type = TextActionType.OpenUrl, IconGlyph = "\uE774", UrlTemplate = "https://example.com/?q={encodedText}" },
                ServiceOrigin.BuiltIn,
                null),
            new PresentedTextAction(
                new TextAction { Id = "preview-translate", Title = "Translate action", Type = TextActionType.OpenUrl, IconGlyph = "\uE8E1", UrlTemplate = "https://example.com/translate?q={encodedText}" },
                ServiceOrigin.BuiltIn,
                null)
        ];
        popButton.SetActions(previewActions, ElementTheme.Light);

        var iconClicks = 0;
        var actionClicks = 0;
        popButton.OnClicked += () =>
        {
            iconClicks++;
            status.Text = $"Floating icon hit: {iconClicks}; action hits: {actionClicks}. No translation was started.";
        };
        popButton.OnActionClicked += action =>
        {
            actionClicks++;
            status.Text = $"Action hit ({action.Id}): {actionClicks}; icon hits: {iconClicks}. No URL was opened.";
        };
        hostWindow.Closed += (_, _) => popButton.Close();
        popButton.Closed += (_, _) => hostWindow.Close();

        var hostHandle = WindowNative.GetWindowHandle(hostWindow);
        if (!GetWindowRect(hostHandle, out var bounds))
            throw new InvalidOperationException("Could not determine preview host bounds.");

        var anchorX = (bounds.Left + bounds.Right) / 2;
        var anchorY = (bounds.Top + bounds.Bottom) / 2;
        void ShowAndRecordForeground()
        {
            var foregroundBefore = GetForegroundWindow();
            popButton.ShowAt(anchorX, anchorY);
            var foregroundAfter = GetForegroundWindow();
            diagnostics.Text = DescribeNativeState(
                popButton.WindowHandle,
                popButton.LastStyleWriteDiagnostics,
                foregroundBefore,
                foregroundAfter);
        }

        iconOnlyButton.Click += (_, _) =>
        {
            popButton.HidePopup();
            popButton.SetActions([], ElementTheme.Light);
            ShowAndRecordForeground();
        };
        actionsButton.Click += (_, _) =>
        {
            popButton.HidePopup();
            popButton.SetActions(previewActions, ElementTheme.Light);
            ShowAndRecordForeground();
        };

        ShowAndRecordForeground();
        return true;
    }

    private static string DescribeNativeState(
        nint hwnd,
        string styleWriteDiagnostics,
        nint foregroundBefore,
        nint foregroundAfter)
    {
        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_BORDER = 0x00800000;
        const int WS_DLGFRAME = 0x00400000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_CAPTION = 0x00C00000;
        const int WS_EX_WINDOWEDGE = 0x00000100;
        const int WS_EX_CLIENTEDGE = 0x00000200;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37;

        var style = unchecked((uint)GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64());
        var exStyle = unchecked((uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64());
        var borderHr = DwmGetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, out var borderColor, sizeof(int));
        var frameHr = DwmGetWindowAttribute(hwnd, DWMWA_VISIBLE_FRAME_BORDER_THICKNESS, out var frameThickness, sizeof(int));
        var cornerHr = DwmGetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out var cornerPreference, sizeof(int));
        var client = GetClientRect(hwnd, out var rect)
            ? $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top}"
            : $"error:{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";

        return $"HWND style=0x{style:X8} [BORDER={(style & WS_BORDER) != 0}, DLGFRAME={(style & WS_DLGFRAME) != 0}, THICKFRAME={(style & WS_THICKFRAME) != 0}, CAPTION={(style & WS_CAPTION) != 0}]\n" +
               $"exStyle=0x{exStyle:X8} [WINDOWEDGE={(exStyle & WS_EX_WINDOWEDGE) != 0}, CLIENTEDGE={(exStyle & WS_EX_CLIENTEDGE) != 0}] client={client}\n" +
               $"DWM border=0x{borderColor:X8}/hr=0x{borderHr:X8}; visibleFramePx={frameThickness}/hr=0x{frameHr:X8}; corner={cornerPreference}/hr=0x{cornerHr:X8}\n" +
               $"ShowAt foreground=0x{foregroundBefore.ToInt64():X}, after=0x{foregroundAfter.ToInt64():X}, unchanged={foregroundBefore == foregroundAfter}\n" +
               styleWriteDiagnostics;
    }
}
#endif
