use regex::Regex;
use serde_json::Value;
use std::env;
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::OnceLock;
use std::time::{Duration, Instant, SystemTime};
use url::Url;

pub const FOUNDRY_LOCAL_DEFAULT_MODEL: &str = "qwen2.5-0.5b";
pub const FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE: &str = "EASYDICT_FOUNDRY_LOCAL_CLI";
const FOUNDRY_LOCAL_DEFAULT_CLI_EXECUTABLE_NAME: &str = "foundry";

pub const FOUNDRY_LOCAL_STATUS_READY: &str = "FoundryLocal_Status_Ready";
pub const FOUNDRY_LOCAL_STATUS_NOT_INSTALLED: &str = "FoundryLocal_Status_NotInstalled";
pub const FOUNDRY_LOCAL_STATUS_NOT_RUNNING: &str = "FoundryLocal_Status_NotRunning";
pub const FOUNDRY_LOCAL_STATUS_START_FAILED: &str = "FoundryLocal_Status_StartFailed";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FoundryLocalErrorCode {
    InvalidResponse,
    ServiceUnavailable,
    Timeout,
    NetworkError,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FoundryLocalError {
    pub code: FoundryLocalErrorCode,
    pub message: String,
}

impl FoundryLocalError {
    pub fn new(code: FoundryLocalErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

impl fmt::Display for FoundryLocalError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for FoundryLocalError {}

pub type FoundryLocalResult<T> = Result<T, FoundryLocalError>;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FoundryLocalPrepareOutcome {
    pub ready: bool,
    pub status_message: String,
    pub endpoint: Option<String>,
    pub model: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FoundryLocalRuntimeState {
    NotInstalled,
    NotRunning,
    Running,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FoundryLocalRuntimeStatus {
    pub state: FoundryLocalRuntimeState,
    pub endpoint: Option<String>,
    pub detail_message: Option<String>,
}

impl FoundryLocalRuntimeStatus {
    pub fn new(state: FoundryLocalRuntimeState) -> Self {
        Self {
            state,
            endpoint: None,
            detail_message: None,
        }
    }

    pub fn with_endpoint(state: FoundryLocalRuntimeState, endpoint: impl Into<String>) -> Self {
        Self {
            state,
            endpoint: Some(endpoint.into()),
            detail_message: None,
        }
    }

    pub fn with_detail(state: FoundryLocalRuntimeState, detail: impl Into<String>) -> Self {
        Self {
            state,
            endpoint: None,
            detail_message: Some(detail.into()),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FoundryLocalModelState {
    NotCompatible,
    NeedsPreparation,
    Ready,
    Failed,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FoundryLocalStatusCheck {
    pub state: FoundryLocalModelState,
    pub resource_key: &'static str,
    pub detail_message: Option<String>,
    pub endpoint: Option<String>,
}

pub trait FoundryLocalEndpointResolver {
    fn resolve_chat_completions_endpoint(&mut self) -> FoundryLocalResult<Option<String>>;
}

pub trait FoundryLocalRuntimeController: FoundryLocalEndpointResolver {
    fn get_status(&mut self) -> FoundryLocalResult<FoundryLocalRuntimeStatus>;

    fn start_service(&mut self) -> FoundryLocalResult<()>;

    fn load_model(&mut self, model: &str) -> FoundryLocalResult<()>;
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FoundryLocalSdkModel {
    pub alias: String,
    pub id: String,
    pub cached: bool,
}

impl FoundryLocalSdkModel {
    pub fn new(alias: impl Into<String>, id: impl Into<String>, cached: bool) -> Self {
        Self {
            alias: alias.into().trim().to_string(),
            id: id.into().trim().to_string(),
            cached,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FoundryLocalSdkPrepareOutcome {
    pub ready: bool,
    pub status_message: String,
    pub model_alias: String,
    pub model_id: Option<String>,
    pub downloaded_model: bool,
    pub execution_providers_registered: bool,
}

pub trait FoundryLocalSdkModelProvider {
    fn register_execution_providers(&mut self) -> FoundryLocalResult<bool>;

    fn resolve_model(&mut self, alias: &str) -> FoundryLocalResult<Option<FoundryLocalSdkModel>>;

    fn download_model(&mut self, model_id: &str) -> FoundryLocalResult<()>;

    fn load_model(&mut self, model_id: &str) -> FoundryLocalResult<()>;
}

#[cfg(feature = "sdk")]
pub struct FoundryLocalSdkProvider {
    manager: &'static foundry_local_sdk::FoundryLocalManager,
    runtime: tokio::runtime::Runtime,
}

#[cfg(feature = "sdk")]
impl FoundryLocalSdkProvider {
    pub fn new(app_name: impl AsRef<str>) -> FoundryLocalResult<Self> {
        let app_name = app_name.as_ref().trim();
        if app_name.is_empty() {
            return Err(FoundryLocalError::new(
                FoundryLocalErrorCode::InvalidResponse,
                "Foundry Local SDK app name cannot be empty",
            ));
        }

        let runtime = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .map_err(|error| {
                FoundryLocalError::new(
                    FoundryLocalErrorCode::NetworkError,
                    format!("Could not create Foundry Local SDK async runtime: {error}"),
                )
            })?;
        let config = foundry_local_sdk::FoundryLocalConfig::new(app_name);
        let manager =
            foundry_local_sdk::FoundryLocalManager::create(config).map_err(map_sdk_error)?;

        Ok(Self { manager, runtime })
    }
}

#[cfg(feature = "sdk")]
impl FoundryLocalSdkModelProvider for FoundryLocalSdkProvider {
    fn register_execution_providers(&mut self) -> FoundryLocalResult<bool> {
        self.runtime
            .block_on(self.manager.download_and_register_eps(None))
            .map(|outcome| outcome.success)
            .map_err(map_sdk_error)
    }

    fn resolve_model(&mut self, alias: &str) -> FoundryLocalResult<Option<FoundryLocalSdkModel>> {
        let alias = alias.trim();
        if alias.is_empty() {
            return Ok(None);
        }

        self.runtime
            .block_on(self.manager.catalog().get_model(alias))
            .and_then(|model| {
                let cached = self.runtime.block_on(model.is_cached())?;
                Ok(Some(FoundryLocalSdkModel::new(
                    model.alias(),
                    model.id(),
                    cached,
                )))
            })
            .map_err(map_sdk_error)
    }

    fn download_model(&mut self, model_id: &str) -> FoundryLocalResult<()> {
        let model_id = model_id.trim();
        if model_id.is_empty() {
            return Err(FoundryLocalError::new(
                FoundryLocalErrorCode::InvalidResponse,
                "Foundry Local SDK model id cannot be empty",
            ));
        }

        self.runtime
            .block_on(self.manager.catalog().get_model_variant(model_id))
            .and_then(|model| self.runtime.block_on(model.download(None::<fn(f64)>)))
            .map_err(map_sdk_error)
    }

    fn load_model(&mut self, model_id: &str) -> FoundryLocalResult<()> {
        let model_id = model_id.trim();
        if model_id.is_empty() {
            return Err(FoundryLocalError::new(
                FoundryLocalErrorCode::InvalidResponse,
                "Foundry Local SDK model id cannot be empty",
            ));
        }

        self.runtime
            .block_on(self.manager.catalog().get_model_variant(model_id))
            .and_then(|model| self.runtime.block_on(model.load()))
            .map_err(map_sdk_error)
    }
}

#[cfg(feature = "sdk")]
impl FoundryLocalEndpointResolver for FoundryLocalSdkProvider {
    fn resolve_chat_completions_endpoint(&mut self) -> FoundryLocalResult<Option<String>> {
        let endpoint = self
            .manager
            .urls()
            .map_err(map_sdk_error)?
            .into_iter()
            .filter_map(|url| normalized_optional(Some(&url)))
            .map(|url| normalize_foundry_local_chat_completions_endpoint(&url))
            .next();
        Ok(endpoint)
    }
}

#[cfg(feature = "sdk")]
impl FoundryLocalRuntimeController for FoundryLocalSdkProvider {
    fn get_status(&mut self) -> FoundryLocalResult<FoundryLocalRuntimeStatus> {
        match self.resolve_chat_completions_endpoint()? {
            Some(endpoint) => Ok(FoundryLocalRuntimeStatus::with_endpoint(
                FoundryLocalRuntimeState::Running,
                endpoint,
            )),
            None => Ok(FoundryLocalRuntimeStatus::new(
                FoundryLocalRuntimeState::NotRunning,
            )),
        }
    }

    fn start_service(&mut self) -> FoundryLocalResult<()> {
        self.runtime
            .block_on(self.manager.start_web_service())
            .map_err(map_sdk_error)
    }

    fn load_model(&mut self, model: &str) -> FoundryLocalResult<()> {
        let outcome = prepare_foundry_local_sdk_model(self, Some(model))?;
        if outcome.ready {
            return Ok(());
        }

        Err(FoundryLocalError::new(
            FoundryLocalErrorCode::ServiceUnavailable,
            outcome.status_message,
        ))
    }
}

#[cfg(feature = "sdk")]
fn map_sdk_error(error: foundry_local_sdk::FoundryLocalError) -> FoundryLocalError {
    let code = match &error {
        foundry_local_sdk::FoundryLocalError::LibraryLoad { .. }
        | foundry_local_sdk::FoundryLocalError::CommandExecution { .. }
        | foundry_local_sdk::FoundryLocalError::ModelOperation { .. } => {
            FoundryLocalErrorCode::ServiceUnavailable
        }
        foundry_local_sdk::FoundryLocalError::HttpRequest(_)
        | foundry_local_sdk::FoundryLocalError::Io(_) => FoundryLocalErrorCode::NetworkError,
        foundry_local_sdk::FoundryLocalError::InvalidConfiguration { .. }
        | foundry_local_sdk::FoundryLocalError::Serialization(_)
        | foundry_local_sdk::FoundryLocalError::Validation { .. }
        | foundry_local_sdk::FoundryLocalError::Internal { .. } => {
            FoundryLocalErrorCode::InvalidResponse
        }
    };
    FoundryLocalError::new(code, error.to_string())
}

#[derive(Clone, Debug)]
pub struct CommandFoundryLocalEndpointResolver {
    executable_name: String,
    blocked_executable_name: Option<String>,
    status_command_timeout: Duration,
    start_command_timeout: Duration,
    model_load_command_timeout: Duration,
}

impl Default for CommandFoundryLocalEndpointResolver {
    fn default() -> Self {
        let executable_name = env::var(FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE)
            .ok()
            .unwrap_or_else(|| FOUNDRY_LOCAL_DEFAULT_CLI_EXECUTABLE_NAME.to_string());

        Self::with_timeouts(
            executable_name,
            Duration::from_secs(8),
            Duration::from_secs(15),
            Duration::from_secs(180),
        )
    }
}

impl CommandFoundryLocalEndpointResolver {
    pub fn new(executable_name: impl Into<String>, command_timeout: Duration) -> Self {
        Self::with_timeouts(
            executable_name,
            command_timeout,
            command_timeout,
            command_timeout,
        )
    }

    pub fn with_timeouts(
        executable_name: impl Into<String>,
        status_command_timeout: Duration,
        start_command_timeout: Duration,
        model_load_command_timeout: Duration,
    ) -> Self {
        let requested_executable_name = executable_name.into();
        let (executable_name, blocked_executable_name) =
            validate_foundry_local_cli_executable_name(requested_executable_name);

        Self {
            executable_name,
            blocked_executable_name,
            status_command_timeout,
            start_command_timeout,
            model_load_command_timeout,
        }
    }

    fn run_foundry_command(
        &self,
        arguments: &[&str],
        command_timeout: Duration,
        require_success: bool,
    ) -> FoundryLocalResult<String> {
        self.ensure_cli_executable_allowed()?;
        let mut child = Command::new(&self.executable_name)
            .args(arguments)
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|error| {
                let message = if error.kind() == std::io::ErrorKind::NotFound {
                    "Foundry Local CLI is not installed or is not available on PATH.".to_string()
                } else {
                    format!("Could not run Foundry Local CLI: {error}")
                };
                FoundryLocalError::new(FoundryLocalErrorCode::ServiceUnavailable, message)
            })?;

        let deadline = Instant::now() + command_timeout;
        loop {
            match child.try_wait() {
                Ok(Some(_)) => break,
                Ok(None) if Instant::now() >= deadline => {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(FoundryLocalError::new(
                        FoundryLocalErrorCode::Timeout,
                        "Foundry Local CLI command timed out",
                    ));
                }
                Ok(None) => std::thread::sleep(Duration::from_millis(50)),
                Err(error) => {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(FoundryLocalError::new(
                        FoundryLocalErrorCode::NetworkError,
                        format!("Could not wait for Foundry Local CLI: {error}"),
                    ));
                }
            }
        }

        let output = child.wait_with_output().map_err(|error| {
            FoundryLocalError::new(
                FoundryLocalErrorCode::NetworkError,
                format!("Could not read Foundry Local CLI output: {error}"),
            )
        })?;

        let mut text = String::new();
        text.push_str(&String::from_utf8_lossy(&output.stdout));
        if !output.stderr.is_empty() {
            if !text.is_empty() {
                text.push('\n');
            }
            text.push_str(&String::from_utf8_lossy(&output.stderr));
        }
        if require_success && !output.status.success() {
            let command = arguments.join(" ");
            let message = if text.trim().is_empty() {
                format!("foundry {command} failed")
            } else {
                format!("foundry {command} failed: {}", text.trim())
            };
            return Err(FoundryLocalError::new(
                FoundryLocalErrorCode::ServiceUnavailable,
                message,
            ));
        }

        Ok(text)
    }

    fn run_status_command(&self, arguments: &[&str]) -> FoundryLocalResult<String> {
        self.run_foundry_command(arguments, self.status_command_timeout, false)
    }

    fn run_foundry_service_start_and_wait(&mut self) -> FoundryLocalResult<()> {
        self.ensure_cli_executable_allowed()?;
        let mut child = Command::new(&self.executable_name)
            .args(["service", "start"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|error| {
                let message = if error.kind() == std::io::ErrorKind::NotFound {
                    "Foundry Local CLI is not installed or is not available on PATH.".to_string()
                } else {
                    format!("Could not run Foundry Local CLI: {error}")
                };
                FoundryLocalError::new(FoundryLocalErrorCode::ServiceUnavailable, message)
            })?;

        let deadline = Instant::now() + self.start_command_timeout;
        let mut last_status: Option<FoundryLocalRuntimeStatus> = None;
        loop {
            if Instant::now() >= deadline {
                let _ = child.kill();
                let _ = child.wait();
                let detail = last_status
                    .and_then(|status| status.detail_message)
                    .unwrap_or_else(|| "no status reported".to_string());
                return Err(FoundryLocalError::new(
                    FoundryLocalErrorCode::Timeout,
                    format!("Foundry Local CLI command timed out. Latest status: {detail}"),
                ));
            }

            let status = self.get_status()?;
            if status.state == FoundryLocalRuntimeState::Running {
                return Ok(());
            }
            last_status = Some(status);

            if let Ok(Some(exit_status)) = child.try_wait() {
                if !exit_status.success() {
                    let detail = last_status
                        .as_ref()
                        .and_then(|status| status.detail_message.as_deref())
                        .unwrap_or("no status reported");
                    return Err(FoundryLocalError::new(
                        FoundryLocalErrorCode::ServiceUnavailable,
                        format!(
                            "foundry service start failed with exit code {}. Latest status: {detail}",
                            exit_status.code().unwrap_or(-1)
                        ),
                    ));
                }
            }

            let remaining = deadline.saturating_duration_since(Instant::now());
            std::thread::sleep(remaining.min(Duration::from_millis(300)));
        }
    }

    fn ensure_cli_executable_allowed(&self) -> FoundryLocalResult<()> {
        if let Some(blocked) = &self.blocked_executable_name {
            return Err(FoundryLocalError::new(
                FoundryLocalErrorCode::ServiceUnavailable,
                format!(
                    "{FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE} must point to the native Foundry Local CLI; retained runtime/worker command is disabled: {blocked}"
                ),
            ));
        }

        Ok(())
    }
}

impl FoundryLocalEndpointResolver for CommandFoundryLocalEndpointResolver {
    fn resolve_chat_completions_endpoint(&mut self) -> FoundryLocalResult<Option<String>> {
        for arguments in [
            ["service", "status"].as_slice(),
            ["service", "status", "--verbose"].as_slice(),
            ["service", "status", "--json"].as_slice(),
        ] {
            let output = self.run_status_command(arguments)?;
            if let Some(endpoint) = extract_foundry_local_chat_completions_endpoint(&output) {
                return Ok(Some(endpoint));
            }
        }

        for log_dir in foundry_local_default_log_dirs() {
            if let Some(endpoint) =
                extract_foundry_local_chat_completions_endpoint_from_logs(log_dir)
            {
                return Ok(Some(endpoint));
            }
        }

        Ok(None)
    }
}

impl FoundryLocalRuntimeController for CommandFoundryLocalEndpointResolver {
    fn get_status(&mut self) -> FoundryLocalResult<FoundryLocalRuntimeStatus> {
        match self.run_status_command(&["service", "status"]) {
            Ok(output) => Ok(parse_foundry_local_runtime_status(&output)),
            Err(error)
                if error.message
                    == "Foundry Local CLI is not installed or is not available on PATH." =>
            {
                Ok(FoundryLocalRuntimeStatus::with_detail(
                    FoundryLocalRuntimeState::NotInstalled,
                    error.message,
                ))
            }
            Err(error) => Err(error),
        }
    }

    fn start_service(&mut self) -> FoundryLocalResult<()> {
        self.run_foundry_service_start_and_wait()
    }

    fn load_model(&mut self, model: &str) -> FoundryLocalResult<()> {
        let model = model.trim();
        if model.is_empty() {
            return Err(FoundryLocalError::new(
                FoundryLocalErrorCode::InvalidResponse,
                "Foundry Local model is not configured",
            ));
        }

        self.run_foundry_command(
            &["model", "load", model],
            self.model_load_command_timeout,
            true,
        )?;
        Ok(())
    }
}

pub fn normalize_foundry_local_chat_completions_endpoint(endpoint: &str) -> String {
    let normalized = endpoint.trim().trim_end_matches('/');
    if normalized.is_empty() {
        return String::new();
    }

    if let Ok(mut url) = Url::parse(normalized) {
        let path = url.path().trim_end_matches('/').to_ascii_lowercase();
        if path == "/openai/status" || path == "/status" || path.starts_with("/openai/load/") {
            url.set_path("/v1/chat/completions");
            url.set_query(None);
            url.set_fragment(None);
            return url.to_string().trim_end_matches('/').to_string();
        }
    }

    if normalized
        .to_ascii_lowercase()
        .ends_with("/chat/completions")
    {
        return normalized.to_string();
    }

    if normalized.to_ascii_lowercase().ends_with("/v1") {
        return format!("{normalized}/chat/completions");
    }

    format!("{normalized}/v1/chat/completions")
}

pub fn foundry_local_models_endpoint_from_chat_completions_endpoint(
    endpoint: &str,
) -> Option<String> {
    let mut url = Url::parse(endpoint.trim()).ok()?;
    let path = url.path().trim_end_matches('/');
    let suffix = "/chat/completions";
    if !path.to_ascii_lowercase().ends_with(suffix) {
        return None;
    }

    let base_path = &path[..path.len().saturating_sub(suffix.len())];
    url.set_path(&format!("{base_path}/models"));
    url.set_query(None);
    url.set_fragment(None);
    Some(url.to_string().trim_end_matches('/').to_string())
}

pub fn try_resolve_foundry_local_model_id(
    model_list_json: &str,
    configured_model: &str,
) -> Option<String> {
    let configured_model = configured_model.trim();
    if configured_model.is_empty() || model_list_json.trim().is_empty() {
        return None;
    }

    let root: Value = serde_json::from_str(model_list_json).ok()?;
    let ids = root
        .get("data")
        .and_then(Value::as_array)?
        .iter()
        .filter_map(|model| model.get("id").and_then(Value::as_str))
        .filter_map(|id| normalized_optional(Some(id)))
        .collect::<Vec<_>>();

    if let Some(exact) = ids
        .iter()
        .find(|id| id.eq_ignore_ascii_case(configured_model))
    {
        return Some(exact.clone());
    }

    let alias_prefix = format!("{configured_model}-instruct-");
    ids.into_iter()
        .enumerate()
        .filter(|(_, id)| {
            id.to_ascii_lowercase()
                .starts_with(&alias_prefix.to_ascii_lowercase())
        })
        .min_by_key(|(index, id)| (foundry_local_model_device_preference(id), *index))
        .map(|(_, id)| id)
}

pub fn parse_foundry_local_runtime_status(output: &str) -> FoundryLocalRuntimeStatus {
    if output.trim().is_empty() {
        return FoundryLocalRuntimeStatus::new(FoundryLocalRuntimeState::NotRunning);
    }

    if let Some(endpoint) = extract_foundry_local_chat_completions_endpoint(output) {
        return FoundryLocalRuntimeStatus::with_endpoint(
            FoundryLocalRuntimeState::Running,
            endpoint,
        );
    }

    let detail = trim_foundry_local_status_output(output);
    if contains_foundry_local_missing_cli_status(output) {
        return FoundryLocalRuntimeStatus {
            state: FoundryLocalRuntimeState::NotInstalled,
            endpoint: None,
            detail_message: detail,
        };
    }

    if contains_foundry_local_not_running_status(output) {
        return FoundryLocalRuntimeStatus {
            state: FoundryLocalRuntimeState::NotRunning,
            endpoint: None,
            detail_message: detail,
        };
    }

    if output.to_ascii_lowercase().contains("running") {
        return FoundryLocalRuntimeStatus {
            state: FoundryLocalRuntimeState::Running,
            endpoint: None,
            detail_message: detail,
        };
    }

    FoundryLocalRuntimeStatus {
        state: FoundryLocalRuntimeState::NotRunning,
        endpoint: None,
        detail_message: detail,
    }
}

pub fn check_foundry_local_runtime_status<C: FoundryLocalRuntimeController>(
    controller: &mut C,
    configured_endpoint: Option<&str>,
) -> FoundryLocalResult<FoundryLocalStatusCheck> {
    let configured_endpoint = normalized_optional(configured_endpoint)
        .map(|endpoint| normalize_foundry_local_chat_completions_endpoint(&endpoint));

    if let Some(endpoint) = configured_endpoint.as_deref() {
        if !is_loopback_endpoint(endpoint) {
            return Ok(FoundryLocalStatusCheck {
                state: FoundryLocalModelState::Ready,
                resource_key: FOUNDRY_LOCAL_STATUS_READY,
                detail_message: None,
                endpoint: Some(endpoint.to_string()),
            });
        }
    }

    let runtime_status = match controller.get_status() {
        Ok(status) => status,
        Err(error) => {
            return Ok(FoundryLocalStatusCheck {
                state: FoundryLocalModelState::Failed,
                resource_key: FOUNDRY_LOCAL_STATUS_START_FAILED,
                detail_message: Some(error.message),
                endpoint: None,
            });
        }
    };

    match runtime_status.state {
        FoundryLocalRuntimeState::NotInstalled => Ok(FoundryLocalStatusCheck {
            state: FoundryLocalModelState::NotCompatible,
            resource_key: FOUNDRY_LOCAL_STATUS_NOT_INSTALLED,
            detail_message: runtime_status.detail_message,
            endpoint: None,
        }),
        FoundryLocalRuntimeState::NotRunning => Ok(FoundryLocalStatusCheck {
            state: FoundryLocalModelState::NeedsPreparation,
            resource_key: FOUNDRY_LOCAL_STATUS_NOT_RUNNING,
            detail_message: runtime_status.detail_message,
            endpoint: None,
        }),
        FoundryLocalRuntimeState::Running => {
            let endpoint = runtime_status
                .endpoint
                .as_deref()
                .map(normalize_foundry_local_chat_completions_endpoint)
                .or_else(|| {
                    controller
                        .resolve_chat_completions_endpoint()
                        .ok()
                        .flatten()
                        .map(|endpoint| {
                            normalize_foundry_local_chat_completions_endpoint(&endpoint)
                        })
                });

            match endpoint {
                Some(endpoint) => Ok(FoundryLocalStatusCheck {
                    state: FoundryLocalModelState::Ready,
                    resource_key: FOUNDRY_LOCAL_STATUS_READY,
                    detail_message: runtime_status.detail_message,
                    endpoint: Some(endpoint),
                }),
                None => Ok(FoundryLocalStatusCheck {
                    state: FoundryLocalModelState::Failed,
                    resource_key: FOUNDRY_LOCAL_STATUS_START_FAILED,
                    detail_message: Some(
                        "Foundry Local service is running but did not report a local endpoint."
                            .to_string(),
                    ),
                    endpoint: None,
                }),
            }
        }
    }
}

pub fn prepare_foundry_local_service<C: FoundryLocalRuntimeController>(
    controller: &mut C,
    configured_endpoint: Option<&str>,
    configured_model: Option<&str>,
) -> FoundryLocalResult<FoundryLocalPrepareOutcome> {
    let model = normalized_optional(configured_model)
        .unwrap_or_else(|| FOUNDRY_LOCAL_DEFAULT_MODEL.to_string());
    let configured_endpoint = normalized_optional(configured_endpoint);

    if let Some(endpoint) = configured_endpoint.as_deref() {
        let normalized = normalize_foundry_local_chat_completions_endpoint(endpoint);
        if !is_loopback_endpoint(&normalized) {
            return Ok(FoundryLocalPrepareOutcome {
                ready: true,
                status_message: "Foundry Local is configured with a user-managed endpoint."
                    .to_string(),
                endpoint: Some(normalized),
                model,
            });
        }
    }

    let status = controller.get_status()?;
    match status.state {
        FoundryLocalRuntimeState::NotInstalled => {
            return Ok(FoundryLocalPrepareOutcome {
                ready: false,
                status_message: status.detail_message.unwrap_or_else(|| {
                    "Foundry Local CLI is not installed or is not available on PATH.".to_string()
                }),
                endpoint: None,
                model,
            });
        }
        FoundryLocalRuntimeState::NotRunning => controller.start_service()?,
        FoundryLocalRuntimeState::Running => {}
    }

    controller.load_model(&model)?;
    let runtime_status = controller.get_status()?;
    let endpoint = if let Some(endpoint) = runtime_status
        .endpoint
        .as_deref()
        .map(normalize_foundry_local_chat_completions_endpoint)
    {
        Some(endpoint)
    } else {
        controller
            .resolve_chat_completions_endpoint()?
            .map(|endpoint| normalize_foundry_local_chat_completions_endpoint(&endpoint))
    };

    match endpoint {
        Some(endpoint) => Ok(FoundryLocalPrepareOutcome {
            ready: true,
            status_message: format!("Foundry Local is ready at {endpoint}."),
            endpoint: Some(endpoint),
            model,
        }),
        None => Ok(FoundryLocalPrepareOutcome {
            ready: false,
            status_message: "Foundry Local service is running but did not report a local endpoint."
                .to_string(),
            endpoint: None,
            model,
        }),
    }
}

pub fn prepare_foundry_local_sdk_model<P: FoundryLocalSdkModelProvider>(
    provider: &mut P,
    configured_model: Option<&str>,
) -> FoundryLocalResult<FoundryLocalSdkPrepareOutcome> {
    let model_alias = normalized_optional(configured_model)
        .unwrap_or_else(|| FOUNDRY_LOCAL_DEFAULT_MODEL.to_string());
    let execution_providers_registered = provider.register_execution_providers()?;
    if !execution_providers_registered {
        return Ok(FoundryLocalSdkPrepareOutcome {
            ready: false,
            status_message: "Foundry Local SDK execution-provider registration failed.".to_string(),
            model_alias,
            model_id: None,
            downloaded_model: false,
            execution_providers_registered,
        });
    }

    let Some(model) = provider.resolve_model(&model_alias)? else {
        return Ok(FoundryLocalSdkPrepareOutcome {
            ready: false,
            status_message: format!("Foundry Local SDK model '{model_alias}' was not found."),
            model_alias,
            model_id: None,
            downloaded_model: false,
            execution_providers_registered,
        });
    };
    let model_id = normalized_optional(Some(&model.id)).ok_or_else(|| {
        FoundryLocalError::new(
            FoundryLocalErrorCode::InvalidResponse,
            "Foundry Local SDK returned a model without an id",
        )
    })?;

    let downloaded_model = !model.cached;
    if downloaded_model {
        provider.download_model(&model_id)?;
    }
    provider.load_model(&model_id)?;

    Ok(FoundryLocalSdkPrepareOutcome {
        ready: true,
        status_message: format!("Foundry Local SDK model is ready: {model_id}."),
        model_alias,
        model_id: Some(model_id),
        downloaded_model,
        execution_providers_registered,
    })
}

pub fn extract_foundry_local_chat_completions_endpoint(output: &str) -> Option<String> {
    let mut candidates = Vec::new();
    for url in extract_urls(output) {
        let endpoint = normalize_foundry_local_chat_completions_endpoint(&url);
        if endpoint
            .to_ascii_lowercase()
            .contains("/v1/chat/completions")
            && !candidates
                .iter()
                .any(|candidate: &String| candidate.eq_ignore_ascii_case(&endpoint))
        {
            candidates.push(endpoint);
        }
    }

    candidates
        .iter()
        .find(|endpoint| {
            endpoint.contains("localhost")
                || endpoint.contains("127.0.0.1")
                || endpoint.contains("[::1]")
        })
        .cloned()
        .or_else(|| candidates.into_iter().next())
}

pub fn extract_foundry_local_chat_completions_endpoint_from_logs(
    log_dir: impl AsRef<Path>,
) -> Option<String> {
    let mut logs = fs::read_dir(log_dir)
        .ok()?
        .filter_map(Result::ok)
        .filter_map(|entry| {
            let path = entry.path();
            if !is_foundry_local_log_path(&path) {
                return None;
            }

            let modified = entry
                .metadata()
                .and_then(|metadata| metadata.modified())
                .unwrap_or(SystemTime::UNIX_EPOCH);
            Some((path, modified))
        })
        .collect::<Vec<_>>();

    logs.sort_by(|(_, left), (_, right)| right.cmp(left));
    logs.into_iter().find_map(|(path, _)| {
        let text = fs::read_to_string(path).ok()?;
        extract_foundry_local_chat_completions_endpoint(&text)
    })
}

fn normalized_optional(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
}

fn validate_foundry_local_cli_executable_name(value: String) -> (String, Option<String>) {
    validate_foundry_local_cli_executable_name_with_canonicalizer(value, |path| {
        fs::canonicalize(path).ok()
    })
}

fn validate_foundry_local_cli_executable_name_with_canonicalizer<F>(
    value: String,
    canonicalize: F,
) -> (String, Option<String>)
where
    F: Fn(&Path) -> Option<PathBuf>,
{
    let trimmed = value.trim().trim_matches('"').to_string();
    if trimmed.is_empty() {
        return (FOUNDRY_LOCAL_DEFAULT_CLI_EXECUTABLE_NAME.to_string(), None);
    }

    if is_retained_dotnet_runtime_or_worker_command(&trimmed) {
        return (
            FOUNDRY_LOCAL_DEFAULT_CLI_EXECUTABLE_NAME.to_string(),
            Some(trimmed),
        );
    }

    if let Some(canonical_path) = canonicalize(Path::new(&trimmed)) {
        let canonical = canonical_path.to_string_lossy().to_string();
        if is_retained_dotnet_runtime_or_worker_command(&canonical) {
            return (
                FOUNDRY_LOCAL_DEFAULT_CLI_EXECUTABLE_NAME.to_string(),
                Some(format!("{trimmed} -> {canonical}")),
            );
        }
    }

    (trimmed, None)
}

fn is_retained_dotnet_runtime_or_worker_command(value: &str) -> bool {
    let normalized = value.trim().trim_matches('"').replace('\\', "/");
    let lower = normalized.to_ascii_lowercase();
    let leaf = lower
        .rsplit('/')
        .next()
        .unwrap_or(lower.as_str())
        .split_whitespace()
        .next()
        .unwrap_or("");

    matches!(
        leaf,
        "dotnet"
            | "dotnet.exe"
            | "powershell"
            | "powershell.exe"
            | "pwsh"
            | "pwsh.exe"
            | "hostfxr.dll"
            | "hostpolicy.dll"
            | "coreclr.dll"
            | "clrjit.dll"
            | "singlefilehost.exe"
            | "system.private.corelib.dll"
    ) || lower.contains("easydict.compathost")
        || lower.contains("easydict.workers.")
        || lower.contains(".runtimeconfig.json")
        || has_retained_dotnet_runtime_or_worker_path_component(&lower)
        || lower.contains("/host/fxr/")
        || lower.contains(".ps1")
}

fn has_retained_dotnet_runtime_or_worker_path_component(value: &str) -> bool {
    value
        .split('/')
        .filter(|component| !component.is_empty())
        .any(|component| {
            matches!(
                component,
                "dotnet"
                    | "workers"
                    | "microsoft.netcore.app"
                    | "microsoft.windowsdesktop.app"
                    | "microsoft.aspnetcore.app"
            )
        })
}

fn foundry_local_default_log_dirs() -> Vec<PathBuf> {
    let mut dirs = Vec::new();
    for variable in ["USERPROFILE", "HOME"] {
        if let Some(home) = env::var_os(variable) {
            let path = PathBuf::from(home);
            if !path.as_os_str().is_empty()
                && !dirs.iter().any(|existing: &PathBuf| existing == &path)
            {
                dirs.push(path.join(".foundry").join("logs"));
            }
        }
    }
    dirs
}

fn contains_foundry_local_not_running_status(output: &str) -> bool {
    let output = output.to_ascii_lowercase();
    output.contains("not running")
        || output.contains("isn't running")
        || output.contains("is not running")
}

fn contains_foundry_local_missing_cli_status(output: &str) -> bool {
    let output = output.to_ascii_lowercase();
    output.contains("not recognized")
        || output.contains("command not found")
        || output.contains("executable file not found")
}

fn trim_foundry_local_status_output(output: &str) -> Option<String> {
    let text = output
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .map(sanitize_foundry_local_status_line)
        .filter(|line| !line.trim().is_empty())
        .collect::<Vec<_>>()
        .join("\n")
        .trim()
        .to_string();

    if text.is_empty() {
        None
    } else {
        Some(text.chars().take(512).collect())
    }
}

fn sanitize_foundry_local_status_line(line: &str) -> String {
    const STATUS_LINE_ANCHORS: &[&str] = &[
        "Model management service",
        "Foundry Local service",
        "To start the service",
    ];

    let text = ansi_escape_regex().replace_all(line, "").trim().to_string();
    for anchor in STATUS_LINE_ANCHORS {
        let Some(index) = text.to_ascii_lowercase().find(&anchor.to_ascii_lowercase()) else {
            continue;
        };
        if index > 0 && !contains_ascii_letter_or_digit(&text[..index]) {
            return text[index..].trim().to_string();
        }
    }

    text
}

fn contains_ascii_letter_or_digit(text: &str) -> bool {
    text.bytes().any(|byte| byte.is_ascii_alphanumeric())
}

fn ansi_escape_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(r"\x1B\[[0-?]*[ -/]*[@-~]").expect("ANSI escape regex should compile")
    })
}

fn is_foundry_local_log_path(path: &Path) -> bool {
    let Some(file_name) = path.file_name().and_then(|name| name.to_str()) else {
        return false;
    };
    let file_name = file_name.to_ascii_lowercase();
    file_name.starts_with("foundry") && file_name.ends_with(".log")
}

fn foundry_local_model_device_preference(model_id: &str) -> usize {
    let model_id = model_id.to_ascii_lowercase();
    if model_id.contains("openvino-npu") || model_id.contains("-npu") {
        return 0;
    }

    if model_id.contains("openvino-gpu") || model_id.contains("-gpu") {
        return 1;
    }

    if model_id.contains("-cpu") {
        return 2;
    }

    3
}

fn extract_urls(text: &str) -> Vec<String> {
    let mut urls = Vec::new();
    let mut offset = 0;
    while let Some(index) = find_next_url_start(&text[offset..]) {
        let start = offset + index;
        let rest = &text[start..];
        let end = rest
            .find(|ch: char| ch.is_whitespace() || matches!(ch, '"' | '\'' | '<' | '>'))
            .unwrap_or(rest.len());
        let url = rest[..end].trim_end_matches(['.', ',', ';', ')', ']']);
        if !url.is_empty() {
            urls.push(url.to_string());
        }
        offset = start + end.max(1);
    }
    urls
}

fn find_next_url_start(text: &str) -> Option<usize> {
    match (text.find("http://"), text.find("https://")) {
        (Some(http), Some(https)) => Some(http.min(https)),
        (Some(http), None) => Some(http),
        (None, Some(https)) => Some(https),
        (None, None) => None,
    }
}

fn is_loopback_url(url: &Url) -> bool {
    url.host_str()
        .map(|host| {
            host.eq_ignore_ascii_case("localhost")
                || host == "127.0.0.1"
                || host == "::1"
                || host.starts_with("127.")
        })
        .unwrap_or(false)
}

fn is_loopback_endpoint(endpoint: &str) -> bool {
    Url::parse(endpoint)
        .ok()
        .as_ref()
        .is_some_and(is_loopback_url)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;

    #[cfg(feature = "sdk")]
    #[test]
    fn sdk_provider_stays_inside_lib_owned_runtime_and_model_traits() {
        fn assert_provider_traits<T>()
        where
            T: FoundryLocalRuntimeController + FoundryLocalSdkModelProvider,
        {
        }

        assert_provider_traits::<FoundryLocalSdkProvider>();
    }

    #[test]
    fn normalizes_foundry_local_chat_completion_endpoints() {
        assert_eq!(
            normalize_foundry_local_chat_completions_endpoint("http://127.0.0.1:5273"),
            "http://127.0.0.1:5273/v1/chat/completions"
        );
        assert_eq!(
            normalize_foundry_local_chat_completions_endpoint("http://127.0.0.1:5273/v1"),
            "http://127.0.0.1:5273/v1/chat/completions"
        );
        assert_eq!(
            normalize_foundry_local_chat_completions_endpoint(
                "http://127.0.0.1:5273/openai/status?x=1#old"
            ),
            "http://127.0.0.1:5273/v1/chat/completions"
        );
    }

    #[test]
    fn extracts_loopback_endpoint_from_status_output() {
        let output = "Foundry Local endpoint: https://example.test/v1\n\
            local: http://127.0.0.1:5273/openai/status";

        assert_eq!(
            extract_foundry_local_chat_completions_endpoint(output).as_deref(),
            Some("http://127.0.0.1:5273/v1/chat/completions")
        );
    }

    #[test]
    fn derives_models_endpoint_from_chat_completions_endpoint() {
        assert_eq!(
            foundry_local_models_endpoint_from_chat_completions_endpoint(
                "http://127.0.0.1:5273/v1/chat/completions?ignored=1#frag"
            )
            .as_deref(),
            Some("http://127.0.0.1:5273/v1/models")
        );
        assert!(
            foundry_local_models_endpoint_from_chat_completions_endpoint(
                "http://127.0.0.1:5273/v1/models"
            )
            .is_none()
        );
    }

    #[test]
    fn resolves_model_alias_with_device_preference() {
        let models = r#"{
            "data": [
                {"id": "qwen2.5-0.5b-instruct-generic-cpu"},
                {"id": "qwen2.5-0.5b-instruct-openvino-gpu"},
                {"id": "qwen2.5-0.5b-instruct-openvino-npu"}
            ]
        }"#;

        assert_eq!(
            try_resolve_foundry_local_model_id(models, "qwen2.5-0.5b").as_deref(),
            Some("qwen2.5-0.5b-instruct-openvino-npu")
        );
        assert_eq!(
            try_resolve_foundry_local_model_id(
                r#"{"data":[{"id":"QWEN2.5-0.5B"}]}"#,
                "qwen2.5-0.5b"
            )
            .as_deref(),
            Some("QWEN2.5-0.5B")
        );
        assert!(try_resolve_foundry_local_model_id("not json", "qwen2.5-0.5b").is_none());
    }

    #[test]
    fn parses_runtime_status_from_cli_output() {
        let stopped = parse_foundry_local_runtime_status(
            "Foundry Local service is not running.\nTo start the service, run foundry service start.",
        );
        assert_eq!(stopped.state, FoundryLocalRuntimeState::NotRunning);

        let missing = parse_foundry_local_runtime_status(
            "'foundry' is not recognized as an internal or external command",
        );
        assert_eq!(missing.state, FoundryLocalRuntimeState::NotInstalled);

        let ready = parse_foundry_local_runtime_status(
            "Foundry Local endpoint: http://localhost:5273/v1/chat/completions",
        );
        assert_eq!(ready.state, FoundryLocalRuntimeState::Running);
        assert_eq!(
            ready.endpoint.as_deref(),
            Some("http://localhost:5273/v1/chat/completions")
        );
    }

    #[test]
    fn cli_executable_override_rejects_retained_dotnet_runtime_commands() {
        for blocked in [
            "dotnet.exe",
            r"C:\Program Files\dotnet\dotnet.exe",
            "pwsh",
            "powershell.exe",
            "C:/Easydict/Easydict.CompatHost.exe",
            "C:/Easydict/workers/localai/Easydict.Workers.LocalAi.exe",
            "C:/Easydict/Easydict.Workers.LocalAi.runtimeconfig.json",
            "C:/Easydict/dotnet/host/fxr/8.0.11/hostfxr.dll",
            "hostpolicy.dll",
            "coreclr.dll",
            "clrjit.dll",
            "singlefilehost.exe",
            "C:/Easydict/scripts/launch-localai.ps1",
        ] {
            let mut resolver =
                CommandFoundryLocalEndpointResolver::new(blocked, Duration::from_millis(1));
            assert_eq!(resolver.executable_name, "foundry");
            assert_eq!(resolver.blocked_executable_name.as_deref(), Some(blocked));

            let error = resolver
                .get_status()
                .expect_err("blocked CLI override should fail before spawning");
            assert_eq!(error.code, FoundryLocalErrorCode::ServiceUnavailable);
            assert!(error.message.contains("retained runtime/worker"));
            assert!(error.message.contains(blocked));
        }
    }

    #[test]
    fn cli_executable_override_rejects_canonical_retained_dotnet_runtime_target() {
        let requested = r"C:\Tools\Foundry\foundry.exe".to_string();
        let canonical =
            PathBuf::from(r"C:\Easydict\dotnet\shared\Microsoft.NETCore.App\8.0.11\foundry.exe");

        let (executable_name, blocked_executable_name) =
            validate_foundry_local_cli_executable_name_with_canonicalizer(
                requested.clone(),
                |_| Some(canonical.clone()),
            );

        assert_eq!(executable_name, "foundry");
        let blocked =
            blocked_executable_name.expect("canonical retained runtime target should be blocked");
        assert!(blocked.contains(&requested));
        assert!(blocked.contains("Microsoft.NETCore.App"));
    }

    #[test]
    fn cli_executable_override_allows_native_foundry_names() {
        for allowed in ["foundry", "foundry.exe", "C:/Tools/Foundry/foundry.exe"] {
            let resolver =
                CommandFoundryLocalEndpointResolver::new(allowed, Duration::from_millis(1));
            assert_eq!(resolver.executable_name, allowed);
            assert_eq!(resolver.blocked_executable_name, None);
        }

        let resolver = CommandFoundryLocalEndpointResolver::new("   ", Duration::from_millis(1));
        assert_eq!(resolver.executable_name, "foundry");
        assert_eq!(resolver.blocked_executable_name, None);
    }

    #[test]
    fn extracts_latest_endpoint_from_logs() {
        let temp_dir = unique_temp_dir("easydict-foundry-local-logs");
        fs::create_dir_all(&temp_dir).expect("temp dir should be created");
        fs::write(
            temp_dir.join("foundry-old.log"),
            "http://127.0.0.1:1111/v1/chat/completions",
        )
        .expect("old log should be written");
        std::thread::sleep(Duration::from_millis(5));
        fs::write(
            temp_dir.join("foundry-new.log"),
            "http://127.0.0.1:2222/v1/chat/completions",
        )
        .expect("new log should be written");

        assert_eq!(
            extract_foundry_local_chat_completions_endpoint_from_logs(&temp_dir).as_deref(),
            Some("http://127.0.0.1:2222/v1/chat/completions")
        );

        fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
    }

    #[test]
    fn status_check_preserves_user_managed_endpoint_without_cli_probe() {
        let mut controller = RecordingController::default();
        let check = check_foundry_local_runtime_status(
            &mut controller,
            Some("https://example.test/foundry/v1"),
        )
        .expect("status check");

        assert_eq!(check.state, FoundryLocalModelState::Ready);
        assert_eq!(
            check.endpoint.as_deref(),
            Some("https://example.test/foundry/v1/chat/completions")
        );
        assert_eq!(controller.status_calls, 0);
    }

    #[test]
    fn prepare_starts_service_loads_model_and_resolves_endpoint() {
        let mut controller = RecordingController::with_statuses([
            Ok(FoundryLocalRuntimeStatus::new(
                FoundryLocalRuntimeState::NotRunning,
            )),
            Ok(FoundryLocalRuntimeStatus::with_endpoint(
                FoundryLocalRuntimeState::Running,
                "http://127.0.0.1:5273/v1",
            )),
        ]);

        let outcome = prepare_foundry_local_service(&mut controller, None, Some("phi-3-mini"))
            .expect("prepare outcome");

        assert!(outcome.ready);
        assert_eq!(outcome.model, "phi-3-mini");
        assert_eq!(
            outcome.endpoint.as_deref(),
            Some("http://127.0.0.1:5273/v1/chat/completions")
        );
        assert_eq!(controller.start_calls, 1);
        assert_eq!(controller.loaded_models, vec!["phi-3-mini"]);
    }

    #[test]
    fn sdk_prepare_registers_eps_downloads_uncached_model_and_loads_it() {
        let mut provider = RecordingSdkProvider::with_models([Some(FoundryLocalSdkModel::new(
            "qwen2.5-0.5b",
            "qwen2.5-0.5b-instruct-npu",
            false,
        ))]);

        let outcome =
            prepare_foundry_local_sdk_model(&mut provider, Some("qwen2.5-0.5b")).expect("prepare");

        assert_eq!(
            outcome,
            FoundryLocalSdkPrepareOutcome {
                ready: true,
                status_message: "Foundry Local SDK model is ready: qwen2.5-0.5b-instruct-npu."
                    .to_string(),
                model_alias: "qwen2.5-0.5b".to_string(),
                model_id: Some("qwen2.5-0.5b-instruct-npu".to_string()),
                downloaded_model: true,
                execution_providers_registered: true,
            }
        );
        assert_eq!(
            provider.events,
            [
                "register-eps",
                "resolve:qwen2.5-0.5b",
                "download:qwen2.5-0.5b-instruct-npu",
                "load:qwen2.5-0.5b-instruct-npu",
            ]
        );
    }

    #[test]
    fn sdk_prepare_uses_default_alias_and_skips_download_when_model_is_cached() {
        let mut provider = RecordingSdkProvider::with_models([Some(FoundryLocalSdkModel::new(
            FOUNDRY_LOCAL_DEFAULT_MODEL,
            "qwen2.5-0.5b-instruct-cpu",
            true,
        ))]);

        let outcome = prepare_foundry_local_sdk_model(&mut provider, Some(" ")).expect("prepare");

        assert_eq!(outcome.model_alias, FOUNDRY_LOCAL_DEFAULT_MODEL);
        assert_eq!(
            outcome.model_id.as_deref(),
            Some("qwen2.5-0.5b-instruct-cpu")
        );
        assert!(!outcome.downloaded_model);
        assert_eq!(
            provider.events,
            [
                "register-eps",
                "resolve:qwen2.5-0.5b",
                "load:qwen2.5-0.5b-instruct-cpu",
            ]
        );
    }

    #[test]
    fn sdk_prepare_reports_missing_alias_without_download_or_load() {
        let mut provider = RecordingSdkProvider::with_models([None]);

        let outcome =
            prepare_foundry_local_sdk_model(&mut provider, Some("missing-alias")).expect("prepare");

        assert_eq!(
            outcome,
            FoundryLocalSdkPrepareOutcome {
                ready: false,
                status_message: "Foundry Local SDK model 'missing-alias' was not found."
                    .to_string(),
                model_alias: "missing-alias".to_string(),
                model_id: None,
                downloaded_model: false,
                execution_providers_registered: true,
            }
        );
        assert_eq!(provider.events, ["register-eps", "resolve:missing-alias"]);
    }

    #[test]
    fn sdk_prepare_stops_when_execution_provider_registration_fails() {
        let mut provider = RecordingSdkProvider::with_models([Some(FoundryLocalSdkModel::new(
            "qwen2.5-0.5b",
            "qwen2.5-0.5b-instruct-npu",
            false,
        ))])
        .with_execution_provider_registration(false);

        let outcome =
            prepare_foundry_local_sdk_model(&mut provider, Some("qwen2.5-0.5b")).expect("prepare");

        assert_eq!(
            outcome,
            FoundryLocalSdkPrepareOutcome {
                ready: false,
                status_message: "Foundry Local SDK execution-provider registration failed."
                    .to_string(),
                model_alias: "qwen2.5-0.5b".to_string(),
                model_id: None,
                downloaded_model: false,
                execution_providers_registered: false,
            }
        );
        assert_eq!(provider.events, ["register-eps"]);
    }

    #[test]
    fn sdk_prepare_rejects_model_without_id_before_download() {
        let mut provider = RecordingSdkProvider::with_models([Some(FoundryLocalSdkModel::new(
            "qwen2.5-0.5b",
            " ",
            false,
        ))]);

        let error = prepare_foundry_local_sdk_model(&mut provider, Some("qwen2.5-0.5b"))
            .expect_err("missing SDK model id should fail");

        assert_eq!(error.code, FoundryLocalErrorCode::InvalidResponse);
        assert_eq!(provider.events, ["register-eps", "resolve:qwen2.5-0.5b"]);
    }

    #[derive(Default)]
    struct RecordingController {
        endpoints: VecDeque<FoundryLocalResult<Option<String>>>,
        statuses: VecDeque<FoundryLocalResult<FoundryLocalRuntimeStatus>>,
        loaded_models: Vec<String>,
        status_calls: usize,
        start_calls: usize,
    }

    impl RecordingController {
        fn with_statuses(
            statuses: impl IntoIterator<Item = FoundryLocalResult<FoundryLocalRuntimeStatus>>,
        ) -> Self {
            Self {
                statuses: statuses.into_iter().collect(),
                ..Self::default()
            }
        }
    }

    impl FoundryLocalEndpointResolver for RecordingController {
        fn resolve_chat_completions_endpoint(&mut self) -> FoundryLocalResult<Option<String>> {
            self.endpoints.pop_front().unwrap_or(Ok(None))
        }
    }

    impl FoundryLocalRuntimeController for RecordingController {
        fn get_status(&mut self) -> FoundryLocalResult<FoundryLocalRuntimeStatus> {
            self.status_calls += 1;
            self.statuses.pop_front().unwrap_or_else(|| {
                Ok(FoundryLocalRuntimeStatus::new(
                    FoundryLocalRuntimeState::Running,
                ))
            })
        }

        fn start_service(&mut self) -> FoundryLocalResult<()> {
            self.start_calls += 1;
            Ok(())
        }

        fn load_model(&mut self, model: &str) -> FoundryLocalResult<()> {
            self.loaded_models.push(model.to_string());
            Ok(())
        }
    }

    struct RecordingSdkProvider {
        models: VecDeque<Option<FoundryLocalSdkModel>>,
        events: Vec<String>,
        execution_providers_registered: bool,
    }

    impl RecordingSdkProvider {
        fn with_models(models: impl IntoIterator<Item = Option<FoundryLocalSdkModel>>) -> Self {
            Self {
                models: models.into_iter().collect(),
                events: Vec::new(),
                execution_providers_registered: true,
            }
        }

        fn with_execution_provider_registration(mut self, registered: bool) -> Self {
            self.execution_providers_registered = registered;
            self
        }
    }

    impl FoundryLocalSdkModelProvider for RecordingSdkProvider {
        fn register_execution_providers(&mut self) -> FoundryLocalResult<bool> {
            self.events.push("register-eps".to_string());
            Ok(self.execution_providers_registered)
        }

        fn resolve_model(
            &mut self,
            alias: &str,
        ) -> FoundryLocalResult<Option<FoundryLocalSdkModel>> {
            self.events.push(format!("resolve:{alias}"));
            Ok(self.models.pop_front().flatten())
        }

        fn download_model(&mut self, model_id: &str) -> FoundryLocalResult<()> {
            self.events.push(format!("download:{model_id}"));
            Ok(())
        }

        fn load_model(&mut self, model_id: &str) -> FoundryLocalResult<()> {
            self.events.push(format!("load:{model_id}"));
            Ok(())
        }
    }

    fn unique_temp_dir(prefix: &str) -> PathBuf {
        let mut path = std::env::temp_dir();
        path.push(format!(
            "{}-{}-{}",
            prefix,
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("system time should be after Unix epoch")
                .as_nanos()
        ));
        path
    }
}
