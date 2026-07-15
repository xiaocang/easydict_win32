#![cfg(feature = "parity-diagnostics")]

use std::collections::BTreeMap;
use std::fmt;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Component, Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

pub const CONTROL_REQUEST_SCHEMA: &str = "easydict.preview-control.v1";
pub const CONTROL_ACK_SCHEMA: &str = "easydict.preview-ack.v1";
pub const RUNTIME_DIAGNOSTICS_SCHEMA: &str = "easydict.winfluent.runtime-diagnostics.v1";
pub const MAX_REQUEST_BYTES: usize = 1024 * 1024;
pub const CONTROL_EVENT_ENV: &str = "EASYDICT_PREVIEW_CONTROL_EVENT";
pub const CONTROL_REQUEST_PATH_ENV: &str = "EASYDICT_PREVIEW_CONTROL_REQUEST_PATH";
pub const CONTROL_ACK_PATH_ENV: &str = "EASYDICT_PREVIEW_CONTROL_ACK_PATH";
pub const CONTROL_OUTPUT_ROOT_ENV: &str = "EASYDICT_PREVIEW_CONTROL_OUTPUT_ROOT";
pub const CONTROL_SESSION_ID_ENV: &str = "EASYDICT_PREVIEW_CONTROL_SESSION_ID";

const SESSION_FIXED_KEYS: [&str; 4] = [
    "EASYDICT_PREVIEW_WINDOW",
    "EASYDICT_PREVIEW_THEME",
    "EASYDICT_PREVIEW_UI_LANGUAGE",
    "EASYDICT_PREVIEW_DPI",
];

pub const ERR_INVALID_SESSION: &str = "preview-invalid-session";
pub const ERR_STALE_GENERATION: &str = "preview-stale-generation";
pub const ERR_INVALID_ARTIFACT_STEM: &str = "preview-invalid-artifact-stem";
pub const ERR_INVALID_DIMENSIONS: &str = "preview-invalid-dimensions";
pub const ERR_REQUEST_TOO_LARGE: &str = "preview-request-too-large";
pub const ERR_MISSING_REQUIRED_CONTROL: &str = "preview-missing-required-control";
pub const ERR_RENDER_TIMEOUT: &str = "preview-render-timeout";
pub const ERR_SESSION_INVARIANT_MISMATCH: &str = "session-invariant-mismatch";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ControlLaunchSettings {
    pub event_name: String,
    pub request_path: PathBuf,
    pub ack_path: PathBuf,
    pub output_root: PathBuf,
    pub session_id: String,
}

impl ControlLaunchSettings {
    pub fn from_lookup(
        mut lookup: impl FnMut(&str) -> Option<String>,
    ) -> Result<Option<Self>, ControlError> {
        let event_name = lookup(CONTROL_EVENT_ENV);
        let request_path = lookup(CONTROL_REQUEST_PATH_ENV);
        let ack_path = lookup(CONTROL_ACK_PATH_ENV);
        let output_root = lookup(CONTROL_OUTPUT_ROOT_ENV);
        let session_id = lookup(CONTROL_SESSION_ID_ENV);
        let configured = event_name.is_some()
            || request_path.is_some()
            || ack_path.is_some()
            || output_root.is_some()
            || session_id.is_some();
        if !configured {
            return Ok(None);
        }

        let required = |name: &'static str, value: Option<String>| {
            value
                .filter(|value| !value.trim().is_empty())
                .ok_or_else(|| {
                    ControlError::new(ERR_INVALID_SESSION, format!("{name} is required"))
                })
        };
        Ok(Some(Self {
            event_name: required(CONTROL_EVENT_ENV, event_name)?,
            request_path: PathBuf::from(required(CONTROL_REQUEST_PATH_ENV, request_path)?),
            ack_path: PathBuf::from(required(CONTROL_ACK_PATH_ENV, ack_path)?),
            output_root: PathBuf::from(required(CONTROL_OUTPUT_ROOT_ENV, output_root)?),
            session_id: required(CONTROL_SESSION_ID_ENV, session_id)?,
        }))
    }
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
pub struct PreviewControlRequest {
    #[serde(rename = "sessionId")]
    pub session_id: String,
    pub generation: u64,
    pub command: String,
    pub scenario: String,
    #[serde(rename = "artifactStem")]
    pub artifact_stem: String,
    #[serde(rename = "widthDips", default)]
    pub width_dips: Option<f32>,
    #[serde(rename = "heightDips", default)]
    pub height_dips: Option<f32>,
    #[serde(default)]
    pub overrides: BTreeMap<String, String>,
    #[serde(rename = "requiredControlIds", default)]
    pub required_control_ids: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
pub struct PreviewControlAck {
    pub schema: String,
    #[serde(rename = "sessionId")]
    pub session_id: String,
    pub generation: u64,
    pub status: String,
    #[serde(rename = "errorCode")]
    pub error_code: Option<String>,
    pub message: Option<String>,
    #[serde(rename = "artifactPaths", default)]
    pub artifact_paths: BTreeMap<String, String>,
    #[serde(rename = "observedControlIds", default)]
    pub observed_control_ids: Vec<String>,
    #[serde(rename = "missingControlIds", default)]
    pub missing_control_ids: Vec<String>,
    #[serde(rename = "renderDurationMs", default)]
    pub render_duration_ms: Option<u64>,
}

impl PreviewControlAck {
    pub fn rendered(
        session_id: impl Into<String>,
        generation: u64,
        artifact_paths: BTreeMap<String, String>,
        observed_control_ids: Vec<String>,
        missing_control_ids: Vec<String>,
        render_duration_ms: u64,
    ) -> Self {
        Self {
            schema: CONTROL_ACK_SCHEMA.to_string(),
            session_id: session_id.into(),
            generation,
            status: "rendered".to_string(),
            error_code: None,
            message: None,
            artifact_paths,
            observed_control_ids,
            missing_control_ids,
            render_duration_ms: Some(render_duration_ms),
        }
    }

    pub fn error(
        session_id: impl Into<String>,
        generation: u64,
        code: impl Into<String>,
        message: impl Into<String>,
    ) -> Self {
        Self {
            schema: CONTROL_ACK_SCHEMA.to_string(),
            session_id: session_id.into(),
            generation,
            status: "error".to_string(),
            error_code: Some(code.into()),
            message: Some(message.into()),
            artifact_paths: BTreeMap::new(),
            observed_control_ids: Vec::new(),
            missing_control_ids: Vec::new(),
            render_duration_ms: None,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ControlError {
    pub code: &'static str,
    pub message: String,
}

impl ControlError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

impl fmt::Display for ControlError {
    fn fmt(&self, output: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(output, "{}: {}", self.code, self.message)
    }
}

impl std::error::Error for ControlError {}

#[derive(Clone, Debug, Default)]
pub struct PreviewControlState {
    session_id: Option<String>,
    last_generation: u64,
}

impl PreviewControlState {
    pub fn new(session_id: impl Into<String>) -> Self {
        Self {
            session_id: Some(session_id.into()),
            last_generation: 0,
        }
    }

    pub fn last_generation(&self) -> u64 {
        self.last_generation
    }

    pub fn validate_and_accept(
        &mut self,
        request: &PreviewControlRequest,
        output_root: &Path,
    ) -> Result<(), ControlError> {
        validate_request(
            request,
            self.session_id.as_deref(),
            self.last_generation,
            output_root,
        )?;
        self.last_generation = request.generation;
        Ok(())
    }
}
pub fn request_environment(
    startup: &BTreeMap<String, String>,
    request: &PreviewControlRequest,
) -> Result<BTreeMap<String, String>, ControlError> {
    for key in SESSION_FIXED_KEYS {
        let Some(requested) = request.overrides.get(key) else {
            continue;
        };
        if startup.get(key) != Some(requested) {
            return Err(ControlError::new(
                ERR_SESSION_INVARIANT_MISMATCH,
                format!("{key} cannot change during a preview session"),
            ));
        }
    }

    let mut environment = startup.clone();
    environment.extend(request.overrides.clone());
    environment.insert(
        "EASYDICT_PREVIEW_SCENARIO".to_string(),
        request.scenario.clone(),
    );
    Ok(environment)
}

pub fn bounds_snapshot(
    generation: &win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration,
) -> String {
    let mut output = String::from("ViewBounds version=1\n");
    for control in &generation.controls {
        output.push_str(&format!(
            "Bounds id=\"{}\" kind={} x={:.2} y={:.2} width={:.2} height={:.2}\n",
            control.id.replace('\\', "\\\\").replace('"', "\\\""),
            control.kind,
            control.x,
            control.y,
            control.width,
            control.height,
        ));
    }
    output
}

pub fn parse_request(bytes: &[u8]) -> Result<PreviewControlRequest, ControlError> {
    if bytes.len() > MAX_REQUEST_BYTES {
        return Err(ControlError::new(
            ERR_REQUEST_TOO_LARGE,
            format!(
                "request is {} bytes; maximum is {MAX_REQUEST_BYTES}",
                bytes.len()
            ),
        ));
    }
    serde_json::from_slice(bytes).map_err(|error| {
        ControlError::new(
            ERR_INVALID_SESSION,
            format!("invalid request JSON: {error}"),
        )
    })
}

pub fn read_request(path: &Path) -> Result<PreviewControlRequest, ControlError> {
    let metadata = fs::metadata(path).map_err(|error| {
        ControlError::new(ERR_INVALID_SESSION, format!("request metadata: {error}"))
    })?;
    if metadata.len() > MAX_REQUEST_BYTES as u64 {
        return Err(ControlError::new(
            ERR_REQUEST_TOO_LARGE,
            format!(
                "request is {} bytes; maximum is {MAX_REQUEST_BYTES}",
                metadata.len()
            ),
        ));
    }
    let bytes = fs::read(path).map_err(|error| {
        ControlError::new(ERR_INVALID_SESSION, format!("request read: {error}"))
    })?;
    parse_request(&bytes)
}

pub fn validate_request(
    request: &PreviewControlRequest,
    expected_session_id: Option<&str>,
    last_generation: u64,
    output_root: &Path,
) -> Result<(), ControlError> {
    if request.session_id.trim().is_empty()
        || expected_session_id.is_some_and(|expected| expected != request.session_id)
    {
        return Err(ControlError::new(
            ERR_INVALID_SESSION,
            "request sessionId does not match the active preview session",
        ));
    }
    if request.generation <= last_generation {
        return Err(ControlError::new(
            ERR_STALE_GENERATION,
            format!(
                "generation {} is not greater than {last_generation}",
                request.generation
            ),
        ));
    }
    if request.command != "render" && request.command != "shutdown" {
        return Err(ControlError::new(
            ERR_INVALID_SESSION,
            format!("unsupported command {:?}", request.command),
        ));
    }
    if request.artifact_stem.is_empty()
        || !request
            .artifact_stem
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(ControlError::new(
            ERR_INVALID_ARTIFACT_STEM,
            "artifactStem must match [A-Za-z0-9._-]+",
        ));
    }
    if request
        .width_dips
        .is_some_and(|value| !value.is_finite() || value <= 0.0)
        || request
            .height_dips
            .is_some_and(|value| !value.is_finite() || value <= 0.0)
    {
        return Err(ControlError::new(
            ERR_INVALID_DIMENSIONS,
            "widthDips and heightDips must be finite and positive",
        ));
    }
    // Check the generated artifact path even for shutdown so a malformed
    // request cannot smuggle an unsafe path through the protocol parser.
    artifact_path(output_root, &request.artifact_stem, "ack.json")?;
    Ok(())
}

pub fn artifact_path(
    output_root: &Path,
    artifact_stem: &str,
    suffix: &str,
) -> Result<PathBuf, ControlError> {
    if output_root.as_os_str().is_empty() || artifact_stem.is_empty() {
        return Err(ControlError::new(
            ERR_INVALID_ARTIFACT_STEM,
            "output root and artifact stem are required",
        ));
    }
    let stem_path = Path::new(artifact_stem);
    if stem_path.is_absolute()
        || stem_path.components().any(|component| {
            matches!(
                component,
                Component::ParentDir | Component::RootDir | Component::Prefix(_)
            )
        })
    {
        return Err(ControlError::new(
            ERR_INVALID_ARTIFACT_STEM,
            "artifact path must remain under output root",
        ));
    }
    let candidate = output_root.join(format!("{artifact_stem}.{suffix}"));
    let root = absolute_path(output_root);
    let candidate_absolute = absolute_path(&candidate);
    if !candidate_absolute.starts_with(&root) {
        return Err(ControlError::new(
            ERR_INVALID_ARTIFACT_STEM,
            "artifact path escapes output root",
        ));
    }
    Ok(candidate)
}

fn absolute_path(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .unwrap_or_else(|_| PathBuf::from("."))
            .join(path)
    }
}

/// Writes one artifact through a temporary sibling followed by rename. The
/// renderer never calls this; preview tasks own all artifact I/O.
pub fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    fs::create_dir_all(parent)?;
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let temporary = parent.join(format!(
        ".{}.{}.tmp",
        path.file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("artifact"),
        nonce
    ));
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        fs::rename(&temporary, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

pub fn write_ack(path: &Path, ack: &PreviewControlAck) -> io::Result<()> {
    let bytes = serde_json::to_vec_pretty(ack).map_err(io::Error::other)?;
    atomic_write(path, &bytes)
}

pub fn write_runtime_artifacts(
    output_root: &Path,
    artifact_stem: &str,
    schema: &str,
    bounds: &str,
    diagnostics: &str,
) -> Result<BTreeMap<String, String>, ControlError> {
    let schema_path = artifact_path(output_root, artifact_stem, "schema")?;
    let bounds_path = artifact_path(output_root, artifact_stem, "bounds")?;
    let diagnostics_path = artifact_path(output_root, artifact_stem, "diagnostics")?;
    atomic_write(&schema_path, schema.as_bytes())
        .and_then(|_| atomic_write(&bounds_path, bounds.as_bytes()))
        .and_then(|_| atomic_write(&diagnostics_path, diagnostics.as_bytes()))
        .map_err(|error| {
            ControlError::new(ERR_INVALID_SESSION, format!("artifact write: {error}"))
        })?;
    Ok(BTreeMap::from([
        (
            "schema".to_string(),
            schema_path.to_string_lossy().into_owned(),
        ),
        (
            "bounds".to_string(),
            bounds_path.to_string_lossy().into_owned(),
        ),
        (
            "diagnostics".to_string(),
            diagnostics_path.to_string_lossy().into_owned(),
        ),
    ]))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request() -> PreviewControlRequest {
        PreviewControlRequest {
            session_id: "session".to_string(),
            generation: 1,
            command: "render".to_string(),
            scenario: "main.target-language-dropdown-open".to_string(),
            artifact_stem: "main-open".to_string(),
            width_dips: Some(900.0),
            height_dips: Some(700.0),
            overrides: BTreeMap::new(),
            required_control_ids: vec!["main.window".to_string(), "TargetLangCombo".to_string()],
        }
    }

    #[test]
    fn request_validation_enforces_session_and_monotonic_generation() {
        let root = std::env::temp_dir().join("easydict-preview-control-tests");
        let mut state = PreviewControlState::new("session");
        state.validate_and_accept(&request(), &root).unwrap();
        let stale = state.validate_and_accept(&request(), &root).unwrap_err();
        assert_eq!(stale.code, ERR_STALE_GENERATION);
        let mut invalid = request();
        invalid.session_id = "other".to_string();
        assert_eq!(
            PreviewControlState::new("session")
                .validate_and_accept(&invalid, &root)
                .unwrap_err()
                .code,
            ERR_INVALID_SESSION
        );
    }

    #[test]
    fn request_validation_rejects_path_traversal_and_bad_dimensions() {
        let root = std::env::temp_dir().join("easydict-preview-control-tests");
        let mut invalid = request();
        invalid.artifact_stem = "../escape".to_string();
        assert_eq!(
            PreviewControlState::new("session")
                .validate_and_accept(&invalid, &root)
                .unwrap_err()
                .code,
            ERR_INVALID_ARTIFACT_STEM
        );
        let mut invalid = request();
        invalid.width_dips = Some(f32::NAN);
        assert_eq!(
            PreviewControlState::new("session")
                .validate_and_accept(&invalid, &root)
                .unwrap_err()
                .code,
            ERR_INVALID_DIMENSIONS
        );
    }

    #[test]
    fn atomic_artifacts_round_trip() {
        let root =
            std::env::temp_dir().join(format!("easydict-preview-control-{}", std::process::id()));
        let paths =
            write_runtime_artifacts(&root, "run-1", "schema", "bounds", "diagnostics").unwrap();
        assert_eq!(fs::read(paths.get("schema").unwrap()).unwrap(), b"schema");
        let _ = fs::remove_dir_all(root);
    }
    #[test]
    fn one_session_consumes_two_generations_without_override_leakage() {
        let root = std::env::temp_dir().join(format!(
            "easydict-preview-control-two-generations-{}",
            std::process::id()
        ));
        let ack_path = root.join("ack.json");
        let request_path = root.join("request.json");
        let startup = BTreeMap::from([
            ("EASYDICT_PREVIEW_WINDOW".to_string(), "main".to_string()),
            ("EASYDICT_PREVIEW_THEME".to_string(), "light".to_string()),
            (
                "EASYDICT_PREVIEW_UI_LANGUAGE".to_string(),
                "zh-CN".to_string(),
            ),
            ("EASYDICT_PREVIEW_DPI".to_string(), "96".to_string()),
        ]);
        let mut control_state = PreviewControlState::new("session");

        let mut first_request = request();
        first_request.overrides.insert(
            "EASYDICT_PREVIEW_MAIN_OPEN_DROPDOWN".to_string(),
            "target".to_string(),
        );
        atomic_write(&request_path, &serde_json::to_vec(&first_request).unwrap()).unwrap();
        let first = read_request(&request_path).unwrap();
        let first_environment = request_environment(&startup, &first).unwrap();
        control_state.validate_and_accept(&first, &root).unwrap();
        let first_state = easydict_app::EasydictUiState::preview_from_lookup(|name| {
            first_environment.get(name).cloned()
        });
        assert_eq!(
            first_state.main_open_language_dropdown.as_deref(),
            Some("target")
        );
        write_ack(
            &ack_path,
            &PreviewControlAck::rendered(
                "session",
                first.generation,
                BTreeMap::new(),
                vec!["main.window".to_string(), "TargetLangCombo".to_string()],
                Vec::new(),
                1,
            ),
        )
        .unwrap();
        let first_ack: PreviewControlAck =
            serde_json::from_slice(&fs::read(&ack_path).unwrap()).unwrap();

        let mut second_request = request();
        second_request.generation = 2;
        second_request.scenario = "initial".to_string();
        second_request.artifact_stem = "main-initial".to_string();
        atomic_write(&request_path, &serde_json::to_vec(&second_request).unwrap()).unwrap();
        let second = read_request(&request_path).unwrap();
        let second_environment = request_environment(&startup, &second).unwrap();
        control_state.validate_and_accept(&second, &root).unwrap();
        let second_state = easydict_app::EasydictUiState::preview_from_lookup(|name| {
            second_environment.get(name).cloned()
        });
        assert_eq!(second_state.main_open_language_dropdown, None);
        assert!(!second_environment.contains_key("EASYDICT_PREVIEW_MAIN_OPEN_DROPDOWN"));
        write_ack(
            &ack_path,
            &PreviewControlAck::rendered(
                "session",
                second.generation,
                BTreeMap::new(),
                vec!["main.window".to_string(), "TargetLangCombo".to_string()],
                Vec::new(),
                1,
            ),
        )
        .unwrap();
        let second_ack: PreviewControlAck =
            serde_json::from_slice(&fs::read(&ack_path).unwrap()).unwrap();

        assert_eq!((first_ack.generation, second_ack.generation), (1, 2));
        assert_eq!(
            (first_ack.status.as_str(), second_ack.status.as_str()),
            ("rendered", "rendered")
        );
        assert_eq!(control_state.last_generation(), 2);
        let _ = fs::remove_dir_all(root);
    }
}
