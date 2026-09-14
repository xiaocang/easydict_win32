namespace Easydict.BobPlugin;

/// <summary>
/// Why loading or running a Bob plugin failed, as opposed to a translation failing.
/// </summary>
public enum BobPluginError
{
    /// <summary>info.json is missing, unparsable or lacks required fields.</summary>
    InvalidManifest,

    /// <summary>The package is not a readable .bobplugin archive, or violates the extraction limits.</summary>
    InvalidPackage,

    /// <summary>The plugin declares a category this host does not support.</summary>
    UnsupportedCategory,

    /// <summary>main.js is missing, or failed to parse / evaluate at load time.</summary>
    ScriptLoadFailed,

    /// <summary>The plugin does not expose the entry point required for its category.</summary>
    MissingEntryPoint
}

/// <summary>
/// Thrown for plugin packaging and loading problems. Translation failures raised by a running
/// plugin surface as <see cref="TranslationService.TranslationException"/> instead.
/// </summary>
public sealed class BobPluginException : Exception
{
    public BobPluginException(BobPluginError code, string message) : base(message) => Code = code;

    public BobPluginException(BobPluginError code, string message, Exception inner) : base(message, inner) => Code = code;

    /// <summary>Machine-readable reason.</summary>
    public BobPluginError Code { get; }
}
