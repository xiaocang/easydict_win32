using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class TextToSpeechLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunLatestAsync_CanceledWaiter_NeverEntersOrOverlapsOwner()
    {
        // Arrange
        var gate = new LatestSpeechRequestGate();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentOperations = 0;
        var maxConcurrentOperations = 0;
        var secondRan = false;
        var thirdRan = false;

        var first = gate.RunLatestAsync(async _ =>
        {
            var concurrent = Interlocked.Increment(ref concurrentOperations);
            InterlockedExtensions.Max(ref maxConcurrentOperations, concurrent);
            firstStarted.TrySetResult(true);
            await releaseFirst.Task;
            Interlocked.Decrement(ref concurrentOperations);
        }, CancellationToken.None);

        await firstStarted.Task.WaitAsync(TestTimeout);

        var second = gate.RunLatestAsync(_ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        var third = gate.RunLatestAsync(_ =>
        {
            var concurrent = Interlocked.Increment(ref concurrentOperations);
            InterlockedExtensions.Max(ref maxConcurrentOperations, concurrent);
            thirdRan = true;
            Interlocked.Decrement(ref concurrentOperations);
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Act
        Func<Task> waitForCanceledSecond = async () => await second.WaitAsync(TestTimeout);
        await waitForCanceledSecond.Should().ThrowAsync<OperationCanceledException>();
        releaseFirst.TrySetResult(true);
        await first.WaitAsync(TestTimeout);
        await third.WaitAsync(TestTimeout);

        // Assert
        secondRan.Should().BeFalse();
        thirdRan.Should().BeTrue();
        maxConcurrentOperations.Should().Be(1);
    }

    [Fact]
    public async Task CancelActive_CancelsCurrentOperationWithoutLeakingGate()
    {
        // Arrange
        var gate = new LatestSpeechRequestGate();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = gate.RunLatestAsync(async cancellationToken =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, CancellationToken.None);

        await started.Task.WaitAsync(TestTimeout);

        // Act
        gate.CancelActive();

        // Assert
        Func<Task> waitForCancellation = async () => await operation.WaitAsync(TestTimeout);
        await waitForCancellation.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Stop_CancelsPendingSpeechRequestBeforePlaybackIsPublished()
    {
        // Arrange
        var gate = new LatestSpeechRequestGate();
        using var service = new TextToSpeechService(gate);
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = gate.RunLatestAsync(async cancellationToken =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, CancellationToken.None);

        await requestStarted.Task.WaitAsync(TestTimeout);

        // Act
        service.Stop();

        // Assert
        Func<Task> waitForCancellation = async () => await request.WaitAsync(TestTimeout);
        await waitForCancellation.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SpeakAsync_WhenInitializationFails_DisposesWithoutPublishingPlayback()
    {
        // Arrange
        var synthesizer = new FakeSapiSpeechSynthesizer { ThrowOnSelectVoice = true };
        var controller = new SapiPlaybackController(() => synthesizer);

        // Act
        Func<Task> act = async () => await controller
            .SpeakAsync("missing", "hello", 0, CancellationToken.None)
            .WaitAsync(TestTimeout);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        controller.IsPlaying.Should().BeFalse();
        synthesizer.DisposeCount.Should().Be(1);
        synthesizer.CancelCount.Should().Be(0);
    }

    [Fact]
    public async Task Stop_CancelsActivePlayback_AndOwnerDisposesExactlyOnce()
    {
        // Arrange
        var synthesizer = new FakeSapiSpeechSynthesizer();
        var controller = new SapiPlaybackController(() => synthesizer);
        var playbackEndedCount = 0;
        controller.PlaybackEnded += () => Interlocked.Increment(ref playbackEndedCount);

        var playback = controller.SpeakAsync("voice", "hello", 0, CancellationToken.None);
        await synthesizer.Started.Task.WaitAsync(TestTimeout);
        controller.IsPlaying.Should().BeTrue();

        // Act
        controller.Stop();

        // Assert
        Func<Task> waitForStop = async () => await playback.WaitAsync(TestTimeout);
        await waitForStop.Should().ThrowAsync<OperationCanceledException>();
        controller.IsPlaying.Should().BeFalse();
        synthesizer.CancelCount.Should().Be(1);
        synthesizer.DisposeCount.Should().Be(1);
        playbackEndedCount.Should().Be(1);
    }

    [Fact]
    public async Task Stop_WhenCancellationThrows_CompletesWithErrorInsteadOfHanging()
    {
        // Arrange
        var synthesizer = new FakeSapiSpeechSynthesizer { ThrowOnCancel = true };
        var controller = new SapiPlaybackController(() => synthesizer);
        var playback = controller.SpeakAsync("voice", "hello", 0, CancellationToken.None);
        await synthesizer.Started.Task.WaitAsync(TestTimeout);

        // Act
        controller.Stop();

        // Assert
        Func<Task> waitForFailure = async () => await playback.WaitAsync(TestTimeout);
        await waitForFailure.Should().ThrowAsync<InvalidOperationException>();
        controller.IsPlaying.Should().BeFalse();
        synthesizer.DisposeCount.Should().Be(1);
    }

    private sealed class FakeSapiSpeechSynthesizer : ISapiSpeechSynthesizer
    {
        public event Action<SapiPlaybackCompletedEventArgs>? SpeakCompleted;

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ThrowOnSelectVoice { get; init; }

        public bool ThrowOnCancel { get; init; }

        public int CancelCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int Rate { private get; set; }

        public void SelectVoice(string voiceName)
        {
            if (ThrowOnSelectVoice)
            {
                throw new InvalidOperationException("Voice is unavailable.");
            }
        }

        public void SetOutputToDefaultAudioDevice()
        {
        }

        public void SpeakAsync(string text)
        {
            Started.TrySetResult(true);
        }

        public void CancelAll()
        {
            CancelCount++;
            if (ThrowOnCancel)
            {
                throw new InvalidOperationException("Cancellation failed.");
            }

            SpeakCompleted?.Invoke(new SapiPlaybackCompletedEventArgs(null, IsCanceled: true));
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int candidate)
        {
            var current = Volatile.Read(ref target);
            while (candidate > current)
            {
                var previous = Interlocked.CompareExchange(ref target, candidate, current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }
    }
}
