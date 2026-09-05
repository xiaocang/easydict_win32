using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class HotkeyWindowShowCoordinatorTests
{
    [Theory]
    [InlineData("selected text")]
    [InlineData(null)]
    [InlineData("")]
    public async Task Show_PrecedesCapture_AndCompletionEndsPendingPhase(string? text)
    {
        var coordinator = new HotkeyWindowShowCoordinator();
        var capture = new TaskCompletionSource<string?>();
        var calls = new List<string>();
        var states = new List<bool>();
        coordinator.PendingChanged += states.Add;
        var operation = coordinator.RunAsync(
            () => calls.Add("show"),
            _ => { calls.Add("capture"); return capture.Task; },
            result =>
            {
                result.Should().Be(text);
                coordinator.IsPending.Should().BeFalse();
                calls.Add("complete");
            }, () => true);

        calls.Should().Equal("show", "capture");
        coordinator.IsPending.Should().BeTrue();
        operation.IsCompleted.Should().BeFalse();
        capture.SetResult(text);
        await operation;
        calls.Should().Equal("show", "capture", "complete");
        states.Should().Equal(true, false);
    }

    [Fact]
    public async Task Invalidate_CancelsCapture_AndDiscardsEvenUncooperativeCompletion()
    {
        var coordinator = new HotkeyWindowShowCoordinator();
        var capture = new TaskCompletionSource<string?>();
        CancellationToken token = default;
        var completions = 0;
        var operation = coordinator.RunAsync(() => { }, ct =>
        {
            token = ct;
            return capture.Task;
        }, _ => completions++, () => true);

        coordinator.Invalidate(); // Hide, close, manual interaction and shutdown share this boundary.
        coordinator.Invalidate();
        coordinator.IsPending.Should().BeFalse();
        token.IsCancellationRequested.Should().BeTrue();
        capture.SetResult("stale selection");
        await operation;
        completions.Should().Be(0);
    }

    [Fact]
    public async Task NewRequest_OldFinallyCannotClearNewPendingState()
    {
        var coordinator = new HotkeyWindowShowCoordinator();
        var first = new TaskCompletionSource<string?>();
        var second = new TaskCompletionSource<string?>();
        var results = new List<string?>();
        var oldOperation = coordinator.RunAsync(() => { }, _ => first.Task, results.Add, () => true);
        var newOperation = coordinator.RunAsync(() => { }, _ => second.Task, results.Add, () => true);
        first.SetResult("old");
        await oldOperation;
        coordinator.IsPending.Should().BeTrue();
        second.SetResult("new");
        await newOperation;
        results.Should().Equal("new");
    }

    [Fact]
    public async Task SourceChanged_DoesNotActivateOrApplyText()
    {
        var coordinator = new HotkeyWindowShowCoordinator();
        var completed = false;
        await coordinator.RunAsync(() => { }, _ => Task.FromResult<string?>("selection"),
            _ => completed = true, () => false);
        completed.Should().BeFalse();
        coordinator.IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task RecoverableFailure_CompletesWithoutReplacementText()
    {
        var coordinator = new HotkeyWindowShowCoordinator();
        var results = new List<string?>();
        await coordinator.RunAsync(() => { }, _ => throw new InvalidOperationException(), results.Add, () => true);
        results.Should().Equal(new string?[] { null });
        coordinator.IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task SourceCancellation_DoesNotActivate()
    {
        var coordinator = new HotkeyWindowShowCoordinator();
        var completed = false;
        await coordinator.RunAsync(() => { }, _ => throw new OperationCanceledException(),
            _ => completed = true, () => true);
        completed.Should().BeFalse();
        coordinator.IsPending.Should().BeFalse();
    }
}
