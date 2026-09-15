namespace Easydict.BobPlugin;

/// <summary>
/// Service ids for plugin instances. Plugins get their own namespace so they can never collide
/// with (or silently replace) a built-in service, and so the UI can recognise them by id alone
/// when the instance itself is gone.
/// </summary>
public static class BobServiceIds
{
    /// <summary>Prefix of every Bob plugin service id.</summary>
    public const string Prefix = "bob:";

    /// <summary>Build the service id for one installed instance of a plugin.</summary>
    public static string Build(string pluginIdentifier, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return $"{Prefix}{pluginIdentifier}:{instanceId}";
    }

    /// <summary>Split a plugin service id back into its identifier and instance parts.</summary>
    public static bool TryParse(string? serviceId, out string pluginIdentifier, out string instanceId)
    {
        pluginIdentifier = string.Empty;
        instanceId = string.Empty;

        if (string.IsNullOrEmpty(serviceId) || !serviceId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = serviceId.Substring(Prefix.Length);
        var separator = rest.LastIndexOf(':');
        if (separator <= 0 || separator == rest.Length - 1)
        {
            return false;
        }

        pluginIdentifier = rest.Substring(0, separator);
        instanceId = rest.Substring(separator + 1);
        return true;
    }

    /// <summary>True when the id belongs to a Bob plugin instance.</summary>
    public static bool IsPluginServiceId(string? serviceId)
        => serviceId is not null && serviceId.StartsWith(Prefix, StringComparison.Ordinal);
}
