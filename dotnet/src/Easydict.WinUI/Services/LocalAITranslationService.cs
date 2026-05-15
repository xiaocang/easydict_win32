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
public sealed class LocalAITranslationService : IStreamTranslationService, ILocalModelProvider
{
    internal const string ServiceIdValue = PhiSilicaTranslationService.ServiceIdValue;
    internal const string LegacyOpenVinoServiceId = OpenVINOTranslationService.ServiceIdValue;
    internal const string DisplayNameValue = "Windows Local AI";

    private readonly PhiSilicaTranslationService _phiSilica;
    private readonly IStreamTranslationService _foundryLocal;
    private readonly ILocalModelProvider? _foundryLocalModelProvider;
    private readonly OpenVINOTranslationService _openVino;
    private LocalAIProviderMode _providerMode = LocalAIProviderMode.Auto;

    public LocalAITranslationService(
        PhiSilicaTranslationService phiSilica,
        IStreamTranslationService foundryLocal,
        OpenVINOTranslationService openVino)
    {
        _phiSilica = phiSilica ?? throw new ArgumentNullException(nameof(phiSilica));
        _foundryLocal = foundryLocal ?? throw new ArgumentNullException(nameof(foundryLocal));
        _foundryLocalModelProvider = foundryLocal as ILocalModelProvider;
        _openVino = openVino ?? throw new ArgumentNullException(nameof(openVino));

        _phiSilica.StatusChanged += (_, status) => RaiseStatusChanged(status);
        if (_foundryLocalModelProvider is not null)
        {
            _foundryLocalModelProvider.StatusChanged += (_, status) => RaiseStatusChanged(status);
        }
        _openVino.StatusChanged += (_, status) => RaiseStatusChanged(status);
    }

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
            LocalAIProviderMode.WindowsAI => _phiSilica.SupportsLanguagePair(from, to),
            LocalAIProviderMode.FoundryLocal => _foundryLocal.SupportsLanguagePair(from, to),
            LocalAIProviderMode.OpenVINO => _openVino.SupportsLanguagePair(from, to),
            _ => _phiSilica.SupportsLanguagePair(from, to)
                || _foundryLocal.SupportsLanguagePair(from, to)
                || _openVino.SupportsLanguagePair(from, to),
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

    public LocalModelStatus GetStatus()
    {
        return _providerMode switch
        {
            LocalAIProviderMode.WindowsAI => _phiSilica.GetStatus(),
            LocalAIProviderMode.FoundryLocal => GetFoundryLocalStatus(),
            LocalAIProviderMode.OpenVINO => _openVino.GetStatus(),
            _ => GetAutoStatus(),
        };
    }

    public Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
    {
        return _providerMode switch
        {
            LocalAIProviderMode.FoundryLocal when _foundryLocalModelProvider is not null =>
                _foundryLocalModelProvider.PrepareAsync(cancellationToken),
            LocalAIProviderMode.OpenVINO => _openVino.PrepareAsync(cancellationToken),
            _ => _phiSilica.PrepareAsync(cancellationToken),
        };
    }

    private IEnumerable<IStreamTranslationService> GetCandidateServices(Language from, Language to)
    {
        if (_providerMode != LocalAIProviderMode.Auto)
        {
            var explicitService = _providerMode switch
            {
                LocalAIProviderMode.WindowsAI => _phiSilica,
                LocalAIProviderMode.FoundryLocal => _foundryLocal,
                LocalAIProviderMode.OpenVINO => _openVino,
                _ => _openVino,
            };

            if (explicitService.SupportsLanguagePair(from, to))
            {
                yield return explicitService;
            }
            yield break;
        }

        if (ShouldTryPhiSilica(from, to))
        {
            yield return _phiSilica;
        }

        if (ShouldTryFoundryLocal(from, to))
        {
            yield return _foundryLocal;
        }

        if (_openVino.SupportsLanguagePair(from, to))
        {
            yield return _openVino;
        }
    }

    private bool ShouldTryPhiSilica(Language from, Language to)
    {
        if (!_phiSilica.SupportsLanguagePair(from, to))
        {
            return false;
        }

        var phiSilicaStatus = _phiSilica.GetStatus();
        if (phiSilicaStatus.State is LocalModelState.Ready
            or LocalModelState.NeedsPreparation
            or LocalModelState.Preparing)
        {
            return true;
        }

        return !ShouldTryFoundryLocal(from, to) && !_openVino.SupportsLanguagePair(from, to);
    }

    private bool ShouldTryFoundryLocal(Language from, Language to)
    {
        if (!_foundryLocal.IsConfigured || !_foundryLocal.SupportsLanguagePair(from, to))
        {
            return false;
        }

        var status = GetFoundryLocalStatus();
        return status.State is LocalModelState.Ready
            or LocalModelState.NeedsPreparation
            or LocalModelState.Preparing;
    }

    private bool CanFallbackFrom(
        IReadOnlyList<IStreamTranslationService> candidates,
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
        var phiSilicaStatus = _phiSilica.GetStatus();
        if (phiSilicaStatus.State == LocalModelState.Ready)
        {
            return phiSilicaStatus;
        }

        var foundryLocalStatus = GetFoundryLocalStatus();
        if (foundryLocalStatus.State == LocalModelState.Ready)
        {
            return foundryLocalStatus;
        }

        var openVinoStatus = _openVino.GetStatus();
        return openVinoStatus.State == LocalModelState.Ready
            ? openVinoStatus
            : phiSilicaStatus;
    }

    private LocalModelStatus GetFoundryLocalStatus()
    {
        return _foundryLocalModelProvider?.GetStatus()
            ?? new LocalModelStatus(LocalModelState.Ready, "FoundryLocal_Status_Ready");
    }

    private void RaiseStatusChanged(LocalModelStatus status)
    {
        StatusChanged?.Invoke(this, status);
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
}


