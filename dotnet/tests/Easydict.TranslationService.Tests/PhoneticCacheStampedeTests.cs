using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// Tests that concurrent phonetic enrichment requests for the same English word
/// are deduplicated so only one Youdao API call is made (cache stampede prevention).
/// </summary>
public class PhoneticCacheStampedeTests : IDisposable
{
    private readonly TranslationManager _manager;
    private readonly TrackingYoudaoService _trackingYoudao;

    public PhoneticCacheStampedeTests()
    {
        _manager = new TranslationManager();
        _trackingYoudao = new TrackingYoudaoService();
        // Replace the default Youdao service with our tracking mock
        _manager.RegisterService(_trackingYoudao);
    }

    [Fact]
    public async Task EnrichPhonetics_ConcurrentCallsSameWord_OnlyOneYoudaoRequest()
    {
        // Arrange: three translation results for "hello" from different services,
        // all targeting English, all lacking phonetics.
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result1 = MakeResultWithoutPhonetics("hello");
        var result2 = MakeResultWithoutPhonetics("hello");
        var result3 = MakeResultWithoutPhonetics("hello");

        // Act: call EnrichPhoneticsIfMissingAsync concurrently for the same word
        var tasks = new[]
        {
            _manager.EnrichPhoneticsIfMissingAsync(result1, request),
            _manager.EnrichPhoneticsIfMissingAsync(result2, request),
            _manager.EnrichPhoneticsIfMissingAsync(result3, request),
        };

        var results = await Task.WhenAll(tasks);

        // Assert: Youdao was called exactly once despite three concurrent requests
        _trackingYoudao.CallCount.Should().Be(1,
            "concurrent phonetic requests for the same word should be deduplicated");

        // All three results should have phonetics merged in
        foreach (var enriched in results)
        {
            enriched.WordResult.Should().NotBeNull();
            enriched.WordResult!.Phonetics.Should().NotBeNull();
            enriched.WordResult.Phonetics!.Count.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task EnrichPhonetics_DifferentWords_SeparateYoudaoRequests()
    {
        // Arrange: two different English words
        var request = new TranslationRequest
        {
            Text = "test",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result1 = MakeResultWithoutPhonetics("hello");
        var result2 = MakeResultWithoutPhonetics("world");

        var requestForHello = request;
        var requestForWorld = request;

        // Act: call for two different words concurrently
        var tasks = new[]
        {
            _manager.EnrichPhoneticsIfMissingAsync(result1, requestForHello),
            _manager.EnrichPhoneticsIfMissingAsync(result2, requestForWorld),
        };

        await Task.WhenAll(tasks);

        // Assert: Youdao was called twice (once per distinct word)
        _trackingYoudao.CallCount.Should().Be(2,
            "different words should each trigger a separate Youdao request");
    }

    [Fact]
    public async Task EnrichPhonetics_SecondCallAfterFirstCompletes_UsesCache()
    {
        // Arrange
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = MakeResultWithoutPhonetics("hello");

        // Act: first call populates the cache
        await _manager.EnrichPhoneticsIfMissingAsync(result, request);
        _trackingYoudao.CallCount.Should().Be(1);

        // Second call should hit the cache, not Youdao
        await _manager.EnrichPhoneticsIfMissingAsync(result, request);
        _trackingYoudao.CallCount.Should().Be(1,
            "second call should use cached phonetics, not call Youdao again");
    }

    [Fact]
    public async Task EnrichPhonetics_NonEnglishTarget_SkipsEnrichment()
    {
        // Arrange: target is Chinese, not English
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = MakeResultWithoutPhonetics("你好");

        // Act
        var enriched = await _manager.EnrichPhoneticsIfMissingAsync(result, request);

        // Assert: no Youdao call for non-English targets
        _trackingYoudao.CallCount.Should().Be(0);
        enriched.Should().BeSameAs(result);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    private static TranslationResult MakeResultWithoutPhonetics(string translatedText)
    {
        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = "test",
            ServiceName = "test-service",
            TargetLanguage = Language.English
        };
    }

    /// <summary>
    /// A mock Youdao service that tracks how many times TranslateAsync is called
    /// and introduces a small delay to simulate network latency (ensuring concurrent
    /// callers overlap).
    /// </summary>
    private class TrackingYoudaoService : ITranslationService
    {
        private int _callCount;

        public int CallCount => _callCount;

        public string ServiceId => "youdao";
        public string DisplayName => "Youdao (Tracking Mock)";
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;

        public IReadOnlyList<Language> SupportedLanguages =>
            [Language.English, Language.SimplifiedChinese];

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public async Task<TranslationResult> TranslateAsync(
            TranslationRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);

            // Simulate network delay so concurrent callers overlap
            await Task.Delay(50, cancellationToken);

            return new TranslationResult
            {
                TranslatedText = request.Text,
                OriginalText = request.Text,
                ServiceName = "Youdao",
                TargetLanguage = request.ToLanguage,
                WordResult = new WordResult
                {
                    Phonetics =
                    [
                        new Phonetic { Text = "həˈloʊ", Accent = "US" },
                        new Phonetic { Text = "həˈləʊ", Accent = "UK" }
                    ]
                }
            };
        }

        public Task<Language> DetectLanguageAsync(
            string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Language.Auto);
        }
    }
}
