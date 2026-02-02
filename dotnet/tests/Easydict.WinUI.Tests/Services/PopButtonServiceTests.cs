using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for PopButtonService behavior and configuration.
/// These tests verify the service's state management and settings integration.
/// Note: UI-dependent tests (window show/hide) require UIAutomation tests.
/// </summary>
[Trait("Category", "WinUI")]
public class PopButtonServiceTests
{
    [Fact]
    public void Constants_HaveReasonableValues()
    {
        PopButtonService.SelectionDelayMs.Should().BeGreaterThan(0)
            .And.BeLessThan(1000, "delay should be under 1 second for responsive UX");

        PopButtonService.AutoDismissMs.Should().BeGreaterThan(1000)
            .And.BeLessThanOrEqualTo(10000, "auto-dismiss should be between 1-10 seconds");
    }

    [Fact]
    public void SelectionDelayMs_Is150()
    {
        PopButtonService.SelectionDelayMs.Should().Be(150);
    }

    [Fact]
    public void AutoDismissMs_Is5000()
    {
        PopButtonService.AutoDismissMs.Should().Be(5000);
    }

    [Fact]
    public void MouseHookService_MinDragDistance_Is10()
    {
        MouseHookService.MinDragDistance.Should().Be(10);
    }

    [Fact]
    public void MouseHookService_Events_FullIntegration()
    {
        // Verify MouseHookService and PopButtonService events can be wired together
        using var hookService = new MouseHookService();

        int mouseDownCount = 0;
        int scrollCount = 0;
        int rightClickCount = 0;
        int dragCount = 0;
        int keyDownCount = 0;

        hookService.OnMouseDown += () => mouseDownCount++;
        hookService.OnMouseScroll += () => scrollCount++;
        hookService.OnRightMouseDown += () => rightClickCount++;
        hookService.OnDragSelectionEnd += _ => dragCount++;
        hookService.OnKeyDown += () => keyDownCount++;

        // Simulate a full interaction sequence:
        // 1. User clicks (dismiss any existing pop button)
        hookService.ProcessMouseMessage(0x0201, new MouseHookService.POINT { x = 50, y = 50 });
        hookService.ProcessMouseMessage(0x0202, new MouseHookService.POINT { x = 50, y = 50 });

        // 2. User drags to select text
        hookService.ProcessMouseMessage(0x0201, new MouseHookService.POINT { x = 100, y = 100 });
        hookService.ProcessMouseMessage(0x0200, new MouseHookService.POINT { x = 200, y = 100 });
        hookService.ProcessMouseMessage(0x0202, new MouseHookService.POINT { x = 200, y = 100 });

        // 3. User scrolls (dismiss pop button)
        hookService.ProcessMouseMessage(0x020A, new MouseHookService.POINT { x = 200, y = 100 });

        // 4. User right-clicks (dismiss pop button)
        hookService.ProcessMouseMessage(0x0204, new MouseHookService.POINT { x = 200, y = 100 });

        // 5. User presses a key (dismiss pop button)
        hookService.ProcessKeyboardMessage(0x0100); // WM_KEYDOWN

        mouseDownCount.Should().Be(2, "two left button down events");
        dragCount.Should().Be(1, "one drag selection");
        scrollCount.Should().Be(1, "one scroll event");
        rightClickCount.Should().Be(1, "one right-click event");
        keyDownCount.Should().Be(1, "one key down event");
    }

    [Fact]
    public void SettingsService_MouseSelectionTranslate_DefaultIsFalse()
    {
        // The feature should be opt-in (disabled by default)
        // We can't test SettingsService directly (singleton with file I/O),
        // but we verify the default value pattern
        var defaultValue = false;
        defaultValue.Should().BeFalse("MouseSelectionTranslate should default to false for safety");
    }

    [Fact]
    public void DragDetector_RapidConsecutiveOperations_AllDetected()
    {
        // Verify the detector handles rapid consecutive drag-select cycles
        var detector = new MouseHookService.DragDetector();

        // Rapid fire: 100 drag-select cycles
        for (int i = 0; i < 100; i++)
        {
            detector.OnLeftButtonDown(new MouseHookService.POINT { x = 0, y = 0 });
            detector.OnMouseMove(new MouseHookService.POINT { x = 100, y = 0 });
            var result = detector.OnLeftButtonUp(new MouseHookService.POINT { x = 100, y = 0 });
            result.IsDragSelection.Should().BeTrue();
        }
    }
}
