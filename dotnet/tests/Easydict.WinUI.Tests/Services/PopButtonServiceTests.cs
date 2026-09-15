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
    public void MultiClickSelectionDelayMs_IsShorterThanDragDelay()
    {
        // The multi-click path already waited out the settle window in MouseHookService,
        // so re-paying the drag delay only kept the pop button from appearing.
        PopButtonService.MultiClickSelectionDelayMs.Should().BeGreaterThan(0)
            .And.BeLessThan(PopButtonService.SelectionDelayMs);
    }

    [Fact]
    public void AutoDismissMs_Is5000()
    {
        PopButtonService.AutoDismissMs.Should().Be(5000);
    }

    // --- Configurable pop delay (issue #216) ---

    [Fact]
    public void GetSelectionDelayMs_ForADrag_KeepsTheDragDelay()
    {
        // Nothing has been waited out yet on this path, so the drag delay applies in full.
        PopButtonService.GetSelectionDelayMs(
            SettingsService.DefaultMouseSelectionPopDelayMs, isMultiClick: false)
            .Should().Be(PopButtonService.SelectionDelayMs);
        PopButtonService.GetSelectionDelayMs(
            SettingsService.MaxMouseSelectionPopDelayMs, isMultiClick: false)
            .Should().Be(PopButtonService.SelectionDelayMs, "the pop delay is a bound, not a target");
    }

    [Theory]
    [InlineData(SettingsService.DefaultMouseSelectionPopDelayMs)]
    [InlineData(120)]
    [InlineData(SettingsService.MinMouseSelectionPopDelayMs)]
    public void GetSelectionDelayMs_WhenTheSettleWaitSpentTheBudget_AddsNothing(int popDelayMs)
    {
        // MouseHookService caps the settle wait at the pop delay, so by the time a multi-click
        // arrives the budget is normally gone: adding the gesture delay on top would push the
        // total past the bound this setting documents.
        PopButtonService.GetSelectionDelayMs(popDelayMs, isMultiClick: true, settleWaitMs: popDelayMs)
            .Should().Be(0);
    }

    [Fact]
    public void GetSelectionDelayMs_WithBudgetLeft_SpendsOnlyWhatRemains()
    {
        // A short system double-click time settles sooner than the cap, leaving budget over.
        PopButtonService.GetSelectionDelayMs(220, isMultiClick: true, settleWaitMs: 200)
            .Should().Be(20);
        // Never more than the gesture's own delay, however much budget is left.
        PopButtonService.GetSelectionDelayMs(600, isMultiClick: true, settleWaitMs: 150)
            .Should().Be(PopButtonService.MultiClickSelectionDelayMs);
    }

    [Fact]
    public void GetSelectionDelayMs_ForADrag_AtMinimum_StillLeavesTheSourceAppAMoment()
    {
        SettingsService.MinMouseSelectionPopDelayMs.Should().Be(10,
            "reading the selection with no wait at all races the source app");

        PopButtonService.GetSelectionDelayMs(
            SettingsService.MinMouseSelectionPopDelayMs, isMultiClick: false)
            .Should().Be(SettingsService.MinMouseSelectionPopDelayMs);
        // A settings file from an older build (or a hand-edit) cannot drive it below the floor.
        PopButtonService.GetSelectionDelayMs(0, isMultiClick: false)
            .Should().Be(SettingsService.MinMouseSelectionPopDelayMs);
    }

    [Fact]
    public void GetSelectionDelayMs_BelowGestureDelay_LowersTheWait()
    {
        PopButtonService.GetSelectionDelayMs(60, isMultiClick: false).Should().Be(60);
    }

    [Theory]
    [InlineData(SettingsService.MinMouseSelectionPopDelayMs)]
    [InlineData(SettingsService.DefaultMouseSelectionPopDelayMs)]
    [InlineData(SettingsService.MaxMouseSelectionPopDelayMs)]
    public void GetSelectionDelayMs_NeverExceedsTheConfiguredBudget(int popDelayMs)
    {
        // What the two paths add up to is what the setting promises: the whole pre-query wait.
        PopButtonService.GetSelectionDelayMs(popDelayMs, isMultiClick: false)
            .Should().BeLessThanOrEqualTo(popDelayMs);

        foreach (var settleWaitMs in new[] { 0, 10, 150, popDelayMs })
        {
            (settleWaitMs + PopButtonService.GetSelectionDelayMs(
                popDelayMs, isMultiClick: true, settleWaitMs))
                .Should().BeLessThanOrEqualTo(Math.Max(popDelayMs, settleWaitMs));
        }
    }

    [Fact]
    public void MouseSelectionPopDelayMs_DefaultsToCurrentBehaviorAndClamps()
    {
        SettingsService.DefaultMouseSelectionPopDelayMs
            .Should().Be(MouseHookService.MaxMultiClickWaitMs, "the default must not change today's timing");

        var settings = SettingsService.Instance;
        var original = settings.MouseSelectionPopDelayMs;
        try
        {
            settings.MouseSelectionPopDelayMs = 0;
            settings.MouseSelectionPopDelayMs.Should().Be(SettingsService.MinMouseSelectionPopDelayMs);

            settings.MouseSelectionPopDelayMs = 99999;
            settings.MouseSelectionPopDelayMs.Should().Be(SettingsService.MaxMouseSelectionPopDelayMs);

            settings.MouseSelectionPopDelayMs = 120;
            settings.MouseSelectionPopDelayMs.Should().Be(120);
        }
        finally
        {
            settings.MouseSelectionPopDelayMs = original;
        }
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
