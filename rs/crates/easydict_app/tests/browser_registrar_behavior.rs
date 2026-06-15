use easydict_app::browser_registrar::{
    bridge_directory_for_root, chrome_registry_path, default_bridge_directory,
    firefox_registry_path, parse_browser_registrar_args, parse_chrome_ext_ids,
    rust_bridge_directory, serialize_cli_json, usage, BrowserRegistrarCommand,
    BrowserRegistrarCore, BrowserRegistrarParseError, BrowserRegistry, MemoryBrowserRegistry,
    CHROME_MANIFEST_FILE, DEFAULT_BRIDGE_ROOT_NAME, DEFAULT_CHROME_EXT_IDS, DEFAULT_FIREFOX_EXT_ID,
    NATIVE_HOST_NAME, RUST_BRIDGE_ROOT_NAME, RUST_NATIVE_HOST_NAME,
};
use serde_json::{json, Value};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

const LEGACY_BRIDGE_ROOT_NAME: &str = "Easydict";
const LEGACY_NATIVE_HOST_NAME: &str = "com.easydict.bridge";

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
    for value in [
        LEGACY_BRIDGE_ROOT_NAME,
        "easydict",
        "dotnet",
        "workers",
        "Easydict.CompatHost",
        "Easydict.Workers.LocalAi",
        "Easydict.Workers.LongDoc.exe",
        "powershell.exe",
        "pwsh.cmd",
        "legacy-backend.ps1",
        "hostfxr.dll",
    ] {
        assert!(
            matches!(
                parse_browser_registrar_args(["install", "--bridge-root-name", value])
                    .expect_err("legacy/retained bridge root should be rejected"),
                BrowserRegistrarParseError::InvalidValue { .. }
            ),
            "{value} must not be accepted as a browser bridge root for the rs portable"
        );
    }
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
fn parser_rejects_unknown_or_legacy_options_instead_of_silently_ignoring_them() {
    for args in [
        vec!["install", "--legacy-host", "com.easydict.bridge"],
        vec!["install", "--native-host-name=com.easydict.bridge"],
        vec!["install", "Easydict.NativeBridge.exe"],
    ] {
        let error = parse_browser_registrar_args(args.clone())
            .expect_err("unknown or legacy registrar argument should be rejected");
        assert!(
            matches!(error, BrowserRegistrarParseError::UnknownOption(_)),
            "{args:?} should fail as an unknown option, got {error:?}"
        );
    }
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
fn browser_registrar_defaults_to_rs_native_host_without_overwriting_legacy_host() {
    assert_eq!(NATIVE_HOST_NAME, RUST_NATIVE_HOST_NAME);
    assert_ne!(NATIVE_HOST_NAME, LEGACY_NATIVE_HOST_NAME);
    assert_eq!(
        chrome_registry_path(),
        format!(r"Software\Google\Chrome\NativeMessagingHosts\{RUST_NATIVE_HOST_NAME}")
    );
    assert_eq!(
        firefox_registry_path(),
        format!(r"Software\Mozilla\NativeMessagingHosts\{RUST_NATIVE_HOST_NAME}")
    );
}

#[test]
fn browser_extension_defaults_to_rs_native_host_without_legacy_fallback() {
    for (name, source) in [
        (
            "background.js",
            include_str!("../../../../browser-extension/background.js"),
        ),
        (
            "setup.js",
            include_str!("../../../../browser-extension/setup.js"),
        ),
    ] {
        assert!(
            source.contains(RUST_NATIVE_HOST_NAME),
            "{name} should contain the rs native host name"
        );
        assert!(
            !source.contains(LEGACY_NATIVE_HOST_NAME),
            "{name} must not fall back to the legacy dotnet native host in the default extension"
        );
        assert!(
            !source.contains("sendNativeMessageWithFallback")
                && !source.contains("sendNativeMessageToHost"),
            "{name} should not keep fallback helper plumbing on the default rs path"
        );
    }
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
fn browser_registrar_cli_returns_failure_exit_when_uninstall_reports_failure() {
    let registrar_bin = include_str!("../src/bin/easydict_browser_registrar.rs");
    let uninstall_branch = registrar_bin
        .split("BrowserRegistrarCommand::Uninstall")
        .nth(1)
        .expect("uninstall branch should exist");

    assert!(uninstall_branch.contains("let success = output.success"));
    assert!(uninstall_branch.contains("ExitCode::from(1)"));
}

#[test]
fn browser_registrar_cli_returns_failure_exit_when_status_reports_failure() {
    let registrar_bin = include_str!("../src/bin/easydict_browser_registrar.rs");
    let status_branch = registrar_bin
        .split("BrowserRegistrarCommand::Status")
        .nth(1)
        .expect("status branch should exist");

    assert!(status_branch.contains("let success = output.error.is_none()"));
    assert!(status_branch.contains("ExitCode::from(1)"));
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
    assert!(
        production
            .contains("easydict_runtime_guards::command_target_is_retained_runtime_or_script_marker"),
        "browser registrar should reject script/shell bridge root names through the lib-owned runtime guard"
    );
    assert!(
        production.contains("easydict_runtime_guards::bytes_contain_retained_runtime_marker"),
        "browser registrar should scan bridge file contents through the lib-owned runtime marker guard"
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
fn install_retained_staged_bridge_cleanup_failure_stays_observable() {
    let source = include_str!("../src/browser_registrar.rs");
    let production = source
        .split("#[cfg(test)]")
        .next()
        .expect("browser registrar source should have production section");

    assert!(
        !production.contains("let _ = delete_file(&bridge_path)"),
        "staged retained bridge cleanup errors must not be discarded"
    );
    assert!(
        production.contains("staged_bridge_retained_marker_error("),
        "staged retained bridge cleanup should route through an observable error helper"
    );
    assert!(
        production.contains("failed to remove staged bridge"),
        "install error should include staged bridge cleanup failure details"
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
    assert_eq!(manifest["name"], RUST_NATIVE_HOST_NAME);
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

    assert_eq!(manifest["name"], RUST_NATIVE_HOST_NAME);
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
fn status_rejects_registered_bridge_with_retained_runtime_content() {
    let sandbox = TestSandbox::new("status_retained_bridge_content");
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

    assert!(core.status().chrome.installed);
    fs::write(
        core.bridge_exe_path(),
        b"renamed apphost still references hostfxr.dll and This application requires .NET",
    )
    .expect("overwrite bridge with retained runtime marker");

    let status = core.status();
    assert!(!status.chrome.installed);
    assert!(status.bridge_exists);
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
    let status = core.status();
    assert!(!status.chrome.installed);
    assert!(status.error.is_none());

    write_json(
        &manifest_path,
        json!({
            "name": NATIVE_HOST_NAME,
            "description": "Easydict native messaging bridge",
            "path": bridge_path.clone(),
            "type": "pipe",
            "allowed_origins": ["chrome-extension://custom-chrome-extension/"]
        }),
    );
    let status = core.status();
    assert!(!status.chrome.installed);
    assert!(status.error.is_none());

    let other_bridge_path = sandbox.path("other-easydict-native-bridge.exe");
    fs::write(&other_bridge_path, b"bridge").expect("write other bridge");
    write_json(
        &manifest_path,
        json!({
            "name": NATIVE_HOST_NAME,
            "description": "Easydict native messaging bridge",
            "path": other_bridge_path.display().to_string(),
            "type": "stdio",
            "allowed_origins": ["chrome-extension://custom-chrome-extension/"]
        }),
    );
    let status = core.status();
    assert!(!status.chrome.installed);
    assert!(status.error.is_none());

    write_json(
        &manifest_path,
        json!({
            "name": NATIVE_HOST_NAME,
            "description": "Easydict native messaging bridge",
            "path": bridge_path,
            "type": "stdio",
            "allowed_origins": ["chrome-extension://custom-chrome-extension/"]
        }),
    );
    let status = core.status();
    assert!(status.chrome.installed);
    assert!(status.error.is_none());
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
fn status_reports_registry_read_failures_instead_of_silent_uninstalled_state() {
    let sandbox = TestSandbox::new("status_registry_read_failure");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(
        &bridge_dir,
        ReadFailureRegistry::new(&chrome_registry_path()),
    );

    core.install(
        true,
        false,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    let status = core.status();

    assert!(!status.chrome.installed);
    assert!(status
        .error
        .as_deref()
        .expect("status error")
        .contains("failed to read chrome native messaging registry key"));
    let json: Value = serde_json::from_str(&serialize_cli_json(&status)).expect("status json");
    assert_eq!(json["chrome"]["installed"], false);
    assert!(json["error"]
        .as_str()
        .expect("json error")
        .contains("registry key"));
}

#[test]
fn status_reports_manifest_read_failures_instead_of_silent_uninstalled_state() {
    let sandbox = TestSandbox::new("status_manifest_read_failure");
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

    let manifest_path = bridge_dir.join(CHROME_MANIFEST_FILE);
    fs::remove_file(&manifest_path).expect("replace manifest file");
    fs::create_dir(&manifest_path).expect("create conflicting manifest directory");

    let status = core.status();

    assert!(!status.chrome.installed);
    assert!(status
        .error
        .as_deref()
        .expect("status error")
        .contains("failed to read chrome native messaging manifest"));
}

#[test]
fn status_reports_manifest_parse_failures_instead_of_silent_uninstalled_state() {
    let sandbox = TestSandbox::new("status_manifest_parse_failure");
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

    fs::write(bridge_dir.join(CHROME_MANIFEST_FILE), b"{not valid json")
        .expect("write invalid manifest json");

    let status = core.status();

    assert!(!status.chrome.installed);
    assert!(status
        .error
        .as_deref()
        .expect("status error")
        .contains("failed to parse chrome native messaging manifest"));
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
    assert!(chrome.success);
    assert!(chrome.error.is_none());
    assert_eq!(chrome.uninstalled, ["chrome"]);
    assert!(core.registry().value(&chrome_registry_path()).is_none());
    assert!(core.registry().value(&firefox_registry_path()).is_some());
    assert!(bridge_dir.exists());

    let firefox = core.uninstall(false, true);
    assert!(firefox.success);
    assert!(firefox.error.is_none());
    assert_eq!(firefox.uninstalled, ["firefox"]);
    assert!(core.registry().value(&firefox_registry_path()).is_none());
    assert!(!bridge_dir.exists());
}

#[test]
fn uninstall_reports_registry_delete_failures_without_claiming_browser_success() {
    let sandbox = TestSandbox::new("uninstall_registry_delete_failure");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(
        &bridge_dir,
        DeleteFailureRegistry::new(&chrome_registry_path()),
    );

    core.install(
        true,
        false,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    let output = core.uninstall(true, false);

    assert!(!output.success);
    assert!(output.uninstalled.is_empty());
    let error = output.error.as_deref().expect("cleanup error");
    assert!(error.contains("failed to delete chrome native messaging registry key"));
    let expected_manifest_path = bridge_dir.join(CHROME_MANIFEST_FILE).display().to_string();
    assert_eq!(
        core.registry().value(&chrome_registry_path()),
        Some(expected_manifest_path.as_str())
    );
    assert!(bridge_dir.exists());

    let json: Value = serde_json::from_str(&serialize_cli_json(&output)).expect("error json");
    assert_eq!(json["success"], false);
    assert_eq!(json["uninstalled"], json!([]));
    assert!(json["error"]
        .as_str()
        .expect("json error")
        .contains("registry key"));
}

#[test]
fn uninstall_reports_manifest_delete_failures_before_reporting_success() {
    let sandbox = TestSandbox::new("uninstall_manifest_delete_failure");
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

    let manifest_path = bridge_dir.join(CHROME_MANIFEST_FILE);
    fs::remove_file(&manifest_path).expect("replace manifest file");
    fs::create_dir(&manifest_path).expect("create conflicting manifest directory");

    let output = core.uninstall(true, false);

    assert!(!output.success);
    assert!(output.uninstalled.is_empty());
    assert!(output
        .error
        .as_deref()
        .expect("cleanup error")
        .contains("failed to delete chrome native messaging manifest"));
    assert!(core.registry().value(&chrome_registry_path()).is_none());
    assert!(
        bridge_dir.exists(),
        "bridge directory cleanup should wait when manifest deletion failed"
    );
}

#[test]
fn uninstall_reports_registry_read_failures_without_cleaning_bridge_directory() {
    let sandbox = TestSandbox::new("uninstall_registry_read_failure");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(
        &bridge_dir,
        ReadFailureRegistry::new(&chrome_registry_path()),
    );

    core.install(
        true,
        false,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    let output = core.uninstall(true, false);

    assert!(!output.success);
    assert!(output.uninstalled.is_empty());
    assert!(output
        .error
        .as_deref()
        .expect("cleanup error")
        .contains("failed to read chrome native messaging registry key"));
    assert!(bridge_dir.exists());
}

#[test]
fn uninstall_reports_remaining_browser_status_failures_before_removing_bridge_directory() {
    let sandbox = TestSandbox::new("uninstall_remaining_status_failure");
    let source_bridge = sandbox.write_source_bridge();
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(
        &bridge_dir,
        ReadFailureRegistry::new(&firefox_registry_path()),
    );

    core.install(
        true,
        false,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    let output = core.uninstall(true, false);

    assert!(!output.success);
    assert_eq!(output.uninstalled, ["chrome"]);
    assert!(output
        .error
        .as_deref()
        .expect("cleanup error")
        .contains("failed to read firefox native messaging registry key"));
    assert!(
        bridge_dir.exists(),
        "bridge directory should remain until the other browser status can be trusted"
    );
}

#[test]
fn uninstall_reports_bridge_directory_cleanup_failures() {
    let sandbox = TestSandbox::new("uninstall_bridge_directory_cleanup_failure");
    let bridge_dir = sandbox.path("browser-bridge");
    fs::write(&bridge_dir, b"not a directory").expect("create conflicting bridge path");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    let output = core.uninstall(true, true);

    assert!(!output.success);
    assert!(output.uninstalled.is_empty());
    assert!(output
        .error
        .as_deref()
        .expect("cleanup error")
        .contains("failed to remove browser bridge directory"));
    assert!(bridge_dir.exists());
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
            "name": LEGACY_NATIVE_HOST_NAME,
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

    assert!(output.success);
    assert!(output.error.is_none());
    assert!(output.uninstalled.is_empty());
    assert_eq!(
        core.registry().value(&chrome_registry_path()),
        Some(foreign_manifest_path.display().to_string().as_str())
    );
    assert!(!bridge_dir.exists());
    assert!(foreign_manifest_path.exists());
}

#[test]
fn status_and_uninstall_ignore_legacy_dotnet_native_messaging_keys() {
    let sandbox = TestSandbox::new("ignore_legacy_registry_keys");
    let bridge_dir = sandbox.path("browser-bridge");
    let legacy_dir = sandbox.path("legacy-browser-bridge");
    fs::create_dir_all(&legacy_dir).expect("create legacy bridge dir");
    let legacy_bridge_path = legacy_dir.join("Easydict.NativeBridge.exe");
    fs::write(&legacy_bridge_path, b"legacy dotnet native bridge").expect("write legacy bridge");
    let legacy_manifest_path = legacy_dir.join("chrome-manifest.json");
    write_json(
        &legacy_manifest_path,
        json!({
            "name": LEGACY_NATIVE_HOST_NAME,
            "description": "legacy Easydict native messaging bridge",
            "path": legacy_bridge_path.display().to_string(),
            "type": "stdio",
            "allowed_origins": ["chrome-extension://legacy-extension/"]
        }),
    );
    let legacy_chrome_key =
        format!(r"Software\Google\Chrome\NativeMessagingHosts\{LEGACY_NATIVE_HOST_NAME}");
    let mut registry = MemoryBrowserRegistry::default();
    registry
        .write_default_value(
            &legacy_chrome_key,
            &legacy_manifest_path.display().to_string(),
        )
        .expect("write legacy registry value");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, registry);

    let status = core.status();
    assert!(!status.chrome.installed);
    assert!(!status.firefox.installed);

    let uninstall = core.uninstall(true, true);
    assert!(uninstall.success);
    assert!(uninstall.error.is_none());
    assert!(uninstall.uninstalled.is_empty());
    assert_eq!(
        core.registry().value(&legacy_chrome_key),
        Some(legacy_manifest_path.display().to_string().as_str())
    );
    assert!(legacy_manifest_path.exists());
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
fn install_rejects_native_bridge_name_with_retained_runtime_content() {
    let sandbox = TestSandbox::new("reject_retained_bridge_content");
    let source_bridge = sandbox.path("easydict-native-bridge.exe");
    fs::write(
        &source_bridge,
        b"fake bridge apphost references hostfxr.dll and This application requires .NET",
    )
    .expect("write renamed retained bridge");
    let bridge_dir = sandbox.path("browser-bridge");
    let mut core = BrowserRegistrarCore::new(&bridge_dir, MemoryBrowserRegistry::default());

    let output = core.install(
        true,
        true,
        &source_bridge,
        &parse_chrome_ext_ids(DEFAULT_CHROME_EXT_IDS),
        DEFAULT_FIREFOX_EXT_ID,
    );

    assert!(!output.success);
    let error = output.error.as_deref().expect("error text");
    assert!(error.contains("retained payload"));
    assert!(!bridge_dir.exists(), "retained bridge should not be staged");
    assert!(core.registry().value(&chrome_registry_path()).is_none());
    assert!(core.registry().value(&firefox_registry_path()).is_none());
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
        bridge_directory_for_root("C:/Users/Test/AppData/Local", LEGACY_BRIDGE_ROOT_NAME),
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

struct DeleteFailureRegistry {
    inner: MemoryBrowserRegistry,
    failing_key: String,
}

impl DeleteFailureRegistry {
    fn new(failing_key: &str) -> Self {
        Self {
            inner: MemoryBrowserRegistry::default(),
            failing_key: failing_key.to_string(),
        }
    }

    fn value(&self, key_path: &str) -> Option<&str> {
        self.inner.value(key_path)
    }
}

impl BrowserRegistry for DeleteFailureRegistry {
    fn write_default_value(&mut self, key_path: &str, value: &str) -> io::Result<()> {
        self.inner.write_default_value(key_path, value)
    }

    fn delete_key(&mut self, key_path: &str) -> io::Result<()> {
        if key_path == self.failing_key {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "simulated registry delete failure",
            ));
        }

        self.inner.delete_key(key_path)
    }

    fn read_default_value(&self, key_path: &str) -> io::Result<Option<String>> {
        self.inner.read_default_value(key_path)
    }
}

struct ReadFailureRegistry {
    inner: MemoryBrowserRegistry,
    failing_key: String,
}

impl ReadFailureRegistry {
    fn new(failing_key: &str) -> Self {
        Self {
            inner: MemoryBrowserRegistry::default(),
            failing_key: failing_key.to_string(),
        }
    }
}

impl BrowserRegistry for ReadFailureRegistry {
    fn write_default_value(&mut self, key_path: &str, value: &str) -> io::Result<()> {
        self.inner.write_default_value(key_path, value)
    }

    fn delete_key(&mut self, key_path: &str) -> io::Result<()> {
        self.inner.delete_key(key_path)
    }

    fn read_default_value(&self, key_path: &str) -> io::Result<Option<String>> {
        if key_path == self.failing_key {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "simulated registry read failure",
            ));
        }

        self.inner.read_default_value(key_path)
    }
}
