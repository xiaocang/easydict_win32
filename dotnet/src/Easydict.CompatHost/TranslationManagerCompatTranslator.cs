using System.Diagnostics;
using System.Text;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;

namespace Easydict.CompatHost;

public sealed class TranslationManagerCompatTranslator : ICompatHostTranslator, IAsyncDisposable
{
    private readonly TranslationManager _manager;

    public TranslationManagerCompatTranslator()
        : this(new TranslationManager())
    {
    }

    public TranslationManagerCompatTranslator(TranslationManager manager)
    {
        _manager = manager;
    }

    public async Task<TranslationResultDto> TranslateAsync(
        TranslateParams parameters,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parameters.Text))
        {
            throw new CompatHostException(IpcErrorCodes.InvalidParams, "Text cannot be empty");
        }

        var serviceId = ChooseService(parameters);
        var request = new TranslationRequest
        {
            Text = parameters.Text,
            FromLanguage = ParseLanguage(parameters.From, Language.Auto),
            ToLanguage = ParseLanguage(parameters.To, Language.SimplifiedChinese),
        };

        var result = await _manager.TranslateAsync(request, cancellationToken, serviceId)
            .ConfigureAwait(false);
        var effectiveServiceId = serviceId ?? _manager.DefaultServiceId;

        return new TranslationResultDto
        {
            TranslatedText = result.TranslatedText,
            ServiceId = effectiveServiceId,
            ServiceName = result.ServiceName,
            DetectedLanguage = result.DetectedLanguage == Language.Auto
                ? null
                : result.DetectedLanguage.ToIso639(),
            ResultKind = result.ResultKind.ToString(),
            InfoMessage = result.InfoMessage,
            TimingMs = result.TimingMs,
            Alternatives = result.Alternatives,
        };
    }

    public async Task<TranslationResultDto> TranslateStreamAsync(
        TranslateParams parameters,
        Func<string, CancellationToken, Task> onChunkAsync,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parameters.Text))
        {
            throw new CompatHostException(IpcErrorCodes.InvalidParams, "Text cannot be empty");
        }

        var serviceId = ChooseService(parameters);
        var effectiveServiceId = serviceId ?? _manager.DefaultServiceId;
        var request = new TranslationRequest
        {
            Text = parameters.Text,
            FromLanguage = ParseLanguage(parameters.From, Language.Auto),
            ToLanguage = ParseLanguage(parameters.To, Language.SimplifiedChinese),
            BypassCache = true,
        };

        var stopwatch = Stopwatch.StartNew();
        var translated = new StringBuilder();

        await foreach (var chunk in _manager.TranslateStreamAsync(request, cancellationToken, serviceId)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            translated.Append(chunk);
            await onChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();

        return new TranslationResultDto
        {
            TranslatedText = translated.ToString(),
            ServiceId = effectiveServiceId,
            ServiceName = _manager.Services.TryGetValue(effectiveServiceId, out var service)
                ? service.DisplayName
                : null,
            DetectedLanguage = null,
            ResultKind = TranslationResultKind.Success.ToString(),
            TimingMs = stopwatch.ElapsedMilliseconds,
        };
    }

    public async Task<GrammarCorrectResultDto> CorrectGrammarAsync(
        GrammarCorrectParams parameters,
        Func<string, CancellationToken, Task> onChunkAsync,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parameters.Text))
        {
            throw new CompatHostException(IpcErrorCodes.InvalidParams, "Text cannot be empty");
        }

        var language = ParseLanguage(parameters.Language, Language.Auto);
        var serviceId = ChooseGrammarService(parameters, language);
        var service = _manager.Services[serviceId];
        var grammarService = (IGrammarCorrectionService)service;
        var request = new GrammarCorrectionRequest
        {
            Text = parameters.Text,
            Language = language,
            IncludeExplanations = parameters.IncludeExplanations,
        };

        var stopwatch = Stopwatch.StartNew();
        var rawOutput = new StringBuilder();

        await foreach (var chunk in grammarService.CorrectGrammarStreamAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rawOutput.Append(chunk);
            await onChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();

        var rawText = rawOutput.ToString();
        var parsed = GrammarCorrectionParser.Parse(
            rawText,
            parameters.Text,
            service.DisplayName,
            stopwatch.ElapsedMilliseconds);

        return new GrammarCorrectResultDto
        {
            OriginalText = parsed.OriginalText,
            CorrectedText = parsed.CorrectedText,
            Explanation = parsed.Explanation,
            RawText = rawText,
            ServiceId = service.ServiceId,
            ServiceName = parsed.ServiceName,
            Language = language.ToIso639(),
            TimingMs = parsed.TimingMs,
            HasCorrections = parsed.HasCorrections,
        };
    }

    public ValueTask DisposeAsync()
    {
        _manager.Dispose();
        return ValueTask.CompletedTask;
    }

    private string? ChooseService(TranslateParams parameters)
    {
        if (parameters.Services is null || parameters.Services.Length == 0)
        {
            return null;
        }

        return parameters.Services.FirstOrDefault(serviceId =>
            !string.IsNullOrWhiteSpace(serviceId) &&
            _manager.Services.ContainsKey(serviceId));
    }

    private string ChooseGrammarService(GrammarCorrectParams parameters, Language language)
    {
        if (parameters.Services is { Length: > 0 })
        {
            var serviceId = parameters.Services.FirstOrDefault(id =>
                !string.IsNullOrWhiteSpace(id) &&
                _manager.Services.TryGetValue(id, out var service) &&
                IsGrammarServiceAvailable(service, language));

            if (serviceId is not null)
            {
                return serviceId;
            }

            throw new CompatHostException(
                IpcErrorCodes.ServiceError,
                "No requested service supports grammar correction");
        }

        if (_manager.Services.TryGetValue(_manager.DefaultServiceId, out var defaultService)
            && IsGrammarServiceAvailable(defaultService, language))
        {
            return _manager.DefaultServiceId;
        }

        var firstGrammarService = _manager.Services.Values.FirstOrDefault(service =>
            IsGrammarServiceAvailable(service, language));

        if (firstGrammarService is not null)
        {
            return firstGrammarService.ServiceId;
        }

        throw new CompatHostException(
            IpcErrorCodes.ServiceError,
            "No service supports grammar correction for this language");
    }

    private static bool IsGrammarServiceAvailable(ITranslationService service, Language sourceLanguage)
    {
        if (service is not IGrammarCorrectionService)
        {
            return false;
        }

        var from = sourceLanguage == Language.Auto ? Language.Auto : sourceLanguage;
        var to = sourceLanguage == Language.Auto ? Language.English : sourceLanguage;
        return service.SupportsLanguagePair(from, to);
    }

    private static Language ParseLanguage(string? languageCode, Language defaultLanguage)
    {
        return string.IsNullOrWhiteSpace(languageCode)
            ? defaultLanguage
            : LanguageCodes.FromIso639(languageCode);
    }
}
