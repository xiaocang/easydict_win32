namespace Easydict.TranslationService.Services.ModelCatalog;

/// <summary>
/// A translation service that can enumerate the models its provider currently offers.
/// Implemented by the aggregator services (OpenRouter, OrcaRouter) whose catalogs change
/// often enough that a hard-coded list goes stale.
/// </summary>
public interface IModelCatalogProvider
{
    /// <summary>
    /// Absolute URL of the provider's OpenAI-compatible <c>/models</c> endpoint.
    /// </summary>
    string ModelsEndpoint { get; }

    /// <summary>
    /// Fetch the provider's live model catalog, free models sorted first.
    /// </summary>
    Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(CancellationToken cancellationToken = default);
}
