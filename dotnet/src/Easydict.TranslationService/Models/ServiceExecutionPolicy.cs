namespace Easydict.TranslationService.Models;

/// <summary>
/// Declares how much of the host's automatic behavior a translation service is allowed to receive.
/// Built-in services use <see cref="Default"/>. Third-party or side-effecting services (for example
/// plugins that write to external systems) should use <see cref="Conservative"/> so that a single
/// user action maps to a single execution: no automatic retries, no cached replays and no extra
/// network calls made on the service's behalf.
/// </summary>
/// <param name="AllowHostRetry">Whether <see cref="TranslationManager"/> may retry a failed request.</param>
/// <param name="AllowResultCache">Whether results may be served from or written to the translation cache.</param>
/// <param name="AllowPhoneticEnrichment">Whether the host may call another provider to add missing phonetics.</param>
public sealed record ServiceExecutionPolicy(
    bool AllowHostRetry = true,
    bool AllowResultCache = true,
    bool AllowPhoneticEnrichment = true)
{
    /// <summary>Behavior applied to every service that does not declare a policy.</summary>
    public static ServiceExecutionPolicy Default { get; } = new();

    /// <summary>One user action = one execution: no retry, no cache, no host-initiated enrichment.</summary>
    public static ServiceExecutionPolicy Conservative { get; } = new(false, false, false);
}
