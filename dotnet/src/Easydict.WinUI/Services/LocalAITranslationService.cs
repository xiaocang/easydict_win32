using System.Runtime.CompilerServices;
using Easydict.OpenVINO.Services;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.WindowsAI.Services;

namespace Easydict.WinUI.Services;

/// <summary>
/// Single local-AI service entry for Settings/result selection. The selected
/// provider decides whether requests go to Phi Silica directly, Foundry Local
/// directly, OpenVINO directly, or an Auto chain across all local providers.
/// </summary>
public sealed class LocalAITranslationService : IStreamTranslationService, IGrammarCorrectionService, ILocalModelProvider, IDisposable
{
    internal const string ServiceIdValue = PhiSilicaTranslationService.ServiceIdValue;
    internal const string LegacyOpenVinoServiceId = OpenVINOTranslationService.ServiceIdValue;
    internal const string DisplayNameValue = "Windows Local AI";

    private readonly Lazy<PhiSilicaTranslationService> _phiSilicaLazy;
    private readonly Lazy<IStreamTranslationService> _foundryLocalLazy;
    private readonly Lazy<OpenVINOTranslationService> _openVinoLazy;

    // 0 = not subscribed, 1 = subscribed. Interlocked guards concurrent first access.
    private int _phiSilicaSubscribed;
    private int _foundryLocalSubscribed;
    private int _openVinoSubscribed;

    private LocalAIProviderMode _providerMode = LocalAIProviderMode.Auto;
    private bool _disposed;

    public LocalAITranslationService(
        Lazy<PhiSilicaTranslationService> phiSilica,
        Lazy<IStreamTranslationService> foundryLocal,
        Lazy<OpenVINOTranslationService> openVino)
    {
        _phiSilicaLazy = phiSilica ?? throw new ArgumentNullException(nameof(phiSilica));
        _foundryLocalLazy = foundryLocal ?? throw new ArgumentNullException(nameof(foundryLocal));
        _openVinoLazy = openVino ?? throw new ArgumentNullException(nameof(openVino));
        // Inner provider StatusChanged subscriptions are deferred to first
        // materialization (see PhiSilica/FoundryLocal/OpenVino properties).
        // Constructing this wrapper at startup must not force the inner
        // providers to materialize — the user may never select local AI.
    }

    // Backward-compat ctor for tests that construct with concrete instances.
    public LocalAITranslationService(
        PhiSilicaTranslationService phiSilica,
        IStreamTranslationService foundryLocal,
        OpenVINOTranslationService openVino)
        : this(
            new Lazy<PhiSilicaTranslationService>(() => phiSilica ?? throw new ArgumentNullException(nameof(phiSilica))),
            new Lazy<IStreamTranslationService>(() => foundryLocal ?? throw new ArgumentNullException(nameof(foundryLocal))),
            new Lazy<OpenVINOTranslationService>(() => openVino ?? throw new ArgumentNullException(nameof(openVino))))
    {
    }

    private PhiSilicaTranslationService PhiSilica
    {
        get
        {
            var svc = _phiSilicaLazy.Value;
            if (Interlocked.CompareExchange(ref _phiSilicaSubscribed, 1, 0) == 0)
            {
                svc.StatusChanged += OnInnerStatusChanged;
            }
            return svc;
        }
    }

    private IStreamTranslationService FoundryLocal
    {
        get
        {
            var svc = _foundryLocalLazy.Value;
            if (Interlocked.CompareExchange(ref _foundryLocalSubscribed, 1, 0) == 0
                && svc is ILocalModelProvider provider)
            {
                provider.StatusChanged += OnInnerStatusChanged;
            }
            return svc;
        }
    }

    private ILocalModelProvider? FoundryLocalModelProvider => FoundryLocal as ILocalModelProvider;

    private OpenVINOTranslationService OpenVino
    {
        get
        {
            var svc = _openVinoLazy.Value;
            if (Interlocked.CompareExchange(ref _openVinoSubscribed, 1, 0) == 0)
            {
                svc.StatusChanged += OnInnerStatusChanged;
            }
            return svc;
        }
    }

    // Test hook — true if a sub-provider has been materialized via these accessors.
    internal bool IsPhiSilicaMaterialized => _phiSilicaLazy.IsValueCreated;
    internal bool IsFoundryLocalMaterialized => _foundryLocalLazy.IsValueCreated;
    internal bool IsOpenVinoMaterialized => _openVinoLazy.IsValueCreated;

    public string ServiceId => ServiceIdValue;

    public string DisplayName => DisplayNameValue;

    public bool RequiresApiKey => false;

    public bool IsConfigured => true;

    public bool IsStreaming => true;

    public IReadOnlyList<Language> SupportedLanguages { get; } =
        Enum.GetValues<Language>()
            .Where(language => language != Language.Auto)
            .ToArray();

    public event EventHandler<LocalModelStatus>? StatusChanged;

    public void Configure(LocalAIProviderMode providerMode)
    {
        _providerMode = providerMode;
    }

    public bool SupportsLanguagePair(Language from, Language to)
    {
        return _providerMode switch
        {
            LocalAIProviderMode.WindowsAI => PhiSilica.SupportsLanguagePair(from, to),
            LocalAIProviderMode.FoundryLocal => FoundryLocal.SupportsLanguagePair(from, to),
            LocalAIProviderMode.OpenVINO => OpenVino.SupportsLanguagePair(from, to),
            _ => PhiSilica.SupportsLanguagePair(from, to)
                || FoundryLocal.SupportsLanguagePair(from, to)
                || OpenVino.SupportsLanguagePair(from, to),
        };
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
    {
        return GetCandidateServices(Language.Auto, Language.English)
            .FirstOrDefault()
            ?.DetectLanguageAsync(text, cancellationToken)
            ?? Task.FromResult(Language.Auto);
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var candidates = GetCandidateServices(request.FromLanguage, request.ToLanguage).ToArray();
        if (candidates.Length == 0)
        {
            throw CreateNoProviderException();
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            try
            {
                return NormalizeResult(await candidates[i].TranslateAsync(request, cancellationToken));
            }
            catch (TranslationException ex) when (CanFallbackFrom(candidates, i, ex))
            {
                // Try the next local backend in Auto mode.
            }
        }

        throw CreateNoProviderException();
    }

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var candidates = GetCandidateServices(request.FromLanguage, request.ToLanguage).ToArray();
        if (candidates.Length == 0)
        {
            throw CreateNoProviderException();
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            var emittedAnyChunk = false;
            var shouldFallback = false;

            await using var enumerator = candidates[i]
                .TranslateStreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasNext;
                string current;

                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                    if (!hasNext)
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (TranslationException ex) when (!emittedAnyChunk && CanFallbackFrom(candidates, i, ex))
                {
                    shouldFallback = true;
                    break;
                }

                if (!string.IsNullOrEmpty(current))
                {
                    emittedAnyChunk = true;
                    yield return current;
                }
            }

            if (!shouldFallback)
            {
                yield break;
            }
        }
    }

    public async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var candidates = GetGrammarCorrectionCandidateServices(request.Language).ToArray();
        if (candidates.Length == 0)
        {
            throw CreateNoGrammarProviderException();
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            var emittedAnyChunk = false;
            var shouldFallback = false;

            await using var enumerator = candidates[i]
                .CorrectGrammarStreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasNext;
                string current;

                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                    if (!hasNext)
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (TranslationException ex) when (!emittedAnyChunk && CanFallbackFrom(candidates, i, ex))
                {
                    shouldFallback = true;
                    break;
                }

                if (!string.IsNullOrEmpty(current))
                {
                    emittedAnyChunk = true;
                    yield return current;
                }
            }

            if (!shouldFallback)
            {
                yield break;
            }
        }
    }

    public LocalModelStatus GetStatus()
    {
        return _providerMode switch
        {
            LocalAIProviderMode.WindowsAI => PhiSilica.GetStatus(),
            LocalAIProviderMode.FoundryLocal => GetFoundryLocalStatus(),
            LocalAIProviderMode.OpenVINO => OpenVino.GetStatus(),
            _ => GetAutoStatus(),
        };
    }

    public Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
    {
        return _providerMode switch
        {
            LocalAIProviderMode.FoundryLocal when FoundryLocalModelProvider is not null =>
                FoundryLocalModelProvider.PrepareAsync(cancellationToken),
            LocalAIProviderMode.OpenVINO => OpenVino.PrepareAsync(cancellationToken),
            _ => PhiSilica.PrepareAsync(cancellationToken),
        };
    }

    public bool SupportsGrammarCorrection(Language language)
    {
        return GetGrammarCorrectionCandidateServices(language).Any();
    }

    private IEnumerable<IStreamTranslationService> GetCandidateServices(Language from, Language to)
    {
        if (_providerMode != LocalAIProviderMode.Auto)
        {
            IStreamTranslationService explicitService = _providerMode switch
            {
                LocalAIProviderMode.WindowsAI => PhiSilica,
                LocalAIProviderMode.FoundryLocal => FoundryLocal,
                LocalAIProviderMode.OpenVINO => OpenVino,
                _ => OpenVino,
            };

            if (explicitService.SupportsLanguagePair(from, to))
            {
                yield return explicitService;
            }
            yield break;
        }

        if (ShouldTryPhiSilica(from, to))
        {
            yield return PhiSilica;
        }

        if (ShouldTryFoundryLocal(from, to))
        {
            yield return FoundryLocal;
        }

        if (OpenVino.SupportsLanguagePair(from, to))
        {
            yield return OpenVino;
        }
    }

    private IEnumerable<IGrammarCorrectionService> GetGrammarCorrectionCandidateServices(Language language)
    {
        var from = language == Language.Auto ? Language.Auto : language;
        var to = language == Language.Auto ? Language.English : language;
        foreach (var service in GetCandidateServices(from, to))
        {
            if (service is IGrammarCorrectionService grammarService)
            {
                yield return grammarService;
            }
        }
    }

    private bool ShouldTryPhiSilica(Language from, Language to)
    {
        if (!PhiSilica.SupportsLanguagePair(from, to))
        {
            return false;
        }

        var phiSilicaStatus = PhiSilica.GetStatus();
        if (phiSilicaStatus.State is LocalModelState.Ready
            or LocalModelState.NeedsPreparation
            or LocalModelState.Preparing)
        {
            return true;
        }

        return !ShouldTryFoundryLocal(from, to) && !OpenVino.SupportsLanguagePair(from, to);
    }

    private bool ShouldTryFoundryLocal(Language from, Language to)
    {
        if (!FoundryLocal.IsConfigured || !FoundryLocal.SupportsLanguagePair(from, to))
        {
            return false;
        }

        var status = GetFoundryLocalStatus();
        return status.State is LocalModelState.Ready
            or LocalModelState.NeedsPreparation
            or LocalModelState.Preparing;
    }

    private bool CanFallbackFrom<T>(
        IReadOnlyList<T> candidates,
        int serviceIndex,
        TranslationException ex)
    {
        return _providerMode == LocalAIProviderMode.Auto
            && serviceIndex < candidates.Count - 1
            && ShouldFallbackFromProvider(ex);
    }

    private static bool ShouldFallbackFromProvider(TranslationException ex)
    {
        return ex.ErrorCode is TranslationErrorCode.ServiceUnavailable
            or TranslationErrorCode.NetworkError
            or TranslationErrorCode.Timeout
            or TranslationErrorCode.InvalidModel
            or TranslationErrorCode.Unknown;
    }

    private LocalModelStatus GetAutoStatus()
    {
        var phiSilicaStatus = PhiSilica.GetStatus();
        if (phiSilicaStatus.State == LocalModelState.Ready)
        {
            return phiSilicaStatus;
        }

        var foundryLocalStatus = GetFoundryLocalStatus();
        if (foundryLocalStatus.State == LocalModelState.Ready)
        {
            return foundryLocalStatus;
        }

        var openVinoStatus = OpenVino.GetStatus();
        return openVinoStatus.State == LocalModelState.Ready
            ? openVinoStatus
            : phiSilicaStatus;
    }

    private LocalModelStatus GetFoundryLocalStatus()
    {
        return FoundryLocalModelProvider?.GetStatus()
            ?? new LocalModelStatus(LocalModelState.Ready, FoundryLocalResources.StatusKeys.Ready);
    }

    private void OnInnerStatusChanged(object? sender, LocalModelStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Only unsubscribe if we actually subscribed. The IsValueCreated check is
        // a belt-and-suspenders guard: _xxxSubscribed == 1 already implies the
        // Lazy was materialized via the property getter.
        if (_phiSilicaSubscribed == 1 && _phiSilicaLazy.IsValueCreated)
        {
            _phiSilicaLazy.Value.StatusChanged -= OnInnerStatusChanged;
        }
        if (_foundryLocalSubscribed == 1
            && _foundryLocalLazy.IsValueCreated
            && _foundryLocalLazy.Value is ILocalModelProvider provider)
        {
            provider.StatusChanged -= OnInnerStatusChanged;
        }
        if (_openVinoSubscribed == 1 && _openVinoLazy.IsValueCreated)
        {
            _openVinoLazy.Value.StatusChanged -= OnInnerStatusChanged;
        }
    }

    private TranslationResult NormalizeResult(TranslationResult result)
    {
        return result with { ServiceName = DisplayName };
    }

    private TranslationException CreateNoProviderException()
    {
        return new TranslationException("No local AI provider supports this language pair")
        {
            ErrorCode = TranslationErrorCode.UnsupportedLanguage,
            ServiceId = ServiceId,
        };
    }

    private TranslationException CreateNoGrammarProviderException()
    {
        return new TranslationException("No local AI provider supports grammar correction")
        {
            ErrorCode = TranslationErrorCode.UnsupportedLanguage,
            ServiceId = ServiceId,
        };
    }
}


