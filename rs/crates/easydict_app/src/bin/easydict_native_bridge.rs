use std::io;

use easydict_app::native_bridge::run_native_bridge;
use easydict_app::OCR_TRANSLATE_EVENT_NAME;

fn main() {
    if let Err(error) = run() {
        eprintln!("[Easydict NativeBridge] {error}");
        std::process::exit(1);
    }
}

fn run() -> io::Result<()> {
    let stdin = io::stdin();
    let stdout = io::stdout();

    run_native_bridge(stdin.lock(), stdout.lock(), || {
        win_fluent_platform_win::WindowsPlatformAdapter::signal_named_event(
            OCR_TRANSLATE_EVENT_NAME,
        )
        .map_err(|error| io::Error::other(format!("{error:?}")))
    })?;

    Ok(())
}
