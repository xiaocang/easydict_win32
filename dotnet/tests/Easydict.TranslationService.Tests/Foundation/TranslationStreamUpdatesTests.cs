using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Streaming;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

public class TranslationStreamUpdatesTests : IDisposable
{
    private readonly TranslationManager _manager = new();

    private static TranslationRequest Request() => new()
    {
        Text = "hello",
        FromLanguage = Language.English,
        ToLanguage = Language.SimplifiedChinese
    };

    private static TranslationResult Completed(string text, TranslationResultKind kind = TranslationResultKind.Success) => new()
    {
        TranslatedText = text,
        OriginalText = "hello",
        ServiceName = "Rich",
        ResultKind = kind,
        InfoMessage = kind == TranslationResultKind.NoResult ? "nothing" : null
    };

    private async Task<List<TranslationStreamUpdate>> CollectAsync(string serviceId)
    {
        var list = new List<TranslationStreamUpdate>();
        await foreach (var u in _manager.TranslateStreamUpdatesAsync(Request(), serviceId: serviceId))
        {
            list.Add(u);
        }

        return list;
    }

    [Fact]
    public async Task LegacyStreamingService_IsWrappedAsDeltas()
    {
        _manager.RegisterService(new LegacyStreamTestService("legacy", "你", "好"));

        var updates = await CollectAsync("legacy");

        updates.Should().AllBeOfType<TranslationStreamUpdate.TextDelta>();
        updates.Cast<TranslationStreamUpdate.TextDelta>().Select(d => d.Text).Should().Equal("你", "好");
    }

    [Fact]
    public async Task RichService_IsPassedThroughIncludingCompleted()
    {
        var scripted = new TranslationStreamUpdate[]
        {
            new TranslationStreamUpdate.TextSnapshot("你"),
            new TranslationStreamUpdate.TextSnapshot("你好"),
            new TranslationStreamUpdate.Completed(Completed("你好"))
        };
        _manager.RegisterService(new RichStreamTestService("rich", scripted));

        var updates = await CollectAsync("rich");

        updates.Should().HaveCount(3);
        updates[2].Should().BeOfType<TranslationStreamUpdate.Completed>()
            .Which.Result.TranslatedText.Should().Be("你好");
    }

    [Fact]
    public async Task RichService_NoResultCompletion_IsDeliveredWithEmptyText()
    {
        var scripted = new TranslationStreamUpdate[] { new TranslationStreamUpdate.Completed(Completed("", TranslationResultKind.NoResult)) };
        _manager.RegisterService(new RichStreamTestService("rich-none", scripted));

        var updates = await CollectAsync("rich-none");

        var done = updates.Should().ContainSingle().Which.Should().BeOfType<TranslationStreamUpdate.Completed>().Which;
        done.Result.ResultKind.Should().Be(TranslationResultKind.NoResult);
        done.Result.TranslatedText.Should().BeEmpty();
    }

    [Fact]
    public async Task NonStreamingService_YieldsSingleCompleted()
    {
        _manager.RegisterService(new PolicyTestService("plain"));

        var updates = await CollectAsync("plain");

        updates.Should().ContainSingle().Which.Should().BeOfType<TranslationStreamUpdate.Completed>()
            .Which.Result.TranslatedText.Should().Be("Translated: hello");
    }

    [Fact]
    public async Task RichService_ConservativePolicy_IsExecutedEveryTime()
    {
        var service = new RichStreamTestService("rich-nocache",
            new TranslationStreamUpdate[] { new TranslationStreamUpdate.Completed(Completed("x")) });
        _manager.RegisterService(service);

        await CollectAsync("rich-nocache");
        var second = await CollectAsync("rich-nocache");

        service.StreamCallCount.Should().Be(2);
        second.OfType<TranslationStreamUpdate.Completed>().Single().Result.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task RichService_CachingPolicy_ServesSecondCallFromCache()
    {
        var service = new RichStreamTestService("rich-cache",
            new TranslationStreamUpdate[] { new TranslationStreamUpdate.TextSnapshot("x"), new TranslationStreamUpdate.Completed(Completed("x")) },
            ServiceExecutionPolicy.Default);
        _manager.RegisterService(service);

        await CollectAsync("rich-cache");
        var second = await CollectAsync("rich-cache");

        service.StreamCallCount.Should().Be(1);
        second.Should().ContainSingle().Which.Should().BeOfType<TranslationStreamUpdate.Completed>()
            .Which.Result.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownService_Throws()
    {
        var act = async () => await CollectAsync("nope");
        await act.Should().ThrowAsync<TranslationException>();
    }

    [Fact]
    public async Task ToDeltas_EmitsOnlyExtensions()
    {
        var updates = new TranslationStreamUpdate[]
        {
            new TranslationStreamUpdate.TextDelta("ab"),
            new TranslationStreamUpdate.TextSnapshot("abcd"),   // extends → "cd"
            new TranslationStreamUpdate.TextSnapshot("zz"),     // non-monotonic → ignored
            new TranslationStreamUpdate.TextSnapshot("abc"),    // shorter → ignored
            new TranslationStreamUpdate.Completed(Completed("abcdef")) // extends → "ef"
        };

        var deltas = new List<string>();
        await foreach (var d in StreamUpdateAdapter.ToDeltas(ToAsync(updates)))
        {
            deltas.Add(d);
        }

        deltas.Should().Equal("ab", "cd", "ef");
    }

    [Fact]
    public void SuffixIfExtends_Cases()
    {
        StreamUpdateAdapter.SuffixIfExtends(new StringBuilder(), "abc").Should().Be("abc");
        StreamUpdateAdapter.SuffixIfExtends(new StringBuilder("ab"), "abc").Should().Be("c");
        StreamUpdateAdapter.SuffixIfExtends(new StringBuilder("ab"), "ab").Should().BeEmpty();
        StreamUpdateAdapter.SuffixIfExtends(new StringBuilder("ab"), "a").Should().BeNull();
        StreamUpdateAdapter.SuffixIfExtends(new StringBuilder("ab"), "xb").Should().BeNull();
        StreamUpdateAdapter.SuffixIfExtends(new StringBuilder("ab"), null).Should().BeNull();
    }

    private static async IAsyncEnumerable<TranslationStreamUpdate> ToAsync(IEnumerable<TranslationStreamUpdate> updates)
    {
        foreach (var u in updates)
        {
            await Task.Yield();
            yield return u;
        }
    }

    public void Dispose() => _manager.Dispose();
}
