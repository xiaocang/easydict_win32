namespace Easydict.TranslationService.Models;

/// <summary>
/// Where a translation service comes from. UI surfaces use this to visually separate third-party
/// plugins and user-imported dictionaries from Easydict's built-in services.
/// </summary>
public enum ServiceOriginKind
{
    /// <summary>Shipped with Easydict.</summary>
    BuiltIn,

    /// <summary>A dictionary file the user imported (for example an MDX dictionary).</summary>
    ImportedDictionary,

    /// <summary>A third-party plugin executed by a compatibility runtime (for example a Bob plugin).</summary>
    Plugin
}

/// <summary>
/// Provenance metadata for a service.
/// </summary>
/// <param name="Kind">The origin category.</param>
/// <param name="Label">Short label for the origin family (for example "Bob" or "MDX"); <c>null</c> for built-ins.</param>
/// <param name="Detail">Optional detail such as a plugin identifier and version.</param>
public sealed record ServiceOrigin(ServiceOriginKind Kind, string? Label = null, string? Detail = null)
{
    /// <summary>Origin of every built-in service.</summary>
    public static ServiceOrigin BuiltIn { get; } = new(ServiceOriginKind.BuiltIn);

    /// <summary>True for services shipped with Easydict.</summary>
    public bool IsNative => Kind == ServiceOriginKind.BuiltIn;
}

/// <summary>
/// Optional interface for services that are not built-in. Services that do not implement it are
/// treated as <see cref="ServiceOrigin.BuiltIn"/>.
/// </summary>
public interface IServiceOriginProvider
{
    /// <summary>The origin of this service.</summary>
    ServiceOrigin Origin { get; }
}
