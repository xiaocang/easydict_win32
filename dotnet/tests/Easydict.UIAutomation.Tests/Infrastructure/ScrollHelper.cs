using FlaUI.Core.AutomationElements;
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
            var scrollPattern = scrollViewer.Patterns.Scroll.Pattern;
            // -1 means "do not change" for horizontal scroll
            scrollPattern.SetScrollPercent(-1, verticalPercent);
            log?.Invoke($"ScrollPattern: scrolled to {verticalPercent}%");
        }
        else
        {
            log?.Invoke("ScrollPattern not available, falling back to Mouse.Scroll");
            Mouse.MoveTo(scrollViewer.GetClickablePoint());
            Mouse.Scroll(-15);
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
        if (scrollViewer.Patterns.Scroll.IsSupported)
        {
            var scrollPattern = scrollViewer.Patterns.Scroll.Pattern;

            // Jump to the expected location first
            scrollPattern.SetScrollPercent(-1, startPercent);
            log?.Invoke($"ScrollPattern: jumped to {startPercent}%");
            Thread.Sleep(ScrollSettleMs);

            var result = finder();
            if (result != null) return result;

            // Scan forward from startPercent to 100% in small steps
            for (var percent = startPercent + ScanStepPercent; percent <= 100; percent += ScanStepPercent)
            {
                scrollPattern.SetScrollPercent(-1, percent);
                log?.Invoke($"ScrollPattern: scanning at {percent}%");
                Thread.Sleep(ScanSettleMs);

                result = finder();
                if (result != null) return result;
            }

            // If not found scanning forward, scan backward from startPercent to 0%
            for (var percent = startPercent - ScanStepPercent; percent >= 0; percent -= ScanStepPercent)
            {
                scrollPattern.SetScrollPercent(-1, percent);
                log?.Invoke($"ScrollPattern: scanning back at {percent}%");
                Thread.Sleep(ScanSettleMs);

                result = finder();
                if (result != null) return result;
            }
        }
        else
        {
            log?.Invoke("ScrollPattern not available, falling back to Mouse.Scroll");
            Mouse.MoveTo(scrollViewer.GetClickablePoint());

            // Scroll down incrementally and check at each step
            for (int i = 0; i < 20; i++)
            {
                Mouse.Scroll(-5);
                Thread.Sleep(ScanSettleMs);

                var result = finder();
                if (result != null) return result;
            }
        }

        return null;
    }
}
