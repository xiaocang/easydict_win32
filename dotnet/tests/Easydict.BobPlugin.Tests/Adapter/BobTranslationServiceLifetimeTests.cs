using Easydict.BobPlugin.Adapter;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Adapter;

/// <summary>
/// Lifetime and concurrency: a plugin instance keeps module-level state, so the host serializes
/// calls into it and must shut it down cleanly.
/// </summary>
public class BobTranslationServiceLifetimeTests
{
    [Fact]
    public async Task ConcurrentQueriesAreSerialized()
    {
        using var fixture = PluginFixture.Create("echo-global");
        var service = fixture.Service();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => service.TranslateAsync(PluginFixture.Request($"q{i}"))));

        // The fixture numbers its own invocations, so serialization shows as 1..8 with no repeats.
        results.Select(r => r.TranslatedText.Split(')')[0])
            .Should().BeEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ECHO({i}"));
    }

    [Fact]
    public async Task AnUnparsablePluginOnlyFailsWhenItIsFirstUsed()
    {
        using var fixture = PluginFixture.Create("bad-syntax");

        // Constructing the service must not evaluate the script: installing a plugin never costs
        // a JavaScript engine, and a broken one must not break startup.
        var service = fixture.Service();

        var act = async () => await service.TranslateAsync(PluginFixture.Request());

        (await act.Should().ThrowAsync<BobPluginException>()).Which.Code.Should().Be(BobPluginError.ScriptLoadFailed);
    }

    [Fact]
    public async Task DisposeStopsTheInstanceAndFurtherQueriesAreRefused()
    {
        using var fixture = PluginFixture.Create("echo-global");
        var service = new BobTranslationService(fixture.Descriptor, fixture.HttpClient);

        await service.TranslateAsync(PluginFixture.Request());
        service.Dispose();

        var act = async () => await service.TranslateAsync(PluginFixture.Request());

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        using var fixture = PluginFixture.Create("echo-global");
        var service = new BobTranslationService(fixture.Descriptor, fixture.HttpClient);

        service.Dispose();
        var act = service.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void DisposeWithoutEverRunningIsSafe()
    {
        using var fixture = PluginFixture.Create("hang");
        var service = new BobTranslationService(fixture.Descriptor, fixture.HttpClient);

        var act = service.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeDoesNotCloseTheSharedHttpClient()
    {
        using var fixture = PluginFixture.Create("http-post");
        var service = new BobTranslationService(fixture.Descriptor, fixture.HttpClient);
        service.Dispose();

        // The host owns the client, so another plugin can keep using it.
        fixture.Http.EnqueueJson("""{"translation":"ok"}""");
        using var second = new BobTranslationService(fixture.Descriptor, fixture.HttpClient);

        (await second.TranslateAsync(PluginFixture.Request())).TranslatedText.Should().Be("ok");
    }

    [Fact]
    public void TheConstructorRejectsMissingArguments()
    {
        using var fixture = PluginFixture.Create("echo-global");

        var noDescriptor = () => new BobTranslationService(null!, fixture.HttpClient);
        var noClient = () => new BobTranslationService(fixture.Descriptor, null!);

        noDescriptor.Should().Throw<ArgumentNullException>();
        noClient.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task PluginLogLinesAreRetainedForTheSettingsPage()
    {
        using var fixture = PluginFixture.Create("cancel");
        var service = fixture.Service();
        using var cts = new CancellationTokenSource();

        var query = service.TranslateAsync(PluginFixture.Request(), cts.Token);
        await PluginFixture.WaitForLogAsync(service, "translate running");
        cts.Cancel();

        try
        {
            await query;
        }
        catch (OperationCanceledException)
        {
        }

        // The cancel notification reaches the plugin on its own loop thread; the next call into
        // that loop cannot start before it has run.
        await service.ValidateAsync();

        service.RecentLogLines.Should().Contain(line => line.Contains("cancelSignal received"));
    }

    [Fact]
    public async Task AMissingInstallDirectoryFailsTheQueryRatherThanTheConstructor()
    {
        using var fixture = PluginFixture.Create("echo-global");
        var descriptor = fixture.Descriptor with
        {
            InstallDirectory = Path.Combine(Path.GetTempPath(), "easydict-bob-missing", Guid.NewGuid().ToString("N"))
        };
        using var service = new BobTranslationService(descriptor, fixture.HttpClient);

        var act = async () => await service.TranslateAsync(PluginFixture.Request());

        (await act.Should().ThrowAsync<BobPluginException>()).Which.Code.Should().Be(BobPluginError.InvalidPackage);
    }
}
