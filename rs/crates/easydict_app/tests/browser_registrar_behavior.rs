use easydict_app::browser_registrar::{
    chrome_registry_path, default_bridge_directory, firefox_registry_path,
    parse_browser_registrar_args, parse_chrome_ext_ids, serialize_cli_json,
    BrowserRegistrarCommand, BrowserRegistrarCore, BrowserRegistrarParseError,
    MemoryBrowserRegistry, DEFAULT_CHROME_EXT_IDS, DEFAULT_FIREFOX_EXT_ID,
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

    let uninstall = parse_browser_registrar_args(["uninstall"]).expect("uninstall args parse");
    assert_eq!(uninstall.command, BrowserRegistrarCommand::Uninstall);
    assert!(uninstall.chrome);
    assert!(uninstall.firefox);
}

#[test]
fn parser_accepts_browser_flags_and_custom_extension_ids() {
    let options = parse_browser_registrar_args([
        "install",
        "--chrome",
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
    assert_eq!(options.chrome_ext_ids, ["alpha", "beta"]);
    assert_eq!(options.firefox_ext_id, "easydict-test@example.test");

    let status = parse_browser_registrar_args(["status"]).expect("status args parse");
    assert_eq!(status.command, BrowserRegistrarCommand::Status);
    assert!(!status.chrome);
    assert!(!status.firefox);
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
fn default_bridge_directory_matches_the_legacy_local_app_data_location() {
    assert_eq!(
        default_bridge_directory("C:/Users/Test/AppData/Local"),
        PathBuf::from("C:/Users/Test/AppData/Local")
            .join("Easydict")
            .join("browser-bridge")
    );
}

fn read_json(path: impl AsRef<Path>) -> Value {
    let json = fs::read_to_string(path).expect("manifest should be readable");
    serde_json::from_str(&json).expect("manifest should be valid json")
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
        let path = self.path("source-easydict-native-bridge.exe");
        fs::write(&path, b"bridge").expect("write source bridge");
        path
    }
}

impl Drop for TestSandbox {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}
