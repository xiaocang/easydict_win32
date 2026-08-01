using Easydict.DirectXaml.Render;
using Microsoft.Graphics.Canvas;

using DxColor = Easydict.DirectXaml.Color;
using DxRect = Easydict.DirectXaml.Rect;
using WinColor = Windows.UI.Color;
using WinRect = Windows.Foundation.Rect;

namespace Easydict.DirectXaml.Win2D;

/// <summary>
/// Replays a <see cref="DisplayList"/> onto a Win2D drawing session.
///
/// This is the only place that knows about Win2D drawing primitives. Everything upstream — layout,
/// invalidation, the display list itself — is backend-neutral, so replacing Win2D means replacing
/// this file and nothing else.
/// </summary>
public static class DisplayListExecutor
{
    public static void Execute(
        CanvasDrawingSession session,
        DisplayList displayList,
        Win2DTextMeasurerFactory formats)
    {
        // Both clips and opacity groups map onto Win2D layers, so one stack serves both. The
        // display list is emitted balanced; an unbalanced list would leak a layer, so the finally
        // block unwinds whatever is left.
        var layers = new Stack<CanvasActiveLayer>();

        try
        {
            foreach (DrawCommand command in displayList.Commands)
            {
                switch (command)
                {
                    case FillRectangle fill:
                        DrawFill(session, fill);
                        break;

                    case StrokeRectangle stroke:
                        session.DrawRoundedRectangle(
                            ToWinRect(stroke.Bounds),
                            (float)stroke.Radius.Uniform,
                            (float)stroke.Radius.Uniform,
                            ToWinColor(stroke.Color),
                            (float)stroke.Thickness);
                        break;

                    case DrawTextLine text:
                        session.DrawText(
                            text.Text,
                            (float)text.X,
                            (float)text.Y,
                            ToWinColor(text.Color),
                            formats.GetFormat(text.Font));
                        break;

                    case PushClip clip:
                        layers.Push(session.CreateLayer(1f, ToWinRect(clip.Bounds)));
                        break;

                    case PushOpacity opacity:
                        layers.Push(session.CreateLayer((float)opacity.Opacity));
                        break;

                    case PopClip:
                    case PopOpacity:
                        if (layers.Count > 0)
                        {
                            layers.Pop().Dispose();
                        }

                        break;
                }
            }
        }
        finally
        {
            while (layers.Count > 0)
            {
                layers.Pop().Dispose();
            }
        }
    }

    private static void DrawFill(CanvasDrawingSession session, FillRectangle fill)
    {
        WinRect bounds = ToWinRect(fill.Bounds);
        WinColor color = ToWinColor(fill.Color);

        if (fill.Radius.IsZero)
        {
            session.FillRectangle(bounds, color);
            return;
        }

        session.FillRoundedRectangle(
            bounds,
            (float)fill.Radius.Uniform,
            (float)fill.Radius.Uniform,
            color);
    }

    internal static WinRect ToWinRect(DxRect rect) =>
        new(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));

    internal static WinColor ToWinColor(DxColor color) =>
        WinColor.FromArgb(color.A, color.R, color.G, color.B);
}
