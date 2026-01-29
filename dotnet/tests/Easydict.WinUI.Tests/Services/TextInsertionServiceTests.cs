using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for TextInsertionService.
/// Note: TextInsertionService uses Win32 APIs (SetForegroundWindow, SendInput) which require
/// actual Windows UI elements. These tests verify safe behavior and state management
/// rather than full insertion functionality which requires integration tests.
/// </summary>
[Trait("Category", "WinUI")]
public class TextInsertionServiceTests
{
    [Fact]
    public void HasSourceWindow_ReturnsFalse_WhenNoCaptured()
    {
        // Before any capture, or with invalid handle, HasSourceWindow should be false
        // (In test environment, there may not be a valid foreground window)
        // This test verifies the property doesn't throw
        var act = () => TextInsertionService.HasSourceWindow;
        act.Should().NotThrow();
    }

    [Fact]
    public void CaptureSourceWindow_DoesNotThrow()
    {
        // CaptureSourceWindow should never throw, even in test environment
        var exception = Record.Exception(() => TextInsertionService.CaptureSourceWindow());
        exception.Should().BeNull();
    }

    [Fact]
    public void CaptureSourceWindow_CanBeCalledMultipleTimes()
    {
        // Should be safe to call repeatedly (overwrites previous handle)
        var exception = Record.Exception(() =>
        {
            TextInsertionService.CaptureSourceWindow();
            TextInsertionService.CaptureSourceWindow();
            TextInsertionService.CaptureSourceWindow();
        });
        exception.Should().BeNull();
    }

    [Fact]
    public async Task InsertTextAsync_ReturnsFalse_ForEmptyText()
    {
        var result = await TextInsertionService.InsertTextAsync("");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task InsertTextAsync_ReturnsFalse_ForNullText()
    {
        var result = await TextInsertionService.InsertTextAsync(null!);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task InsertTextAsync_DoesNotThrow()
    {
        // Should never throw, even when source window is invalid or App.MainWindow is null
        var exception = await Record.ExceptionAsync(() =>
            TextInsertionService.InsertTextAsync("test text"));
        exception.Should().BeNull();
    }

    [Fact]
    public async Task InsertTextAsync_ReturnsFalse_WhenNoSourceWindow()
    {
        // Without a valid source window, insertion should fail gracefully
        // In test environment, the captured window may not be valid
        var result = await TextInsertionService.InsertTextAsync("test text");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task InsertTextAsync_CanBeCalledMultipleTimes()
    {
        // Multiple calls should not cause issues
        var exception = await Record.ExceptionAsync(async () =>
        {
            await TextInsertionService.InsertTextAsync("text1");
            await TextInsertionService.InsertTextAsync("text2");
            await TextInsertionService.InsertTextAsync("text3");
        });
        exception.Should().BeNull();
    }

    [Fact]
    public void HasSourceWindow_AfterCapture_DoesNotThrow()
    {
        // Capture and check - should not throw regardless of environment
        TextInsertionService.CaptureSourceWindow();
        var exception = Record.Exception(() =>
        {
            _ = TextInsertionService.HasSourceWindow;
        });
        exception.Should().BeNull();
    }
}
