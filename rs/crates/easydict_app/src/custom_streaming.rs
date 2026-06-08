use crate::grammar_correction::{
    build_grammar_correction_user_prompt, grammar_correction_system_prompt,
    parse_grammar_correction, GrammarCorrectionResult,
};
use crate::openai_compatible::{
    cleanup_openai_translation_text, OpenAiExecutionError, OpenAiExecutionErrorCode,
    OpenAiTranslationRequest, OPENAI_DEFAULT_TEMPERATURE, OPENAI_TRANSLATION_SYSTEM_PROMPT,
};
use crate::protocol::{GrammarCorrectResultDto, SettingsSnapshot, TranslationResultDto};
use crate::translation_language::TranslationLanguage;
use serde_json::{json, Value};
use std::time::Duration;

pub const GEMINI_API_BASE_URL: &str = "https://generativelanguage.googleapis.com/v1beta";
pub const GEMINI_DEFAULT_MODEL: &str = "gemini-2.5-flash";
pub const DOUBAO_DEFAULT_ENDPOINT: &str = "https://ark.cn-beijing.volces.com/api/v3/responses";
pub const DOUBAO_DEFAULT_MODEL: &str = "doubao-seed-translation-250915";

const GEMINI_SUPPORTED_LANGUAGES: [TranslationLanguage; 32] = [
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

const DOUBAO_SUPPORTED_LANGUAGES: [TranslationLanguage; 20] = [
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
    TranslationLanguage::Turkish,
    TranslationLanguage::Swedish,
    TranslationLanguage::Indonesian,
    TranslationLanguage::Vietnamese,
    TranslationLanguage::Thai,
    TranslationLanguage::Hindi,
];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CustomStreamingFormat {
    Gemini,
    Doubao,
}

#[derive(Clone, Debug, PartialEq)]
pub struct CustomStreamingHttpRequestPlan {
    pub method: &'static str,
    pub endpoint: String,
    pub headers: Vec<(String, String)>,
    pub body: Value,
    pub streaming_format: CustomStreamingFormat,
}

#[derive(Clone, Debug, PartialEq)]
pub struct GeminiConfig {
    pub api_key: String,
    pub model: String,
    pub temperature: f64,
}

impl GeminiConfig {
    pub fn new(api_key: impl Into<String>, model: Option<&str>) -> Self {
        Self {
            api_key: api_key.into(),
            model: model.unwrap_or(GEMINI_DEFAULT_MODEL).to_string(),
            temperature: OPENAI_DEFAULT_TEMPERATURE,
        }
    }

    pub fn with_temperature(mut self, temperature: f64) -> Self {
        self.temperature = temperature.clamp(0.0, 2.0);
        self
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct DoubaoConfig {
    pub endpoint: String,
    pub api_key: String,
    pub model: String,
}

impl DoubaoConfig {
    pub fn new(api_key: impl Into<String>, endpoint: Option<&str>, model: Option<&str>) -> Self {
        Self {
            endpoint: endpoint.unwrap_or(DOUBAO_DEFAULT_ENDPOINT).to_string(),
            api_key: api_key.into(),
            model: model.unwrap_or(DOUBAO_DEFAULT_MODEL).to_string(),
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub enum CustomStreamingServiceConfig {
    Gemini(GeminiConfig),
    Doubao(DoubaoConfig),
}

pub trait CustomStreamingHttpClient {
    fn post_sse(
        &mut self,
        request: &CustomStreamingHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError>;
}

pub struct ReqwestCustomStreamingHttpClient {
    client: reqwest::blocking::Client,
}

impl ReqwestCustomStreamingHttpClient {
    pub fn from_settings(settings: &SettingsSnapshot) -> Result<Self, OpenAiExecutionError> {
        let mut builder = reqwest::blocking::Client::builder().timeout(Duration::from_secs(120));

        if settings.proxy_enabled.unwrap_or(false) {
            if let Some(proxy_uri) = normalized_optional(settings.proxy_uri.as_deref()) {
                let proxy = if settings.proxy_bypass_local.unwrap_or(false) {
                    let proxy_url = reqwest::Url::parse(&proxy_uri).map_err(|error| {
                        OpenAiExecutionError::new(
                            OpenAiExecutionErrorCode::InvalidResponse,
                            format!("Invalid custom streaming proxy URI '{proxy_uri}': {error}"),
                        )
                    })?;
                    reqwest::Proxy::custom(move |url| {
                        (!is_loopback_url(url)).then(|| proxy_url.clone())
                    })
                } else {
                    reqwest::Proxy::all(&proxy_uri).map_err(|error| {
                        OpenAiExecutionError::new(
                            OpenAiExecutionErrorCode::InvalidResponse,
                            format!("Invalid custom streaming proxy URI '{proxy_uri}': {error}"),
                        )
                    })?
                };
                builder = builder.proxy(proxy);
            }
        }

        let client = builder.build().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not create custom streaming HTTP client: {error}"),
            )
        })?;
        Ok(Self { client })
    }
}

impl CustomStreamingHttpClient for ReqwestCustomStreamingHttpClient {
    fn post_sse(
        &mut self,
        request: &CustomStreamingHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        let mut builder = self.client.post(&request.endpoint).json(&request.body);
        for (name, value) in &request.headers {
            builder = builder.header(name, value);
        }

        let response = builder.send().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Custom streaming HTTP request failed: {error}"),
            )
        })?;
        let status = response.status();
        let body = response.text().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not read custom streaming HTTP response: {error}"),
            )
        })?;

        if !status.is_success() {
            return Err(custom_streaming_error_from_response(
                status.as_u16(),
                status.canonical_reason().unwrap_or("Unknown"),
                &body,
            ));
        }

        Ok(body)
    }
}

pub fn gemini_service_config(api_key: impl Into<String>, model: Option<&str>) -> GeminiConfig {
    GeminiConfig::new(api_key, model)
}

pub fn doubao_service_config(
    api_key: impl Into<String>,
    endpoint: Option<&str>,
    model: Option<&str>,
) -> DoubaoConfig {
    DoubaoConfig::new(api_key, endpoint, model)
}

pub fn custom_streaming_config_for_service(
    service_id: &str,
    settings: &SettingsSnapshot,
) -> Option<CustomStreamingServiceConfig> {
    match service_id {
        "gemini" => Some(CustomStreamingServiceConfig::Gemini(gemini_service_config(
            settings.gemini_api_key.clone().unwrap_or_default(),
            settings.gemini_model.as_deref(),
        ))),
        "doubao" => Some(CustomStreamingServiceConfig::Doubao(doubao_service_config(
            settings.doubao_api_key.clone().unwrap_or_default(),
            settings.doubao_endpoint.as_deref(),
            settings.doubao_model.as_deref(),
        ))),
        _ => None,
    }
}

pub fn build_custom_streaming_translation_request_plan(
    config: &CustomStreamingServiceConfig,
    request: &OpenAiTranslationRequest,
) -> Result<CustomStreamingHttpRequestPlan, OpenAiExecutionError> {
    match config {
        CustomStreamingServiceConfig::Gemini(config) => {
            build_gemini_translation_request_plan(config, request)
        }
        CustomStreamingServiceConfig::Doubao(config) => {
            build_doubao_translation_request_plan(config, request)
        }
    }
}

pub fn custom_streaming_supports_language_pair(
    config: &CustomStreamingServiceConfig,
    from: TranslationLanguage,
    to: TranslationLanguage,
) -> bool {
    let supported_languages = custom_streaming_supported_languages(config);
    if from == TranslationLanguage::Auto {
        return supported_languages.contains(&to);
    }

    supported_languages.contains(&from) && supported_languages.contains(&to)
}

pub fn validate_custom_streaming_translation_request(
    config: &CustomStreamingServiceConfig,
    request: &OpenAiTranslationRequest,
) -> Result<(), OpenAiExecutionError> {
    validate_custom_streaming_language_pair(custom_streaming_supported_languages(config), request)
}

fn custom_streaming_supported_languages(
    config: &CustomStreamingServiceConfig,
) -> &'static [TranslationLanguage] {
    match config {
        CustomStreamingServiceConfig::Gemini(_) => &GEMINI_SUPPORTED_LANGUAGES,
        CustomStreamingServiceConfig::Doubao(_) => &DOUBAO_SUPPORTED_LANGUAGES,
    }
}

fn validate_custom_streaming_language_pair(
    supported_languages: &'static [TranslationLanguage],
    request: &OpenAiTranslationRequest,
) -> Result<(), OpenAiExecutionError> {
    let supports_language_pair = if request.from_language == TranslationLanguage::Auto {
        supported_languages.contains(&request.to_language)
    } else {
        supported_languages.contains(&request.from_language)
            && supported_languages.contains(&request.to_language)
    };
    if !supports_language_pair {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::UnsupportedLanguage,
            format!(
                "Language pair not supported: {:?} -> {:?}",
                request.from_language, request.to_language
            ),
        ));
    }

    Ok(())
}

pub fn build_custom_streaming_grammar_request_plan(
    config: &CustomStreamingServiceConfig,
    language: TranslationLanguage,
    text: &str,
    include_explanations: bool,
) -> Result<CustomStreamingHttpRequestPlan, OpenAiExecutionError> {
    match config {
        CustomStreamingServiceConfig::Gemini(config) => {
            build_gemini_grammar_request_plan(config, language, text, include_explanations)
        }
        CustomStreamingServiceConfig::Doubao(_) => Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::ServiceUnavailable,
            "Grammar correction is not available for Doubao",
        )),
    }
}

pub fn build_gemini_translation_request_plan(
    config: &GeminiConfig,
    request: &OpenAiTranslationRequest,
) -> Result<CustomStreamingHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Gemini API key", &config.api_key)?;
    validate_required("Gemini model", &config.model)?;
    validate_custom_streaming_language_pair(&GEMINI_SUPPORTED_LANGUAGES, request)?;

    let source_lang_name = if request.from_language == TranslationLanguage::Auto {
        "the detected language".to_string()
    } else {
        request.from_language.display_name().to_string()
    };
    let target_lang_name = request.to_language.display_name();
    let user_prompt = format!(
        "Translate the following {source_lang_name} text into {target_lang_name} text: \"\"\"{}\"\"\"",
        request.text
    );
    let system_prompt = if request
        .custom_prompt
        .as_deref()
        .is_some_and(|prompt| !prompt.trim().is_empty())
    {
        format!(
            "{}\n\nAdditional instructions: {}",
            OPENAI_TRANSLATION_SYSTEM_PROMPT,
            request.custom_prompt.as_deref().unwrap_or_default()
        )
    } else {
        OPENAI_TRANSLATION_SYSTEM_PROMPT.to_string()
    };

    Ok(CustomStreamingHttpRequestPlan {
        method: "POST",
        endpoint: gemini_stream_endpoint(&config.model, &config.api_key)?,
        headers: Vec::new(),
        body: gemini_request_body(system_prompt, user_prompt, config.temperature),
        streaming_format: CustomStreamingFormat::Gemini,
    })
}

pub fn build_gemini_grammar_request_plan(
    config: &GeminiConfig,
    language: TranslationLanguage,
    text: &str,
    include_explanations: bool,
) -> Result<CustomStreamingHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Gemini API key", &config.api_key)?;
    validate_required("Gemini model", &config.model)?;

    Ok(CustomStreamingHttpRequestPlan {
        method: "POST",
        endpoint: gemini_stream_endpoint(&config.model, &config.api_key)?,
        headers: Vec::new(),
        body: gemini_request_body(
            grammar_correction_system_prompt(include_explanations).to_string(),
            build_grammar_correction_user_prompt(language, text),
            config.temperature,
        ),
        streaming_format: CustomStreamingFormat::Gemini,
    })
}

pub fn build_doubao_translation_request_plan(
    config: &DoubaoConfig,
    request: &OpenAiTranslationRequest,
) -> Result<CustomStreamingHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Doubao endpoint", &config.endpoint)?;
    validate_required("Doubao API key", &config.api_key)?;
    validate_required("Doubao model", &config.model)?;
    validate_custom_streaming_language_pair(&DOUBAO_SUPPORTED_LANGUAGES, request)?;

    Ok(CustomStreamingHttpRequestPlan {
        method: "POST",
        endpoint: config.endpoint.clone(),
        headers: vec![(
            "Authorization".to_string(),
            format!("Bearer {}", config.api_key),
        )],
        body: json!({
            "model": config.model,
            "stream": true,
            "input": [{
                "role": "user",
                "content": [{
                    "type": "input_text",
                    "text": request.text,
                    "translation_options": {
                        "source_language": doubao_language_code(request.from_language),
                        "target_language": doubao_language_code(request.to_language),
                    }
                }]
            }]
        }),
        streaming_format: CustomStreamingFormat::Doubao,
    })
}

pub fn execute_custom_streaming_request<C: CustomStreamingHttpClient>(
    client: &mut C,
    plan: &CustomStreamingHttpRequestPlan,
) -> Result<Vec<String>, OpenAiExecutionError> {
    let sse = client.post_sse(plan)?;
    Ok(parse_custom_streaming_chunks(plan.streaming_format, &sse))
}

pub fn translate_custom_streaming_service<C: CustomStreamingHttpClient>(
    client: &mut C,
    config: &CustomStreamingServiceConfig,
    request: &OpenAiTranslationRequest,
    service_id: impl Into<String>,
    service_name: impl Into<String>,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let service_id = service_id.into();
    let service_name = service_name.into();
    let plan = build_custom_streaming_translation_request_plan(config, request)
        .map_err(|error| attach_service_id(error, &service_id))?;
    let chunks = execute_custom_streaming_request(client, &plan)
        .map_err(|error| attach_service_id(error, &service_id))?;
    let translated_text = cleanup_custom_streaming_translation_text(config, &chunks.concat());

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
        raw_html: None,
    })
}

pub fn correct_custom_streaming_grammar<C: CustomStreamingHttpClient>(
    client: &mut C,
    config: &CustomStreamingServiceConfig,
    language: TranslationLanguage,
    text: &str,
    include_explanations: bool,
    service_id: impl Into<String>,
    service_name: impl Into<String>,
) -> Result<GrammarCorrectResultDto, OpenAiExecutionError> {
    let service_id = service_id.into();
    let service_name = service_name.into();
    let plan =
        build_custom_streaming_grammar_request_plan(config, language, text, include_explanations)
            .map_err(|error| attach_service_id(error, &service_id))?;
    let chunks = execute_custom_streaming_request(client, &plan)
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

pub fn parse_custom_streaming_chunks(format: CustomStreamingFormat, sse: &str) -> Vec<String> {
    match format {
        CustomStreamingFormat::Gemini => parse_gemini_stream_chunks(sse),
        CustomStreamingFormat::Doubao => parse_doubao_stream_chunks(sse),
    }
}

pub fn parse_gemini_stream_chunks(sse: &str) -> Vec<String> {
    let mut chunks = Vec::new();
    for line in sse.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }

        let data = line.strip_prefix("data: ").unwrap_or(line).trim();
        if data == "[DONE]" {
            break;
        }

        if let Some(text) = gemini_text_delta(data) {
            chunks.push(text);
        }
    }

    chunks
}

pub fn parse_doubao_stream_chunks(sse: &str) -> Vec<String> {
    let mut chunks = Vec::new();
    let mut current_event: Option<&str> = None;

    for line in sse.lines() {
        let line = line.trim();
        if line.is_empty() {
            current_event = None;
            continue;
        }

        if let Some(event) = line.strip_prefix("event: ") {
            current_event = Some(event.trim());
            continue;
        }

        let Some(data) = line.strip_prefix("data: ") else {
            continue;
        };
        let data = data.trim();
        if data == "[DONE]" {
            break;
        }

        if current_event == Some("response.output_text.delta") {
            if let Some(delta) = doubao_text_delta(data) {
                chunks.push(delta);
            }
        }

        current_event = None;
    }

    chunks
}

pub fn cleanup_custom_streaming_translation_text(
    config: &CustomStreamingServiceConfig,
    text: &str,
) -> String {
    match config {
        CustomStreamingServiceConfig::Gemini(_) => cleanup_openai_translation_text(text),
        CustomStreamingServiceConfig::Doubao(_) => cleanup_doubao_translation_text(text),
    }
}

pub fn cleanup_doubao_translation_text(text: &str) -> String {
    let cleaned = cleanup_openai_translation_text(text);
    let trimmed = cleaned.trim();
    if trimmed.len() >= 2 && trimmed.starts_with('\'') && trimmed.ends_with('\'') {
        trimmed[1..trimmed.len() - 1].trim().to_string()
    } else {
        cleaned
    }
}

pub fn doubao_language_code(language: TranslationLanguage) -> &'static str {
    match language {
        TranslationLanguage::Auto => "auto",
        TranslationLanguage::SimplifiedChinese => "zh",
        TranslationLanguage::TraditionalChinese => "zh-Hant",
        TranslationLanguage::English => "en",
        TranslationLanguage::Japanese => "ja",
        TranslationLanguage::Korean => "ko",
        TranslationLanguage::French => "fr",
        TranslationLanguage::Spanish => "es",
        TranslationLanguage::Portuguese => "pt",
        TranslationLanguage::Italian => "it",
        TranslationLanguage::German => "de",
        TranslationLanguage::Russian => "ru",
        TranslationLanguage::Arabic => "ar",
        TranslationLanguage::Dutch => "nl",
        TranslationLanguage::Polish => "pl",
        TranslationLanguage::Turkish => "tr",
        TranslationLanguage::Swedish => "sv",
        TranslationLanguage::Indonesian => "id",
        TranslationLanguage::Vietnamese => "vi",
        TranslationLanguage::Thai => "th",
        TranslationLanguage::Hindi => "hi",
        other => other.to_code(),
    }
}

pub fn custom_streaming_error_from_response(
    status_code: u16,
    reason: &str,
    error_body: &str,
) -> OpenAiExecutionError {
    let message = extract_error_message(error_body)
        .unwrap_or_else(|| format!("API error ({status_code}): {reason}"));
    let code = match status_code {
        401 | 403 => OpenAiExecutionErrorCode::InvalidApiKey,
        429 => OpenAiExecutionErrorCode::RateLimited,
        408 | 504 => OpenAiExecutionErrorCode::Timeout,
        500..=599 => OpenAiExecutionErrorCode::ServiceUnavailable,
        _ => OpenAiExecutionErrorCode::InvalidResponse,
    };

    OpenAiExecutionError::new(code, message)
}

fn gemini_request_body(system_prompt: String, user_prompt: String, temperature: f64) -> Value {
    json!({
        "contents": [{
            "role": "user",
            "parts": [{ "text": user_prompt }]
        }],
        "systemInstruction": {
            "parts": [{ "text": system_prompt }]
        },
        "generationConfig": {
            "temperature": temperature.clamp(0.0, 2.0)
        }
    })
}

fn gemini_stream_endpoint(model: &str, api_key: &str) -> Result<String, OpenAiExecutionError> {
    let endpoint = format!(
        "{GEMINI_API_BASE_URL}/models/{}:streamGenerateContent",
        model.trim()
    );
    let mut url = reqwest::Url::parse(&endpoint).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Gemini endpoint '{endpoint}': {error}"),
        )
    })?;
    url.query_pairs_mut()
        .append_pair("alt", "sse")
        .append_pair("key", api_key.trim());
    Ok(url.to_string())
}

fn validate_required(label: &str, value: &str) -> Result<(), OpenAiExecutionError> {
    if value.trim().is_empty() {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidApiKey,
            format!("{label} is required but not configured"),
        ));
    }

    Ok(())
}

fn gemini_text_delta(data: &str) -> Option<String> {
    let root: Value = serde_json::from_str(data).ok()?;
    root.get("candidates")?
        .as_array()?
        .first()?
        .get("content")?
        .get("parts")?
        .as_array()?
        .first()?
        .get("text")?
        .as_str()
        .map(str::to_string)
        .filter(|text| !text.is_empty())
}

fn doubao_text_delta(data: &str) -> Option<String> {
    let root: Value = serde_json::from_str(data).ok()?;
    root.get("delta")?
        .as_str()
        .map(str::to_string)
        .filter(|text| !text.is_empty())
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

fn extract_error_message(error_body: &str) -> Option<String> {
    let root: Value = serde_json::from_str(error_body).ok()?;
    root.get("error")
        .and_then(|error| error.get("message"))
        .or_else(|| root.get("message"))
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
