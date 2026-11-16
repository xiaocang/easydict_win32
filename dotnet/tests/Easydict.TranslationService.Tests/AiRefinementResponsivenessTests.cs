using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Scheduling;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// Uses a real single-thread message pump and externally released gates instead of timing sleeps.
/// These tests run in ordinary CI without a desktop or Windows App SDK runtime.
/// </summary>
[Trait("Category", "UIResponsiveness")]
public sealed class AiRefinementResponsivenessTests
{
    [Theory]
    [InlineData("send")]
    [InlineData("stream")]
    [InlineData("error")]
    public async Task HttpSuggestionPipeline_KeepsUiResponsiveThroughMagpieProfileAndSse(string stage)
    {
        using var ui = new TestUiThread();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var threads = new ConcurrentBag<int>();
        using var client = new HttpClient(new GatedHttpHandler(stage, entered, release, threads));
        var magpie = new MagpieService(client);
        magpie.Configure("http://127.0.0.1:4545/v1/chat/completions", "test-key", "test-model");
        ITranslationService profile = new ProfiledModelCatalogTranslationService(magpie, magpie, magpie, "profile:test", "Gateway", "test", 1);
        AiRefinementDemoState state = null!;
        var commitThread = 0;
        await ui.RunAsync(() =>
        {
            state = AiRefinementDemoState.CreateLive(profile, new TranslationResult
            {
                OriginalText = "source", TranslatedText = "one two", TargetLanguage = Language.English, ServiceName = "Gateway"
            }, false, true)!;
            state.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(state.Words)) commitThread = Environment.CurrentManagedThreadId; };
            return Task.CompletedTask;
        });
        var pending = ui.RunAsync(() => state.PrefetchCandidatesAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var tick = 0; tick < 3; tick++) await ui.TickAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await ui.RunAsync(() => { state.Dispose(); return Task.CompletedTask; });
        }
        threads.Should().NotBeEmpty().And.OnlyContain(thread => thread != ui.ThreadId);
        state.IsCandidatePrefetching.Should().BeFalse();
        if (stage == "error") state.RefinementError.Should().NotBeNullOrEmpty();
        else
        {
            state.RefinementError.Should().BeNull();
            state.Words.Should().HaveCount(2);
            commitThread.Should().Be(ui.ThreadId);
        }
    }

    [Fact]
    public async Task WordRenderingBatches_ProcessInputBeforeAllWordsFinish()
    {
        using var ui = new TestUiThread();
        var rendered = 0;
        var heartbeat = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threads = new List<int>();
        await ui.RunAsync(async () =>
        {
            var completed = await CooperativeBatchProcessor.ProcessAsync(Enumerable.Range(0, 512).ToArray(), (_, index) =>
            {
                rendered++;
                threads.Add(Environment.CurrentManagedThreadId);
                if (index == 0) ui.Post(_ => heartbeat.TrySetResult(rendered), null);
            }, ui.TickAsync, () => true);
            completed.Should().BeTrue();
        });
        var countAtInput = await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
        countAtInput.Should().BeInRange(1, 64, "input must run before the entire word layer is constructed");
        rendered.Should().Be(512);
        threads.Should().OnlyContain(thread => thread == ui.ThreadId, "XAML work must remain on its owning UI thread");
    }

    [Fact]
    public async Task WordRenderingBatches_AbandonStaleResultAtNextYield()
    {
        using var ui = new TestUiThread();
        var current = true;
        var rendered = 0;
        await ui.RunAsync(async () =>
        {
            var completed = await CooperativeBatchProcessor.ProcessAsync(Enumerable.Range(0, 512).ToArray(), (_, index) =>
            {
                rendered++;
                if (index == 0) ui.Post(_ => current = false, null);
            }, ui.TickAsync, () => current);
            completed.Should().BeFalse();
        });
        rendered.Should().BeInRange(1, 64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingSuggestionRequest_DoesNotRunBlockingCallbacksOnUiThread(bool dispose)
    {
        using var ui = new TestUiThread();
        using var releaseCancellation = new ManualResetEventSlim();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var provider = new DelayedWordService
        {
            Handler = async (_, token) =>
            {
                if (Interlocked.Increment(ref count) != 1) return [new("one two", ["other wording"])];
                using var registration = token.Register(() =>
                {
                    cancelling.TrySetResult();
                    if (!releaseCancellation.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancellation was not released.");
                });
                requested.TrySetResult();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return [];
            }
        };
        AiRefinementDemoState state = null!;
        await ui.RunAsync(() =>
        {
            state = AiRefinementDemoState.CreateLive(provider, new TranslationResult
            {
                OriginalText = "source", TranslatedText = "one two", ServiceName = "Test", TargetLanguage = Language.English
            }, false, true)!;
            return Task.CompletedTask;
        });
        var first = ui.RunAsync(() => state.PrefetchCandidatesAsync());
        Task next = Task.CompletedTask;
        try
        {
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            next = ui.RunAsync(() =>
            {
                if (!dispose) return state.PrefetchCandidatesAsync();
                state.Dispose();
                return Task.CompletedTask;
            });
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ui.TickAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseCancellation.Set();
            await Task.WhenAll(first, next).WaitAsync(TimeSpan.FromSeconds(5));
            await ui.RunAsync(() => { state.Dispose(); return Task.CompletedTask; });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UiMessageLoop_RemainsResponsiveDuringQueryAndCandidatePreparation(bool discardResponse)
    {
        using var ui = new TestUiThread();
        using var releasePreparation = new ManualResetEventSlim();
        var preparationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelayedWordService();
        AiRefinementDemoState state = null!;
        var wordNotifications = 0;
        var textNotifications = 0;
        var commitThread = 0;
        await ui.RunAsync(() =>
        {
            state = AiRefinementDemoState.CreateLive(provider, new TranslationResult
            {
                OriginalText = "source", TranslatedText = "one two", ServiceName = "Test", TargetLanguage = Language.English
            }, false, true)!;
            state.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(state.Words)) { wordNotifications++; commitThread = Environment.CurrentManagedThreadId; }
                if (args.PropertyName == nameof(state.TranslationText)) textNotifications++;
            };
            return Task.CompletedTask;
        });
        var pending = ui.RunAsync(() => state.PrefetchCandidatesAsync());
        try
        {
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ui.TickAsync().WaitAsync(TimeSpan.FromSeconds(2));
            provider.Response.SetResult([
                new("one ", ["a "]),
                new("two", new BlockingAlternatives(preparationStarted, releasePreparation))
            ]);
            await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // This tick must run even while the response's candidate data is still being processed.
            await ui.TickAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (discardResponse) await ui.RunAsync(() => { state.Dispose(); return Task.CompletedTask; });
        }
        finally
        {
            releasePreparation.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await ui.RunAsync(() => { state.Dispose(); return Task.CompletedTask; });
        }
        wordNotifications.Should().Be(discardResponse ? 0 : 1);
        textNotifications.Should().Be(0, "segmentation must not rewrite an unchanged translation");
        if (!discardResponse) commitThread.Should().Be(ui.ThreadId, "only the prepared snapshot is published on the UI thread");
    }

    private sealed class BlockingAlternatives(TaskCompletionSource started, ManualResetEventSlim release) : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => index == 0 ? "three" : throw new IndexOutOfRangeException();
        public IEnumerator<string> GetEnumerator()
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Candidate preparation was not released.");
            yield return "three";
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GatedHttpHandler(string stage, TaskCompletionSource entered, ManualResetEventSlim release, ConcurrentBag<int> threads) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            threads.Add(Environment.CurrentManagedThreadId);
            if (stage is "send" or "error") Block(entered, release);
            if (stage == "error") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"test rejection\"}}", Encoding.UTF8, "application/json")
            });
            var json = JsonSerializer.Serialize(new[]
            {
                new { Text = "one ", Alternatives = new[] { "a " } },
                new { Text = "two", Alternatives = new[] { "three" } }
            });
            var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = json } } } });
            var bytes = Encoding.UTF8.GetBytes("data: " + chunk + "\n\ndata: [DONE]\n\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedSseStream(bytes, stage == "stream", entered, release, threads))
            });
        }
    }

    private static void Block(TaskCompletionSource entered, ManualResetEventSlim release)
    {
        entered.TrySetResult();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("HTTP stage was not released.");
    }

    private sealed class GatedSseStream(byte[] bytes, bool gateRead, TaskCompletionSource entered, ManualResetEventSlim release, ConcurrentBag<int> threads) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        private bool _firstRead = true;
        private void BeforeRead()
        {
            threads.Add(Environment.CurrentManagedThreadId);
            if (!_firstRead) return;
            _firstRead = false;
            if (gateRead) Block(entered, release);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeforeRead();
            // Deliberately fragment the SSE payload to exercise incremental decoding and parsing.
            return _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            BeforeRead();
            return _inner.Read(buffer, offset, Math.Min(count, 7));
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class DelayedWordService : ITranslationService, IWordRefinementService
    {
        public Func<TranslationRefinementRequest, CancellationToken, Task<IReadOnlyList<WordAlternatives>>>? Handler { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<WordAlternatives>> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<WordAlternatives>> GetWordAlternativesAsync(TranslationRefinementRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return Handler?.Invoke(request, cancellationToken) ?? Response.Task;
        }
        public Task<string> ApplyWordAlternativeAsync(WordReplacementRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string ServiceId => "delayed";
        public string DisplayName => "Delayed service";
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public IReadOnlyList<Language> SupportedLanguages => [Language.English];
        public bool SupportsLanguagePair(Language from, Language to) => true;
        public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestUiThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _messages = new();
        private readonly Thread _thread;
        public int ThreadId => _thread.ManagedThreadId;

        public TestUiThread()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var message in _messages.GetConsumingEnumerable()) message.Callback(message.State);
            }) { IsBackground = true, Name = "Refinement test UI" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback callback, object? state) => _messages.Add((callback, state));
        public Task TickAsync() => RunAsync(() => Task.CompletedTask);
        public Task RunAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, null);
            return completion.Task;
        }
        public void Dispose()
        {
            _messages.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _messages.Dispose();
        }
    }
}
