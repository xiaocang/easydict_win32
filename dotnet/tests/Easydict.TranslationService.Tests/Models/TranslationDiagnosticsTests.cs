using System.Runtime.CompilerServices;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Models;

public class TranslationDiagnosticsTests
{
    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(long milliseconds) => _milliseconds += milliseconds;
    }

    [Fact]
    public void NestedStagesAreMeasuredIndependentlyAndCompletionFreezesTime()
    {
        var clock = new Clock();
        var diagnostics = new TranslationDiagnostics(clock);
        using (diagnostics.Measure(TranslationDiagnosticStage.FirstOutput))
        {
            clock.Advance(10);
            using (diagnostics.Measure(TranslationDiagnosticStage.ResponseHeaders)) clock.Advance(30);
            clock.Advance(20);
        }
        diagnostics.Complete(TranslationDiagnosticStatus.Completed);
        clock.Advance(500);
        var snapshot = diagnostics.Snapshot();
        snapshot.TotalMs.Should().Be(60);
        snapshot.Status.Should().Be(TranslationDiagnosticStatus.Completed);
        snapshot.Timings.Select(row => row.DurationMs).Should().Equal(60, 30);
        snapshot.Timings[1].StartedMs.Should().Be(10);
        snapshot.Timings.Should().OnlyContain(row => !row.IsRunning);
    }

    [Fact]
    public void ForkCopiesPreparationWithoutSharingProviderTimings()
    {
        var clock = new Clock();
        var query = new TranslationDiagnostics(clock);
        using (query.Measure(TranslationDiagnosticStage.QueryPreparation)) clock.Advance(50);
        var first = query.Fork();
        var second = query.Fork();
        using (first.Measure(TranslationDiagnosticStage.Streaming)) clock.Advance(10);
        second.Snapshot().Timings.Should().ContainSingle();
        second.Snapshot().Timings[0].DurationMs.Should().Be(50);
        query.Snapshot().Timings.Should().ContainSingle();
        first.Snapshot().Timings.Should().HaveCount(2);
    }

    [Fact]
    public async Task StreamIgnoresEmptyUpdatesAndSeparatesFirstOutputFromGeneration()
    {
        var clock = new Clock();
        var diagnostics = new TranslationDiagnostics(clock);
        async IAsyncEnumerable<TranslationStreamUpdate> Stream()
        {
            await Task.CompletedTask;
            clock.Advance(10);
            yield return new TranslationStreamUpdate.TextDelta("");
            clock.Advance(20);
            yield return new TranslationStreamUpdate.TextDelta("first");
            clock.Advance(40);
            yield return new TranslationStreamUpdate.TextSnapshot("complete");
        }
        var updates = new List<TranslationStreamUpdate>();
        await foreach (var update in diagnostics.ObserveStreamAsync(Stream())) updates.Add(update);
        updates.Should().HaveCount(3);
        var snapshot = diagnostics.Snapshot();
        snapshot.Timings.Select(row => row.DurationMs).Should().Equal(30, 40);
        snapshot.Timings[1].Stage.Should().Be(TranslationDiagnosticStage.Streaming);
    }

    [Fact]
    public async Task FailedStreamKeepsWaitTimeAndDisposesMeasurementsWithoutChangingException()
    {
        var clock = new Clock();
        var diagnostics = new TranslationDiagnostics(clock);
        var expected = new TranslationException("fixture") { ErrorCode = TranslationErrorCode.Timeout };
        async IAsyncEnumerable<TranslationStreamUpdate> Stream()
        {
            await Task.CompletedTask;
            clock.Advance(80);
            yield return new TranslationStreamUpdate.TextDelta("");
            throw expected;
        }
        var action = async () => { await foreach (var _ in diagnostics.ObserveStreamAsync(Stream())) { } };
        var error = await action.Should().ThrowAsync<TranslationException>();
        error.Which.Should().BeSameAs(expected);
        diagnostics.Snapshot().Status.Should().Be(TranslationDiagnosticStatus.Failed);
        clock.Advance(50);
        diagnostics.Snapshot().TotalMs.Should().Be(80);
        diagnostics.Snapshot().Timings.Should().ContainSingle(row => row.DurationMs == 80 && !row.IsRunning);
    }

    [Fact]
    public async Task CancellingAStreamPreservesCompletedPhases()
    {
        var clock = new Clock();
        var diagnostics = new TranslationDiagnostics(clock);
        using var cts = new CancellationTokenSource();
        async IAsyncEnumerable<TranslationStreamUpdate> Stream([EnumeratorCancellation] CancellationToken ct = default)
        {
            clock.Advance(20);
            yield return new TranslationStreamUpdate.TextDelta("first");
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in diagnostics.ObserveStreamAsync(Stream(), cts.Token)) { }
        });
        diagnostics.Complete(TranslationDiagnosticStatus.Cancelled);
        diagnostics.Snapshot().Status.Should().Be(TranslationDiagnosticStatus.Cancelled);
        diagnostics.Snapshot().Timings.Should().HaveCount(2).And.OnlyContain(row => !row.IsRunning);
    }

    [Fact]
    public void ResultLifecycleFreezesDiagnosticsOnFailureAndClearsItForNextQuery()
    {
        var clock = new Clock();
        var result = new ServiceQueryResult { Diagnostics = new(clock) };
        using var waiting = result.Diagnostics.Measure(TranslationDiagnosticStage.FirstOutput);
        clock.Advance(100);
        result.Error = new TranslationException("fixture") { ErrorCode = TranslationErrorCode.Timeout };
        clock.Advance(200);
        result.Diagnostics.Snapshot().TotalMs.Should().Be(100);
        result.Diagnostics.Snapshot().Status.Should().Be(TranslationDiagnosticStatus.Failed);
        result.Reset();
        result.Diagnostics.Should().BeNull();
    }

    [Fact]
    public void RequestCopiesKeepRoutingOptionsAndIndependentDiagnostics()
    {
        var request = new TranslationRequest
        {
            Text = "fixture", FromLanguage = Language.Auto, ToLanguage = Language.Auto,
            AutomaticDirection = new(Language.English, Language.Japanese), BypassCache = true,
            CustomPrompt = "custom", OriginalText = "raw", TimeoutMs = 1234
        };
        var diagnostics = new TranslationDiagnostics();
        var copy = request.WithDiagnostics(diagnostics);
        copy.Should().BeEquivalentTo(request, options => options.Excluding(item => item.Diagnostics));
        copy.Diagnostics.Should().BeSameAs(diagnostics);
        request.Diagnostics.Should().BeNull();
    }

    [Fact]
    public void ResetFreezesThePreviousInFlightQueryBeforeDetachingIt()
    {
        var clock = new Clock();
        var previous = new TranslationDiagnostics(clock);
        var result = new ServiceQueryResult { Diagnostics = previous };
        using var waiting = previous.Measure(TranslationDiagnosticStage.FirstOutput);
        clock.Advance(40);
        result.Reset();
        clock.Advance(80);
        previous.Snapshot().Status.Should().Be(TranslationDiagnosticStatus.Cancelled);
        previous.Snapshot().TotalMs.Should().Be(40);
        result.Diagnostics.Should().BeNull();
    }

    [Fact]
    public void ReplacingDiagnosticsFreezesOldQueryWithoutCancellingTheNewRecord()
    {
        var clock = new Clock();
        var old = new TranslationDiagnostics(clock);
        var result = new ServiceQueryResult { Diagnostics = old };
        clock.Advance(35);
        var current = new TranslationDiagnostics(clock);
        result.Diagnostics = current;
        result.ClearQueried(); // A previous request's cancellation callback can arrive late.
        old.Snapshot().Status.Should().Be(TranslationDiagnosticStatus.Cancelled);
        old.Snapshot().TotalMs.Should().Be(35);
        current.Snapshot().Status.Should().Be(TranslationDiagnosticStatus.Running);
    }

    [Fact]
    public void PostCompletionMeasurementKeepsReportingLiveElapsedTime()
    {
        var clock = new Clock();
        var diagnostics = new TranslationDiagnostics(clock);
        clock.Advance(20);
        diagnostics.Complete(TranslationDiagnosticStatus.Completed);
        using var enrichment = diagnostics.MeasureAfterCompletion(TranslationDiagnosticStage.PhoneticEnrichment);
        clock.Advance(30);

        var pending = diagnostics.Snapshot();
        pending.TotalMs.Should().Be(20, "the frozen total must not grow with best-effort work");
        var running = pending.Timings.Should().ContainSingle().Subject;
        running.DurationMs.Should().Be(30, "an open post-completion phase reports real elapsed time");
        running.IsRunning.Should().BeTrue();

        enrichment.Dispose();
        clock.Advance(10);
        var finished = diagnostics.Snapshot().Timings.Single();
        finished.DurationMs.Should().Be(30);
        finished.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void PostCompletionPhaseStartingAfterCompletionIsNotNegativeOrFrozen()
    {
        var clock = new Clock();
        var diagnostics = new TranslationDiagnostics(clock);
        clock.Advance(50);
        diagnostics.Complete(TranslationDiagnosticStatus.Completed);
        clock.Advance(60);
        using var enrichment = diagnostics.MeasureAfterCompletion(TranslationDiagnosticStage.PhoneticEnrichment);

        var pending = diagnostics.Snapshot().Timings.Should().ContainSingle().Subject;
        pending.StartedMs.Should().Be(110);
        pending.DurationMs.Should().Be(0);
        pending.IsRunning.Should().BeTrue();

        clock.Advance(15);
        diagnostics.Snapshot().Timings.Single().DurationMs.Should().Be(15);
    }

    [Fact]
    public void ForkKeepsPostCompletionMeasurementsLive()
    {
        var clock = new Clock();
        var query = new TranslationDiagnostics(clock);
        clock.Advance(10);
        query.Complete(TranslationDiagnosticStatus.Completed);
        using var enrichment = query.MeasureAfterCompletion(TranslationDiagnosticStage.PhoneticEnrichment);
        var copy = query.Fork();
        clock.Advance(25);

        var timing = copy.Snapshot().Timings.Should().ContainSingle().Subject;
        timing.Stage.Should().Be(TranslationDiagnosticStage.PhoneticEnrichment);
        timing.DurationMs.Should().Be(25);
        timing.IsRunning.Should().BeTrue();
    }
}
