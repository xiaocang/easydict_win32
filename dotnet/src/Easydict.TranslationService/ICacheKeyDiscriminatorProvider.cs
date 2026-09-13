namespace Easydict.TranslationService;

/// <summary>
/// Optional interface for services whose output depends on configuration that is not part of the
/// <see cref="Models.TranslationRequest"/> (plugin version, prompt options, model selection, ...).
/// The discriminator is folded into the translation cache key so that changing the configuration
/// never replays a stale cached result.
/// </summary>
public interface ICacheKeyDiscriminatorProvider
{
    /// <summary>
    /// A short, stable string describing the current configuration, or <c>null</c> when the
    /// service output depends only on the request.
    /// </summary>
    string? CacheKeyDiscriminator { get; }
}
