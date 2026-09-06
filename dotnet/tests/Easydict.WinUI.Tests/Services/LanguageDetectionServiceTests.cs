using System.Net;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for LanguageDetectionService.
/// Verifies language detection and intelligent target language selection.
/// </summary>
[Trait("Category", "WinUI")]
public class LanguageDetectionServiceTests : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LanguageDetectionService _service;

    public LanguageDetectionServiceTests()
    {
        _settings = SettingsService.Instance;
        _service = new LanguageDetectionService(_settings);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    [Fact]
    public void Constructor_WithValidSettings_CreatesInstance()
    {
        using var service = new LanguageDetectionService(_settings);

        service.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithNullSettings_ThrowsArgumentNullException()
    {
        var act = () => new LanguageDetectionService(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("settings");
    }

    [Fact]
    public async Task DetectAsync_WithEmptyText_ReturnsAuto()
    {
        var result = await _service.DetectAsync("");

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WithNullText_ReturnsAuto()
    {
        var result = await _service.DetectAsync(null!);

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WithWhitespaceText_ReturnsAuto()
    {
        var result = await _service.DetectAsync("   ");

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WithShortText_ReturnsAuto()
    {
        // Non-CJK text shorter than 4 characters should return Auto
        var result = await _service.DetectAsync("Hi");

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WithShortCjkText_ReturnsAuto()
    {
        // CJK text shorter than 2 characters should return Auto
        var result = await _service.DetectAsync("你");

        result.Should().Be(Language.Auto);
    }

    [Fact]
    public async Task DetectAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _service.DetectAsync("This text is long enough to trigger language detection.", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void GetTargetLanguage_WhenSourceMatchesFirst_ReturnsSecond()
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);
        var secondLang = LanguageExtensions.FromCode(_settings.SecondLanguage);

        var result = _service.GetTargetLanguage(firstLang);

        result.Should().Be(secondLang);
    }

    [Fact]
    public void GetTargetLanguage_WhenSourceMatchesSecond_ReturnsFirst()
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);
        var secondLang = LanguageExtensions.FromCode(_settings.SecondLanguage);

        var result = _service.GetTargetLanguage(secondLang);

        // When source matches second language, should return first language
        result.Should().Be(firstLang);
    }

    [Fact]
    public void GetTargetLanguage_WithAuto_ReturnsFirstLanguage()
    {
        var firstLang = LanguageExtensions.FromCode(_settings.FirstLanguage);

        var result = _service.GetTargetLanguage(Language.Auto);

        // Auto doesn't match first language, so default target is first language
        result.Should().Be(firstLang);
    }

    [Fact]
    public void GetTargetLanguage_PreventsSameSourceAndTarget()
    {
        // Test the fallback logic when source equals target
        // If source is English, target should be SimplifiedChinese (fallback)
        var result = _service.GetTargetLanguage(Language.English);

        // The result should never equal the source
        result.Should().NotBe(Language.English);
    }

    [Fact]
    public void ClearCache_DoesNotThrow()
    {
        var act = () => _service.ClearCache();

        act.Should().NotThrow();
    }

    [Fact]
    public void ClearCache_CanBeCalledMultipleTimes()
    {
        var act = () =>
        {
            _service.ClearCache();
            _service.ClearCache();
            _service.ClearCache();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        using var service = new LanguageDetectionService(_settings);

        var act = () =>
        {
            service.Dispose();
            service.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DetectAsync_AfterDispose_ReturnsAutoAndDoesNotThrow()
    {
        using var service = new LanguageDetectionService(_settings);

        service.Dispose();

        var act = async () => await service.DetectAsync("hello");

        await act.Should().NotThrowAsync();
        var result = await service.DetectAsync("hello");
        result.Should().Be(Language.Auto);
    }

    [Fact]
    public void ClearCache_AfterDispose_DoesNotThrow()
    {
        using var service = new LanguageDetectionService(_settings);

        service.Dispose();

        var act = () => service.ClearCache();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DetectAsync_WhenGoogleRateLimited_FallbackRestoresGrammarRoutingAndIsCached()
    {
        using var http = new HttpClient(new RateLimitedHandler());
        var fallbackCalls = 0;
        var warnings = 0;
        var providers = new Dictionary<string, ITranslationService>
        {
            ["google"] = new GoogleTranslateService(http),
            ["bing"] = new StubDetector(_ =>
            {
                warnings.Should().Be(1, "the UI must be notified before waiting for the fallback");
                fallbackCalls++;
                return Task.FromResult(Language.English);
            })
        };
        using var detector = new LanguageDetectionService(_settings,
            (text, ct, notify) => LanguageDetectionService.DetectWithFallbackAsync(text, providers, ct, notify));

        var detected = await detector.DetectAsync("She go to school yesterday.", onRateLimited: () => warnings++);
        detected.Should().Be(Language.English);
        var route = new TargetLanguageSelector(_settings).ResolveQueryLanguage(
            Language.Auto, Language.English, detected, grammarCorrectionAvailable: true);
        route.EffectiveMode.Should().Be(QueryMode.GrammarCorrection);
        (await detector.DetectAsync("She go to school yesterday.", onRateLimited: () => warnings++)).Should().Be(Language.English);
        fallbackCalls.Should().Be(1);
        warnings.Should().Be(1, "a cache hit must not report a new rate-limit failure");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetectWithFallback_WhenWrappedRateLimitAndFallbackFails_StillNotifiesUi(bool wrapped)
    {
        var error = new HttpRequestException("limited", null, HttpStatusCode.TooManyRequests);
        var providers = new Dictionary<string, ITranslationService>
        {
            ["google"] = new StubDetector(_ => Task.FromException<Language>(wrapped
                ? new TranslationException("network error", error) : error)),
            ["bing"] = new StubDetector(_ => Task.FromResult(Language.Auto))
        };
        var warned = false;

        var detected = await LanguageDetectionService.DetectWithFallbackAsync(
            "hello world", providers, CancellationToken.None, () => warned = true);

        detected.Should().Be(Language.Auto);
        warned.Should().BeTrue();
    }

    [Fact]
    public async Task DetectAsync_WhenDetectionUnknown_RetriesInsteadOfCachingAuto()
    {
        var calls = 0;
        using var detector = new LanguageDetectionService(_settings,
            (_, _) => Task.FromResult(++calls == 1 ? Language.Auto : Language.French));

        (await detector.DetectAsync("Bonjour le monde")).Should().Be(Language.Auto);
        (await detector.DetectAsync("Bonjour le monde")).Should().Be(Language.French);
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectWithFallback_WhenPrimaryUnknownOrTimesOut_TriesBing(bool timeout)
    {
        var providers = new Dictionary<string, ITranslationService>
        {
            ["google"] = new StubDetector(_ => timeout
                ? Task.FromException<Language>(new TaskCanceledException())
                : Task.FromResult(Language.Auto)),
            ["bing"] = new StubDetector(_ => Task.FromResult(Language.Japanese))
        };

        var detected = await LanguageDetectionService.DetectWithFallbackAsync(
            "こんにちは世界", providers, CancellationToken.None);

        detected.Should().Be(Language.Japanese);
    }

    [Fact]
    public async Task DetectWithFallback_WhenPrimarySucceeds_DoesNotCallBing()
    {
        var fallbackCalled = false;
        var providers = new Dictionary<string, ITranslationService>
        {
            ["google"] = new StubDetector(_ => Task.FromResult(Language.English)),
            ["bing"] = new StubDetector(_ =>
            {
                fallbackCalled = true;
                return Task.FromResult(Language.French);
            })
        };

        (await LanguageDetectionService.DetectWithFallbackAsync(
            "hello world", providers, CancellationToken.None)).Should().Be(Language.English);
        fallbackCalled.Should().BeFalse();
    }

    [Fact]
    public async Task DetectWithFallback_WhenUserCancels_DoesNotCallBing()
    {
        using var cts = new CancellationTokenSource();
        var fallbackCalled = false;
        var providers = new Dictionary<string, ITranslationService>
        {
            ["google"] = new StubDetector(ct =>
            {
                cts.Cancel();
                return Task.FromCanceled<Language>(ct);
            }),
            ["bing"] = new StubDetector(_ =>
            {
                fallbackCalled = true;
                return Task.FromResult(Language.English);
            })
        };

        var act = () => LanguageDetectionService.DetectWithFallbackAsync("hello world", providers, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fallbackCalled.Should().BeFalse();
    }

    [Fact]
    public async Task DetectWithFallback_WhenBothProvidersFail_ReturnsAuto()
    {
        var providers = new Dictionary<string, ITranslationService>
        {
            ["google"] = new StubDetector(_ => Task.FromException<Language>(new HttpRequestException())),
            ["bing"] = new StubDetector(_ => Task.FromException<Language>(new HttpRequestException()))
        };

        (await LanguageDetectionService.DetectWithFallbackAsync(
            "hello world", providers, CancellationToken.None)).Should().Be(Language.Auto);
    }

    private sealed class RateLimitedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
    }

    private sealed class StubDetector(Func<CancellationToken, Task<Language>> detect) : BaseTranslationService(null!)
    {
        public override string ServiceId => "stub";
        public override string DisplayName => "stub";
        public override bool RequiresApiKey => false;
        public override bool IsConfigured => true;
        public override IReadOnlyList<Language> SupportedLanguages => [];
        public override Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
            => detect(cancellationToken);
        protected override Task<TranslationResult> TranslateInternalAsync(TranslationRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
