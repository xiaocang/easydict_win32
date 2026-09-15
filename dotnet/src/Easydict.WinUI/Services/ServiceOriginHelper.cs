using Easydict.TranslationService;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Resolves where a service comes from and provides the user-facing wording for the
/// "plugin / imported" visual language used across result cards, the selection pop-up,
/// the Actions menu and the settings page.
/// </summary>
internal static class ServiceOriginHelper
{
    /// <summary>Service id prefix used by Bob plugin instances ("bob:&lt;identifier&gt;:&lt;instance&gt;").</summary>
    public const string BobServiceIdPrefix = "bob:";

    /// <summary>Service id prefix used by imported MDX dictionaries.</summary>
    public const string MdxServiceIdPrefix = "mdx::";

    /// <summary>
    /// Resolve the origin of a service. Prefers the live service's own declaration; falls back to
    /// id-prefix conventions so saved/history cards keep their marking after a plugin is removed.
    /// </summary>
    public static ServiceOrigin Resolve(ITranslationService? service, string? serviceId)
    {
        if (service is IServiceOriginProvider provider)
        {
            return provider.Origin;
        }

        return ResolveFromId(serviceId);
    }

    /// <summary>Resolve an origin from the service id alone.</summary>
    public static ServiceOrigin ResolveFromId(string? serviceId)
    {
        if (string.IsNullOrEmpty(serviceId))
        {
            return ServiceOrigin.BuiltIn;
        }

        if (serviceId.StartsWith(BobServiceIdPrefix, StringComparison.Ordinal))
        {
            return new ServiceOrigin(ServiceOriginKind.Plugin, "Bob");
        }

        if (serviceId.StartsWith(MdxServiceIdPrefix, StringComparison.Ordinal))
        {
            return new ServiceOrigin(ServiceOriginKind.ImportedDictionary, "MDX");
        }

        return ServiceOrigin.BuiltIn;
    }

    /// <summary>Resolve using the currently registered service when available.</summary>
    public static ServiceOrigin ResolveFromManager(string serviceId)
    {
        try
        {
            var services = TranslationManagerService.Instance.Manager.Services;
            return Resolve(services.TryGetValue(serviceId, out var service) ? service : null, serviceId);
        }
        catch
        {
            return ResolveFromId(serviceId);
        }
    }

    /// <summary>Short badge text ("Bob plugin", "MDX dictionary"); <c>null</c> for built-ins.</summary>
    public static string? BadgeText(ServiceOrigin origin)
    {
        var loc = LocalizationService.Instance;
        return origin.Kind switch
        {
            ServiceOriginKind.Plugin when string.Equals(origin.Label, "Bob", StringComparison.OrdinalIgnoreCase)
                => loc.GetStringOrDefault("ServiceOrigin_BobBadge", "Bob plugin"),
            ServiceOriginKind.Plugin => loc.GetStringOrDefault("ServiceOrigin_PluginBadge", "Plugin"),
            ServiceOriginKind.ImportedDictionary => loc.GetStringOrDefault("ServiceOrigin_MdxBadge", "MDX dictionary"),
            _ => null
        };
    }

    /// <summary>Explanatory tooltip; <c>null</c> for built-ins.</summary>
    public static string? Tooltip(ServiceOrigin origin)
    {
        var loc = LocalizationService.Instance;
        return origin.Kind switch
        {
            ServiceOriginKind.Plugin => string.Format(
                loc.GetStringOrDefault(
                    "ServiceOrigin_PluginTooltip",
                    "Third-party {0} plugin{1}. Not an Easydict built-in service; network requests and data handling are done by the plugin."),
                origin.Label ?? "Bob",
                string.IsNullOrEmpty(origin.Detail) ? string.Empty : $" ({origin.Detail})"),
            ServiceOriginKind.ImportedDictionary => loc.GetStringOrDefault(
                "ServiceOrigin_MdxTooltip",
                "Dictionary file imported by you. Looked up locally; not an Easydict built-in service."),
            _ => null
        };
    }

    /// <summary>
    /// Display name for plain-text contexts (dialogs, notifications) where no badge can be drawn.
    /// </summary>
    public static string FormatName(string displayName, ServiceOrigin origin)
    {
        var badge = BadgeText(origin);
        return string.IsNullOrEmpty(badge)
            ? displayName
            : string.Format(
                LocalizationService.Instance.GetStringOrDefault("ServiceOrigin_NameSuffix", "{0} ({1})"),
                displayName,
                badge);
    }
}
