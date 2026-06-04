# Easydict Rust UI Refactor

This workspace contains the Rust-side Easydict application layer.

Boundary rules:

- Application-specific UI, state, messages, and window options live under `rs/`.
- `lib/winfluent-rs` stays generic. Do not add Easydict names or workflows to the library crates.
- Easydict UI code depends on `win_fluent` token APIs, not Iced, Win32, COM, HWND, wgpu, or winit types.
- UI parity is locked through token snapshots, accessibility audits, and window option tests before runtime work.

Useful commands:

```powershell
cargo fmt --all --check
cargo test --workspace
cargo check --workspace --all-targets
cargo run -p easydict_app
```

UI preview checks:

```powershell
cargo run -p easydict_preview_iced
$env:EASYDICT_PREVIEW_SCENARIO="long_document_running"; cargo run -p easydict_preview_iced
$env:EASYDICT_PREVIEW_WINDOW="mini"; $env:EASYDICT_PREVIEW_MINI_TRANSLATE_STATE="hovered"; cargo run -p easydict_preview_iced
$env:EASYDICT_PREVIEW_WINDOW="fixed"; $env:EASYDICT_PREVIEW_FIXED_TRANSLATE_STATE="pressed"; cargo run -p easydict_preview_iced
$env:EASYDICT_PREVIEW_WINDOW="capture-overlay"; $env:EASYDICT_PREVIEW_CAPTURE_OVERLAY_STATE="selecting"; cargo run -p easydict_preview_iced
$env:EASYDICT_PREVIEW_WINDOW="pop-button"; $env:EASYDICT_PREVIEW_POPBUTTON_STATE="hovered"; cargo run -p easydict_preview_iced
```

`EASYDICT_PREVIEW_WINDOW` switches the single-window preview runtime to the
logical `main`, `settings`, `mini`, `fixed`, `capture-overlay`, or `pop-button`
surface. `rs/scripts/Capture-PreviewScreenshot.ps1` reads the same variable so
small preview windows such as PopButton and capture overlay are not filtered out.

Rust preview parity matrix:

```powershell
.\scripts\Capture-PreviewParityMatrix.ps1 -ListScenarios
.\scripts\Capture-PreviewParityMatrix.ps1 -Matrix settings -ReferenceRoot ..\artifacts\ui-screenshots -RunAnalyzer -SkipBuild -SkipAnalyzerSelfTest
.\scripts\Capture-PreviewParityMatrix.ps1 -Scenario parity-settings-general-behavior-top -ReferenceRoot ..\artifacts\ui-screenshots -RunAnalyzer -UseDefaultScoreGates -FailOnThreshold -RequireManifest
```

`Capture-PreviewParityMatrix.ps1` launches a fresh Rust preview instance per
scenario, captures analyzer-compatible `*-rust-win-fluent-iced.png` files,
writes view schema and Win32/DPI capture metadata, optionally copies matching
`*-dotnet-winui-reference.png` screenshots, and emits `ui-parity-manifest.json`
for `dotnet/scripts/ci/Invoke-UiParityAnalysis.ps1`. Use `-UiLanguage zh-CN`
for the primary .NET parity baseline and keep `en-US` as a smoke variant.

Rust portable package checks:

```powershell
cargo test -p easydict_packager -- --nocapture
cargo run -p easydict_packager -- zip-directory --source path\to\publish-dir --destination $env:TEMP\easydict-portable.zip --exclude-extension .pdb
cargo run -p easydict_packager -- package-browser-extension --extension-dir ..\browser-extension --target All --output-dir $env:TEMP\easydict-browser-extension
cargo run -p easydict_packager -- extract-dotnet-runtime --rid win-x64 --output-dir path\to\publish-msix\dotnet
cargo run -p easydict_packager -- build-rust-helpers --workspace . --platform x64 --configuration Release --output-dir path\to\publish-dir
.\scripts\Package-Portable.ps1 -Platform x64 -Configuration Release -NoZip
.\scripts\Package-Portable.ps1 -Platform x64 -Configuration Release -PackageVersion v0.0.0-local
```

The first Rust release package is portable-only. It writes
`dist\easydict-rs-portable-...` and uses `Easydict.Rust.exe` as the GUI entry
point so it can sit beside the existing .NET `Easydict.WinUI.exe` package. It
does not produce MSIX, an installer, retained .NET workers, or a bundled .NET
runtime. ZIP creation is owned by `easydict_packager` so portable packaging does
not depend on PowerShell `Compress-Archive`.

Browser extension packages are also created by `easydict_packager` through
`package-browser-extension`. The retained
`browser-extension/scripts/Package-Extension.ps1` entry point is only a shim;
Rust owns manifest JSON parsing, package file whitelisting, `key` stripping for
store submission, and Chrome `.zip` / Firefox `.xpi` archive writing.

`easydict_packager` also owns retained worker shared `.NET` runtime bundling for
the hybrid MSIX profile. `dotnet/scripts/Extract-DotnetRuntime.ps1` is only a
shim to `extract-dotnet-runtime`, which downloads the official runtime ZIP,
extracts the standard `DOTNET_ROOT` layout, strips duplicate license/notice
files, and verifies `host/fxr` plus `shared/Microsoft.NETCore.App`.

`dotnet/scripts/Build-RustHelpers.ps1` is likewise only a shim to
`build-rust-helpers`. The Rust packager owns target triple mapping, helper bin
selection, copy validation, and the legacy `BrowserHostRegistrar.exe` alias.

UI parity analyzer smoke checks:

```powershell
cargo test -p easydict_ui_parity_analyzer -- --nocapture
cargo run -p easydict_ui_parity_analyzer -- --self-test
cargo run -p easydict_ui_parity_analyzer -- screenshot-summary --screenshot-root path\to\ui-screenshots --artifact-name ui-screenshots-local --summary-path $env:TEMP\easydict-ui-screenshot-summary.md
..\dotnet\scripts\ci\Invoke-UiParityAnalysis.ps1 -ScreenshotRoot path\to\ui-screenshots -OutputDir path\to\ui-screenshots\ui-parity
```

`easydict_ui_parity_analyzer` is the Rust replacement for the old
`dotnet/tools/UiParityAnalyzer` helper. It keeps the manifest/filename pair
contract, writes the parity report, coverage report, threshold policy, and LLM
review prompt artifacts, preserves the wrapper's score/coverage gate flags, and
also backs `dotnet/scripts/ci/Publish-UiScreenshotSummary.ps1` through the
`screenshot-summary` subcommand.

Build-time icon generator smoke checks:

```powershell
cargo test -p easydict_icon_generator -- --nocapture
cargo run -p easydict_icon_generator -- --source-png ..\dotnet\src\Easydict.WinUI\Assets\macos\white-black-icon.appiconset\icon_512x512@2x.png --output-ico $env:TEMP\easydict-appicon-test.ico --output-tray-png $env:TEMP\easydict-trayicon-test.png
cargo run -p easydict_icon_generator -- windows-assets --source-icon ..\dotnet\src\Easydict.WinUI\Assets\macos\white-black-icon.appiconset\icon_512x512@2x.png --unplated-icon ..\dotnet\src\Easydict.WinUI\Assets\icon_unplated_1024.png --output-dir $env:TEMP\easydict-windows-assets
```

`easydict_icon_generator` is the Rust replacement for the old
`dotnet/scripts/generate-app-icon-ico.ps1` drawing path. It generates the WinUI
`AppIcon.ico` and `TrayIcon.png` assets during the MSBuild `GenerateAppIconIco`
target, and its subcommands back the retained `generate-windows-assets.ps1`,
`generate-assets-from-macos-icon.ps1`, and `convert-service-icons.ps1` script
entry points.

MSIX validator smoke checks:

```powershell
cargo test -p easydict_msix_validate -- --nocapture
cargo run -p easydict_msix_validate -- path\to\package.msix --allow-unsigned
cargo run -p easydict_msix_validate -- fix-minversion path\to\package.msix
cargo run -p easydict_msix_validate -- verify-bundle-minversion path\to\bundle.msixbundle
cargo run -p easydict_msix_validate -- dedupe-worker-shared path\to\publish-dir
cargo run -p easydict_msix_validate -- prepare-package-inputs --platform x64 --publish-dir path\to\publish-msix --manifest ..\dotnet\src\Easydict.WinUI\Package.appxmanifest --output-manifest $env:TEMP\Package.x64.appxmanifest --msix-version 1.2.3.4 --verify-targetsize-icons
```

`easydict_msix_validate` owns package identity/min-version/signature checks,
payload layout policy, Rust-only runtime profile checks, the MSIX MinVersion
fixer, release bundle MinVersion validation, and retained worker shared-file
dedupe. Bundle validation reads the outer `.msixbundle` and each nested
`.appx`/`.msix` directly through the Rust ZIP/XML path instead of PowerShell
archive extraction; dedupe uses Rust SHA-256 hashing instead of PowerShell
`Get-FileHash`/`Remove-Item` logic. Package input preparation verifies required
MSIX assets, normalizes `resources.pri`, and rewrites only the manifest
`Identity` architecture/version through `quick-xml`, leaving `winapp package` as
the external packager.

Store listing smoke checks:

```powershell
cargo test -p easydict_store_listings -- --nocapture
cargo run -p easydict_store_listings -- validate --winstore-root ..\.winstore
cargo run -p easydict_store_listings -- preview --winstore-root ..\.winstore --languages en-us
cargo run -p easydict_store_listings -- summary --winstore-root ..\.winstore --action validate
```

`easydict_store_listings` owns Microsoft Store listing YAML parsing,
validate/preview output, GitHub Actions summary generation, and submit JSON
payload creation. The retained `.winstore/scripts/Sync-StoreListings.ps1` entry
point is only a Cargo shim. Submit still calls the external `msstore` CLI, but
validate/preview/summary no longer need `powershell-yaml` or PowerShell JSON/YAML
conversion.

NLLB/OpenVINO core checks:

```powershell
cargo test --manifest-path ..\lib\easydict-nllb\Cargo.toml -- --nocapture
cargo test --manifest-path ..\lib\easydict-nllb\Cargo.toml --features ort-openvino ort_engine -- --nocapture
```

`lib/easydict-nllb` owns the HuggingFace tokenizer, NLLB/FLORES language
mapping, OpenVINO cache manifest, and the feature-gated `ort-openvino` ONNX
session engine. The library feature is off by default; `easydict_app` enables it
for cache-ready explicit OpenVINO Quick Translate so the Rust route can construct
`HuggingFaceNllbTokenizer + OrtNllbInferenceEngine` directly. The `ort`
dependency uses dynamic loading, so Rust builds do not download or bundle ONNX
Runtime binaries.

```powershell
cargo test -p easydict_app --test quick_translate_behavior openvino -- --nocapture
```

Command-line translation smoke checks:

```powershell
cargo test -p easydict_app --test cli_translate_behavior -- --nocapture
cargo run -p easydict_app --bin easydict_cli -- --help
cargo run -p easydict_app --bin easydict_cli -- translate --service google --from en --to zh-Hans --text "Hello" --json
cargo run -p easydict_app --bin easydict_cli -- stream --service google --from en --to zh-Hans --text "Good morning" --json
cargo run -p easydict_app --bin easydict_cli -- grammar --service openai --language en --text "I has a apple."
echo Hello | cargo run -p easydict_app --bin easydict_cli -- translate --service google --to zh-Hans --text -
"Hello`nGood morning" | cargo run -p easydict_app --bin easydict_cli -- batch --service google --from en --to zh-Hans --text - --json
```

`easydict_cli` runs supported services through Rust-native routes. The rs app
and CLI no longer start the retained `workers/localai` payload from default
packaged Quick Translate routes; `--host` and `--app-dir` remain accepted only
as legacy no-op compatibility options.
Auto LocalAI still probes a running Foundry Local endpoint first, but requests
can now fall back to the Rust-native OpenVINO NLLB route when the model/runtime
cache is complete. Requests that still need the retained `.NET` worker fail
locally with a Rust-native-route requirement. The
`batch` command treats each non-empty input line as a separate translation
request and emits one JSON Line per result when `--json` is set.

Long document CLI smoke checks:

```powershell
cargo run -p easydict_app --bin easydict_long_doc -- --list-services
..\scripts\translate-long-doc.ps1 -ListServices -UseCargo
..\scripts\translate-long-doc.ps1 -InputFile path\to\input.md -TargetLanguage zh-Hans -Service google -OutputMode bilingual -UseCargo
..\scripts\translate-long-doc.ps1 -InputFile path\to\input.pdf -TargetLanguage zh-Hans -Service google -PageRange 1-3 -AppDir path\to\packaged-app
```

`..\scripts\translate-long-doc.ps1` defaults to the Rust long document helper
`easydict_long_doc.exe`. In a source checkout without a built helper, it falls
back to development mode via `cargo run -p easydict_app --bin easydict_long_doc`.
Use `-RustHelperPath` or `-AppDir` to pin a packaged helper, and use `-UseCargo`
when intentionally exercising the development binary. The Rust helper accepts
`--list-services`, `--input`, `--target-language`, `--from`, `--output`,
`--service`, `--output-mode`, `--layout`, `--pdf-export-mode`, `--page`,
`--page-range`, `--max-concurrency`, `--env-file`, `--vision-endpoint`,
`--vision-api-key`, `--vision-model`, and `--app-dir`.

The old WinUI debug entry point is legacy-only now. Pass `-UseDotnetLegacy`
explicitly only when you need to compare against the previous .NET debug CLI.

Sidecar IPC smoke checks:

```powershell
cargo test -p easydict_app --test sidecar_ipc_e2e -- --nocapture
python ..\sidecar_mock\e2e_client.py
```

`sidecar_ipc_e2e` launches the existing Python JSON Lines mock directly and
covers health, translate, unknown-method errors, 10 concurrent in-flight
requests, per-request timeout, crash exit code, shutdown, and stderr log
collection without running the old `.NET` E2E console app.

Browser Native Messaging registrar smoke checks:

```powershell
cargo run -p easydict_app --bin easydict_browser_registrar -- --help
cargo run -p easydict_app --bin easydict_browser_registrar -- status
cargo build -p easydict_app --bin easydict-native-bridge
cargo run -p easydict_app --bin easydict_browser_registrar -- install --chrome --bridge-path target\debug\easydict-native-bridge.exe
cargo run -p easydict_app --bin easydict_browser_registrar -- uninstall --chrome
```
