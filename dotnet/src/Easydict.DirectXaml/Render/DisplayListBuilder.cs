using Easydict.DirectXaml.Layout;
using Polyglot.TextLayout.Layout;

namespace Easydict.DirectXaml.Render;

/// <summary>Walks an arranged tree and emits drawing instructions in paint order.</summary>
public static class DisplayListBuilder
{
    private static readonly Color DefaultForeground = new(255, 0, 0, 0);

    public static DisplayList Build(LayoutEngine layout, DisplayList? reuse = null)
    {
        DisplayList displayList = reuse
            ?? new DisplayList(
                Array.Empty<DrawCommand>(),
                Array.Empty<DrawCommand>(),
                Array.Empty<DrawCommand>());
        displayList.Reset();
        Emit(
            layout,
            layout.View.RootNode,
            displayList.StaticBuffer,
            displayList.DynamicBuffer,
            displayList.CommandBuffer,
            false);
        return displayList;
    }

    private static void Emit(
        LayoutEngine layout,
        int node,
        List<DrawCommand> staticCommands,
        List<DrawCommand> dynamicCommands,
        List<DrawCommand> commands,
        bool inheritedDynamic)
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
        bool dynamic = inheritedDynamic || view.IsDynamicNode(node);
        bool dynamicSubtree = view.HasDynamicDescendant(node);
        List<DrawCommand> target = dynamic ? dynamicCommands : staticCommands;
        double opacity = view.GetDouble(node, PropertyNames.Opacity, 1.0);
        bool hasOpacity = opacity < 1.0;
        if (hasOpacity)
        {
            Add(
                target,
                commands,
                new PushOpacity(Math.Max(0, opacity)));
            if (!dynamic && dynamicSubtree)
            {
                dynamicCommands.Add(new PushOpacity(Math.Max(0, opacity)));
            }
        }

        NodeKind kind = view.KindOf(node);
        CornerRadius radius = view.GetCornerRadius(node, PropertyNames.CornerRadius);

        if (kind is NodeKind.Border or NodeKind.Button or NodeKind.Grid or NodeKind.StackPanel)
        {
            Color background = view.GetColor(node, PropertyNames.Background, Color.Transparent);
            if (!background.IsTransparent)
            {
                Add(target, commands, new FillRectangle(bounds, radius, background));
            }
        }

        if (kind is NodeKind.Border or NodeKind.Button)
        {
            EmitBorder(view, node, bounds, radius, target, commands);
        }

        if (kind is NodeKind.TextBlock or NodeKind.Button)
        {
            EmitText(layout, node, bounds, target, commands);
        }

        foreach (int child in layout.VisualChildren(node))
        {
            Emit(layout, child, staticCommands, dynamicCommands, commands, dynamic);
        }

        if (hasOpacity)
        {
            Add(target, commands, new PopOpacity());
            if (!dynamic && dynamicSubtree)
            {
                dynamicCommands.Add(new PopOpacity());
            }
        }
    }

    private static void Add(
        List<DrawCommand> target,
        List<DrawCommand> commands,
        DrawCommand command)
    {
        target.Add(command);
        commands.Add(command);
    }

    private static void EmitBorder(
        CompiledView view,
        int node,
        Rect bounds,
        CornerRadius radius,
        List<DrawCommand> target,
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
            double stroke = thickness.Left;
            Rect inset = new(
                bounds.X + (stroke / 2),
                bounds.Y + (stroke / 2),
                Math.Max(0, bounds.Width - stroke),
                Math.Max(0, bounds.Height - stroke));
            Add(target, commands, new StrokeRectangle(inset, radius, stroke, color));
            return;
        }

        if (thickness.Top > 0)
        {
            Add(
                target,
                commands,
                new FillRectangle(
                    new Rect(bounds.X, bounds.Y, bounds.Width, thickness.Top),
                    CornerRadius.Zero,
                    color));
        }

        if (thickness.Bottom > 0)
        {
            Add(
                target,
                commands,
                new FillRectangle(
                    new Rect(bounds.X, bounds.Bottom - thickness.Bottom, bounds.Width, thickness.Bottom),
                    CornerRadius.Zero,
                    color));
        }

        if (thickness.Left > 0)
        {
            Add(
                target,
                commands,
                new FillRectangle(
                    new Rect(
                        bounds.X,
                        bounds.Y + thickness.Top,
                        thickness.Left,
                        Math.Max(0, bounds.Height - thickness.Top - thickness.Bottom)),
                    CornerRadius.Zero,
                    color));
        }

        if (thickness.Right > 0)
        {
            Add(
                target,
                commands,
                new FillRectangle(
                    new Rect(
                        bounds.Right - thickness.Right,
                        bounds.Y + thickness.Top,
                        thickness.Right,
                        Math.Max(0, bounds.Height - thickness.Top - thickness.Bottom)),
                    CornerRadius.Zero,
                    color));
        }
    }

    private static void EmitText(
        LayoutEngine layout,
        int node,
        Rect bounds,
        List<DrawCommand> target,
        List<DrawCommand> commands)
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
            Add(target, commands, new PushClip(bounds));
        }

        for (int index = 0; index < lines.Lines.Count; index++)
        {
            LayoutLine line = lines.Lines[index];
            if (line.Text.Length == 0)
            {
                continue;
            }

            Add(
                target,
                commands,
                new DrawTextLine(
                    x,
                    y + (index * lines.LineHeight),
                    line.Text,
                    lines.Font,
                    foreground));
        }

        if (clip)
        {
            Add(target, commands, new PopClip());
        }
    }
}
