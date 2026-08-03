namespace Easydict.DirectXaml.Render;

/// <summary>
/// Backend-neutral geometry for a compact indeterminate loading spinner.
/// </summary>
public static class LoadingSpinnerGeometry
{
    /// <summary>The fixed number of dots in the spinner.</summary>
    public const int SegmentCount = 8;

    /// <summary>Gets one animated spinner dot for a frame and segment.</summary>
    public static SpinnerDot GetDot(Rect bounds, int frame, int segment)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segment, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(segment, SegmentCount);

        double diameter = Math.Min(bounds.Width, bounds.Height);
        if (bounds.IsEmpty || diameter <= 0)
        {
            return default;
        }

        int normalizedFrame = ((frame % SegmentCount) + SegmentCount) % SegmentCount;
        int age = (normalizedFrame - segment + SegmentCount) % SegmentCount;
        double centerX = bounds.X + (bounds.Width / 2);
        double centerY = bounds.Y + (bounds.Height / 2);
        double orbitRadius = Math.Max(0, (diameter / 2) - 1);
        double angle = (Math.PI * 2 * segment) / SegmentCount;

        return new SpinnerDot(
            centerX + (Math.Cos(angle) * orbitRadius),
            centerY + (Math.Sin(angle) * orbitRadius),
            Math.Max(0.5, diameter * 0.12),
            (SegmentCount - age) / (double)SegmentCount);
    }
}

/// <summary>A single dot in an indeterminate loading spinner.</summary>
public readonly record struct SpinnerDot(double X, double Y, double Radius, double Opacity);
