namespace Easydict.DirectXaml;

/// <summary>A resolved <c>Width</c>/<c>Height</c>: either <c>Auto</c> or a fixed DIP value.</summary>
public readonly record struct LengthValue(bool IsAuto, double Dips)
{
    public static readonly LengthValue Auto = new(true, 0);

    public static LengthValue Fixed(double dips) => new(false, dips);
}

public enum GridUnit
{
    Auto,
    Dip,
    Star,
}

/// <summary>A resolved row height or column width.</summary>
public readonly record struct GridLengthValue(GridUnit Unit, double Value)
{
    public static readonly GridLengthValue Auto = new(GridUnit.Auto, 0);

    public static GridLengthValue Dip(double value) => new(GridUnit.Dip, value);

    public static GridLengthValue Star(double weight) => new(GridUnit.Star, weight);
}
