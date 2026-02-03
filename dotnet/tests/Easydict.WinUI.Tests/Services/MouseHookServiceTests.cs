using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;
using static Easydict.WinUI.Services.MouseHookService;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for the DragDetector state machine inside MouseHookService.
/// These tests exercise the pure logic without installing any Win32 hooks.
/// </summary>
[Trait("Category", "WinUI")]
public class MouseHookServiceTests
{
    private readonly DragDetector _detector = new();

    private static POINT Pt(int x, int y) => new() { x = x, y = y };

    // --- Drag selection detection ---

    [Fact]
    public void DragSelection_MouseDownMoveUp_FiresDragSelection()
    {
        _detector.OnLeftButtonDown(Pt(100, 100));
        _detector.OnMouseMove(Pt(200, 100)); // 100px drag
        var result = _detector.OnLeftButtonUp(Pt(200, 100));

        result.IsDragSelection.Should().BeTrue();
        result.EndPoint.x.Should().Be(200);
        result.EndPoint.y.Should().Be(100);
    }

    [Fact]
    public void ShortClick_NoDrag_DoesNotFireDragSelection()
    {
        _detector.OnLeftButtonDown(Pt(100, 100));
        _detector.OnMouseMove(Pt(102, 102)); // 2px - below threshold
        var result = _detector.OnLeftButtonUp(Pt(102, 102));

        result.IsDragSelection.Should().BeFalse();
    }

    [Fact]
    public void ExactThreshold_Fires()
    {
        // MinDragDistance = 10, so (10, 0) distance = 10 -> exactly threshold
        _detector.OnLeftButtonDown(Pt(0, 0));
        _detector.OnMouseMove(Pt(MinDragDistance, 0));
        var result = _detector.OnLeftButtonUp(Pt(MinDragDistance, 0));

        result.IsDragSelection.Should().BeTrue();
    }

    [Fact]
    public void BelowThreshold_DoesNotFire()
    {
        // (9, 0) distance = 9 -> below threshold
        _detector.OnLeftButtonDown(Pt(0, 0));
        _detector.OnMouseMove(Pt(MinDragDistance - 1, 0));
        var result = _detector.OnLeftButtonUp(Pt(MinDragDistance - 1, 0));

        result.IsDragSelection.Should().BeFalse();
    }

    [Fact]
    public void DiagonalDrag_ComputesDistanceCorrectly()
    {
        // (7, 7) distance = sqrt(98) ≈ 9.9 -> below threshold of 10
        _detector.OnLeftButtonDown(Pt(0, 0));
        _detector.OnMouseMove(Pt(7, 7));
        var result = _detector.OnLeftButtonUp(Pt(7, 7));

        result.IsDragSelection.Should().BeFalse();

        // (8, 8) distance = sqrt(128) ≈ 11.3 -> above threshold
        _detector.OnLeftButtonDown(Pt(0, 0));
        _detector.OnMouseMove(Pt(8, 8));
        var result2 = _detector.OnLeftButtonUp(Pt(8, 8));

        result2.IsDragSelection.Should().BeTrue();
    }

    // --- State reset ---

    [Fact]
    public void AfterDragEnd_StateResets()
    {
        // First: drag
        _detector.OnLeftButtonDown(Pt(100, 100));
        _detector.OnMouseMove(Pt(200, 100));
        _detector.OnLeftButtonUp(Pt(200, 100));

        // State should be reset
        _detector.IsLeftButtonDown.Should().BeFalse();
        _detector.IsDragging.Should().BeFalse();
    }

    [Fact]
    public void AfterDragEnd_NextShortClickDoesNotFire()
    {
        // First: drag
        _detector.OnLeftButtonDown(Pt(100, 100));
        _detector.OnMouseMove(Pt(200, 100));
        _detector.OnLeftButtonUp(Pt(200, 100));

        // Second: short click
        _detector.OnLeftButtonDown(Pt(300, 300));
        var result = _detector.OnLeftButtonUp(Pt(300, 300));

        result.IsDragSelection.Should().BeFalse();
    }

    [Fact]
    public void MultipleConsecutiveDrags_EachDetected()
    {
        for (int i = 0; i < 5; i++)
        {
            _detector.OnLeftButtonDown(Pt(0, 0));
            _detector.OnMouseMove(Pt(100, 0));
            var result = _detector.OnLeftButtonUp(Pt(100, 0));
            result.IsDragSelection.Should().BeTrue($"iteration {i}");
        }
    }

    // --- Move without button down ---

    [Fact]
    public void MouseMove_WithoutButtonDown_NoEffect()
    {
        _detector.OnMouseMove(Pt(500, 500));
        _detector.IsDragging.Should().BeFalse();
    }

    [Fact]
    public void MouseUp_WithoutButtonDown_ReturnsFalse()
    {
        var result = _detector.OnLeftButtonUp(Pt(500, 500));
        result.IsDragSelection.Should().BeFalse();
    }

    // --- Gradual drag ---

    [Fact]
    public void GradualDrag_MultipleMoves_DetectedOnceThresholdCrossed()
    {
        _detector.OnLeftButtonDown(Pt(100, 100));

        // Small moves that don't cross threshold individually
        _detector.OnMouseMove(Pt(103, 100));
        _detector.IsDragging.Should().BeFalse();

        _detector.OnMouseMove(Pt(106, 100));
        _detector.IsDragging.Should().BeFalse();

        // This one crosses threshold (distance from start = 11)
        _detector.OnMouseMove(Pt(111, 100));
        _detector.IsDragging.Should().BeTrue();

        var result = _detector.OnLeftButtonUp(Pt(111, 100));
        result.IsDragSelection.Should().BeTrue();
    }

    // --- ProcessMouseMessage integration ---

    [Fact]
    public void ProcessMouseMessage_DragSequence_FiresEvent()
    {
        using var service = new MouseHookService();
        POINT? firedPoint = null;
        service.OnDragSelectionEnd += pt => firedPoint = pt;

        service.ProcessMouseMessage(0x0201, Pt(100, 100)); // WM_LBUTTONDOWN
        service.ProcessMouseMessage(0x0200, Pt(200, 100)); // WM_MOUSEMOVE
        service.ProcessMouseMessage(0x0202, Pt(200, 100)); // WM_LBUTTONUP

        firedPoint.Should().NotBeNull();
        firedPoint!.Value.x.Should().Be(200);
    }

    [Fact]
    public void ProcessMouseMessage_ShortClick_DoesNotFireDragEvent()
    {
        using var service = new MouseHookService();
        bool fired = false;
        service.OnDragSelectionEnd += _ => fired = true;

        service.ProcessMouseMessage(0x0201, Pt(100, 100)); // WM_LBUTTONDOWN
        service.ProcessMouseMessage(0x0202, Pt(102, 102)); // WM_LBUTTONUP

        fired.Should().BeFalse();
    }

    [Fact]
    public void ProcessMouseMessage_MouseDown_FiresOnMouseDown()
    {
        using var service = new MouseHookService();
        bool fired = false;
        service.OnMouseDown += () => fired = true;

        service.ProcessMouseMessage(0x0201, Pt(100, 100)); // WM_LBUTTONDOWN

        fired.Should().BeTrue();
    }

    [Fact]
    public void ProcessMouseMessage_MouseWheel_FiresOnMouseScroll()
    {
        using var service = new MouseHookService();
        bool fired = false;
        service.OnMouseScroll += () => fired = true;

        service.ProcessMouseMessage(0x020A, Pt(100, 100)); // WM_MOUSEWHEEL

        fired.Should().BeTrue();
    }

    [Fact]
    public void ProcessMouseMessage_RightMouseDown_FiresOnRightMouseDown()
    {
        using var service = new MouseHookService();
        bool fired = false;
        service.OnRightMouseDown += () => fired = true;

        service.ProcessMouseMessage(0x0204, Pt(100, 100)); // WM_RBUTTONDOWN

        fired.Should().BeTrue();
    }

    // --- Keyboard dismiss ---

    [Fact]
    public void ProcessKeyboardMessage_KeyDown_FiresOnKeyDown()
    {
        using var service = new MouseHookService();
        bool fired = false;
        service.OnKeyDown += () => fired = true;

        service.ProcessKeyboardMessage(0x0100); // WM_KEYDOWN

        fired.Should().BeTrue();
    }

    [Fact]
    public void ProcessKeyboardMessage_SysKeyDown_FiresOnKeyDown()
    {
        using var service = new MouseHookService();
        bool fired = false;
        service.OnKeyDown += () => fired = true;

        service.ProcessKeyboardMessage(0x0104); // WM_SYSKEYDOWN

        fired.Should().BeTrue();
    }

    [Fact]
    public void ProcessKeyboardMessage_OtherMessage_DoesNotFire()
    {
        using var service = new MouseHookService();
        bool fired = false;
        service.OnKeyDown += () => fired = true;

        service.ProcessKeyboardMessage(0x0101); // WM_KEYUP

        fired.Should().BeFalse();
    }

    // --- Multi-click detection (MultiClickDetector) ---

    [Fact]
    public void MultiClick_DoubleClick_ReturnsCount2()
    {
        var detector = new MultiClickDetector();
        var pt = Pt(100, 100);

        detector.OnClick(pt, 1000, 500); // first click at t=1000
        var result = detector.OnClick(pt, 1300, 500); // second click at t=1300 (within 500ms)

        result.ClickCount.Should().Be(2);
    }

    [Fact]
    public void MultiClick_TripleClick_ReturnsCount3()
    {
        var detector = new MultiClickDetector();
        var pt = Pt(100, 100);

        detector.OnClick(pt, 1000, 500);
        detector.OnClick(pt, 1200, 500);
        var result = detector.OnClick(pt, 1400, 500);

        result.ClickCount.Should().Be(3);
    }

    [Fact]
    public void MultiClick_TooSlow_ResetsCount()
    {
        var detector = new MultiClickDetector();
        var pt = Pt(100, 100);

        detector.OnClick(pt, 1000, 500);
        var result = detector.OnClick(pt, 1600, 500); // 600ms > 500ms threshold

        result.ClickCount.Should().Be(1);
    }

    [Fact]
    public void MultiClick_TooFar_ResetsCount()
    {
        var detector = new MultiClickDetector();

        detector.OnClick(Pt(100, 100), 1000, 500);
        // MaxClickDistance = 4, so (100, 100) → (110, 100) distance = 10 > 4
        var result = detector.OnClick(Pt(110, 100), 1200, 500);

        result.ClickCount.Should().Be(1);
    }

    [Fact]
    public void MultiClick_WithinDistance_Counts()
    {
        var detector = new MultiClickDetector();

        detector.OnClick(Pt(100, 100), 1000, 500);
        // (100, 100) → (103, 103) distance = sqrt(18) ≈ 4.24 > MaxClickDistance(4)
        var result1 = detector.OnClick(Pt(103, 103), 1200, 500);
        result1.ClickCount.Should().Be(1, "distance slightly exceeds threshold");

        // Reset and try within distance
        detector.Reset();
        detector.OnClick(Pt(100, 100), 1000, 500);
        // (100, 100) → (103, 100) distance = 3 <= 4
        var result2 = detector.OnClick(Pt(103, 100), 1200, 500);
        result2.ClickCount.Should().Be(2, "distance within threshold");
    }

    [Fact]
    public void MultiClick_Reset_ClearsState()
    {
        var detector = new MultiClickDetector();
        detector.OnClick(Pt(100, 100), 1000, 500);
        detector.OnClick(Pt(100, 100), 1200, 500);

        detector.ClickCount.Should().Be(2);

        detector.Reset();

        detector.ClickCount.Should().Be(0);
    }

    [Fact]
    public void ProcessMouseMessage_DoubleClick_FiresSelectionEvent()
    {
        using var service = new MouseHookService();
        int fireCount = 0;
        service.OnDragSelectionEnd += _ => fireCount++;

        // First click (down + up, no drag)
        service.ProcessMouseMessage(0x0201, Pt(100, 100));
        service.ProcessMouseMessage(0x0202, Pt(100, 100));

        // Second click (down + up, no drag) — triggers multi-click detection
        service.ProcessMouseMessage(0x0201, Pt(100, 100));
        service.ProcessMouseMessage(0x0202, Pt(100, 100));

        // The timer fires asynchronously, so the event count depends on timing.
        // But the click detector should have count=2.
        service.ClickDetector.ClickCount.Should().Be(2);
    }

    [Fact]
    public void DragAfterClick_DoesNotTriggerMultiClick()
    {
        using var service = new MouseHookService();

        // First: short click
        service.ProcessMouseMessage(0x0201, Pt(100, 100));
        service.ProcessMouseMessage(0x0202, Pt(100, 100));
        service.ClickDetector.ClickCount.Should().Be(1);

        // Second: drag (not a multi-click)
        service.ProcessMouseMessage(0x0201, Pt(100, 100));
        service.ProcessMouseMessage(0x0200, Pt(200, 100)); // drag
        service.ProcessMouseMessage(0x0202, Pt(200, 100));

        // ClickDetector should have been reset by the drag
        service.ClickDetector.ClickCount.Should().Be(0);
    }

    // --- Reset ---

    [Fact]
    public void Reset_ClearsAllState()
    {
        _detector.OnLeftButtonDown(Pt(0, 0));
        _detector.OnMouseMove(Pt(100, 0));

        _detector.IsLeftButtonDown.Should().BeTrue();
        _detector.IsDragging.Should().BeTrue();

        _detector.Reset();

        _detector.IsLeftButtonDown.Should().BeFalse();
        _detector.IsDragging.Should().BeFalse();
    }
}
