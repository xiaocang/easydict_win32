using System.Runtime.CompilerServices;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services.ModelCatalog;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Adds a stable profile identity and display name around a provider implementation. The wrapped
/// service must be owned by that profile and must not be reconfigured for another profile.
/// </summary>
public class ProfiledTranslationService : ITranslationService, ICacheKeyDiscriminatorProvider
{
    protected readonly ITranslationService InnerService;
    private readonly bool _isEnabled;

    public ProfiledTranslationService(
        ITranslationService innerService,
        string serviceId,
        string displayName,
        string profileId,
        int configurationRevision,
        bool isEnabled = true)
    {
        InnerService = innerService ?? throw new ArgumentNullException(nameof(innerService));
        ServiceId = string.IsNullOrWhiteSpace(serviceId)
            ? throw new ArgumentException("A profile runtime service ID is required.", nameof(serviceId))
            : serviceId;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? throw new ArgumentException("A profile display name is required.", nameof(displayName))
            : displayName;
        CacheKeyDiscriminator = $"{profileId}:{configurationRevision}";
        _isEnabled = isEnabled;
    }

    public string ServiceId { get; }
    public string DisplayName { get; }
    public bool RequiresApiKey => InnerService.RequiresApiKey;
    public bool IsConfigured => _isEnabled && InnerService.IsConfigured;
    public IReadOnlyList<Language> SupportedLanguages => InnerService.SupportedLanguages;
    public string? CacheKeyDiscriminator { get; }

    protected void EnsureEnabled()
    {
        if (_isEnabled)
        {
            return;
        }

        throw new TranslationException($"AI profile '{DisplayName}' is disabled.")
        {
            ErrorCode = TranslationErrorCode.ServiceDisabled,
            ServiceId = ServiceId
        };
    }

    public bool SupportsLanguagePair(Language from, Language to)
        => InnerService.SupportsLanguagePair(from, to);

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        try
        {
            return (await InnerService.TranslateAsync(request, cancellationToken).ConfigureAwait(false)) with
            {
                ServiceName = DisplayName
            };
        }
        catch (TranslationException ex)
        {
            throw Reidentify(ex);
        }
    }

    public async Task<Language> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        try
        {
            return await InnerService.DetectLanguageAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (TranslationException ex)
        {
            throw Reidentify(ex);
        }
    }

    protected TranslationException Reidentify(TranslationException exception)
        => new(exception.Message, exception)
        {
            ErrorCode = exception.ErrorCode,
            ServiceId = ServiceId,
            RecoveryAction = exception.RecoveryAction,
            DocumentationUrl = exception.DocumentationUrl
        };
}

/// <summary>Profile wrapper for a streaming provider without grammar correction.</summary>
public class ProfiledStreamingTranslationService : ProfiledTranslationService, IStreamTranslationService
{
    private readonly IStreamTranslationService _streamService;

    public ProfiledStreamingTranslationService(
        IStreamTranslationService innerService,
        string serviceId,
        string displayName,
        string profileId,
        int configurationRevision,
        bool isEnabled = true)
        : base(innerService, serviceId, displayName, profileId, configurationRevision, isEnabled)
    {
        _streamService = innerService;
    }

    public bool IsStreaming => _streamService.IsStreaming;

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var enumerator = _streamService.TranslateStreamAsync(request, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (TranslationException ex)
                {
                    throw Reidentify(ex);
                }

                if (!moved)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Profile wrapper for streaming providers that also support grammar correction.</summary>
public class ProfiledStreamingGrammarTranslationService : ProfiledStreamingTranslationService, IGrammarCorrectionService
{
    private readonly IGrammarCorrectionService _grammarService;

    public ProfiledStreamingGrammarTranslationService(
        IStreamTranslationService streamService,
        IGrammarCorrectionService grammarService,
        string serviceId,
        string displayName,
        string profileId,
        int configurationRevision,
        bool isEnabled = true)
        : base(streamService, serviceId, displayName, profileId, configurationRevision, isEnabled)
    {
        _grammarService = grammarService;
    }

    public async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var enumerator = _grammarService.CorrectGrammarStreamAsync(request, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (TranslationException ex)
                {
                    throw Reidentify(ex);
                }

                if (!moved)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Profile wrapper preserving provider model-catalog access.</summary>
public sealed class ProfiledModelCatalogTranslationService : ProfiledStreamingGrammarTranslationService, IModelCatalogProvider
{
    private readonly IModelCatalogProvider _catalogProvider;

    public ProfiledModelCatalogTranslationService(
        IStreamTranslationService streamService,
        IGrammarCorrectionService grammarService,
        IModelCatalogProvider catalogProvider,
        string serviceId,
        string displayName,
        string profileId,
        int configurationRevision,
        bool isEnabled = true)
        : base(streamService, grammarService, serviceId, displayName, profileId, configurationRevision, isEnabled)
    {
        _catalogProvider = catalogProvider;
    }

    public string ModelsEndpoint => _catalogProvider.ModelsEndpoint;

    public Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        return _catalogProvider.FetchModelsAsync(cancellationToken);
    }
}
