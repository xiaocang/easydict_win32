use crate::compat_client::{CompatClientError, CompatHostFacade};
use crate::compat_protocol::{
    ConfigureParams, OcrLanguageDto, OcrLineDto, OcrRecognizeParams, OcrResultDto, SettingsSnapshot,
};
use crate::quick_translate::QuickTranslateSurface;
use crate::state::{settings_snapshot, EasydictUiState};
use image::codecs::jpeg::JpegEncoder;
use image::ColorType;
use serde_json::{json, Value};
use std::fmt;
use std::fs;
use std::net::IpAddr;
use std::path::{Path, PathBuf};
use std::time::Duration;
use win_fluent::prelude::ScreenCaptureResult;

const DEFAULT_OCR_SYSTEM_PROMPT: &str = "Extract all the text from this image perfectly. Output ONLY the extracted text, without any conversational filler, markdown formatting, or introductory words.";
const DEFAULT_OLLAMA_OCR_ENDPOINT: &str = "http://localhost:11434/api/generate";
const DEFAULT_OLLAMA_OCR_MODEL: &str = "glm-ocr";
const DEFAULT_CUSTOM_OCR_ENDPOINT: &str = "https://api.openai.com/v1/responses";
const DEFAULT_CUSTOM_OCR_MODEL: &str = "gpt-5.4-mini";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OcrMode {
    Translate,
    SilentClipboard,
}

impl OcrMode {
    pub fn label(self) -> &'static str {
        match self {
            Self::Translate => "OCR Translate",
            Self::SilentClipboard => "Silent OCR",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OcrEngineKind {
    WindowsNative,
    Ollama,
    CustomApi,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OcrHttpResponseParser {
    OllamaGenerate,
    ChatCompletions,
    Responses,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OcrEngineConfig {
    pub kind: OcrEngineKind,
    pub api_key: Option<String>,
    pub endpoint: String,
    pub model: String,
    pub system_prompt: String,
    pub language: Option<String>,
}

impl OcrEngineConfig {
    pub fn from_settings(settings: &SettingsSnapshot) -> Self {
        let kind = ocr_engine_kind(settings.ocr_engine.as_deref());
        let endpoint = setting_or_default(
            settings.ocr_endpoint.as_deref(),
            default_endpoint_for_engine(kind),
        );
        let model = setting_or_default(
            settings.ocr_model.as_deref(),
            default_model_for_engine(kind),
        );
        let system_prompt = setting_or_default(
            settings.ocr_system_prompt.as_deref(),
            DEFAULT_OCR_SYSTEM_PROMPT,
        );
        let language = normalized_optional(settings.ocr_language.as_deref())
            .filter(|value| !value.eq_ignore_ascii_case("auto"));

        Self {
            kind,
            api_key: normalized_optional(settings.ocr_api_key.as_deref()),
            endpoint,
            model,
            system_prompt,
            language,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct OcrHttpRequestPlan {
    pub endpoint: String,
    pub authorization_bearer: Option<String>,
    pub body: Value,
    pub response_parser: OcrHttpResponseParser,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum OcrImageEncodeError {
    InvalidDimensions,
    BufferTooShort { expected: usize, actual: usize },
    SizeOverflow,
    JpegEncode(String),
}

impl fmt::Display for OcrImageEncodeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidDimensions => formatter.write_str("OCR image dimensions are invalid"),
            Self::BufferTooShort { expected, actual } => write!(
                formatter,
                "OCR image buffer is too short: expected at least {expected} bytes, got {actual}"
            ),
            Self::SizeOverflow => formatter.write_str("OCR image dimensions are too large"),
            Self::JpegEncode(message) => write!(formatter, "failed to encode OCR JPEG: {message}"),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OcrCaptureResult {
    pub pixel_data_path: String,
    pub pixel_width: u32,
    pub pixel_height: u32,
    pub preferred_language_tag: Option<String>,
}

impl OcrCaptureResult {
    pub fn new(pixel_data_path: impl Into<String>, pixel_width: u32, pixel_height: u32) -> Self {
        Self {
            pixel_data_path: pixel_data_path.into(),
            pixel_width,
            pixel_height,
            preferred_language_tag: None,
        }
    }

    pub fn preferred_language_tag(mut self, value: impl Into<String>) -> Self {
        self.preferred_language_tag = Some(value.into());
        self
    }

    fn into_params(self) -> OcrRecognizeParams {
        OcrRecognizeParams {
            pixel_data_path: self.pixel_data_path,
            pixel_width: self.pixel_width,
            pixel_height: self.pixel_height,
            preferred_language_tag: self.preferred_language_tag,
        }
    }
}

impl From<ScreenCaptureResult> for OcrCaptureResult {
    fn from(value: ScreenCaptureResult) -> Self {
        Self {
            pixel_data_path: value.pixel_data_path,
            pixel_width: value.pixel_width,
            pixel_height: value.pixel_height,
            preferred_language_tag: None,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct OcrRecognizeRequest {
    pub query_id: u64,
    pub mode: OcrMode,
    pub params: OcrRecognizeParams,
    pub settings: SettingsSnapshot,
}

#[derive(Clone, Debug, PartialEq)]
pub struct OcrOutcome {
    pub query_id: u64,
    pub mode: OcrMode,
    pub result: Result<OcrResultDto, OcrBackendError>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum OcrResultAction {
    TranslateInMini,
    CopyText(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum OcrStartError {
    MissingPixelData,
    InvalidDimensions,
}

impl fmt::Display for OcrStartError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingPixelData => formatter.write_str("OCR capture pixel data is missing"),
            Self::InvalidDimensions => formatter.write_str("OCR capture dimensions are invalid"),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OcrBackendError {
    pub message: String,
}

impl OcrBackendError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl fmt::Display for OcrBackendError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl From<CompatClientError> for OcrBackendError {
    fn from(error: CompatClientError) -> Self {
        Self::new(error.to_string())
    }
}

impl From<OcrImageEncodeError> for OcrBackendError {
    fn from(error: OcrImageEncodeError) -> Self {
        Self::new(error.to_string())
    }
}

pub trait OcrBackend {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), OcrBackendError> {
        let _ = settings;
        Ok(())
    }

    fn recognize(&mut self, params: &OcrRecognizeParams) -> Result<OcrResultDto, OcrBackendError>;
}

pub trait OcrHttpClient {
    fn post_json(&mut self, request: &OcrHttpRequestPlan) -> Result<String, OcrBackendError>;
}

pub struct ReqwestOcrHttpClient {
    client: reqwest::blocking::Client,
}

impl ReqwestOcrHttpClient {
    pub fn from_settings(settings: &SettingsSnapshot) -> Result<Self, OcrBackendError> {
        let mut builder = reqwest::blocking::Client::builder().timeout(Duration::from_secs(120));

        if settings.proxy_enabled.unwrap_or(false) {
            if let Some(proxy_uri) = normalized_optional(settings.proxy_uri.as_deref()) {
                let proxy = if settings.proxy_bypass_local.unwrap_or(false) {
                    let proxy_url = reqwest::Url::parse(&proxy_uri).map_err(|error| {
                        OcrBackendError::new(format!(
                            "Invalid OCR proxy URI '{proxy_uri}': {error}"
                        ))
                    })?;
                    reqwest::Proxy::custom(move |url| {
                        (!is_loopback_url(url)).then(|| proxy_url.clone())
                    })
                } else {
                    reqwest::Proxy::all(&proxy_uri).map_err(|error| {
                        OcrBackendError::new(format!(
                            "Invalid OCR proxy URI '{proxy_uri}': {error}"
                        ))
                    })?
                };
                builder = builder.proxy(proxy);
            }
        }

        let client = builder.build().map_err(|error| {
            OcrBackendError::new(format!("Could not create OCR HTTP client: {error}"))
        })?;
        Ok(Self { client })
    }
}

impl OcrHttpClient for ReqwestOcrHttpClient {
    fn post_json(&mut self, request: &OcrHttpRequestPlan) -> Result<String, OcrBackendError> {
        let mut builder = self.client.post(&request.endpoint).json(&request.body);
        if let Some(api_key) = request.authorization_bearer.as_deref() {
            builder = builder.bearer_auth(api_key);
        }

        let response = builder
            .send()
            .map_err(|error| OcrBackendError::new(format!("OCR HTTP request failed: {error}")))?;
        let status = response.status();
        let body = response.text().map_err(|error| {
            OcrBackendError::new(format!("Could not read OCR HTTP response: {error}"))
        })?;

        if !status.is_success() {
            return Err(OcrBackendError::new(format!(
                "OCR HTTP request failed with status {status}: {body}"
            )));
        }

        Ok(body)
    }
}

pub struct NativeOcrBackend<C> {
    http_client: C,
    config: Option<OcrEngineConfig>,
}

impl<C> NativeOcrBackend<C> {
    pub fn new(http_client: C) -> Self {
        Self {
            http_client,
            config: None,
        }
    }

    pub fn http_client(&self) -> &C {
        &self.http_client
    }
}

impl<C: OcrHttpClient> OcrBackend for NativeOcrBackend<C> {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), OcrBackendError> {
        self.config = Some(OcrEngineConfig::from_settings(settings));
        Ok(())
    }

    fn recognize(&mut self, params: &OcrRecognizeParams) -> Result<OcrResultDto, OcrBackendError> {
        let Some(config) = self.config.as_ref() else {
            return Err(OcrBackendError::new(
                "OCR backend must be configured before recognition",
            ));
        };

        recognize_with_native_provider(&mut self.http_client, config, params)
    }
}

pub fn merge_ocr_words<S: AsRef<str>>(words: &[S]) -> String {
    let Some((first, _)) = words.split_first() else {
        return String::new();
    };

    let capacity = words
        .iter()
        .map(|word| word.as_ref().len())
        .sum::<usize>()
        .saturating_add(words.len());
    let mut result = String::with_capacity(capacity);
    result.push_str(first.as_ref());

    for pair in words.windows(2) {
        let previous = pair[0].as_ref();
        let current = pair[1].as_ref();

        let Some(last_char) = previous.chars().next_back() else {
            result.push_str(current);
            continue;
        };
        let Some(first_char) = current.chars().next() else {
            result.push_str(current);
            continue;
        };

        if is_cjk_char(last_char) && is_cjk_char(first_char) {
            result.push_str(current);
        } else {
            result.push(' ');
            result.push_str(current);
        }
    }

    result
}

pub fn merge_ocr_lines(lines: &[OcrLineDto]) -> String {
    lines
        .iter()
        .map(|line| line.text.as_str())
        .collect::<Vec<_>>()
        .join("\r\n")
}

pub fn group_and_sort_ocr_lines(lines: &[OcrLineDto], y_tolerance_factor: f64) -> Vec<OcrLineDto> {
    if lines.len() <= 1 {
        return lines.to_vec();
    }

    let heights = lines
        .iter()
        .map(|line| line.bounding_rect.height)
        .filter(|height| *height > 0.0)
        .collect::<Vec<_>>();
    let average_height = if heights.is_empty() {
        20.0
    } else {
        heights.iter().sum::<f64>() / heights.len() as f64
    };
    let y_tolerance = average_height * y_tolerance_factor;

    let mut sorted = lines.to_vec();
    sorted.sort_by(|left, right| {
        left.bounding_rect
            .y
            .partial_cmp(&right.bounding_rect.y)
            .unwrap_or(std::cmp::Ordering::Equal)
    });

    let mut rows: Vec<Vec<OcrLineDto>> = Vec::new();
    let mut current_row = vec![sorted[0].clone()];
    let mut current_row_y_sum = sorted[0].bounding_rect.y;

    for line in sorted.into_iter().skip(1) {
        let current_row_y_average = current_row_y_sum / current_row.len() as f64;
        if (line.bounding_rect.y - current_row_y_average).abs() <= y_tolerance {
            current_row_y_sum += line.bounding_rect.y;
            current_row.push(line);
        } else {
            rows.push(current_row);
            current_row_y_sum = line.bounding_rect.y;
            current_row = vec![line];
        }
    }
    rows.push(current_row);

    rows.into_iter()
        .flat_map(|mut row| {
            row.sort_by(|left, right| {
                left.bounding_rect
                    .x
                    .partial_cmp(&right.bounding_rect.x)
                    .unwrap_or(std::cmp::Ordering::Equal)
            });
            row
        })
        .collect()
}

pub fn merged_ocr_text(result: &OcrResultDto) -> String {
    if !result.text.trim().is_empty() {
        return result.text.clone();
    }

    let sorted_lines = group_and_sort_ocr_lines(&result.lines, 0.5);
    merge_ocr_lines(&sorted_lines)
}

impl OcrBackend for CompatHostFacade {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), OcrBackendError> {
        CompatHostFacade::configure(
            self,
            &ConfigureParams {
                settings: settings.clone(),
            },
        )
        .map(|_| ())
        .map_err(OcrBackendError::from)
    }

    fn recognize(&mut self, params: &OcrRecognizeParams) -> Result<OcrResultDto, OcrBackendError> {
        CompatHostFacade::ocr_recognize(self, params).map_err(OcrBackendError::from)
    }
}

pub fn build_ollama_ocr_request(
    config: &OcrEngineConfig,
    base64_image: impl Into<String>,
) -> OcrHttpRequestPlan {
    OcrHttpRequestPlan {
        endpoint: config.endpoint.clone(),
        authorization_bearer: None,
        body: json!({
            "model": config.model.as_str(),
            "prompt": config.system_prompt.as_str(),
            "images": [base64_image.into()],
            "stream": false,
        }),
        response_parser: OcrHttpResponseParser::OllamaGenerate,
    }
}

pub fn build_custom_api_ocr_request(
    config: &OcrEngineConfig,
    image_data_url: impl Into<String>,
) -> OcrHttpRequestPlan {
    let image_data_url = image_data_url.into();
    if uses_responses_endpoint(&config.endpoint) {
        return OcrHttpRequestPlan {
            endpoint: config.endpoint.clone(),
            authorization_bearer: config.api_key.clone(),
            body: json!({
                "model": config.model.as_str(),
                "max_output_tokens": 2048,
                "store": false,
                "input": [{
                    "role": "user",
                    "content": [
                        { "type": "input_text", "text": config.system_prompt.as_str() },
                        { "type": "input_image", "image_url": image_data_url },
                    ],
                }],
            }),
            response_parser: OcrHttpResponseParser::Responses,
        };
    }

    OcrHttpRequestPlan {
        endpoint: config.endpoint.clone(),
        authorization_bearer: config.api_key.clone(),
        body: json!({
            "model": config.model.as_str(),
            "max_tokens": 2048,
            "messages": [
                { "role": "system", "content": config.system_prompt.as_str() },
                {
                    "role": "user",
                    "content": [{
                        "type": "image_url",
                        "image_url": { "url": image_data_url },
                    }],
                },
            ],
        }),
        response_parser: OcrHttpResponseParser::ChatCompletions,
    }
}

pub fn parse_ocr_http_response(parser: OcrHttpResponseParser, json_text: &str) -> OcrResultDto {
    let text = match parser {
        OcrHttpResponseParser::OllamaGenerate => parse_ollama_response_text(json_text),
        OcrHttpResponseParser::ChatCompletions => parse_chat_completions_response_text(json_text),
        OcrHttpResponseParser::Responses => parse_responses_response_text(json_text),
    };

    OcrResultDto {
        text,
        lines: Vec::new(),
        text_angle: None,
        detected_language: None,
    }
}

pub fn bgra_to_base64_bmp(
    bgra: &[u8],
    width: u32,
    height: u32,
) -> Result<String, OcrImageEncodeError> {
    let bmp = bgra_to_bmp_bytes(bgra, width, height)?;
    Ok(base64_encode(&bmp))
}

pub fn bgra_to_base64_jpeg_data_url(
    bgra: &[u8],
    width: u32,
    height: u32,
) -> Result<String, OcrImageEncodeError> {
    let jpeg = bgra_to_jpeg_bytes(bgra, width, height)?;
    Ok(format!("data:image/jpeg;base64,{}", base64_encode(&jpeg)))
}

fn recognize_with_native_provider<C: OcrHttpClient>(
    http_client: &mut C,
    config: &OcrEngineConfig,
    params: &OcrRecognizeParams,
) -> Result<OcrResultDto, OcrBackendError> {
    if config.kind == OcrEngineKind::WindowsNative {
        return Err(OcrBackendError::new(
            "Windows Native OCR is handled by the compatibility OCR worker",
        ));
    }

    let pixel_data = fs::read(&params.pixel_data_path).map_err(|error| {
        OcrBackendError::new(format!(
            "Could not read OCR pixel data '{}': {error}",
            params.pixel_data_path
        ))
    })?;

    let plan = match config.kind {
        OcrEngineKind::Ollama => {
            let image = bgra_to_base64_bmp(&pixel_data, params.pixel_width, params.pixel_height)?;
            build_ollama_ocr_request(config, image)
        }
        OcrEngineKind::CustomApi => {
            let image_data_url =
                bgra_to_base64_jpeg_data_url(&pixel_data, params.pixel_width, params.pixel_height)?;
            build_custom_api_ocr_request(config, image_data_url)
        }
        OcrEngineKind::WindowsNative => unreachable!("handled before provider dispatch"),
    };

    let json = http_client.post_json(&plan)?;
    Ok(parse_ocr_http_response(plan.response_parser, &json))
}

fn ocr_engine_kind(value: Option<&str>) -> OcrEngineKind {
    match value
        .unwrap_or_default()
        .trim()
        .to_ascii_lowercase()
        .as_str()
    {
        "ollama" => OcrEngineKind::Ollama,
        "customapi" | "custom-api" | "custom_api" => OcrEngineKind::CustomApi,
        _ => OcrEngineKind::WindowsNative,
    }
}

fn default_endpoint_for_engine(kind: OcrEngineKind) -> &'static str {
    match kind {
        OcrEngineKind::CustomApi => DEFAULT_CUSTOM_OCR_ENDPOINT,
        OcrEngineKind::WindowsNative | OcrEngineKind::Ollama => DEFAULT_OLLAMA_OCR_ENDPOINT,
    }
}

fn default_model_for_engine(kind: OcrEngineKind) -> &'static str {
    match kind {
        OcrEngineKind::CustomApi => DEFAULT_CUSTOM_OCR_MODEL,
        OcrEngineKind::WindowsNative | OcrEngineKind::Ollama => DEFAULT_OLLAMA_OCR_MODEL,
    }
}

fn setting_or_default(value: Option<&str>, default_value: &str) -> String {
    normalized_optional(value).unwrap_or_else(|| default_value.to_string())
}

fn normalized_optional(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToOwned::to_owned)
}

fn is_loopback_url(url: &reqwest::Url) -> bool {
    let Some(host) = url.host_str() else {
        return false;
    };

    host.eq_ignore_ascii_case("localhost")
        || host
            .parse::<IpAddr>()
            .map(|address| address.is_loopback())
            .unwrap_or(false)
}

fn uses_responses_endpoint(endpoint: &str) -> bool {
    let end = endpoint
        .char_indices()
        .find(|(_, character)| *character == '?' || *character == '#')
        .map(|(index, _)| index)
        .unwrap_or(endpoint.len());

    endpoint[..end]
        .trim()
        .trim_end_matches('/')
        .to_ascii_lowercase()
        .ends_with("/responses")
}

fn parse_ollama_response_text(json_text: &str) -> String {
    let Ok(value) = serde_json::from_str::<Value>(json_text) else {
        return String::new();
    };

    value
        .get("response")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_string()
}

fn parse_chat_completions_response_text(json_text: &str) -> String {
    let Ok(value) = serde_json::from_str::<Value>(json_text) else {
        return String::new();
    };

    value
        .get("choices")
        .and_then(Value::as_array)
        .and_then(|choices| choices.first())
        .and_then(|choice| choice.get("message"))
        .and_then(|message| message.get("content"))
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_string()
}

fn parse_responses_response_text(json_text: &str) -> String {
    let Ok(value) = serde_json::from_str::<Value>(json_text) else {
        return String::new();
    };

    if let Some(output_text) = value.get("output_text").and_then(Value::as_str) {
        return output_text.trim().to_string();
    }

    let mut text = String::new();
    let Some(output) = value.get("output").and_then(Value::as_array) else {
        return text;
    };

    for output_item in output {
        let Some(content) = output_item.get("content").and_then(Value::as_array) else {
            continue;
        };

        for content_item in content {
            if let Some(chunk) = content_item.get("text").and_then(Value::as_str) {
                text.push_str(chunk);
            }
        }
    }

    text.trim().to_string()
}

fn bgra_to_bmp_bytes(bgra: &[u8], width: u32, height: u32) -> Result<Vec<u8>, OcrImageEncodeError> {
    let (width, height, _) = validate_bgra_buffer(bgra, width, height)?;
    let bmp_header_size = 54usize;
    let row_bytes = width
        .checked_mul(3)
        .ok_or(OcrImageEncodeError::SizeOverflow)?;
    let row_stride = row_bytes
        .checked_add(3)
        .map(|value| value / 4 * 4)
        .ok_or(OcrImageEncodeError::SizeOverflow)?;
    let image_data_size = row_stride
        .checked_mul(height)
        .ok_or(OcrImageEncodeError::SizeOverflow)?;
    let bmp_size = bmp_header_size
        .checked_add(image_data_size)
        .ok_or(OcrImageEncodeError::SizeOverflow)?;

    let bmp_size_u32 = u32::try_from(bmp_size).map_err(|_| OcrImageEncodeError::SizeOverflow)?;
    let image_data_size_u32 =
        u32::try_from(image_data_size).map_err(|_| OcrImageEncodeError::SizeOverflow)?;
    let width_i32 = i32::try_from(width).map_err(|_| OcrImageEncodeError::SizeOverflow)?;
    let height_i32 = i32::try_from(height).map_err(|_| OcrImageEncodeError::SizeOverflow)?;

    let mut bmp = vec![0u8; bmp_size];
    bmp[0] = 0x42;
    bmp[1] = 0x4d;
    bmp[2..6].copy_from_slice(&bmp_size_u32.to_le_bytes());
    bmp[10..14].copy_from_slice(&(bmp_header_size as u32).to_le_bytes());
    bmp[14..18].copy_from_slice(&(40u32).to_le_bytes());
    bmp[18..22].copy_from_slice(&width_i32.to_le_bytes());
    bmp[22..26].copy_from_slice(&height_i32.to_le_bytes());
    bmp[26..28].copy_from_slice(&(1u16).to_le_bytes());
    bmp[28..30].copy_from_slice(&(24u16).to_le_bytes());
    bmp[34..38].copy_from_slice(&image_data_size_u32.to_le_bytes());

    for y in 0..height {
        let src_row = y
            .checked_mul(width)
            .and_then(|offset| offset.checked_mul(4))
            .ok_or(OcrImageEncodeError::SizeOverflow)?;
        let dst_row = bmp_header_size
            + (height - 1 - y)
                .checked_mul(row_stride)
                .ok_or(OcrImageEncodeError::SizeOverflow)?;

        for x in 0..width {
            let src_index = src_row
                .checked_add(x.checked_mul(4).ok_or(OcrImageEncodeError::SizeOverflow)?)
                .ok_or(OcrImageEncodeError::SizeOverflow)?;
            let dst_index = dst_row
                .checked_add(x.checked_mul(3).ok_or(OcrImageEncodeError::SizeOverflow)?)
                .ok_or(OcrImageEncodeError::SizeOverflow)?;

            bmp[dst_index] = bgra[src_index];
            bmp[dst_index + 1] = bgra[src_index + 1];
            bmp[dst_index + 2] = bgra[src_index + 2];
        }
    }

    Ok(bmp)
}

fn bgra_to_jpeg_bytes(
    bgra: &[u8],
    width: u32,
    height: u32,
) -> Result<Vec<u8>, OcrImageEncodeError> {
    let (width_usize, height_usize, expected) = validate_bgra_buffer(bgra, width, height)?;
    let rgb_len = width_usize
        .checked_mul(height_usize)
        .and_then(|pixels| pixels.checked_mul(3))
        .ok_or(OcrImageEncodeError::SizeOverflow)?;
    let mut rgb = Vec::with_capacity(rgb_len);

    for pixel in bgra[..expected].chunks_exact(4) {
        rgb.push(pixel[2]);
        rgb.push(pixel[1]);
        rgb.push(pixel[0]);
    }

    let mut jpeg = Vec::new();
    JpegEncoder::new_with_quality(&mut jpeg, 90)
        .encode(&rgb, width, height, ColorType::Rgb8.into())
        .map_err(|error| OcrImageEncodeError::JpegEncode(error.to_string()))?;
    Ok(jpeg)
}

fn validate_bgra_buffer(
    bgra: &[u8],
    width: u32,
    height: u32,
) -> Result<(usize, usize, usize), OcrImageEncodeError> {
    if width == 0 || height == 0 {
        return Err(OcrImageEncodeError::InvalidDimensions);
    }

    let width = usize::try_from(width).map_err(|_| OcrImageEncodeError::SizeOverflow)?;
    let height = usize::try_from(height).map_err(|_| OcrImageEncodeError::SizeOverflow)?;
    let expected = width
        .checked_mul(height)
        .and_then(|pixels| pixels.checked_mul(4))
        .ok_or(OcrImageEncodeError::SizeOverflow)?;

    if bgra.len() < expected {
        return Err(OcrImageEncodeError::BufferTooShort {
            expected,
            actual: bgra.len(),
        });
    }

    Ok((width, height, expected))
}

fn base64_encode(bytes: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    let mut encoded = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let first = chunk[0];
        let second = chunk.get(1).copied().unwrap_or(0);
        let third = chunk.get(2).copied().unwrap_or(0);

        encoded.push(TABLE[(first >> 2) as usize] as char);
        encoded.push(TABLE[(((first & 0x03) << 4) | (second >> 4)) as usize] as char);

        if chunk.len() > 1 {
            encoded.push(TABLE[(((second & 0x0f) << 2) | (third >> 6)) as usize] as char);
        } else {
            encoded.push('=');
        }

        if chunk.len() > 2 {
            encoded.push(TABLE[(third & 0x3f) as usize] as char);
        } else {
            encoded.push('=');
        }
    }

    encoded
}

pub fn begin_ocr_recognize(
    state: &mut EasydictUiState,
    mode: OcrMode,
    mut capture: OcrCaptureResult,
) -> Result<OcrRecognizeRequest, OcrStartError> {
    let pixel_data_path = capture.pixel_data_path.trim();
    if pixel_data_path.is_empty() {
        return Err(OcrStartError::MissingPixelData);
    }

    if capture.pixel_width == 0 || capture.pixel_height == 0 {
        return Err(OcrStartError::InvalidDimensions);
    }

    if capture.preferred_language_tag.is_none() {
        let language = state.settings.ocr_language.trim();
        if !language.is_empty() && !language.eq_ignore_ascii_case("auto") {
            capture.preferred_language_tag = Some(language.to_string());
        }
    }

    let query_id = state.next_ocr_query_id;
    state.next_ocr_query_id = state.next_ocr_query_id.saturating_add(1);
    state.active_ocr_query_id = Some(query_id);
    state.active_ocr_mode = Some(mode);
    state.pending_ocr_mode = None;
    state.ocr_status_text = format!("{}: recognizing text", mode.label());
    state.last_ocr_text = None;
    state.last_ocr_error = None;

    Ok(OcrRecognizeRequest {
        query_id,
        mode,
        params: capture.into_params(),
        settings: settings_snapshot(&state.settings),
    })
}

pub fn apply_ocr_start_error(state: &mut EasydictUiState, error: OcrStartError) {
    state.active_ocr_query_id = None;
    state.active_ocr_mode = None;
    state.ocr_status_text = error.to_string();
    state.last_ocr_error = Some(error.to_string());
}

pub fn run_ocr_recognize<B: OcrBackend>(
    backend: &mut B,
    request: &OcrRecognizeRequest,
) -> OcrOutcome {
    let result = backend
        .configure(&request.settings)
        .and_then(|_| backend.recognize(&request.params));

    OcrOutcome {
        query_id: request.query_id,
        mode: request.mode,
        result,
    }
}

pub fn run_ocr_recognize_with_current_app_dir(request: OcrRecognizeRequest) -> OcrOutcome {
    match current_app_dir() {
        Ok(app_dir) => run_ocr_recognize_with_packaged_host(request, app_dir),
        Err(message) => OcrOutcome {
            query_id: request.query_id,
            mode: request.mode,
            result: Err(OcrBackendError::new(message)),
        },
    }
}

pub fn run_ocr_recognize_with_packaged_host(
    request: OcrRecognizeRequest,
    app_dir: impl AsRef<Path>,
) -> OcrOutcome {
    if OcrEngineConfig::from_settings(&request.settings).kind != OcrEngineKind::WindowsNative {
        return run_ocr_recognize_with_native_provider(request);
    }

    match CompatHostFacade::spawn_packaged(app_dir) {
        Ok(mut backend) => run_ocr_recognize(&mut backend, &request),
        Err(error) => OcrOutcome {
            query_id: request.query_id,
            mode: request.mode,
            result: Err(OcrBackendError::from(error)),
        },
    }
}

pub fn run_ocr_recognize_with_native_provider(request: OcrRecognizeRequest) -> OcrOutcome {
    let query_id = request.query_id;
    let mode = request.mode;
    let mut backend = match ReqwestOcrHttpClient::from_settings(&request.settings) {
        Ok(http_client) => NativeOcrBackend::new(http_client),
        Err(error) => {
            return OcrOutcome {
                query_id,
                mode,
                result: Err(error),
            };
        }
    };

    run_ocr_recognize(&mut backend, &request)
}

pub fn apply_ocr_outcome(
    state: &mut EasydictUiState,
    outcome: OcrOutcome,
) -> Option<OcrResultAction> {
    if state.active_ocr_query_id != Some(outcome.query_id) {
        return None;
    }

    state.active_ocr_query_id = None;
    state.active_ocr_mode = None;

    match outcome.result {
        Ok(result) => apply_ocr_success(state, outcome.mode, result),
        Err(error) => {
            state.ocr_status_text = format!("{} failed: {}", outcome.mode.label(), error.message);
            state.last_ocr_error = Some(error.message);
            None
        }
    }
}

fn apply_ocr_success(
    state: &mut EasydictUiState,
    mode: OcrMode,
    result: OcrResultDto,
) -> Option<OcrResultAction> {
    let text = merged_ocr_text(&result).trim().to_string();
    if text.is_empty() {
        state.ocr_status_text = "No text recognized".to_string();
        state.last_ocr_text = None;
        state.last_ocr_error = None;
        return None;
    }

    state.ocr_status_text = format!("{} recognized text", mode.label());
    state.last_ocr_text = Some(text.clone());
    state.last_ocr_error = None;

    match mode {
        OcrMode::Translate => {
            state.mini.text = text;
            state.mini.source_language = ocr_source_language(&result.detected_language);
            state.mini.detected_language = result
                .detected_language
                .as_ref()
                .map(ocr_detected_language_label);
            state.mini.status_text = "OCR text ready".to_string();
            state.mini.current_quick_query_mode =
                crate::quick_translate::QuickQueryMode::Translation;
            state.mini.grammar_correction_fallback = false;
            Some(OcrResultAction::TranslateInMini)
        }
        OcrMode::SilentClipboard => Some(OcrResultAction::CopyText(text)),
    }
}

fn is_cjk_char(character: char) -> bool {
    matches!(
        character,
        '\u{4E00}'..='\u{9FFF}'
            | '\u{3400}'..='\u{4DBF}'
            | '\u{F900}'..='\u{FAFF}'
            | '\u{3040}'..='\u{309F}'
            | '\u{30A0}'..='\u{30FF}'
            | '\u{AC00}'..='\u{D7AF}'
            | '\u{3000}'..='\u{303F}'
            | '\u{FF00}'..='\u{FFEF}'
    )
}

fn ocr_source_language(language: &Option<OcrLanguageDto>) -> String {
    language
        .as_ref()
        .map(|language| language.tag.trim())
        .filter(|value| !value.is_empty())
        .unwrap_or("auto")
        .to_string()
}

fn ocr_detected_language_label(language: &OcrLanguageDto) -> String {
    let display_name = language.display_name.trim();
    if !display_name.is_empty() {
        return format!("Detected: {display_name}");
    }

    let tag = language.tag.trim();
    if tag.is_empty() {
        "Detected: Unknown".to_string()
    } else {
        format!("Detected: {tag}")
    }
}

pub fn pending_mode_from_surface_action(current: Option<OcrMode>, copy_requested: bool) -> OcrMode {
    if copy_requested {
        return OcrMode::SilentClipboard;
    }

    current.unwrap_or(OcrMode::Translate)
}

pub fn reset_pending_ocr(state: &mut EasydictUiState) {
    state.pending_ocr_mode = None;
    state.active_ocr_query_id = None;
    state.active_ocr_mode = None;
    state.ocr_status_text = "OCR capture cancelled".to_string();
}

pub fn ocr_surface() -> QuickTranslateSurface {
    QuickTranslateSurface::Mini
}

fn current_app_dir() -> Result<PathBuf, String> {
    let exe = std::env::current_exe()
        .map_err(|error| format!("Could not locate current executable: {error}"))?;
    exe.parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| "Could not locate current executable directory".to_string())
}
