using System;

namespace Easydict.WinUI.Services;

/// <summary>
/// Immutable snapshot of the effective appearance metrics, computed from
/// <see cref="SettingsService"/>. Passed into result-item controls so each
/// one does not re-read the service. See issue #172.
/// </summary>
public readonly struct AppearanceSettings
{
    public double ResultFontSize { get; init; }
    public double ServiceNameFontSize { get; init; }
    public double StatusFontSize { get; init; }
}

/// <summary>
/// Single source of truth for user-adjustable appearance (currently the result
/// font scale). Mirrors the static-service pattern used by
/// <see cref="MinimalThemeService"/>: it reads <see cref="SettingsService"/>,
/// derives effective sizes, and raises <see cref="AppearanceChanged"/> so the
/// app can re-broadcast to all windows.
/// </summary>
internal static class AppearanceService
{
    // Base font sizes hardcoded in the result-item XAML today.
    private const double BaseResultFontSize = 13.0;
    private const double BaseServiceNameFontSize = 12.0;
    private const double BaseStatusFontSize = 10.0;

    private const double MinFontScale = 0.85;
    private const double MaxFontScale = 1.4;

    /// <summary>
    /// Minimum height (DIPs) the floating windows shrink to. Lowered from the
    /// previous hardcoded 200 so short results take less screen space (issue #172).
    /// </summary>
    public const double MinFloatingWindowHeightDips = 110.0;

    /// <summary>Clamped font-size multiplier from settings.</summary>
    public static double FontScale =>
        Math.Clamp(SettingsService.Instance.ResultFontScale, MinFontScale, MaxFontScale);

    public static double ResultFontSize => BaseResultFontSize * FontScale;
    public static double ServiceNameFontSize => BaseServiceNameFontSize * FontScale;
    public static double StatusFontSize => BaseStatusFontSize * FontScale;

    /// <summary>Captures the current effective metrics into an immutable snapshot.</summary>
    public static AppearanceSettings CurrentSnapshot() => new()
    {
        ResultFontSize = ResultFontSize,
        ServiceNameFontSize = ServiceNameFontSize,
        StatusFontSize = StatusFontSize,
    };

    /// <summary>Raised when an appearance setting changes and windows must re-apply.</summary>
    public static event EventHandler? AppearanceChanged;

    /// <summary>Notify listeners that appearance settings changed.</summary>
    public static void NotifyChanged() => AppearanceChanged?.Invoke(null, EventArgs.Empty);
}
