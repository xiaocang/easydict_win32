using Easydict.DirectXaml.Layout;
using Polyglot.TextLayout.Layout;

namespace Easydict.DirectXaml.Render;

/// <summary>Walks an arranged tree and emits drawing instructions in paint order.</summary>
public static class DisplayListBuilder
{
    private static readonly Color DefaultForeground = new(255, 0, 0, 0);

    public static DisplayList Build(LayoutEngine layout)
    {
        var commands = new List<DrawCommand>();
        Emit(layout, layout.View.RootNode, commands);
        return new DisplayList(commands);
    }

    private static void Emit(LayoutEngine layout, int node, List<DrawCommand> commands)
    {
        CompiledView view = layout.View;
        if (layout.VisibilityOf(node) != Visibility.Visible)
        {
            return;
        }

        Rect bounds = layout.BoundsOf(node);
        if (bounds.IsEmpty)
        {
            return;
        }

        double opacity = view.GetDouble(node, PropertyNames.Opacity, 1.0);
        bool hasOpacity = opacity < 1.0;
        if (hasOpacity)
        {
            commands.Add(new PushOpacity(Math.Max(0, opacity)));
        }

        NodeKind kind = view.KindOf(node);
        CornerRadius radius = view.GetCornerRadius(node, PropertyNames.CornerRadius);

        if (kind is NodeKind.Border or NodeKind.Grid or NodeKind.StackPanel)
        {
            Color background = view.GetColor(node, PropertyNames.Background, Color.Transparent);
            if (!background.IsTransparent)
            {
                commands.Add(new FillRectangle(bounds, radius, background));
            }
        }

        if (kind == NodeKind.Border)
        {
            EmitBorder(view, node, bounds, radius, commands);
        }

        if (kind == NodeKind.TextBlock)
        {
            EmitText(layout, node, bounds, commands);
        }

        foreach (int child in layout.VisualChildren(node))
        {
            Emit(layout, child, commands);
        }

        if (hasOpacity)
        {
            commands.Add(new PopOpacity());
        }
    }

    private static void EmitBorder(
        CompiledView view,
        int node,
        Rect bounds,
        CornerRadius radius,
        List<DrawCommand> commands)
    {
        Thickness thickness = view.GetThickness(node, PropertyNames.BorderThickness);
        if (thickness.IsZero)
        {
            return;
        }

        Color color = view.GetColor(node, PropertyNames.BorderBrush, Color.Transparent);
        if (color.IsTransparent)
        {
            return;
        }

        bool uniform = thickness.Left == thickness.Top
            && thickness.Top == thickness.Right
            && thickness.Right == thickness.Bottom;

        if (uniform && !radius.IsZero)
        {
            // A rounded, evenly-stroked border is the one case a single stroke draws correctly.
            double stroke = thickness.Left;
            Rect inset = new(
                bounds.X + (stroke / 2),
                bounds.Y + (stroke / 2),
                Math.Max(0, bounds.Width - stroke),
                Math.Max(0, bounds.Height - stroke));
            commands.Add(new StrokeRectangle(inset, radius, stroke, color));
            return;
        }

        // Otherwise fill each edge separately. This is what makes an asymmetric border such as the
        // header's BorderThickness="0,0,0,1" come out right.
        if (thickness.Top > 0)
        {
            commands.Add(new FillRectangle(
                new Rect(bounds.X, bounds.Y, bounds.Width, thickness.Top), CornerRadius.Zero, color));
        }

        if (thickness.Bottom > 0)
        {
            commands.Add(new FillRectangle(
                new Rect(bounds.X, bounds.Bottom - thickness.Bottom, bounds.Width, thickness.Bottom),
                CornerRadius.Zero,
                color));
        }

        if (thickness.Left > 0)
        {
            commands.Add(new FillRectangle(
                new Rect(bounds.X, bounds.Y + thickness.Top, thickness.Left,
                    Math.Max(0, bounds.Height - thickness.Top - thickness.Bottom)),
                CornerRadius.Zero,
                color));
        }

        if (thickness.Right > 0)
        {
            commands.Add(new FillRectangle(
                new Rect(bounds.Right - thickness.Right, bounds.Y + thickness.Top, thickness.Right,
                    Math.Max(0, bounds.Height - thickness.Top - thickness.Bottom)),
                CornerRadius.Zero,
                color));
        }
    }

    private static void EmitText(LayoutEngine layout, int node, Rect bounds, List<DrawCommand> commands)
    {
        TextLines? lines = layout.TextLinesOf(node);
        if (lines is null || lines.Lines.Count == 0)
        {
            return;
        }

        CompiledView view = layout.View;
        Thickness padding = view.GetThickness(node, PropertyNames.Padding);
        Color foreground = view.GetColor(node, PropertyNames.Foreground, DefaultForeground);

        double x = bounds.X + padding.Left;
        double y = bounds.Y + padding.Top;

        bool clip = bounds.Height < lines.Lines.Count * lines.LineHeight;
        if (clip)
        {
            commands.Add(new PushClip(bounds));
        }

        for (int index = 0; index < lines.Lines.Count; index++)
        {
            LayoutLine line = lines.Lines[index];
            if (line.Text.Length == 0)
            {
                continue;
            }

            commands.Add(new DrawTextLine(
                x,
                y + (index * lines.LineHeight),
                line.Text,
                lines.Font,
                foreground));
        }

        if (clip)
        {
            commands.Add(new PopClip());
        }
    }
}
