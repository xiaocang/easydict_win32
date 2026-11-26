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

    [Fact]
    public void EmptyRangeArray_IsConfirmedEmpty()
    {
        UiaSelectionResult.FromRanges(Array.Empty<string>()).Should().Be(UiaSelectionResult.Empty);
    }

    [Fact]
    public void NullRangeArray_RemainsUnavailable()
    {
        UiaSelectionResult.FromRanges(null).Should().Be(UiaSelectionResult.Unavailable);
    }

    [Fact]
    public void MultipleEmptyRanges_AreConfirmedEmpty()
    {
        UiaSelectionResult.FromRanges(new[] { "", " ", "\n" }).IsConfirmedEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("later selection")]
    [InlineData(null)]
    public async Task EmptyFirstRange_WithSelectedOrUnreadableLaterRange_KeepsClipboardFallback(string? later)
    {
        var selection = UiaSelectionResult.FromRanges(new[] { "", later });
        var session = new SelectionCaptureSession(123, () => 123, CancellationToken.None);
        var clipboardCalls = 0;

        var result = await session.ResolveAsync(selection, () =>
        {
            clipboardCalls++;
            return Task.FromResult<string?>("full selection");
        });

        clipboardCalls.Should().Be(1);
        result.Should().Be("full selection");
    }

    [Fact]
    public void SelectedFirstRange_PreservesExistingExtractionWithoutReadingLaterRanges()
    {
        static IEnumerable<string?> Ranges()
        {
            yield return "first selection";
            throw new InvalidOperationException("Later ranges should not be read.");
        }

        UiaSelectionResult.FromRanges(Ranges()).Text.Should().Be("first selection");
    }

    [Fact]
    public void RangeReadFailure_IsNotReportedAsConfirmedEmpty()
    {
        static IEnumerable<string?> Ranges()
        {
            yield return "";
            throw new InvalidOperationException("Provider failed.");
        }

        Action read = () => UiaSelectionResult.FromRanges(Ranges());
        read.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("null", true)]
    [InlineData("ambiguous", false)]
    [InlineData("ambiguous", true)]
    [InlineData("error", false)]
    [InlineData("error", true)]
    public async Task EmptyAndUncertainProvider_KeepClipboardFallback(string uncertain, bool emptyFirst)
    {
        Func<UiaSelectionResult?> unknown = () => ReadProvider(uncertain);
        Func<UiaSelectionResult?> empty = () => UiaSelectionResult.Empty;
        var selection = UiaSelectionResult.ReadProviders(
            emptyFirst ? new[] { empty, unknown } : new[] { unknown, empty });
        var session = new SelectionCaptureSession(123, () => 123, CancellationToken.None);
        var clipboardCalls = 0;

        var result = await session.ResolveAsync(selection, () =>
        {
            clipboardCalls++;
            return Task.FromResult<string?>("recovered selection");
        });

        clipboardCalls.Should().Be(1);
        result.Should().Be("recovered selection");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyAndUnsupportedProvider_ConfirmEmpty(bool emptyFirst)
    {
        Func<UiaSelectionResult?> unsupported = () => null;
        Func<UiaSelectionResult?> empty = () => UiaSelectionResult.Empty;

        var result = UiaSelectionResult.ReadProviders(
            emptyFirst ? new[] { empty, unsupported } : new[] { unsupported, empty });

        result.Should().Be(UiaSelectionResult.Empty);
    }

    [Fact]
    public void BothUnsupportedProviders_RemainUnavailable()
    {
        UiaSelectionResult.ReadProviders(new Func<UiaSelectionResult?>[] { () => null, () => null })
            .Should().Be(UiaSelectionResult.Unavailable);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("ambiguous")]
    [InlineData("error")]
    [InlineData("unsupported")]
    [InlineData("empty")]
    public void LaterProviderSelection_RemainsAvailable(string first)
    {
        var result = UiaSelectionResult.ReadProviders(new Func<UiaSelectionResult?>[]
        {
            () => ReadProvider(first),
            () => UiaSelectionResult.FromText("selected text")
        });

        result.Text.Should().Be("selected text");
    }

    [Fact]
    public void FirstProviderSelection_DoesNotReadAnotherProvider()
    {
        var readAgain = false;
        var result = UiaSelectionResult.ReadProviders(new Func<UiaSelectionResult?>[]
        {
            () => UiaSelectionResult.FromText("selected text"),
            () => { readAgain = true; return UiaSelectionResult.Unavailable; }
        });

        result.Text.Should().Be("selected text");
        readAgain.Should().BeFalse();
    }

    [Fact]
    public void ProviderCancellation_IsNeverConvertedToEmptyOrUnavailable()
    {
        Action read = () => UiaSelectionResult.ReadProviders(new Func<UiaSelectionResult?>[]
        {
            () => UiaSelectionResult.Empty,
            () => throw new OperationCanceledException()
        });

        read.Should().Throw<OperationCanceledException>();
    }

    private static UiaSelectionResult? ReadProvider(string outcome)
        => outcome switch
        {
            "unsupported" => null,
            "empty" => UiaSelectionResult.Empty,
            "null" => UiaSelectionResult.FromRanges(null),
            "ambiguous" => UiaSelectionResult.FromRanges(new[] { "", "later range" }),
            "error" => throw new InvalidOperationException("Provider failed."),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
}
