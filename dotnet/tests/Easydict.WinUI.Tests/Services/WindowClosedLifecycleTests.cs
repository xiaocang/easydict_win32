using FluentAssertions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Regression tests for the window Closed event handler pattern used in
/// MiniWindowService and FixedWindowService (commit 5de0016).
///
/// The pattern under test:
///   _window = new SomeWindow();
///   _window.Closed += (_, _) => _window = null;
///
/// Since MiniWindow/FixedWindow are heavy XAML windows that can't be
/// instantiated in unit tests, we test the exact same close-nullify pattern
/// using plain WinUI Window objects. The Closed event is inherited from
/// Microsoft.UI.Xaml.Window, so the behavior is identical.
/// </summary>
[Trait("Category", "WinUI")]
public class WindowClosedLifecycleTests : IDisposable
{
    private readonly List<Window> _createdWindows = new();

    public void Dispose()
    {
        foreach (var window in _createdWindows)
        {
            try
            {
                window.Close();
            }
            catch
            {
                // Window may already be closed
            }
        }
    }

    /// <summary>
    /// Core regression test: closing a window fires the Closed event,
    /// which nullifies the field via the handler pattern used in
    /// MiniWindowService.cs:122 and FixedWindowService.cs:123.
    /// </summary>
    [Fact]
    public void Close_SetsFieldToNull()
    {
        // Arrange — mirrors: _miniWindow = new MiniWindow();
        Window? window = CreateTrackedWindow();
        var windowToClose = window;

        // Register the exact pattern from MiniWindowService/FixedWindowService
        window.Closed += (_, _) => window = null;

        // Act — mirrors: user closes window via Alt+F4 or close button
        windowToClose.Close();

        // Assert — the Closed handler should have set field to null
        window.Should().BeNull();
    }

    /// <summary>
    /// After close, null-conditional access returns default value.
    /// Mirrors: IsVisible => _miniWindow?.IsVisible ?? false
    /// </summary>
    [Fact]
    public void Close_NullCoalescingReturnsDefaultAfterClose()
    {
        // Arrange
        Window? window = CreateTrackedWindow();
        var windowToClose = window;
        window.Closed += (_, _) => window = null;

        // Act
        windowToClose.Close();

        // Assert — mirrors the IsVisible property pattern
        var content = window?.Content;
        content.Should().BeNull();

        var isVisible = window?.Content != null;
        isVisible.Should().BeFalse();
    }

    /// <summary>
    /// After close nullifies the field, a new window can be created.
    /// Mirrors the EnsureWindowCreated() lazy-creation pattern.
    /// </summary>
    [Fact]
    public void Close_NewWindowCanBeCreatedAfterClose()
    {
        // Arrange — first window
        Window? window = CreateTrackedWindow();
        var windowToClose = window;
        window.Closed += (_, _) => window = null;

        // Act — close first window
        windowToClose.Close();
        window.Should().BeNull();

        // Recreate — mirrors EnsureWindowCreated(): if (_window == null) { _window = new ...; }
        window = CreateTrackedWindow();
        window.Closed += (_, _) => window = null;

        // Assert — new window is alive
        window.Should().NotBeNull();
    }

    /// <summary>
    /// Multiple create-close-recreate cycles work correctly,
    /// ensuring the pattern is robust across repeated use.
    /// </summary>
    [Fact]
    public void Close_MultipleCloseRecreateCycles()
    {
        Window? window = null;

        for (var i = 0; i < 3; i++)
        {
            // EnsureWindowCreated pattern
            if (window == null)
            {
                window = CreateTrackedWindow();
                window.Closed += (_, _) => window = null;
            }

            window.Should().NotBeNull($"cycle {i}: window should exist after creation");

            // Close the window (keep a reference for calling Close)
            var windowToClose = window;
            windowToClose!.Close();

            window.Should().BeNull($"cycle {i}: window should be null after close");
        }
    }

    /// <summary>
    /// Full simulation of the service lifecycle pattern:
    /// EnsureWindowCreated() → check AppWindow.IsVisible → close → field is null → recreate.
    /// Uses WindowNative + Win32Interop to obtain AppWindow, mirroring the real services.
    /// </summary>
    [Fact]
    public void SimulatedServiceLifecycle_EnsureWindowCreatedAndIsVisible()
    {
        // Simulate service state
        Window? serviceWindow = null;

        // --- First use: EnsureWindowCreated ---
        serviceWindow = CreateTrackedWindow();
        serviceWindow.Closed += (_, _) => serviceWindow = null;

        // Verify AppWindow can be obtained (mirrors real service code)
        var hWnd = WindowNative.GetWindowHandle(serviceWindow);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Should().NotBeNull();

        // IsVisible check (window not shown, so IsVisible is false, but the field is not null)
        var isVisible = serviceWindow != null && appWindow.IsVisible;
        isVisible.Should().BeFalse("window exists but has not been shown/activated");
        serviceWindow.Should().NotBeNull("window field should still hold a reference");

        // --- Close (user action) ---
        var windowToClose = serviceWindow;
        windowToClose!.Close();

        serviceWindow.Should().BeNull("Closed handler should have nullified the field");

        // --- Second use: EnsureWindowCreated again ---
        serviceWindow = CreateTrackedWindow();
        serviceWindow.Closed += (_, _) => serviceWindow = null;
        serviceWindow.Should().NotBeNull("new window should be created after close");

        // Clean up the second window
        serviceWindow.Close();
        serviceWindow.Should().BeNull();
    }

    private Window CreateTrackedWindow()
    {
        var window = new Window();
        _createdWindows.Add(window);
        return window;
    }
}
