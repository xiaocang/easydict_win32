using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Adapter;

/// <summary>
/// The host, not the plugin, decides how much automatic behavior a plugin receives. These tests
/// register a real plugin with a real <see cref="TranslationManager"/> and check the boundary.
/// </summary>
public class BobPluginThroughTranslationManagerTests
{
    [Fact]
    public async Task AConservativePluginIsNeverServedFromCache()
    {
        using var fixture = PluginFixture.Create("echo-global");
        using var manager = new TranslationManager();
        var service = fixture.Service();
        manager.RegisterService(service);

        var first = await manager.TranslateAsync(PluginFixture.Request("same"), serviceId: service.ServiceId);
        var second = await manager.TranslateAsync(PluginFixture.Request("same"), serviceId: service.ServiceId);

        first.TranslatedText.Should().Be("ECHO(1): same");
        second.TranslatedText.Should().Be("ECHO(2): same", "the plugin must run again rather than replay a cached result");
        second.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task OptingIntoCachingMakesTheSecondQueryAReplay()
    {
        using var fixture = PluginFixture.Create("echo-global", policy: ServiceExecutionPolicy.Default);
        using var manager = new TranslationManager();
        var service = fixture.Service();
        manager.RegisterService(service);

        var first = await manager.TranslateAsync(PluginFixture.Request("same"), serviceId: service.ServiceId);
        var second = await manager.TranslateAsync(PluginFixture.Request("same"), serviceId: service.ServiceId);

        first.TranslatedText.Should().Be("ECHO(1): same");
        second.TranslatedText.Should().Be("ECHO(1): same");
        second.FromCache.Should().BeTrue();
    }

    [Fact]
    public void TheManagerReadsThePluginsPolicy()
    {
        using var fixture = PluginFixture.Create("dict");
        using var manager = new TranslationManager();
        var service = fixture.Service();
        manager.RegisterService(service);

        manager.GetExecutionPolicy(service.ServiceId).Should().Be(ServiceExecutionPolicy.Conservative);

        // A service that says nothing keeps the host's default behavior.
        manager.GetExecutionPolicy("google").Should().Be(ServiceExecutionPolicy.Default);
    }

    [Fact]
    public async Task StructuredResultsSurviveTheManagersStreamingPath()
    {
        using var fixture = PluginFixture.Create("stream");
        using var manager = new TranslationManager();
        var service = fixture.Service();
        manager.RegisterService(service);

        var updates = new List<TranslationStreamUpdate>();
        await foreach (var update in manager.TranslateStreamUpdatesAsync(
                           PluginFixture.Request("hello world"), serviceId: service.ServiceId))
        {
            updates.Add(update);
        }

        updates.OfType<TranslationStreamUpdate.TextSnapshot>().Should().HaveCount(3);
        var result = updates.OfType<TranslationStreamUpdate.Completed>().Should().ContainSingle().Which.Result;
        result.TranslatedText.Should().Be("Hello world");
        result.WordResult!.Definitions.Should().ContainSingle();
    }

    [Fact]
    public async Task ANoResultFromAPluginIsNotAFailure()
    {
        using var fixture = PluginFixture.Create("error-notfound");
        using var manager = new TranslationManager();
        var service = fixture.Service();
        manager.RegisterService(service);

        var result = await manager.TranslateAsync(PluginFixture.Request("qwertyuiop"), serviceId: service.ServiceId);

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
        result.InfoMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AConservativePluginIsNotRetried()
    {
        using var fixture = PluginFixture.Create("fails-once");
        using var manager = new TranslationManager();
        var service = fixture.Service();
        manager.RegisterService(service);

        var act = async () => await manager.TranslateAsync(PluginFixture.Request(), serviceId: service.ServiceId);

        // The fixture would succeed on a second attempt, so a retry would hide the failure.
        (await act.Should().ThrowAsync<TranslationException>())
            .Which.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
    }

    [Fact]
    public async Task OptingIntoRetriesLetsTheHostRecoverATransientFailure()
    {
        using var fixture = PluginFixture.Create("fails-once", policy: ServiceExecutionPolicy.Default);
        using var manager = new TranslationManager();
        var service = fixture.Service();
        manager.RegisterService(service);

        var result = await manager.TranslateAsync(PluginFixture.Request(), serviceId: service.ServiceId);

        result.TranslatedText.Should().Be("attempt 2");
    }

    [Fact]
    public void RegisteringAPluginCannotDisplaceABuiltInService()
    {
        using var fixture = PluginFixture.Create("echo-global");
        using var manager = new TranslationManager();
        var builtInCount = manager.Services.Count;

        manager.RegisterService(fixture.Service());

        manager.Services.Should().HaveCount(builtInCount + 1);
        manager.Services.Keys.Should().Contain("bob:test.echo.global:test");
        manager.Services.Should().ContainKey("google", "a plugin id can never collide with a built-in one");
    }
}
