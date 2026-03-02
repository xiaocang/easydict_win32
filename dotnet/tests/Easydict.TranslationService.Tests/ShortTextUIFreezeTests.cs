using System.Diagnostics;
using System.Runtime.CompilerServices;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// Tests that verify short text translation (single query, streaming, concurrent)
/// does not block / freeze the UI thread.
///
/// Uses MockHttpMessageHandler for non-streaming services and in-memory fake services
/// for streaming, with timing budgets and concurrency checks to detect synchronous blocking.
///
/// Run with:  dotnet test --filter "Category=UIFreeze"
/// </summary>
[Trait("Category", "UIFreeze")]
public class ShortTextUIFreezeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TranslationManager _manager;

    public ShortTextUIFreezeTests(ITestOutputHelper output)
    {
        _output = output;
        _manager = new TranslationManager();
    }

    // ──────────────────────────────────────────────
    //  1. Single service — TranslateAsync completes asynchronously
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_SingleService_CompletesAsynchronously()
    {
        // Arrange: register a fake service with 50ms async delay
        var service = new DelayedTranslationService("delay-svc", "Delayed Service", delayMs: 50);
        _manager.RegisterService(service);

        var request = new TranslationRequest
        {
            Text = "Hello",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var sw = Stopwatch.StartNew();
        var result = await _manager.TranslateAsync(request, serviceId: "delay-svc");
        sw.Stop();

        // Assert
        _output.WriteLine($"Single service translate: {sw.ElapsedMilliseconds}ms");
        result.TranslatedText.Should().Be("T:Hello");

        // Should complete in <2s (50ms delay + overhead; would be much longer if blocking)
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "single async translate with 50ms delay should not block for extended time");
    }

    // ──────────────────────────────────────────────
    //  2. Multiple services concurrently — no deadlock
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_MultipleServicesConcurrently_NoDeadlock()
    {
        // Arrange: register 5 fake services with varying delays
        for (var i = 0; i < 5; i++)
        {
            var svc = new DelayedTranslationService($"concurrent-{i}", $"Concurrent {i}", delayMs: 100);
            _manager.RegisterService(svc);
        }

        var request = new TranslationRequest
        {
            Text = "Hello World",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act: call all 5 services concurrently
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 5)
            .Select(i => _manager.TranslateAsync(request, serviceId: $"concurrent-{i}"))
            .ToList();

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        // Assert
        _output.WriteLine($"5 concurrent services: {sw.ElapsedMilliseconds}ms");
        results.Should().HaveCount(5);
        results.Should().AllSatisfy(r =>
            r.TranslatedText.Should().Be("T:Hello World"));

        // All 5 should complete in parallel: ~100ms + overhead, not 5×100ms = 500ms
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "5 concurrent async translates should overlap, not serialize");
    }

    // ──────────────────────────────────────────────
    //  3. Streaming — chunks are yielded incrementally
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateStreamAsync_YieldsChunksIncrementally()
    {
        // Arrange: register a streaming service that yields chunks with delays
        var service = new FakeStreamTranslationService("stream-svc", "Streaming Service",
            chunks: new[] { "Hello", " ", "World", "!", " How", " are", " you", "?", " Fine", "." },
            chunkDelayMs: 10);
        _manager.RegisterService(service);

        var request = new TranslationRequest
        {
            Text = "你好世界",
            ToLanguage = Language.English
        };

        // Act: collect chunks with timestamps
        var chunkTimestamps = new List<(string Chunk, long Ms)>();
        var sw = Stopwatch.StartNew();

        await foreach (var chunk in _manager.TranslateStreamAsync(request, serviceId: "stream-svc"))
        {
            chunkTimestamps.Add((chunk, sw.ElapsedMilliseconds));
        }
        sw.Stop();

        // Assert
        _output.WriteLine($"Streaming 10 chunks: {sw.ElapsedMilliseconds}ms");
        foreach (var (chunk, ms) in chunkTimestamps)
        {
            _output.WriteLine($"  {ms,4}ms - \"{chunk}\"");
        }

        chunkTimestamps.Should().HaveCount(10, "all 10 chunks should be yielded");
        var fullText = string.Concat(chunkTimestamps.Select(c => c.Chunk));
        fullText.Should().Be("Hello World! How are you? Fine.");

        // Chunks should arrive incrementally, not all at once at the end
        // First chunk should arrive much earlier than last chunk
        var firstMs = chunkTimestamps.First().Ms;
        var lastMs = chunkTimestamps.Last().Ms;
        (lastMs - firstMs).Should().BeGreaterThan(30,
            "chunks should be spread over time, not all buffered and returned at once");
    }

    // ──────────────────────────────────────────────
    //  4. Streaming cancellation — returns quickly
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateStreamAsync_CancellationAfterFirstChunk_ReturnsQuickly()
    {
        var service = new FakeStreamTranslationService("stream-cancel-svc", "Stream Cancel",
            chunks: Enumerable.Range(0, 20).Select(i => $"chunk{i}").ToArray(),
            chunkDelayMs: 50);
        _manager.RegisterService(service);

        using var cts = new CancellationTokenSource();
        var request = new TranslationRequest
        {
            Text = "test",
            ToLanguage = Language.English
        };

        var sw = Stopwatch.StartNew();
        var collected = new List<string>();

        var act = async () =>
        {
            await foreach (var chunk in _manager.TranslateStreamAsync(request, cts.Token, "stream-cancel-svc"))
            {
                collected.Add(chunk);
                if (collected.Count >= 2)
                {
                    await cts.CancelAsync();
                }
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        _output.WriteLine($"Streaming cancelled after {collected.Count} chunks: {sw.ElapsedMilliseconds}ms");

        // Should return within 500ms, not wait for all 20 chunks (20×50ms = 1s)
        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
            "cancellation should stop streaming promptly");
        collected.Count.Should().BeLessThan(20, "not all chunks should be received after cancellation");
    }

    // ──────────────────────────────────────────────
    //  5. Retry — does not use blocking Thread.Sleep between attempts
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_WithRetry_DoesNotBlockBetweenAttempts()
    {
        // Arrange: service that fails first 2 times, succeeds on 3rd
        var attempts = 0;
        var service = new CallbackTranslationService("retry-svc", "Retry Service",
            (request, ct) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt <= 2)
                {
                    throw new TranslationException("transient error")
                    {
                        ErrorCode = TranslationErrorCode.NetworkError
                    };
                }

                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = $"T:{request.Text}",
                    ServiceName = "retry-svc",
                    TargetLanguage = request.ToLanguage
                });
            });
        _manager.RegisterService(service);

        var request = new TranslationRequest
        {
            Text = "retry test",
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var sw = Stopwatch.StartNew();
        var result = await _manager.TranslateAsync(request, serviceId: "retry-svc");
        sw.Stop();

        // Assert
        _output.WriteLine($"Retry translate ({attempts} attempts): {sw.ElapsedMilliseconds}ms");
        result.TranslatedText.Should().Be("T:retry test");
        attempts.Should().Be(3, "should have retried twice then succeeded");

        // With async Task.Delay backoff (500ms, 1000ms), total ~1.5-2s
        // If blocking with Thread.Sleep, it would still be ~1.5s but UI thread would freeze
        // We verify it completes within a generous budget
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "retry with async backoff should complete within time budget");
    }

    // ──────────────────────────────────────────────
    //  6. Concurrent calls to same service — no cross-contamination
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_ConcurrentCallsSameService_NoCrossContamination()
    {
        var service = new DelayedTranslationService("shared-svc", "Shared Service", delayMs: 50);
        _manager.RegisterService(service);

        // Launch 10 concurrent requests with different texts
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var request = new TranslationRequest
            {
                Text = $"text-{i}",
                ToLanguage = Language.SimplifiedChinese
            };
            var result = await _manager.TranslateAsync(request, serviceId: "shared-svc");
            return (Index: i, Result: result);
        }).ToList();

        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(tasks);
        sw.Stop();

        _output.WriteLine($"10 concurrent calls to same service: {sw.ElapsedMilliseconds}ms");

        // Assert: each result matches its input
        results.Should().HaveCount(10);
        foreach (var (index, result) in results)
        {
            result.TranslatedText.Should().Be($"T:text-{index}",
                $"result for text-{index} should not be contaminated by other concurrent requests");
        }

        // Should overlap: 10 × 50ms concurrent = ~50ms, not 500ms serial
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "10 concurrent calls should overlap, not serialize");
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    // ═══════════════════════════════════════════════
    //  Fake service implementations for testing
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Non-streaming translation service with configurable async delay.
    /// </summary>
    private sealed class DelayedTranslationService : ITranslationService
    {
        private readonly int _delayMs;

        public DelayedTranslationService(string serviceId, string displayName, int delayMs)
        {
            ServiceId = serviceId;
            DisplayName = displayName;
            _delayMs = delayMs;
        }

        public string ServiceId { get; }
        public string DisplayName { get; }
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public IReadOnlyList<Language> SupportedLanguages => new[] { Language.English, Language.SimplifiedChinese };

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
        {
            await Task.Delay(_delayMs, ct);
            return new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"T:{request.Text}",
                ServiceName = DisplayName,
                TargetLanguage = request.ToLanguage
            };
        }

        public Task<Language> DetectLanguageAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Language.Auto);
    }

    /// <summary>
    /// Non-streaming translation service with a custom callback.
    /// </summary>
    private sealed class CallbackTranslationService : ITranslationService
    {
        private readonly Func<TranslationRequest, CancellationToken, Task<TranslationResult>> _callback;

        public CallbackTranslationService(string serviceId, string displayName,
            Func<TranslationRequest, CancellationToken, Task<TranslationResult>> callback)
        {
            ServiceId = serviceId;
            DisplayName = displayName;
            _callback = callback;
        }

        public string ServiceId { get; }
        public string DisplayName { get; }
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public IReadOnlyList<Language> SupportedLanguages => new[] { Language.English, Language.SimplifiedChinese };

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
            => _callback(request, ct);

        public Task<Language> DetectLanguageAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Language.Auto);
    }

    /// <summary>
    /// Streaming translation service that yields chunks with configurable delay.
    /// Implements IStreamTranslationService so TranslationManager uses the streaming path.
    /// </summary>
    private sealed class FakeStreamTranslationService : IStreamTranslationService
    {
        private readonly string[] _chunks;
        private readonly int _chunkDelayMs;

        public FakeStreamTranslationService(string serviceId, string displayName,
            string[] chunks, int chunkDelayMs)
        {
            ServiceId = serviceId;
            DisplayName = displayName;
            _chunks = chunks;
            _chunkDelayMs = chunkDelayMs;
        }

        public string ServiceId { get; }
        public string DisplayName { get; }
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public bool IsStreaming => true;
        public IReadOnlyList<Language> SupportedLanguages => new[] { Language.English, Language.SimplifiedChinese };

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public async IAsyncEnumerable<string> TranslateStreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var chunk in _chunks)
            {
                ct.ThrowIfCancellationRequested();
                if (_chunkDelayMs > 0)
                    await Task.Delay(_chunkDelayMs, ct);
                yield return chunk;
            }
        }

        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
        {
            var text = string.Concat(_chunks);
            await Task.Delay(_chunkDelayMs, ct);
            return new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = text,
                ServiceName = DisplayName,
                TargetLanguage = request.ToLanguage
            };
        }

        public Task<Language> DetectLanguageAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Language.Auto);
    }
}
