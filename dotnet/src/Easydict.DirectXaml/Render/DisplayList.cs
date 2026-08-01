using Easydict.DirectXaml.Text;

namespace Easydict.DirectXaml.Render;

/// <summary>
/// Backend-neutral drawing instructions.
///
/// Keeping paint expressed this way is what makes the renderer portable: if Win2D turns out not to
/// be usable, only the executor is replaced — layout and the display list survive untouched.
/// </summary>
public abstract record DrawCommand;

/// <summary>A filled rectangle.</summary>
/// <param name="Bounds">Rectangle to fill, in view coordinates.</param>
/// <param name="Radius">Corner radii; <see cref="CornerRadius.Zero"/> for square corners.</param>
/// <param name="Color">Fill colour, already resolved from any resource slot.</param>
public sealed record FillRectangle(Rect Bounds, CornerRadius Radius, Color Color) : DrawCommand;

/// <summary>
/// A stroked rounded rectangle. Emitted only when the border thickness is uniform — an asymmetric
/// border comes through as one <see cref="FillRectangle"/> per edge instead, because a single
/// stroke would draw all four sides.
/// </summary>
/// <param name="Bounds">Rectangle the stroke is centred on.</param>
/// <param name="Radius">Corner radii.</param>
/// <param name="Thickness">Stroke width in DIPs.</param>
/// <param name="Color">Stroke colour.</param>
public sealed record StrokeRectangle(Rect Bounds, CornerRadius Radius, double Thickness, Color Color)
    : DrawCommand;

/// <summary>One laid-out line of text, positioned at its top-left corner.</summary>
/// <param name="X">Left edge in view coordinates.</param>
/// <param name="Y">Top edge in view coordinates.</param>
/// <param name="Text">The line's text, already broken by the layout engine.</param>
/// <param name="Font">Font to draw with; the executor maps it onto a platform text format.</param>
/// <param name="Color">Foreground colour.</param>
public sealed record DrawTextLine(double X, double Y, string Text, FontSpec Font, Color Color)
    : DrawCommand;

/// <summary>Restricts subsequent drawing to a rectangle until the matching <see cref="PopClip"/>.</summary>
/// <param name="Bounds">Clip rectangle in view coordinates.</param>
public sealed record PushClip(Rect Bounds) : DrawCommand;

/// <summary>Ends the clip opened by the most recent <see cref="PushClip"/>.</summary>
public sealed record PopClip : DrawCommand;

/// <summary>Applies an opacity to subsequent drawing until the matching <see cref="PopOpacity"/>.</summary>
/// <param name="Opacity">Multiplier in the range 0 to 1.</param>
public sealed record PushOpacity(double Opacity) : DrawCommand;

/// <summary>Ends the group opened by the most recent <see cref="PushOpacity"/>.</summary>
public sealed record PopOpacity : DrawCommand;

/// <summary>An ordered list of drawing instructions for one frame.</summary>
/// <param name="commands">Instructions in paint order. Push/pop pairs are balanced.</param>
public sealed class DisplayList(IReadOnlyList<DrawCommand> commands)
{
    /// <summary>A list that draws nothing.</summary>
    public static readonly DisplayList Empty = new(Array.Empty<DrawCommand>());

    /// <summary>The instructions, in paint order.</summary>
    public IReadOnlyList<DrawCommand> Commands { get; } = commands;

    /// <summary>Number of instructions.</summary>
    public int Count => Commands.Count;
}
