using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

public class TranslationManagerCacheKeyTests : IDisposable
{
    private readonly TranslationManager _manager = new();

    private static TranslationRequest Request(string? customPrompt = null) => new()
    {
        Text = "cache me",
        FromLanguage = Language.English,
        ToLanguage = Language.SimplifiedChinese,
        CustomPrompt = customPrompt
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
