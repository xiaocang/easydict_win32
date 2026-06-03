use crate::grammar_correction::{
    build_grammar_correction_user_prompt, grammar_correction_system_prompt,
    parse_grammar_correction,
};
use crate::llm_streaming::{parse_openai_sse_chunks, ChatMessage, ChatRole, OpenAiStreamingFormat};
use crate::translation_language::TranslationLanguage;
use crate::{
    compat_protocol::{
        local_ai_provider_modes, GrammarCorrectResultDto, SettingsSnapshot, TranslationResultDto,
    },
    grammar_correction::GrammarCorrectionResult,
};
use aes::cipher::{block_padding::Pkcs7, BlockDecryptMut, KeyIvInit};
use base64::{engine::general_purpose, Engine as _};
use regex::Regex;
use ring::digest;
use serde_json::{json, Map, Value};
use std::env;
use std::fmt::{self, Write as _};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::OnceLock;
use std::time::{Duration, Instant, SystemTime};

pub const OPENAI_TRANSLATION_SYSTEM_PROMPT: &str = "You are a translation expert proficient in various languages, focusing solely on translating text without interpretation. You accurately understand the meanings of proper nouns, idioms, metaphors, allusions, and other obscure words in sentences, translating them appropriately based on the context and language environment. The translation should be natural and fluent. Only return the translated text, without including redundant quotes or additional notes.";
pub const OPENAI_DEFAULT_ENDPOINT: &str = "https://api.openai.com/v1/responses";
pub const OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT: &str =
    "https://api.openai.com/v1/chat/completions";
pub const OPENAI_DEFAULT_MODEL: &str = "gpt-5.4-mini";
pub const OLLAMA_DEFAULT_ENDPOINT: &str = "http://localhost:11434/v1/chat/completions";
pub const OLLAMA_DEFAULT_MODEL: &str = "llama3.2";
pub const DEEPSEEK_DEFAULT_ENDPOINT: &str = "https://api.deepseek.com/v1/chat/completions";
pub const DEEPSEEK_DEFAULT_MODEL: &str = "deepseek-chat";
pub const GROQ_DEFAULT_ENDPOINT: &str = "https://api.groq.com/openai/v1/chat/completions";
pub const GROQ_DEFAULT_MODEL: &str = "llama-3.3-70b-versatile";
pub const ZHIPU_DEFAULT_ENDPOINT: &str = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
pub const ZHIPU_DEFAULT_MODEL: &str = "glm-4.5-flash";
pub const GITHUB_MODELS_DEFAULT_ENDPOINT: &str =
    "https://models.github.ai/inference/chat/completions";
pub const GITHUB_MODELS_DEFAULT_MODEL: &str = "gpt-4.1";
pub const CUSTOM_OPENAI_DEFAULT_MODEL: &str = "gpt-3.5-turbo";
pub const BUILT_IN_AI_DEFAULT_MODEL: &str = "glm-4-flash-250414";
pub const BUILT_IN_AI_ALLOWED_PROXY_MODELS: &[&str] = &[
    "glm-4-flash",
    "glm-4-flash-250414",
    "llama-3.3-70b-versatile",
    "llama-3.1-8b-instant",
];
pub const FOUNDRY_LOCAL_DEFAULT_MODEL: &str = "qwen2.5-0.5b";
pub const OPENAI_DEFAULT_TEMPERATURE: f64 = 0.3;
pub const FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE: &str = "EASYDICT_FOUNDRY_LOCAL_CLI";

const FOUNDRY_LOCAL_STATUS_READY: &str = "FoundryLocal_Status_Ready";
const FOUNDRY_LOCAL_STATUS_NOT_INSTALLED: &str = "FoundryLocal_Status_NotInstalled";
const FOUNDRY_LOCAL_STATUS_NOT_RUNNING: &str = "FoundryLocal_Status_NotRunning";
const FOUNDRY_LOCAL_STATUS_START_FAILED: &str = "FoundryLocal_Status_StartFailed";

const BUILT_IN_AI_ENCRYPTED_SECRETS_JSON: &str = include_str!(
    "../../../../dotnet/src/Easydict.TranslationService/Resources/EncryptedSecrets.json"
);
const BUILT_IN_AI_SECRET_ASSEMBLY_NAME: &str = "Easydict.TranslationService";
const BUILT_IN_AI_API_KEY_SECRET_NAME: &str = "builtInAIAPIKey";
const BUILT_IN_AI_ENDPOINT_SECRET_NAME: &str = "builtInAIEndpoint";

type Aes128CbcDec = cbc::Decryptor<aes::Aes128>;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OpenAiApiFormat {
    Auto,
    ChatCompletions,
    Responses,
}

impl OpenAiApiFormat {
    pub fn streaming_format(self) -> Option<OpenAiStreamingFormat> {
        match self {
            Self::Auto => None,
            Self::ChatCompletions => Some(OpenAiStreamingFormat::ChatCompletions),
            Self::Responses => Some(OpenAiStreamingFormat::Responses),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenAiTranslationRequest {
    pub text: String,
    pub from_language: TranslationLanguage,
    pub to_language: TranslationLanguage,
    pub custom_prompt: Option<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct OpenAiCompatibleConfig {
    pub endpoint: String,
    pub api_key: String,
    pub model: String,
    pub temperature: f64,
    pub format_override: OpenAiApiFormat,
    pub requires_api_key: bool,
    pub reasoning_effort: Option<String>,
    pub extra_headers: Vec<(String, String)>,
}

impl OpenAiCompatibleConfig {
    pub fn new(endpoint: impl Into<String>, model: impl Into<String>) -> Self {
        Self {
            endpoint: endpoint.into(),
            api_key: String::new(),
            model: model.into(),
            temperature: OPENAI_DEFAULT_TEMPERATURE,
            format_override: OpenAiApiFormat::Auto,
            requires_api_key: true,
            reasoning_effort: None,
            extra_headers: Vec::new(),
        }
    }

    pub fn with_api_key(mut self, api_key: impl Into<String>) -> Self {
        self.api_key = api_key.into();
        self
    }

    pub fn with_temperature(mut self, temperature: f64) -> Self {
        self.temperature = clamp_openai_temperature(temperature);
        self
    }

    pub fn with_format_override(mut self, format_override: OpenAiApiFormat) -> Self {
        self.format_override = format_override;
        self
    }

    pub fn without_required_api_key(mut self) -> Self {
        self.requires_api_key = false;
        self
    }

    pub fn with_reasoning_effort(mut self, reasoning_effort: Option<&str>) -> Self {
        self.reasoning_effort = reasoning_effort
            .filter(|value| !value.trim().is_empty())
            .map(str::to_string);
        self
    }

    pub fn with_extra_header(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.extra_headers.push((name.into(), value.into()));
        self
    }

    pub fn resolved_format(&self) -> OpenAiApiFormat {
        resolve_openai_api_format(&self.endpoint, self.format_override)
    }

    pub fn is_configured(&self) -> bool {
        !self.endpoint.is_empty() && (!self.requires_api_key || !self.api_key.is_empty())
    }
}

const OPENAI_COMPATIBLE_SUPPORTED_LANGUAGES: [TranslationLanguage; 32] = [
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::French,
    TranslationLanguage::Spanish,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Italian,
    TranslationLanguage::German,
    TranslationLanguage::Russian,
    TranslationLanguage::Arabic,
    TranslationLanguage::Dutch,
    TranslationLanguage::Polish,
    TranslationLanguage::Vietnamese,
    TranslationLanguage::Thai,
    TranslationLanguage::Indonesian,
    TranslationLanguage::Turkish,
    TranslationLanguage::Swedish,
    TranslationLanguage::Danish,
    TranslationLanguage::Norwegian,
    TranslationLanguage::Finnish,
    TranslationLanguage::Greek,
    TranslationLanguage::Czech,
    TranslationLanguage::Romanian,
    TranslationLanguage::Hungarian,
    TranslationLanguage::Ukrainian,
    TranslationLanguage::Hebrew,
    TranslationLanguage::Hindi,
    TranslationLanguage::Bengali,
    TranslationLanguage::Tamil,
    TranslationLanguage::Persian,
];

const OLLAMA_SUPPORTED_LANGUAGES: [TranslationLanguage; 18] = [
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::French,
    TranslationLanguage::Spanish,
    TranslationLanguage::German,
    TranslationLanguage::Russian,
    TranslationLanguage::Italian,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Dutch,
    TranslationLanguage::Polish,
    TranslationLanguage::Vietnamese,
    TranslationLanguage::Thai,
    TranslationLanguage::Arabic,
    TranslationLanguage::Turkish,
    TranslationLanguage::Indonesian,
];

const BUILT_IN_AI_SUPPORTED_LANGUAGES: [TranslationLanguage; 16] = [
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::French,
    TranslationLanguage::Spanish,
    TranslationLanguage::German,
    TranslationLanguage::Russian,
    TranslationLanguage::Italian,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Dutch,
    TranslationLanguage::Polish,
    TranslationLanguage::Vietnamese,
    TranslationLanguage::Arabic,
    TranslationLanguage::Turkish,
];

pub fn openai_compatible_supports_language(language: TranslationLanguage) -> bool {
    OPENAI_COMPATIBLE_SUPPORTED_LANGUAGES.contains(&language)
}

pub fn openai_compatible_supports_language_pair(
    from: TranslationLanguage,
    to: TranslationLanguage,
) -> bool {
    if from == TranslationLanguage::Auto {
        return openai_compatible_supports_language(to);
    }

    openai_compatible_supports_language(from) && openai_compatible_supports_language(to)
}

pub fn openai_compatible_supports_language_pair_for_service(
    service_id: &str,
    from: TranslationLanguage,
    to: TranslationLanguage,
) -> bool {
    let supported_languages = openai_compatible_supported_languages_for_service(service_id);
    if from == TranslationLanguage::Auto {
        return supported_languages.contains(&to);
    }

    supported_languages.contains(&from) && supported_languages.contains(&to)
}

fn openai_compatible_supported_languages_for_service(
    service_id: &str,
) -> &'static [TranslationLanguage] {
    match service_id {
        "ollama" => &OLLAMA_SUPPORTED_LANGUAGES,
        "builtin" => &BUILT_IN_AI_SUPPORTED_LANGUAGES,
        _ => &OPENAI_COMPATIBLE_SUPPORTED_LANGUAGES,
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct OpenAiHttpRequestPlan {
    pub method: &'static str,
    pub endpoint: String,
    pub content_type: &'static str,
    pub headers: Vec<(String, String)>,
    pub body: Value,
    pub api_format: OpenAiApiFormat,
    pub streaming_format: OpenAiStreamingFormat,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenAiHttpGetRequestPlan {
    pub method: &'static str,
    pub endpoint: String,
    pub headers: Vec<(String, String)>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenAiHttpTextResponse {
    pub status_code: u16,
    pub reason_phrase: String,
    pub body: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BuiltInAiDeviceRegistrationRequestPlan {
    pub method: &'static str,
    pub endpoint: String,
    pub headers: Vec<(String, String)>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BuiltInAiDeviceRegistrationHttpResponse {
    pub status_code: u16,
    pub reason_phrase: String,
    pub body: String,
}

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

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum OpenAiPlanError {
    EndpointNotConfigured,
    ApiKeyRequired,
    AutoFormatUnresolved,
    UnsupportedLanguagePair {
        from: TranslationLanguage,
        to: TranslationLanguage,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OpenAiExecutionErrorCode {
    InvalidApiKey,
    RateLimited,
    TextTooLong,
    UnsupportedLanguage,
    InvalidResponse,
    ServiceUnavailable,
    Timeout,
    NetworkError,
    Unknown,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenAiExecutionError {
    pub code: OpenAiExecutionErrorCode,
    pub message: String,
    pub service_id: Option<String>,
}

impl OpenAiExecutionError {
    pub fn new(code: OpenAiExecutionErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
            service_id: None,
        }
    }

    pub fn with_service_id(mut self, service_id: impl Into<String>) -> Self {
        self.service_id = Some(service_id.into());
        self
    }
}

impl fmt::Display for OpenAiExecutionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl From<OpenAiPlanError> for OpenAiExecutionError {
    fn from(error: OpenAiPlanError) -> Self {
        match error {
            OpenAiPlanError::EndpointNotConfigured => Self::new(
                OpenAiExecutionErrorCode::ServiceUnavailable,
                "Endpoint is not configured",
            ),
            OpenAiPlanError::ApiKeyRequired => Self::new(
                OpenAiExecutionErrorCode::InvalidApiKey,
                "API key is required but not configured",
            ),
            OpenAiPlanError::AutoFormatUnresolved => Self::new(
                OpenAiExecutionErrorCode::InvalidResponse,
                "OpenAI API format must be resolved before execution",
            ),
            OpenAiPlanError::UnsupportedLanguagePair { from, to } => Self::new(
                OpenAiExecutionErrorCode::UnsupportedLanguage,
                format!("Language pair not supported: {from:?} -> {to:?}"),
            ),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BuiltInAiSecretError {
    InvalidBase64,
    DecryptFailed,
    InvalidUtf8,
}

impl fmt::Display for BuiltInAiSecretError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidBase64 => formatter.write_str("Built-in AI secret is not valid base64"),
            Self::DecryptFailed => formatter.write_str("Built-in AI secret could not be decrypted"),
            Self::InvalidUtf8 => formatter.write_str("Built-in AI secret is not valid UTF-8"),
        }
    }
}

pub trait OpenAiHttpClient {
    fn post_sse(&mut self, request: &OpenAiHttpRequestPlan)
        -> Result<String, OpenAiExecutionError>;

    fn get_text(
        &mut self,
        _request: &OpenAiHttpGetRequestPlan,
    ) -> Result<Option<OpenAiHttpTextResponse>, OpenAiExecutionError> {
        Ok(None)
    }
}

pub trait BuiltInAiDeviceRegistrationHttpClient {
    fn post_device_registration(
        &mut self,
        request: &BuiltInAiDeviceRegistrationRequestPlan,
    ) -> Result<BuiltInAiDeviceRegistrationHttpResponse, OpenAiExecutionError>;
}

pub trait FoundryLocalEndpointResolver {
    fn resolve_chat_completions_endpoint(&mut self)
        -> Result<Option<String>, OpenAiExecutionError>;
}

pub trait FoundryLocalRuntimeController: FoundryLocalEndpointResolver {
    fn get_status(&mut self) -> Result<FoundryLocalRuntimeStatus, OpenAiExecutionError>;

    fn start_service(&mut self) -> Result<(), OpenAiExecutionError>;

    fn load_model(&mut self, model: &str) -> Result<(), OpenAiExecutionError>;
}

#[derive(Clone, Debug)]
pub struct CommandFoundryLocalEndpointResolver {
    executable_name: String,
    status_command_timeout: Duration,
    start_command_timeout: Duration,
    model_load_command_timeout: Duration,
}

impl Default for CommandFoundryLocalEndpointResolver {
    fn default() -> Self {
        let executable_name = env::var(FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE)
            .ok()
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| "foundry".to_string());

        Self {
            executable_name,
            status_command_timeout: Duration::from_secs(8),
            start_command_timeout: Duration::from_secs(15),
            model_load_command_timeout: Duration::from_secs(180),
        }
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
        Self {
            executable_name: executable_name.into(),
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
    ) -> Result<String, OpenAiExecutionError> {
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
                OpenAiExecutionError::new(OpenAiExecutionErrorCode::ServiceUnavailable, message)
            })?;

        let deadline = Instant::now() + command_timeout;
        loop {
            match child.try_wait() {
                Ok(Some(_)) => break,
                Ok(None) if Instant::now() >= deadline => {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(OpenAiExecutionError::new(
                        OpenAiExecutionErrorCode::Timeout,
                        "Foundry Local CLI command timed out",
                    ));
                }
                Ok(None) => std::thread::sleep(Duration::from_millis(50)),
                Err(error) => {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(OpenAiExecutionError::new(
                        OpenAiExecutionErrorCode::NetworkError,
                        format!("Could not wait for Foundry Local CLI: {error}"),
                    ));
                }
            }
        }

        let output = child.wait_with_output().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
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
            return Err(OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::ServiceUnavailable,
                message,
            ));
        }

        Ok(text)
    }

    fn run_status_command(&self, arguments: &[&str]) -> Result<String, OpenAiExecutionError> {
        self.run_foundry_command(arguments, self.status_command_timeout, false)
    }

    fn run_foundry_service_start_and_wait(&mut self) -> Result<(), OpenAiExecutionError> {
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
                OpenAiExecutionError::new(OpenAiExecutionErrorCode::ServiceUnavailable, message)
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
                return Err(OpenAiExecutionError::new(
                    OpenAiExecutionErrorCode::Timeout,
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
                    return Err(OpenAiExecutionError::new(
                        OpenAiExecutionErrorCode::ServiceUnavailable,
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
}

impl FoundryLocalEndpointResolver for CommandFoundryLocalEndpointResolver {
    fn resolve_chat_completions_endpoint(
        &mut self,
    ) -> Result<Option<String>, OpenAiExecutionError> {
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
    fn get_status(&mut self) -> Result<FoundryLocalRuntimeStatus, OpenAiExecutionError> {
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

    fn start_service(&mut self) -> Result<(), OpenAiExecutionError> {
        self.run_foundry_service_start_and_wait()
    }

    fn load_model(&mut self, model: &str) -> Result<(), OpenAiExecutionError> {
        let model = model.trim();
        if model.is_empty() {
            return Err(OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::InvalidResponse,
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

pub struct ReqwestOpenAiHttpClient {
    client: reqwest::blocking::Client,
}

impl ReqwestOpenAiHttpClient {
    pub fn from_settings(settings: &SettingsSnapshot) -> Result<Self, OpenAiExecutionError> {
        let mut builder = reqwest::blocking::Client::builder().timeout(Duration::from_secs(120));

        if settings.proxy_enabled.unwrap_or(false) {
            if let Some(proxy_uri) = normalized_optional(settings.proxy_uri.as_deref()) {
                let proxy = if settings.proxy_bypass_local.unwrap_or(false) {
                    let proxy_url = reqwest::Url::parse(&proxy_uri).map_err(|error| {
                        OpenAiExecutionError::new(
                            OpenAiExecutionErrorCode::InvalidResponse,
                            format!("Invalid OpenAI proxy URI '{proxy_uri}': {error}"),
                        )
                    })?;
                    reqwest::Proxy::custom(move |url| {
                        (!is_loopback_url(url)).then(|| proxy_url.clone())
                    })
                } else {
                    reqwest::Proxy::all(&proxy_uri).map_err(|error| {
                        OpenAiExecutionError::new(
                            OpenAiExecutionErrorCode::InvalidResponse,
                            format!("Invalid OpenAI proxy URI '{proxy_uri}': {error}"),
                        )
                    })?
                };
                builder = builder.proxy(proxy);
            }
        }

        let client = builder.build().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not create OpenAI HTTP client: {error}"),
            )
        })?;
        Ok(Self { client })
    }
}

impl OpenAiHttpClient for ReqwestOpenAiHttpClient {
    fn post_sse(
        &mut self,
        request: &OpenAiHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        let mut builder = self.client.post(&request.endpoint).json(&request.body);
        for (name, value) in &request.headers {
            builder = builder.header(name, value);
        }

        let response = builder.send().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("OpenAI HTTP request failed: {error}"),
            )
        })?;
        let status = response.status();
        let body = response.text().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not read OpenAI HTTP response: {error}"),
            )
        })?;

        if !status.is_success() {
            return Err(openai_error_from_response(
                status.as_u16(),
                status.canonical_reason().unwrap_or("Unknown"),
                &body,
            ));
        }

        Ok(body)
    }

    fn get_text(
        &mut self,
        request: &OpenAiHttpGetRequestPlan,
    ) -> Result<Option<OpenAiHttpTextResponse>, OpenAiExecutionError> {
        let mut builder = self.client.get(&request.endpoint);
        for (name, value) in &request.headers {
            builder = builder.header(name, value);
        }

        let response = builder.send().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("OpenAI HTTP GET request failed: {error}"),
            )
        })?;
        let status = response.status();
        let reason_phrase = status.canonical_reason().unwrap_or("Unknown").to_string();
        let body = response.text().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not read OpenAI HTTP GET response: {error}"),
            )
        })?;

        Ok(Some(OpenAiHttpTextResponse {
            status_code: status.as_u16(),
            reason_phrase,
            body,
        }))
    }
}

impl BuiltInAiDeviceRegistrationHttpClient for ReqwestOpenAiHttpClient {
    fn post_device_registration(
        &mut self,
        request: &BuiltInAiDeviceRegistrationRequestPlan,
    ) -> Result<BuiltInAiDeviceRegistrationHttpResponse, OpenAiExecutionError> {
        let mut builder = self.client.post(&request.endpoint);
        for (name, value) in &request.headers {
            builder = builder.header(name, value);
        }

        let response = builder.send().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Built-in AI device registration failed: {error}"),
            )
        })?;
        let status = response.status();
        let reason_phrase = status.canonical_reason().unwrap_or("Unknown").to_string();
        let body = response.text().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not read Built-in AI registration response: {error}"),
            )
        })?;

        Ok(BuiltInAiDeviceRegistrationHttpResponse {
            status_code: status.as_u16(),
            reason_phrase,
            body,
        })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OllamaModelRefreshOutcome {
    pub available_models: Vec<String>,
    pub selected_model: String,
}

pub fn openai_service_config(
    api_key: impl Into<String>,
    endpoint: Option<&str>,
    model: Option<&str>,
    temperature: Option<f64>,
    format_override: OpenAiApiFormat,
) -> OpenAiCompatibleConfig {
    let model = model.unwrap_or(OPENAI_DEFAULT_MODEL);
    OpenAiCompatibleConfig::new(endpoint.unwrap_or(OPENAI_DEFAULT_ENDPOINT), model)
        .with_api_key(api_key)
        .with_temperature(openai_effective_temperature(
            model,
            temperature
                .map(clamp_openai_temperature)
                .unwrap_or(OPENAI_DEFAULT_TEMPERATURE),
        ))
        .with_format_override(format_override)
        .with_reasoning_effort(openai_responses_reasoning_effort(model))
}

pub fn ollama_service_config(
    endpoint: Option<&str>,
    model: Option<&str>,
) -> OpenAiCompatibleConfig {
    OpenAiCompatibleConfig::new(
        endpoint.unwrap_or(OLLAMA_DEFAULT_ENDPOINT),
        model.unwrap_or(OLLAMA_DEFAULT_MODEL),
    )
    .without_required_api_key()
}

pub fn custom_openai_service_config(
    endpoint: impl Into<String>,
    api_key: Option<&str>,
    model: Option<&str>,
    temperature: Option<f64>,
) -> OpenAiCompatibleConfig {
    OpenAiCompatibleConfig::new(endpoint, model.unwrap_or(CUSTOM_OPENAI_DEFAULT_MODEL))
        .with_api_key(api_key.unwrap_or_default())
        .with_temperature(temperature.unwrap_or(OPENAI_DEFAULT_TEMPERATURE))
        .without_required_api_key()
}

pub fn foundry_local_service_config(
    endpoint: impl AsRef<str>,
    model: Option<&str>,
) -> OpenAiCompatibleConfig {
    let model =
        normalized_optional(model).unwrap_or_else(|| FOUNDRY_LOCAL_DEFAULT_MODEL.to_string());
    OpenAiCompatibleConfig::new(
        normalize_foundry_local_chat_completions_endpoint(endpoint.as_ref()),
        model,
    )
    .without_required_api_key()
    .with_format_override(OpenAiApiFormat::ChatCompletions)
}

pub fn foundry_local_models_endpoint_from_chat_completions_endpoint(
    endpoint: &str,
) -> Option<String> {
    let mut url = reqwest::Url::parse(endpoint.trim()).ok()?;
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

pub fn build_foundry_local_models_request_plan(
    config: &OpenAiCompatibleConfig,
) -> Option<OpenAiHttpGetRequestPlan> {
    Some(OpenAiHttpGetRequestPlan {
        method: "GET",
        endpoint: foundry_local_models_endpoint_from_chat_completions_endpoint(&config.endpoint)?,
        headers: Vec::new(),
    })
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

pub fn resolve_foundry_local_model_id_for_config<C: OpenAiHttpClient>(
    client: &mut C,
    config: &OpenAiCompatibleConfig,
) -> OpenAiCompatibleConfig {
    let Some(request) = build_foundry_local_models_request_plan(config) else {
        return config.clone();
    };

    let Ok(Some(response)) = client.get_text(&request) else {
        return config.clone();
    };

    if !(200..=299).contains(&response.status_code) {
        return config.clone();
    }

    let Some(model) = try_resolve_foundry_local_model_id(&response.body, &config.model) else {
        return config.clone();
    };

    let mut resolved = config.clone();
    resolved.model = model;
    resolved
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
    settings: &SettingsSnapshot,
) -> Result<FoundryLocalStatusCheck, OpenAiExecutionError> {
    let configured_endpoint = normalized_optional(settings.foundry_local_endpoint.as_deref())
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
    settings: &SettingsSnapshot,
) -> Result<FoundryLocalPrepareOutcome, OpenAiExecutionError> {
    let model = normalized_optional(settings.foundry_local_model.as_deref())
        .unwrap_or_else(|| FOUNDRY_LOCAL_DEFAULT_MODEL.to_string());
    let configured_endpoint = normalized_optional(settings.foundry_local_endpoint.as_deref());

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

pub fn deepseek_service_config(
    api_key: impl Into<String>,
    model: Option<&str>,
) -> OpenAiCompatibleConfig {
    OpenAiCompatibleConfig::new(
        DEEPSEEK_DEFAULT_ENDPOINT,
        model.unwrap_or(DEEPSEEK_DEFAULT_MODEL),
    )
    .with_api_key(api_key)
}

pub fn groq_service_config(
    api_key: impl Into<String>,
    model: Option<&str>,
) -> OpenAiCompatibleConfig {
    OpenAiCompatibleConfig::new(GROQ_DEFAULT_ENDPOINT, model.unwrap_or(GROQ_DEFAULT_MODEL))
        .with_api_key(api_key)
}

pub fn zhipu_service_config(
    api_key: impl Into<String>,
    model: Option<&str>,
) -> OpenAiCompatibleConfig {
    OpenAiCompatibleConfig::new(ZHIPU_DEFAULT_ENDPOINT, model.unwrap_or(ZHIPU_DEFAULT_MODEL))
        .with_api_key(api_key)
}

pub fn github_models_service_config(
    api_key: impl Into<String>,
    model: Option<&str>,
) -> OpenAiCompatibleConfig {
    OpenAiCompatibleConfig::new(
        GITHUB_MODELS_DEFAULT_ENDPOINT,
        model.unwrap_or(GITHUB_MODELS_DEFAULT_MODEL),
    )
    .with_api_key(api_key)
}

pub fn built_in_ai_direct_service_config(
    api_key: impl Into<String>,
    model: Option<&str>,
) -> OpenAiCompatibleConfig {
    let model = model.unwrap_or(BUILT_IN_AI_DEFAULT_MODEL);
    OpenAiCompatibleConfig::new(built_in_ai_direct_endpoint_for_model(model), model)
        .with_api_key(api_key)
}

pub fn built_in_ai_proxy_service_config(
    api_key: impl Into<String>,
    endpoint: impl Into<String>,
    model: Option<&str>,
    device_id: Option<&str>,
    device_token: Option<&str>,
) -> Option<OpenAiCompatibleConfig> {
    let api_key = api_key.into();
    let endpoint = endpoint.into();
    let api_key = api_key.trim();
    let endpoint = endpoint.trim();
    if api_key.is_empty() || endpoint.is_empty() {
        return None;
    }

    let model = built_in_ai_proxy_model_or_default(model);
    let mut config = OpenAiCompatibleConfig::new(endpoint, model).with_api_key(api_key);
    for (name, value) in built_in_ai_proxy_headers(
        device_id.unwrap_or_default().trim(),
        device_token.unwrap_or_default().trim(),
    ) {
        config = config.with_extra_header(name, value);
    }

    Some(config)
}

pub fn built_in_ai_embedded_proxy_service_config(
    model: Option<&str>,
    device_id: Option<&str>,
    device_token: Option<&str>,
) -> Option<OpenAiCompatibleConfig> {
    let api_key = built_in_ai_embedded_secret(BUILT_IN_AI_API_KEY_SECRET_NAME)?;
    let endpoint = built_in_ai_embedded_secret(BUILT_IN_AI_ENDPOINT_SECRET_NAME)?;
    built_in_ai_proxy_service_config(api_key, endpoint, model, device_id, device_token)
}

pub fn built_in_ai_embedded_device_registration_request_plan(
    device_id: &str,
) -> Option<BuiltInAiDeviceRegistrationRequestPlan> {
    let api_key = built_in_ai_embedded_secret(BUILT_IN_AI_API_KEY_SECRET_NAME)?;
    let endpoint = built_in_ai_embedded_secret(BUILT_IN_AI_ENDPOINT_SECRET_NAME)?;
    build_built_in_ai_device_registration_request_plan(api_key, endpoint, device_id)
}

pub fn build_built_in_ai_device_registration_request_plan(
    api_key: impl Into<String>,
    proxy_endpoint: impl AsRef<str>,
    device_id: &str,
) -> Option<BuiltInAiDeviceRegistrationRequestPlan> {
    let device_id = device_id.trim();
    if device_id.is_empty() {
        return None;
    }

    let endpoint = built_in_ai_device_registration_endpoint(proxy_endpoint.as_ref())?;
    let api_key = api_key.into();
    let api_key = api_key.trim();
    let mut headers = vec![("X-Device-Id".to_string(), device_id.to_string())];
    if !api_key.is_empty() {
        headers.push(("Authorization".to_string(), format!("Bearer {api_key}")));
    }

    Some(BuiltInAiDeviceRegistrationRequestPlan {
        method: "POST",
        endpoint,
        headers,
    })
}

pub fn built_in_ai_device_registration_endpoint(proxy_endpoint: &str) -> Option<String> {
    let url = reqwest::Url::parse(proxy_endpoint.trim()).ok()?;
    let origin = url.origin().ascii_serialization();
    (origin != "null").then(|| format!("{origin}/v1/device/register"))
}

pub fn parse_built_in_ai_device_registration_response(json_text: &str) -> Option<String> {
    let root: Value = serde_json::from_str(json_text).ok()?;
    normalized_optional(root.get("device_token").and_then(Value::as_str))
}

pub fn register_built_in_ai_device<C: BuiltInAiDeviceRegistrationHttpClient>(
    client: &mut C,
    request: &BuiltInAiDeviceRegistrationRequestPlan,
) -> Result<Option<String>, OpenAiExecutionError> {
    let response = client.post_device_registration(request)?;
    if !(200..=299).contains(&response.status_code) {
        return Ok(None);
    }

    Ok(parse_built_in_ai_device_registration_response(
        &response.body,
    ))
}

pub fn built_in_ai_embedded_secret(secret_name: &str) -> Option<String> {
    let root: Map<String, Value> = serde_json::from_str(BUILT_IN_AI_ENCRYPTED_SECRETS_JSON).ok()?;
    let encrypted = root.get(secret_name).and_then(Value::as_str)?;
    decrypt_built_in_ai_secret(encrypted).ok()
}

pub fn decrypt_built_in_ai_secret(base64_encrypted: &str) -> Result<String, BuiltInAiSecretError> {
    let encrypted_bytes = general_purpose::STANDARD
        .decode(base64_encrypted.trim().as_bytes())
        .map_err(|_| BuiltInAiSecretError::InvalidBase64)?;
    let key = built_in_ai_secret_key();
    let decrypted = Aes128CbcDec::new(&key.into(), &key.into())
        .decrypt_padded_vec_mut::<Pkcs7>(&encrypted_bytes)
        .map_err(|_| BuiltInAiSecretError::DecryptFailed)?;

    String::from_utf8(decrypted).map_err(|_| BuiltInAiSecretError::InvalidUtf8)
}

pub fn openai_api_format_from_setting(value: Option<&str>) -> OpenAiApiFormat {
    match value
        .unwrap_or_default()
        .trim()
        .to_ascii_lowercase()
        .as_str()
    {
        "responses" => OpenAiApiFormat::Responses,
        "chatcompletions" | "chat-completions" | "chat_completions" => {
            OpenAiApiFormat::ChatCompletions
        }
        _ => OpenAiApiFormat::Auto,
    }
}

pub fn openai_compatible_config_for_service(
    service_id: &str,
    settings: &SettingsSnapshot,
) -> Option<OpenAiCompatibleConfig> {
    match service_id {
        "openai" => Some(openai_service_config(
            settings.open_ai_api_key.clone().unwrap_or_default(),
            settings.open_ai_endpoint.as_deref(),
            settings.open_ai_model.as_deref(),
            settings.open_ai_temperature.map(f64::from),
            openai_api_format_from_setting(settings.open_ai_api_format_override.as_deref()),
        )),
        "ollama" => Some(ollama_service_config(
            settings.ollama_endpoint.as_deref(),
            settings.ollama_model.as_deref(),
        )),
        "builtin" => {
            if let Some(api_key) = normalized_optional(settings.built_in_ai_api_key.as_deref()) {
                return Some(built_in_ai_direct_service_config(
                    api_key,
                    settings.built_in_ai_model.as_deref(),
                ));
            }

            built_in_ai_embedded_proxy_service_config(
                settings.built_in_ai_model.as_deref(),
                settings.device_id.as_deref(),
                settings.device_token.as_deref(),
            )
        }
        "custom-openai" => Some(custom_openai_service_config(
            settings.custom_open_ai_endpoint.clone().unwrap_or_default(),
            settings.custom_open_ai_api_key.as_deref(),
            settings.custom_open_ai_model.as_deref(),
            None,
        )),
        "windows-local-ai" => {
            let endpoint = normalized_optional(settings.foundry_local_endpoint.as_deref())?;
            if !can_use_configured_foundry_local_endpoint(
                settings.local_ai_provider.as_deref(),
                Some(endpoint.as_str()),
            ) {
                return None;
            }

            Some(foundry_local_service_config(
                endpoint,
                settings.foundry_local_model.as_deref(),
            ))
        }
        "deepseek" => Some(deepseek_service_config(
            settings.deep_seek_api_key.clone().unwrap_or_default(),
            settings.deep_seek_model.as_deref(),
        )),
        "groq" => Some(groq_service_config(
            settings.groq_api_key.clone().unwrap_or_default(),
            settings.groq_model.as_deref(),
        )),
        "zhipu" => Some(zhipu_service_config(
            settings.zhipu_api_key.clone().unwrap_or_default(),
            settings.zhipu_model.as_deref(),
        )),
        "github" => Some(github_models_service_config(
            settings.github_models_api_key.clone().unwrap_or_default(),
            settings.github_models_model.as_deref(),
        )),
        _ => None,
    }
}

pub fn resolve_openai_compatible_config_for_service<R: FoundryLocalEndpointResolver>(
    service_id: &str,
    settings: &SettingsSnapshot,
    foundry_local_endpoint_resolver: &mut R,
) -> Result<Option<OpenAiCompatibleConfig>, OpenAiExecutionError> {
    if let Some(config) = openai_compatible_config_for_service(service_id, settings) {
        return Ok(Some(config));
    }

    if service_id != "windows-local-ai"
        || !is_foundry_local_provider(settings.local_ai_provider.as_deref())
    {
        return Ok(None);
    }

    let endpoint = foundry_local_endpoint_resolver
        .resolve_chat_completions_endpoint()?
        .ok_or_else(|| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::ServiceUnavailable,
                "Foundry Local service is not running or did not report a local endpoint.",
            )
        })?;

    Ok(Some(foundry_local_service_config(
        endpoint,
        settings.foundry_local_model.as_deref(),
    )))
}

pub fn openai_compatible_service_can_route_natively(
    service_id: &str,
    settings: &SettingsSnapshot,
) -> bool {
    openai_compatible_config_for_service(service_id, settings).is_some()
        || (service_id == "windows-local-ai"
            && is_foundry_local_provider(settings.local_ai_provider.as_deref()))
}

pub fn normalize_foundry_local_chat_completions_endpoint(endpoint: &str) -> String {
    let normalized = endpoint.trim().trim_end_matches('/');
    if normalized.is_empty() {
        return String::new();
    }

    if let Ok(mut url) = reqwest::Url::parse(normalized) {
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

pub fn build_openai_http_request_plan(
    config: &OpenAiCompatibleConfig,
    messages: &[ChatMessage],
) -> Result<OpenAiHttpRequestPlan, OpenAiPlanError> {
    validate_openai_config(config)?;
    let api_format = config.resolved_format();
    let Some(streaming_format) = api_format.streaming_format() else {
        return Err(OpenAiPlanError::AutoFormatUnresolved);
    };

    let mut headers = Vec::new();
    if !config.api_key.is_empty() {
        headers.push((
            "Authorization".to_string(),
            format!("Bearer {}", config.api_key),
        ));
    }
    headers.extend(config.extra_headers.iter().cloned());

    Ok(OpenAiHttpRequestPlan {
        method: "POST",
        endpoint: config.endpoint.clone(),
        content_type: "application/json",
        headers,
        body: build_openai_request_body(
            api_format,
            messages,
            &config.model,
            config.temperature,
            config.reasoning_effort.as_deref(),
        ),
        api_format,
        streaming_format,
    })
}

pub fn build_openai_translation_request_plan(
    config: &OpenAiCompatibleConfig,
    request: &OpenAiTranslationRequest,
) -> Result<OpenAiHttpRequestPlan, OpenAiPlanError> {
    validate_openai_translation_request(request)?;
    let messages = build_openai_translation_messages(request);
    build_openai_http_request_plan(config, &messages)
}

pub fn validate_openai_translation_request(
    request: &OpenAiTranslationRequest,
) -> Result<(), OpenAiPlanError> {
    if !openai_compatible_supports_language_pair(request.from_language, request.to_language) {
        return Err(OpenAiPlanError::UnsupportedLanguagePair {
            from: request.from_language,
            to: request.to_language,
        });
    }

    Ok(())
}

pub fn validate_openai_translation_request_for_service(
    service_id: &str,
    request: &OpenAiTranslationRequest,
) -> Result<(), OpenAiPlanError> {
    if !openai_compatible_supports_language_pair_for_service(
        service_id,
        request.from_language,
        request.to_language,
    ) {
        return Err(OpenAiPlanError::UnsupportedLanguagePair {
            from: request.from_language,
            to: request.to_language,
        });
    }

    Ok(())
}

pub fn build_openai_grammar_request_plan(
    config: &OpenAiCompatibleConfig,
    language: TranslationLanguage,
    text: &str,
    include_explanations: bool,
) -> Result<OpenAiHttpRequestPlan, OpenAiPlanError> {
    let messages = build_openai_grammar_messages(language, text, include_explanations);
    build_openai_http_request_plan(config, &messages)
}

pub fn execute_openai_stream_request<C: OpenAiHttpClient>(
    client: &mut C,
    plan: &OpenAiHttpRequestPlan,
) -> Result<Vec<String>, OpenAiExecutionError> {
    let sse = client.post_sse(plan)?;
    Ok(parse_openai_sse_chunks(plan.streaming_format, &sse))
}

pub fn translate_openai_compatible<C: OpenAiHttpClient>(
    client: &mut C,
    config: &OpenAiCompatibleConfig,
    request: &OpenAiTranslationRequest,
    service_id: impl Into<String>,
    service_name: impl Into<String>,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let service_id = service_id.into();
    let service_name = service_name.into();
    validate_openai_translation_request_for_service(&service_id, request)
        .map_err(|error| OpenAiExecutionError::from(error).with_service_id(service_id.clone()))?;
    let plan = build_openai_translation_request_plan(config, request)
        .map_err(|error| OpenAiExecutionError::from(error).with_service_id(service_id.clone()))?;
    let chunks = execute_openai_stream_request(client, &plan)
        .map_err(|error| attach_service_id(error, &service_id))?;
    let translated_text = cleanup_openai_translation_text(&chunks.concat());

    Ok(TranslationResultDto {
        translated_text,
        service_id: Some(service_id),
        service_name: Some(service_name),
        detected_language: Some(request.from_language.to_code().to_string()),
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: None,
        word_result: None,
    })
}

pub fn correct_grammar_openai_compatible<C: OpenAiHttpClient>(
    client: &mut C,
    config: &OpenAiCompatibleConfig,
    language: TranslationLanguage,
    text: &str,
    include_explanations: bool,
    service_id: impl Into<String>,
    service_name: impl Into<String>,
) -> Result<GrammarCorrectResultDto, OpenAiExecutionError> {
    let service_id = service_id.into();
    let service_name = service_name.into();
    let plan = build_openai_grammar_request_plan(config, language, text, include_explanations)
        .map_err(|error| OpenAiExecutionError::from(error).with_service_id(service_id.clone()))?;
    let chunks = execute_openai_stream_request(client, &plan)
        .map_err(|error| attach_service_id(error, &service_id))?;
    let raw_text = chunks.concat();
    let parsed = parse_grammar_correction(&raw_text, text, &service_name, 0);
    Ok(grammar_result_dto(
        parsed,
        raw_text,
        service_id,
        service_name,
        language,
    ))
}

pub fn validate_openai_config(config: &OpenAiCompatibleConfig) -> Result<(), OpenAiPlanError> {
    if config.endpoint.is_empty() {
        return Err(OpenAiPlanError::EndpointNotConfigured);
    }

    if config.requires_api_key && config.api_key.is_empty() {
        return Err(OpenAiPlanError::ApiKeyRequired);
    }

    Ok(())
}

pub fn clamp_openai_temperature(temperature: f64) -> f64 {
    temperature.clamp(0.0, 2.0)
}

pub fn openai_effective_temperature(model: &str, temperature: f64) -> f64 {
    if is_legacy_gpt5_reasoning_model(model) {
        1.0
    } else {
        temperature
    }
}

pub fn openai_responses_reasoning_effort(model: &str) -> Option<&'static str> {
    if supports_none_reasoning_effort(model) {
        return Some("none");
    }

    is_legacy_gpt5_reasoning_model(model).then_some("minimal")
}

pub fn cleanup_openai_translation_text(text: &str) -> String {
    let mut result = text.trim();
    if result.len() >= 2 && result.starts_with('"') && result.ends_with('"') {
        result = result[1..result.len() - 1].trim();
    }

    result.to_string()
}

pub fn built_in_ai_proxy_headers(device_id: &str, device_token: &str) -> Vec<(String, String)> {
    if device_id.is_empty() {
        return Vec::new();
    }

    let mut headers = vec![("X-Device-Id".to_string(), device_id.to_string())];
    if !device_token.is_empty() {
        headers.push(("X-Device-Token".to_string(), device_token.to_string()));
    }

    headers
}

pub fn built_in_ai_proxy_model_or_default(model: Option<&str>) -> &'static str {
    let Some(model) = model.map(str::trim).filter(|model| !model.is_empty()) else {
        return BUILT_IN_AI_DEFAULT_MODEL;
    };

    BUILT_IN_AI_ALLOWED_PROXY_MODELS
        .iter()
        .copied()
        .find(|candidate| *candidate == model)
        .unwrap_or(BUILT_IN_AI_DEFAULT_MODEL)
}

pub fn built_in_ai_direct_endpoint_for_model(model: &str) -> &'static str {
    match model {
        "llama-3.3-70b-versatile" | "llama-3.1-8b-instant" => GROQ_DEFAULT_ENDPOINT,
        _ => ZHIPU_DEFAULT_ENDPOINT,
    }
}

pub fn ollama_tags_url_from_endpoint(endpoint: &str) -> Option<String> {
    let endpoint = endpoint.trim();
    let scheme_end = endpoint.find("://")?;
    let scheme = &endpoint[..scheme_end];
    if scheme.is_empty() {
        return None;
    }

    let after_scheme = &endpoint[(scheme_end + 3)..];
    let authority_end = after_scheme
        .find(['/', '?', '#'])
        .unwrap_or(after_scheme.len());
    let authority = &after_scheme[..authority_end];
    if authority.is_empty() {
        return None;
    }

    let authority = authority
        .rsplit_once('@')
        .map_or(authority, |(_, host)| host);
    let (host, port) = split_host_port(authority, scheme)?;
    Some(format!("{scheme}://{host}:{port}/api/tags"))
}

pub fn parse_ollama_model_names(json_text: &str) -> Result<Vec<String>, serde_json::Error> {
    let root: Value = serde_json::from_str(json_text)?;
    let models = root
        .get("models")
        .and_then(Value::as_array)
        .map(|models| {
            models
                .iter()
                .filter_map(|model| model.get("name").and_then(Value::as_str))
                .filter(|name| !name.is_empty())
                .map(str::to_string)
                .collect()
        })
        .unwrap_or_default();
    Ok(models)
}

pub fn resolve_ollama_model_refresh(
    current_model: &str,
    available_models: Vec<String>,
) -> OllamaModelRefreshOutcome {
    let selected_model = if !available_models.is_empty()
        && !available_models.iter().any(|model| model == current_model)
    {
        available_models[0].clone()
    } else {
        current_model.to_string()
    };

    OllamaModelRefreshOutcome {
        available_models,
        selected_model,
    }
}

pub fn ollama_model_refresh_fallback() -> OllamaModelRefreshOutcome {
    OllamaModelRefreshOutcome {
        available_models: vec![OLLAMA_DEFAULT_MODEL.to_string()],
        selected_model: OLLAMA_DEFAULT_MODEL.to_string(),
    }
}

pub fn openai_error_from_response(
    status_code: u16,
    reason_phrase: &str,
    error_body: &str,
) -> OpenAiExecutionError {
    let code = match status_code {
        401 | 403 => OpenAiExecutionErrorCode::InvalidApiKey,
        429 => OpenAiExecutionErrorCode::RateLimited,
        400 => OpenAiExecutionErrorCode::InvalidResponse,
        500 | 503 => OpenAiExecutionErrorCode::ServiceUnavailable,
        504 => OpenAiExecutionErrorCode::Timeout,
        _ => OpenAiExecutionErrorCode::Unknown,
    };
    let message = extract_openai_error_message(error_body)
        .unwrap_or_else(|| format!("API error ({status_code}): {reason_phrase}"));

    OpenAiExecutionError::new(code, message)
}

pub fn detect_openai_api_format_from_url(endpoint: &str) -> OpenAiApiFormat {
    let Some(path) = absolute_url_path(endpoint) else {
        return OpenAiApiFormat::ChatCompletions;
    };

    let trimmed_path = path.trim_end_matches('/');
    if trimmed_path.to_ascii_lowercase().ends_with("/responses") {
        OpenAiApiFormat::Responses
    } else {
        OpenAiApiFormat::ChatCompletions
    }
}

pub fn resolve_openai_api_format(
    endpoint: &str,
    format_override: OpenAiApiFormat,
) -> OpenAiApiFormat {
    match format_override {
        OpenAiApiFormat::Auto => detect_openai_api_format_from_url(endpoint),
        format => format,
    }
}

pub fn build_openai_request_body(
    format: OpenAiApiFormat,
    messages: &[ChatMessage],
    model: &str,
    temperature: f64,
    reasoning_effort: Option<&str>,
) -> Value {
    match format {
        OpenAiApiFormat::ChatCompletions => {
            build_chat_completions_request_body(messages, model, temperature, reasoning_effort)
        }
        OpenAiApiFormat::Responses => {
            build_responses_request_body(messages, model, temperature, reasoning_effort)
        }
        OpenAiApiFormat::Auto => panic!("Auto must be resolved before building request bodies"),
    }
}

pub fn build_openai_translation_messages(request: &OpenAiTranslationRequest) -> Vec<ChatMessage> {
    let source_language_name = if request.from_language == TranslationLanguage::Auto {
        "the detected language"
    } else {
        request.from_language.display_name()
    };
    let target_language_name = request.to_language.display_name();

    let mut system_prompt = OPENAI_TRANSLATION_SYSTEM_PROMPT.to_string();
    if let Some(custom_prompt) = request
        .custom_prompt
        .as_deref()
        .filter(|value| !value.trim().is_empty())
    {
        system_prompt.push_str("\n\nAdditional instructions: ");
        system_prompt.push_str(custom_prompt);
    }

    vec![
        ChatMessage::new(ChatRole::System, system_prompt),
        ChatMessage::new(
            ChatRole::User,
            format!(
                "Translate the following {source_language_name} text into {target_language_name} text: \"\"\"{}\"\"\"",
                request.text
            ),
        ),
    ]
}

pub fn build_openai_grammar_messages(
    language: TranslationLanguage,
    text: &str,
    include_explanations: bool,
) -> Vec<ChatMessage> {
    vec![
        ChatMessage::new(
            ChatRole::System,
            grammar_correction_system_prompt(include_explanations),
        ),
        ChatMessage::new(
            ChatRole::User,
            build_grammar_correction_user_prompt(language, text),
        ),
    ]
}

fn build_chat_completions_request_body(
    messages: &[ChatMessage],
    model: &str,
    temperature: f64,
    reasoning_effort: Option<&str>,
) -> Value {
    let mut root = Map::new();
    root.insert("model".to_string(), Value::String(model.to_string()));
    root.insert(
        "messages".to_string(),
        Value::Array(
            messages
                .iter()
                .map(|message| {
                    json!({
                        "role": message.role_str(),
                        "content": message.content,
                    })
                })
                .collect(),
        ),
    );
    root.insert("temperature".to_string(), json!(temperature));
    root.insert("stream".to_string(), Value::Bool(true));

    if let Some(reasoning_effort) = non_blank(reasoning_effort) {
        root.insert(
            "reasoning_effort".to_string(),
            Value::String(reasoning_effort.to_string()),
        );
    }

    Value::Object(root)
}

fn build_responses_request_body(
    messages: &[ChatMessage],
    model: &str,
    temperature: f64,
    reasoning_effort: Option<&str>,
) -> Value {
    let instructions = messages
        .iter()
        .find(|message| message.role == ChatRole::System)
        .map(|message| Value::String(message.content.clone()))
        .unwrap_or(Value::Null);
    let input = messages
        .iter()
        .filter(|message| message.role != ChatRole::System)
        .map(|message| message.content.as_str())
        .collect::<Vec<_>>()
        .join("\n\n");

    let mut root = Map::new();
    root.insert("model".to_string(), Value::String(model.to_string()));
    root.insert("instructions".to_string(), instructions);
    root.insert("input".to_string(), Value::String(input));
    root.insert("temperature".to_string(), json!(temperature));
    root.insert("stream".to_string(), Value::Bool(true));
    root.insert("store".to_string(), Value::Bool(false));

    if let Some(reasoning_effort) = non_blank(reasoning_effort) {
        root.insert(
            "reasoning".to_string(),
            json!({ "effort": reasoning_effort }),
        );
    }

    Value::Object(root)
}

fn non_blank(value: Option<&str>) -> Option<&str> {
    value.filter(|value| !value.trim().is_empty())
}

fn supports_none_reasoning_effort(model: &str) -> bool {
    let normalized = model.trim().to_ascii_lowercase();
    let Some(suffix) = normalized.strip_prefix("gpt-5.") else {
        return false;
    };

    let version_digits = suffix
        .chars()
        .take_while(|character| character.is_ascii_digit())
        .collect::<String>();
    !version_digits.is_empty()
        && version_digits
            .parse::<u32>()
            .is_ok_and(|minor_version| minor_version >= 1)
}

fn is_legacy_gpt5_reasoning_model(model: &str) -> bool {
    let normalized = model.trim().to_ascii_lowercase();
    normalized == "gpt-5"
        || normalized.starts_with("gpt-5-2025-")
        || normalized == "gpt-5-mini"
        || normalized.starts_with("gpt-5-mini-")
        || normalized == "gpt-5-nano"
        || normalized.starts_with("gpt-5-nano-")
}

fn grammar_result_dto(
    parsed: GrammarCorrectionResult,
    raw_text: String,
    service_id: String,
    service_name: String,
    language: TranslationLanguage,
) -> GrammarCorrectResultDto {
    let has_corrections = parsed.has_corrections();
    GrammarCorrectResultDto {
        original_text: parsed.original_text,
        corrected_text: parsed.corrected_text,
        explanation: parsed.explanation,
        raw_text: Some(raw_text),
        service_id: Some(service_id),
        service_name: Some(service_name),
        language: Some(language.to_code().to_string()),
        timing_ms: None,
        has_corrections,
    }
}

fn attach_service_id(mut error: OpenAiExecutionError, service_id: &str) -> OpenAiExecutionError {
    if error.service_id.is_none() {
        error.service_id = Some(service_id.to_string());
    }
    error
}

fn extract_openai_error_message(error_body: &str) -> Option<String> {
    let root: Value = serde_json::from_str(error_body).ok()?;
    root.get("error")
        .and_then(|error| error.get("message"))
        .and_then(Value::as_str)
        .map(str::to_string)
        .filter(|message| !message.is_empty())
}

fn normalized_optional(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
}

fn built_in_ai_secret_key() -> [u8; 16] {
    let hash = digest::digest(&digest::SHA256, BUILT_IN_AI_SECRET_ASSEMBLY_NAME.as_bytes());
    let mut hex = String::with_capacity(hash.as_ref().len() * 2);
    for byte in hash.as_ref() {
        let _ = write!(&mut hex, "{byte:02x}");
    }

    let mut key = [0_u8; 16];
    key.copy_from_slice(&hex.as_bytes()[..16]);
    key
}

fn is_foundry_local_provider(value: Option<&str>) -> bool {
    let normalized = value
        .unwrap_or_default()
        .trim()
        .to_ascii_lowercase()
        .replace(['-', '_', ' '], "");
    normalized == local_ai_provider_modes::FOUNDRY_LOCAL.to_ascii_lowercase()
}

fn is_auto_local_ai_provider(value: Option<&str>) -> bool {
    let normalized = value
        .unwrap_or(local_ai_provider_modes::AUTO)
        .trim()
        .to_ascii_lowercase()
        .replace(['-', '_', ' '], "");
    normalized.is_empty() || normalized == local_ai_provider_modes::AUTO.to_ascii_lowercase()
}

fn can_use_configured_foundry_local_endpoint(
    provider: Option<&str>,
    endpoint: Option<&str>,
) -> bool {
    normalized_optional(endpoint).is_some()
        && (is_foundry_local_provider(provider) || is_auto_local_ai_provider(provider))
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

fn is_loopback_url(url: &reqwest::Url) -> bool {
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
    reqwest::Url::parse(endpoint)
        .ok()
        .as_ref()
        .is_some_and(is_loopback_url)
}

fn split_host_port<'a>(authority: &'a str, scheme: &str) -> Option<(&'a str, u16)> {
    if authority.starts_with('[') {
        let closing = authority.find(']')?;
        let host = &authority[..=closing];
        let port = authority[(closing + 1)..]
            .strip_prefix(':')
            .and_then(|value| value.parse::<u16>().ok())
            .unwrap_or_else(|| default_port_for_scheme(scheme));
        return Some((host, port));
    }

    if let Some((host, port)) = authority.rsplit_once(':') {
        if let Ok(port) = port.parse::<u16>() {
            if !host.is_empty() {
                return Some((host, port));
            }
        }
    }

    Some((authority, default_port_for_scheme(scheme)))
}

fn default_port_for_scheme(scheme: &str) -> u16 {
    if scheme.eq_ignore_ascii_case("https") {
        443
    } else {
        80
    }
}

fn absolute_url_path(endpoint: &str) -> Option<&str> {
    let endpoint = endpoint.trim();
    let scheme_end = endpoint.find("://")?;
    if scheme_end == 0 {
        return None;
    }

    let after_authority = &endpoint[(scheme_end + 3)..];
    if after_authority.is_empty() {
        return None;
    }

    let path_start = after_authority.find('/').unwrap_or(after_authority.len());
    let path_and_suffix = &after_authority[path_start..];
    let path_end = path_and_suffix
        .find(['?', '#'])
        .unwrap_or(path_and_suffix.len());
    Some(&path_and_suffix[..path_end])
}
