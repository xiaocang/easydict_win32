use easydict_app::browser_registrar::{
    chrome_registry_path, default_bridge_directory, firefox_registry_path, legacy_bridge_directory,
    parse_browser_registrar_args, parse_chrome_ext_ids, rust_bridge_directory, serialize_cli_json,
    usage, BrowserRegistrarCommand, BrowserRegistrarCore, BrowserRegistrarParseError,
    BrowserRegistry, MemoryBrowserRegistry, DEFAULT_BRIDGE_ROOT_NAME, DEFAULT_CHROME_EXT_IDS,
    DEFAULT_FIREFOX_EXT_ID, LEGACY_BRIDGE_ROOT_NAME, RUST_BRIDGE_ROOT_NAME,
};
use serde_json::{json, Value};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[test]
fn parser_defaults_install_and_uninstall_to_both_browsers() {
    let install = parse_browser_registrar_args(["install"]).expect("install args parse");

    assert_eq!(install.command, BrowserRegistrarCommand::Install);
    assert!(install.chrome);
    assert!(install.firefox);
    assert_eq!(
        install.chrome_ext_ids,
        parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS)
    );
    assert_eq!(install.firefox_ext_id, DEFAULT_FIREFOX_EXT_ID);
    assert_eq!(install.bridge_root_name, DEFAULT_BRIDGE_ROOT_NAME);
    assert_eq!(install.bridge_root_name, RUST_BRIDGE_ROOT_NAME);

    let uninstall = parse_browser_registrar_args(["uninstall"]).expect("uninstall args parse");
    assert_eq!(uninstall.command, BrowserRegistrarCommand::Uninstall);
    assert!(uninstall.chrome);
    assert!(uninstall.firefox);
    assert_eq!(uninstall.bridge_root_name, DEFAULT_BRIDGE_ROOT_NAME);
    assert_eq!(uninstall.bridge_root_name, RUST_BRIDGE_ROOT_NAME);
}

#[test]
fn parser_accepts_browser_flags_custom_bridge_root_and_extension_ids() {
    let options = parse_browser_registrar_args([
        "install",
        "--chrome",
        "--bridge-root-name",
        "EasydictRs",
        "--bridge-path",
        "C:/tools/easydict-native-bridge.exe",
        "--chrome-ext-id",
        "alpha, beta",
        "--firefox-ext-id=easydict-test@example.test",
    ])
    .expect("install args parse");

    assert_eq!(options.command, BrowserRegistrarCommand::Install);
    assert!(options.chrome);
    assert!(!options.firefox);
    assert_eq!(
        options.bridge_path.as_deref(),
        Some(Path::new("C:/tools/easydict-native-bridge.exe"))
    );
    assert_eq!(options.bridge_root_name, RUST_BRIDGE_ROOT_NAME);
    assert_eq!(options.chrome_ext_ids, ["alpha", "beta"]);
    assert_eq!(options.firefox_ext_id, "easydict-test@example.test");

    let status = parse_browser_registrar_args(["status"]).expect("status args parse");
    assert_eq!(status.command, BrowserRegistrarCommand::Status);
    assert!(!status.chrome);
    assert!(!status.firefox);
}

#[test]
fn parser_rejects_bridge_root_names_that_escape_the_local_app_data_child() {
    assert!(matches!(
        parse_browser_registrar_args(["install", "--bridge-root-name", r"..\Easydict"])
            .expect_err("invalid root"),
        BrowserRegistrarParseError::InvalidValue { .. }
    ));
    assert!(matches!(
        parse_browser_registrar_args(["install", "--bridge-root-name="]).expect_err("empty root"),
        BrowserRegistrarParseError::InvalidValue { .. }
    ));
}

#[test]
fn parser_rejects_legacy_dotnet_bridge_root_for_rs_portable() {
    assert!(matches!(
        parse_browser_registrar_args(["install", "--bridge-root-name", LEGACY_BRIDGE_ROOT_NAME])
            .expect_err("legacy dotnet root should be rejected"),
        BrowserRegistrarParseError::InvalidValue { .. }
    ));
    assert!(matches!(
        parse_browser_registrar_args(["install", "--bridge-root-name", "easydict"])
            .expect_err("legacy dotnet root should be rejected case-insensitively"),
        BrowserRegistrarParseError::InvalidValue { .. }
    ));
}

#[test]
fn parser_reports_missing_and_unknown_commands_like_the_registrar_cli() {
    assert_eq!(
        parse_browser_registrar_args(std::iter::empty::<&str>()).expect_err("missing command"),
        BrowserRegistrarParseError::MissingCommand
    );
    assert_eq!(
        parse_browser_registrar_args(["bogus"]).expect_err("unknown command"),
        BrowserRegistrarParseError::UnknownCommand("bogus".to_string())
    );
}

#[test]
fn browser_registrar_usage_names_rust_binary_not_legacy_alias() {
    let usage = usage();

    assert!(usage.contains("easydict_browser_registrar"));
    assert!(
        !usage.contains("BrowserHostRegistrar"),
        "default registrar help should not present the legacy .NET alias as the primary entrypoint"
    );
}

#[test]
fn browser_registrar_binary_uses_rust_owned_registry_helper() {
    let registrar_bin = include_str!("../src/bin/easydict_browser_registrar.rs");

    assert!(registrar_bin.contains("easydict_windows_registry::write_current_user_default_string"));
    assert!(
        !registrar_bin.contains("win_fluent_platform_win"),
        "browser registrar binary should not reach into WinFluent platform registry helpers"
    );
    assert!(
        !registrar_bin.contains("WindowsPlatformAdapter"),
        "browser registrar binary should keep registry IO behind lib/easydict-windows-registry"
    );
}

#[test]
fn browser_registrar_source_uses_lib_owned_retained_runtime_guard() {
    let source = include_str!("../src/browser_registrar.rs");
    let production = source
        .split("#[cfg(test)]")
        .next()
        .expect("browser registrar source should have production section");

    assert!(
        production.contains("easydict_runtime_guards::path_has_retained_runtime_component"),
        "browser registrar should keep retained payload component policy in a lib-owned guard"
    );
    for forbidden_inline_marker in [
        "value == \"dotnet\"",
        "value == \"workers\"",
        "easydict.compathost",
        "easydict.nativebridge",
        "easydict.workers.",
    ] {
        assert!(
            !production.contains(forbidden_inline_marker),
            "browser registrar production source should not inline retained runtime marker {forbidden_inline_marker}"
        );
    }
}

#[test]
fn install_chrome_writes_manifest_registry_and_copies_bridge() {
    let sandbox = TestSandbox::new("install_chrome");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    let output = core.install(
        true,
        false,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    assert!(output.success);
    assert_eq!(
        output.installed.as_deref(),
        Some(&["chrome".to_string()][..])
    );
    assert_eq!(
        output.bridge_path.as_deref(),
        Some(core.bridge_exe_path().display().to_string().as_str())
    );
    assert_eq!(
        fs::read(core.bridge_exe_path()).expect("bridge copied"),
        b"bridge"
    );

    let manifest_path = core
        .registry()
        .value(&chrome_registry_path())
        .expect("chrome registry value");
    assert_eq!(
        Path::new(manifest_path)
            .file_name()
            .and_then(|name| name.to_str()),
        Some("chrome-manifest.json")
    );

    let manifest = read_json(manifest_path);
    assert_eq!(manifest["name"], "com.easydict.bridge");
    assert_eq!(manifest["description"], "Easydict native messaging bridge");
    assert_eq!(
        manifest["path"],
        core.bridge_exe_path().display().to_string()
    );
    assert_eq!(manifest["type"], "stdio");
    assert_eq!(
        manifest["allowed_origins"],
        json!([
            "chrome-extension://dmokdfinnomehfpmhoeekomncpobgagf/",
            "chrome-extension://cbhpnmadpnoedfgonddpmlhaclbicllg/"
        ])
    );
}

#[test]
fn install_firefox_writes_allowed_extensions_manifest() {
    let sandbox = TestSandbox::new("install_firefox");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    let output = core.install(
        false,
        true,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        "custom-firefox@example.test",
    );

    assert!(output.success);
    assert_eq!(
        output.installed.as_deref(),
        Some(&["firefox".to_string()][..])
    );
    let manifest_path = core
        .registry()
        .value(&firefox_registry_path())
        .expect("firefox registry value");
    let manifest = read_json(manifest_path);

    assert_eq!(manifest["name"], "com.easydict.bridge");
    assert_eq!(manifest["type"], "stdio");
    assert_eq!(
        manifest["allowed_extensions"],
        json!(["custom-firefox@example.test"])
    );
}

#[test]
fn status_requires_registry_manifest_and_bridge_exe_to_exist() {
    let sandbox = TestSandbox::new("status");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    core.install(
        true,
        false,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    let status = core.status();
    assert!(status.chrome.installed);
    assert!(!status.firefox.installed);
    assert!(status.bridge_exists);
    assert_eq!(status.bridge_directory, bridge_dir.display().to_string());

    fs::remove_file(core.bridge_exe_path()).expect("remove bridge");
    let status = core.status();
    assert!(!status.chrome.installed);
    assert!(!status.bridge_exists);
}

#[test]
fn status_validates_manifest_name_type_and_current_bridge_path() {
    let sandbox = TestSandbox::new("status_manifest_integrity");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    core.install(
        true,
        false,
        &source_bridge,
        &["custom-chrome-extension".to_string()],
        DEFAULT_FIREFOX_EXT_ID,
    );

    let manifest_path = PathBuf::from(
        core.registry()
            .value(&chrome_registry_path())
            .expect("chrome registry value"),
    );
    let bridge_path = core.bridge_exe_path().display().to_string();

    assert!(core.status().chrome.installed);

    write_json(
        &manifest_path,
        json!({
            "name": "not.easydict.bridge",
            "description": "Easydict native messaging bridge",
            "path": bridge_path.clone(),
            "type": "stdio",
            "allowed_origins": ["chrome-extension://custom-chrome-extension/"]
        }),
    );
    assert!(!core.status().chrome.installed);

    write_json(
        &manifest_path,
        json!({
            "name": "com.easydict.bridge",
            "description": "Easydict native messaging bridge",
            "path": bridge_path.clone(),
            "type": "pipe",
            "allowed_origins": ["chrome-extension://custom-chrome-extension/"]
        }),
    );
    assert!(!core.status().chrome.installed);

    let other_bridge_path = sandbox.path("other-easydict-native-bridge.exe");
    fs::write(&other_bridge_path, b"bridge").expect("write other bridge");
    write_json(
        &manifest_path,
        json!({
            "name": "com.easydict.bridge",
            "description": "Easydict native messaging bridge",
            "path": other_bridge_path.display().to_string(),
            "type": "stdio",
            "allowed_origins": ["chrome-extension://custom-chrome-extension/"]
        }),
    );
    assert!(!core.status().chrome.installed);

    write_json(
        &manifest_path,
        json!({
            "name": "com.easydict.bridge",
            "description": "Easydict native messaging bridge",
            "path": bridge_path,
            "type": "stdio",
            "allowed_origins": ["chrome-extension://custom-chrome-extension/"]
        }),
    );
    assert!(core.status().chrome.installed);
}

#[test]
fn status_accepts_manifests_with_custom_extension_ids() {
    let sandbox = TestSandbox::new("status_custom_extension_ids");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    core.install(
        true,
        true,
        &source_bridge,
        &["custom-chrome-extension".to_string()],
        "custom-firefox@example.test",
    );

    let status = core.status();
    assert!(status.chrome.installed);
    assert!(status.firefox.installed);
}

#[test]
fn uninstall_removes_selected_browser_and_cleans_directory_when_none_remain() {
    let sandbox = TestSandbox::new("uninstall");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    core.install(
        true,
        true,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    let chrome = core.uninstall(true, false);
    assert_eq!(chrome.uninstalled, ["chrome"]);
    assert!(core.registry().value(&chrome_registry_path()).is_none());
    assert!(core.registry().value(&firefox_registry_path()).is_some());
    assert!(bridge_dir.exists());

    let firefox = core.uninstall(false, true);
    assert_eq!(firefox.uninstalled, ["firefox"]);
    assert!(core.registry().value(&firefox_registry_path()).is_none());
    assert!(!bridge_dir.exists());
}

#[test]
fn uninstall_preserves_foreign_native_messaging_registration() {
    let sandbox = TestSandbox::new("foreign_uninstall");
    let bridge_dir = sandbox.path("browser-bridge");
    fs::create_dir_all(&bridge_dir).expect("create bridge dir");
    fs::write(bridge_dir.join("stale.txt"), b"stale").expect("write local stale file");

    let foreign_dir = sandbox.path("dotnet-browser-bridge");
    fs::create_dir_all(&foreign_dir).expect("create foreign dir");
    let foreign_bridge_path = foreign_dir.join("easydict-native-bridge.exe");
    fs::write(&foreign_bridge_path, b"dotnet bridge").expect("write foreign bridge");
    let foreign_manifest_path = foreign_dir.join("chrome-manifest.json");
    write_json(
        &foreign_manifest_path,
        json!({
            "name": "com.easydict.bridge",
            "description": "Easydict native messaging bridge",
            "path": foreign_bridge_path.display().to_string(),
            "type": "stdio",
            "allowed_origins": ["chrome-extension://custom-chrome-extension/"]
        }),
    );

    let mut registry = MemoryBrowserRegistry::default();
    registry
        .write_default_value(
            &chrome_registry_path(),
            &foreign_manifest_path.display().to_string(),
        )
        .expect("write foreign registry value");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, registry);

    let output = core.uninstall(true, false);

    assert!(output.uninstalled.is_empty());
    assert_eq!(
        core.registry().value(&chrome_registry_path()),
        Some(foreign_manifest_path.display().to_string().as_str())
    );
    assert!(!bridge_dir.exists());
    assert!(foreign_manifest_path.exists());
}

#[test]
fn missing_bridge_source_returns_json_shaped_error_without_creating_bridge_dir() {
    let sandbox = TestSandbox::new("missing_source");
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    let output = core.install(
        true,
        true,
        &sandbox.path("missing.exe"),
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    assert!(!output.success);
    assert!(output
        .error
        .as_deref()
        .expect("error text")
        .contains("Bridge exe not found"));
    assert!(!bridge_dir.exists());
    assert_eq!(
        serde_json::from_str::<Value>(&serialize_cli_json(&output)).expect("error json"),
        json!({
            "success": false,
            "error": output.error.expect("error text")
        })
    );
}

#[test]
fn install_rejects_dotnet_and_compat_host_sources_even_when_they_exist() {
    let sandbox = TestSandbox::new("reject_dotnet_sources");

    for forbidden_name in [
        "Easydict.CompatHost.exe",
        "Easydict.NativeBridge.exe",
        "Easydict.WinUI.exe",
        "Easydict.Workers.LongDoc.exe",
        "Easydict.Workers.LocalAi.exe",
    ] {
        let source = sandbox.path(forbidden_name);
        fs::write(&source, b"dotnet").expect("write forbidden source");
        let bridge_dir = sandbox.path(&format!("browser-bridge-{forbidden_name}"));
        let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

        let output = core.install(
            true,
            true,
            &source,
            &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
            DEFAULT_FIREFOX_EXT_ID,
        );

        assert!(!output.success, "{forbidden_name} should be rejected");
        let error = output.error.as_deref().expect("error text");
        assert!(error.contains("easydict-native-bridge.exe"));
        assert!(error.contains("non-Rust native bridge"));
        assert!(
            !bridge_dir.exists(),
            "{forbidden_name} should not be staged"
        );
    }
}

#[test]
fn install_rejects_renamed_bridge_sources_from_legacy_payload_roots() {
    let sandbox = TestSandbox::new("reject_renamed_payload_sources");

    for forbidden_parent in [
        Path::new("workers").join("ocr"),
        Path::new("dotnet").join("host").join("fxr"),
        PathBuf::from("Easydict.CompatHost"),
        PathBuf::from("Easydict.NativeBridge"),
        PathBuf::from("Easydict.Workers.LongDoc"),
        PathBuf::from("Easydict.Workers.LocalAi"),
    ] {
        let source_dir = sandbox.root.join(&forbidden_parent);
        fs::create_dir_all(&source_dir).expect("forbidden source dir should be created");
        let source = source_dir.join("easydict-native-bridge.exe");
        fs::write(&source, b"renamed legacy payload").expect("write renamed source");
        let bridge_dir = sandbox.path(&format!(
            "browser-bridge-{}",
            forbidden_parent
                .display()
                .to_string()
                .replace(['\\', '/', ':'], "-")
        ));
        let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

        let output = core.install(
            true,
            true,
            &source,
            &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
            DEFAULT_FIREFOX_EXT_ID,
        );

        assert!(
            !output.success,
            "{} should be rejected even after being renamed to the native bridge exe",
            source.display()
        );
        let error = output.error.as_deref().expect("error text");
        assert!(error.contains("non-Rust native bridge"));
        assert!(!bridge_dir.exists(), "renamed source should not be staged");
    }
}

#[test]
fn install_fails_when_existing_bridge_path_cannot_be_replaced() {
    let sandbox = TestSandbox::new("copy_failure");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    fs::create_dir_all(bridge_dir.join("easydict-native-bridge.exe"))
        .expect("create conflicting bridge directory");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    let output = core.install(
        true,
        true,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    assert!(!output.success);
    assert!(core.registry().value(&chrome_registry_path()).is_none());
    assert!(core.registry().value(&firefox_registry_path()).is_none());
}

#[test]
fn default_bridge_directory_matches_the_rs_portable_local_app_data_location() {
    assert_eq!(
        default_bridge_directory("C:/Users/Test/AppData/Local"),
        PathBuf::from("C:/Users/Test/AppData/Local")
            .join(RUST_BRIDGE_ROOT_NAME)
            .join("browser-bridge")
    );
}

#[test]
fn legacy_bridge_directory_matches_the_dotnet_local_app_data_location() {
    assert_eq!(
        legacy_bridge_directory("C:/Users/Test/AppData/Local"),
        PathBuf::from("C:/Users/Test/AppData/Local")
            .join(LEGACY_BRIDGE_ROOT_NAME)
            .join("browser-bridge")
    );
}

#[test]
fn rust_bridge_directory_uses_a_portable_specific_local_app_data_location() {
    assert_eq!(
        rust_bridge_directory("C:/Users/Test/AppData/Local"),
        PathBuf::from("C:/Users/Test/AppData/Local")
            .join(RUST_BRIDGE_ROOT_NAME)
            .join("browser-bridge")
    );
}

fn read_json(path: impl AsRef<Path>) -> Value {
    let json = fs::read_to_string(path).expect("manifest should be readable");
    serde_json::from_str(&json).expect("manifest should be valid json")
}

fn write_json(path: impl AsRef<Path>, value: Value) {
    let json = serde_json::to_string(&value).expect("manifest should serialize");
    fs::write(path, json).expect("write manifest");
}

struct TestSandbox {
    root: PathBuf,
}

impl TestSandbox {
    fn new(name: &str) -> Self {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time")
            .as_nanos();
        let root = std::env::temp_dir().join(format!(
            "easydict_browser_registrar_{name}_{}_{}",
            std::process::id(),
            nonce
        ));
        fs::create_dir_all(&root).expect("create sandbox");
        Self { root }
    }

    fn path(&self, child: &str) -> PathBuf {
        self.root.join(child)
    }

    fn write_source_bridge(&self) -> PathBuf {
        let path = self.path("easydict-native-bridge.exe");
        fs::write(&path, b"bridge").expect("write source bridge");
        path
    }
}

impl Drop for TestSandbox {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}
