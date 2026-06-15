# Migration Experience

## Iteration Closure

- Keep each migration slice tied to a minimal validation matrix before editing: one formatter check, the smallest behavior/unit tests that prove the changed boundary, and a `gstep diff` scope check before checkpointing. Broader suites should be deliberate risk expansion, not the default closing move.
- When parallel UI work is dirty, isolate those files before Rust core tests and restore them immediately after `gstep commit`; do not let test setup/restoration become an implicit extra refactor.
- Use `rs/scripts/Invoke-RsCoreSliceValidation.ps1` for one-command core validation when known parallel UI/parity files are dirty. Keep custom invocations scoped to a single `rustfmt` or targeted `cargo test`, so failures still point at the current slice instead of a bundled ad hoc suite. When the child command has PowerShell-looking flags such as cargo's `-p`, pass it with an argument array splat (`$cmdArgs = @('cargo', 'test', '-p', 'easydict_app', ...); ...ps1 @cmdArgs`); raw `-File ... cargo -p ...` lets PowerShell capture `-p` before the wrapper sees it.
- When using the validation wrapper to run a custom child command that has short flags, put PowerShell's argument terminator before the child command: `.\rs\scripts\Invoke-RsCoreSliceValidation.ps1 -- gstep commit -m "..."`. Without `--`, `-m` can bind to the wrapper's `-MaxRecommendedProfiles` parameter instead of reaching `gstep`. If launching from another PowerShell process, use `-Command "& .\rs\scripts\Invoke-RsCoreSliceValidation.ps1 -- gstep commit -m '...'"`; `powershell -File ... -- ...` can treat `--` as an ambiguous wrapper parameter before the script sees it.
- Start close-out with `Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles` when a slice is already dirty, or pass planned files with `-ChangedPath` before editing. It computes the same recommendation as `-RecommendProfiles`, selects the top profile by default, and runs that profile under a single UI/parity isolation pass. Use `-DryRun` to inspect the selected steps without taking the isolation lock or running cargo.
- Use `Invoke-RsCoreSliceValidation.ps1 -RecommendProfiles` only when you want to inspect all matching lanes before deciding. The recommender ignores known parallel UI/parity files plus profile-exempt docs, then prints the aligned profile commands that match the core paths and diff keywords. If the changed paths are only the validation tooling, only the tooling self-test lane should be recommended.
- For repeated close-out lanes, use `Invoke-RsCoreSliceValidation.ps1 -ListProfiles` and then `-Profile <name>`. Profiles isolate the parallel UI/parity files once, automatically enable `RUST_TEST_NOCAPTURE`, and run the aligned tests for that lane (`core-validation-tooling`, `desktop-settings`, `settings-credentials`, `builtin-ai-registration`, `openai-compatible`, `custom-streaming`, `traditional-http`, `foundry-local`, `openvino-download`, `windows-ai-native`, `windows-ai-prepare`, `browser-support`, `native-bridge`, `protocol-facade`, `input-actions`, `tts`, `file-dialog`, `text-selection`, `mouse-selection`, `ocr-diagnostics`, `longdoc-layout`, `longdoc-export`, `longdoc-formula`, `mdx-native`, `local-dictionary-suggestions`, `rs-portable-release`, or `rust-only-boundary`). Add a new profile and recommendation rule in the same slice when the same boundary needs both app reducer/lifecycle coverage and lower-level Rust contract coverage; one-off exploration should remain a custom command.
- When a second close-out profile catches a regression that belongs to the first recommended lane, move that focused test into the lane before checkpointing. In particular, Foundry Local route work should cover the packaged Auto LocalAI stale app-dir boundary inside `foundry-local`, so `-RunRecommendedProfiles` catches stale `.NET` worker probing and bad Foundry fake-helper fixtures in the first pass.
- Before checkpointing a slice that touches routing, process launch, packaged app-dir handling, or default CLI/LongDoc behavior, run `-Profile rust-only-boundary` instead of hand-picking no-runtime smoke tests. It is intentionally smaller than the full release gate but covers the common ways `.NET` runtime probing sneaks back into default rs paths; keep its dry-run self-test broad enough to include runtime policy, source/process scans, CLI, GUI stale app-dir, and LongDoc inherited-env boundaries.
- Do not intentionally run multiple core-slice wrapper invocations in parallel. The wrapper now serializes isolation with a named mutex, but parallel cargo/rustfmt checks still waste time and make outputs harder to read; run formatter and cargo profile/check commands sequentially. If `rs/Cargo.lock` is dirty only because a known parallel UI/parity `Cargo.toml` is dirty, the wrapper temporarily isolates that lockfile too; if a core slice intentionally changes dependencies, keep that manifest/lockfile in the slice and do not rely on UI isolation to hide it. Direct `cargo test --manifest-path` runs against standalone helper crates can generate crate-local `Cargo.lock` files; list those as known generated lock drift in the wrapper so recommendations and checkpoints are not polluted.

## Desktop Shell And Integration

- Desktop shell changes are process-boundary changes, not only reducer diagnostics. Close them with `Invoke-RsCoreSliceValidation.ps1 -Profile desktop-settings` so `lib/easydict-windows-shell`, app desktop integration registry plans, app shell/process boundary scans, and settings error prefixes move together.
- Keep desktop profile filters exact. A broad `default_api_boundary_behavior shell` filter also runs WinFluent backend shell-surface scans; when parallel UI/backend files are intentionally isolated to `gstep:@`, that can fail against the wrong baseline and slow every core close-out.
- When parallel UI files are dirty, do not run app cargo tests directly for shell/desktop slices. Use the wrapper so `win_fluent` and `easydict_app` compile against the same isolated baseline; otherwise unrelated UI token/schema drift can look like a core desktop failure.

## Browser Bridge

- Browser support spans app diagnostics, `easydict_browser_registrar`, extension source defaults, and browser-extension packaging. Close browser-support changes with `Invoke-RsCoreSliceValidation.ps1 -Profile browser-support`, and keep registrar behavior plus extension release/package scanning in that lane; a narrow app `browser_support` reducer test can miss legacy host fallback or retained runtime marker regressions.

## Packaging And Release

- First rs portable release changes should close with `Invoke-RsCoreSliceValidation.ps1 -Profile rs-portable-release`. Keep recommendation paths pointed at the real workflow file, `.github/workflows/release-publish.yml`; stale names such as `release.yml` make release workflow edits depend on incidental diff keywords and can route them to the broader rust-only boundary lane.

## Native Bridge

- Native Messaging changes span the frame parser/binary, `lib/easydict-windows-ipc` named-event signal/listener helper, and the app-owned OCR named-event receiver stream. Close them with `Invoke-RsCoreSliceValidation.ps1 -Profile native-bridge`; a standalone `native_bridge_behavior` run can miss a regression back to WinFluent `Subscription::named_event`.

## Protocol Facade

- Protocol DTO or retained-worker wire-shape changes should close with `Invoke-RsCoreSliceValidation.ps1 -Profile protocol-facade`. A default `protocol_behavior` run can miss crate-root feature gates or default manifest drift that would expose retained `.NET` worker protocol surfaces in the rs build.

## Settings And Credentials

- Settings or credential changes should close with `Invoke-RsCoreSliceValidation.ps1 -Profile settings-credentials`. A focused storage/migration test can miss DPAPI wrapper drift, settings-save diagnostics, or default settings-path scans that keep retained `.NET` runtime markers out of rs settings code.

## Input Actions

- Clipboard/text-insertion changes should close with `Invoke-RsCoreSliceValidation.ps1 -Profile input-actions`, including the shared `lib/easydict-windows-text-selection` helper contracts. Do not make that shared helper path exclusively drive the input-actions recommendation, because the same file also owns selected-text and mouse-hook behavior; use app clipboard/text-insertion paths or focused diff keywords to avoid stealing the more specific lanes.

## Text Selection

- Selected-text capture changes should close with `Invoke-RsCoreSliceValidation.ps1 -Profile text-selection`, including the shared `lib/easydict-windows-text-selection` helper contracts plus app-level Result/diagnostic plumbing. A narrow `text_selection_behavior` or Quick Translate filter can miss helper crate regressions in UIA, foreground classification, clipboard sequence/restore, or synthetic Ctrl+C boundaries.
- Keep recommendation rules app-path and diff-keyword focused for selected text. The helper crate path is shared with clipboard/text-insertion and mouse hooks, so adding it broadly to `text-selection` recommendations can send unrelated input-action or hook work down the wrong close-out lane.

## Text To Speech

- TTS changes should close with `Invoke-RsCoreSliceValidation.ps1 -Profile tts`, not just the SAPI helper crate. The app facade, Speak action, AutoPlayTranslation routing, and legacy PowerShell/System.Speech feature boundary need to move together or the default rs route can look green while the old backend surface reopens.

## File Dialogs

- File dialog changes span the native COM helper, app facade, Quick Translate MDX import routing, and LongDoc browse routing/diagnostics. Close them with `Invoke-RsCoreSliceValidation.ps1 -Profile file-dialog`; a narrow MDX or LongDoc diagnostic test can miss a fallback to WinFluent `Task::OpenFileDialog` / `Task::OpenFolderDialog`.
- Standalone dialog helper tests may create `lib/easydict-windows-dialogs/Cargo.lock`; keep that as generated lock drift in the wrapper so profile recommendations and gstep checkpoints stay focused on the real slice.

## OCR Capture Diagnostics

- OCR capture changes span the Win32 screen-capture helper, app facade, HTTP OCR parser, capture failure diagnostics, window-snapshot fallback, and task-surface ownership. Close them with `Invoke-RsCoreSliceValidation.ps1 -Profile ocr-diagnostics`; a single OCR HTTP or reducer test does not prove the app stayed off WinFluent capture tasks.

## Collins MDX/MDD Real Corpus

- Do not assume a real MDD contains image, audio, or media resources. The local Collins COBUILD English Usage MDD currently exposes one resource, `\cceu.css`; image/audio/media behavior must stay covered by synthetic real-MDD fixtures unless a corpus with those assets is explicitly provided.
- The Collins MDD test should be exact about that corpus: assert the `\cceu.css` inventory and exercise resource-key variants (`cceu.css`, `/cceu.css`, `\cceu.css`, `./cceu.css`, `.\cceu.css`) through the app facade. That catches path normalization regressions without pretending this MDD covers media assets.
- Keep MDX/MDD real-corpus tests env-gated with `RS_MDICT_TEST_MDX` and `RS_MDICT_TEST_MDD`; do not hard-code `C:\Users\...` paths into tests or make them required for the default suite.
- Before adding a new MDX/MDD dependency, check `lib/rs-mdict` first. It already exposes resource inventory, prefix lookup, MIME inference, encrypted MDD support, and app-level `run_native_mdd_resource_lookup(...)` integration.
- For MDX/MDD lookup, resource inlining, encryption, or real-corpus work, close with `Invoke-RsCoreSliceValidation.ps1 -Profile mdx-native`. Keep `local-dictionary-suggestions` for the index/suggestion route only; otherwise MDX lookup changes can look validated while only the suggestion runner ran.
- Keep encrypted MDX route fixtures explicit about credentials. A native-route `Encrypted=1` regression should set a valid regcode/email; missing credentials and invalid regcode are separate local-error cases and should keep asserting `quick_translate_request_can_route_natively(...) == false`.
- For local dictionary suggestions, do not stop at Quick Translate reducer tests. The reusable Rust-owned surface is the `LexIndex` file format, `easydict-lex-index` diagnostic CLI, persistent per-dictionary index lifecycle, and Quick Translate suggestion routing together; `Invoke-RsCoreSliceValidation.ps1 -Profile local-dictionary-suggestions` should keep those layers in one lane.

## Streaming Providers

- Close Gemini/Doubao custom-streaming and traditional HTTP provider changes with their profiles (`custom-streaming` or `traditional-http`). Parser-only tests can miss Quick Translate live chunk delivery, Bing's two-phase route, CLI local SSE/HTTP behavior, or no-worker wording regressions.
- Full-response SSE parser tests are not enough for user-visible streaming. For every streaming provider route, include a fake transport that blocks after the first parsed chunk and assert the app/CLI observes that chunk before the HTTP method returns; otherwise a later refactor can silently regress to `response.text()`-style buffering while still passing parser tests.
- For OpenAI-compatible provider work, run `Invoke-RsCoreSliceValidation.ps1 -Profile openai-compatible`. Keep `async-openai` / broad multi-provider SDKs as future candidates only when they remove real duplicated protocol code; for current parity slices, the existing Rust `openai_compatible` request planner, blocking `reqwest` executor, and `llm_streaming` SSE parser already preserve proxy/settings behavior with less dependency churn.

## Clipboard/TextSelection Diagnostics

- Backend errors should not be collapsed into "no text" or timeout if the distinction affects suppression or diagnostics. Preserve backend errors in result enums and only feed confirmed no-text outcomes into suppression.
- Preserving backend errors in a reducer is only half the work. The default app task should expose a Result-shaped completion so UIA/clipboard capture failures can reach settings diagnostics; keep normal fallback success quiet.
- Explicit clipboard read/write task errors should return completion messages; avoid `.ok().flatten()` and `let _ = ...` when errors should be observable.
- Clipboard monitor loops need a bounded diagnostic path. Silently continuing on backend errors hides locked/unavailable clipboard failures, but reporting every poll can spam the UI; emit only distinct errors and clear the monitor-specific message after a successful read.
- The same rule applies to text insertion side effects. `Task::perform(async { let _ = ... }, |_| Noop)` makes Replace/capture failures invisible; return a typed completion message and let success clear only that subsystem's error prefix.
- Settings persistence is also a side-effect future. Do not use `let _ = settings_storage::save_settings_file(...)`; return a typed completion message so write/permission failures are visible, and let success clear only the settings-save error prefix.
- Desktop shell/integration futures should prefix settings diagnostics (`Desktop shell failed: ...`, `Desktop integration failed: ...`) and clear only that prefix on success. Raw error strings make later subsystem-specific recovery checks ambiguous.
- Background registration/writeback futures need the same treatment even when they are startup helpers. Built-in AI device-registration backend errors should be visible, while a later valid token clears only the Built-in AI registration prefix and keeps unrelated subsystem errors intact.
- WindowsAI/Phi prepare can report a semantic failure as `Ok(WindowsAiStatus { state: Failed, ... })`, not only as `Err(...)`. Treat both as app-visible diagnostics, and let Ready/NeedsPreparation/NotCompatible clear only the Phi prepare prefix so unrelated settings errors survive.
- For WindowsAI/Phi route work, prefer `Invoke-RsCoreSliceValidation.ps1 -Profile windows-ai-native` over the older prepare-only lane. The native lane keeps the lower-level `easydict-windows-ai` contract, app prepare reducer, Quick Translate route decisions/client streaming, CLI explicit WindowsAI/no-worker boundary, and LongDoc native route matrix together in one isolation pass.
- Close LocalAI setup/provider slices with their focused profiles: `builtin-ai-registration` for device-registration writeback, `foundry-local` for Rust-owned Foundry prepare and Auto route diagnostics, and `openvino-download` for NLLB/OpenVINO asset download state. These profiles are faster than hand-picking app reducer, OpenAI-compatible, CLI, and LongDoc filters, and they keep the no-worker/stale app-dir checks attached to the route that can regress.
- Auto provider fallback should distinguish a non-ready provider from a broken provider. Foundry Local `NotInstalled` / no endpoint can remain `Ok(None)` so Auto may continue OpenVINO, but `prepare_foundry_local_service(...)` backend errors must stay `Err(...)`; do not hide them behind `.ok()?`, or Quick/LongDoc will replace actionable native diagnostics with generic worker-required fallback text.
- Keep CLI LocalAI route ownership in the shared app-dir helpers. The default `easydict_cli` binary should not call the compatibility `auto_foundry_local_native_probe_request(...) -> Option<_>` helper or keep standalone OpenVINO/Foundry fallback blocks; those are easy to audit as possible error-swallowing paths even when unreachable.
- In Foundry CLI tests, do not use `.cmd` / `.bat` / script files as fake native helpers. The runtime guard is supposed to reject those before spawn. Use injected controllers for backend-error tests, or a missing plain `.exe` name when the contract is the Auto `NotInstalled` / non-ready fallback. If the test needs Auto to reach the Foundry branch deterministically, also set `EASYDICT_WINDOWS_AI_DISABLE_WINRT=1`.
- Screen capture has the same cancellation-vs-backend-failure trap. Keep native OCR capture on a Result-shaped task and reserve `OcrCaptureCancelled` for user cancellation; mapping `Err(...)` to `None` makes GDI/temp-file/native-call failures look like an intentional cancel.
- OCR window snapshots are optional for double-click auto-detection, but their backend errors still matter. Preserve `EnumWindows` failures as diagnostics while keeping manual drag capture available; do not convert them to an empty detector with no clue.
- File dialogs need the same split: `Ok(None)` is user cancellation, `Err(...)` is backend failure. Keep default dialog tasks on Result-shaped completions so COM/thread failures do not look like the user simply dismissed the dialog.

## LongDoc Native Layout

- Close DocLayout-YOLO/TATR/Vision layout work with `Invoke-RsCoreSliceValidation.ps1 -Profile longdoc-layout`. That profile keeps layout model download, DocLayout preprocessing/ONNX wrapper, Vision request/parser/executor, TATR pure/ONNX contracts, and explicit backend diagnostics in one isolation pass; a single `vision_layout` or `tatr` filter can miss the adjacent setup path.
- Keep `Auto` PDF layout enrichment best-effort, but do not swallow setup/download/session errors after the user explicitly selects `OnnxLocal`. Explicit modes should fail locally with a backend diagnostic instead of silently producing lower-fidelity heuristic chunks.
- TATR is still optional enrichment in `Auto`, but it becomes part of the explicit local ONNX contract once table detection is enabled. Avoid `.ok()?` around TATR download-client, ensure, missing-model, session-load, or per-table inference failures in explicit `OnnxLocal`; return a `LongDocumentBackendError` while keeping Auto best-effort.
- The same applies to configured `VisionLLM` layout enrichment. An HTTP/provider error should keep the page number and provider message in a `LongDocumentBackendError`; do not `continue` per page and quietly fall back to heuristic chunks.
- Treat missing explicit `VisionLLM` endpoint/model/API key as backend configuration errors before rendering pages. Preserve the loopback endpoint exception for local no-key vision providers, but do not let remote no-key configs drift into heuristic fallback.

## LongDoc Export And Formula

- Close TXT/Markdown/PDF export changes with `Invoke-RsCoreSliceValidation.ps1 -Profile longdoc-export`. It keeps composers, PDF content-stream patching, native PDF export, overlay metadata, and PDF source extraction export metadata together; a narrow `pdf_native_export` run can miss sidecar/source-block regressions.
- Close formula preservation, text layout, font metrics, document layout, and PDF formula evidence changes with `Invoke-RsCoreSliceValidation.ps1 -Profile longdoc-formula`. The profile also runs native LongDoc formula integration, so pure algorithm tests do not accidentally green-light a broken runner path.
