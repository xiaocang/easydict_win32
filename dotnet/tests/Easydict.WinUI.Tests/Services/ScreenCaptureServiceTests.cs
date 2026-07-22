using Easydict.WinUI.Services;
using Easydict.WinUI.Services.ScreenCapture;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
[Collection("ScreenCapture")]
public class ScreenCaptureServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

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
        ScreenCaptureService.IsCaptureInProgress.Should().BeFalse();
    }

    [Fact]
    public async Task CaptureRegionAsync_PreWindowCancellation_ClosesBeforeReturning()
    {
        using var gate = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        var probe = new ScreenCaptureLifecycleProbe { BeforeCaptureGate = gate };
        var service = new ScreenCaptureService(() => new ScreenCaptureWindow(probe));
        var captureTask = service.CaptureRegionAsync(cts.Token);

        try
        {
            await probe.ThreadStarted.Task.WaitAsync(TestTimeout);
            ScreenCaptureService.IsCaptureInProgress.Should().BeTrue();

            cts.Cancel();
            gate.Set();

            var result = await captureTask.WaitAsync(TestTimeout);
            result.Should().BeNull();
            probe.Closed.Task.IsCompleted.Should().BeTrue(
                "capture completion must follow STA cleanup");
            ScreenCaptureService.IsCaptureInProgress.Should().BeFalse();
        }
        finally
        {
            cts.Cancel();
            gate.Set();
            await captureTask.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task CancelCurrentCapture_TerminatesReadyWindow()
    {
        var probe = new ScreenCaptureLifecycleProbe();
        var service = new ScreenCaptureService(() => new ScreenCaptureWindow(probe));
        var captureTask = service.CaptureRegionAsync();

        try
        {
            await probe.Ready.Task.WaitAsync(TestTimeout);
            ScreenCaptureService.IsCaptureInProgress.Should().BeTrue();

            service.CancelCurrentCapture();

            var result = await captureTask.WaitAsync(TestTimeout);
            result.Should().BeNull(
                "CancelCurrentCapture posts WM_USER_CANCEL to the ready capture window");
            await probe.Closed.Task.WaitAsync(TestTimeout);
            ScreenCaptureService.IsCaptureInProgress.Should().BeFalse();
        }
        finally
        {
            service.CancelCurrentCapture();
            await captureTask.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task CaptureRegionAsync_TokenCancellationReleasesSemaphore()
    {
        var probes = new Queue<ScreenCaptureLifecycleProbe>([
            new ScreenCaptureLifecycleProbe(),
            new ScreenCaptureLifecycleProbe()
        ]);
        var service = new ScreenCaptureService(
            () => new ScreenCaptureWindow(probes.Dequeue()));

        foreach (var probe in probes.ToArray())
        {
            using var cts = new CancellationTokenSource();
            var captureTask = service.CaptureRegionAsync(cts.Token);

            try
            {
                await probe.Ready.Task.WaitAsync(TestTimeout);
                ScreenCaptureService.IsCaptureInProgress.Should().BeTrue();

                cts.Cancel();

                var result = await captureTask.WaitAsync(TestTimeout);
                result.Should().BeNull();
                await probe.Closed.Task.WaitAsync(TestTimeout);
                ScreenCaptureService.IsCaptureInProgress.Should().BeFalse();
            }
            finally
            {
                cts.Cancel();
                service.CancelCurrentCapture();
                await captureTask.WaitAsync(TestTimeout);
            }
        }
    }
}

[CollectionDefinition("ScreenCapture", DisableParallelization = true)]
public sealed class ScreenCaptureTestCollection
{
}
