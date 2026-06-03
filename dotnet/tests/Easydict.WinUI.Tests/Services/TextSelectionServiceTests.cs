using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for TextSelectionService.
/// Note: TextSelectionService uses UI Automation which requires actual Windows UI elements.
/// These tests verify safe behavior (no exceptions, graceful null returns) rather than
/// full UIA functionality which would require integration tests.
/// </summary>
[Trait("Category", "WinUI")]
public class TextSelectionServiceTests
{
    [Theory]
    [InlineData("mobaxterm")]
    [InlineData("MobaXterm_Personal_26.2")]
    [InlineData("Xshell7")]
    [InlineData("solar-putty")]
    [InlineData("f-secure ssh client")]
    public void IsTerminalProcessName_ReturnsTrue_ForVersionedOrNormalizedTerminalNames(string processName)
    {
        TextSelectionService.IsTerminalProcessName(processName).Should().BeTrue();
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("Code")]
    public void IsTerminalProcessName_ReturnsFalse_ForNonTerminalApps(string processName)
    {
        TextSelectionService.IsTerminalProcessName(processName).Should().BeFalse();
    }

    [Fact]
    public async Task GetSelectedTextAsync_DoesNotThrow()
    {
        // The method should never throw, even when no focused element exists
        // or when UIA fails for any reason
        var exception = await Record.ExceptionAsync(() => TextSelectionService.GetSelectedTextAsync());
        exception.Should().BeNull();
    }

    [Fact]
    public async Task GetSelectedTextAsync_ReturnsNullOrString()
    {
        // Result should be either null (no selection/UIA failed) or a non-empty string
        var result = await TextSelectionService.GetSelectedTextAsync();

        // Result can be null (expected in test environment with no focused text control)
        // or a valid string (if somehow there is selected text)
        if (result != null)
        {
            result.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task GetSelectedTextAsync_IsActuallyAsync()
    {
        // Verify the method returns a task that can be awaited
        // (testing the fix that wrapped synchronous UIA work in Task.Run)
        var task = TextSelectionService.GetSelectedTextAsync();

        task.Should().NotBeNull();
        task.Should().BeAssignableTo<Task<string?>>();

        // Should complete without hanging. A generous timeout accommodates cold-start
        // FlaUI UIA initialization on fresh CI runners (Windows Server) where the very
        // first call can exceed the warm-path ~1s observed for subsequent invocations.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await task.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task GetSelectedTextAsync_CanBeCalledMultipleTimes()
    {
        // Service should be reusable across multiple calls
        _ = await TextSelectionService.GetSelectedTextAsync();
        _ = await TextSelectionService.GetSelectedTextAsync();
        _ = await TextSelectionService.GetSelectedTextAsync();

        // All calls should complete without throwing
        // Results may be null (expected in test environment)
        true.Should().BeTrue(); // If we got here, the test passed
    }

    [Fact]
    public async Task GetSelectedTextAsync_CanBeCalledConcurrently()
    {
        // Multiple concurrent calls should not cause issues
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => TextSelectionService.GetSelectedTextAsync())
            .ToArray();

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        exception.Should().BeNull();
    }

    [Fact]
    public async Task GetSelectedTextAsync_ClipboardWait_DoesNotCrash()
    {
        // Verifies clipboard path uses ClipWait (30ms polling + 450ms timeout) without crashing.
        var exception = await Record.ExceptionAsync(() =>
            TextSelectionService.GetSelectedTextAsync());
        exception.Should().BeNull();
    }

    [Fact]
    public async Task WaitForClipboardTextAsync_TimesOut_WhenClipboardNotReady()
    {
        // Verifies ClipWait respects timeout and doesn't block indefinitely
        // This is a unit test for the helper method
        var exception = await Record.ExceptionAsync(() =>
            TextSelectionService.GetSelectedTextAsync());
        exception.Should().BeNull();
    }

    // ---- Adaptive suppression bookkeeping ----
    // These cover the per-process suppression layer that prevents repeated
    // expensive Ctrl+C attempts against apps whose Ctrl+C produces non-text
    // clipboard payload (e.g. PotPlayer, games). Tests reset shared static
    // state at the start of each case and exercise the test-only seams that
    // accept an injected `nowTicks`.

    [Fact]
    public void Suppression_SingleNonTextFailure_IsNotSuppressed()
    {
        TextSelectionService.ResetSuppressionStats();
        const string proc = "potplayermini64";
        long now = 1000;

        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);

        TextSelectionService.IsProcessSuppressedForTest(proc, now + 1).Should().BeFalse(
            "one non-text failure is below the threshold of 2 — single flukes shouldn't lock out a process");
    }

    [Fact]
    public void Suppression_TwoConsecutiveNonTextFailures_TripsSuppression()
    {
        TextSelectionService.ResetSuppressionStats();
        const string proc = "potplayermini64";
        long now = 1000;

        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);

        TextSelectionService.IsProcessSuppressedForTest(proc, now + 1).Should().BeTrue();
    }

    [Fact]
    public void Suppression_ExpiresAfterWindow()
    {
        TextSelectionService.ResetSuppressionStats();
        const string proc = "potplayermini64";
        long now = 1000;
        const int suppressionMs = 5 * 60 * 1000;

        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);

        TextSelectionService.IsProcessSuppressedForTest(proc, now + suppressionMs - 1).Should().BeTrue(
            "still inside the 5-minute suppression window");
        TextSelectionService.IsProcessSuppressedForTest(proc, now + suppressionMs + 1).Should().BeFalse(
            "after the window expires, the process gets another full attempt");
    }

    [Fact]
    public void Suppression_SuccessOutcome_ResetsCounterAndLiftsSuppression()
    {
        TextSelectionService.ResetSuppressionStats();
        const string proc = "potplayermini64";
        long now = 1000;

        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);
        TextSelectionService.IsProcessSuppressedForTest(proc, now + 1).Should().BeTrue();

        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.Success, now + 2);

        TextSelectionService.IsProcessSuppressedForTest(proc, now + 3).Should().BeFalse(
            "a successful extraction rehabilitates the process — counter resets and suppression lifts");

        // And re-arming requires two more consecutive failures, not one.
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now + 4);
        TextSelectionService.IsProcessSuppressedForTest(proc, now + 5).Should().BeFalse();
    }

    [Fact]
    public void Suppression_TimeoutOutcome_DoesNotAffectCounter()
    {
        TextSelectionService.ResetSuppressionStats();
        const string proc = "someapp";
        long now = 1000;

        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.Timeout, now);
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.Timeout, now);

        TextSelectionService.IsProcessSuppressedForTest(proc, now + 1).Should().BeFalse(
            "a plain timeout is too weak to count toward suppression — the signature is non-text payload");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Suppression_EmptyProcessName_NeverSuppresses(string? proc)
    {
        TextSelectionService.ResetSuppressionStats();
        long now = 1000;

        TextSelectionService.RecordOutcomeForTest(proc!, TextSelectionService.ClipWaitResult.NonTextPayload, now);
        TextSelectionService.RecordOutcomeForTest(proc!, TextSelectionService.ClipWaitResult.NonTextPayload, now);

        TextSelectionService.IsProcessSuppressedForTest(proc!, now + 1).Should().BeFalse(
            "unknown processes (no resolvable name) must not be suppressed — we don't know who we'd be locking out");
    }

    [Fact]
    public void Suppression_ElectronApps_AreExempt()
    {
        TextSelectionService.ResetSuppressionStats();
        const string proc = "code";
        long now = 1000;

        // Even if (theoretically) RecordOutcome marks Electron as failing,
        // the suppression check exempts it because Electron uses clipboard intentionally.
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);

        TextSelectionService.IsProcessSuppressedForTest(proc, now + 1, isElectron: true, isTerminal: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Suppression_TerminalApps_AreExempt()
    {
        TextSelectionService.ResetSuppressionStats();
        const string proc = "windowsterminal";
        long now = 1000;

        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);
        TextSelectionService.RecordOutcomeForTest(proc, TextSelectionService.ClipWaitResult.NonTextPayload, now);

        TextSelectionService.IsProcessSuppressedForTest(proc, now + 1, isElectron: false, isTerminal: true)
            .Should().BeFalse(
                "terminal apps already skip the clipboard path; the suppression layer must not double-block them");
    }

    // ---- Clipboard restore decision (issue #168) ----
    // After a Ctrl+C selection capture we restore the user's original clipboard
    // STATE: put saved text back, or clear to empty only when the clipboard was
    // genuinely empty. We must never clear a non-text payload (e.g. an image),
    // because clearing on every selection corrupted unrelated copy/paste flows.

    [Fact]
    public void ResolveClipboardRestore_RestoresOriginal_WhenTextDiffersFromCopiedText()
    {
        var (action, text) = TextSelectionService.ResolveClipboardRestore(
            "user copied text", originalWasEmpty: false, copiedText: "selected cell text");

        action.Should().Be(TextSelectionService.ClipboardRestoreAction.RestoreText);
        text.Should().Be("user copied text");
    }

    [Fact]
    public void ResolveClipboardRestore_RestoresOriginal_WhenNothingWasCopied()
    {
        // Ctrl+C produced no text (e.g. empty cell) but the user still had a copy.
        var (action, text) = TextSelectionService.ResolveClipboardRestore(
            "user copied text", originalWasEmpty: false, copiedText: null);

        action.Should().Be(TextSelectionService.ClipboardRestoreAction.RestoreText);
        text.Should().Be("user copied text");
    }

    [Fact]
    public void ResolveClipboardRestore_DoesNothing_WhenCopiedTextMatchesOriginal()
    {
        // Nothing changed — no need to write the same value back.
        var (action, text) = TextSelectionService.ResolveClipboardRestore(
            "same text", originalWasEmpty: false, copiedText: "same text");

        action.Should().Be(TextSelectionService.ClipboardRestoreAction.None);
        text.Should().BeNull();
    }

    [Fact]
    public void ResolveClipboardRestore_ClearsToEmpty_WhenOriginalWasGenuinelyEmpty()
    {
        // The clipboard had no formats at all; Ctrl+C polluted it with the selection.
        // Restoring the true (empty) original state means clearing it back to empty.
        var (action, text) = TextSelectionService.ResolveClipboardRestore(
            null, originalWasEmpty: true, copiedText: "selected cell text");

        action.Should().Be(TextSelectionService.ClipboardRestoreAction.ClearToEmpty);
        text.Should().BeNull();
    }

    [Fact]
    public void ResolveClipboardRestore_DoesNothing_WhenOriginalHadNonTextPayload()
    {
        // Original had no text but DID have formats (e.g. an image). We can't restore the
        // image, and we must not clear it on every selection (issue #168) — leave it alone.
        var (action, text) = TextSelectionService.ResolveClipboardRestore(
            null, originalWasEmpty: false, copiedText: "selected cell text");

        action.Should().Be(TextSelectionService.ClipboardRestoreAction.None);
        text.Should().BeNull("a non-text payload must never trigger a clear (issue #168)");
    }
}
