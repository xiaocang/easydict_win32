namespace Easydict.TranslationService.Models;

/// <summary>Chooses the services for an ordinary query or an explicit service action.</summary>
public static class ServiceQuerySelection
{
    public static bool IsEnabled(
        string serviceId,
        ServiceOrigin origin,
        IReadOnlyDictionary<string, bool> enabledQuerySettings,
        string? focusedServiceId = null)
    {
        if (focusedServiceId is not null)
        {
            return string.Equals(serviceId, focusedServiceId, StringComparison.Ordinal);
        }

        return origin.Kind != ServiceOriginKind.Plugin
            && (!enabledQuerySettings.TryGetValue(serviceId, out var enabled) || enabled);
    }
}
