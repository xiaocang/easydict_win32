namespace Easydict.WinUI.Services;

/// <summary>
/// Labs tray-icon picker layout. Four previews stay on one row while each image
/// is at least <see cref="MinimumReadableImage"/> DIP. Narrower panes wrap to two
/// columns and clamp the image into the column. Keeping the 160 DIP XAML size
/// clips the choices because the settings pane does not scroll horizontally.
/// </summary>
internal readonly record struct TrayIconOptionLayout(int Columns, double ImageSize)
{
    internal const double MinimumReadableImage = 48;
    internal const double MaximumImage = 320;
    internal const double ColumnGap = 12;

    /// <summary>Radio-button border and padding around the preview image.</summary>
    internal const double ImageChrome = 20;

    internal static TrayIconOptionLayout Compute(double availableWidth)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
            return new(2, 1);

        var across = Fit(availableWidth, 4);
        if (across >= MinimumReadableImage)
            return new(4, Math.Min(across, MaximumImage));

        return new(2, Math.Clamp(Fit(availableWidth, 2), 1, MaximumImage));
    }

    private static double Fit(double availableWidth, int columns)
    {
        var column = (availableWidth - ColumnGap * (columns - 1)) / columns;
        return Math.Floor(column - ImageChrome);
    }
}
