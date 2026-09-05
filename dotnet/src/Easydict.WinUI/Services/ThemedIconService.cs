using System.Drawing;



namespace Easydict.WinUI.Services;

/// <summary>Selects pre-generated icon assets without raster conversion at runtime.</summary>
internal static class ThemedIconService
{
    internal static bool UseDarkWindowIcon(string theme, bool systemDark) => theme switch
    {
        "Dark" => true,
        "Light" or "Minimal" => false,
        _ => systemDark
    };

    internal static bool IsWindowDark => ThemeResourceService.IsHighContrastActive()
        ? SystemColors.Window.GetBrightness() < 0.5f
        : UseDarkWindowIcon(SettingsService.Instance.AppTheme, SystemThemeProbe.IsSystemDark() ?? false);

    internal static bool IsTaskbarDark => ThemeResourceService.IsHighContrastActive()
        ? SystemColors.Window.GetBrightness() < 0.5f
        : SystemThemeProbe.IsTaskbarDark() ?? true;

    internal static string DarkIconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "Dark", "AppIcon.ico");
}