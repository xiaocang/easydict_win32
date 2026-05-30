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
