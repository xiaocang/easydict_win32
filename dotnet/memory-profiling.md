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

`SettingsPage` now also emits two DEBUG-only helper streams during open/back loops:

- `[SettingsPage][Lifetime]`: instance ID, explicit global counts such as `globalLiveInstances` and `globalSurvivorsAfterLastTrackedFullGC`, plus delayed-check fields that say whether the specific unloaded page instance is still alive.
- `[SettingsPage][Objects]`: counts for service collections, language items, nav icons, MDX panel children, credential field cache entries, frame back stack depth, and deferred I/O state.
- `[SettingsPage][DeferredIO]`: explicit deferred I/O state transitions such as `queued`, `onnx-running`, `cache-running`, `cache-complete`, or `cache-canceled`.

`MainPage` also emits a DEBUG helper stream during the same workflow:

- `[MainPage][Objects]`: counts for `_serviceResults`, `_resultControls`, `ResultsPanel.Items`, long-document combo/history state, current mode, active A/B mode, whether result rebuild skipping is enabled, and the current rebuild reason in `InitializeServiceResults`.

Use them together:

- `globalLiveInstances` or `globalSurvivorsAfterLastTrackedFullGC` keeps rising across clean runs -> page retention is real.
- `trackedInstanceAliveAfterDelayedFullGC=false` with `liveInstances=1` or `globalLiveInstances=1` -> do not call it a leak yet; that often just means a newer `SettingsPage` was opened before the delayed check ran.
- `trackedInstanceAliveAfterDelayedFullGC=true` at the `1000ms` delayed check -> stronger evidence that the specific unloaded page is still retained.
- Object counts stay flat but working set climbs then plateaus -> more likely WinUI/native cache warm-up than a managed event-chain leak.
- `SettingsPage` counts return to 0 but `MainPage` result-control counts are rebuilt on every return -> prioritize `MainPage`/`ServiceResultItem` lifecycle over Settings teardown.

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

1. For manual profiling, set `EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE=1` before launching.
2. Launch app in Debug.
3. Stay on MainPage and take baseline snapshot.
4. Open Settings, wait for content to reveal, then go back to MainPage.
5. Repeat step 4 at least 5 times.
6. Compare memory after each iteration.

During the loop, avoid drag-selecting text in Terminal, browsers, or the app itself. If you need repeated runs, prefer the UIAutomation loop over manual interaction.

What to compare:

- Managed heap (`GC Heap`): should stay roughly flat and not increase linearly per visit.
- Working set (`WorkingSet`): may spike on first visits, but should flatten instead of growing every loop.
- Object retention in profiler snapshots: check `SettingsPage`, `ServiceCheckItem`, and `PropertyChangedEventHandler` counts.
- DEBUG helper output: `globalSurvivorsAfterLastTrackedFullGC`, service collection counts, language item count, nav icon count, and back stack depth should stop trending upward after the page is closed.
- MainPage helper output: `_serviceResults`, `_resultControls`, and `ResultsPanel.Items` should not accumulate unexpectedly; the current healthy path produces only one result-panel rebuild per load cycle, and it should be `reason=OnPageLoaded`.
- SettingsPage delayed lifetime checks: compare both `250ms` and `1000ms`, but interpret `trackedInstanceAliveAfterDelayedFullGC` first. If that field is `false`, a non-zero global `liveInstances` count can just mean a newer Settings page exists.

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
- `EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE=1`: disable mouse hook / pop button profiling noise during manual runs in DEBUG.
- `EASYDICT_DEBUG_DISABLE_SETTINGS_DEFERRED_IO=1`: skip Settings deferred ONNX/cache status work in DEBUG.
- `EASYDICT_DEBUG_DISABLE_MAINPAGE_RESULT_REBUILD=1`: keep existing MainPage result controls on return, instead of rebuilding them, in DEBUG.

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

### Second-Round Isolation Workflow

Use this order once instance-level delayed checks stay `false` at `1000ms` and manual input-hook noise is disabled or absent:

1. Run `A` mode with current settings and confirm each `MainPage.OnPageLoaded` now shows only one `InitializeServiceResults` pass with `reason=OnPageLoaded`.
2. Run the same loop in `B` mode and compare the tail slope of `WorkingSet`.
3. Repeat `A` and `B` with `EASYDICT_DEBUG_DISABLE_SETTINGS_DEFERRED_IO=1`.
4. Repeat `A` with `EASYDICT_DEBUG_DISABLE_MAINPAGE_RESULT_REBUILD=1`.
5. Repeat `A/B` with a clean `settings.json` that omits `ImportedMdxDictionaries`.

Interpretation:

- `trackedInstanceAliveAfterDelayedFullGC=false` across repeated unloads -> treat `SettingsPage` managed leakage as excluded unless a later run produces `true@1000ms`.
- `SettingsPage` survivors stay at `0`, but `A` mode keeps climbing more than `B` -> cached `MainPage` reuse and result-control rebuild are the primary suspects.
- `trackedInstanceAliveAfterDelayedFullGC=false` while `liveInstances` or `globalLiveInstances` is non-zero -> treat that run as overlap with a newer `SettingsPage`, not proof that the unloaded page leaked.
- `trackedInstanceAliveAfterDelayedFullGC=true` at `250ms` but `false` at `1000ms` -> prefer async tail / dispatcher lag over real leak.
- `trackedInstanceAliveAfterDelayedFullGC=true` at `1000ms` for the same unloaded instance -> treat that as real evidence of delayed retention and investigate page-specific references again.
- Disabling Settings deferred I/O reduces the first few jumps -> ONNX or cache warm-up is a major contributor.
- Disabling MainPage result rebuild flattens the curve -> prioritize `MainPage` / `ServiceResultItem` cleanup and native control lifetime.
- Removing imported MDX dictionaries materially improves the curve -> treat MDX result UI or MDX-backed WebView usage as a separate investigation lane.

Hard failure condition:

- If the same load cycle still logs both `reason=ApplyModeState` and `reason=OnPageLoaded`, the MainPage double-rebuild fix has regressed and that run should not be used for memory conclusions.
- If `trackedInstanceAliveAfterDelayedFullGC=false` for an unloaded page but the run is still labeled as a retained old `SettingsPage`, the delayed-GC interpretation is wrong and that run should not be used for leak conclusions.

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
