using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LocalApi;

/// <summary>
/// Immutable snapshot of local API server configuration.
/// Passed to <see cref="LocalApiServer.StartAsync"/>; never mutated after creation.
/// </summary>
public sealed class LocalApiOptions
{
    public static LocalApiOptions Disabled { get; } = new()
    {
        Port = 0,
        Token = string.Empty,
        ExposedServiceIds = new HashSet<string>(),
        CorsMode = LocalApiCorsMode.Any,
        AllowedOrigins = Array.Empty<string>(),
        DefaultTargetLanguage = Language.SimplifiedChinese,
    };

    public required int Port { get; init; }
    public required string Token { get; init; }
    public required IReadOnlySet<string> ExposedServiceIds { get; init; }
    public required LocalApiCorsMode CorsMode { get; init; }
    public required IReadOnlyList<string> AllowedOrigins { get; init; }
    public required Language DefaultTargetLanguage { get; init; }
}

public enum LocalApiCorsMode
{
    /// <summary>Allow any origin (sets <c>Access-Control-Allow-Origin: *</c>).</summary>
    Any,
    /// <summary>Only echo Origin if present in <see cref="LocalApiOptions.AllowedOrigins"/>.</summary>
    AllowList,
}
