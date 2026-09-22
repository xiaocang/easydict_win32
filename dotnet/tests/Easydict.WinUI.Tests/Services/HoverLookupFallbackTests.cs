using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class HoverLookupFallbackTests
{
    private static TranslationResult Result(string text = "meaning") => new()
    {
        OriginalText = "word",
        TranslatedText = text,
        ServiceName = "test",
    };

    [Fact]
    public async Task FirstSuccess_DoesNotCallLaterServices()
    {
        var calls = new List<string>();
        var expected = Result();
        var result = await HoverLookupFallback.TranslateAsync(["first", "second"], (id, _) =>
        {
            calls.Add(id);
            return Task.FromResult(expected);
        }, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().BeSameAs(expected);
        calls.Should().Equal("first");
    }

    private static TranslationException ProxyFailure(string serviceId) => new(
        "Cannot reach the HTTP proxy http://127.0.0.1:59999: connection refused")
    {
        ErrorCode = TranslationErrorCode.ProxyError,
        ServiceId = serviceId,
    };

    [Fact]
    public async Task ProxyFailure_DoesNotStopALocalDictionaryFromAnswering()
    {
        // Auto order puts locally imported mdx:: dictionaries after the remote services, and they
        // do no networking at all — so a dead proxy must not cut the chain short.
        var calls = new List<string>();
        var expected = Result();

        var result = await HoverLookupFallback.TranslateAsync(
            ["youdao", "google_web", "mdx::local"], (id, _) =>
            {
                calls.Add(id);
                return id.StartsWith(HoverLookupRules.MdxServiceIdPrefix, StringComparison.Ordinal)
                    ? Task.FromResult(expected)
                    : throw ProxyFailure(id);
            }, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().BeSameAs(expected);
        calls.Should().Equal("youdao", "google_web", "mdx::local");
    }

    [Fact]
    public async Task AfterAProxyFailure_NetworkFreeCandidatesAreTriedFirst()
    {
        // Waiting out a connect timeout on every remote candidate before reaching a local
        // dictionary is wasted once the proxy is known to be dead. Reordered, not filtered: the
        // remote candidate that was skipped past is still attempted afterwards.
        var calls = new List<string>();
        var expected = Result();

        var result = await HoverLookupFallback.TranslateAsync(
            ["youdao", "google_web", "mdx::local"], (id, _) =>
            {
                calls.Add(id);
                return id == "mdx::local" ? Task.FromResult(expected) : throw ProxyFailure(id);
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            HoverLookupRules.IsNetworkFree);

        result.Should().BeSameAs(expected);
        calls.Should().Equal("youdao", "mdx::local");
    }

    [Fact]
    public async Task APromotedLocalDictionaryThatMisses_StillLeavesTheRemoteCandidatesTried()
    {
        // The promotion must not cost a candidate: google_web is reached after the local
        // dictionary returns nothing useful.
        var calls = new List<string>();
        var expected = Result();

        var result = await HoverLookupFallback.TranslateAsync(
            ["youdao", "google_web", "mdx::local"], (id, _) =>
            {
                calls.Add(id);
                return id switch
                {
                    "youdao" => throw ProxyFailure(id),
                    "mdx::local" => Task.FromResult(Result("   ")),
                    _ => Task.FromResult(expected),
                };
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            HoverLookupRules.IsNetworkFree);

        result.Should().BeSameAs(expected);
        calls.Should().Equal("youdao", "mdx::local", "google_web");
    }

    [Fact]
    public async Task WhenEverythingFails_TheProxyIsReportedRatherThanTheLastService()
    {
        // The dead proxy is the one thing the user can act on, so it should not be buried under
        // whichever service happened to fail last.
        var act = async () => await HoverLookupFallback.TranslateAsync(
            ["youdao", "google_web"], (id, _) =>
            {
                if (id == "youdao")
                {
                    throw ProxyFailure(id);
                }

                throw new HttpRequestException("offline");
            }, TimeSpan.FromSeconds(5), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.WithInnerException<TranslationException>()
            .Which.ErrorCode.Should().Be(TranslationErrorCode.ProxyError);
    }

    [Fact]
    public void TheHoverDeadline_OutlastsTheProxyConnectTimeout()
    {
        // If the hover attempt gave up first, a dead proxy would be indistinguishable from the
        // user dismissing the popup, and every service would report an unexplained timeout again.
        TimeSpan.FromMilliseconds(HoverWordLookupService.TranslationTimeoutMs)
            .Should().BeGreaterThan(TranslationManager.ProxiedConnectTimeout);
    }

    [Fact]
    public async Task ErrorsAndEmptyResults_FallBackInOrderUntilSuccess()
    {
        var calls = new List<string>();
        var expected = Result();
        var result = await HoverLookupFallback.TranslateAsync(
            ["error", "miss", "empty", "success", "unused"], (id, _) =>
            {
                calls.Add(id);
                return id switch
                {
                    "error" => throw new HttpRequestException("offline"),
                    "miss" => Task.FromResult(Result("informational text") with { ResultKind = TranslationResultKind.NoResult }),
                    "empty" => Task.FromResult(Result("  ")),
                    _ => Task.FromResult(expected),
                };
            }, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().BeSameAs(expected);
        calls.Should().Equal("error", "miss", "empty", "success");
    }

    [Fact]
    public async Task AttemptTimeout_CancelsAttemptAndTriesNextService()
    {
        var calls = new List<string>();
        CancellationToken firstToken = default;
        var pending = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await HoverLookupFallback.TranslateAsync(["slow", "success"], (id, ct) =>
            {
                calls.Add(id);
                if (id == "slow")
                {
                    firstToken = ct;
                    return pending.Task; // Even a service that ignores cancellation must not block fallback.
                }

                return Task.FromResult(Result());
            }, TimeSpan.FromMilliseconds(50), CancellationToken.None);

            result.Should().NotBeNull();
            firstToken.IsCancellationRequested.Should().BeTrue();
            calls.Should().Equal("slow", "success");
        }
        finally
        {
            pending.TrySetResult(Result("late"));
        }
    }

    [Fact]
    public async Task DismissalCancellation_DoesNotTryNextService()
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        Func<Task> act = () => HoverLookupFallback.TranslateAsync(["first", "second"], (id, ct) =>
        {
            calls.Add(id);
            cts.Cancel();
            return Task.FromCanceled<TranslationResult>(ct);
        }, TimeSpan.FromSeconds(5), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Equal("first");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinitionsOrAlternatives_AreUsefulWithoutTranslatedText(bool alternatives)
    {
        var expected = alternatives
            ? Result("") with { Alternatives = ["meaning"] }
            : Result("") with { WordResult = new WordResult { Definitions = [new Definition { Meanings = ["meaning"] }] } };
        var result = await HoverLookupFallback.TranslateAsync(["dictionary"], (_, _) => Task.FromResult(expected),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task AllEmpty_ReturnsNoResult()
    {
        var result = await HoverLookupFallback.TranslateAsync(["first", "second"], (_, _) => Task.FromResult(Result("")),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AllFailed_ReportsErrorAfterTryingEveryService()
    {
        var calls = new List<string>();
        Func<Task> act = () => HoverLookupFallback.TranslateAsync(["first", "second"], (id, _) =>
        {
            calls.Add(id);
            throw new HttpRequestException("offline");
        }, TimeSpan.FromSeconds(5), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Equal("first", "second");
    }
}
