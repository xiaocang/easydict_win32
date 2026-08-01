using Easydict.DirectXaml.Text;

namespace Easydict.DirectXaml.Render;

/// <summary>
/// Backend-neutral drawing instructions.
///
/// Keeping paint expressed this way is what makes the renderer portable: if Win2D turns out not to
/// be usable, only the executor is replaced — layout and the display list survive untouched.
/// </summary>
public abstract record DrawCommand;

/// <summary>A filled rectangle. <see cref="Radius"/> is zero for square corners.</summary>
public sealed record FillRectangle(Rect Bounds, CornerRadius Radius, Color Color) : DrawCommand;

/// <summary>A stroked rounded rectangle, used only when the border thickness is uniform.</summary>
public sealed record StrokeRectangle(Rect Bounds, CornerRadius Radius, double Thickness, Color Color)
    : DrawCommand;

/// <summary>One laid-out line of text, positioned at its top-left corner.</summary>
public sealed record DrawTextLine(double X, double Y, string Text, FontSpec Font, Color Color)
    : DrawCommand;

public sealed record PushClip(Rect Bounds) : DrawCommand;

public sealed record PopClip : DrawCommand;

public sealed record PushOpacity(double Opacity) : DrawCommand;

public sealed record PopOpacity : DrawCommand;

/// <summary>An ordered list of drawing instructions for one frame.</summary>
public sealed class DisplayList(IReadOnlyList<DrawCommand> commands)
{
    public static readonly DisplayList Empty = new(Array.Empty<DrawCommand>());

    public IReadOnlyList<DrawCommand> Commands { get; } = commands;

    public int Count => Commands.Count;
}
