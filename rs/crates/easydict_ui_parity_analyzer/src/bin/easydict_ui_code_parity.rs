use std::ffi::OsString;

fn main() {
    let mut args = std::env::args_os();
    let program = args
        .next()
        .unwrap_or_else(|| OsString::from("easydict_ui_code_parity"));
    let forwarded = std::iter::once(program)
        .chain(std::iter::once(OsString::from("code-parity")))
        .chain(args);
    std::process::exit(easydict_ui_parity_analyzer::run_cli(forwarded));
}
