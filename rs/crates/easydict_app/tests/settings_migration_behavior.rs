use easydict_app::compat_protocol::SettingsMigrateParams;
use easydict_app::{migrate_settings_file, migrate_settings_json, SettingsMigrationError};
use serde_json::Value;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[test]
fn settings_migration_normalizes_legacy_settings_shape() {
    let temp = TempDir::new("settings-migrate-normalizes");
    let source = temp.path().join("legacy.json");
    let target = temp.path().join("target.json");
    fs::write(
        &source,
        r#"{
  "WindowWidth": 640,
  "WindowHeight": 720,
  "MiniWindowXDips": 10,
  "MiniWindowYDips": 0,
  "UseLongDocWorker": false,
  "MiniWindowEnabledServices": ["google", "openvino-local-ai"],
  "MainWindowEnabledServices": ["openvino-local-ai"],
  "FixedWindowEnabledServices": ["google"],
  "MiniWindowServiceEnabledQuery": { "openvino-local-ai": true }
}"#,
    )
    .unwrap();

    let result = migrate_settings_file(&SettingsMigrateParams {
        legacy_settings_path: Some(source.to_string_lossy().to_string()),
        target_settings_path: Some(target.to_string_lossy().to_string()),
    })
    .unwrap();

    assert!(result.migrated);
    assert!(result.warnings.is_empty());

    let root = read_json(&target);
    assert_eq!(root["WindowWidthDips"], 640.0);
    assert_eq!(root["WindowHeightDips"], 720.0);
    assert_eq!(root["MiniWindowPositionSaved"], true);
    assert!(root.get("UseLongDocWorker").is_none());
    assert_eq!(root["LocalAIProvider"], "OpenVINO");
    assert_array_contains(&root["MiniWindowEnabledServices"], "windows-local-ai");
    assert_array_not_contains(&root["MiniWindowEnabledServices"], "openvino-local-ai");
    assert_eq!(
        root["MiniWindowServiceEnabledQuery"]["windows-local-ai"],
        true
    );
    assert!(root["MiniWindowServiceEnabledQuery"]
        .get("openvino-local-ai")
        .is_none());
}

#[test]
fn settings_migration_missing_source_returns_warning_without_writing_target() {
    let temp = TempDir::new("settings-migrate-missing");
    let source = temp.path().join("missing.json");
    let target = temp.path().join("target.json");

    let result = migrate_settings_file(&SettingsMigrateParams {
        legacy_settings_path: Some(source.to_string_lossy().to_string()),
        target_settings_path: Some(target.to_string_lossy().to_string()),
    })
    .unwrap();

    assert!(!result.migrated);
    assert_eq!(result.warnings.len(), 1);
    assert!(result.warnings[0].contains("Settings file not found:"));
    assert!(!target.exists());
}

#[test]
fn settings_migration_rejects_invalid_json() {
    let error = migrate_settings_json("{not-json}").unwrap_err();

    assert!(matches!(error, SettingsMigrationError::InvalidJson(_)));
    assert!(error
        .to_string()
        .contains("Settings file is not valid JSON"));
}

#[test]
fn settings_migration_rejects_non_object_root() {
    let error = migrate_settings_json("[]").unwrap_err();

    assert!(matches!(error, SettingsMigrationError::RootNotObject));
}

#[test]
fn settings_migration_target_copy_counts_as_migration_even_without_changes() {
    let temp = TempDir::new("settings-migrate-copy");
    let source = temp.path().join("legacy.json");
    let target = temp.path().join("nested").join("target.json");
    fs::write(&source, r#"{"WindowWidthDips": 900}"#).unwrap();

    let result = migrate_settings_file(&SettingsMigrateParams {
        legacy_settings_path: Some(source.to_string_lossy().to_string()),
        target_settings_path: Some(target.to_string_lossy().to_string()),
    })
    .unwrap();

    assert!(result.migrated);
    assert_eq!(read_json(&target)["WindowWidthDips"], 900.0);
}

#[test]
fn settings_migration_preserves_existing_new_keys() {
    let (json, changed) = migrate_settings_json(
        r#"{
  "WindowWidth": 640,
  "WindowWidthDips": 800,
  "MiniWindowXDips": 50,
  "MiniWindowPositionSaved": false
}"#,
    )
    .unwrap();

    let root = serde_json::from_str::<Value>(&json).unwrap();
    assert!(!changed);
    assert_eq!(root["WindowWidthDips"], 800.0);
    assert_eq!(root["MiniWindowPositionSaved"], false);
}

#[test]
fn settings_migration_removes_duplicate_openvino_service_when_windows_local_ai_exists() {
    let (json, changed) = migrate_settings_json(
        r#"{
  "MainWindowEnabledServices": ["windows-local-ai", "openvino-local-ai"],
  "MainWindowServiceEnabledQuery": {
    "windows-local-ai": false,
    "openvino-local-ai": true
  }
}"#,
    )
    .unwrap();

    let root = serde_json::from_str::<Value>(&json).unwrap();
    assert!(changed);
    assert_array_contains(&root["MainWindowEnabledServices"], "windows-local-ai");
    assert_array_not_contains(&root["MainWindowEnabledServices"], "openvino-local-ai");
    assert_eq!(
        root["MainWindowServiceEnabledQuery"]["windows-local-ai"],
        false
    );
    assert!(root.get("LocalAIProvider").is_none());
}

fn read_json(path: &Path) -> Value {
    serde_json::from_str(&fs::read_to_string(path).unwrap()).unwrap()
}

fn assert_array_contains(value: &Value, expected: &str) {
    assert!(
        value
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item.as_str() == Some(expected)),
        "expected array to contain {expected}: {value}"
    );
}

fn assert_array_not_contains(value: &Value, expected: &str) {
    assert!(
        !value
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item.as_str() == Some(expected)),
        "expected array not to contain {expected}: {value}"
    );
}

struct TempDir {
    path: PathBuf,
}

impl TempDir {
    fn new(label: &str) -> Self {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path =
            std::env::temp_dir().join(format!("easydict-{label}-{}-{unique}", std::process::id()));
        fs::create_dir_all(&path).unwrap();
        Self { path }
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}
