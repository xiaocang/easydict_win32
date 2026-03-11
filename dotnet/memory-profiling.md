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

## SettingsPage Repro Loop (Memory Retention)

Use this exact loop when validating SettingsPage memory behavior:

1. Launch app in Debug.
2. Stay on MainPage and take baseline snapshot.
3. Open Settings, wait for content to reveal, then go back to MainPage.
4. Repeat step 3 at least 5 times.
5. Compare memory after each iteration.

What to compare:

- Managed heap (`GC Heap`): should stay roughly flat and not increase linearly per visit.
- Working set (`WorkingSet`): may spike on first visits, but should flatten instead of growing every loop.
- Object retention in profiler snapshots: check `SettingsPage`, `ServiceCheckItem`, and `PropertyChangedEventHandler` counts.

Expected healthy pattern:

- `GC Heap` remains near a steady-state band across loops.
- `SettingsPage` instance count does not rise linearly.
- Working set growth slows significantly after first one or two visits.

### UIAutomation A/B Switch (MainPage cache impact)

You can run the same Settings loop in two runtime modes without editing source files:

- `EASYDICT_UIA_MEMORY_AB_MODE=A` (default): MainPage cache enabled, lightweight unload.
- `EASYDICT_UIA_MEMORY_AB_MODE=B`: MainPage cache disabled at runtime, unload cleanup enabled.

Extra controls:

- `EASYDICT_UIA_MEMORY_LOOP_ITERATIONS`: number of open/back loops (default `5`).
- `EASYDICT_UIA_MEMORY_IDLE_MS_AFTER_BACK`: extra idle delay after each Back before settled sample (default `1500`).
- `EASYDICT_EXE_PATH`: explicit app exe path for UIAutomation launch.

Example (PowerShell):

```powershell
$env:EASYDICT_EXE_PATH = "C:\path\to\Easydict.WinUI.exe"
$env:EASYDICT_UIA_ALLOW_EXE_FALLBACK = "1"

# A mode (baseline)
$env:EASYDICT_UIA_MEMORY_AB_MODE = "A"
$env:EASYDICT_UIA_MEMORY_LOOP_ITERATIONS = "10"
$env:EASYDICT_UIA_MEMORY_IDLE_MS_AFTER_BACK = "1500"
dotnet test dotnet/tests/Easydict.UIAutomation.Tests/Easydict.UIAutomation.Tests.csproj `
  --filter "FullyQualifiedName~SettingsPage_OpenBackLoop_ShouldSupportMemoryMarkerCollection" `
  --logger "console;verbosity=detailed"

# B mode (compare)
$env:EASYDICT_UIA_MEMORY_AB_MODE = "B"
dotnet test dotnet/tests/Easydict.UIAutomation.Tests/Easydict.UIAutomation.Tests.csproj `
  --filter "FullyQualifiedName~SettingsPage_OpenBackLoop_ShouldSupportMemoryMarkerCollection" `
  --logger "console;verbosity=detailed"
```

The test output includes per-iteration process markers like:

`[MemoryLoop][A_iter_5_after_back] ...` or `[MemoryLoop][B_iter_5_after_back] ...`

It also prints aggregated summaries for two phases:

- `ImmediateBack`: sample taken right after Back navigation.
- `SettledBack`: sample taken after `EASYDICT_UIA_MEMORY_IDLE_MS_AFTER_BACK` delay.

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
