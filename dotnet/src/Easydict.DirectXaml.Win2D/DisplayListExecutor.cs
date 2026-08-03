using System.Numerics;
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
        Win2DTextMeasurerFactory formats) =>
        Execute(
            session,
            displayList,
            formats,
            Vector2.Zero,
            DisplayListLayer.All,
            null);

    /// <summary>Replays a card-local display-list partition at <paramref name="offset"/>.</summary>
    public static void Execute(
        CanvasDrawingSession session,
        DisplayList displayList,
        Win2DTextMeasurerFactory formats,
        Vector2 offset,
        DisplayListLayer layer = DisplayListLayer.All,
        WinRect? clipRegion = null)
    {
        IReadOnlyList<DrawCommand> commands = layer switch
        {
            DisplayListLayer.Static => displayList.StaticCommands,
            DisplayListLayer.Dynamic => displayList.DynamicCommands,
            _ => displayList.Commands,
        };
        using DirectRendererTelemetry.Scope telemetry =
            DirectRendererTelemetry.Measure("draw", commands.Count);
        Matrix3x2 originalTransform = session.Transform;
        session.Transform = Matrix3x2.CreateTranslation(offset) * originalTransform;
        // Both clips and opacity groups map onto Win2D layers, so one stack serves both. The
        // display list is emitted balanced; an unbalanced list would leak a layer, so the finally
        // block unwinds whatever is left.
        var layers = new Stack<CanvasActiveLayer>();
        if (clipRegion is WinRect region)
        {
            layers.Push(session.CreateLayer(
                1f,
                new WinRect(
                    region.X - offset.X,
                    region.Y - offset.Y,
                    region.Width,
                    region.Height)));
        }

        try
        {
            foreach (DrawCommand command in commands)
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
                        formats.DrawTextLayout(
                            session,
                            text.Text,
                            text.X,
                            text.Y,
                            text.Font,
                            ToWinColor(text.Color));
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
            session.Transform = originalTransform;
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
