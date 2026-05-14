using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Services;

internal static class ServiceIconAssetResolver
{
    public const string GitHubOnLightIconName = "GitHubOnLight";

    public static Uri GetIconUri(string iconName, ElementTheme theme)
    {
        return new Uri($"ms-appx:///Assets/ServiceIcons/{GetIconName(iconName, theme)}.png");
    }

    public static string GetIconName(string iconName, ElementTheme theme)
    {
        return IsGitHubIcon(iconName) && theme != ElementTheme.Dark
            ? GitHubOnLightIconName
            : iconName;
    }

    private static bool IsGitHubIcon(string iconName)
    {
        return string.Equals(iconName, "github", StringComparison.OrdinalIgnoreCase)
            || string.Equals(iconName, "GitHub", StringComparison.Ordinal);
    }
}
