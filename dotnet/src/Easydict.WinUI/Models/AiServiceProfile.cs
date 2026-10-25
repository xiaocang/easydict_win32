using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.WinUI.Models;

/// <summary>
/// One configured AI provider account. Profile identity is independent of its display name and
/// provider type. Secrets are deliberately stored by SettingsService in protected settings keys.
/// </summary>
public sealed class AiServiceProfile
{
    // Missing identities must remain invalid during deserialization. New profiles receive IDs
    // explicitly from SettingsService.AddAiServiceProfile.
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>
    /// Runtime service identity. Migrated profiles keep their historical service ID so saved
    /// window selections, actions and other references continue to resolve.
    /// </summary>
    public string ServiceId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int ConfigurationRevision { get; set; }

    /// <summary>Provider-specific non-secret options, encoded as invariant strings.</summary>
    public Dictionary<string, string> Configuration { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Unknown JSON fields are round-tripped to avoid damaging forward-version data.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public AiServiceProfile CopyWithConfiguration(
        Dictionary<string, string> configuration,
        string? name = null,
        bool? isEnabled = null)
    {
        return new AiServiceProfile
        {
            ProfileId = ProfileId,
            ServiceId = ServiceId,
            ProviderType = ProviderType,
            Name = name ?? Name,
            IsEnabled = isEnabled ?? IsEnabled,
            ConfigurationRevision = checked(ConfigurationRevision + 1),
            Configuration = new Dictionary<string, string>(configuration, StringComparer.Ordinal),
            ExtensionData = ExtensionData is null ? null : new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal)
        };
    }
}
