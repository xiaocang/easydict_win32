namespace Easydict.DirectXaml;

/// <summary>A resolved <c>Width</c>/<c>Height</c>: either <c>Auto</c> or a fixed DIP value.</summary>
/// <param name="IsAuto">True when the size follows content rather than a fixed value.</param>
/// <param name="Dips">The fixed size in DIPs. Meaningless when <paramref name="IsAuto"/> is true.</param>
public readonly record struct LengthValue(bool IsAuto, double Dips)
{
    /// <summary>Size to content.</summary>
    public static readonly LengthValue Auto = new(true, 0);

    /// <summary>A fixed size in DIPs.</summary>
    public static LengthValue Fixed(double dips) => new(false, dips);
}

/// <summary>How a grid track derives its size.</summary>
public enum GridUnit
{
    /// <summary>Sized to the largest child in the track.</summary>
    Auto,

    /// <summary>A fixed size in DIPs.</summary>
    Dip,

    /// <summary>A share of the space left after fixed and auto tracks are placed.</summary>
    Star,
}

/// <summary>A resolved row height or column width.</summary>
/// <param name="Unit">How the size is derived.</param>
/// <param name="Value">DIPs for <see cref="GridUnit.Dip"/>, the weight for <see cref="GridUnit.Star"/>, unused for <see cref="GridUnit.Auto"/>.</param>
public readonly record struct GridLengthValue(GridUnit Unit, double Value)
{
    /// <summary>A track sized to its content.</summary>
    public static readonly GridLengthValue Auto = new(GridUnit.Auto, 0);

    /// <summary>A track of fixed size.</summary>
    public static GridLengthValue Dip(double value) => new(GridUnit.Dip, value);

    /// <summary>A track taking a weighted share of the remaining space.</summary>
    public static GridLengthValue Star(double weight) => new(GridUnit.Star, weight);
}
