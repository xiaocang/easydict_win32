# Migration Experience

## Iteration Closure

- Keep each migration slice tied to a minimal validation matrix before editing: one formatter check, the smallest behavior/unit tests that prove the changed boundary, and a `gstep diff` scope check before checkpointing. Broader suites should be deliberate risk expansion, not the default closing move.
- When parallel UI work is dirty, isolate those files before Rust core tests and restore them immediately after `gstep commit`; do not let test setup/restoration become an implicit extra refactor.

## Collins MDX/MDD Real Corpus

- Do not assume a real MDD contains image, audio, or media resources. The local Collins COBUILD English Usage MDD currently exposes one resource, `\cceu.css`; image/audio/media behavior must stay covered by synthetic real-MDD fixtures unless a corpus with those assets is explicitly provided.
- The Collins MDD test should be exact about that corpus: assert the `\cceu.css` inventory and exercise resource-key variants (`cceu.css`, `/cceu.css`, `\cceu.css`, `./cceu.css`, `.\cceu.css`) through the app facade. That catches path normalization regressions without pretending this MDD covers media assets.
- Keep MDX/MDD real-corpus tests env-gated with `RS_MDICT_TEST_MDX` and `RS_MDICT_TEST_MDD`; do not hard-code `C:\Users\...` paths into tests or make them required for the default suite.
- Before adding a new MDX/MDD dependency, check `lib/rs-mdict` first. It already exposes resource inventory, prefix lookup, MIME inference, encrypted MDD support, and app-level `run_native_mdd_resource_lookup(...)` integration.

## Clipboard/TextSelection Diagnostics

- Backend errors should not be collapsed into "no text" or timeout if the distinction affects suppression or diagnostics. Preserve backend errors in result enums and only feed confirmed no-text outcomes into suppression.
- Preserving backend errors in a reducer is only half the work. The default app task should expose a Result-shaped completion so UIA/clipboard capture failures can reach settings diagnostics; keep normal fallback success quiet.
- Explicit clipboard read/write task errors should return completion messages; avoid `.ok().flatten()` and `let _ = ...` when errors should be observable.
- Clipboard monitor loops need a bounded diagnostic path. Silently continuing on backend errors hides locked/unavailable clipboard failures, but reporting every poll can spam the UI; emit only distinct errors and clear the monitor-specific message after a successful read.
- The same rule applies to text insertion side effects. `Task::perform(async { let _ = ... }, |_| Noop)` makes Replace/capture failures invisible; return a typed completion message and let success clear only that subsystem's error prefix.
- Settings persistence is also a side-effect future. Do not use `let _ = settings_storage::save_settings_file(...)`; return a typed completion message so write/permission failures are visible, and let success clear only the settings-save error prefix.
- Desktop shell/integration futures should prefix settings diagnostics (`Desktop shell failed: ...`, `Desktop integration failed: ...`) and clear only that prefix on success. Raw error strings make later subsystem-specific recovery checks ambiguous.
- Screen capture has the same cancellation-vs-backend-failure trap. Keep native OCR capture on a Result-shaped task and reserve `OcrCaptureCancelled` for user cancellation; mapping `Err(...)` to `None` makes GDI/temp-file/native-call failures look like an intentional cancel.
- OCR window snapshots are optional for double-click auto-detection, but their backend errors still matter. Preserve `EnumWindows` failures as diagnostics while keeping manual drag capture available; do not convert them to an empty detector with no clue.
- File dialogs need the same split: `Ok(None)` is user cancellation, `Err(...)` is backend failure. Keep default dialog tasks on Result-shaped completions so COM/thread failures do not look like the user simply dismissed the dialog.

## LongDoc Native Layout

- Keep `Auto` PDF layout enrichment best-effort, but do not swallow setup/download/session errors after the user explicitly selects `OnnxLocal`. Explicit modes should fail locally with a backend diagnostic instead of silently producing lower-fidelity heuristic chunks.
- TATR is still optional enrichment in `Auto`, but it becomes part of the explicit local ONNX contract once table detection is enabled. Avoid `.ok()?` around TATR download-client, ensure, missing-model, or session-load failures in explicit `OnnxLocal`; return a `LongDocumentBackendError` while keeping Auto best-effort.
- The same applies to configured `VisionLLM` layout enrichment. An HTTP/provider error should keep the page number and provider message in a `LongDocumentBackendError`; do not `continue` per page and quietly fall back to heuristic chunks.
- Treat missing explicit `VisionLLM` endpoint/model/API key as backend configuration errors before rendering pages. Preserve the loopback endpoint exception for local no-key vision providers, but do not let remote no-key configs drift into heuristic fallback.
