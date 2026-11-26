using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class SelectionCaptureFallbackTests
{
    [Fact]
    public async Task ConfirmedEmptyUia_CompletesPendingWindowWithoutWaitingForClipboard()
    {
        var session = new SelectionCaptureSession(123, () => 123, CancellationToken.None);
        var coordinator = new HotkeyWindowShowCoordinator();
        var clipboard = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboardCalls = 0;
        var completions = 0;
        var operation = coordinator.RunAsync(
            () => { },
            _ => session.ResolveAsync(UiaSelectionResult.Empty, () =>
            {
                clipboardCalls++;
                return clipboard.Task;
            }),
            text =>
            {
                text.Should().BeNull();
                completions++;
            },
            () => true);

        try
        {
            // No wall-clock threshold: a known empty UIA selection must complete
            // without entering the clipboard path or waiting for its timeout.
            operation.IsCompletedSuccessfully.Should().BeTrue();
            coordinator.IsPending.Should().BeFalse();
            clipboardCalls.Should().Be(0);
            completions.Should().Be(1);
        }
        finally
        {
            clipboard.TrySetResult(null);
            await operation;
        }
    }

    [Fact]
    public async Task UnavailableUia_StillWaitsForClipboardFallback()
    {
        var session = new SelectionCaptureSession(123, () => 123, CancellationToken.None);
        var clipboard = new TaskCompletionSource<string?>();
        var clipboardCalls = 0;

        var capture = session.ResolveAsync(UiaSelectionResult.Unavailable, () =>
        {
            clipboardCalls++;
            return clipboard.Task;
        });

        capture.IsCompleted.Should().BeFalse();
        clipboardCalls.Should().Be(1);
        clipboard.SetResult("selected text");
        (await capture).Should().Be("selected text");
    }

    [Fact]
    public async Task SelectedUiaText_DoesNotReadOrWriteClipboard()
    {
        var session = new SelectionCaptureSession(123, () => 123, CancellationToken.None);
        var result = await session.ResolveAsync(UiaSelectionResult.FromText("selected text"),
            () => throw new InvalidOperationException("Clipboard must not be touched."));

        result.Should().Be("selected text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidatedSource_NeverStartsClipboardFallback(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        nint foreground = 123;
        var session = new SelectionCaptureSession(foreground, () => foreground, cancellation.Token);
        if (cancel) cancellation.Cancel();
        else foreground = 456;
        var clipboardCalls = 0;

        Func<Task> capture = () => session.ResolveAsync(UiaSelectionResult.Unavailable, () =>
        {
            clipboardCalls++;
            return Task.FromResult<string?>("stale selection");
        });

        await capture.Should().ThrowAsync<OperationCanceledException>();
        clipboardCalls.Should().Be(0);
    }

    [Fact]
    public async Task SourceChangesDuringFallback_DiscardsClipboardResult()
    {
        nint foreground = 123;
        var session = new SelectionCaptureSession(foreground, () => foreground, CancellationToken.None);
        var clipboard = new TaskCompletionSource<string?>();
        var capture = session.ResolveAsync(UiaSelectionResult.Unavailable, () => clipboard.Task);

        foreground = 456;
        clipboard.SetResult("stale selection");

        Func<Task> observe = () => capture;
        await observe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n\t")]
    public void SuccessfulUiaRead_WithoutTranslatableText_IsConfirmedEmpty(string text)
    {
        UiaSelectionResult.FromText(text).IsConfirmedEmpty.Should().BeTrue();
    }

    [Fact]
    public void NullUiaRead_RemainsUnavailable()
    {
        UiaSelectionResult.FromText(null).Should().Be(UiaSelectionResult.Unavailable);
        UiaSelectionResult.Unavailable.IsConfirmedEmpty.Should().BeFalse();
    }
}
