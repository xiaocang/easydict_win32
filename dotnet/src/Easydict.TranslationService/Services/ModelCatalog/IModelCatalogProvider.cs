namespace Easydict.TranslationService.Services.ModelCatalog;

/// <summary>
/// A translation service that can enumerate the models its provider currently offers.
/// Supports both OpenAI-compatible catalogs and provider-native lists such as Ollama's.
/// </summary>
public interface IModelCatalogProvider
{
    /// <summary>
    /// Absolute URL of the provider's model-list endpoint (for example <c>/models</c> or <c>/api/tags</c>).
    /// </summary>
    string ModelsEndpoint { get; }

    /// <summary>
    /// Fetch the provider's live model catalog, free models sorted first.
    /// </summary>
    Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(CancellationToken cancellationToken = default);
}
