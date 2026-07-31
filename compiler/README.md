# Direct XAML compiler (`dxamlc`)

Compiles a strict subset of WinUI 3 XAML into a backend-neutral UI IR, so a translation-result
card can eventually be painted directly instead of being built as a `FrameworkElement` tree.

**This is the compiler front-end only.** There is no runtime, no Win2D executor, no MSBuild
integration, and nothing in `dotnet/` depends on it yet. It builds and tests entirely on its own.

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

## Build and test

```bash
cd compiler
cargo fmt --all --check
cargo clippy --all-targets -- -D warnings
cargo test
```

Compile the shipping card:

```bash
cargo run -p dxaml-cli -- compile \
  --input ../dotnet/src/Easydict.WinUI/Views/Controls/MinimalServiceResultItem.xaml \
  --output ./out
```

That writes `out/MinimalServiceResultItem.dxir.json`. The full card is expected to **fail**, which
is the subset working as designed:

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

**Named slots, not bindings.** The app contains zero `x:Bind` — all 12 XAML files use `x:Name`
plus imperative code-behind. So `x:Name` compiles to a *named slot* carrying the set of properties
that may be written at runtime and what each write invalidates. A test in `crates/dxaml-cli` pins
that set against every property `MinimalServiceResultItem.UpdateUI()` actually writes.

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

## Not done yet

MSBuild integration, C# accessor codegen, the runtime, layout, hit testing, virtualization, the
automation tree, and hot reload. `spec/compatibility.md` also records the open functional gaps —
the largest is text selection, which the current cards enable and a painted card would lose.

Whether the runtime work is worth doing is a measurement question, not an architectural one. The
app already has the instrumentation to answer it: `dotnet/scripts/memory/Invoke-PrMemoryGate.ps1`,
`Easydict.UIAutomation.Tests/Tests/MemoryGateTests.cs`, and the
`UiThreadHotspotDiagnostics.Measure("MinimalServiceResultItem.UpdateUI")` marker already wrapping
the method a direct renderer would replace.
