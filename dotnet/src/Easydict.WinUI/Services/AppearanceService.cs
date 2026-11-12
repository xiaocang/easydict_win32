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
    public double PhoneticFontSize { get; init; }
}

/// <summary>
/// Single source of truth for user-adjustable appearance (currently the result
/// font scale). Mirrors the static-service pattern used by
/// <see cref="MinimalThemeService"/>: it reads <see cref="SettingsService"/> and
/// exposes effective sizes as an on-demand snapshot (<see cref="CurrentSnapshot"/>).
/// Callers pull the current values after a settings change (via
/// <c>App.ApplyAppearance()</c>); there is no push/event flow.
/// </summary>
internal static class AppearanceService
{
    // Base font sizes hardcoded in the result-item XAML today.
    private const double BaseResultFontSize = 14.0;
    private const double BaseServiceNameFontSize = 12.0;
    private const double BaseStatusFontSize = 10.0;

    /// <summary>
    /// The phonetic badge size that shipped before issue #249. Kept as the base so the
    /// phonetic scale reads as "how much bigger than the old unreadable badges".
    /// </summary>
    private const double BasePhoneticFontSize = 10.0;

    private const double MinFontScale = 0.85;
    private const double MaxFontScale = 1.4;

    private const double MinPhoneticFontScale = 1.0;
    private const double MaxPhoneticFontScale = 2.4;

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

    /// <summary>Clamped phonetic-size multiplier from settings.</summary>
    public static double PhoneticFontScale =>
        Math.Clamp(SettingsService.Instance.PhoneticFontScale, MinPhoneticFontScale, MaxPhoneticFontScale);

    /// <summary>
    /// Effective phonetic (IPA) badge font size in DIPs. Phonetics follow the global result
    /// font scale — enlarging all result text must not leave them behind — and carry their
    /// own multiplier on top, which is what issue #249 asked for.
    /// <para>
    /// There is deliberately no additional ceiling: both multipliers are already bounded by
    /// their sliders, so the composition tops out at 33.6 DIP. An extra cap would silently
    /// make the top of the phonetic slider do nothing once the result scale is raised.
    /// </para>
    /// </summary>
    public static double PhoneticFontSize => BasePhoneticFontSize * PhoneticFontScale * FontScale;

    /// <summary>Captures the current effective metrics into an immutable snapshot.</summary>
    public static AppearanceSettings CurrentSnapshot() => new()
    {
        ResultFontSize = ResultFontSize,
        ServiceNameFontSize = ServiceNameFontSize,
        StatusFontSize = StatusFontSize,
        PhoneticFontSize = PhoneticFontSize,
    };
}
