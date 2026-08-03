namespace Easydict.DirectXaml;

/// <summary>Device-independent size in DIPs.</summary>
/// <param name="Width">Width in DIPs.</param>
/// <param name="Height">Height in DIPs.</param>
public readonly record struct Size(double Width, double Height)
{
    /// <summary>Zero in both dimensions.</summary>
    public static readonly Size Empty = new(0, 0);

    /// <summary>
    /// Stands in for an unbounded constraint. A finite value avoids the overflow and NaN traps
    /// that <see cref="double.PositiveInfinity"/> introduces once sizes start being added up.
    /// </summary>
    public const double Unbounded = 1_000_000;

    /// <summary>A constraint fixed in width and unbounded in height — how a list row is measured.</summary>
    public static Size FromWidth(double width) => new(width, Unbounded);

    /// <summary>Shrinks by the given insets, clamping at zero.</summary>
    public Size Deflate(Thickness by) =>
        new(Math.Max(0, Width - by.Horizontal), Math.Max(0, Height - by.Vertical));

    /// <summary>Grows by the given insets.</summary>
    public Size Inflate(Thickness by) => new(Width + by.Horizontal, Height + by.Vertical);
}

/// <summary>Device-independent rectangle in DIPs, relative to the view origin.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in DIPs.</param>
/// <param name="Height">Height in DIPs.</param>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    /// <summary>A rectangle at the origin with no extent. Used for nodes that are not laid out.</summary>
    public static readonly Rect Empty = new(0, 0, 0, 0);

    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>True when the rectangle has no area and so draws nothing.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Insets the rectangle, clamping its extent at zero.</summary>
    public Rect Deflate(Thickness by) =>
        new(X + by.Left, Y + by.Top, Math.Max(0, Width - by.Horizontal), Math.Max(0, Height - by.Vertical));

    /// <summary>
    /// Hit test. The right and bottom edges are exclusive so adjacent rectangles do not both
    /// claim a point on their shared boundary.
    /// </summary>
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>Left/top/right/bottom offsets, matching XAML's Thickness.</summary>
/// <param name="Left">Left offset.</param>
/// <param name="Top">Top offset.</param>
/// <param name="Right">Right offset.</param>
/// <param name="Bottom">Bottom offset.</param>
public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
{
    /// <summary>No offset on any side.</summary>
    public static readonly Thickness Zero = new(0, 0, 0, 0);

    /// <summary>The same offset on all four sides.</summary>
    public Thickness(double uniform) : this(uniform, uniform, uniform, uniform)
    {
    }

    /// <summary>Left plus right.</summary>
    public double Horizontal => Left + Right;

    /// <summary>Top plus bottom.</summary>
    public double Vertical => Top + Bottom;

    /// <summary>True when no side has an offset.</summary>
    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>Per-corner radii, in XAML's order.</summary>
/// <param name="TopLeft">Top-left radius.</param>
/// <param name="TopRight">Top-right radius.</param>
/// <param name="BottomRight">Bottom-right radius.</param>
/// <param name="BottomLeft">Bottom-left radius.</param>
public readonly record struct CornerRadius(
    double TopLeft,
    double TopRight,
    double BottomRight,
    double BottomLeft)
{
    /// <summary>Square corners.</summary>
    public static readonly CornerRadius Zero = new(0, 0, 0, 0);

    /// <summary>The same radius on all four corners.</summary>
    public CornerRadius(double uniform) : this(uniform, uniform, uniform, uniform)
    {
    }

    /// <summary>True when every corner is square.</summary>
    public bool IsZero => TopLeft == 0 && TopRight == 0 && BottomRight == 0 && BottomLeft == 0;

    /// <summary>
    /// Win2D draws rounded rectangles with a single x/y radius pair, so a non-uniform radius has
    /// to be approximated until the executor grows a path-based fallback. The largest corner is
    /// used, which keeps the shape from looking squarer than the markup asked for.
    /// </summary>
    public double Uniform => Math.Max(Math.Max(TopLeft, TopRight), Math.Max(BottomRight, BottomLeft));
}

/// <summary>
/// Straight ARGB, deliberately not <c>Windows.UI.Color</c> so this assembly stays platform-neutral.
/// </summary>
/// <param name="A">Alpha.</param>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    /// <summary>Fully transparent. Also what an unresolved brush falls back to.</summary>
    public static readonly Color Transparent = new(0, 0, 0, 0);

    /// <summary>True when the colour would draw nothing, so the caller can skip emitting a command.</summary>
    public bool IsTransparent => A == 0;

    /// <summary>Parses the <c>#AARRGGBB</c> form the compiler emits.</summary>
    /// <param name="text">Text to parse; anything else yields false.</param>
    /// <param name="color">The parsed colour, or <see cref="Transparent"/> on failure.</param>
    /// <returns>True when <paramref name="text"/> was a well-formed eight-digit hex colour.</returns>
    public static bool TryParseArgbHex(string? text, out Color color)
    {
        color = Transparent;
        if (string.IsNullOrEmpty(text) || text[0] != '#' || text.Length != 9)
        {
            return false;
        }

        static bool TryByte(ReadOnlySpan<char> span, out byte value)
        {
            value = 0;
            return byte.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        ReadOnlySpan<char> digits = text.AsSpan(1);
        if (TryByte(digits[..2], out byte a)
            && TryByte(digits[2..4], out byte r)
            && TryByte(digits[4..6], out byte g)
            && TryByte(digits[6..8], out byte b))
        {
            color = new Color(a, r, g, b);
            return true;
        }

        return false;
    }
}

/// <summary>Whether an element takes part in layout and paint.</summary>
public enum Visibility
{
    /// <summary>Laid out and drawn.</summary>
    Visible,

    /// <summary>Removed from layout entirely — it occupies no space and emits no draw commands.</summary>
    Collapsed,
}

/// <summary>The axis a stack panel arranges along.</summary>
public enum Orientation
{
    /// <summary>Children stack top to bottom.</summary>
    Vertical,

    /// <summary>Children stack left to right.</summary>
    Horizontal,
}

/// <summary>How text behaves when it exceeds the available width.</summary>
public enum TextWrapping
{
    /// <summary>Stays on one line and overflows.</summary>
    NoWrap,

    /// <summary>Breaks onto further lines, splitting a word if it cannot fit alone.</summary>
    Wrap,

    /// <summary>Breaks onto further lines without splitting words.</summary>
    WrapWholeWords,
}

/// <summary>How text is shortened when it does not fit.</summary>
public enum TextTrimming
{
    /// <summary>No trimming.</summary>
    None,

    /// <summary>Cut at a character boundary and append an ellipsis.</summary>
    CharacterEllipsis,

    /// <summary>Cut at a word boundary and append an ellipsis.</summary>
    WordEllipsis,

    /// <summary>Cut with no ellipsis.</summary>
    Clip,
}

/// <summary>Placement within the horizontal space a parent offers.</summary>
public enum HorizontalAlignment
{
    /// <summary>Fill the available width.</summary>
    Stretch,

    /// <summary>Size to content, against the left edge.</summary>
    Left,

    /// <summary>Size to content, centred.</summary>
    Center,

    /// <summary>Size to content, against the right edge.</summary>
    Right,
}

/// <summary>Placement within the vertical space a parent offers.</summary>
public enum VerticalAlignment
{
    /// <summary>Fill the available height.</summary>
    Stretch,

    /// <summary>Size to content, against the top edge.</summary>
    Top,

    /// <summary>Size to content, centred.</summary>
    Center,

    /// <summary>Size to content, against the bottom edge.</summary>
    Bottom,
}

/// <summary>Font stroke weight, matching the names XAML accepts.</summary>
public enum FontWeight
{
    /// <summary>100.</summary>
    Thin,

    /// <summary>200.</summary>
    ExtraLight,

    /// <summary>300.</summary>
    Light,

    /// <summary>400.</summary>
    Normal,

    /// <summary>500.</summary>
    Medium,

    /// <summary>600.</summary>
    SemiBold,

    /// <summary>700.</summary>
    Bold,

    /// <summary>800.</summary>
    ExtraBold,

    /// <summary>900.</summary>
    Black,
}

/// <summary>Node kinds, matching the <c>kind</c> strings in dxir-v0.</summary>
public enum NodeKind
{
    /// <summary>The document root.</summary>
    UserControl,

    /// <summary>A single-child container with padding, border and corner radius.</summary>
    Border,

    /// <summary>A row/column container.</summary>
    Grid,

    /// <summary>A single-axis container with uniform spacing.</summary>
    StackPanel,

    Button,

    /// <summary>A run of text.</summary>
    TextBlock,

    /// <summary>A grid row definition. Carried as a child of the grid, distinguished by kind.</summary>
    RowDefinition,

    /// <summary>A grid column definition. Carried as a child of the grid, distinguished by kind.</summary>
    ColumnDefinition,
}

/// <summary>
/// What a runtime property write must invalidate. Carried per property by the compiler so that,
/// for example, a colour change repaints without re-running layout.
/// </summary>
[Flags]
public enum Invalidation
{
    /// <summary>Nothing to redo.</summary>
    None = 0,

    /// <summary>Desired sizes are stale.</summary>
    Measure = 1,

    /// <summary>Positions are stale.</summary>
    Arrange = 2,

    /// <summary>Pixels are stale.</summary>
    Paint = 4,

    /// <summary>The accessibility view is stale.</summary>
    Semantics = 8,
}
