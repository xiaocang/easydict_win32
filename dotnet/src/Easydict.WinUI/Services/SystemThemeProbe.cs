using Microsoft.Win32;

namespace Easydict.WinUI.Services;

/// <summary>
/// Detects the current Windows app theme by reading the Personalize registry key
/// (<c>AppsUseLightTheme</c>). <see cref="Windows.UI.ViewManagement.UISettings"/>
/// returns a fixed value in WinAppSDK desktop apps and does not track the OS theme,
/// so we read the registry directly.
/// </summary>
internal static class SystemThemeProbe
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>
    /// Returns <see langword="true"/> when Windows is in dark mode for apps,
    /// <see langword="false"/> for light mode, or <see langword="null"/> when the
    /// registry value cannot be read.
    /// </summary>
    public static bool? IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            if (key?.GetValue(AppsUseLightThemeValue) is int value)
            {
                return value == 0;
            }
        }
        catch
        {
        }

        return null;
    }
}
