using System.Diagnostics;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>
/// Polling utilities for waiting on async conditions in UI automation tests.
/// Complements FlaUI's Retry.WhileNull (which targets UIA elements) with
/// general-purpose polling for Win32 window discovery and state transitions.
/// </summary>
public static class TestRetry
{
    /// <summary>
    /// Poll until a condition becomes true, or timeout.
    /// Returns whether the condition was met.
    /// </summary>
    public static bool Until(Func<bool> condition, TimeSpan timeout, int pollIntervalMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(pollIntervalMs);
        }
        return condition(); // Final check
    }

    /// <summary>
    /// Poll until a factory produces a non-zero IntPtr, or timeout.
    /// Useful for waiting on window handle discovery via EnumWindows.
    /// </summary>
    public static IntPtr UntilNotZero(Func<IntPtr> factory, TimeSpan timeout, int pollIntervalMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var value = factory();
            if (value != IntPtr.Zero) return value;
            Thread.Sleep(pollIntervalMs);
        }
        return factory(); // Final check
    }
}
