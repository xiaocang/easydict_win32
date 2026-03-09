# Memory Profiling Guide

This document describes how to profile memory usage in Easydict Win32 using Visual Studio and .NET CLI tools.

## Quick Start: DEBUG Output

Build in **Debug** configuration and run. The `[Memory]` markers in the VS **Output** window (Debug pane) show GC heap, committed bytes, working set, and GC generation counts at key lifecycle points:

```
[Memory] MainPage.OnPageLoaded
  GC Heap   : 12.3 MB
  Committed : 45.6 MB
  WorkingSet: 110.2 MB
  Gen0/1/2  : 5/2/1

[Memory] SettingsPage.OnPageLoaded
  GC Heap   : 14.1 MB
  ...
```

These are emitted by `MemoryDiagnostics.LogSnapshot()` (see `Services/MemoryDiagnostics.cs`). To add more checkpoints:

```csharp
#if DEBUG
MemoryDiagnostics.LogSnapshot("MyComponent.MyMethod");
#endif
```

For delta measurement:

```csharp
#if DEBUG
var baseline = GC.GetTotalMemory(true);
// ... operation ...
MemoryDiagnostics.LogDelta("After operation", baseline);
#endif
```

## Visual Studio Memory Profiler

### Heap Snapshots (Recommended for leak detection)

1. **Debug → Performance Profiler** (Alt+F2)
2. Select **Memory Usage** → Start
3. Take a snapshot (baseline)
4. Navigate to Settings page, navigate back, repeat several times
5. Take another snapshot
6. Compare snapshots — look for growing object counts (especially `PropertyChangedEventHandler`, `ServiceCheckItem`)

### .NET Object Allocation Tracking

1. **Debug → Performance Profiler** (Alt+F2)
2. Select **.NET Object Allocation Tracking** → Start
3. Perform the workflow to profile
4. Stop — analyze allocation hotspots by type and call stack

### Diagnostic Tools (while debugging)

1. Start debugging (F5) in Debug configuration
2. Open **Debug → Windows → Diagnostic Tools** (Ctrl+Alt+F2)
3. Use the **Memory** tab to take heap snapshots at runtime
4. The **Process Memory** graph shows working set over time

## dotnet-counters (Real-time monitoring)

Monitor GC and memory counters without modifying the app:

```powershell
# Install (one-time)
dotnet tool install --global dotnet-counters

# Find the process ID
dotnet-counters ps

# Monitor GC and memory counters
dotnet-counters monitor --process-id <PID> --counters System.Runtime[gc-heap-size,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,working-set]

# Collect to file for later analysis
dotnet-counters collect --process-id <PID> --output memory-counters.csv --format csv --duration 60
```

## dotnet-dump (Heap analysis)

For deeper analysis of what's on the heap:

```powershell
# Install (one-time)
dotnet tool install --global dotnet-dump

# Capture a dump
dotnet-dump collect --process-id <PID> --output easydict.dmp

# Analyze
dotnet-dump analyze easydict.dmp

# Useful SOS commands inside the analyzer:
> dumpheap -stat                    # Summary of all heap objects by type
> dumpheap -type ServiceCheckItem   # Find specific type instances
> gcroot <address>                  # Find what keeps an object alive
```

## CI Memory Regression Tests

Memory budget tests run as part of the `Performance` test category:

```bash
dotnet test tests/Easydict.TranslationService.Tests --filter "Category=Performance" -v n
```

These tests measure GC heap delta for key operations and assert upper bounds. If a test fails, it means a code change increased memory usage beyond the budget — investigate before merging.

## Key Memory Areas to Watch

| Area | Expected | What to check |
|------|----------|---------------|
| TranslationManager creation | < 5 MB heap delta | 20 service instances + caches |
| TranslationManager dispose | < 1 MB retained | No leak after 10 create/dispose cycles |
| Settings page navigation | Stable across visits | No event handler accumulation |
| Screen capture | Temporary 24-90 MB | GDI buffers released after capture |
| Long document translation | Proportional to doc | Full object graph during processing |
