use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::Value;

pub const WORKER_PROTOCOL_VERSION_CURRENT: u32 = 1;

pub mod compat_methods {
    pub const TRANSLATE: &str = "translate";
    pub const TRANSLATE_STREAM: &str = "translate_stream";
    pub const GRAMMAR_CORRECT: &str = "grammar_correct";
    pub const OCR_RECOGNIZE: &str = "ocr_recognize";
    pub const LONGDOC_TRANSLATE: &str = "longdoc_translate";
    pub const LOCAL_AI_PREPARE: &str = "local_ai_prepare";
    pub const LOCAL_AI_TRANSLATE: &str = "local_ai_translate";
    pub const MDX_LOOKUP: &str = "mdx_lookup";
    pub const SETTINGS_MIGRATE: &str = "settings_migrate";
}

pub mod compat_events {
    pub const TRANSLATE_CHUNK: &str = "translate_chunk";
    pub const TRANSLATE_DONE: &str = "translate_done";
    pub const GRAMMAR_CHUNK: &str = "grammar_chunk";
    pub const GRAMMAR_DONE: &str = "grammar_done";
}

pub mod worker_methods {
    pub const CONFIGURE: &str = "configure";
    pub const CANCEL: &str = "cancel";
    pub const SHUTDOWN: &str = "shutdown";
    pub const LONGDOC_TRANSLATE_DOCUMENT: &str = "translate_document";
    pub const LOCAL_AI_TRANSLATE: &str = "translate";
    pub const LOCAL_AI_TRANSLATE_STREAM: &str = "translate_stream";
    pub const LOCAL_AI_PREPARE_MODEL: &str = "prepare_model";
    pub const LOCAL_AI_IS_AVAILABLE: &str = "is_available";
    pub const LOCAL_AI_LIST_MODELS: &str = "list_models";
    pub const LOCAL_AI_GRAMMAR_STREAM: &str = "grammar_stream";
    pub const OCR_RECOGNIZE: &str = "recognize";
}

pub mod worker_events {
    pub const READY: &str = "ready";
    pub const LONGDOC_STATUS: &str = "status";
    pub const LONGDOC_PROGRESS: &str = "progress";
    pub const LONGDOC_BLOCK_TRANSLATED: &str = "block_translated";
    pub const LOCAL_AI_CHUNK: &str = "chunk";
    pub const LOCAL_AI_DOWNLOAD_PROGRESS: &str = "download_progress";
}

pub mod worker_kinds {
    pub const LONGDOC: &str = "longdoc";
    pub const LOCAL_AI: &str = "localai";
    pub const OCR: &str = "ocr";
}

pub mod worker_error_codes {
    pub const CANCELLED: &str = "cancelled";
    pub const MODEL_MISSING: &str = "model_missing";
    pub const INVALID_PARAMS: &str = "invalid_params";
    pub const SERVICE_ERROR: &str = "service_error";
    pub const INTERNAL: &str = "internal_error";
    pub const VERSION_MISMATCH: &str = "version_mismatch";
}

pub mod ipc_error_codes {
    pub const INVALID_JSON: &str = "invalid_json";
    pub const METHOD_NOT_FOUND: &str = "method_not_found";
    pub const INVALID_PARAMS: &str = "invalid_params";
    pub const INTERNAL_ERROR: &str = "internal_error";
    pub const SERVICE_ERROR: &str = "service_error";
}

pub mod local_ai_provider_modes {
    pub const WINDOWS_AI: &str = "WindowsAI";
    pub const FOUNDRY_LOCAL: &str = "FoundryLocal";
    pub const OPENVINO: &str = "OpenVINO";
    pub const AUTO: &str = "Auto";
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IpcRequest<P = Value> {
    pub id: String,
    pub method: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub params: Option<P>,
}

impl<P> IpcRequest<P> {
    pub fn new(id: impl Into<String>, method: impl Into<String>, params: P) -> Self {
        Self {
            id: id.into(),
            method: method.into(),
            params: Some(params),
        }
    }

    pub fn without_params(id: impl Into<String>, method: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            method: method.into(),
            params: None,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IpcResponse<R = Value> {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<R>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
}

impl<R> IpcResponse<R> {
    pub fn ok(id: impl Into<String>, result: R) -> Self {
        Self {
            id: Some(id.into()),
            result: Some(result),
            error: None,
        }
    }

    pub fn error(id: impl Into<String>, error: IpcError) -> Self {
        Self {
            id: Some(id.into()),
            result: None,
            error: Some(error),
        }
    }

    pub fn is_success(&self) -> bool {
        self.error.is_none() && self.result.is_some()
    }

    pub fn is_error(&self) -> bool {
        self.error.is_some()
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IpcError {
    pub code: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub details: Option<Value>,
}

impl IpcError {
    pub fn new(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
            details: None,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IpcEvent<D = Value> {
    pub event: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub data: Option<D>,
}

impl<D> IpcEvent<D> {
    pub fn new(event: impl Into<String>, data: D) -> Self {
        Self {
            event: event.into(),
            id: None,
            data: Some(data),
        }
    }

    pub fn for_request(id: impl Into<String>, event: impl Into<String>, data: D) -> Self {
        Self {
            event: event.into(),
            id: Some(id.into()),
            data: Some(data),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IpcMessage {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub event: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub data: Option<Value>,
}

impl IpcMessage {
    pub fn is_event(&self) -> bool {
        self.event.is_some()
    }

    pub fn is_response(&self) -> bool {
        self.id.is_some() && self.event.is_none()
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslateParams {
    pub text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub from: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub to: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub services: Option<Vec<String>>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslationResultDto {
    pub translated_text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub service_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub service_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detected_language: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_kind: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub info_message: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub timing_ms: Option<i64>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslateChunkEventData {
    pub text: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GrammarCorrectParams {
    pub text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub language: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub services: Option<Vec<String>>,
    #[serde(default = "default_true")]
    pub include_explanations: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GrammarCorrectResultDto {
    pub original_text: String,
    pub corrected_text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub explanation: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub raw_text: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub service_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub service_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub language: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub timing_ms: Option<i64>,
    #[serde(default)]
    pub has_corrections: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GrammarChunkEventData {
    pub text: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReadyEventData {
    pub worker_kind: String,
    pub worker_version: String,
    pub protocol_version: u32,
    pub capabilities: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigureParams {
    pub settings: SettingsSnapshot,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigureResult {
    pub ok: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CancelRequestParams {
    pub target_request_id: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CancelRequestResult {
    pub cancelled: bool,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsSnapshot {
    #[serde(skip_serializing_if = "Option::is_none", rename = "openAIApiKey")]
    pub open_ai_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "openAIEndpoint")]
    pub open_ai_endpoint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "openAIModel")]
    pub open_ai_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "openAITemperature")]
    pub open_ai_temperature: Option<f32>,
    #[serde(
        skip_serializing_if = "Option::is_none",
        rename = "openAIApiFormatOverride"
    )]
    pub open_ai_api_format_override: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "deepLApiKey")]
    pub deep_l_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "deepLUseFreeApi")]
    pub deep_l_use_free_api: Option<bool>,
    #[serde(
        skip_serializing_if = "Option::is_none",
        rename = "deepLUseQualityOptimized"
    )]
    pub deep_l_use_quality_optimized: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "deepSeekApiKey")]
    pub deep_seek_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "deepSeekModel")]
    pub deep_seek_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "geminiApiKey")]
    pub gemini_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "geminiModel")]
    pub gemini_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "groqApiKey")]
    pub groq_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "groqModel")]
    pub groq_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "zhipuApiKey")]
    pub zhipu_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "zhipuModel")]
    pub zhipu_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "doubaoApiKey")]
    pub doubao_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "doubaoEndpoint")]
    pub doubao_endpoint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "doubaoModel")]
    pub doubao_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "githubModelsApiKey")]
    pub github_models_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "githubModelsModel")]
    pub github_models_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub caiyun_token: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "niuTransApiKey")]
    pub niu_trans_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "youdaoAppKey")]
    pub youdao_app_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "youdaoAppSecret")]
    pub youdao_app_secret: Option<String>,
    #[serde(
        skip_serializing_if = "Option::is_none",
        rename = "youdaoUseOfficialApi"
    )]
    pub youdao_use_official_api: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "volcanoAccessKeyId")]
    pub volcano_access_key_id: Option<String>,
    #[serde(
        skip_serializing_if = "Option::is_none",
        rename = "volcanoSecretAccessKey"
    )]
    pub volcano_secret_access_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "customOpenAIApiKey")]
    pub custom_open_ai_api_key: Option<String>,
    #[serde(
        skip_serializing_if = "Option::is_none",
        rename = "customOpenAIEndpoint"
    )]
    pub custom_open_ai_endpoint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "customOpenAIModel")]
    pub custom_open_ai_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ollama_endpoint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ollama_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "builtInAIModel")]
    pub built_in_ai_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "builtInAIApiKey")]
    pub built_in_ai_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub device_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub device_token: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub foundry_local_endpoint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub foundry_local_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "openVinoDevice")]
    pub open_vino_device: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", rename = "localAIProvider")]
    pub local_ai_provider: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ocr_engine: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ocr_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ocr_endpoint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ocr_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ocr_system_prompt: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ocr_language: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub proxy_enabled: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub proxy_uri: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub proxy_bypass_local: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub long_doc_max_concurrency: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub long_doc_enable_document_context_pass: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enable_tatr_table_structure: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub formula_font_pattern: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub formula_char_pattern: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub long_doc_custom_prompt: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub layout_detection_mode: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enable_international_services: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub imported_mdx_dictionaries: Option<Vec<ImportedMdxDictionarySnapshot>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub doc_layout_yolo_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tatr_model_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cjk_font_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cache_dir: Option<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportedMdxDictionarySnapshot {
    #[serde(default)]
    pub service_id: String,
    #[serde(default)]
    pub display_name: String,
    #[serde(default)]
    pub file_path: String,
    #[serde(default)]
    pub is_encrypted: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub regcode: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub email: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub mdd_file_paths: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslateDocumentParams {
    pub input_path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output_path: Option<String>,
    pub input_mode: String,
    pub from: String,
    pub to: String,
    pub service_id: String,
    pub output_mode: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pdf_export_mode: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub layout_detection: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page_range: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub vision_endpoint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub vision_api_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub vision_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_json_path: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslateDocumentResult {
    pub state: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bilingual_output_path: Option<String>,
    pub total_chunks: u32,
    pub succeeded_chunks: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub failed_chunk_indexes: Option<Vec<u32>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub quality_report: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result_json_path: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BlockTranslatedEventData {
    pub chunk_index: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page_number: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_block_id: Option<String>,
    pub translated_text: String,
    pub retry_count: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_error: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusEventData {
    pub message: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProgressEventData {
    pub stage: String,
    pub current_block: u32,
    pub total_blocks: u32,
    pub current_page: u32,
    pub total_pages: u32,
    pub percentage: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_block_preview: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalAiTranslateParams {
    pub text: String,
    pub from_language: String,
    pub to_language: String,
    pub provider_mode: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_prompt: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalAiTranslateResult {
    pub translated_text: String,
    pub service_id: String,
    pub service_name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detected_language: Option<String>,
    pub timing_ms: i64,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChunkEventData {
    pub text: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslateStreamResult {
    pub done: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub full_text: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrepareModelParams {
    pub provider: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub endpoint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalModelStatusDto {
    pub state: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub status_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadProgressEventData {
    pub bytes_downloaded: i64,
    pub total_bytes: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_file: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IsAvailableParams {
    pub provider: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IsAvailableResult {
    pub available: bool,
    pub state: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ListModelsParams {
    pub provider: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ListModelsResult {
    pub models: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OcrRecognizeParams {
    pub pixel_data_path: String,
    pub pixel_width: u32,
    pub pixel_height: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub preferred_language_tag: Option<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OcrResultDto {
    #[serde(default)]
    pub text: String,
    #[serde(default)]
    pub lines: Vec<OcrLineDto>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detected_language: Option<OcrLanguageDto>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub text_angle: Option<f64>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OcrLineDto {
    #[serde(default)]
    pub text: String,
    pub bounding_rect: OcrRectDto,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OcrRectDto {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OcrLanguageDto {
    #[serde(default)]
    pub tag: String,
    #[serde(default)]
    pub display_name: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MdxLookupParams {
    pub dictionary_id: String,
    pub query: String,
    #[serde(default)]
    pub fuzzy: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MdxLookupResult {
    pub entries: Vec<MdxLookupEntry>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MdxLookupEntry {
    pub key: String,
    pub html: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dictionary_name: Option<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsMigrateParams {
    #[serde(default)]
    pub legacy_settings_path: Option<String>,
    #[serde(default)]
    pub target_settings_path: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsMigrateResult {
    pub migrated: bool,
    #[serde(default)]
    pub warnings: Vec<String>,
}

fn default_true() -> bool {
    true
}

pub fn serialize_json<T: Serialize>(value: &T) -> serde_json::Result<String> {
    serde_json::to_string(value)
}

pub fn serialize_json_line<T: Serialize>(value: &T) -> serde_json::Result<String> {
    serialize_json(value)
}

pub fn serialize_json_line_with_newline<T: Serialize>(value: &T) -> serde_json::Result<String> {
    serialize_json(value).map(|mut value| {
        value.push('\n');
        value
    })
}

pub fn deserialize_json<T: DeserializeOwned>(json: &str) -> serde_json::Result<T> {
    serde_json::from_str(json)
}

pub fn deserialize_json_line<T: DeserializeOwned>(line: &str) -> serde_json::Result<T> {
    deserialize_json(line)
}
