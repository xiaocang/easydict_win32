namespace Easydict.WinUI.Services;

internal sealed class LatestSpeechRequestGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private CancellationTokenSource? _activeRequestCts;

    internal async Task RunLatestAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCts = Interlocked.Exchange(ref _activeRequestCts, currentCts);
        try
        {
            previousCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous request owner completed between the exchange and cancellation.
        }

        var lockTaken = false;
        try
        {
            await _semaphore.WaitAsync(currentCts.Token);
            lockTaken = true;
            await operation(currentCts.Token);
        }
        finally
        {
            if (lockTaken)
            {
                _semaphore.Release();
            }

            Interlocked.CompareExchange(ref _activeRequestCts, null, currentCts);
        }
    }

    internal void CancelActive()
    {
        var activeCts = Volatile.Read(ref _activeRequestCts);
        try
        {
            activeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request owner completed concurrently.
        }
    }
}

internal sealed class LatestPlaybackTaskObserver
{
    private int _generation;

    internal void Invalidate()
    {
        Interlocked.Increment(ref _generation);
    }

    internal bool IsCurrent(int generation)
    {
        return Volatile.Read(ref _generation) == generation;
    }

    internal Task ObserveAsync(
        Task playbackTask,
        Action<int, Exception?> onCompleted)
    {
        ArgumentNullException.ThrowIfNull(playbackTask);
        ArgumentNullException.ThrowIfNull(onCompleted);

        var generation = Interlocked.Increment(ref _generation);
        return ObserveCoreAsync(playbackTask, generation, onCompleted);
    }

    private async Task ObserveCoreAsync(
        Task playbackTask,
        int generation,
        Action<int, Exception?> onCompleted)
    {
        Exception? error = null;
        try
        {
            await playbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected playback completion path.
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (IsCurrent(generation))
        {
            onCompleted(generation, error);
        }
    }
}

internal readonly record struct SapiPlaybackCompletedEventArgs(Exception? Error, bool IsCanceled);

internal interface ISapiSpeechSynthesizer : IDisposable
{
    event Action<SapiPlaybackCompletedEventArgs>? SpeakCompleted;

    int Rate { set; }

    void SelectVoice(string voiceName);

    void SetOutputToDefaultAudioDevice();

    void SpeakAsync(string text);

    void CancelAll();
}

internal sealed class SystemSapiSpeechSynthesizer : ISapiSpeechSynthesizer
{
    private readonly System.Speech.Synthesis.SpeechSynthesizer _inner = new();

    internal SystemSapiSpeechSynthesizer()
    {
        _inner.SpeakCompleted += OnSpeakCompleted;
    }

    public event Action<SapiPlaybackCompletedEventArgs>? SpeakCompleted;

    public int Rate
    {
        set => _inner.Rate = value;
    }

    public void SelectVoice(string voiceName) => _inner.SelectVoice(voiceName);

    public void SetOutputToDefaultAudioDevice() => _inner.SetOutputToDefaultAudioDevice();

    public void SpeakAsync(string text) => _inner.SpeakAsync(text);

    public void CancelAll() => _inner.SpeakAsyncCancelAll();

    private void OnSpeakCompleted(
        object? sender,
        System.Speech.Synthesis.SpeakCompletedEventArgs args)
    {
        SpeakCompleted?.Invoke(new SapiPlaybackCompletedEventArgs(args.Error, args.Cancelled));
    }

    public void Dispose()
    {
        _inner.SpeakCompleted -= OnSpeakCompleted;
        _inner.Dispose();
    }
}

internal sealed class SapiPlaybackController
{
    private sealed record ActivePlayback(
        ISapiSpeechSynthesizer Synthesizer,
        TaskCompletionSource<bool> Completion);

    private readonly object _lock = new();
    private readonly Func<ISapiSpeechSynthesizer> _synthesizerFactory;
    private ActivePlayback? _activePlayback;

    internal SapiPlaybackController(Func<ISapiSpeechSynthesizer> synthesizerFactory)
    {
        _synthesizerFactory = synthesizerFactory;
    }

    internal event Action? PlaybackEnded;

    internal bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _activePlayback != null;
            }
        }
    }

    internal async Task SpeakAsync(
        string voiceName,
        string text,
        int rate,
        CancellationToken cancellationToken)
    {
        using var synthesizer = _synthesizerFactory();
        synthesizer.SelectVoice(voiceName);
        synthesizer.SetOutputToDefaultAudioDevice();
        synthesizer.Rate = rate;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnSpeakCompleted(SapiPlaybackCompletedEventArgs args)
        {
            if (args.Error != null)
            {
                completion.TrySetException(args.Error);
            }
            else if (args.IsCanceled)
            {
                completion.TrySetCanceled();
            }
            else
            {
                completion.TrySetResult(true);
            }
        }

        synthesizer.SpeakCompleted += OnSpeakCompleted;
        CancellationTokenRegistration cancellationRegistration = default;
        var playbackStarted = false;

        try
        {
            cancellationRegistration = cancellationToken.Register(() =>
                CancelPlayback(synthesizer, completion, cancellationToken));

            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _activePlayback = new ActivePlayback(synthesizer, completion);
                synthesizer.SpeakAsync(text);
                playbackStarted = true;
            }

            await completion.Task;
        }
        finally
        {
            cancellationRegistration.Dispose();
            synthesizer.SpeakCompleted -= OnSpeakCompleted;

            lock (_lock)
            {
                if (ReferenceEquals(_activePlayback?.Synthesizer, synthesizer))
                {
                    _activePlayback = null;
                }
            }

            if (playbackStarted)
            {
                PlaybackEnded?.Invoke();
            }
        }
    }

    internal void Stop()
    {
        ActivePlayback? activePlayback;
        lock (_lock)
        {
            activePlayback = _activePlayback;
            if (activePlayback == null)
            {
                return;
            }

            try
            {
                activePlayback.Synthesizer.CancelAll();
            }
            catch (ObjectDisposedException ex)
            {
                activePlayback.Completion.TrySetException(ex);
            }
            catch (InvalidOperationException ex)
            {
                activePlayback.Completion.TrySetException(ex);
            }
        }
    }

    private void CancelPlayback(
        ISapiSpeechSynthesizer synthesizer,
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_activePlayback?.Synthesizer, synthesizer))
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                synthesizer.CancelAll();
            }
            catch (ObjectDisposedException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
        }
    }
}
