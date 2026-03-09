using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.Definitions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using System.Diagnostics;
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
            var scrollViewer = window.FindFirstDescendant(cf => cf.ByName("MainScrollViewer"));
            if (scrollViewer != null)
            {
                // Scroll down to show more settings
                Mouse.MoveTo(scrollViewer.GetClickablePoint());
                Mouse.Scroll(-5); // Scroll down
                Thread.Sleep(1000);

                var path = ScreenshotHelper.CaptureWindow(window, "06_settings_services");
                _output.WriteLine($"Screenshot saved: {path}");
            }

            // Scroll further down for more sections
            if (scrollViewer != null)
            {
                Mouse.Scroll(-10);
                Thread.Sleep(1000);

                var path = ScreenshotHelper.CaptureWindow(window, "07_settings_api_keys");
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
    public void SettingsPage_OpenBackLoop_ShouldSupportMemoryMarkerCollection()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);
        var baseline = CaptureAppProcessMemory($"{_abMode}_baseline");

        var iterations = ResolveLoopIterations();
        _output.WriteLine($"[MemoryLoop] Iterations={iterations}");
        var afterBackSamples = new List<MemorySample>();
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

            var backButton = FindTopLeftBackButton(window);
            backButton.Should().NotBeNull($"iteration {i}: should find the Settings back button");
            ClickElement(backButton!, $"MemoryLoop.BackButton iteration={i}");
            Thread.Sleep(1200);
            var backSample = CaptureAppProcessMemory($"{_abMode}_iter_{i}_after_back");
            if (backSample.HasValue)
            {
                afterBackSamples.Add(backSample.Value);
            }

            _output.WriteLine($"[MemoryLoop] Iteration {i}: navigated back to Main page.");
        }

        EmitMemorySummary(baseline, afterBackSamples);
    }

    private AutomationElement? WaitForSettingsButton(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => TryFindSettingsButton(window),
            timeout).Result;
    }

    private static AutomationElement? TryFindSettingsButton(Window window)
    {
        return window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))
            ?? window.FindFirstDescendant(cf => cf.ByName("SettingsButton"))
            ?? window.FindFirstDescendant(cf => cf.ByName("Settings"))
            ?? FindTopRightLikelySettingsButton(window);
    }

    private static AutomationElement? FindTopLeftBackButton(Window window)
    {
        var bounds = window.BoundingRectangle;
        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        return buttons
            .Where(button =>
                !button.IsOffscreen &&
                button.BoundingRectangle.Width > 5 &&
                button.BoundingRectangle.Height > 5 &&
                button.BoundingRectangle.Left >= bounds.Left &&
                button.BoundingRectangle.Top >= bounds.Top &&
                button.BoundingRectangle.Left <= bounds.Left + 120 &&
                button.BoundingRectangle.Top <= bounds.Top + 180)
            .OrderBy(button => button.BoundingRectangle.Left + button.BoundingRectangle.Top)
            .FirstOrDefault();
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

    private void EmitMemorySummary(MemorySample? baseline, IReadOnlyList<MemorySample> afterBackSamples)
    {
        if (!baseline.HasValue || afterBackSamples.Count == 0)
        {
            _output.WriteLine($"[MemoryLoop][{_abMode}] Summary unavailable (missing baseline or back samples).");
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
            $"[MemoryLoop][{_abMode}] Summary: BaselineWS={baseline.Value.WorkingSetMb:F1}MB FirstBackWS={firstBack.WorkingSetMb:F1}MB LastBackWS={lastBack.WorkingSetMb:F1}MB PeakBackWS={peakBack:F1}MB");
        _output.WriteLine(
            $"[MemoryLoop][{_abMode}] Delta: WS={deltaWs:+0.0;-0.0;0.0}MB Private={deltaPrivate:+0.0;-0.0;0.0}MB Paged={deltaPaged:+0.0;-0.0;0.0}MB TailSlopeWS={tailSlope:+0.00;-0.00;0.00}MB/iter");
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
