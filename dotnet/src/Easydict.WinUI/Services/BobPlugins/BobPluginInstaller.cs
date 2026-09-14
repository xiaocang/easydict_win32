using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Easydict.BobPlugin;
using Easydict.BobPlugin.Manifest;
using Easydict.BobPlugin.Package;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services.BobPlugins;

/// <summary>
/// Installs, describes and removes Bob plugins on disk.
///
/// Everything lands under the settings directory so an installed plugin travels with the user's
/// profile, and each instance gets a private sandbox that its <c>$file</c> access is confined to.
/// </summary>
public static class BobPluginInstaller
{
    /// <summary>Root of every installed plugin.</summary>
    public static string PluginsRoot => Path.Combine(SettingsService.ResolveSettingsDirectory(), "plugins", "bob");

    /// <summary>Root of the per-instance sandboxes.</summary>
    public static string SandboxRoot => Path.Combine(PluginsRoot, "_sandbox");

    private static string StagingRoot => Path.Combine(PluginsRoot, "_staging");

    /// <summary>
    /// Extract a .bobplugin, verify it is a translate plugin, and move it into place.
    /// The returned descriptor is not yet persisted; the caller adds it to settings.
    /// </summary>
    /// <exception cref="BobPluginException">The package is unreadable, unsafe or unsupported.</exception>
    public static SettingsService.InstalledBobPlugin Install(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        var staging = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var staged = BobPluginPackage.Extract(packagePath, staging);
            if (!staged.Manifest.IsTranslatePlugin)
            {
                throw new BobPluginException(
                    BobPluginError.UnsupportedCategory,
                    $"This host only supports plugins of category '{BobPluginManifest.TranslateCategory}'; " +
                    $"'{staged.Manifest.Name}' is '{staged.Manifest.Category}'.");
            }

            var manifest = staged.Manifest;
            var installDirectory = Path.Combine(PluginsRoot, SanitizeSegment(manifest.Identifier), SanitizeSegment(manifest.Version));

            // Reinstalling the same version replaces the files but keeps the instance, so the
            // user's options and the service id survive an update.
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);
            Directory.Move(staged.Directory, installDirectory);

            var installed = BobPluginPackage.LoadFromDirectory(installDirectory);
            var instanceId = BuildInstanceId(manifest.Identifier);

            return new SettingsService.InstalledBobPlugin
            {
                ServiceId = BobServiceIds.Build(manifest.Identifier, instanceId),
                PluginIdentifier = manifest.Identifier,
                InstanceId = instanceId,
                Version = manifest.Version,
                DisplayName = manifest.Name,
                InstallDirectory = installDirectory,
                IconPath = installed.IconPath,
                SupportedLanguageCodes = [],
                OptionValues = SeedOptionValues(manifest),
                SecureOptionIds = manifest.SecureOptions.Select(o => o.Identifier).ToList()
            };
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Turn a stored plugin into what the runtime needs, merging in the secure option values the
    /// credential store holds.
    /// </summary>
    public static BobPluginInstanceDescriptor ToDescriptor(SettingsService.InstalledBobPlugin plugin, SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(settings);

        var options = new Dictionary<string, string>(plugin.OptionValues, StringComparer.Ordinal);
        foreach (var optionId in plugin.SecureOptionIds)
        {
            var value = settings.GetBobPluginSecureOption(plugin.ServiceId, optionId);
            options[optionId] = value ?? string.Empty;
        }

        var sandbox = Path.Combine(SandboxRoot, SanitizeSegment(plugin.PluginIdentifier), SanitizeSegment(plugin.InstanceId));
        Directory.CreateDirectory(sandbox);

        return new BobPluginInstanceDescriptor
        {
            ServiceId = plugin.ServiceId,
            PluginIdentifier = plugin.PluginIdentifier,
            Version = plugin.Version,
            DisplayName = string.IsNullOrWhiteSpace(plugin.DisplayName) ? plugin.PluginIdentifier : plugin.DisplayName,
            InstallDirectory = plugin.InstallDirectory,
            SandboxDirectory = sandbox,
            IconPath = plugin.IconPath,
            OptionValues = options,
            SecureOptionIds = plugin.SecureOptionIds,
            SupportedLanguageCodes = plugin.SupportedLanguageCodes,
            Policy = new ServiceExecutionPolicy(
                AllowHostRetry: plugin.AllowHostRetry,
                AllowResultCache: plugin.AllowResultCache,
                AllowPhoneticEnrichment: plugin.AllowPhoneticEnrichment)
        };
    }

    /// <summary>Read the manifest of an installed plugin, or <c>null</c> when its files are gone.</summary>
    public static BobPluginManifest? TryReadManifest(SettingsService.InstalledBobPlugin plugin)
    {
        try
        {
            return BobPluginPackage.LoadFromDirectory(plugin.InstallDirectory).Manifest;
        }
        catch (Exception ex) when (ex is BobPluginException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[BobPluginInstaller] Cannot read manifest of '{plugin.ServiceId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Delete a plugin's files and its sandbox. Never throws.</summary>
    public static void DeleteFiles(SettingsService.InstalledBobPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        TryDelete(plugin.InstallDirectory);
        TryDelete(Path.Combine(SandboxRoot, SanitizeSegment(plugin.PluginIdentifier), SanitizeSegment(plugin.InstanceId)));

        // Remove the now-empty version parent so reinstalling starts clean.
        var parent = Path.GetDirectoryName(plugin.InstallDirectory);
        try
        {
            if (parent is not null && Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, string> SeedOptionValues(BobPluginManifest manifest)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in manifest.Options)
        {
            if (option.IsSecure)
            {
                continue;   // held by the credential store, never in settings.json
            }

            values[option.Identifier] = option.DefaultValue
                ?? (option.MenuValues.Count > 0 ? option.MenuValues[0].Value : string.Empty);
        }

        return values;
    }

    /// <summary>
    /// A short stable instance id, so the same plugin installed twice cannot share a service id
    /// while a reinstall of the same plugin keeps one.
    /// </summary>
    private static string BuildInstanceId(string identifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return Convert.ToHexString(hash)[..6].ToLowerInvariant();
    }

    /// <summary>Make a manifest value safe to use as one path segment.</summary>
    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        var sanitized = builder.ToString().Trim('.', ' ');
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[BobPluginInstaller] Could not delete '{directory}': {ex.Message}");
        }
    }
}
