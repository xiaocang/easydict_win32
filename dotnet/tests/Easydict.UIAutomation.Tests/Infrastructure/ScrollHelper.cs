using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Shared helpers for scrolling a ScrollViewer via UIA ScrollPattern (percentage-based).
/// Falls back to Mouse.Scroll when ScrollPattern is not available.
/// </summary>
public static class ScrollHelper
{
    /// <summary>
    /// Default step size (percentage points) used when scanning for an element.
    /// </summary>
    private const double ScanStepPercent = 5;

    /// <summary>
    /// Delay after each scroll operation to allow the UI to settle.
    /// </summary>
    private const int ScrollSettleMs = 800;

    /// <summary>
    /// Shorter delay used during incremental scanning.
    /// </summary>
    private const int ScanSettleMs = 500;

    private const int MouseFallbackScrollStep = 5;
    private const int MouseFallbackSettleMs = 40;
    private const int MouseFallbackMaxWheelTicks = 30;
    private const double MouseFallbackPercentTolerance = 4;

    /// <summary>
    /// Scroll a ScrollViewer to the specified vertical percentage.
    /// </summary>
    /// <param name="scrollViewer">The ScrollViewer automation element.</param>
    /// <param name="verticalPercent">Target vertical scroll position (0–100).</param>
    /// <param name="log">Optional logging callback.</param>
    public static void ScrollToPercent(
        AutomationElement scrollViewer,
        double verticalPercent,
        Action<string>? log = null)
    {
        if (scrollViewer.Patterns.Scroll.IsSupported)
        {
            try
            {
                var scrollPattern = scrollViewer.Patterns.Scroll.Pattern;
                // -1 means "do not change" for horizontal scroll
                scrollPattern.SetScrollPercent(-1, verticalPercent);
                log?.Invoke($"ScrollPattern: scrolled to {verticalPercent}%");
            }
            catch (InvalidOperationException ex)
            {
                log?.Invoke($"ScrollPattern failed ({ex.Message}), falling back to Mouse.Scroll");
                ScrollByMouseToPercent(scrollViewer, verticalPercent, log);
            }
        }
        else
        {
            log?.Invoke("ScrollPattern not available, falling back to Mouse.Scroll");
            ScrollByMouseToPercent(scrollViewer, verticalPercent, log);
        }

        Thread.Sleep(ScrollSettleMs);
    }

    /// <summary>
    /// Scroll to an approximate percentage, then scan incrementally until
    /// <paramref name="finder"/> returns a non-null element.
    /// </summary>
    /// <param name="scrollViewer">The ScrollViewer automation element.</param>
    /// <param name="startPercent">Initial scroll percentage to jump to.</param>
    /// <param name="finder">
    /// Delegate called after each scroll to locate the target element.
    /// Return non-null to stop scanning.
    /// </param>
    /// <param name="log">Optional logging callback.</param>
    /// <returns>The element returned by <paramref name="finder"/>, or null if not found.</returns>
    public static AutomationElement? ScrollToFind(
        AutomationElement scrollViewer,
        double startPercent,
        Func<AutomationElement?> finder,
        Action<string>? log = null)
    {
        var currentResult = finder();
        if (IsVisible(currentResult))
        {
            return currentResult;
        }

        if (scrollViewer.Patterns.Scroll.IsSupported)
        {
            try
            {
                var scrollPattern = scrollViewer.Patterns.Scroll.Pattern;

                scrollPattern.SetScrollPercent(-1, startPercent);
                log?.Invoke($"ScrollPattern: jumped to {startPercent}%");
                Thread.Sleep(ScrollSettleMs);

                var result = finder();
                if (IsVisible(result)) return result;

                for (var percent = startPercent + ScanStepPercent; percent <= 100; percent += ScanStepPercent)
                {
                    scrollPattern.SetScrollPercent(-1, percent);
                    log?.Invoke($"ScrollPattern: scanning at {percent}%");
                    Thread.Sleep(ScanSettleMs);

                    result = finder();
                    if (IsVisible(result)) return result;
                }

                for (var percent = startPercent - ScanStepPercent; percent >= 0; percent -= ScanStepPercent)
                {
                    scrollPattern.SetScrollPercent(-1, percent);
                    log?.Invoke($"ScrollPattern: scanning back at {percent}%");
                    Thread.Sleep(ScanSettleMs);

                    result = finder();
                    if (IsVisible(result)) return result;
                }
            }
            catch (InvalidOperationException ex)
            {
                log?.Invoke($"ScrollPattern failed ({ex.Message}), falling back to Mouse.Scroll");
            }
        }

        log?.Invoke("ScrollPattern not available or failed, falling back to Mouse.Scroll");
        MoveMouseToScrollTarget(scrollViewer, log);

        // Scroll down incrementally and check at each step
        for (int i = 0; i < 20; i++)
        {
            Mouse.Scroll(-5);
            Thread.Sleep(ScanSettleMs);

            var result = finder();
            if (IsVisible(result)) return result;
        }

        return null;
    }

    private static bool IsVisible(AutomationElement? element)
    {
        return element is { IsOffscreen: false };
    }

    private static void MoveMouseToScrollTarget(
        AutomationElement scrollViewer,
        Action<string>? log)
    {
        try
        {
            Mouse.MoveTo(scrollViewer.GetClickablePoint());
            return;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Clickable point unavailable ({ex.Message}), using element center");
        }

        var bounds = scrollViewer.BoundingRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Mouse.MoveTo(new Point(
            bounds.Left + (bounds.Width / 2),
            bounds.Top + (bounds.Height / 2)));
    }

    private static int GetMouseScrollDeltaForTargetPercent(double verticalPercent)
    {
        return verticalPercent <= 0 ? MouseFallbackScrollStep : -MouseFallbackScrollStep;
    }

    private static int GetMouseScrollTickCountForTargetPercent(double verticalPercent)
    {
        if (verticalPercent <= 0)
        {
            return MouseFallbackMaxWheelTicks;
        }

        return Math.Clamp(
            (int)Math.Ceiling(verticalPercent / ScanStepPercent),
            1,
            MouseFallbackMaxWheelTicks);
    }

    private static void ScrollByMouseToPercent(
        AutomationElement scrollViewer,
        double verticalPercent,
        Action<string>? log)
    {
        var targetPercent = Math.Clamp(verticalPercent, 0, 100);
        MoveMouseToScrollTarget(scrollViewer, log);

        var fallbackDelta = GetMouseScrollDeltaForTargetPercent(targetPercent);
        var wheelTicks = GetMouseScrollTickCountForTargetPercent(targetPercent);

        for (var i = 0; i < wheelTicks; i++)
        {
            if (TryGetVerticalScrollPercent(scrollViewer, out var currentPercent))
            {
                var remaining = targetPercent - currentPercent;
                if (Math.Abs(remaining) <= MouseFallbackPercentTolerance)
                {
                    log?.Invoke($"Mouse.Scroll fallback reached {currentPercent:F1}%");
                    return;
                }

                fallbackDelta = remaining > 0
                    ? -MouseFallbackScrollStep
                    : MouseFallbackScrollStep;
            }

            Mouse.Scroll(fallbackDelta);
            Thread.Sleep(MouseFallbackSettleMs);
        }

        if (TryGetVerticalScrollPercent(scrollViewer, out var finalPercent))
        {
            log?.Invoke($"Mouse.Scroll fallback stopped at {finalPercent:F1}% after {wheelTicks} wheel tick(s)");
        }
        else
        {
            log?.Invoke($"Mouse.Scroll fallback used {wheelTicks} wheel tick(s) toward {targetPercent}%");
        }
    }

    public static bool TryGetVerticalScrollPercent(
        AutomationElement scrollViewer,
        out double verticalPercent)
    {
        verticalPercent = 0;
        if (!scrollViewer.Patterns.Scroll.IsSupported)
        {
            return false;
        }

        try
        {
            var current = scrollViewer.Patterns.Scroll.Pattern.VerticalScrollPercent.Value;
            if (double.IsNaN(current) || current < 0)
            {
                return false;
            }

            verticalPercent = current;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or PropertyNotSupportedException)
        {
            return false;
        }
    }
}
