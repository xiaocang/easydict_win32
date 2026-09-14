using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Adapter;

/// <summary>
/// Covers <c>$http</c>: what the plugin sends, what it gets back, and what the host refuses.
/// </summary>
public class BobTranslationServiceHttpTests
{
    [Fact]
    public async Task PostSendsAJsonBodyAndTheHandlerSeesTheParsedResponse()
    {
        using var fixture = PluginFixture.Create("http-post");
        fixture.Http.EnqueueJson("""{"translation":"你好"}""");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("hello"));

        result.TranslatedText.Should().Be("你好");

        var request = fixture.Http.Requests.Should().ContainSingle().Which;
        request.Method.Should().Be("POST");
        request.Url.Should().Be("https://plugin.invalid/translate");
        request.Headers.Should().ContainKey("X-Test").WhoseValue.Should().Be("yes");
        request.Headers["Content-Type"].Should().StartWith("application/json");
        request.Body.Should().Be("""{"text":"hello","to":"zh-Hans"}""");
    }

    [Fact]
    public async Task ANetworkFailureReachesThePluginAsAnError()
    {
        using var fixture = PluginFixture.Create("http-post");
        fixture.Http.EnqueueFailure("connection refused");

        var act = async () => await fixture.Service().TranslateAsync(PluginFixture.Request());

        var exception = (await act.Should().ThrowAsync<TranslationException>()).Which;
        exception.ErrorCode.Should().Be(TranslationErrorCode.NetworkError);
        exception.Message.Should().Contain("connection refused");
    }

    [Fact]
    public async Task ThePromiseFormWorksFromAnAsyncTranslate()
    {
        using var fixture = PluginFixture.Create("http-promise");
        fixture.Http.EnqueueJson("""{"translation":"bonjour"}""");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("hello"));

        result.TranslatedText.Split('\n').Should().Equal("bonjour", "status=200");
        fixture.Http.Requests.Should().ContainSingle()
            .Which.Url.Should().Be("https://plugin.invalid/translate?q=hello");
    }

    [Fact]
    public async Task QueryParametersAreUrlEncoded()
    {
        using var fixture = PluginFixture.Create("http-promise");
        fixture.Http.EnqueueJson("""{"translation":"x"}""");

        await fixture.Service().TranslateAsync(PluginFixture.Request("a b&c=d"));

        fixture.Http.Requests.Should().ContainSingle()
            .Which.Url.Should().Be("https://plugin.invalid/translate?q=a%20b%26c%3Dd");
    }

    [Fact]
    public async Task StreamedChunksReachThePluginAndBecomeSnapshots()
    {
        using var fixture = PluginFixture.Create("http-stream");
        fixture.Http.EnqueueText("chunked body");

        var updates = new List<TranslationStreamUpdate>();
        await foreach (var update in fixture.Service().TranslateStreamUpdatesAsync(PluginFixture.Request()))
        {
            updates.Add(update);
        }

        updates.OfType<TranslationStreamUpdate.TextSnapshot>().Should().NotBeEmpty();
        updates.OfType<TranslationStreamUpdate.TextSnapshot>().Last().Text.Should().Be("chunked body");
        updates.OfType<TranslationStreamUpdate.Completed>().Should().ContainSingle()
            .Which.Result.TranslatedText.Should().Be("chunked body");
    }

    [Fact]
    public async Task NonHttpUrlsAreRefusedWithoutReachingTheNetwork()
    {
        using var fixture = PluginFixture.Create("http-badurl");

        var act = async () => await fixture.Service().TranslateAsync(PluginFixture.Request());

        var exception = (await act.Should().ThrowAsync<TranslationException>()).Which;
        exception.Message.Should().Contain("http(s)");
        fixture.Http.Requests.Should().BeEmpty();
    }
}
