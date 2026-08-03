# Direct XAML compiler (`dxamlc`)

Compiles a strict subset of WinUI 3 XAML into backend-neutral JSON IR plus typed C# slot
accessors. The app loads the embedded IR and paints `MinimalServiceResultItem` cards through one
virtualized Win2D `CanvasVirtualControl` per results host, with the stock XAML card retained as the fallback backend.

## Layout

| Path | Contents |
|---|---|
| `spec/direct-xaml-v0.md` | The frozen v0 language contract. Start here. |
| `spec/compatibility.md` | What survives a move to a direct renderer, and what breaks. |
| `schemas/dxir-v0.schema.json` | Normative JSON Schema for the emitted IR. |
| `schemas/direct-xaml-v0.subset.json` | Machine-readable mirror of the accepted input surface. |
| `crates/dxaml-syntax` | XML lexing, CST, spans, diagnostics. The only crate using `quick-xml`. |
| `crates/dxaml-ast` | Namespace resolution, directives, property elements, markup extensions. |
| `crates/dxaml-schema` | The authoritative v0 control / property / enum tables. |
| `crates/dxaml-hir` | Typed, schema-checked nodes and parsed property values. |
| `crates/dxaml-lower` | HIR → IR, resource interning, invalidation classification. |
| `crates/dxaml-ir` | IR types, serialization, structural validator. |
| `crates/dxaml-cli` | The `dxamlc` driver and the end-to-end tests. |
| `crates/dxaml-codegen-csharp` | Typed C# accessors for the emitted named-slot contract. |

## Build and test

```bash
cd compiler
cargo fmt --all --check
cargo clippy --all-targets -- -D warnings
cargo test
```

Compile the shipping card and generate both artifacts:

```bash
cargo run -p dxaml-cli -- compile \
  --input ../dotnet/src/Easydict.WinUI/Views/Controls/MinimalServiceResultItem.xaml \
  --output ./out \
  --source-root ../dotnet/src/Easydict.WinUI
```

That writes `out/MinimalServiceResultItem.dxir.json` and
`out/MinimalServiceResultItem.bindings.g.cs`. Normal app builds run the same command through
`dotnet/build/DirectXaml.targets`; the packaged native compiler under
`dotnet/build/tools/win-x64/` keeps the .NET build independent of an installed Rust toolchain.

The full rich card is expected to **fail**, which is the subset working as designed:

```bash
cargo run -p dxaml-cli -- compile \
  --input ../dotnet/src/Easydict.WinUI/Views/Controls/ServiceResultItem.xaml --check
```

## Design notes

**The compiler is total.** Every construct is either in the v0 subset or produces a diagnostic.
Nothing is silently ignored, and a document either yields complete IR or none at all. That is what
makes the IR trustworthy enough to render from.

**The IR carries no geometry and no colours.** Layout depends on window size and DPI; colours
depend on the active theme. `{ThemeResource}` compiles to a runtime slot, never a folded value, so
Light/Dark/HighContrast switching keeps working. Resolution on the C# side will reuse the existing
`Services/ThemeResourceService.cs`.

**Generated accessors and typed bindings.** The shipping card uses `x:Name` plus imperative
code-behind, so `x:Name` compiles to a *named slot* carrying the mutable properties and their
invalidation. `dxaml-codegen-csharp` turns that contract into typed methods such as
`SetResultTextText(string?)`. Typed `x:Bind` also lowers to a schema-validated binding table:
`OneTime` applies during context assignment, while `OneWay` subscribes to
`INotifyPropertyChanged`, filters unrelated properties before dispatch, and detaches during
context teardown. The generated glue keeps all UI writes on the configured dispatcher.

**`quick-xml` is quarantined.** It changes API across minor versions, so every call lives in
`crates/dxaml-syntax/src/lexer.rs`, using only `from_str`, `read_event`, `buffer_position` and the
core events, with catch-all match arms for variants added later. Spans are computed here rather
than taken from the library, which does not expose per-attribute positions.

## Goldens

`crates/dxaml-cli/tests/fixtures/MinimalServiceResultItem.dxir.json` is a byte-exact regression
golden. It is created on first `cargo test` — review it and commit it. To accept an intended
change afterwards:

```bash
UPDATE_GOLDEN=1 cargo test
```

The fixture `MinimalServiceResultItem.xaml` is a verbatim copy of the shipping card. If that card
changes, update the copy deliberately; the test suite is meant to notice.

## Implemented vertical slice

- strict XML/CST/AST/HIR/IR compiler with source diagnostics and deterministic goldens
- JSON Schema, runtime capability/version validation, and typed C# accessor generation
- incremental MSBuild generation before XAML/C# compilation
- managed layout, display-list generation, theme-resource re-resolution, and Win2D execution
- one tile-virtualized results surface with per-card named slots, pointer hit testing, and Copy action routing
- cold `DirectRenderer` switch in Minimal theme, with automatic stock-XAML fallback
- resize, device-loss, theme handling, and UI automation visual coverage

## MVP benchmark gate

The original per-card `CanvasControl` vertical slice answered the plan's first performance question,
and the result was **not** a reason to replace the stock card. A deterministic Debug/x64 run on
2026-08-01 at 200% DPI produced:

| Metric | Direct | stock XAML | Result |
|---|---:|---:|---:|
| hosted `FrameworkElement` count per card | 3 | 13 | 77% fewer |
| first visible result, median of 3 | 2,340 ms | 1,883 ms | Direct 24% slower |
| process Private Bytes, median of 3 | 143.8 MiB | 127.1 MiB | app-process private commit +16.7 MiB; GPU/DWM not sampled |
| CPU for 120 paced text updates, median | 2,953 ms | 1,172 ms | Direct 2.5x higher |
| 20-card first visible result, one run | 4,709 ms | 2,175 ms | Direct 2.2x slower |

The outer results `ScrollViewer` preserved a non-zero position through a viewport resize and kept
the twentieth card reachable. That was a correctness pass, not a performance win.

## Shared-surface follow-up

The per-card canvas has been replaced by one `CanvasVirtualControl` per result host. Each card owns
only its compiled view, layout/display-list cache, pointer router, and two transparent automation
peers; the surface owns the Win2D device, text-format cache, region-invalidated drawing, and tile
culling. Reordering changes card offsets without rebuilding a XAML item subtree.
Incremental text snapshots already arrive through `StreamingTextCoalescer` at 16 ms; the surface
adds no second timer. It then invalidates from the earliest changed card through the stable surface
extent, avoiding a repaint above it. Issued invalidation generations remain pending until their own
card is drawn, so an older draw cannot discard a later update. An extent change uses a full
invalidation because WinUI must first apply the new virtual-surface height.


`DirectRendererTests` passes its four current-app UI automation scenarios: painted-card resize,
Copy pointer routing, stock-XAML fallback baseline, and a twenty-card scroll/resize path.

The Light-theme whole-app hotspot and memory runs are retained only as scenario smoke data. They
are not a backend comparison: Direct paints the compiled Minimal card while the non-Minimal XAML
branch creates the rich `ServiceResultItem`.

## Reproducible matched renderer comparison

`dotnet/scripts/memory/Invoke-RendererComparison.ps1` preserves the existing
`Invoke-PrMemoryGate.ps1` assertions and pairs alternating Direct/XAML runs with isolated
settings, a deterministic DEBUG-only result hook, per-PID/LUID GPU Process Memory capture, and
bounded process-CPU samples. It writes the memory-gate output plus `environment.json`, raw
`gpu-process-memory.csv`, renderer marker artifacts, phase snapshots, and
`comparison-summary.json` beneath the requested output directory.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  C:\repo\easydict_win32\dotnet\scripts\memory\Invoke-RendererComparison.ps1 `
  -RunsPerBackend 3 -CardCount 1 -InitialIdleSeconds 5 -PostCloseIdleSeconds 5
```

The earlier 2026-08-01 Debug/x64 20-card run on the Intel integrated adapter completed three
alternating runs per backend with all GPU samples available. At
`07-translation-submitted`, its app-PID medians were:

| Metric | Direct | Minimal XAML | Direct − XAML |
|---|---:|---:|---:|
| Process Private Bytes | 159.44 MiB | 141.16 MiB | +18.28 MiB |
| GPU Process Memory `Total Committed` | 120.05 MiB | 87.33 MiB | +32.73 MiB |
| GPU Process Memory `Shared Usage` | 152.93 MiB | 111.36 MiB | +41.57 MiB |
| GPU Process Memory `Local Usage` | 119.61 MiB | 88.24 MiB | +31.37 MiB |
| GPU Process Memory `Dedicated Usage` | 0 MiB | 0 MiB | 0 MiB |

`Total Committed` is the primary app-GPU comparison. Do **not** add Dedicated, Shared, Local,
Non Local, and Total Committed: they are overlapping counter views, not independent memory pools.
The zero Dedicated value is specific to this integrated-GPU run. DWM is recorded separately as
compositor context and must not be attributed to either backend.

### Critical-path telemetry

The same comparison now writes a result-submission marker immediately before UI refresh. Direct
completes it after the target Win2D card draw returns; XAML completes it on its next
`CompositionTarget.Rendering` callback. It then executes 120 controlled text updates at 50-ms
intervals and restricts `\Process(...)\% Processor Time` to the marker-bounded streaming window.

The later 2026-08-01 Debug/x64, one-card, three-runs-per-backend run produced three usable
observations for each backend:

| Metric | Direct | Minimal XAML | Direct − XAML |
|---|---:|---:|---:|
| First renderer completion, median | 56.44 ms | 30.93 ms | +25.51 ms |
| Streaming process CPU, median of per-run medians | 66.97% | 22.94% | +44.02 percentage points |
| Controlled streaming duration, median | 7.38 s | 7.32 s | same 120 × 50-ms workload |

This is a renderer-callback measure, not a compositor-present timestamp. The CPU counter is raw
process `% Processor Time`, not normalized to logical cores; its one-second sampling excludes
system-wide cost and sub-second scheduler variation. Nevertheless, both matched measurements favor
Minimal XAML; they reinforce the existing decision not to enable Direct by default. Repeat across
comparable hardware/workloads and measure scroll-frame stability before reopening that decision.

The intentionally deferred work remains rich
`ServiceResultItem.xaml`, character-level text selection, arbitrary control templates,
virtualized accessibility peers, compiler watch/IR hot reload, and a native Rust Direct2D runtime.
