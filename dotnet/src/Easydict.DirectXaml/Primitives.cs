namespace Easydict.DirectXaml;

/// <summary>Device-independent size in DIPs.</summary>
public readonly record struct Size(double Width, double Height)
{
    public static readonly Size Empty = new(0, 0);

    /// <summary>
    /// Stands in for an unbounded constraint. A finite value avoids the overflow and NaN traps
    /// that <see cref="double.PositiveInfinity"/> introduces once sizes start being added up.
    /// </summary>
    public const double Unbounded = 1_000_000;

    /// <summary>A constraint fixed in width and unbounded in height — how a list row is measured.</summary>
    public static Size FromWidth(double width) => new(width, Unbounded);

    public Size Deflate(Thickness by) =>
        new(Math.Max(0, Width - by.Horizontal), Math.Max(0, Height - by.Vertical));

    public Size Inflate(Thickness by) => new(Width + by.Horizontal, Height + by.Vertical);
}

/// <summary>Device-independent rectangle in DIPs, relative to the view origin.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public static readonly Rect Empty = new(0, 0, 0, 0);

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public Rect Deflate(Thickness by) =>
        new(X + by.Left, Y + by.Top, Math.Max(0, Width - by.Horizontal), Math.Max(0, Height - by.Vertical));

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>Left/top/right/bottom offsets, matching XAML's Thickness.</summary>
public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
{
    public static readonly Thickness Zero = new(0, 0, 0, 0);

    public Thickness(double uniform) : this(uniform, uniform, uniform, uniform)
    {
    }

    public double Horizontal => Left + Right;

    public double Vertical => Top + Bottom;

    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>Per-corner radii, in XAML's order.</summary>
public readonly record struct CornerRadius(
    double TopLeft,
    double TopRight,
    double BottomRight,
    double BottomLeft)
{
    public static readonly CornerRadius Zero = new(0, 0, 0, 0);

    public CornerRadius(double uniform) : this(uniform, uniform, uniform, uniform)
    {
    }

    public bool IsZero => TopLeft == 0 && TopRight == 0 && BottomRight == 0 && BottomLeft == 0;

    /// <summary>
    /// Win2D draws rounded rectangles with a single x/y radius pair, so a non-uniform radius has
    /// to be approximated until the executor grows a path-based fallback.
    /// </summary>
    public double Uniform => Math.Max(Math.Max(TopLeft, TopRight), Math.Max(BottomRight, BottomLeft));
}

/// <summary>
/// Straight ARGB, deliberately not <c>Windows.UI.Color</c> so this assembly stays platform-neutral.
/// </summary>
public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    public static readonly Color Transparent = new(0, 0, 0, 0);

    public bool IsTransparent => A == 0;

    /// <summary>Parses the <c>#AARRGGBB</c> form the compiler emits.</summary>
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

public enum Visibility
{
    Visible,
    Collapsed,
}

public enum Orientation
{
    Vertical,
    Horizontal,
}

public enum TextWrapping
{
    NoWrap,
    Wrap,
    WrapWholeWords,
}

public enum TextTrimming
{
    None,
    CharacterEllipsis,
    WordEllipsis,
    Clip,
}

public enum HorizontalAlignment
{
    Stretch,
    Left,
    Center,
    Right,
}

public enum VerticalAlignment
{
    Stretch,
    Top,
    Center,
    Bottom,
}

public enum FontWeight
{
    Thin,
    ExtraLight,
    Light,
    Normal,
    Medium,
    SemiBold,
    Bold,
    ExtraBold,
    Black,
}

/// <summary>Node kinds, matching the <c>kind</c> strings in dxir-v0.</summary>
public enum NodeKind
{
    UserControl,
    Border,
    Grid,
    StackPanel,
    TextBlock,
    RowDefinition,
    ColumnDefinition,
}

/// <summary>What a runtime property write must invalidate.</summary>
[Flags]
public enum Invalidation
{
    None = 0,
    Measure = 1,
    Arrange = 2,
    Paint = 4,
    Semantics = 8,
}
