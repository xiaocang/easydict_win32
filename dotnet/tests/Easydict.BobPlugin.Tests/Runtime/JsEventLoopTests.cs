using Easydict.BobPlugin.Runtime;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Runtime;

public class JsEventLoopTests
{
    [Fact]
    public async Task Post_RunsEverythingOnOneThread()
    {
        using var loop = new JsEventLoop("test-loop");
        var threadIds = new List<int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < 50; i++)
        {
            loop.Post(() => threadIds.Add(Environment.CurrentManagedThreadId));
        }

        loop.Post(() => done.SetResult());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        threadIds.Should().HaveCount(50);
        threadIds.Distinct().Should().HaveCount(1, "the engine must only ever be touched from one thread");
    }

    [Fact]
    public async Task InvokeAsync_ReturnsTheResultFromTheLoopThread()
    {
        using var loop = new JsEventLoop("test-loop");

        var loopThreadId = await loop.InvokeAsync(() => Environment.CurrentManagedThreadId);

        loopThreadId.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task InvokeAsync_PropagatesFailures()
    {
        using var loop = new JsEventLoop("test-loop");

        var act = async () => await loop.InvokeAsync<int>(() => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task InvokeAsync_RunsInlineWhenAlreadyOnTheLoopThread()
    {
        using var loop = new JsEventLoop("test-loop");

        var nested = await loop.InvokeAsync(() =>
        {
            loop.IsOnLoopThread.Should().BeTrue();
            // Would deadlock if this re-queued instead of running inline.
            return loop.InvokeAsync(() => 42).GetAwaiter().GetResult();
        });

        nested.Should().Be(42);
    }

    [Fact]
    public async Task AFailingCallbackDoesNotStopTheLoop()
    {
        using var loop = new JsEventLoop("test-loop");
        loop.Post(() => throw new InvalidOperationException("boom"));

        var stillAlive = await loop.InvokeAsync(() => "alive");

        stillAlive.Should().Be("alive");
    }

    [Fact]
    public void Post_AfterDisposeIsIgnored()
    {
        var loop = new JsEventLoop("test-loop");
        loop.Dispose();

        var act = () => loop.Post(() => { });

        act.Should().NotThrow();
    }

    [Fact]
    public void DisposeTwice_IsSafe()
    {
        var loop = new JsEventLoop("test-loop");
        loop.Dispose();

        var act = loop.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_DoesNotBlockOnAThreadStuckInACallback()
    {
        var loop = new JsEventLoop("test-loop");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        loop.Post(() =>
        {
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(30));
        });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        loop.Dispose();
        stopwatch.Stop();

        // The join is bounded, so a wedged plugin cannot hold up shutdown.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        release.Set();
    }
}
