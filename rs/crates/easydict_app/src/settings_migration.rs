use crate::compat_protocol::{SettingsMigrateParams, SettingsMigrateResult};
use serde_json::{Map, Value};
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

const LEGACY_OPENVINO_SERVICE_ID: &str = "openvino-local-ai";
const WINDOWS_LOCAL_AI_SERVICE_ID: &str = "windows-local-ai";
const SETTINGS_DIRECTORY_ENVIRONMENT_VARIABLE: &str = "EASYDICT_SETTINGS_DIR";

#[derive(Debug)]
pub enum SettingsMigrationError {
    InvalidJson(String),
    RootNotObject,
    Io(std::io::Error),
    Serialize(serde_json::Error),
}

impl fmt::Display for SettingsMigrationError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidJson(message) => {
                write!(formatter, "Settings file is not valid JSON: {message}")
            }
            Self::RootNotObject => formatter.write_str("Settings file root is not an object"),
            Self::Io(error) => write!(formatter, "Settings migration I/O failed: {error}"),
            Self::Serialize(error) => write!(formatter, "Settings serialization failed: {error}"),
        }
    }
}

impl From<std::io::Error> for SettingsMigrationError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<serde_json::Error> for SettingsMigrationError {
    fn from(error: serde_json::Error) -> Self {
        Self::Serialize(error)
    }
}

pub fn migrate_settings_file(
    params: &SettingsMigrateParams,
) -> Result<SettingsMigrateResult, SettingsMigrationError> {
    let source_path = resolve_source_path(params.legacy_settings_path.as_deref());
    let target_path = params
        .target_settings_path
        .as_deref()
        .filter(|path| !path.trim().is_empty())
        .map(resolve_expanded_full_path)
        .unwrap_or_else(|| source_path.clone());
    let mut warnings = Vec::new();

    if !source_path.exists() {
        warnings.push(format!(
            "Settings file not found: {}",
            source_path.display()
        ));
        return Ok(SettingsMigrateResult {
            migrated: false,
            warnings,
        });
    }

    let source_text = fs::read_to_string(&source_path)?;
    let mut root = parse_settings_object(&source_text)?;
    let changed = migrate_settings_object(&mut root);

    if changed || !paths_equal(&source_path, &target_path) {
        if let Some(parent) = target_path.parent() {
            if !parent.as_os_str().is_empty() {
                fs::create_dir_all(parent)?;
            }
        }

        let json = serde_json::to_string_pretty(&Value::Object(root))?;
        fs::write(&target_path, json)?;
    }

    Ok(SettingsMigrateResult {
        migrated: changed || !paths_equal(&source_path, &target_path),
        warnings,
    })
}

pub fn migrate_settings_json(json: &str) -> Result<(String, bool), SettingsMigrationError> {
    let mut root = parse_settings_object(json)?;
    let changed = migrate_settings_object(&mut root);
    let json = serde_json::to_string_pretty(&Value::Object(root))?;
    Ok((json, changed))
}

pub fn migrate_settings_object(root: &mut Map<String, Value>) -> bool {
    let mut changed = false;
    changed |= copy_legacy_number(root, "WindowWidth", "WindowWidthDips");
    changed |= copy_legacy_number(root, "WindowHeight", "WindowHeightDips");
    changed |= set_position_saved_from_coordinates(root, "MiniWindow");
    changed |= set_position_saved_from_coordinates(root, "FixedWindow");
    changed |= remove_runtime_only_worker_isolation_settings(root);
    changed |= migrate_standalone_openvino_service(root);
    changed
}

pub fn resolve_source_path(path: Option<&str>) -> PathBuf {
    path.filter(|value| !value.trim().is_empty())
        .map(resolve_expanded_full_path)
        .unwrap_or_else(default_settings_path)
}

fn parse_settings_object(json: &str) -> Result<Map<String, Value>, SettingsMigrationError> {
    let value = serde_json::from_str::<Value>(json)
        .map_err(|error| SettingsMigrationError::InvalidJson(error.to_string()))?;
    match value {
        Value::Object(root) => Ok(root),
        _ => Err(SettingsMigrationError::RootNotObject),
    }
}

fn copy_legacy_number(root: &mut Map<String, Value>, legacy_key: &str, new_key: &str) -> bool {
    if root.contains_key(new_key) {
        return false;
    }

    let Some(value) = root.get(legacy_key).and_then(Value::as_f64) else {
        return false;
    };

    root.insert(new_key.to_string(), Value::from(value));
    true
}

fn set_position_saved_from_coordinates(root: &mut Map<String, Value>, prefix: &str) -> bool {
    let key = format!("{prefix}PositionSaved");
    if root.contains_key(&key) {
        return false;
    }

    let x = root.get(&format!("{prefix}XDips")).and_then(Value::as_f64);
    let y = root.get(&format!("{prefix}YDips")).and_then(Value::as_f64);

    if x.is_none() && y.is_none() {
        return false;
    }

    root.insert(
        key,
        Value::Bool(x.unwrap_or_default() != 0.0 || y.unwrap_or_default() != 0.0),
    );
    true
}

fn remove_runtime_only_worker_isolation_settings(root: &mut Map<String, Value>) -> bool {
    let mut changed = false;
    changed |= root.remove("UseLongDocWorker").is_some();
    changed |= root.remove("UseLocalAiWorker").is_some();
    changed |= root.remove("UseOcrWorker").is_some();
    changed
}

fn migrate_standalone_openvino_service(root: &mut Map<String, Value>) -> bool {
    let mut changed = false;
    let list_keys = [
        "MiniWindowEnabledServices",
        "MainWindowEnabledServices",
        "FixedWindowEnabledServices",
    ];
    let dictionary_keys = [
        "MiniWindowServiceEnabledQuery",
        "MainWindowServiceEnabledQuery",
        "FixedWindowServiceEnabledQuery",
    ];

    let had_openvino = list_keys
        .iter()
        .any(|key| array_contains(root, key, LEGACY_OPENVINO_SERVICE_ID));
    let had_windows_local_ai = list_keys
        .iter()
        .any(|key| array_contains(root, key, WINDOWS_LOCAL_AI_SERVICE_ID));

    if had_openvino && !had_windows_local_ai && !root.contains_key("LocalAIProvider") {
        root.insert(
            "LocalAIProvider".to_string(),
            Value::String("OpenVINO".to_string()),
        );
        changed = true;
    }

    for key in list_keys {
        changed |= replace_string_in_array(
            root,
            key,
            LEGACY_OPENVINO_SERVICE_ID,
            WINDOWS_LOCAL_AI_SERVICE_ID,
        );
    }

    for key in dictionary_keys {
        changed |= move_dictionary_key(
            root,
            key,
            LEGACY_OPENVINO_SERVICE_ID,
            WINDOWS_LOCAL_AI_SERVICE_ID,
        );
    }

    changed
}

fn replace_string_in_array(
    root: &mut Map<String, Value>,
    key: &str,
    old_value: &str,
    new_value: &str,
) -> bool {
    let Some(Value::Array(array)) = root.get_mut(key) else {
        return false;
    };

    let mut changed = false;
    let mut has_new_value = array
        .iter()
        .any(|item| value_eq_ignore_case(item, new_value));
    let mut index = array.len();
    while index > 0 {
        index -= 1;
        if !value_eq_ignore_case(&array[index], old_value) {
            continue;
        }

        if has_new_value {
            array.remove(index);
        } else {
            array[index] = Value::String(new_value.to_string());
            has_new_value = true;
        }

        changed = true;
    }

    changed
}

fn move_dictionary_key(
    root: &mut Map<String, Value>,
    object_key: &str,
    old_key: &str,
    new_key: &str,
) -> bool {
    let Some(Value::Object(object)) = root.get_mut(object_key) else {
        return false;
    };

    let Some(existing_key) = object
        .keys()
        .find(|key| key.eq_ignore_ascii_case(old_key))
        .cloned()
    else {
        return false;
    };

    let value = object.remove(&existing_key);
    if !object.keys().any(|key| key.eq_ignore_ascii_case(new_key)) {
        if let Some(value) = value {
            object.insert(new_key.to_string(), value);
        }
    }

    true
}

fn array_contains(root: &Map<String, Value>, key: &str, value: &str) -> bool {
    root.get(key)
        .and_then(Value::as_array)
        .is_some_and(|array| array.iter().any(|item| value_eq_ignore_case(item, value)))
}

fn value_eq_ignore_case(value: &Value, expected: &str) -> bool {
    value
        .as_str()
        .is_some_and(|value| value.eq_ignore_ascii_case(expected))
}

fn default_settings_path() -> PathBuf {
    if let Some(settings_directory) = std::env::var(SETTINGS_DIRECTORY_ENVIRONMENT_VARIABLE)
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
    {
        return resolve_expanded_full_path(&settings_directory).join("settings.json");
    }

    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Easydict")
        .join("settings.json")
}

fn resolve_expanded_full_path(path: &str) -> PathBuf {
    let expanded = expand_environment_variables(path.trim());
    let path = PathBuf::from(expanded);
    if path.is_absolute() {
        path
    } else {
        std::env::current_dir()
            .unwrap_or_else(|_| PathBuf::from("."))
            .join(path)
    }
}

fn expand_environment_variables(path: &str) -> String {
    let mut result = String::with_capacity(path.len());
    let mut chars = path.char_indices().peekable();

    while let Some((_, character)) = chars.next() {
        if character != '%' {
            result.push(character);
            continue;
        }

        let mut name = String::new();
        let mut closed = false;
        for (_, next) in chars.by_ref() {
            if next == '%' {
                closed = true;
                break;
            }
            name.push(next);
        }

        if closed {
            if let Some(value) = std::env::var_os(&name) {
                result.push_str(&value.to_string_lossy());
            } else {
                result.push('%');
                result.push_str(&name);
                result.push('%');
            }
        } else {
            result.push('%');
            result.push_str(&name);
        }
    }

    result
}

fn paths_equal(left: &Path, right: &Path) -> bool {
    let left = left.to_string_lossy();
    let right = right.to_string_lossy();
    left.eq_ignore_ascii_case(&right)
}
