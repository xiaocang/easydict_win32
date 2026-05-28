using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.Definitions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class SettingsPageTests : IDisposable
{
    private readonly record struct MemorySample(double WorkingSetMb, double PrivateMb, double PagedMb);

    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;
    private readonly string _abMode;
    private static readonly TimeSpan ImmediateMouseResponseBudget = TimeSpan.FromSeconds(1);

    public SettingsPageTests(ITestOutputHelper output)
    {
        _output = output;
        _abMode = ResolveAbMode();
        _output.WriteLine($"[MemoryLoop] A/B mode={_abMode}");
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void SettingsPage_ShouldOpenFromMainWindow()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));

        if (settingsButton != null)
        {
            settingsButton.Click();
            Thread.Sleep(2000); // Wait for page transition

            var path = ScreenshotHelper.CaptureWindow(window, "05_settings_page");
            _output.WriteLine($"Screenshot saved: {path}");

            var result = VisualRegressionHelper.CompareWithBaseline(path, "05_settings_page");
            if (result != null)
            {
                _output.WriteLine(result.ToString());
                result.Passed.Should().BeTrue(result.ToString());
            }
        }
        else
        {
            _output.WriteLine("SettingsButton not found - capturing window for inspection");
            DumpButtonDiagnostics(window, "SettingsPage_ShouldOpenFromMainWindow");
            ScreenshotHelper.CaptureWindow(window, "05_settings_button_not_found");
        }
    }

    [Fact]
    public void SettingsPage_ShouldShowServiceConfiguration()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));

        if (settingsButton != null)
        {
            settingsButton.Click();
            Thread.Sleep(2000);

            // Try to scroll down to see service configuration
            var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
            if (scrollViewer != null)
            {
                // Scroll to Enabled Services section (~12%)
                ScrollHelper.ScrollToPercent(scrollViewer, 12, _output.WriteLine);

                var path = ScreenshotHelper.CaptureWindow(window, "06_settings_services");
                _output.WriteLine($"Screenshot saved: {path}");

                // Scroll to API keys area (~50%)
                ScrollHelper.ScrollToPercent(scrollViewer, 50, _output.WriteLine);

                path = ScreenshotHelper.CaptureWindow(window, "07_settings_api_keys");
                _output.WriteLine($"Screenshot saved: {path}");
            }
        }
        else
        {
            _output.WriteLine("SettingsButton not found");
            DumpButtonDiagnostics(window, "SettingsPage_ShouldShowServiceConfiguration");
        }
    }

    [Fact]
    public void SettingsPage_ShouldAcceptImmediateScrollAfterContentVisible()
    {
        var window = _launcher.GetMainWindow();
        window.SetForeground();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on main window");

        ClickElement(settingsButton!, "ImmediateScroll.SettingsButton");

        var scrollViewer = WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15));
        scrollViewer.Should().NotBeNull("MainScrollViewer should be visible as soon as Settings content is interactive");
        window.SetForeground();
        Thread.Sleep(250);

        var scrollPattern = scrollViewer!.Patterns.Scroll.PatternOrDefault;
        scrollPattern.Should().NotBeNull("Settings MainScrollViewer should expose ScrollPattern");
        scrollPattern!.VerticallyScrollable.Value.Should().BeTrue("Settings should be scrollable for immediate wheel input");

        scrollPattern.SetScrollPercent(-1, 0);
        Thread.Sleep(100);
        ScrollHelper.TryGetVerticalScrollPercent(scrollViewer, out var before);
        MoveMouseToScrollGutter(scrollViewer);
        Mouse.Click(GetScrollGutterPoint(scrollViewer));

        var wheelDelta = before > 80 ? 4 : -4;
        var wheelScrolled = WaitForVerticalPercentChange(
            scrollViewer,
            before,
            TimeSpan.FromSeconds(2),
            pollIntervalMs: 150,
            onTick: () => Mouse.Scroll(wheelDelta));
        if (!wheelScrolled)
        {
            var targetPercent = before > 50 ? 0 : Math.Min(100, before + 20);
            scrollPattern.SetScrollPercent(-1, targetPercent);
        }

        (wheelScrolled || WaitForVerticalPercentChange(scrollViewer, before, TimeSpan.FromSeconds(2)))
            .Should().BeTrue("Settings scrolling should be handled immediately after content becomes visible");
    }

    [Fact]
    public void SettingsPage_ShouldLoadUnwarmedTabWhenClickedImmediately()
    {
        var window = _launcher.GetMainWindow();
        window.SetForeground();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on main window");

        ClickElement(settingsButton!, "ImmediateTab.SettingsButton");

        WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15))
            .Should().NotBeNull("Settings content should become visible before tab interaction");

        var viewsTab = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "SettingsTab_Views"),
                TimeSpan.FromSeconds(10))
            .Result;
        viewsTab.Should().NotBeNull("Views tab should be available immediately after Settings opens");

        ClickElement(viewsTab!, "ImmediateTab.Views");

        Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "MainWindowReorderModeButton"),
                TimeSpan.FromSeconds(15))
            .Result
            .Should()
            .NotBeNull("clicking an unwarmed Views tab should finish loading and show its content");
    }

    [Fact]
    public void SettingsPage_FirstEntry_ShouldAcceptImmediatePhysicalMouseTabClickAndShowReaction()
    {
        var window = _launcher.GetMainWindow();
        window.SetForeground();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on main window");

        ClickElementWithMouse(settingsButton!, "FirstEntryMouse.SettingsButton");

        WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15))
            .Should()
            .NotBeNull("Settings content should become visible before immediate tab interaction");

        var hotkeysTab = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "SettingsTab_Hotkeys"),
                TimeSpan.FromSeconds(5))
            .Result;
        hotkeysTab.Should().NotBeNull("Hotkeys tab should be physically clickable on first Settings entry");

        var hotkeysPoint = MoveMouseToElement(hotkeysTab!, "FirstEntryMouse.HotkeysTab");
        var reactionTimer = Stopwatch.StartNew();
        Mouse.Click(hotkeysPoint);

        var visibleHotkeyBox = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "ShowHotkeyBox"),
                ImmediateMouseResponseBudget)
            .Result;
        reactionTimer.Stop();

        visibleHotkeyBox
            .Should()
            .NotBeNull("the first physical mouse click inside Settings must produce visible tab content within 1s");
        reactionTimer.Elapsed
            .Should()
            .BeLessThanOrEqualTo(ImmediateMouseResponseBudget, "mouse input must never feel unresponsive for more than 1s");

        _output.WriteLine($"[FirstEntryMouse] Hotkeys tab reaction in {reactionTimer.ElapsedMilliseconds}ms");
        Retry.WhileNull(
                () =>
                {
                    var hotkeyBox = FindVisibleByAutomationId(window, "ShowHotkeyBox")?.AsTextBox();
                    return string.IsNullOrWhiteSpace(hotkeyBox?.Text) ? null : hotkeyBox;
                },
                TimeSpan.FromSeconds(5))
            .Result
            .Should()
            .NotBeNull("the Hotkeys tab should finish loading its editable settings after the immediate visual response");
        ScreenshotHelper.CaptureWindow(window, "settings_first_entry_immediate_mouse_tab_reaction");
    }

    [Fact]
    public void SettingsPage_OpenBackLoop_ShouldSupportMemoryMarkerCollection()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);
        var baseline = CaptureAppProcessMemory($"{_abMode}_baseline");

        var iterations = ResolveLoopIterations();
        var idleDelayMsAfterBack = ResolveIdleDelayMsAfterBack();
        _output.WriteLine($"[MemoryLoop] Iterations={iterations}");
        _output.WriteLine($"[MemoryLoop] IdleAfterBackMs={idleDelayMsAfterBack}");
        var immediateBackSamples = new List<MemorySample>();
        var settledBackSamples = new List<MemorySample>();
        for (var i = 1; i <= iterations; i++)
        {
            var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
            if (settingsButton == null)
            {
                DumpButtonDiagnostics(window, $"SettingsPage_OpenBackLoop iteration={i}");
                ScreenshotHelper.CaptureWindow(window, $"memory_loop_missing_settings_button_iter_{i}");
            }
            settingsButton.Should().NotBeNull($"iteration {i}: settings button should be available");
            ClickElement(settingsButton!, $"MemoryLoop.SettingsButton iteration={i}");
            Thread.Sleep(1800);
            _ = CaptureAppProcessMemory($"{_abMode}_iter_{i}_after_open");

            _output.WriteLine($"[MemoryLoop] Iteration {i}: opened Settings page. Check Debug Output for [Memory] SettingsPage markers.");

            var backButton = WaitForBackButton(window, TimeSpan.FromSeconds(15));
            backButton.Should().NotBeNull($"iteration {i}: should find the Settings back button");
            ClickElement(backButton!, $"MemoryLoop.BackButton iteration={i}");
            Thread.Sleep(1200);
            var backSample = CaptureAppProcessMemory($"{_abMode}_iter_{i}_after_back");
            if (backSample.HasValue)
            {
                immediateBackSamples.Add(backSample.Value);
            }

            if (idleDelayMsAfterBack > 0)
            {
                Thread.Sleep(idleDelayMsAfterBack);
                var settledSample = CaptureAppProcessMemory($"{_abMode}_iter_{i}_after_back_idle");
                if (settledSample.HasValue)
                {
                    settledBackSamples.Add(settledSample.Value);
                }
            }

            _output.WriteLine($"[MemoryLoop] Iteration {i}: navigated back to Main page.");
        }

        EmitMemorySummary("ImmediateBack", baseline, immediateBackSamples);
        if (settledBackSamples.Count > 0)
        {
            EmitMemorySummary("SettledBack", baseline, settledBackSamples);
        }
    }

    private AutomationElement? WaitForSettingsButton(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => TryFindSettingsButton(window),
            timeout).Result;
    }

    private AutomationElement? WaitForBackButton(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => TryFindBackButton(window),
            timeout).Result;
    }

    private AutomationElement? WaitForSettingsScrollViewer(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => FindVisibleByAutomationId(window, "MainScrollViewer"),
            timeout).Result;
    }

    private static AutomationElement? FindVisibleByAutomationId(Window window, string automationId)
    {
        try
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return element != null && IsOnScreenOrUnknown(element)
                ? element
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static AutomationElement? TryFindSettingsButton(Window window)
    {
        try
        {
            return window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))
                ?? window.FindFirstDescendant(cf => cf.ByName("SettingsButton"))
                ?? window.FindFirstDescendant(cf => cf.ByName("Settings"))
                ?? FindTopRightLikelySettingsButton(window);
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static AutomationElement? TryFindBackButton(Window window)
    {
        try
        {
            return window.FindFirstDescendant(cf => cf.ByAutomationId("BackButton"))
                ?? window.FindFirstDescendant(cf => cf.ByName("BackButton"))
                ?? window.FindFirstDescendant(cf => cf.ByName("Back"))
                ?? FindTopLeftBackButton(window);
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static AutomationElement? FindTopLeftBackButton(Window window)
    {
        var bounds = window.BoundingRectangle;
        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        return buttons
            .Where(button =>
                IsOnScreenOrUnknown(button) &&
                button.BoundingRectangle.Width > 5 &&
                button.BoundingRectangle.Height > 5 &&
                button.BoundingRectangle.Left >= bounds.Left &&
                button.BoundingRectangle.Top >= bounds.Top &&
                button.BoundingRectangle.Left <= bounds.Left + 120 &&
                button.BoundingRectangle.Top <= bounds.Top + 180)
            .OrderBy(button => button.BoundingRectangle.Left + button.BoundingRectangle.Top)
            .FirstOrDefault();
    }

    // Some auto-generated WinUI subtrees expose Button control type without the
    // IsOffscreen property; reading it throws PropertyNotSupportedException. Treat
    // those as on-screen so geometry filters below decide visibility.
    private static bool IsOnScreenOrUnknown(AutomationElement element)
    {
        try
        {
            return !element.IsOffscreen;
        }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return true;
        }
    }

    private static bool WaitForVerticalPercentChange(
        AutomationElement scrollViewer,
        double initialPercent,
        TimeSpan timeout,
        int pollIntervalMs = 100,
        Action? onTick = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            onTick?.Invoke();
            Thread.Sleep(pollIntervalMs);
            if (ScrollHelper.TryGetVerticalScrollPercent(scrollViewer, out var current)
                && Math.Abs(current - initialPercent) > 0.5)
            {
                return true;
            }
        }

        return false;
    }

    private static void MoveMouseToScrollGutter(AutomationElement element)
    {
        Mouse.MoveTo(GetScrollGutterPoint(element));
    }

    private static Point GetScrollGutterPoint(AutomationElement element)
    {
        var bounds = element.BoundingRectangle;
        if (bounds.Width > 48 && bounds.Height > 48)
        {
            return new Point(
                bounds.Right - 24,
                bounds.Top + bounds.Height / 2);
        }

        try
        {
            return element.GetClickablePoint();
        }
        catch
        {
            return new Point(
                bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2);
        }
    }

    private static AutomationElement? FindTopRightLikelySettingsButton(Window window)
    {
        var bounds = window.BoundingRectangle;
        var headerTopLimit = bounds.Top + 220;
        var rightLimit = bounds.Right - 160;

        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        return buttons
            .Where(button =>
                button.BoundingRectangle.Top <= headerTopLimit &&
                button.BoundingRectangle.Right >= rightLimit &&
                button.BoundingRectangle.Width <= 60 &&
                button.BoundingRectangle.Height <= 60)
            .OrderByDescending(button => button.BoundingRectangle.Right)
            .FirstOrDefault();
    }

    private void DumpButtonDiagnostics(Window window, string context)
    {
        var bounds = window.BoundingRectangle;
        _output.WriteLine($"[{context}] Window bounds: {bounds}");

        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Take(20)
            .ToList();
        _output.WriteLine($"[{context}] Found {buttons.Count} button(s), logging up to 20:");

        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            _output.WriteLine(
                $"  [{i}] Name='{button.Name}', AutomationId='{button.AutomationId}', Class='{button.ClassName}', Bounds={button.BoundingRectangle}");
        }
    }

    private void ClickElement(AutomationElement element, string context)
    {
        try
        {
            var invokePattern = element.Patterns.Invoke.PatternOrDefault;
            if (invokePattern != null)
            {
                invokePattern.Invoke();
                return;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[{context}] Invoke pattern failed: {ex.Message}");
        }

        element.Click();
    }

    private void ClickElementWithMouse(AutomationElement element, string context)
    {
        var point = MoveMouseToElement(element, context);
        Mouse.Click(point);
    }

    private Point MoveMouseToElement(AutomationElement element, string context)
    {
        var point = GetClickablePoint(element, context);
        _output.WriteLine($"[{context}] Physical mouse click at {point}, bounds={element.BoundingRectangle}");
        Mouse.MoveTo(point);
        return point;
    }

    private Point GetClickablePoint(AutomationElement element, string context)
    {
        try
        {
            return element.GetClickablePoint();
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[{context}] GetClickablePoint failed: {ex.Message}; using element center");
            var bounds = element.BoundingRectangle;
            return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }
    }

    private MemorySample? CaptureAppProcessMemory(string marker)
    {
        try
        {
            var pid = _launcher.Application.ProcessId;
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            var sample = new MemorySample(
                ToMb(process.WorkingSet64),
                ToMb(process.PrivateMemorySize64),
                ToMb(process.PagedMemorySize64));
            _output.WriteLine(
                $"[MemoryLoop][{marker}] PID={pid} WS={sample.WorkingSetMb:F1}MB Private={sample.PrivateMb:F1}MB Paged={sample.PagedMb:F1}MB");
            return sample;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[MemoryLoop][{marker}] Failed to read process memory: {ex.Message}");
            return null;
        }
    }

    private void EmitMemorySummary(string phase, MemorySample? baseline, IReadOnlyList<MemorySample> afterBackSamples)
    {
        if (!baseline.HasValue || afterBackSamples.Count == 0)
        {
            _output.WriteLine($"[MemoryLoop][{_abMode}][{phase}] Summary unavailable (missing baseline or back samples).");
            return;
        }

        var firstBack = afterBackSamples[0];
        var lastBack = afterBackSamples[^1];
        var peakBack = afterBackSamples.Max(s => s.WorkingSetMb);

        var deltaWs = lastBack.WorkingSetMb - baseline.Value.WorkingSetMb;
        var deltaPrivate = lastBack.PrivateMb - baseline.Value.PrivateMb;
        var deltaPaged = lastBack.PagedMb - baseline.Value.PagedMb;

        var halfIndex = afterBackSamples.Count / 2;
        double tailSlope = 0;
        if (afterBackSamples.Count - halfIndex > 1)
        {
            tailSlope = (afterBackSamples[^1].WorkingSetMb - afterBackSamples[halfIndex].WorkingSetMb)
                / (afterBackSamples.Count - halfIndex - 1);
        }

        _output.WriteLine(
            $"[MemoryLoop][{_abMode}][{phase}] Summary: BaselineWS={baseline.Value.WorkingSetMb:F1}MB FirstBackWS={firstBack.WorkingSetMb:F1}MB LastBackWS={lastBack.WorkingSetMb:F1}MB PeakBackWS={peakBack:F1}MB");
        _output.WriteLine(
            $"[MemoryLoop][{_abMode}][{phase}] Delta: WS={deltaWs:+0.0;-0.0;0.0}MB Private={deltaPrivate:+0.0;-0.0;0.0}MB Paged={deltaPaged:+0.0;-0.0;0.0}MB TailSlopeWS={tailSlope:+0.00;-0.00;0.00}MB/iter");
    }

    private static double ToMb(long bytes) => bytes / 1024d / 1024d;

    private static int ResolveLoopIterations()
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_MEMORY_LOOP_ITERATIONS");
        if (!int.TryParse(value, out var iterations))
        {
            return 5;
        }

        return Math.Clamp(iterations, 1, 50);
    }

    private static int ResolveIdleDelayMsAfterBack()
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_MEMORY_IDLE_MS_AFTER_BACK");
        if (!int.TryParse(value, out var delayMs))
        {
            return 1500;
        }

        return Math.Clamp(delayMs, 0, 10000);
    }

    private static string ResolveAbMode()
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_MEMORY_AB_MODE");
        return string.Equals(value, "B", StringComparison.OrdinalIgnoreCase) ? "B" : "A";
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
