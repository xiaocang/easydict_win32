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
  scroll frame. A direct renderer has no per-node `FrameworkElement`, so sticky headers must be
  reimplemented as a paint-time offset. Until then, a direct card must report
  `ActionButtonsPanel => null`, which `UpdateStickyHeaders` already skips.

## Source compatibility

**Keeps working unchanged.** `ServiceQueryResult` and its `INotifyPropertyChanged` properties; the
`QueueUpdateUI` / `_updateUIRequestVersion` coalescing logic; `ServiceResultStatusTextProvider`;
`ServiceResultDemotionHelper`; `AppearanceService` snapshots; `ThemeResourceService` lookups; every
`UiThreadHotspotDiagnostics.Measure` marker.

**Ports mechanically.** `MinimalServiceResultItem.UpdateUI()` writes exactly six property kinds —
`.Text`, `.Visibility`, `.Foreground`, `.Opacity`, and (via `ApplyAppearance`) `.FontSize`. Those
are precisely the runtime-mutable set v0 defines, so the method body survives with each
`Element.Property = value` becoming `slots.SetProperty(value)`.

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
| Lines of XAML | 88 | 350 |
| Code-behind | 288 | 2383 |
| `x:Name` | 8 | 36 |
| Bindings | 0 | 0 |
| Elements outside v0 | none | `Button`, `FontIcon`, `Image`, `ProgressRing`, `ScrollViewer`, `HyperlinkButton` |
| Attached props outside v0 | none | `ToolTipService.ToolTip`, `AutomationProperties.*` |
| Verdict | **compiles under v0** | **rejected by v0, by design** |

The minimal card is the whole v0 target. The full card defines the v0.1 backlog, and its 2383-line
code-behind — WebView2 dictionary rendering, phonetics, speech, per-service action buttons — is a
much larger port than the markup suggests.

## What this does not answer

Whether any of it is worth doing. That question is settled by measurement, not architecture, and
the infrastructure already exists: `dotnet/scripts/memory/Invoke-PrMemoryGate.ps1` (PR gate,
160 MB absolute allowance), `Easydict.UIAutomation.Tests/Tests/MemoryGateTests.cs`, and the
`UiThreadHotspotDiagnostics.Measure("MinimalServiceResultItem.UpdateUI")` marker already wrapping
the exact method a direct renderer would replace.

The numbers to compare, before committing to the runtime work: `FrameworkElement` count per card,
idle Private Bytes with N service results open, time to first paint of a result, and CPU during
streaming token updates.
