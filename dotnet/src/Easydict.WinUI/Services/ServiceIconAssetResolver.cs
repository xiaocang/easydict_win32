using System.Collections.Concurrent;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Services;

internal static class ServiceIconAssetResolver
{
    public const string GitHubOnLightIconName = "GitHubOnLight";

    /// <summary>
    /// Icons that ship with a service rather than with the app (Bob plugins). Registered when the
    /// service is registered, so the result cards, the settings list and the selection strip all
    /// resolve the same image through <see cref="GetIconUri"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Uri> _fileIcons = new(StringComparer.Ordinal);

    /// <summary>Point a service id at an icon file on disk. A missing or unreadable path is ignored.</summary>
    public static void RegisterFileIcon(string serviceId, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            _fileIcons.TryRemove(serviceId, out _);
            return;
        }

        if (Uri.TryCreate(iconPath, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            _fileIcons[serviceId] = uri;
        }
    }

    /// <summary>Forget a service's file icon (the plugin was removed).</summary>
    public static void UnregisterFileIcon(string serviceId) => _fileIcons.TryRemove(serviceId, out _);

    /// <summary>True when this service brings its own icon file.</summary>
    public static bool HasFileIcon(string serviceId) => _fileIcons.ContainsKey(serviceId);

    public static Uri GetIconUri(string iconName, ElementTheme theme)
    {
        if (_fileIcons.TryGetValue(iconName, out var fileUri))
        {
            return fileUri;
        }

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
