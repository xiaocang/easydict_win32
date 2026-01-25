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

        // Should complete without hanging
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
}
