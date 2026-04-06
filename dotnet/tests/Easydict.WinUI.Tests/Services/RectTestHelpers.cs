using PdfSharpCore.Drawing;

namespace Easydict.WinUI.Tests.Services;

internal static class RectTestHelpers
{
    internal static bool ContainsRect(XRect outer, XRect inner) =>
        outer.Left <= inner.Left + 0.01 &&
        outer.Top <= inner.Top + 0.01 &&
        outer.Right >= inner.Right - 0.01 &&
        outer.Bottom >= inner.Bottom - 0.01;

    internal static bool NearlySameRect(XRect left, XRect right) =>
        Math.Abs(left.Left - right.Left) < 0.01 &&
        Math.Abs(left.Top - right.Top) < 0.01 &&
        Math.Abs(left.Width - right.Width) < 0.01 &&
        Math.Abs(left.Height - right.Height) < 0.01;
}
