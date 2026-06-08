use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};

pub mod local_ai_provider_modes {
    pub const WINDOWS_AI: &str = "WindowsAI";
    pub const FOUNDRY_LOCAL: &str = "FoundryLocal";
    pub const OPENVINO: &str = "OpenVINO";
    pub const AUTO: &str = "Auto";
}

pub fn normalize_local_ai_provider_mode(value: Option<&str>) -> &'static str {
    let normalized = value
        .unwrap_or(local_ai_provider_modes::AUTO)
        .trim()
        .to_ascii_lowercase()
        .replace(['-', '_', ' '], "");
    match normalized.as_str() {
        "" | "auto" => local_ai_provider_modes::AUTO,
        "windowsai" | "phi" | "phisilica" => local_ai_provider_modes::WINDOWS_AI,
        "foundry" | "foundrylocal" | "localai" => local_ai_provider_modes::FOUNDRY_LOCAL,
        "openvino" => local_ai_provider_modes::OPENVINO,
        _ => local_ai_provider_modes::AUTO,
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
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_prompt: Option<String>,
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
    /// Secondary translation candidates (e.g. Linguee alternatives), preserved
    /// from the legacy `TranslationResult.Alternatives`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub alternatives: Option<Vec<String>>,
    /// Dictionary/word lookup payload for rich providers such as Google Dict
    /// and Youdao. This is additive so older hosts that omit it still parse.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub word_result: Option<WordResultDto>,
    /// Rich HTML payload for dictionary-style providers. Plain text remains in
    /// `translatedText`; renderers may use this when they can display HTML.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub raw_html: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WordResultDto {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub phonetics: Option<Vec<PhoneticDto>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub definitions: Option<Vec<DefinitionDto>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub examples: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub word_forms: Option<Vec<WordFormDto>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub synonyms: Option<Vec<SynonymDto>>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PhoneticDto {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub audio_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub accent: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DefinitionDto {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub part_of_speech: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub meanings: Option<Vec<String>>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WordFormDto {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub value: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SynonymDto {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub part_of_speech: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub meaning: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub words: Option<Vec<String>>,
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
    pub enable_translation_cache: Option<bool>,
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

fn default_true() -> bool {
    true
}

pub fn serialize_json<T: Serialize>(value: &T) -> serde_json::Result<String> {
    serde_json::to_string(value)
}
pub fn deserialize_json<T: DeserializeOwned>(json: &str) -> serde_json::Result<T> {
    serde_json::from_str(json)
}
