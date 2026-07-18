using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class ScreenCaptureServiceTests
{
    [Fact]
    public void CancelCurrentCapture_DoesNotThrow_WhenNoCaptureInProgress()
    {
        var service = new ScreenCaptureService();
        var exception = Record.Exception(() => service.CancelCurrentCapture());
        exception.Should().BeNull();
    }

    [Fact]
    public async Task CaptureRegionAsync_CancelledToken_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = new ScreenCaptureService();
        // Cancelled token causes WaitAsync before the STA thread even spawns
        var exception = await Record.ExceptionAsync(
            async () => await service.CaptureRegionAsync(cts.Token));

        exception.Should().NotBeNull()
            .And.Subject.Should()
            .BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task CaptureRegionAsync_PreCancelledMidFlow_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        var service = new ScreenCaptureService();

        // CaptureRegionAsync has a minimum execution time since it spawns an STA thread.
        // Cancel immediately — this validates the SemaphoreSlim + CancellationToken
        // integration doesn't hang or deadlock.
        var task = service.CaptureRegionAsync(cts.Token);
        cts.Cancel();

        try { await task; } catch (OperationCanceledException) { }

        // After cancellation, the semaphore should be released so a second call succeeds.
        var exception = await Record.ExceptionAsync(
            async () =>
            {
                using var cts2 = new CancellationTokenSource(5000);
                await service.CaptureRegionAsync(cts2.Token);
            });

        exception.Should().BeNull("semaphore should be released after token cancellation");
    }

    [Fact]
    public void CancelCurrentCapture_NoopAfterCompletion()
    {
        var service = new ScreenCaptureService();
        // Cancel called twice should not throw
        service.CancelCurrentCapture();
        var exception = Record.Exception(() => service.CancelCurrentCapture());
        exception.Should().BeNull();
    }
}
