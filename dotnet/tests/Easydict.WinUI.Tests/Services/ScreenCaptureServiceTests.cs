using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class ScreenCaptureServiceTests
{
    [Fact]
    public void CancelCurrentCapture_DoesNotThrow_WhenIdle()
    {
        var service = new ScreenCaptureService();

        var single = Record.Exception(() => service.CancelCurrentCapture());
        var consequent = Record.Exception(() => service.CancelCurrentCapture());

        single.Should().BeNull();
        consequent.Should().BeNull();
    }

    [Fact]
    public async Task CaptureRegionAsync_ThrowsOnPreCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = new ScreenCaptureService();
        var exception = await Record.ExceptionAsync(
            async () => await service.CaptureRegionAsync(cts.Token));

        exception.Should()
            .BeAssignableTo<OperationCanceledException>(
                "WaitAsync must propagate cancellation before semaphore entry");
    }

    [Fact]
    public async Task CaptureRegionAsync_SemaphoreSurvivesCancellationCycle()
    {
        // Regression: Bug 2 (Silent OCR second invocation deadlock).
        // If the semaphore leaks after any cancellation, subsequent calls hang.
        // Run three capture→cancel cycles sequentially; each must complete.
        var service = new ScreenCaptureService();

        for (var i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource(5000);
            var task = service.CaptureRegionAsync(cts.Token);
            await Task.Yield(); // let the STA thread spin up
            service.CancelCurrentCapture();
            await task.WaitAsync(cts.Token);
        }
    }

    [Fact]
    public async Task CaptureRegionAsync_TokenCancellationReleasesSemaphore()
    {
        // Regression: Bug 2 — token cancellation, like CancelCurrentCapture,
        // must release the semaphore so a queued caller doesn't deadlock.
        using var ctsA = new CancellationTokenSource();
        var service = new ScreenCaptureService();

        var taskA = service.CaptureRegionAsync(ctsA.Token);
        await Task.Yield();
        ctsA.Cancel();

        try { await taskA; } catch (OperationCanceledException) { }

        using var ctsB = new CancellationTokenSource(5000);
        await service
            .CaptureRegionAsync(ctsB.Token)
            .WaitAsync(ctsB.Token);
    }

    [Fact]
    public async Task CancelCurrentCapture_TerminatesActiveCapture()
    {
        // Regression: Bug 3 (pop button fired during OCR capture).
        // CancelCurrentCapture must terminate the overlay via WM_USER_CANCEL
        // so that RunOcrPipelineAsync's CTS cancellation actually tears down
        // the capture and releases the semaphore.
        using var cts = new CancellationTokenSource(10000);
        var service = new ScreenCaptureService();

        var task = service.CaptureRegionAsync(cts.Token);
        await Task.Yield(); // let overlay initialize

        service.CancelCurrentCapture();

        // The task must complete (not hang) — result is null because we
        // cancelled, not because the overlay never opened.
        var result = await task.WaitAsync(cts.Token);
        result.Should().BeNull(
            "CancelCurrentCapture must terminate the overlay via WM_USER_CANCEL");
    }
}
