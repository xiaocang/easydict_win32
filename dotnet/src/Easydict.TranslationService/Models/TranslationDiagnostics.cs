using System.Runtime.CompilerServices;

namespace Easydict.TranslationService.Models;

public enum TranslationDiagnosticStage
{
    QueryPreparation, LanguageRouting, PromptPreparation, RequestPreparation, ResponseHeaders, FirstOutput, Streaming, PostProcessing
}

public enum TranslationDiagnosticStatus { Running, Completed, Failed, Cancelled }

public sealed record TranslationDiagnosticTiming(TranslationDiagnosticStage Stage, double StartedMs, double DurationMs, bool IsRunning);
public sealed record TranslationDiagnosticSnapshot(double TotalMs, TranslationDiagnosticStatus Status, IReadOnlyList<TranslationDiagnosticTiming> Timings);

/// <summary>Per-query timing only: never stores prompts, credentials, response text, or endpoint URLs.</summary>
public sealed class TranslationDiagnostics
{
    private sealed record Phase(TranslationDiagnosticStage Stage, long Started, long? Ended = null);
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly List<Phase> _phases = [];
    private long? _ended;
    private TranslationDiagnosticStatus _status;

    public TranslationDiagnostics(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _started = _time.GetTimestamp();
    }

    private TranslationDiagnostics(TimeProvider time, long started, IEnumerable<Phase> phases)
    {
        _time = time;
        _started = started;
        _phases.AddRange(phases);
    }

    public TranslationDiagnostics Fork()
    {
        lock (_lock)
        {
            var now = _ended ?? _time.GetTimestamp();
            return new(_time, _started, _phases.Select(phase => phase with { Ended = phase.Ended ?? now }));
        }
    }

    public IDisposable Measure(TranslationDiagnosticStage stage)
    {
        lock (_lock)
        {
            var index = _phases.Count;
            _phases.Add(new(stage, _ended ?? _time.GetTimestamp()));
            return new Measurement(this, index);
        }
    }

    public void Complete(TranslationDiagnosticStatus status)
    {
        lock (_lock)
        {
            if (_ended is not null) return;
            _ended = _time.GetTimestamp();
            _status = status;
        }
    }

    public TranslationDiagnosticSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = _ended ?? _time.GetTimestamp();
            return new(_time.GetElapsedTime(_started, now).TotalMilliseconds, _status,
                _phases.Select(phase => new TranslationDiagnosticTiming(phase.Stage,
                    _time.GetElapsedTime(_started, phase.Started).TotalMilliseconds,
                    _time.GetElapsedTime(phase.Started, phase.Ended ?? now).TotalMilliseconds,
                    phase.Ended is null && _ended is null)).ToArray());
        }
    }

    public async IAsyncEnumerable<TranslationStreamUpdate> ObserveStreamAsync(
        IAsyncEnumerable<TranslationStreamUpdate> stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IDisposable? waiting = Measure(TranslationDiagnosticStage.FirstOutput);
        IDisposable? streaming = null;
        var fullyRead = false;
        try
        {
            await foreach (var update in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var hasText = update switch
                {
                    TranslationStreamUpdate.TextDelta delta => !string.IsNullOrEmpty(delta.Text),
                    TranslationStreamUpdate.TextSnapshot snapshot => !string.IsNullOrEmpty(snapshot.Text),
                    TranslationStreamUpdate.Completed done => !string.IsNullOrEmpty(done.Result.TranslatedText),
                    _ => false
                };
                if (hasText && waiting is not null)
                {
                    waiting.Dispose();
                    waiting = null;
                    streaming = Measure(TranslationDiagnosticStage.Streaming);
                }
                yield return update;
            }
            fullyRead = true;
        }
        finally
        {
            waiting?.Dispose();
            streaming?.Dispose();
            if (!fullyRead)
                Complete(cancellationToken.IsCancellationRequested ? TranslationDiagnosticStatus.Cancelled : TranslationDiagnosticStatus.Failed);
        }
    }

    public async IAsyncEnumerable<string> ObserveTextAsync(
        IAsyncEnumerable<string> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in ObserveStreamAsync(ToUpdatesAsync(stream, cancellationToken), cancellationToken).ConfigureAwait(false))
            yield return ((TranslationStreamUpdate.TextDelta)update).Text;
    }

    private static async IAsyncEnumerable<TranslationStreamUpdate> ToUpdatesAsync(
        IAsyncEnumerable<string> stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var text in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return new TranslationStreamUpdate.TextDelta(text);
    }

    private void EndPhase(int index)
    {
        lock (_lock)
        {
            if (_phases[index].Ended is null)
                _phases[index] = _phases[index] with { Ended = _ended ?? _time.GetTimestamp() };
        }
    }

    private sealed class Measurement(TranslationDiagnostics owner, int index) : IDisposable
    {
        public void Dispose() => owner.EndPhase(index);
    }
}
