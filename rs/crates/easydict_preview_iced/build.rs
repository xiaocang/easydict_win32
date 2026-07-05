fn main() {
    let is_windows = std::env::var("CARGO_CFG_WINDOWS").is_ok();
    let is_msvc = std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc");
    if is_windows && is_msvc {
        println!("cargo:rustc-link-arg-bin=easydict_preview_iced=/STACK:33554432");
    }
}
