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

`easydict_cli` auto-detects a packaged `Easydict.CompatHost.exe`, a common local
Debug build under `dotnet/src/Easydict.CompatHost/bin/`, or the
`EASYDICT_COMPAT_HOST` environment variable. Use `--host` or `--app-dir` to pin a
specific bridge binary. The `batch` command treats each non-empty input line as a
separate translation request and emits one JSON Line per result when `--json` is
set.

Browser Native Messaging registrar smoke checks:

```powershell
cargo run -p easydict_app --bin easydict_browser_registrar -- --help
cargo run -p easydict_app --bin easydict_browser_registrar -- status
cargo build -p easydict_app --bin easydict-native-bridge
cargo run -p easydict_app --bin easydict_browser_registrar -- install --chrome --bridge-path target\debug\easydict-native-bridge.exe
cargo run -p easydict_app --bin easydict_browser_registrar -- uninstall --chrome
```
