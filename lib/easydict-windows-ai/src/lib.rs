use std::fmt;
use std::path::{Path, PathBuf};

mod winrt_language_model;

pub const SERVICE_ID: &str = "windows-local-ai";
pub const SERVICE_NAME: &str = "Phi Silica";
pub const WARM_UP_PROMPT: &str = "Reply with OK.";
pub const WINDOWS_AI_DISABLE_WINRT_ENV: &str = "EASYDICT_WINDOWS_AI_DISABLE_WINRT";
pub const WINDOWS_APP_SDK_AI_PACKAGE_ID: &str = "microsoft.windowsappsdk.ai";
pub const WINDOWS_AI_METADATA_ENV: &str = "EASYDICT_WINDOWS_APP_SDK_AI_METADATA_DIR";
pub const WINDOWS_AI_NUGET_ROOT_ENV: &str = "EASYDICT_WINDOWS_APP_SDK_AI_NUGET_ROOT";
pub const WINDOWS_AI_WINMD_FILE: &str = "Microsoft.Windows.AI.winmd";
pub const WINDOWS_AI_FOUNDATION_WINMD_FILE: &str = "Microsoft.Windows.AI.Foundation.winmd";
pub const WINDOWS_AI_TEXT_WINMD_FILE: &str = "Microsoft.Windows.AI.Text.winmd";

pub const STATUS_READY: &str = "WindowsLocalAI_Status_Ready";
pub const STATUS_NOT_READY: &str = "WindowsLocalAI_Status_NotReady";
pub const STATUS_PREPARING: &str = "WindowsLocalAI_Status_Preparing";
pub const STATUS_PREPARE_FAILED: &str = "WindowsLocalAI_Status_PrepareFailed";
pub const STATUS_CAPABILITY_MISSING: &str = "WindowsLocalAI_Status_CapabilityMissing";
pub const STATUS_NOT_COMPATIBLE_HARDWARE: &str = "WindowsLocalAI_Status_NotCompatibleHardware";
pub const STATUS_OS_UPDATE_NEEDED: &str = "WindowsLocalAI_Status_OSUpdateNeeded";
pub const STATUS_DISABLED_BY_USER: &str = "WindowsLocalAI_Status_DisabledByUser";
pub const STATUS_UNSUPPORTED_WINDOWS_AI_BASELINE: &str =
    "WindowsLocalAI_Status_UnsupportedWindowsAIBaseline";
pub const STATUS_NOT_SUPPORTED: &str = "WindowsLocalAI_Status_NotSupported";
pub const STATUS_RUNTIME_UNHEALTHY: &str = "WindowsLocalAI_Status_RuntimeUnhealthy";
pub const STATUS_WARMING_UP: &str = "WindowsLocalAI_Status_WarmingUp";
pub const STATUS_WARMUP_REQUIRED: &str = "WindowsLocalAI_Status_WarmupRequired";

pub const MINIMUM_SUPPORTED_OS_BASELINE: WindowsAiOsVersion = WindowsAiOsVersion {
    major: 10,
    minor: 0,
    build: 26200,
    revision: 7309,
};

const GRAMMAR_CORRECTION_SYSTEM_PROMPT: &str = r#"You are a grammar correction expert. Your task is to correct grammar, spelling, and punctuation errors in the text provided by the user.

Rules:
1. NEVER translate the text. The output must be in the exact same language as the input.
2. Keep the original meaning unchanged.
3. Only fix actual errors; do not rephrase, paraphrase, or "polish" correct text.
4. Output ONLY the corrected text with no additional commentary, labels, or formatting.
5. If the text has no errors, output it unchanged."#;

const GRAMMAR_CORRECTION_SYSTEM_PROMPT_WITH_EXPLANATION: &str = r#"You are a grammar correction expert. Your task is to correct grammar, spelling, and punctuation errors in the text provided by the user.

Rules:
1. NEVER translate the text. The output must be in the exact same language as the input.
2. Keep the original meaning unchanged.
3. Only fix actual errors; do not rephrase, paraphrase, or "polish" correct text.
4. First output the fully corrected text, then on a new line output "---", then briefly list the key corrections you made.
5. The "---" separator MUST be on its own line after the corrected text. NEVER put "---" before the corrected text.
6. If the text has no errors, output it unchanged followed by "---" and "No errors found.""#;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowsAiReadyState {
    Ready,
    NotReady,
    CapabilityMissing,
    NotCompatibleWithSystemHardware,
    OsUpdateNeeded,
    DisabledByUser,
    UnsupportedWindowsAiBaseline,
    NotSupportedOnCurrentSystem,
}

impl WindowsAiReadyState {
    pub fn is_provider_candidate(self) -> bool {
        matches!(self, Self::Ready | Self::NotReady)
    }

    pub fn user_message(self) -> &'static str {
        match self {
            Self::Ready => "Phi Silica is ready.",
            Self::NotReady => "Phi Silica model is not ready.",
            Self::CapabilityMissing => "Phi Silica requires the systemAIModels app capability.",
            Self::NotCompatibleWithSystemHardware => {
                "Phi Silica requires compatible Copilot+ PC hardware."
            }
            Self::OsUpdateNeeded => "Phi Silica requires a Windows AI component update.",
            Self::DisabledByUser => "Phi Silica is disabled by the current Windows settings.",
            Self::UnsupportedWindowsAiBaseline => {
                "Phi Silica requires a newer Windows AI baseline."
            }
            Self::NotSupportedOnCurrentSystem => {
                "Phi Silica is not supported on the current system."
            }
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowsAiModelState {
    Ready,
    NeedsPreparation,
    NotCompatible,
    Failed,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiStatus {
    pub state: WindowsAiModelState,
    pub resource_key: &'static str,
    pub message: String,
    pub ready_state: WindowsAiReadyState,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiError {
    message: String,
}

impl WindowsAiError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }

    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for WindowsAiError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for WindowsAiError {}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowsAiLanguage {
    Auto,
    SimplifiedChinese,
    TraditionalChinese,
    Japanese,
    Korean,
    English,
    German,
    Dutch,
    Swedish,
    Norwegian,
    Danish,
    French,
    Spanish,
    Portuguese,
    Italian,
    Romanian,
    Russian,
    Polish,
    Czech,
    Ukrainian,
    Bulgarian,
    Slovak,
    Slovenian,
    Estonian,
    Latvian,
    Lithuanian,
    Greek,
    Hungarian,
    Finnish,
    Turkish,
    Arabic,
    Persian,
    Hebrew,
    Hindi,
    Bengali,
    Tamil,
    Telugu,
    Urdu,
    Vietnamese,
    Thai,
    Indonesian,
    Malay,
    Filipino,
    ClassicalChinese,
}

impl WindowsAiLanguage {
    pub fn from_code(code: &str) -> Option<Self> {
        match code.trim().to_ascii_lowercase().as_str() {
            "" | "auto" => Some(Self::Auto),
            "zh" | "zh-cn" | "zh-hans" => Some(Self::SimplifiedChinese),
            "zh-tw" | "zh-hant" => Some(Self::TraditionalChinese),
            "zh-classical" | "lzh" => Some(Self::ClassicalChinese),
            "ja" => Some(Self::Japanese),
            "ko" => Some(Self::Korean),
            "en" | "en-us" => Some(Self::English),
            "de" => Some(Self::German),
            "nl" => Some(Self::Dutch),
            "sv" => Some(Self::Swedish),
            "no" | "nb" | "nb-no" => Some(Self::Norwegian),
            "da" => Some(Self::Danish),
            "fr" => Some(Self::French),
            "es" => Some(Self::Spanish),
            "pt" | "pt-br" => Some(Self::Portuguese),
            "it" => Some(Self::Italian),
            "ro" => Some(Self::Romanian),
            "ru" => Some(Self::Russian),
            "pl" => Some(Self::Polish),
            "cs" => Some(Self::Czech),
            "uk" => Some(Self::Ukrainian),
            "bg" => Some(Self::Bulgarian),
            "sk" => Some(Self::Slovak),
            "sl" => Some(Self::Slovenian),
            "et" => Some(Self::Estonian),
            "lv" => Some(Self::Latvian),
            "lt" => Some(Self::Lithuanian),
            "el" => Some(Self::Greek),
            "hu" => Some(Self::Hungarian),
            "fi" => Some(Self::Finnish),
            "tr" => Some(Self::Turkish),
            "ar" => Some(Self::Arabic),
            "fa" => Some(Self::Persian),
            "he" => Some(Self::Hebrew),
            "hi" => Some(Self::Hindi),
            "bn" => Some(Self::Bengali),
            "ta" => Some(Self::Tamil),
            "te" => Some(Self::Telugu),
            "ur" => Some(Self::Urdu),
            "vi" => Some(Self::Vietnamese),
            "th" => Some(Self::Thai),
            "id" => Some(Self::Indonesian),
            "ms" => Some(Self::Malay),
            "tl" | "fil" => Some(Self::Filipino),
            _ => None,
        }
    }

    pub fn display_name(self) -> &'static str {
        match self {
            Self::Auto => "Auto Detect",
            Self::SimplifiedChinese => "Simplified Chinese",
            Self::TraditionalChinese => "Traditional Chinese",
            Self::ClassicalChinese => "Classical Chinese",
            Self::Japanese => "Japanese",
            Self::Korean => "Korean",
            Self::English => "English",
            Self::German => "German",
            Self::Dutch => "Dutch",
            Self::Swedish => "Swedish",
            Self::Norwegian => "Norwegian",
            Self::Danish => "Danish",
            Self::French => "French",
            Self::Spanish => "Spanish",
            Self::Portuguese => "Portuguese",
            Self::Italian => "Italian",
            Self::Romanian => "Romanian",
            Self::Russian => "Russian",
            Self::Polish => "Polish",
            Self::Czech => "Czech",
            Self::Ukrainian => "Ukrainian",
            Self::Bulgarian => "Bulgarian",
            Self::Slovak => "Slovak",
            Self::Slovenian => "Slovenian",
            Self::Estonian => "Estonian",
            Self::Latvian => "Latvian",
            Self::Lithuanian => "Lithuanian",
            Self::Greek => "Greek",
            Self::Hungarian => "Hungarian",
            Self::Finnish => "Finnish",
            Self::Turkish => "Turkish",
            Self::Arabic => "Arabic",
            Self::Persian => "Persian",
            Self::Hebrew => "Hebrew",
            Self::Hindi => "Hindi",
            Self::Bengali => "Bengali",
            Self::Tamil => "Tamil",
            Self::Telugu => "Telugu",
            Self::Urdu => "Urdu",
            Self::Vietnamese => "Vietnamese",
            Self::Thai => "Thai",
            Self::Indonesian => "Indonesian",
            Self::Malay => "Malay",
            Self::Filipino => "Filipino",
        }
    }

    pub fn iso639(self) -> &'static str {
        match self {
            Self::Auto => "auto",
            Self::SimplifiedChinese => "zh-CN",
            Self::TraditionalChinese => "zh-TW",
            Self::ClassicalChinese => "zh-CN",
            Self::English => "en",
            Self::Japanese => "ja",
            Self::Korean => "ko",
            Self::French => "fr",
            Self::Spanish => "es",
            Self::Portuguese => "pt",
            Self::Italian => "it",
            Self::German => "de",
            Self::Russian => "ru",
            Self::Arabic => "ar",
            Self::Swedish => "sv",
            Self::Romanian => "ro",
            Self::Thai => "th",
            Self::Dutch => "nl",
            Self::Hungarian => "hu",
            Self::Greek => "el",
            Self::Danish => "da",
            Self::Finnish => "fi",
            Self::Polish => "pl",
            Self::Czech => "cs",
            Self::Turkish => "tr",
            Self::Ukrainian => "uk",
            Self::Bulgarian => "bg",
            Self::Indonesian => "id",
            Self::Malay => "ms",
            Self::Vietnamese => "vi",
            Self::Persian => "fa",
            Self::Hindi => "hi",
            Self::Telugu => "te",
            Self::Tamil => "ta",
            Self::Urdu => "ur",
            Self::Filipino => "tl",
            Self::Bengali => "bn",
            Self::Norwegian => "no",
            Self::Hebrew => "he",
            Self::Slovak => "sk",
            Self::Slovenian => "sl",
            Self::Estonian => "et",
            Self::Latvian => "lv",
            Self::Lithuanian => "lt",
        }
    }

    fn prompt_label(self) -> String {
        match self {
            Self::SimplifiedChinese => "Simplified Chinese (zh-CN)".to_string(),
            Self::TraditionalChinese => "Traditional Chinese (zh-TW)".to_string(),
            Self::ClassicalChinese => "Classical Chinese (zh-CN)".to_string(),
            language => format!("{} ({})", language.display_name(), language.iso639()),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WindowsAiGenerationOptions {
    pub temperature: f32,
    pub top_k: u32,
    pub top_p: f32,
}

impl Default for WindowsAiGenerationOptions {
    fn default() -> Self {
        Self {
            temperature: 0.1,
            top_k: 1,
            top_p: 0.9,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowsAiResponseStatus {
    Complete,
    PromptLargerThanContext,
    BlockedByPolicy,
    Error,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiResponse {
    pub status: WindowsAiResponseStatus,
    pub text: String,
    pub error_message: Option<String>,
}

impl WindowsAiResponse {
    pub fn complete(text: impl Into<String>) -> Self {
        Self {
            status: WindowsAiResponseStatus::Complete,
            text: text.into(),
            error_message: None,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiTranslationRequest {
    pub text: String,
    pub from_language: WindowsAiLanguage,
    pub to_language: WindowsAiLanguage,
    pub custom_prompt: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiTranslationOutcome {
    pub translated_text: String,
    pub detected_language: WindowsAiLanguage,
    pub target_language: WindowsAiLanguage,
    pub service_name: &'static str,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiStreamOutcome {
    pub result: WindowsAiTranslationOutcome,
    pub chunks: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiGrammarCorrectionRequest {
    pub text: String,
    pub language: WindowsAiLanguage,
    pub include_explanations: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowsAiTranslationErrorKind {
    InvalidResponse,
    UnsupportedLanguage,
    LocalModelNeedsPreparation,
    ServiceUnavailable,
    TextTooLong,
    Unknown,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiTranslationError {
    pub kind: WindowsAiTranslationErrorKind,
    message: String,
}

impl WindowsAiTranslationError {
    pub fn new(kind: WindowsAiTranslationErrorKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
        }
    }

    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for WindowsAiTranslationError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for WindowsAiTranslationError {}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiHealthFingerprint {
    pub os_build: String,
    pub ubr: Option<u32>,
    pub windows_app_sdk_version: String,
    pub process_architecture: String,
    pub backend_name: String,
    pub component_marker: String,
    pub windows_activated: Option<bool>,
    pub phi_silica_ai_components_present: Option<bool>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsAiWinmdSet {
    pub metadata_dir: PathBuf,
    pub windows_ai: PathBuf,
    pub windows_ai_foundation: PathBuf,
    pub windows_ai_text: PathBuf,
}

impl WindowsAiWinmdSet {
    pub fn from_metadata_dir(metadata_dir: impl AsRef<Path>) -> Option<Self> {
        let metadata_dir = metadata_dir.as_ref();
        let windows_ai = metadata_dir.join(WINDOWS_AI_WINMD_FILE);
        let windows_ai_foundation = metadata_dir.join(WINDOWS_AI_FOUNDATION_WINMD_FILE);
        let windows_ai_text = metadata_dir.join(WINDOWS_AI_TEXT_WINMD_FILE);
        if windows_ai.is_file() && windows_ai_foundation.is_file() && windows_ai_text.is_file() {
            Some(Self {
                metadata_dir: metadata_dir.to_path_buf(),
                windows_ai,
                windows_ai_foundation,
                windows_ai_text,
            })
        } else {
            None
        }
    }
}

impl fmt::Display for WindowsAiHealthFingerprint {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let ubr = self
            .ubr
            .map(|value| value.to_string())
            .unwrap_or_else(|| "unknown".to_string());
        let windows_activated = self
            .windows_activated
            .map(|value| value.to_string())
            .unwrap_or_else(|| "unknown".to_string());
        let ai_components = self
            .phi_silica_ai_components_present
            .map(|value| value.to_string())
            .unwrap_or_else(|| "unknown".to_string());

        write!(
            formatter,
            "osBuild={}; ubr={}; windowsAppSdk={}; processArch={}; backend={}; component={}; windowsActivated={}; phiSilicaAiComponentsPresent={}",
            self.os_build,
            ubr,
            self.windows_app_sdk_version,
            self.process_architecture,
            self.backend_name,
            self.component_marker,
            windows_activated,
            ai_components
        )
    }
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct WindowsAiOsVersion {
    pub major: u16,
    pub minor: u16,
    pub build: u32,
    pub revision: u32,
}

pub trait WindowsAiLanguageModelProbe {
    fn ready_state(&mut self) -> WindowsAiReadyState;

    fn health_fingerprint(&mut self) -> Option<WindowsAiHealthFingerprint> {
        None
    }

    fn ensure_ready(&mut self) -> Result<WindowsAiReadyState, WindowsAiError> {
        Ok(self.ready_state())
    }
}

pub trait WindowsAiLanguageModelClient: WindowsAiLanguageModelProbe {
    fn generate(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<WindowsAiResponse, WindowsAiError>;

    fn generate_stream(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<Vec<String>, WindowsAiError>;

    fn warm_up(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<(), WindowsAiError>;
}

#[derive(Clone, Debug, Default)]
pub struct UnsupportedWindowsAiProbe;

impl WindowsAiLanguageModelProbe for UnsupportedWindowsAiProbe {
    fn ready_state(&mut self) -> WindowsAiReadyState {
        WindowsAiReadyState::NotSupportedOnCurrentSystem
    }
}

#[derive(Clone, Debug)]
pub struct DefaultWindowsAiLanguageModelClient {
    winrt_disabled: bool,
}

impl Default for DefaultWindowsAiLanguageModelClient {
    fn default() -> Self {
        Self::new()
    }
}

impl DefaultWindowsAiLanguageModelClient {
    pub fn new() -> Self {
        Self {
            winrt_disabled: windows_ai_winrt_is_disabled_by_environment(),
        }
    }

    pub fn disabled() -> Self {
        Self {
            winrt_disabled: true,
        }
    }

    fn disabled_ready_state(&self) -> Option<WindowsAiReadyState> {
        self.winrt_disabled
            .then_some(WindowsAiReadyState::NotSupportedOnCurrentSystem)
    }

    fn disabled_error(&self) -> Option<WindowsAiError> {
        self.winrt_disabled
            .then(|| WindowsAiError::new("Windows AI WinRT client is disabled by environment."))
    }
}

impl WindowsAiLanguageModelProbe for DefaultWindowsAiLanguageModelClient {
    fn ready_state(&mut self) -> WindowsAiReadyState {
        self.disabled_ready_state()
            .unwrap_or_else(winrt_language_model::ready_state)
    }

    fn health_fingerprint(&mut self) -> Option<WindowsAiHealthFingerprint> {
        if self.winrt_disabled {
            return None;
        }

        current_windows_ai_health_fingerprint()
    }

    fn ensure_ready(&mut self) -> Result<WindowsAiReadyState, WindowsAiError> {
        if let Some(state) = self.disabled_ready_state() {
            return Ok(state);
        }

        winrt_language_model::ensure_ready()
    }
}

impl WindowsAiLanguageModelClient for DefaultWindowsAiLanguageModelClient {
    fn generate(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<WindowsAiResponse, WindowsAiError> {
        if let Some(error) = self.disabled_error() {
            return Err(error);
        }

        winrt_language_model::generate(prompt, options)
    }

    fn generate_stream(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<Vec<String>, WindowsAiError> {
        if let Some(error) = self.disabled_error() {
            return Err(error);
        }

        winrt_language_model::generate_stream(prompt, options)
    }

    fn warm_up(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<(), WindowsAiError> {
        if let Some(error) = self.disabled_error() {
            return Err(error);
        }

        winrt_language_model::warm_up(prompt, options)
    }
}

pub fn default_windows_ai_language_model_client() -> DefaultWindowsAiLanguageModelClient {
    DefaultWindowsAiLanguageModelClient::new()
}

pub fn current_windows_ai_health_fingerprint() -> Option<WindowsAiHealthFingerprint> {
    platform_windows_ai_health_fingerprint()
}

#[cfg(windows)]
fn platform_windows_ai_health_fingerprint() -> Option<WindowsAiHealthFingerprint> {
    let version = current_windows_os_version()?;
    let ubr = current_windows_ubr().or_else(|| (version.revision > 0).then_some(version.revision));
    let windows_app_sdk_version = find_windows_ai_winmd_set()
        .as_ref()
        .and_then(windows_app_sdk_version_from_winmd_set)
        .unwrap_or_else(|| "unknown".to_string());

    Some(WindowsAiHealthFingerprint {
        os_build: format!("{}.{}.{}", version.major, version.minor, version.build),
        ubr,
        windows_app_sdk_version,
        process_architecture: std::env::consts::ARCH.to_string(),
        backend_name: "WinRT".to_string(),
        component_marker: windows_ai_component_marker(),
        windows_activated: None,
        phi_silica_ai_components_present: None,
    })
}

#[cfg(not(windows))]
fn platform_windows_ai_health_fingerprint() -> Option<WindowsAiHealthFingerprint> {
    None
}

#[cfg(windows)]
fn current_windows_os_version() -> Option<WindowsAiOsVersion> {
    use windows_sys::Win32::System::SystemInformation::OSVERSIONINFOW;

    #[link(name = "ntdll")]
    unsafe extern "system" {
        fn RtlGetVersion(version_information: *mut OSVERSIONINFOW) -> i32;
    }

    let mut version = OSVERSIONINFOW {
        dwOSVersionInfoSize: std::mem::size_of::<OSVERSIONINFOW>() as u32,
        ..Default::default()
    };
    let status = unsafe { RtlGetVersion(&mut version) };
    if status != 0 {
        return None;
    }

    Some(WindowsAiOsVersion {
        major: version.dwMajorVersion as u16,
        minor: version.dwMinorVersion as u16,
        build: version.dwBuildNumber,
        revision: 0,
    })
}

#[cfg(windows)]
fn current_windows_ubr() -> Option<u32> {
    use std::ffi::c_void;
    use windows_sys::Win32::System::Registry::{
        RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_DWORD,
    };

    let subkey = wide_null("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion");
    let value_name = wide_null("UBR");
    let mut data = 0_u32;
    let mut data_size = std::mem::size_of::<u32>() as u32;
    let mut data_type = 0;
    let status = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            subkey.as_ptr(),
            value_name.as_ptr(),
            RRF_RT_REG_DWORD,
            &mut data_type,
            (&mut data as *mut u32).cast::<c_void>(),
            &mut data_size,
        )
    };

    (status == 0).then_some(data)
}

#[cfg(windows)]
fn wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn windows_app_sdk_version_from_winmd_set(winmd: &WindowsAiWinmdSet) -> Option<String> {
    let version = winmd.metadata_dir.parent()?.file_name()?.to_string_lossy();
    (!version_parts(&version).is_empty()).then(|| version.to_string())
}

fn windows_ai_component_marker() -> String {
    if cfg!(all(
        target_os = "windows",
        easydict_windows_ai_winrt_bindings
    )) {
        "Microsoft.Windows.AI.Text.LanguageModel".to_string()
    } else {
        "WinRT bindings unavailable".to_string()
    }
}

fn windows_ai_winrt_is_disabled_by_environment() -> bool {
    std::env::var(WINDOWS_AI_DISABLE_WINRT_ENV)
        .ok()
        .map(|value| {
            let value = value.trim().to_ascii_lowercase();
            matches!(value.as_str(), "1" | "true" | "yes" | "on")
        })
        .unwrap_or(false)
}

pub fn windows_ai_status<P>(probe: &mut P) -> WindowsAiStatus
where
    P: WindowsAiLanguageModelProbe,
{
    let fingerprint = probe.health_fingerprint();
    let ready_state = if fingerprint
        .as_ref()
        .is_some_and(windows_ai_baseline_is_unsupported)
    {
        WindowsAiReadyState::UnsupportedWindowsAiBaseline
    } else {
        probe.ready_state()
    };

    status_for_ready_state(ready_state)
}

pub fn status_for_ready_state(ready_state: WindowsAiReadyState) -> WindowsAiStatus {
    let (state, resource_key) = match ready_state {
        WindowsAiReadyState::Ready => (WindowsAiModelState::Ready, STATUS_READY),
        WindowsAiReadyState::NotReady => (WindowsAiModelState::NeedsPreparation, STATUS_NOT_READY),
        WindowsAiReadyState::CapabilityMissing => (
            WindowsAiModelState::NotCompatible,
            STATUS_CAPABILITY_MISSING,
        ),
        WindowsAiReadyState::NotCompatibleWithSystemHardware => (
            WindowsAiModelState::NotCompatible,
            STATUS_NOT_COMPATIBLE_HARDWARE,
        ),
        WindowsAiReadyState::OsUpdateNeeded => {
            (WindowsAiModelState::NotCompatible, STATUS_OS_UPDATE_NEEDED)
        }
        WindowsAiReadyState::DisabledByUser => {
            (WindowsAiModelState::NotCompatible, STATUS_DISABLED_BY_USER)
        }
        WindowsAiReadyState::UnsupportedWindowsAiBaseline => (
            WindowsAiModelState::NotCompatible,
            STATUS_UNSUPPORTED_WINDOWS_AI_BASELINE,
        ),
        WindowsAiReadyState::NotSupportedOnCurrentSystem => {
            (WindowsAiModelState::NotCompatible, STATUS_NOT_SUPPORTED)
        }
    };

    WindowsAiStatus {
        state,
        resource_key,
        message: ready_state.user_message().to_string(),
        ready_state,
    }
}

pub fn prepare_windows_ai_model<P>(probe: &mut P) -> Result<WindowsAiStatus, WindowsAiError>
where
    P: WindowsAiLanguageModelProbe,
{
    let initial_status = windows_ai_status(probe);
    match initial_status.ready_state {
        WindowsAiReadyState::Ready => Ok(initial_status),
        WindowsAiReadyState::NotReady => {
            let prepared_state = probe.ensure_ready()?;
            if prepared_state == WindowsAiReadyState::NotReady {
                return Ok(WindowsAiStatus {
                    state: WindowsAiModelState::Failed,
                    resource_key: STATUS_PREPARE_FAILED,
                    message: "Phi Silica model preparation did not complete.".to_string(),
                    ready_state: WindowsAiReadyState::NotReady,
                });
            }

            Ok(status_for_ready_state(prepared_state))
        }
        _ => Ok(initial_status),
    }
}

pub fn prepare_windows_ai_client<C>(client: &mut C) -> Result<WindowsAiStatus, WindowsAiError>
where
    C: WindowsAiLanguageModelClient,
{
    let initial_status = windows_ai_status(client);
    match initial_status.ready_state {
        WindowsAiReadyState::Ready => warm_up_windows_ai_client(client),
        WindowsAiReadyState::NotReady => {
            let prepared_state = client.ensure_ready()?;
            match prepared_state {
                WindowsAiReadyState::Ready => warm_up_windows_ai_client(client),
                WindowsAiReadyState::NotReady => Ok(WindowsAiStatus {
                    state: WindowsAiModelState::Failed,
                    resource_key: STATUS_PREPARE_FAILED,
                    message: "Phi Silica model preparation did not complete.".to_string(),
                    ready_state: WindowsAiReadyState::NotReady,
                }),
                state => Ok(status_for_ready_state(state)),
            }
        }
        _ => Ok(initial_status),
    }
}

pub fn find_windows_ai_winmd_set() -> Option<WindowsAiWinmdSet> {
    if let Some(metadata_dir) = std::env::var_os(WINDOWS_AI_METADATA_ENV).map(PathBuf::from) {
        if let Some(set) = WindowsAiWinmdSet::from_metadata_dir(metadata_dir) {
            return Some(set);
        }
    }

    let package_root = std::env::var_os(WINDOWS_AI_NUGET_ROOT_ENV)
        .map(PathBuf::from)
        .or_else(default_windows_app_sdk_ai_nuget_root)?;
    find_latest_windows_ai_winmd_set_under(&package_root)
}

pub fn find_latest_windows_ai_winmd_set_under(package_root: &Path) -> Option<WindowsAiWinmdSet> {
    let mut candidates = std::fs::read_dir(package_root)
        .ok()?
        .flatten()
        .filter_map(|entry| {
            let path = entry.path();
            if !path.is_dir() {
                return None;
            }
            let version = path.file_name()?.to_string_lossy().to_string();
            let metadata_dir = path.join("metadata");
            WindowsAiWinmdSet::from_metadata_dir(&metadata_dir).map(|set| (version, set))
        })
        .collect::<Vec<_>>();

    candidates.sort_by(|left, right| compare_version_strings(&left.0, &right.0));
    candidates.pop().map(|(_, set)| set)
}

pub fn windows_ai_bindgen_args(
    winmd: &WindowsAiWinmdSet,
    output_file: impl AsRef<Path>,
) -> Vec<String> {
    vec![
        "--in".to_string(),
        "default".to_string(),
        winmd.metadata_dir.to_string_lossy().to_string(),
        "--out".to_string(),
        output_file.as_ref().to_string_lossy().to_string(),
        "--no-allow".to_string(),
        "--filter".to_string(),
        "Microsoft.Windows.AI.AIFeatureReadyResult".to_string(),
        "Microsoft.Windows.AI.AIFeatureReadyResultState".to_string(),
        "Microsoft.Windows.AI.AIFeatureReadyState".to_string(),
        "Microsoft.Windows.AI.ContentSafety".to_string(),
        "Microsoft.Windows.AI.Foundation".to_string(),
        "Microsoft.Windows.AI.Text".to_string(),
    ]
}

fn default_windows_app_sdk_ai_nuget_root() -> Option<PathBuf> {
    std::env::var_os("USERPROFILE")
        .map(PathBuf::from)
        .map(|home| {
            home.join(".nuget")
                .join("packages")
                .join(WINDOWS_APP_SDK_AI_PACKAGE_ID)
        })
        .filter(|path| path.is_dir())
}

fn compare_version_strings(left: &str, right: &str) -> std::cmp::Ordering {
    let left_parts = version_parts(left);
    let right_parts = version_parts(right);
    left_parts.cmp(&right_parts).then_with(|| left.cmp(right))
}

fn version_parts(value: &str) -> Vec<u64> {
    value
        .split(|character: char| !character.is_ascii_digit())
        .filter(|part| !part.is_empty())
        .map(|part| part.parse::<u64>().unwrap_or(0))
        .collect()
}

fn warm_up_windows_ai_client<C>(client: &mut C) -> Result<WindowsAiStatus, WindowsAiError>
where
    C: WindowsAiLanguageModelClient,
{
    match client.warm_up(WARM_UP_PROMPT, WindowsAiGenerationOptions::default()) {
        Ok(()) => Ok(status_for_ready_state(WindowsAiReadyState::Ready)),
        Err(error) => Ok(runtime_unhealthy_status(
            error.message(),
            client.health_fingerprint(),
        )),
    }
}

pub fn runtime_unhealthy_status(
    detail_message: &str,
    fingerprint: Option<WindowsAiHealthFingerprint>,
) -> WindowsAiStatus {
    let detail = format_runtime_failure_detail(detail_message, fingerprint.as_ref());
    if looks_like_unsupported_windows_ai_baseline(fingerprint.as_ref(), Some(&detail)) {
        return WindowsAiStatus {
            state: WindowsAiModelState::Failed,
            resource_key: STATUS_UNSUPPORTED_WINDOWS_AI_BASELINE,
            message: detail,
            ready_state: WindowsAiReadyState::UnsupportedWindowsAiBaseline,
        };
    }

    WindowsAiStatus {
        state: WindowsAiModelState::Failed,
        resource_key: STATUS_RUNTIME_UNHEALTHY,
        message: detail,
        ready_state: WindowsAiReadyState::Ready,
    }
}

pub fn format_runtime_failure_detail(
    detail_message: &str,
    fingerprint: Option<&WindowsAiHealthFingerprint>,
) -> String {
    let detail = detail_message.trim();
    let mut parts = Vec::new();
    if !detail.is_empty() {
        parts.push(detail.to_string());
    }

    if let Some(fingerprint) = fingerprint {
        add_diagnostic_if_missing(&mut parts, detail, "osBuild", &fingerprint.os_build);
        add_diagnostic_if_missing(
            &mut parts,
            detail,
            "ubr",
            &fingerprint
                .ubr
                .map(|value| value.to_string())
                .unwrap_or_else(|| "unknown".to_string()),
        );
        add_diagnostic_if_missing(
            &mut parts,
            detail,
            "windowsAppSdk",
            &fingerprint.windows_app_sdk_version,
        );
        add_diagnostic_if_missing(
            &mut parts,
            detail,
            "processArch",
            &fingerprint.process_architecture,
        );
        add_diagnostic_if_missing(&mut parts, detail, "backend", &fingerprint.backend_name);
        add_diagnostic_if_missing(
            &mut parts,
            detail,
            "component",
            &fingerprint.component_marker,
        );
        add_diagnostic_if_missing(
            &mut parts,
            detail,
            "windowsActivated",
            &optional_bool_text(fingerprint.windows_activated),
        );
        add_diagnostic_if_missing(
            &mut parts,
            detail,
            "phiSilicaAiComponentsPresent",
            &optional_bool_text(fingerprint.phi_silica_ai_components_present),
        );
    }

    if parts.is_empty() {
        "Windows AI runtime failed while running Phi Silica.".to_string()
    } else {
        parts.join("; ")
    }
}

fn optional_bool_text(value: Option<bool>) -> String {
    value
        .map(|value| value.to_string())
        .unwrap_or_else(|| "unknown".to_string())
}

fn add_diagnostic_if_missing(
    parts: &mut Vec<String>,
    existing_detail: &str,
    key: &str,
    value: &str,
) {
    if value.trim().is_empty()
        || existing_detail
            .to_ascii_lowercase()
            .contains(&format!("{}=", key.to_ascii_lowercase()))
    {
        return;
    }

    parts.push(format!("{key}={value}"));
}

pub fn build_translation_prompt(request: &WindowsAiTranslationRequest) -> String {
    let from = if request.from_language == WindowsAiLanguage::Auto {
        "the source language, auto-detected".to_string()
    } else {
        request.from_language.prompt_label()
    };
    let to = request.to_language.prompt_label();
    let custom = request
        .custom_prompt
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|value| format!("\n\nAdditional user instruction:\n{value}"))
        .unwrap_or_default();

    format!(
        "You are a professional translation engine used inside a desktop dictionary app.\n\n\
Task:\n\
Translate the text from {from} to {to}.\n\n\
Rules:\n\
- Output only the translated text.\n\
- Do not explain.\n\
- Do not add greetings, notes, markdown fences, or alternatives.\n\
- Preserve original line breaks.\n\
- Preserve URLs, emails, file paths, code, variables, placeholders, formulas, and numbers.\n\
- Preserve markdown structure when the input is markdown.{custom}\n\n\
Text to translate:\n\
<<<EASYDICT_SOURCE_TEXT\n{}\nEASYDICT_SOURCE_TEXT",
        request.text
    )
}

pub fn build_grammar_correction_prompt(request: &WindowsAiGrammarCorrectionRequest) -> String {
    let system_prompt = if request.include_explanations {
        GRAMMAR_CORRECTION_SYSTEM_PROMPT_WITH_EXPLANATION
    } else {
        GRAMMAR_CORRECTION_SYSTEM_PROMPT
    };
    let user_prompt = if request.language == WindowsAiLanguage::Auto {
        format!(
            "Correct the grammar in the following text:\n\n{}",
            request.text
        )
    } else {
        let display_name = request.language.display_name();
        format!(
            "Correct the grammar in the following {display_name} text. The result MUST remain in {display_name}:\n\n{}",
            request.text
        )
    };

    format!("{system_prompt}\n\n{user_prompt}")
}

pub fn clean_model_output(text: &str) -> String {
    let mut output = text.trim().to_string();
    if output.is_empty() {
        return output;
    }

    const PREFIX: &str = "Translation:";
    if output
        .get(..PREFIX.len())
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case(PREFIX))
    {
        output = output[PREFIX.len()..].trim_start().to_string();
    }

    if output.len() >= 2 && output.starts_with('"') && output.ends_with('"') {
        output = output[1..output.len() - 1].trim().to_string();
    }

    output
}

pub fn ready_state_translation_error(
    state: WindowsAiReadyState,
) -> Option<WindowsAiTranslationError> {
    if state == WindowsAiReadyState::Ready {
        return None;
    }

    let kind = if state == WindowsAiReadyState::NotReady {
        WindowsAiTranslationErrorKind::LocalModelNeedsPreparation
    } else {
        WindowsAiTranslationErrorKind::ServiceUnavailable
    };
    Some(WindowsAiTranslationError::new(
        kind,
        ready_state_translation_error_message(state),
    ))
}

pub fn ready_state_translation_error_message(state: WindowsAiReadyState) -> String {
    match state {
        WindowsAiReadyState::CapabilityMissing => format!(
            "{SERVICE_NAME} is unavailable: the app package is missing the systemAIModels capability."
        ),
        WindowsAiReadyState::NotCompatibleWithSystemHardware => format!(
            "{SERVICE_NAME} requires a Copilot+ PC with a compatible NPU. Select Auto or OpenVINO in Windows Local AI settings to use the local fallback."
        ),
        WindowsAiReadyState::OsUpdateNeeded => {
            format!("{SERVICE_NAME} requires a newer Windows build. Update Windows and try again.")
        }
        WindowsAiReadyState::DisabledByUser => format!(
            "{SERVICE_NAME} has been disabled or removed. Re-enable Windows AI features in system settings."
        ),
        WindowsAiReadyState::UnsupportedWindowsAiBaseline => format!(
            "{SERVICE_NAME} is unavailable because this Windows installation does not appear to have a valid Windows AI baseline. This usually happens on unactivated, outdated, Insider, managed, or incomplete Copilot+ PC images where Windows Update cannot install AI Components. Activate Windows, install the latest cumulative update and AI Components, then verify Phi Silica in AI Dev Gallery."
        ),
        WindowsAiReadyState::NotSupportedOnCurrentSystem => format!(
            "{SERVICE_NAME} is not supported on the current system or region. Select Auto or OpenVINO in Windows Local AI settings to use the local fallback."
        ),
        WindowsAiReadyState::NotReady => {
            format!("{SERVICE_NAME} model is not ready. Start a translation again and choose Download to prepare it.")
        }
        WindowsAiReadyState::Ready => String::new(),
    }
}

pub fn validate_translation_request(
    request: &WindowsAiTranslationRequest,
) -> Result<(), WindowsAiTranslationError> {
    if request.text.trim().is_empty() {
        return Err(WindowsAiTranslationError::new(
            WindowsAiTranslationErrorKind::InvalidResponse,
            "Text cannot be empty",
        ));
    }

    if request.to_language == WindowsAiLanguage::Auto {
        return Err(WindowsAiTranslationError::new(
            WindowsAiTranslationErrorKind::UnsupportedLanguage,
            "Target language cannot be Auto",
        ));
    }

    Ok(())
}

pub fn validate_grammar_correction_request(
    request: &WindowsAiGrammarCorrectionRequest,
) -> Result<(), WindowsAiTranslationError> {
    if request.text.trim().is_empty() {
        return Err(WindowsAiTranslationError::new(
            WindowsAiTranslationErrorKind::InvalidResponse,
            "Text cannot be empty",
        ));
    }

    Ok(())
}

pub fn ensure_complete_response(
    response: &WindowsAiResponse,
) -> Result<(), WindowsAiTranslationError> {
    if response.status == WindowsAiResponseStatus::Complete {
        return Ok(());
    }

    let kind = match response.status {
        WindowsAiResponseStatus::PromptLargerThanContext => {
            WindowsAiTranslationErrorKind::TextTooLong
        }
        WindowsAiResponseStatus::BlockedByPolicy => {
            WindowsAiTranslationErrorKind::ServiceUnavailable
        }
        WindowsAiResponseStatus::Error | WindowsAiResponseStatus::Complete => {
            WindowsAiTranslationErrorKind::InvalidResponse
        }
    };
    let message = response
        .error_message
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|message| format!("{SERVICE_NAME}: {message}"))
        .unwrap_or_else(|| format!("{SERVICE_NAME} returned status {:?}.", response.status));

    Err(WindowsAiTranslationError::new(kind, message))
}

pub fn translate_with_client<C>(
    client: &mut C,
    request: &WindowsAiTranslationRequest,
) -> Result<WindowsAiTranslationOutcome, WindowsAiTranslationError>
where
    C: WindowsAiLanguageModelClient,
{
    validate_translation_request(request)?;
    ensure_ready_for_generation(client)?;
    let prompt = build_translation_prompt(request);
    let response = client
        .generate(&prompt, WindowsAiGenerationOptions::default())
        .map_err(map_client_error)?;
    ensure_complete_response(&response)?;

    Ok(WindowsAiTranslationOutcome {
        translated_text: clean_model_output(&response.text),
        detected_language: request.from_language,
        target_language: request.to_language,
        service_name: SERVICE_NAME,
    })
}

pub fn translate_stream_with_client<C>(
    client: &mut C,
    request: &WindowsAiTranslationRequest,
) -> Result<WindowsAiStreamOutcome, WindowsAiTranslationError>
where
    C: WindowsAiLanguageModelClient,
{
    validate_translation_request(request)?;
    ensure_ready_for_generation(client)?;
    let prompt = build_translation_prompt(request);
    let chunks = client
        .generate_stream(&prompt, WindowsAiGenerationOptions::default())
        .map_err(map_client_error)?
        .into_iter()
        .filter(|chunk| !chunk.is_empty())
        .collect::<Vec<_>>();
    let translated_text = clean_model_output(&chunks.concat());

    Ok(WindowsAiStreamOutcome {
        result: WindowsAiTranslationOutcome {
            translated_text,
            detected_language: request.from_language,
            target_language: request.to_language,
            service_name: SERVICE_NAME,
        },
        chunks,
    })
}

pub fn correct_grammar_stream_with_client<C>(
    client: &mut C,
    request: &WindowsAiGrammarCorrectionRequest,
) -> Result<Vec<String>, WindowsAiTranslationError>
where
    C: WindowsAiLanguageModelClient,
{
    validate_grammar_correction_request(request)?;
    ensure_ready_for_generation(client)?;
    let prompt = build_grammar_correction_prompt(request);
    client
        .generate_stream(&prompt, WindowsAiGenerationOptions::default())
        .map_err(map_client_error)
        .map(|chunks| {
            chunks
                .into_iter()
                .filter(|chunk| !chunk.is_empty())
                .collect()
        })
}

fn ensure_ready_for_generation<P>(probe: &mut P) -> Result<(), WindowsAiTranslationError>
where
    P: WindowsAiLanguageModelProbe,
{
    let status = windows_ai_status(probe);
    if let Some(error) = ready_state_translation_error(status.ready_state) {
        Err(error)
    } else {
        Ok(())
    }
}

fn map_client_error(error: WindowsAiError) -> WindowsAiTranslationError {
    WindowsAiTranslationError::new(
        WindowsAiTranslationErrorKind::Unknown,
        format!("{SERVICE_NAME} failed: {error}"),
    )
}

pub fn windows_ai_baseline_is_unsupported(fingerprint: &WindowsAiHealthFingerprint) -> bool {
    is_below_minimum_os_baseline(&fingerprint.os_build, fingerprint.ubr)
        || (fingerprint.windows_activated == Some(false)
            && fingerprint.phi_silica_ai_components_present == Some(false))
}

pub fn looks_like_unsupported_windows_ai_baseline(
    fingerprint: Option<&WindowsAiHealthFingerprint>,
    diagnostic_text: Option<&str>,
) -> bool {
    fingerprint.is_some_and(windows_ai_baseline_is_unsupported)
        || contains_windows_ai_baseline_marker(diagnostic_text)
}

pub fn is_below_minimum_os_baseline(os_build: &str, ubr: Option<u32>) -> bool {
    let Some(version) = parse_os_version(os_build, ubr) else {
        return false;
    };

    version < MINIMUM_SUPPORTED_OS_BASELINE
}

pub fn contains_windows_ai_baseline_marker(diagnostic_text: Option<&str>) -> bool {
    let Some(text) = diagnostic_text
        .map(str::trim)
        .filter(|text| !text.is_empty())
    else {
        return false;
    };
    let text = text.to_ascii_lowercase();
    [
        "windows ai baseline",
        "ai components",
        "ai component",
        "windows update",
        "delivery optimization",
        "osupdateneeded",
        "0x80070422",
        "activate windows",
        "unactivated",
    ]
    .iter()
    .any(|marker| text.contains(marker))
}

fn parse_os_version(os_build: &str, ubr: Option<u32>) -> Option<WindowsAiOsVersion> {
    let mut parts = os_build.trim().split('.');
    let major = parse_u16(parts.next()?)?;
    let minor = parse_u16(parts.next().unwrap_or("0"))?;
    let build = parse_u32(parts.next().unwrap_or("0"))?;
    let parsed_revision = parts.next().and_then(parse_u32).unwrap_or(0);
    let revision = ubr.unwrap_or(parsed_revision);

    Some(WindowsAiOsVersion {
        major,
        minor,
        build,
        revision,
    })
}

fn parse_u16(value: &str) -> Option<u16> {
    value.trim().parse().ok()
}

fn parse_u32(value: &str) -> Option<u32> {
    value.trim().parse().ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Mutex, MutexGuard, OnceLock};

    #[derive(Clone, Debug)]
    struct StaticProbe {
        ready_state: WindowsAiReadyState,
        fingerprint: Option<WindowsAiHealthFingerprint>,
    }

    impl WindowsAiLanguageModelProbe for StaticProbe {
        fn ready_state(&mut self) -> WindowsAiReadyState {
            self.ready_state
        }

        fn health_fingerprint(&mut self) -> Option<WindowsAiHealthFingerprint> {
            self.fingerprint.clone()
        }
    }

    #[derive(Clone, Debug)]
    struct PreparingProbe {
        ready_states: Vec<WindowsAiReadyState>,
        ensure_states: Vec<WindowsAiReadyState>,
        ready_calls: usize,
        ensure_calls: usize,
    }

    #[derive(Clone, Debug)]
    struct GeneratingClient {
        ready_state: WindowsAiReadyState,
        fingerprint: Option<WindowsAiHealthFingerprint>,
        ensure_state: Option<WindowsAiReadyState>,
        response: WindowsAiResponse,
        stream_chunks: Vec<String>,
        generate_error: Option<WindowsAiError>,
        stream_error: Option<WindowsAiError>,
        warm_up_error: Option<WindowsAiError>,
        ready_calls: usize,
        ensure_calls: usize,
        generate_calls: usize,
        stream_calls: usize,
        warm_up_calls: usize,
        last_prompt: Option<String>,
        last_options: Option<WindowsAiGenerationOptions>,
    }

    impl PreparingProbe {
        fn new(
            ready_states: impl IntoIterator<Item = WindowsAiReadyState>,
            ensure_states: impl IntoIterator<Item = WindowsAiReadyState>,
        ) -> Self {
            let mut ready_states = ready_states.into_iter().collect::<Vec<_>>();
            ready_states.reverse();
            let mut ensure_states = ensure_states.into_iter().collect::<Vec<_>>();
            ensure_states.reverse();
            Self {
                ready_states,
                ensure_states,
                ready_calls: 0,
                ensure_calls: 0,
            }
        }
    }

    impl WindowsAiLanguageModelProbe for PreparingProbe {
        fn ready_state(&mut self) -> WindowsAiReadyState {
            self.ready_calls += 1;
            self.ready_states
                .pop()
                .unwrap_or(WindowsAiReadyState::NotSupportedOnCurrentSystem)
        }

        fn ensure_ready(&mut self) -> Result<WindowsAiReadyState, WindowsAiError> {
            self.ensure_calls += 1;
            Ok(self
                .ensure_states
                .pop()
                .unwrap_or(WindowsAiReadyState::NotReady))
        }
    }

    impl GeneratingClient {
        fn new() -> Self {
            Self {
                ready_state: WindowsAiReadyState::Ready,
                fingerprint: None,
                ensure_state: None,
                response: WindowsAiResponse::complete("ok"),
                stream_chunks: Vec::new(),
                generate_error: None,
                stream_error: None,
                warm_up_error: None,
                ready_calls: 0,
                ensure_calls: 0,
                generate_calls: 0,
                stream_calls: 0,
                warm_up_calls: 0,
                last_prompt: None,
                last_options: None,
            }
        }
    }

    impl WindowsAiLanguageModelProbe for GeneratingClient {
        fn ready_state(&mut self) -> WindowsAiReadyState {
            self.ready_calls += 1;
            self.ready_state
        }

        fn health_fingerprint(&mut self) -> Option<WindowsAiHealthFingerprint> {
            self.fingerprint.clone()
        }

        fn ensure_ready(&mut self) -> Result<WindowsAiReadyState, WindowsAiError> {
            self.ensure_calls += 1;
            Ok(self.ensure_state.unwrap_or(self.ready_state))
        }
    }

    impl WindowsAiLanguageModelClient for GeneratingClient {
        fn generate(
            &mut self,
            prompt: &str,
            options: WindowsAiGenerationOptions,
        ) -> Result<WindowsAiResponse, WindowsAiError> {
            self.generate_calls += 1;
            self.last_prompt = Some(prompt.to_string());
            self.last_options = Some(options);
            if let Some(error) = self.generate_error.clone() {
                return Err(error);
            }
            Ok(self.response.clone())
        }

        fn generate_stream(
            &mut self,
            prompt: &str,
            options: WindowsAiGenerationOptions,
        ) -> Result<Vec<String>, WindowsAiError> {
            self.stream_calls += 1;
            self.last_prompt = Some(prompt.to_string());
            self.last_options = Some(options);
            if let Some(error) = self.stream_error.clone() {
                return Err(error);
            }
            Ok(self.stream_chunks.clone())
        }

        fn warm_up(
            &mut self,
            prompt: &str,
            options: WindowsAiGenerationOptions,
        ) -> Result<(), WindowsAiError> {
            self.warm_up_calls += 1;
            self.last_prompt = Some(prompt.to_string());
            self.last_options = Some(options);
            if let Some(error) = self.warm_up_error.clone() {
                return Err(error);
            }
            Ok(())
        }
    }

    fn fingerprint(os_build: &str, ubr: Option<u32>) -> WindowsAiHealthFingerprint {
        WindowsAiHealthFingerprint {
            os_build: os_build.to_string(),
            ubr,
            windows_app_sdk_version: "2.0.0".to_string(),
            process_architecture: "x64".to_string(),
            backend_name: "PhiSilica".to_string(),
            component_marker: "Microsoft.Windows.AI.Text".to_string(),
            windows_activated: Some(true),
            phi_silica_ai_components_present: Some(true),
        }
    }

    fn env_lock() -> MutexGuard<'static, ()> {
        static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
        LOCK.get_or_init(|| Mutex::new(()))
            .lock()
            .expect("WindowsAI env lock should not be poisoned")
    }

    struct EnvironmentVariableGuard {
        name: &'static str,
        original: Option<String>,
    }

    impl EnvironmentVariableGuard {
        fn set(name: &'static str, value: &Path) -> Self {
            let original = std::env::var(name).ok();
            std::env::set_var(name, value);
            Self { name, original }
        }

        fn remove(name: &'static str) -> Self {
            let original = std::env::var(name).ok();
            std::env::remove_var(name);
            Self { name, original }
        }
    }

    impl Drop for EnvironmentVariableGuard {
        fn drop(&mut self) {
            if let Some(value) = self.original.as_ref() {
                std::env::set_var(self.name, value);
            } else {
                std::env::remove_var(self.name);
            }
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

    fn create_winmd_set(metadata_dir: &Path) {
        std::fs::create_dir_all(metadata_dir).expect("metadata dir should be created");
        for file_name in [
            WINDOWS_AI_WINMD_FILE,
            WINDOWS_AI_FOUNDATION_WINMD_FILE,
            WINDOWS_AI_TEXT_WINMD_FILE,
        ] {
            std::fs::write(metadata_dir.join(file_name), b"test winmd")
                .expect("test WinMD should be written");
        }
    }

    fn create_incomplete_winmd_set(metadata_dir: &Path) {
        std::fs::create_dir_all(metadata_dir).expect("metadata dir should be created");
        std::fs::write(metadata_dir.join(WINDOWS_AI_WINMD_FILE), b"test winmd")
            .expect("Windows AI WinMD should be written");
        std::fs::write(metadata_dir.join(WINDOWS_AI_TEXT_WINMD_FILE), b"test winmd")
            .expect("Windows AI Text WinMD should be written");
    }

    #[test]
    fn baseline_uses_ubr_when_available() {
        assert!(is_below_minimum_os_baseline("10.0.26200", Some(7308)));
        assert!(!is_below_minimum_os_baseline("10.0.26200", Some(7309)));
        assert!(!is_below_minimum_os_baseline("10.0.26200", Some(7310)));
    }

    #[test]
    fn baseline_uses_version_revision_without_ubr() {
        assert!(is_below_minimum_os_baseline("10.0.26200.7308", None));
        assert!(!is_below_minimum_os_baseline("10.0.26200.7309", None));
        assert!(!is_below_minimum_os_baseline("10.0.26300.1", None));
    }

    #[test]
    fn malformed_os_version_is_not_classified_as_below_baseline() {
        assert!(!is_below_minimum_os_baseline("not-a-version", None));
    }

    #[test]
    fn winmd_set_requires_ai_foundation_and_text_metadata() {
        let temp_dir = unique_temp_dir("easydict-windows-ai-winmd-required-files");
        let metadata_dir = temp_dir.join("metadata");
        create_incomplete_winmd_set(&metadata_dir);

        assert!(WindowsAiWinmdSet::from_metadata_dir(&metadata_dir).is_none());

        std::fs::write(
            metadata_dir.join(WINDOWS_AI_FOUNDATION_WINMD_FILE),
            b"test winmd",
        )
        .expect("Windows AI Foundation WinMD should be written");
        let set = WindowsAiWinmdSet::from_metadata_dir(&metadata_dir)
            .expect("complete Windows AI metadata should be accepted");

        assert_eq!(set.windows_ai, metadata_dir.join(WINDOWS_AI_WINMD_FILE));
        assert_eq!(
            set.windows_ai_foundation,
            metadata_dir.join(WINDOWS_AI_FOUNDATION_WINMD_FILE)
        );
        assert_eq!(
            set.windows_ai_text,
            metadata_dir.join(WINDOWS_AI_TEXT_WINMD_FILE)
        );

        std::fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
    }

    #[test]
    fn winmd_locator_selects_latest_semantic_package_version() {
        let temp_dir = unique_temp_dir("easydict-windows-ai-winmd-latest");
        create_winmd_set(&temp_dir.join("1.8.70").join("metadata"));
        create_winmd_set(&temp_dir.join("2.0.9").join("metadata"));
        create_winmd_set(&temp_dir.join("2.0.185").join("metadata"));
        create_incomplete_winmd_set(&temp_dir.join("9.9.9").join("metadata"));

        let set = find_latest_windows_ai_winmd_set_under(&temp_dir)
            .expect("latest complete Windows AI metadata should be found");

        assert_eq!(set.metadata_dir, temp_dir.join("2.0.185").join("metadata"));

        std::fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
    }

    #[test]
    fn winmd_set_reports_package_version_from_metadata_parent() {
        let temp_dir = unique_temp_dir("easydict-windows-ai-winmd-version");
        let metadata_dir = temp_dir.join("1.8.250515001").join("metadata");
        create_winmd_set(&metadata_dir);
        let set = WindowsAiWinmdSet::from_metadata_dir(&metadata_dir).expect("winmd set");

        assert_eq!(
            windows_app_sdk_version_from_winmd_set(&set).as_deref(),
            Some("1.8.250515001")
        );

        std::fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
    }

    #[cfg(not(windows))]
    #[test]
    fn current_health_fingerprint_is_absent_off_windows() {
        assert_eq!(current_windows_ai_health_fingerprint(), None);
    }

    #[cfg(windows)]
    #[test]
    fn current_health_fingerprint_reports_windows_os_diagnostics() {
        let fingerprint = current_windows_ai_health_fingerprint()
            .expect("Windows should expose OS diagnostics through RtlGetVersion");

        assert!(fingerprint.os_build.starts_with("10."));
        assert!(fingerprint.ubr.is_some());
        assert_eq!(fingerprint.process_architecture, std::env::consts::ARCH);
        assert_eq!(fingerprint.backend_name, "WinRT");
        assert!(!fingerprint.component_marker.is_empty());
    }

    #[test]
    fn disabled_default_client_does_not_report_health_fingerprint() {
        let mut client = DefaultWindowsAiLanguageModelClient::disabled();

        assert_eq!(client.health_fingerprint(), None);
    }

    #[test]
    fn winmd_locator_prefers_explicit_metadata_env_over_nuget_root() {
        let _lock = env_lock();
        let temp_dir = unique_temp_dir("easydict-windows-ai-winmd-env");
        let env_metadata = temp_dir.join("override").join("metadata");
        let root_metadata = temp_dir.join("nuget").join("2.0.185").join("metadata");
        create_winmd_set(&env_metadata);
        create_winmd_set(&root_metadata);

        let _metadata_guard = EnvironmentVariableGuard::set(WINDOWS_AI_METADATA_ENV, &env_metadata);
        let _root_guard =
            EnvironmentVariableGuard::set(WINDOWS_AI_NUGET_ROOT_ENV, &temp_dir.join("nuget"));

        let set =
            find_windows_ai_winmd_set().expect("explicit Windows AI metadata should be found");

        assert_eq!(set.metadata_dir, env_metadata);

        std::fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
    }

    #[test]
    fn winmd_locator_uses_explicit_nuget_root_when_metadata_env_is_absent() {
        let _lock = env_lock();
        let temp_dir = unique_temp_dir("easydict-windows-ai-winmd-root-env");
        let metadata_dir = temp_dir.join("2.0.185").join("metadata");
        create_winmd_set(&metadata_dir);

        let _metadata_guard = EnvironmentVariableGuard::remove(WINDOWS_AI_METADATA_ENV);
        let _root_guard = EnvironmentVariableGuard::set(WINDOWS_AI_NUGET_ROOT_ENV, &temp_dir);

        let set = find_windows_ai_winmd_set()
            .expect("Windows AI metadata under explicit NuGet root should be found");

        assert_eq!(set.metadata_dir, metadata_dir);

        std::fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
    }

    #[test]
    fn bindgen_args_include_default_metadata_windows_ai_filters_and_output() {
        let metadata_dir = PathBuf::from(r"C:\NuGet\microsoft.windowsappsdk.ai\2.0.185\metadata");
        let set = WindowsAiWinmdSet {
            windows_ai: metadata_dir.join(WINDOWS_AI_WINMD_FILE),
            windows_ai_foundation: metadata_dir.join(WINDOWS_AI_FOUNDATION_WINMD_FILE),
            windows_ai_text: metadata_dir.join(WINDOWS_AI_TEXT_WINMD_FILE),
            metadata_dir: metadata_dir.clone(),
        };
        let output_file = PathBuf::from(r"C:\repo\lib\easydict-windows-ai\src\bindings.rs");

        let args = windows_ai_bindgen_args(&set, &output_file);

        assert_eq!(
            args,
            vec![
                "--in",
                "default",
                r"C:\NuGet\microsoft.windowsappsdk.ai\2.0.185\metadata",
                "--out",
                r"C:\repo\lib\easydict-windows-ai\src\bindings.rs",
                "--no-allow",
                "--filter",
                "Microsoft.Windows.AI.AIFeatureReadyResult",
                "Microsoft.Windows.AI.AIFeatureReadyResultState",
                "Microsoft.Windows.AI.AIFeatureReadyState",
                "Microsoft.Windows.AI.ContentSafety",
                "Microsoft.Windows.AI.Foundation",
                "Microsoft.Windows.AI.Text",
            ]
        );
    }

    #[test]
    fn inactive_windows_without_ai_components_is_unsupported_baseline() {
        let mut value = fingerprint("10.0.26200", Some(7310));
        value.windows_activated = Some(false);
        value.phi_silica_ai_components_present = Some(false);

        assert!(windows_ai_baseline_is_unsupported(&value));
    }

    #[test]
    fn diagnostic_markers_match_legacy_windows_ai_baseline_hints() {
        assert!(contains_windows_ai_baseline_marker(Some(
            "Delivery Optimization reports AI Components are unavailable"
        )));
        assert!(contains_windows_ai_baseline_marker(Some(
            "0x80070422: activate Windows first"
        )));
        assert!(!contains_windows_ai_baseline_marker(Some(
            "regular model runtime error"
        )));
    }

    #[test]
    fn status_mapping_matches_phi_silica_resource_keys() {
        assert_eq!(
            status_for_ready_state(WindowsAiReadyState::Ready).resource_key,
            STATUS_READY
        );
        assert_eq!(
            status_for_ready_state(WindowsAiReadyState::NotReady).state,
            WindowsAiModelState::NeedsPreparation
        );
        assert_eq!(
            status_for_ready_state(WindowsAiReadyState::CapabilityMissing).resource_key,
            STATUS_CAPABILITY_MISSING
        );
        assert_eq!(
            status_for_ready_state(WindowsAiReadyState::UnsupportedWindowsAiBaseline).resource_key,
            STATUS_UNSUPPORTED_WINDOWS_AI_BASELINE
        );
    }

    #[test]
    fn status_probe_applies_baseline_before_ready_state() {
        let mut probe = StaticProbe {
            ready_state: WindowsAiReadyState::Ready,
            fingerprint: Some(fingerprint("10.0.26200", Some(7308))),
        };

        let status = windows_ai_status(&mut probe);

        assert_eq!(
            status.ready_state,
            WindowsAiReadyState::UnsupportedWindowsAiBaseline
        );
        assert_eq!(status.state, WindowsAiModelState::NotCompatible);
    }

    #[test]
    fn ready_and_not_ready_are_provider_candidates() {
        assert!(WindowsAiReadyState::Ready.is_provider_candidate());
        assert!(WindowsAiReadyState::NotReady.is_provider_candidate());
        assert!(!WindowsAiReadyState::CapabilityMissing.is_provider_candidate());
        assert!(!WindowsAiReadyState::NotSupportedOnCurrentSystem.is_provider_candidate());
    }

    #[test]
    fn generation_options_match_phi_translation_defaults() {
        assert_eq!(
            WindowsAiGenerationOptions::default(),
            WindowsAiGenerationOptions {
                temperature: 0.1,
                top_k: 1,
                top_p: 0.9,
            }
        );
        assert_eq!(WARM_UP_PROMPT, "Reply with OK.");
    }

    #[test]
    fn build_translation_prompt_includes_source_target_and_custom_prompt() {
        let request = WindowsAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: WindowsAiLanguage::English,
            to_language: WindowsAiLanguage::SimplifiedChinese,
            custom_prompt: Some("Use formal register and prefer Quebec spellings.".to_string()),
        };

        let prompt = build_translation_prompt(&request);

        assert!(prompt.contains("English (en)"));
        assert!(prompt.contains("Simplified Chinese (zh-CN)"));
        assert!(prompt.contains("Additional user instruction"));
        assert!(prompt.contains("Quebec spellings"));
        assert!(prompt.contains("<<<EASYDICT_SOURCE_TEXT\nHello\nEASYDICT_SOURCE_TEXT"));
    }

    #[test]
    fn build_translation_prompt_uses_auto_detection_phrase_without_empty_custom_prompt() {
        let request = WindowsAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: WindowsAiLanguage::Auto,
            to_language: WindowsAiLanguage::Japanese,
            custom_prompt: Some("   ".to_string()),
        };

        let prompt = build_translation_prompt(&request);

        assert!(prompt.contains("auto-detected"));
        assert!(!prompt.contains("Additional user instruction"));
    }

    #[test]
    fn build_grammar_prompt_uses_shared_plain_text_explanation_format() {
        let request = WindowsAiGrammarCorrectionRequest {
            text: "He go to school.".to_string(),
            language: WindowsAiLanguage::English,
            include_explanations: true,
        };

        let prompt = build_grammar_correction_prompt(&request);

        assert!(prompt.contains("First output the fully corrected text"));
        assert!(prompt.contains("\"---\""));
        assert!(prompt.contains("NEVER put \"---\" before the corrected text"));
        assert!(prompt.contains("He go to school."));
        assert!(!prompt.contains("[CORRECTED]"));
        assert!(!prompt.contains("[EXPLANATION]"));
    }

    #[test]
    fn clean_model_output_strips_translation_prefix_and_wrapping_quotes() {
        assert_eq!(clean_model_output("Translation: \"你好\"\n"), "你好");
        assert_eq!(clean_model_output(" translation: done "), "done");
        assert_eq!(clean_model_output("   "), "");
    }

    #[test]
    fn translate_with_client_returns_cleaned_translation_and_prompt() {
        let mut client = GeneratingClient::new();
        client.response = WindowsAiResponse::complete("Translation: \"你好\"\n");
        let request = WindowsAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: WindowsAiLanguage::English,
            to_language: WindowsAiLanguage::SimplifiedChinese,
            custom_prompt: None,
        };

        let outcome = translate_with_client(&mut client, &request).expect("translation");

        assert_eq!(outcome.translated_text, "你好");
        assert_eq!(outcome.service_name, SERVICE_NAME);
        assert_eq!(
            outcome.target_language,
            WindowsAiLanguage::SimplifiedChinese
        );
        assert_eq!(client.ready_calls, 1);
        assert_eq!(client.generate_calls, 1);
        assert_eq!(
            client.last_options,
            Some(WindowsAiGenerationOptions::default())
        );
        assert!(client
            .last_prompt
            .as_deref()
            .is_some_and(|prompt| prompt.contains("Text to translate")));
    }

    #[test]
    fn translate_with_client_does_not_prepare_when_model_is_not_ready() {
        let mut client = GeneratingClient::new();
        client.ready_state = WindowsAiReadyState::NotReady;
        let request = WindowsAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: WindowsAiLanguage::English,
            to_language: WindowsAiLanguage::SimplifiedChinese,
            custom_prompt: None,
        };

        let error = translate_with_client(&mut client, &request).expect_err("not ready");

        assert_eq!(
            error.kind,
            WindowsAiTranslationErrorKind::LocalModelNeedsPreparation
        );
        assert_eq!(client.generate_calls, 0);
        assert_eq!(client.warm_up_calls, 0);
    }

    #[test]
    fn validate_translation_rejects_empty_text_and_target_auto() {
        let empty = WindowsAiTranslationRequest {
            text: "   ".to_string(),
            from_language: WindowsAiLanguage::English,
            to_language: WindowsAiLanguage::SimplifiedChinese,
            custom_prompt: None,
        };
        let target_auto = WindowsAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: WindowsAiLanguage::English,
            to_language: WindowsAiLanguage::Auto,
            custom_prompt: None,
        };

        assert_eq!(
            validate_translation_request(&empty)
                .expect_err("empty")
                .kind,
            WindowsAiTranslationErrorKind::InvalidResponse
        );
        assert_eq!(
            validate_translation_request(&target_auto)
                .expect_err("target auto")
                .kind,
            WindowsAiTranslationErrorKind::UnsupportedLanguage
        );
    }

    #[test]
    fn response_status_maps_context_and_policy_errors() {
        let context = WindowsAiResponse {
            status: WindowsAiResponseStatus::PromptLargerThanContext,
            text: String::new(),
            error_message: Some("context overflow".to_string()),
        };
        let blocked = WindowsAiResponse {
            status: WindowsAiResponseStatus::BlockedByPolicy,
            text: String::new(),
            error_message: Some("blocked".to_string()),
        };

        assert_eq!(
            ensure_complete_response(&context)
                .expect_err("context")
                .kind,
            WindowsAiTranslationErrorKind::TextTooLong
        );
        assert_eq!(
            ensure_complete_response(&blocked)
                .expect_err("blocked")
                .kind,
            WindowsAiTranslationErrorKind::ServiceUnavailable
        );
    }

    #[test]
    fn translate_stream_filters_empty_chunks_but_preserves_space_tokens() {
        let mut client = GeneratingClient::new();
        client.stream_chunks = vec![
            "Hello".to_string(),
            String::new(),
            " ".to_string(),
            "world".to_string(),
        ];
        let request = WindowsAiTranslationRequest {
            text: "你好".to_string(),
            from_language: WindowsAiLanguage::Auto,
            to_language: WindowsAiLanguage::English,
            custom_prompt: None,
        };

        let outcome = translate_stream_with_client(&mut client, &request).expect("stream");

        assert_eq!(outcome.chunks, ["Hello", " ", "world"]);
        assert_eq!(outcome.result.translated_text, "Hello world");
        assert_eq!(client.stream_calls, 1);
    }

    #[test]
    fn correct_grammar_stream_uses_grammar_prompt_and_filters_empty_chunks() {
        let mut client = GeneratingClient::new();
        client.stream_chunks = vec!["He goes.".to_string(), String::new()];
        let request = WindowsAiGrammarCorrectionRequest {
            text: "He go.".to_string(),
            language: WindowsAiLanguage::English,
            include_explanations: false,
        };

        let chunks = correct_grammar_stream_with_client(&mut client, &request).expect("grammar");

        assert_eq!(chunks, ["He goes."]);
        assert!(client
            .last_prompt
            .as_deref()
            .is_some_and(|prompt| prompt.contains("Correct the grammar")));
    }

    #[test]
    fn prepare_returns_ready_without_ensure_when_already_ready() {
        let mut probe = PreparingProbe::new([WindowsAiReadyState::Ready], []);

        let status = prepare_windows_ai_model(&mut probe).expect("prepare status");

        assert_eq!(status.state, WindowsAiModelState::Ready);
        assert_eq!(probe.ready_calls, 1);
        assert_eq!(probe.ensure_calls, 0);
    }

    #[test]
    fn prepare_calls_ensure_when_model_is_not_ready() {
        let mut probe = PreparingProbe::new(
            [WindowsAiReadyState::NotReady],
            [WindowsAiReadyState::Ready],
        );

        let status = prepare_windows_ai_model(&mut probe).expect("prepare status");

        assert_eq!(status.state, WindowsAiModelState::Ready);
        assert_eq!(probe.ready_calls, 1);
        assert_eq!(probe.ensure_calls, 1);
    }

    #[test]
    fn prepare_client_runs_warmup_before_reporting_ready() {
        let mut client = GeneratingClient::new();

        let status = prepare_windows_ai_client(&mut client).expect("client prepare");

        assert_eq!(status.state, WindowsAiModelState::Ready);
        assert_eq!(client.ready_calls, 1);
        assert_eq!(client.ensure_calls, 0);
        assert_eq!(client.warm_up_calls, 1);
        assert_eq!(client.last_prompt.as_deref(), Some(WARM_UP_PROMPT));
        assert_eq!(
            client.last_options,
            Some(WindowsAiGenerationOptions::default())
        );
    }

    #[test]
    fn prepare_client_not_ready_calls_ensure_then_warmup() {
        let mut client = GeneratingClient::new();
        client.ready_state = WindowsAiReadyState::NotReady;
        client.ensure_state = Some(WindowsAiReadyState::Ready);

        let status = prepare_windows_ai_client(&mut client).expect("client prepare");

        assert_eq!(status.state, WindowsAiModelState::Ready);
        assert_eq!(client.ready_calls, 1);
        assert_eq!(client.ensure_calls, 1);
        assert_eq!(client.warm_up_calls, 1);
    }

    #[test]
    fn prepare_client_warmup_failure_reports_runtime_unhealthy() {
        let mut client = GeneratingClient::new();
        client.fingerprint = Some(fingerprint("10.0.26200", Some(7310)));
        client.warm_up_error = Some(WindowsAiError::new(
            "Windows AI runtime failed while running Phi Silica: operation=warmup; hResult=0x80004005",
        ));

        let status = prepare_windows_ai_client(&mut client).expect("client prepare");

        assert_eq!(status.state, WindowsAiModelState::Failed);
        assert_eq!(status.resource_key, STATUS_RUNTIME_UNHEALTHY);
        assert!(status.message.contains("operation=warmup"));
        assert!(status.message.contains("osBuild=10.0.26200"));
        assert_eq!(client.warm_up_calls, 1);
    }

    #[test]
    fn runtime_failure_status_can_report_unsupported_baseline() {
        let status = runtime_unhealthy_status(
            "first warmup failed",
            Some(fingerprint("10.0.26200", Some(7308))),
        );

        assert_eq!(status.state, WindowsAiModelState::Failed);
        assert_eq!(status.resource_key, STATUS_UNSUPPORTED_WINDOWS_AI_BASELINE);
        assert_eq!(
            status.ready_state,
            WindowsAiReadyState::UnsupportedWindowsAiBaseline
        );
    }

    #[test]
    fn prepare_reports_failure_when_ensure_keeps_model_not_ready() {
        let mut probe = PreparingProbe::new(
            [WindowsAiReadyState::NotReady],
            [WindowsAiReadyState::NotReady],
        );

        let status = prepare_windows_ai_model(&mut probe).expect("prepare status");

        assert_eq!(status.state, WindowsAiModelState::Failed);
        assert_eq!(status.resource_key, STATUS_PREPARE_FAILED);
        assert_eq!(probe.ensure_calls, 1);
    }

    #[test]
    fn prepare_does_not_call_ensure_for_incompatible_state() {
        let mut probe = PreparingProbe::new([WindowsAiReadyState::CapabilityMissing], []);

        let status = prepare_windows_ai_model(&mut probe).expect("prepare status");

        assert_eq!(status.state, WindowsAiModelState::NotCompatible);
        assert_eq!(status.resource_key, STATUS_CAPABILITY_MISSING);
        assert_eq!(probe.ensure_calls, 0);
    }
}
