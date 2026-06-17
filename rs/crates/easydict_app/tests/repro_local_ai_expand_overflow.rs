//! Reproduction probe for the windows-local-ai expander stack overflow.
//!
//! Builds the settings view with the `windows-local-ai` service configuration
//! expanded (the exact state a live click produces) and snapshots it. If the
//! built `View` tree is cyclic / pathologically deep, this test overflows the
//! stack and the panic backtrace names the recursive builder in `ui.rs`.

use easydict_app::{settings_view, EasydictUiState};

#[test]
fn local_ai_expanded_settings_view_builds_and_snapshots() {
    let mut state = EasydictUiState::default();
    state
        .settings
        .expanded_service_configurations
        .push("windows-local-ai".to_string());
    // Match the live crash repro: provider explicitly set to WindowsAI.
    state.settings.local_ai_provider = "WindowsAI".to_string();

    // Run on a 1 MiB stack to mirror the real app's UI thread budget (much
    // smaller than the default test/main stack), so a pathologically deep —
    // not just infinite — tree still surfaces here.
    let handle = std::thread::Builder::new()
        .stack_size(1024 * 1024)
        .spawn(move || {
            let view = settings_view(&state.settings);
            let snapshot = win_fluent_testkit::view_snapshot(&view);
            let lines = snapshot.lines().count();
            let max_indent = snapshot
                .lines()
                .map(|l| l.len() - l.trim_start().len())
                .max()
                .unwrap_or(0);
            (lines, max_indent)
        })
        .expect("spawn probe thread");
    let (lines, max_indent) = handle.join().expect("probe thread panicked (overflow?)");
    println!("snapshot lines={lines} max_indent_chars={max_indent}");
    assert!(
        lines < 100_000,
        "settings view snapshot unexpectedly large: {lines} lines"
    );
}
