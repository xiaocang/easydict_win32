using System.Diagnostics;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;
using static Easydict.WinUI.Services.MouseHookService;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Performance regression tests for the mouse selection translate pipeline.
/// These tests measure throughput and latency of hot-path code to catch
/// regressions that could cause UI micro-stutters.
///
/// Each test asserts a maximum time budget derived from the requirement that
/// low-level hook callbacks must return within ~200ms to avoid Windows
/// removing the hook, but we target much tighter budgets for smooth UX.
/// </summary>
[Trait("Category", "Performance")]
public class SelectionPerformanceTests
{
    private static POINT Pt(int x, int y) => new() { x = x, y = y };

    // --- DragDetector throughput ---

    [Fact]
    public void DragDetector_10000MouseMoves_CompletesUnder50ms()
    {
        // WM_MOUSEMOVE is the hottest path — fires hundreds of times per second.
        // The DragDetector.OnMouseMove must be essentially free.
        var detector = new DragDetector();
        detector.OnLeftButtonDown(Pt(0, 0));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            detector.OnMouseMove(Pt(i % 5, i % 5)); // stay below threshold
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50,
            "10k OnMouseMove calls should complete in under 50ms to avoid hook callback delays");
    }

    [Fact]
    public void DragDetector_10000FullCycles_CompletesUnder100ms()
    {
        // Full drag cycle: down → move → up. Measures per-selection overhead.
        var detector = new DragDetector();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            detector.OnLeftButtonDown(Pt(0, 0));
            detector.OnMouseMove(Pt(100, 0));
            detector.OnLeftButtonUp(Pt(100, 0));
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "10k full drag cycles should complete in under 100ms");
    }

    // --- MultiClickDetector throughput ---

    [Fact]
    public void MultiClickDetector_10000Clicks_CompletesUnder50ms()
    {
        // OnClick with explicit timing parameters (no P/Invoke) should be very fast.
        var detector = new MultiClickDetector();
        var pt = Pt(100, 100);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            detector.OnClick(pt, 1000 + i * 100, 500);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50,
            "10k OnClick calls should complete in under 50ms");
    }

    [Fact]
    public void MultiClickDetector_RapidDoubleClicks_CompletesUnder50ms()
    {
        // Simulates rapid double-clicking (e.g. selecting words quickly).
        var detector = new MultiClickDetector();
        var pt = Pt(100, 100);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5_000; i++)
        {
            // Two clicks within double-click time
            detector.OnClick(pt, i * 600, 500);
            detector.OnClick(pt, i * 600 + 200, 500);
            detector.Reset();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50,
            "5k double-click cycles should complete in under 50ms");
    }

    // --- ProcessMouseMessage integration throughput ---

    [Fact]
    public void ProcessMouseMessage_10000MovesWithoutPopButton_CompletesUnder100ms()
    {
        // When pop button handle is zero (most common case), WM_MOUSEMOVE
        // should have minimal overhead — no WindowFromPoint/GetAncestor calls.
        using var service = new MouseHookService();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            service.ProcessMouseMessage(0x0200, Pt(i % 1920, i % 1080)); // WM_MOUSEMOVE
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "10k WM_MOUSEMOVE messages should process in under 100ms without pop button");
    }

    [Fact]
    public void ProcessMouseMessage_1000ClicksWithoutPopButton_CompletesUnder100ms()
    {
        // WM_LBUTTONDOWN without pop button should skip WindowFromPoint.
        using var service = new MouseHookService();
        int mouseDownCount = 0;
        service.OnMouseDown += () => mouseDownCount++;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1_000; i++)
        {
            service.ProcessMouseMessage(0x0201, Pt(100, 100)); // WM_LBUTTONDOWN
            service.ProcessMouseMessage(0x0202, Pt(100, 100)); // WM_LBUTTONUP
        }
        sw.Stop();

        mouseDownCount.Should().Be(1_000);
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "1k click cycles should process in under 100ms without pop button");
    }

    [Fact]
    public void ProcessMouseMessage_FullDragSequence_CompletesUnder200ms()
    {
        // Full drag-select pipeline: down → 10 moves → up, repeated 1000 times.
        // Measures the combined overhead of all state machines.
        using var service = new MouseHookService();
        int dragCount = 0;
        service.OnDragSelectionEnd += _ => dragCount++;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1_000; i++)
        {
            service.ProcessMouseMessage(0x0201, Pt(0, 0));      // WM_LBUTTONDOWN
            for (int j = 1; j <= 10; j++)
                service.ProcessMouseMessage(0x0200, Pt(j * 10, 0)); // WM_MOUSEMOVE
            service.ProcessMouseMessage(0x0202, Pt(100, 0));     // WM_LBUTTONUP
        }
        sw.Stop();

        dragCount.Should().Be(1_000);
        sw.ElapsedMilliseconds.Should().BeLessThan(200,
            "1k full drag sequences (with 10 moves each) should process in under 200ms");
    }

    // --- Dismiss event throughput ---

    [Fact]
    public void DismissEvents_10000Mixed_CompletesUnder100ms()
    {
        // Dismiss events (scroll, right-click, keyboard) must not add latency.
        using var service = new MouseHookService();
        int totalEvents = 0;
        service.OnMouseScroll += () => totalEvents++;
        service.OnRightMouseDown += () => totalEvents++;
        service.OnKeyDown += () => totalEvents++;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            switch (i % 3)
            {
                case 0: service.ProcessMouseMessage(0x020A, Pt(0, 0)); break; // scroll
                case 1: service.ProcessMouseMessage(0x0204, Pt(0, 0)); break; // right-click
                case 2: service.ProcessKeyboardMessage(0x0100); break;         // key down
            }
        }
        sw.Stop();

        totalEvents.Should().Be(10_000);
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "10k dismiss events should process in under 100ms");
    }

    // --- Memory allocation awareness ---

    [Fact]
    public void DragDetector_NoBoxingAllocations_StructsUsedDirectly()
    {
        // Verify that DragResult and ClickResult are value types (record struct),
        // ensuring no heap allocations on the hot path.
        typeof(DragResult).IsValueType.Should().BeTrue("DragResult should be a value type to avoid GC pressure");
        typeof(ClickResult).IsValueType.Should().BeTrue("ClickResult should be a value type to avoid GC pressure");
        typeof(POINT).IsValueType.Should().BeTrue("POINT should be a value type to avoid GC pressure");
    }

    [Fact]
    public void MultiClickDetector_StateSize_IsMinimal()
    {
        // Verify the detector doesn't accumulate unbounded state.
        var detector = new MultiClickDetector();

        // Simulate 10k clicks — state should not grow
        for (int i = 0; i < 10_000; i++)
        {
            detector.OnClick(Pt(100, 100), i * 100, 500);
        }

        // ClickCount should be bounded (resets when time/distance threshold exceeded)
        detector.ClickCount.Should().BeLessThan(100,
            "click count should reset periodically, not accumulate unboundedly");
    }

    // --- Timing constant sanity checks ---

    [Fact]
    public void TimingConstants_AreWithinReasonableBounds()
    {
        // These constants directly affect perceived latency. If someone changes them,
        // this test flags it for review.
        PopButtonService.SelectionDelayMs.Should().BeInRange(50, 300,
            "selection delay should be 50-300ms for responsive UX");

        PopButtonService.AutoDismissMs.Should().BeInRange(3000, 10000,
            "auto-dismiss should be 3-10 seconds");

        MouseHookService.MinDragDistance.Should().BeInRange(5, 20,
            "drag threshold should be 5-20px to avoid false positives without being too insensitive");

        MultiClickDetector.MaxClickDistance.Should().BeInRange(2, 10,
            "click distance threshold should be 2-10px");
    }
}
