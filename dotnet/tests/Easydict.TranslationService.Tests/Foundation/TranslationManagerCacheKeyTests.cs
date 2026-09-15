using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

public class TranslationManagerCacheKeyTests : IDisposable
{
    private readonly TranslationManager _manager = new();

    private static TranslationRequest Request(
        string? customPrompt = null,
        string? originalText = null,
        Language? detectedFromLanguage = null) => new()
    {
        Text = "cache me",
        FromLanguage = Language.English,
        ToLanguage = Language.SimplifiedChinese,
        CustomPrompt = customPrompt,
        OriginalText = originalText,
        DetectedFromLanguage = detectedFromLanguage
    };

    [Fact]
    public async Task DifferentCustomPrompt_ProducesSeparateCacheEntries()
    {
        var service = new PolicyTestService("prompted");
        _manager.RegisterService(service);

        await _manager.TranslateAsync(Request("formal"), serviceId: "prompted");
        await _manager.TranslateAsync(Request("casual"), serviceId: "prompted");
        var repeat = await _manager.TranslateAsync(Request("formal"), serviceId: "prompted");

        service.CallCount.Should().Be(2);
        repeat.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task DifferentOriginalText_ProducesSeparateCacheEntries()
    {
        // OriginalText and DetectedFromLanguage are passed straight through to a Bob plugin
        // (BobTranslationService.BuildQueryJson) and can change its output even when Text/From/To
        // are identical, so they must be part of the cache key too.
        var service = new PolicyTestService("original-text");
        _manager.RegisterService(service);

        await _manager.TranslateAsync(Request(originalText: "raw one"), serviceId: "original-text");
        await _manager.TranslateAsync(Request(originalText: "raw two"), serviceId: "original-text");
        var repeat = await _manager.TranslateAsync(Request(originalText: "raw one"), serviceId: "original-text");

        service.CallCount.Should().Be(2);
        repeat.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task DifferentDetectedFromLanguage_ProducesSeparateCacheEntries()
    {
        var service = new PolicyTestService("detected-from");
        _manager.RegisterService(service);

        await _manager.TranslateAsync(Request(detectedFromLanguage: Language.English), serviceId: "detected-from");
        await _manager.TranslateAsync(Request(detectedFromLanguage: Language.French), serviceId: "detected-from");
        var repeat = await _manager.TranslateAsync(Request(detectedFromLanguage: Language.English), serviceId: "detected-from");

        service.CallCount.Should().Be(2);
        repeat.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task DiscriminatorChange_InvalidatesCachedResult()
    {
        var service = new PolicyTestService("versioned") { CacheKeyDiscriminator = "v1|optsA" };
        _manager.RegisterService(service);

        await _manager.TranslateAsync(Request(), serviceId: "versioned");
        service.CacheKeyDiscriminator = "v1|optsB";
        var afterChange = await _manager.TranslateAsync(Request(), serviceId: "versioned");

        afterChange.FromCache.Should().BeFalse("changing the service configuration must not replay a stale result");
        service.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task BypassCache_AlwaysExecutes()
    {
        var service = new PolicyTestService("bypass");
        _manager.RegisterService(service);

        var request = new TranslationRequest { Text = "x", ToLanguage = Language.English, BypassCache = true };
        await _manager.TranslateAsync(request, serviceId: "bypass");
        await _manager.TranslateAsync(request, serviceId: "bypass");

        service.CallCount.Should().Be(2);
    }

    public void Dispose() => _manager.Dispose();
}
