using System.Diagnostics;

namespace Easydict.WinUI.Services;

/// <summary>
/// Lightweight memory diagnostics for Visual Studio profiling sessions.
/// All methods are conditional on DEBUG — zero cost in Release builds.
///
/// Usage:
///   MemoryDiagnostics.LogSnapshot("SettingsPage.OnPageLoaded");
///   var baseline = GC.GetTotalMemory(true);
///   // ... operation ...
///   MemoryDiagnostics.LogDelta("After operation", baseline);
///
/// Output appears in VS Debug Output window with [Memory] prefix.
/// </summary>
internal static class MemoryDiagnostics
{
    /// <summary>
    /// Log a memory snapshot with GC heap, committed bytes, working set, and GC generation counts.
    /// </summary>
    [Conditional("DEBUG")]
    public static void LogSnapshot(string label)
    {
        var gcMemory = GC.GetTotalMemory(forceFullCollection: false);
        var gcInfo = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();

        Debug.WriteLine($"[Memory] {label}");
        Debug.WriteLine($"  GC Heap   : {gcMemory / 1024.0 / 1024.0:F1} MB");
        Debug.WriteLine($"  Committed : {gcInfo.TotalCommittedBytes / 1024.0 / 1024.0:F1} MB");
        Debug.WriteLine($"  WorkingSet: {process.WorkingSet64 / 1024.0 / 1024.0:F1} MB");
        Debug.WriteLine($"  Gen0/1/2  : {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
    }

    /// <summary>
    /// Log the delta between a baseline measurement and the current GC heap size.
    /// Forces a full GC collection for accurate measurement.
    /// </summary>
    [Conditional("DEBUG")]
    public static void LogDelta(string label, long baselineBytes)
    {
        var current = GC.GetTotalMemory(forceFullCollection: true);
        var delta = current - baselineBytes;
        Debug.WriteLine($"[Memory] {label}: delta = {delta / 1024.0:F1} KB (total = {current / 1024.0 / 1024.0:F1} MB)");
    }
}
