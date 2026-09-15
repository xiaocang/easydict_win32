using System.Runtime.CompilerServices;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Tests.Foundation;

/// <summary>
/// Configurable fake service used by the foundation tests: declares an execution policy,
/// an optional cache-key discriminator, counts calls and can fail a configurable number of times.
/// </summary>
internal sealed class PolicyTestService : ITranslationService, IServiceExecutionPolicyProvider, ICacheKeyDiscriminatorProvider
{
    private int _failuresRemaining;

    public PolicyTestService(string serviceId, ServiceExecutionPolicy? policy = null)
    {
        ServiceId = serviceId;
        DisplayName = $"Test {serviceId}";
        ExecutionPolicy = policy ?? ServiceExecutionPolicy.Default;
    }

    public string ServiceId { get; }
    public string DisplayName { get; }
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;
    public IReadOnlyList<Language> SupportedLanguages => new[] { Language.English, Language.SimplifiedChinese };
    public ServiceExecutionPolicy ExecutionPolicy { get; set; }
    public string? CacheKeyDiscriminator { get; set; }
    public int CallCount { get; private set; }
    public TranslationErrorCode FailureCode { get; set; } = TranslationErrorCode.Unknown;
    public Func<TranslationRequest, TranslationResult>? ResultFactory { get; set; }

    public void FailNext(int times) => _failuresRemaining = times;

    public bool SupportsLanguagePair(Language from, Language to) => true;

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (_failuresRemaining > 0)
        {
            _failuresRemaining--;
            throw new TranslationException($"Simulated failure ({FailureCode})")
            {
                ErrorCode = FailureCode,
                ServiceId = ServiceId
            };
        }

        var result = ResultFactory?.Invoke(request) ?? new TranslationResult
        {
            TranslatedText = $"Translated: {request.Text}",
            OriginalText = request.Text,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName
        };
        return Task.FromResult(result);
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);
}

/// <summary>Legacy delta-only streaming fake.</summary>
internal sealed class LegacyStreamTestService : ITranslationService, IStreamTranslationService
{
    public LegacyStreamTestService(string serviceId, params string[] chunks)
    {
        ServiceId = serviceId;
        Chunks = chunks;
    }

    public string ServiceId { get; }
    public string DisplayName => $"Legacy {ServiceId}";
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;
    public IReadOnlyList<Language> SupportedLanguages => new[] { Language.English };
    public bool IsStreaming => true;
    public string[] Chunks { get; }

    public bool SupportsLanguagePair(Language from, Language to) => true;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var text = string.Concat(Chunks);
        await Task.Yield();
        return new TranslationResult { TranslatedText = text, OriginalText = request.Text, ServiceName = DisplayName };
    }

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in Chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);
}

/// <summary>Rich streaming fake that replays a scripted update sequence.</summary>
internal sealed class RichStreamTestService : ITranslationService, IRichStreamTranslationService, IServiceExecutionPolicyProvider
{
    public RichStreamTestService(string serviceId, IReadOnlyList<TranslationStreamUpdate> updates, ServiceExecutionPolicy? policy = null)
    {
        ServiceId = serviceId;
        Updates = updates;
        ExecutionPolicy = policy ?? ServiceExecutionPolicy.Conservative;
    }

    public string ServiceId { get; }
    public string DisplayName => $"Rich {ServiceId}";
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;
    public IReadOnlyList<Language> SupportedLanguages => new[] { Language.English };
    public bool IsStreaming => true;
    public IReadOnlyList<TranslationStreamUpdate> Updates { get; }
    public ServiceExecutionPolicy ExecutionPolicy { get; set; }
    public int StreamCallCount { get; private set; }

    public bool SupportsLanguagePair(Language from, Language to) => true;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        TranslationResult? last = null;
        await foreach (var update in TranslateStreamUpdatesAsync(request, cancellationToken))
        {
            if (update is TranslationStreamUpdate.Completed c) last = c.Result;
        }

        return last ?? throw new TranslationException("no completed update");
    }

    public IAsyncEnumerable<string> TranslateStreamAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        => Easydict.TranslationService.Streaming.StreamUpdateAdapter.ToDeltas(TranslateStreamUpdatesAsync(request, cancellationToken), cancellationToken);

    public async IAsyncEnumerable<TranslationStreamUpdate> TranslateStreamUpdatesAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamCallCount++;
        foreach (var update in Updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);
}
