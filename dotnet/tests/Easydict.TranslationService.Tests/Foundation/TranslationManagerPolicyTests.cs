using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

/// <summary>
/// Verifies that <see cref="TranslationManager"/> honors <see cref="ServiceExecutionPolicy"/>:
/// conservative services get one execution per user action with no cache replay and no retry.
/// </summary>
public class TranslationManagerPolicyTests : IDisposable
{
    private readonly TranslationManager _manager = new();

    public TranslationManagerPolicyTests()
    {
        // Requests below are en→zh word lookups, which now reach phonetic enrichment.
        // Stand in for the real Youdao service so these tests stay offline; the one test
        // that cares about enrichment registers its own "youdao" over this.
        _manager.RegisterService(new PolicyTestService("youdao"));
    }

    private static TranslationRequest Request(string text = "hello") => new()
    {
        Text = text,
        FromLanguage = Language.English,
        ToLanguage = Language.SimplifiedChinese
    };

    [Fact]
    public void GetExecutionPolicy_UnknownOrPlainService_ReturnsDefault()
    {
        _manager.GetExecutionPolicy("does-not-exist").Should().Be(ServiceExecutionPolicy.Default);
        _manager.GetExecutionPolicy("google").Should().Be(ServiceExecutionPolicy.Default);
    }

    [Fact]
    public void GetExecutionPolicy_PolicyProvider_ReturnsDeclaredPolicy()
    {
        var service = new PolicyTestService("policy-svc", ServiceExecutionPolicy.Conservative);
        _manager.RegisterService(service);

        _manager.GetExecutionPolicy("policy-svc").Should().Be(ServiceExecutionPolicy.Conservative);
    }

    [Fact]
    public async Task TranslateAsync_ConservativePolicy_NeverServesFromCache()
    {
        var service = new PolicyTestService("no-cache", ServiceExecutionPolicy.Conservative);
        _manager.RegisterService(service);

        var first = await _manager.TranslateAsync(Request(), serviceId: "no-cache");
        var second = await _manager.TranslateAsync(Request(), serviceId: "no-cache");

        service.CallCount.Should().Be(2, "a conservative service must execute once per request");
        first.FromCache.Should().BeFalse();
        second.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task TranslateAsync_DefaultPolicy_ServesSecondCallFromCache()
    {
        var service = new PolicyTestService("cached");
        _manager.RegisterService(service);

        await _manager.TranslateAsync(Request(), serviceId: "cached");
        var second = await _manager.TranslateAsync(Request(), serviceId: "cached");

        service.CallCount.Should().Be(1);
        second.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task TranslateAsync_ConservativePolicy_DoesNotRetryTransientFailure()
    {
        var service = new PolicyTestService("no-retry", ServiceExecutionPolicy.Conservative);
        service.FailNext(1);
        _manager.RegisterService(service);

        var act = () => _manager.TranslateAsync(Request(), serviceId: "no-retry");

        await act.Should().ThrowAsync<TranslationException>();
        service.CallCount.Should().Be(1, "host retry is disabled by the policy");
    }

    [Fact]
    public async Task TranslateAsync_DefaultPolicy_RetriesTransientFailure()
    {
        var service = new PolicyTestService("retry");
        service.FailNext(1);
        _manager.RegisterService(service);

        var result = await _manager.TranslateAsync(Request(), serviceId: "retry");

        result.TranslatedText.Should().Be("Translated: hello");
        service.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(TranslationErrorCode.InvalidApiKey)]
    [InlineData(TranslationErrorCode.UnsupportedLanguage)]
    [InlineData(TranslationErrorCode.TextTooLong)]
    [InlineData(TranslationErrorCode.RateLimited)]
    public async Task TranslateAsync_NonTransientError_IsNotRetriedEvenWithDefaultPolicy(TranslationErrorCode code)
    {
        var service = new PolicyTestService("fatal-" + code) { FailureCode = code };
        service.FailNext(3);
        _manager.RegisterService(service);

        var act = () => _manager.TranslateAsync(Request(), serviceId: service.ServiceId);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(code);
        service.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task EnrichPhoneticsIfMissingAsync_ConservativePolicy_SkipsEnrichment()
    {
        // A fake "youdao" provider that would add US phonetics if consulted.
        var youdao = new PolicyTestService("youdao")
        {
            ResultFactory = r => new TranslationResult
            {
                TranslatedText = "你好",
                OriginalText = r.Text,
                ServiceName = "Youdao",
                WordResult = new WordResult
                {
                    Phonetics = new[] { new Phonetic { Text = "həˈloʊ", Accent = "US" } }
                }
            }
        };
        _manager.RegisterService(youdao);
        _manager.RegisterService(new PolicyTestService("plugin", ServiceExecutionPolicy.Conservative));
        _manager.RegisterService(new PolicyTestService("native"));

        var request = new TranslationRequest { Text = "你好", FromLanguage = Language.SimplifiedChinese, ToLanguage = Language.English };
        var bare = new TranslationResult { TranslatedText = "hello", OriginalText = "你好", ServiceName = "x", TargetLanguage = Language.English };

        var forPlugin = await _manager.EnrichPhoneticsIfMissingAsync(bare, request, serviceId: "plugin");
        var forNative = await _manager.EnrichPhoneticsIfMissingAsync(bare, request, serviceId: "native");

        forPlugin.WordResult.Should().BeNull("the plugin's policy forbids host-initiated enrichment");
        forNative.WordResult!.Phonetics.Should().ContainSingle(p => p.Accent == "US");
        youdao.CallCount.Should().Be(1);
    }

    public void Dispose() => _manager.Dispose();
}
