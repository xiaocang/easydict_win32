using System.Collections.Concurrent;
using Easydict.WinUI.Models;
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
        var assetName = ResolveProviderIconName(iconName);
        return IsGitHubIcon(assetName) && theme != ElementTheme.Dark
            ? GitHubOnLightIconName
            : assetName;
    }

    internal static string ResolveProviderIconName(string serviceId)
    {
        IReadOnlyList<AiServiceProfile> profiles;
        if (serviceId.StartsWith("ai-profile:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                profiles = SettingsService.Instance.AiServiceProfiles;
            }
            catch
            {
                // Keep icon resolution non-fatal during startup or after a profile has been removed.
                profiles = Array.Empty<AiServiceProfile>();
            }
            return ResolveProviderIconName(serviceId, profiles);
        }

        return ResolveNonProfileProviderIconName(serviceId);
    }

    internal static string ResolveProviderIconName(string serviceId, IEnumerable<AiServiceProfile> savedProfiles)
    {
        if (serviceId.StartsWith("ai-profile:", StringComparison.OrdinalIgnoreCase))
        {
            var profileId = serviceId["ai-profile:".Length..];
            var profile = savedProfiles.FirstOrDefault(candidate =>
                string.Equals(candidate.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));
            if (profile is not null && AiProviderCatalog.TryGet(profile.ProviderType, out var provider))
                return ProviderIconName(provider.Id);
            return serviceId;
        }

        return ResolveNonProfileProviderIconName(serviceId);
    }

    private static string ResolveNonProfileProviderIconName(string serviceId)
    {
        if (AiProviderCatalog.All.FirstOrDefault(provider =>
                string.Equals(provider.Id, serviceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(provider.LegacyServiceId, serviceId, StringComparison.OrdinalIgnoreCase)) is { } provider)
        {
            return ProviderIconName(provider.Id);
        }

        if (string.Equals(serviceId, "custom_openai", StringComparison.OrdinalIgnoreCase))
            return "CustomOpenAI";

        return serviceId;
    }

    private static string ProviderIconName(string providerId) => providerId switch
    {
        "openai" => "OpenAI",
        "ollama" => "Ollama",
        "deepseek" => "DeepSeek",
        "groq" => "Groq",
        "zhipu" => "Zhipu",
        "kimi" => "Kimi",
        "github" => "GitHub",
        "custom-openai" => "CustomOpenAI",
        "magpie" => "Magpie",
        "openrouter" => "OpenRouter",
        "orcarouter" => "OrcaRouter",
        "gemini" => "Gemini",
        "doubao" => "Doubao",
        "claude-code" => "claude-code",
        "codex" => "codex",
        _ => providerId
    };

    private static bool IsGitHubIcon(string iconName)
    {
        return string.Equals(iconName, "github", StringComparison.OrdinalIgnoreCase)
            || string.Equals(iconName, "GitHub", StringComparison.Ordinal);
    }
}
