using Easydict.TranslationService.Models;

namespace Easydict.BobPlugin;

/// <summary>
/// Everything the host needs to run one installed plugin instance. The same plugin can be
/// installed more than once (different options), which is why the instance, not the plugin, is
/// what maps to a translation service.
/// </summary>
public sealed record BobPluginInstanceDescriptor
{
    /// <summary>Easydict service id, built by <see cref="BobServiceIds.Build"/>.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Reverse-DNS identifier from the manifest.</summary>
    public required string PluginIdentifier { get; init; }

    /// <summary>Installed plugin version.</summary>
    public required string Version { get; init; }

    /// <summary>Name shown in the service list.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Directory holding the extracted plugin.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>Private, writable directory for this instance's <c>$file</c> storage.</summary>
    public required string SandboxDirectory { get; init; }

    /// <summary>Icon shipped by the plugin, if any.</summary>
    public string? IconPath { get; init; }

    /// <summary>
    /// Option values passed to the plugin as <c>$option</c>, with secure values already merged in
    /// by the host. Never persisted from here.
    /// </summary>
    public IReadOnlyDictionary<string, string> OptionValues { get; init; } = new Dictionary<string, string>();

    /// <summary>Ids of options that hold credentials, used to decide whether the service is configured.</summary>
    public IReadOnlyList<string> SecureOptionIds { get; init; } = [];

    /// <summary>
    /// Bob language codes the plugin declares, probed once at install time so startup never has to
    /// spin up a JavaScript engine. Empty means "no restriction".
    /// </summary>
    public IReadOnlyList<string> SupportedLanguageCodes { get; init; } = [];

    /// <summary>
    /// How much of the host's automatic behavior this plugin may receive. Conservative until the
    /// user opts in, because a plugin can have side effects the host cannot see.
    /// </summary>
    public ServiceExecutionPolicy Policy { get; init; } = ServiceExecutionPolicy.Conservative;

    /// <summary>How long a single synchronous script slice may run before the engine cuts it off.</summary>
    public int SliceTimeoutSeconds { get; init; } = 15;
}

/// <summary>Outcome of a plugin's optional self-check.</summary>
/// <param name="Status">Whether the plugin validated, failed, or has no check.</param>
/// <param name="Message">Failure detail, when the plugin supplied one.</param>
public sealed record BobValidationOutcome(BobValidationStatus Status, string? Message = null)
{
    public static BobValidationOutcome Valid { get; } = new(BobValidationStatus.Valid);
    public static BobValidationOutcome NotSupported { get; } = new(BobValidationStatus.NotSupported);
}

/// <summary>Result of calling a plugin's <c>pluginValidate</c>.</summary>
public enum BobValidationStatus
{
    /// <summary>The plugin reported that its configuration works.</summary>
    Valid,

    /// <summary>The plugin reported a problem.</summary>
    Invalid,

    /// <summary>The plugin does not implement a self-check.</summary>
    NotSupported
}
