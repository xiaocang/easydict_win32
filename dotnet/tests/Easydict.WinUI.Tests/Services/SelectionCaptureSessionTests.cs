using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class SelectionCaptureSessionTests
{
    [Fact]
    public void ForegroundChange_InvalidatesCapturedSource()
    {
        nint foreground = 123;
        var session = new SelectionCaptureSession(foreground, () => foreground, CancellationToken.None);
        session.ThrowIfInvalid();
        foreground = 456;
        Action check = session.ThrowIfInvalid;
        check.Should().Throw<OperationCanceledException>();
        session.SourceWindow.Should().Be((nint)123);
    }

    [Fact]
    public void Cancellation_InvalidatesUnchangedSource()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new SelectionCaptureSession(123, () => 123, cancellation.Token);
        cancellation.Cancel();
        Action check = session.ThrowIfInvalid;
        check.Should().Throw<OperationCanceledException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrAfterCopy_AlwaysRunsCleanup(bool copied)
    {
        var clipboard = "original";
        var cleanupCalled = false;
        Func<Task> operation = () => SelectionCaptureSession.WithCleanupAsync<string?>(() =>
        {
            if (copied) clipboard = "selection";
            throw new OperationCanceledException();
        }, () =>
        {
            cleanupCalled = true;
            if (copied) clipboard = "original";
            return Task.CompletedTask;
        });
        await operation.Should().ThrowAsync<OperationCanceledException>();
        cleanupCalled.Should().BeTrue();
        clipboard.Should().Be("original");
    }

    [Fact]
    public async Task NextCapture_WaitsForCancelledCaptureCleanup()
    {
        var gate = new SelectionCaptureGate();
        var cleanup = new TaskCompletionSource();
        var secondStarted = false;
        var first = gate.RunAsync(() => SelectionCaptureSession.WithCleanupAsync<string?>(
            () => throw new OperationCanceledException(), () => cleanup.Task), CancellationToken.None);
        var second = gate.RunAsync(() =>
        {
            secondStarted = true;
            return Task.FromResult("second");
        }, CancellationToken.None);
        secondStarted.Should().BeFalse();
        cleanup.SetResult();
        Func<Task> observeFirst = () => first;
        await observeFirst.Should().ThrowAsync<OperationCanceledException>();
        (await second).Should().Be("second");
    }

    [Fact]
    public async Task CancelledQueuedCapture_DoesNotRunOrReleaseAnotherCapturesGate()
    {
        var gate = new SelectionCaptureGate();
        var blocker = new TaskCompletionSource<string>();
        var first = gate.RunAsync(() => blocker.Task, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var called = false;
        var queued = gate.RunAsync(() => { called = true; return Task.FromResult(0); }, cancellation.Token);
        cancellation.Cancel();
        Func<Task> observe = () => queued;
        await observe.Should().ThrowAsync<OperationCanceledException>();
        var third = gate.RunAsync(() => Task.FromResult(3), CancellationToken.None);
        third.IsCompleted.Should().BeFalse();
        called.Should().BeFalse();
        blocker.SetResult("done");
        await first;
        (await third).Should().Be(3);
    }

    [Fact]
    public async Task CancelledDispatcherAction_CannotSendCopyLater()
    {
        Action? callback = null;
        using var cancellation = new CancellationTokenSource();
        var sent = false;
        var task = SelectionCaptureSession.RunDispatchedAsync(
            action => { callback = action; return true; }, () => sent = true, cancellation.Token, 5000);
        cancellation.Cancel();
        Func<Task> observe = () => task;
        await observe.Should().ThrowAsync<OperationCanceledException>();
        callback!();
        sent.Should().BeFalse();
    }

    [Fact]
    public async Task TimedOutDispatcherAction_CannotWriteClipboardLater()
    {
        Action? callback = null;
        var written = false;
        var task = SelectionCaptureSession.RunDispatchedAsync(
            action => { callback = action; return true; }, () => written = true, CancellationToken.None, 1);
        Func<Task> observe = () => task;
        await observe.Should().ThrowAsync<TimeoutException>();
        callback!();
        written.Should().BeFalse();
    }

    [Fact]
    public async Task CancellationDuringDispatcherAction_WaitsUntilActionFinishes()
    {
        using var cancellation = new CancellationTokenSource();
        var finished = false;
        var task = SelectionCaptureSession.RunDispatchedAsync(action => { action(); return true; }, () =>
        {
            cancellation.Cancel();
            finished = true;
        }, cancellation.Token, 5000);
        await task;
        finished.Should().BeTrue();
        cancellation.IsCancellationRequested.Should().BeTrue();
    }
}
