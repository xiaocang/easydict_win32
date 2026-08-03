# Direct XAML — compatibility

What survives a move to the direct renderer, what breaks, and what the two real translation-result
cards would cost to port. Written against the state of the tree at the time v0 was frozen.

## The seam

There is no need for a new backend abstraction. `Views/Controls/IServiceResultView.cs` already is
one, and `Views/Controls/ServiceResultViewHost.cs:19` already swaps implementations:

```csharp
IServiceResultView control = MinimalThemeService.IsActive
    ? new MinimalServiceResultItem()
    : new ServiceResultItem();
```

A direct renderer is a **third implementation of `IServiceResultView`**. Everything above the
seam — `MainPage`, reordering, sticky headers, phonetic dedup, automation properties, appearance
refresh — keeps working untouched, because it only ever talks to the interface.

Two members of that interface are the whole compatibility question:

- `FrameworkElement Element { get; }` — the host inserts this into an `ItemsControl` and calls
  `TransformToVisual` on it. A direct renderer must therefore still expose **one** real
  `FrameworkElement` (the Win2D canvas host). The saving is the subtree beneath it, not the
  element itself.
- `FrameworkElement HeaderPanel { get; }` — `UpdateStickyHeaders` sets `.Translation` on it per
  scroll frame. The Direct implementation exposes the canvas as this compatibility surface, so
  scrolling moves the painted card without requiring a per-node `FrameworkElement`.

## Source compatibility

**Keeps working unchanged.** `ServiceQueryResult` and its `INotifyPropertyChanged` properties; the
`QueueUpdateUI` / `_updateUIRequestVersion` coalescing logic; `ServiceResultStatusTextProvider`;
`ServiceResultDemotionHelper`; `AppearanceService` snapshots; `ThemeResourceService` lookups; every
`UiThreadHotspotDiagnostics.Measure` marker.

**Ports mechanically.** `MinimalServiceResultItem` writes `.Text`, `.Content`, `.Visibility`,
`.Foreground`, `.Opacity`, and `.FontSize`. Those are precisely the runtime-mutable set v0
defines, so each `Element.Property = value` becomes a generated typed slot write.

**Does not survive.** Anything that treats a named element as a real WinUI control:

```csharp
var brush = ResultText.Foreground;              // reading back a DependencyProperty
element.Focus(FocusState.Programmatic);         // per-node focus
VisualTreeHelper.GetChild(RootBorder, 0);       // walking into the subtree
ResultsPanel.Children.Add(...);                 // mutating structure at runtime
```

The generated accessor type exposes typed setters, not `TextBlock` instances. Structure is fixed
at compile time; only property values vary at runtime.

## Behavioural regressions to accept or fix

**Text selection.** `IsTextSelectionEnabled="True"` appears 7 times across the two cards, including
on the result and error text. Character-level selection inside a Win2D-painted card does not exist
until it is written: hit-testing to a character index, a selection model, highlight painting, and
clipboard integration. In a translation app, selecting part of a result is a normal action, so
this is the single largest functional gap. v0 records the property in the IR so the gap is
explicit and greppable rather than silently dropped.

**Sticky headers.** See above.

**Per-node automation.** `ApplyAutomationProperties` sets an `AutomationId`/`Name` on
`control.Element` and `control.HeaderPanel`. Both still exist on a direct card, so the current
automation surface is preserved — but nothing *inside* the card is reachable by UIA until the IR's
`semantics` table is wired to a virtual automation peer. Existing UI automation tests locate cards
by `ServiceResultItem_<serviceId>`, which continues to work.

**High contrast.** Because `{ThemeResource}` compiles to a runtime slot rather than a folded
colour, high-contrast switching keeps working — provided the renderer re-resolves slots on theme
change, as `RefreshThemeChrome` already does for the XAML path.

## Port cost of the two real cards

| | `MinimalServiceResultItem.xaml` | `ServiceResultItem.xaml` |
|---|---|---|
| Lines of XAML | 100 | 350 |
| Code-behind | 315 | 2383 |
| `x:Name` | 9 | 36 |
| Bindings | 1 constrained command x:Bind | 0 |
| Elements outside v0 | none | `FontIcon`, `Image`, `ProgressRing`, `ScrollViewer`, `HyperlinkButton` |
| Attached props outside v0 | none | `ToolTipService.ToolTip`, `AutomationProperties.*` |
| Verdict | **compiles under v0** | **rejected by v0, by design** |

The minimal card is the whole v0 target. The full card defines the v0.1 backlog, and its 2383-line
code-behind — WebView2 dictionary rendering, phonetics, speech, per-service action buttons — is a
much larger port than the markup suggests.

## Measurement gate

The vertical slice remains intentionally confined to the minimal result card until measurement shows
that the custom runtime pays for itself. A Direct result host contributes one shared root containing
one `CanvasVirtualControl` and its automation layer; it does not create a `CanvasControl` per card.
Each card keeps only compiled state, an automation proxy, and cached layout/display-list data.
`DirectXamlBuildIntegrationTests` checks the shared-host shape when a desktop test window is
available, while `DirectRendererTests` verifies painted-card resize, Copy hit testing, XAML fallback,
and a twenty-card scroll/resize path in a real app process.

Benchmark Direct and stock XAML under the same **Minimal** theme. Minimal chrome deliberately hides
the mode selector, so the UI-hotspot and memory scenarios accept
`EASYDICT_UI_HOTSPOT_SKIP_MODE_TRANSITIONS=1` and
`EASYDICT_MEMORY_GATE_SKIP_MODE_TRANSITIONS=1` for a matched card-only path.

A DEBUG-only deterministic result hook now records the missing critical-path signals. It writes a
submission marker immediately before the result refresh; Direct completes it after the target
Win2D card draw returns, while XAML completes it on the next `CompositionTarget.Rendering`
callback. The same hook sends a paced 120-update, 50-ms streaming sequence and bounds
per-process `\Process(...)\% Processor Time` samples between explicit start and completion
markers.

`Invoke-RendererComparison.ps1` retains the PR memory-gate assertions while alternating
Direct/XAML scenarios, isolating settings, and collecting per-PID/LUID GPU Process Memory,
first-result markers, and matched streaming CPU samples. The 2026-08-01 Debug/x64, one-card,
three-runs-per-backend result produced three usable observations for every new metric:

| Metric | Direct | Minimal XAML | Direct − XAML |
|---|---:|---:|---:|
| First renderer completion, median | 56.44 ms | 30.93 ms | +25.51 ms |
| Streaming process CPU, median of per-run medians | 66.97% | 22.94% | +44.02 percentage points |
| Controlled streaming duration, median | 7.38 s | 7.32 s | same 120 × 50-ms workload |

`Total Committed` remains the primary app-GPU comparison; Dedicated, Shared, Local, Non Local,
and Total Committed are overlapping counter views and must not be summed. DWM/compositor samples
are recorded separately as context and must not be attributed to either backend.

This closes the prior gap for first-renderer completion and matched streaming CPU, but it is not a
compositor-present measurement. CPU is raw process `% Processor Time`, not normalized to logical
cores; its one-second cadence cannot describe sub-second scheduler variation. The matched result is
evidence against enabling Direct by default: it is slower at the observed renderer callback and
consumes more process CPU for the same controlled stream. Keep the slice contained until broader
hardware/workload trials and scroll-frame stability show a net benefit.
