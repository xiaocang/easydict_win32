using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

public sealed class AiRefinementLiveTests
{
    private static TranslationResult Result(string text = "安静的空间。") => new()
    {
        OriginalText = "A quiet space.", TranslatedText = text, TargetLanguage = Language.SimplifiedChinese, ServiceName = "Test"
    };

    private static void Enqueue(MockHttpMessageHandler handler, string text) =>
        handler.EnqueueStreamingResponse([JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } })]);

    [Fact]
    public async Task MagpieProfile_UsesRealCandidatesAndContext_WithProfileEndpointAndModel()
    {
        var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var magpie = new MagpieService(client);
        magpie.Configure("http://127.0.0.1:4545/v1/chat/completions", "gateway-key", "provider/model");
        ITranslationService profile = new ProfiledModelCatalogTranslationService(magpie, magpie, magpie, "profile:test", "My gateway", "test", 1);
        using var state = AiRefinementDemoState.CreateLive(profile, Result(), true, true)!;
        Enqueue(handler, """[{"Text":"安静的空间","Alternatives":["宁静的环境"]},{"Text":"。","Alternatives":[]}]""");
        await state.PrefetchCandidatesAsync();
        state.RefinementError.Should().BeNull();
        state.Words.Should().HaveCount(2);
        await state.SelectWordAsync("0");
        state.SelectedCandidates.Should().Equal("宁静的环境");
        Enqueue(handler, "宁静的环境。");
        Enqueue(handler, """[{"Text":"宁静的环境","Alternatives":["清静的空间"]},{"Text":"。","Alternatives":[]}]""");
        (await state.ApplyCandidateAsync("0", "宁静的环境")).Should().BeTrue();
        state.TranslationText.Should().Be("宁静的环境。");

        Enqueue(handler, "宁静的工作环境。");
        Enqueue(handler, """[{"Text":"宁静的工作环境","Alternatives":["清静的办公环境"]},{"Text":"。","Alternatives":[]}]""");
        await state.ApplyContextAsync("Use a formal tone.");
        state.TranslationText.Should().Be("宁静的工作环境。");
        state.AppliedContext.Should().Be("Use a formal tone.");
        state.RefinementError.Should().BeNull();
        handler.Requests.Should().HaveCount(5);
        handler.LastRequest!.RequestUri!.Port.Should().Be(4545);
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("gateway-key");
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("provider/model");
        body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString().Should().Contain("Use a formal tone.");
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("[{\"Text\":\"changed translation\",\"Alternatives\":[]}]")]
    public async Task InvalidWordResponse_PreservesTranslationAndSurfacesError(string response)
    {
        var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var magpie = new MagpieService(client);
        magpie.Configure("http://127.0.0.1:4545/v1/chat/completions", "", "model");
        using var state = AiRefinementDemoState.CreateLive(magpie, Result(), false, true)!;
        Enqueue(handler, response);
        await state.PrefetchCandidatesAsync();
        state.TranslationText.Should().Be(Result().TranslatedText);
        state.RefinementError.Should().NotBeNullOrEmpty();
        state.IsCandidatePrefetching.Should().BeFalse();
    }

    [Fact]
    public async Task FailedContext_PreservesTranslationAndAppliedContext()
    {
        var service = new TestRefinementService { Context = (_, _) => throw new HttpRequestException("gateway unavailable") };
        using var state = AiRefinementDemoState.CreateLive(service, Result(), true, false)!;
        await state.ApplyContextAsync("formal");
        state.AppliedContext.Should().BeEmpty();
        state.TranslationText.Should().Be(Result().TranslatedText);
        state.RefinementError.Should().Be("gateway unavailable");
        state.IsApplyingContext.Should().BeFalse();
    }

    [Fact]
    public async Task ChoosingAlternative_CanChangeFollowingWordsAndRefreshesCandidates()
    {
        WordReplacementRequest? captured = null;
        var service = new TestRefinementService
        {
            Words = (request, _) => Task.FromResult<IReadOnlyList<WordAlternatives>>(request.TranslatedText.StartsWith("Today they", StringComparison.Ordinal)
                ? [new("Today ", []), new("they", ["he"]), new(" work here.", [])]
                : [new("Today ", []), new("he", ["she"]), new(" works here.", [])]),
            Replacement = (request, _) => { captured = request; return Task.FromResult("Today he works here."); }
        };
        using var state = AiRefinementDemoState.CreateLive(service, Result("Today they work here."), false, true)!;
        await state.PrefetchCandidatesAsync();
        await state.SelectWordAsync("1");
        (await state.ApplyCandidateAsync("1", "he")).Should().BeTrue();
        state.TranslationText.Should().Be("Today he works here.");
        captured!.StartIndex.Should().Be(6);
        captured.Length.Should().Be(4);
        state.SelectedWordId.Should().BeNull();
        await state.SelectWordAsync("1");
        state.SelectedCandidates.Should().Equal("she");
        state.IsRefining.Should().BeFalse();
    }

    [Theory]
    [InlineData("Today she works here.")]
    [InlineData("Yesterday he works here.")]
    public async Task ReplacementThatChangesPrefixOrChosenWord_IsRejected(string response)
    {
        var service = new TestRefinementService
        {
            Words = (_, _) => Task.FromResult<IReadOnlyList<WordAlternatives>>([new("Today ", []), new("they", ["he"]), new(" work here.", [])]),
            Replacement = (_, _) => Task.FromResult(response)
        };
        using var state = AiRefinementDemoState.CreateLive(service, Result("Today they work here."), false, true)!;
        await state.PrefetchCandidatesAsync();
        await state.SelectWordAsync("1");
        (await state.ApplyCandidateAsync("1", "he")).Should().BeFalse();
        state.TranslationText.Should().Be("Today they work here.");
        state.RefinementError.Should().NotBeNullOrEmpty();
        state.IsRefining.Should().BeFalse();
        await state.SelectWordAsync("1");
        state.SelectedCandidates.Should().Equal("he");
    }

    [Fact]
    public async Task Reset_DiscardsLateWordReplacement()
    {
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestRefinementService
        {
            Words = (_, _) => Task.FromResult<IReadOnlyList<WordAlternatives>>([new("They", ["He"]), new(" work here.", [])]),
            Replacement = (_, _) => response.Task
        };
        var state = AiRefinementDemoState.CreateLive(service, Result("They work here."), false, true)!;
        var row = new ServiceQueryResult { Result = Result("They work here."), AiRefinementDemo = state };
        await state.PrefetchCandidatesAsync();
        await state.SelectWordAsync("0");
        var pending = state.ApplyCandidateAsync("0", "He");
        state.IsRefining.Should().BeTrue();
        row.Reset();
        response.SetResult("He works here.");
        (await pending).Should().BeFalse();
        row.Result.Should().BeNull();
        state.TranslationText.Should().Be("They work here.");
    }

    [Fact]
    public async Task NewContext_DiscardsOldCandidatesEvenIfProviderIgnoresCancellation()
    {
        var old = new TaskCompletionSource<IReadOnlyList<WordAlternatives>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new TestRefinementService
        {
            Words = (_, _) =>
            {
                if (Interlocked.Increment(ref calls) != 1) return Task.FromResult<IReadOnlyList<WordAlternatives>>([new("新的译文", ["新表达"])]);
                started.TrySetResult();
                return old.Task;
            },
            Context = (_, _) => Task.FromResult("新的译文")
        };
        using var state = AiRefinementDemoState.CreateLive(service, Result(), true, true)!;
        var pending = state.PrefetchCandidatesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.ApplyContextAsync("new context");
        old.SetResult([new(Result().TranslatedText, ["stale"])]);
        await pending;
        state.TranslationText.Should().Be("新的译文");
        await state.SelectWordAsync("0");
        state.SelectedCandidates.Should().Equal("新表达");
    }

    [Theory]
    [InlineData("candidates")]
    [InlineData("context")]
    [InlineData("replacement")]
    public async Task SynchronousProviderWork_DoesNotBlockTheCallingThread(string operation)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void BlockProvider()
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Provider was not released by the test.");
        }
        var service = new TestRefinementService
        {
            Words = (request, _) =>
            {
                if (operation == "candidates") BlockProvider();
                return Task.FromResult<IReadOnlyList<WordAlternatives>>([new(request.TranslatedText, ["alternative"])]);
            },
            Context = (_, _) => { BlockProvider(); return Task.FromResult("revised"); },
            Replacement = (_, _) => { BlockProvider(); return Task.FromResult("alternative"); }
        };
        using var state = AiRefinementDemoState.CreateLive(service, Result(), operation == "context", operation != "context")!;
        if (operation == "replacement")
        {
            await state.PrefetchCandidatesAsync();
            await state.SelectWordAsync("0");
        }
        var caller = Task.Run(async () =>
        {
            Task pending = operation switch
            {
                "context" => state.ApplyContextAsync("formal"),
                "replacement" => state.ApplyCandidateAsync("0", "alternative"),
                _ => state.PrefetchCandidatesAsync()
            };
            returned.TrySetResult();
            await pending;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await caller.WaitAsync(TimeSpan.FromSeconds(5));
        }
        state.RefinementError.Should().BeNull();
    }

    [Fact]
    public async Task Reset_DisposesStateAndDiscardsLateContext()
    {
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestRefinementService { Context = (_, _) => response.Task };
        var state = AiRefinementDemoState.CreateLive(service, Result(), true, false)!;
        var row = new ServiceQueryResult { AiRefinementDemo = state, Result = Result() };
        var pending = state.ApplyContextAsync("formal");
        row.Reset();
        response.SetResult("stale");
        await pending;
        row.AiRefinementDemo.Should().BeNull();
        row.Result.Should().BeNull();
        state.TranslationText.Should().Be(Result().TranslatedText);
    }

    [Fact]
    public async Task DisabledProfile_RejectsRefinementBeforeHttpRequest()
    {
        var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var magpie = new MagpieService(client);
        magpie.Configure("http://127.0.0.1:4545/v1/chat/completions", "", "model");
        ITranslationService profile = new ProfiledTranslationService(magpie, "profile:test", "Gateway", "test", 1, false);
        var request = new TranslationRefinementRequest("source", "text", Language.English, "formal");
        var error = await Assert.ThrowsAsync<TranslationException>(() => profile.ContextTranslation!.TranslateWithContextAsync(request));
        error.ErrorCode.Should().Be(TranslationErrorCode.ServiceDisabled);
        await Assert.ThrowsAsync<TranslationException>(() => profile.WordRefinement!.GetWordAlternativesAsync(request));
        await Assert.ThrowsAsync<TranslationException>(() => profile.WordRefinement!.ApplyWordAlternativeAsync(new(request, 0, 1, "T")));
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoteMagpie_RejectsBothRefinementsBeforeHttpRequest()
    {
        var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var magpie = new MagpieService(client);
        magpie.Configure("https://remote.example/v1/chat/completions", "key", "model");
        var request = new TranslationRefinementRequest("source", "text", Language.English, "formal");
        await Assert.ThrowsAsync<InvalidOperationException>(() => magpie.TranslateWithContextAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => magpie.GetWordAlternativesAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => magpie.ApplyWordAlternativeAsync(new(request, 0, 1, "T")));
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Capabilities_AreIndependentOfServiceIdAndStreaming(bool context, bool words)
    {
        ITranslationService service = new TestRefinementService { SupportsContext = context, SupportsWords = words };
        using var state = AiRefinementDemoState.CreateLive(service, Result(), true, true)!;
        state.CanShowContext.Should().Be(context);
        state.CanShowWordRefinement.Should().Be(words);
        AiRefinementDemoState.CreateLive(service, Result(), false, false).Should().BeNull();
    }

    private sealed class TestRefinementService : ITranslationService, IContextTranslationService, IWordRefinementService
    {
        public bool SupportsContext { get; init; } = true;
        public bool SupportsWords { get; init; } = true;
        public IContextTranslationService? ContextTranslation => SupportsContext ? this : null;
        public IWordRefinementService? WordRefinement => SupportsWords ? this : null;
        public Func<TranslationRefinementRequest, CancellationToken, Task<string>> Context { get; init; } = (_, _) => Task.FromResult("revised");
        public Func<TranslationRefinementRequest, CancellationToken, Task<IReadOnlyList<WordAlternatives>>> Words { get; init; } = (_, _) => Task.FromResult<IReadOnlyList<WordAlternatives>>([]);
        public Func<WordReplacementRequest, CancellationToken, Task<string>> Replacement { get; init; } = (request, _) =>
            Task.FromResult(request.Translation.TranslatedText[..request.StartIndex] + request.Replacement + request.Translation.TranslatedText[(request.StartIndex + request.Length)..]);
        public Task<string> ApplyWordAlternativeAsync(WordReplacementRequest request, CancellationToken cancellationToken = default) => Replacement(request, cancellationToken);
        public Task<string> TranslateWithContextAsync(TranslationRefinementRequest request, CancellationToken cancellationToken = default) => Context(request, cancellationToken);
        public Task<IReadOnlyList<WordAlternatives>> GetWordAlternativesAsync(TranslationRefinementRequest request, CancellationToken cancellationToken = default) => Words(request, cancellationToken);
        public string ServiceId => "future-provider";
        public string DisplayName => "Future provider";
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public IReadOnlyList<Language> SupportedLanguages => [Language.English, Language.SimplifiedChinese];
        public bool SupportsLanguagePair(Language from, Language to) => true;
        public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
