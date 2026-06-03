use crate::ocr::{bgra_to_base64_bmp, bgra_to_base64_jpeg_data_url, OcrImageEncodeError};
use crate::openai_compatible::{OpenAiApiFormat, OpenAiCompatibleConfig};
use serde_json::{json, Value};

pub const VISION_LAYOUT_DETECTION_PROMPT: &str = r#"Analyze this PDF page image and detect all layout regions.
For each region, identify its type and bounding box coordinates.

Return ONLY a JSON array (no other text) with objects having these fields:
- type: one of "title", "text", "figure", "table", "formula", "caption", "header", "footer", "isolated_formula"
- x: left coordinate as percentage (0-100) of page width
- y: top coordinate as percentage (0-100) of page height
- width: width as percentage (0-100) of page width
- height: height as percentage (0-100) of page height
- confidence: detection confidence (0.0-1.0)

Example: [{"type":"title","x":10,"y":5,"width":80,"height":4,"confidence":0.95}]"#;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VisionLayoutRegionType {
    Unknown,
    Header,
    Footer,
    Body,
    LeftColumn,
    RightColumn,
    TableLike,
    Figure,
    Table,
    Formula,
    Caption,
    Title,
    IsolatedFormula,
}

#[derive(Clone, Debug, PartialEq)]
pub struct VisionLayoutDetection {
    pub region_type: VisionLayoutRegionType,
    pub confidence: f32,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct VisionLayoutHttpRequestPlan {
    pub method: &'static str,
    pub endpoint: String,
    pub content_type: &'static str,
    pub headers: Vec<(String, String)>,
    pub body: Value,
    pub api_format: OpenAiApiFormat,
}

pub fn build_vision_layout_request_plan_from_bgra(
    config: &OpenAiCompatibleConfig,
    bgra: &[u8],
    width: u32,
    height: u32,
) -> Result<VisionLayoutHttpRequestPlan, OcrImageEncodeError> {
    let api_format = config.resolved_format();
    let image_data_url = match api_format {
        OpenAiApiFormat::ChatCompletions => {
            let base64_bmp = bgra_to_base64_bmp(bgra, width, height)?;
            format!("data:image/bmp;base64,{base64_bmp}")
        }
        OpenAiApiFormat::Responses => bgra_to_base64_jpeg_data_url(bgra, width, height)?,
        OpenAiApiFormat::Auto => unreachable!("config.resolved_format() resolves Auto"),
    };

    Ok(build_vision_layout_request_plan_with_format(
        config,
        api_format,
        image_data_url,
    ))
}

pub fn build_vision_layout_request_plan(
    config: &OpenAiCompatibleConfig,
    image_data_url: impl Into<String>,
) -> VisionLayoutHttpRequestPlan {
    let api_format = config.resolved_format();
    build_vision_layout_request_plan_with_format(config, api_format, image_data_url)
}

pub fn parse_vision_layout_response(
    api_format: OpenAiApiFormat,
    response_json: &str,
    image_width: u32,
    image_height: u32,
) -> Vec<VisionLayoutDetection> {
    let Some(content) = extract_vision_layout_response_content(api_format, response_json) else {
        return Vec::new();
    };
    let Some(json_array) = extract_json_array(&content) else {
        return Vec::new();
    };

    parse_vision_layout_detection_array(json_array, image_width, image_height)
}

pub fn parse_vision_layout_detection_array(
    json_array: &str,
    image_width: u32,
    image_height: u32,
) -> Vec<VisionLayoutDetection> {
    let Ok(value) = serde_json::from_str::<Value>(json_array) else {
        return Vec::new();
    };
    let Some(array) = value.as_array() else {
        return Vec::new();
    };

    let mut detections = Vec::with_capacity(array.len());
    for item in array {
        if let Some(detection) = parse_detection_item(item, image_width, image_height) {
            detections.push(detection);
        }
    }

    detections
}

pub fn vision_layout_region_type_from_str(value: &str) -> VisionLayoutRegionType {
    match value.trim().to_ascii_lowercase().as_str() {
        "title" => VisionLayoutRegionType::Title,
        "text" | "plain text" => VisionLayoutRegionType::Body,
        "figure" => VisionLayoutRegionType::Figure,
        "table" => VisionLayoutRegionType::Table,
        "formula" => VisionLayoutRegionType::Formula,
        "caption" => VisionLayoutRegionType::Caption,
        "header" => VisionLayoutRegionType::Header,
        "footer" => VisionLayoutRegionType::Footer,
        "isolated_formula" => VisionLayoutRegionType::IsolatedFormula,
        _ => VisionLayoutRegionType::Unknown,
    }
}

fn build_vision_layout_request_plan_with_format(
    config: &OpenAiCompatibleConfig,
    api_format: OpenAiApiFormat,
    image_data_url: impl Into<String>,
) -> VisionLayoutHttpRequestPlan {
    let image_data_url = image_data_url.into();
    let body = match api_format {
        OpenAiApiFormat::ChatCompletions => json!({
            "model": config.model.as_str(),
            "messages": [{
                "role": "user",
                "content": [
                    { "type": "text", "text": VISION_LAYOUT_DETECTION_PROMPT },
                    { "type": "image_url", "image_url": { "url": image_data_url } },
                ],
            }],
            "max_tokens": 4096,
            "temperature": 0.1,
        }),
        OpenAiApiFormat::Responses => json!({
            "model": config.model.as_str(),
            "max_output_tokens": 4096,
            "store": false,
            "temperature": 0.1,
            "input": [{
                "role": "user",
                "content": [
                    { "type": "input_text", "text": VISION_LAYOUT_DETECTION_PROMPT },
                    { "type": "input_image", "image_url": image_data_url },
                ],
            }],
        }),
        OpenAiApiFormat::Auto => unreachable!("Auto must be resolved before building request body"),
    };

    VisionLayoutHttpRequestPlan {
        method: "POST",
        endpoint: config.endpoint.clone(),
        content_type: "application/json",
        headers: request_headers(config),
        body,
        api_format,
    }
}

fn request_headers(config: &OpenAiCompatibleConfig) -> Vec<(String, String)> {
    let mut headers = config.extra_headers.clone();
    let api_key = config.api_key.trim();
    if !api_key.is_empty() {
        headers.push(("Authorization".to_string(), format!("Bearer {api_key}")));
    }
    headers
}

fn extract_vision_layout_response_content(
    api_format: OpenAiApiFormat,
    response_json: &str,
) -> Option<String> {
    match api_format {
        OpenAiApiFormat::ChatCompletions => extract_chat_completions_content(response_json),
        OpenAiApiFormat::Responses => extract_responses_content(response_json),
        OpenAiApiFormat::Auto => extract_chat_completions_content(response_json)
            .or_else(|| extract_responses_content(response_json)),
    }
}

fn extract_chat_completions_content(response_json: &str) -> Option<String> {
    let value = serde_json::from_str::<Value>(response_json).ok()?;
    value
        .get("choices")
        .and_then(Value::as_array)
        .and_then(|choices| choices.first())
        .and_then(|choice| choice.get("message"))
        .and_then(|message| message.get("content"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|content| !content.is_empty())
        .map(ToOwned::to_owned)
}

fn extract_responses_content(response_json: &str) -> Option<String> {
    let value = serde_json::from_str::<Value>(response_json).ok()?;
    if let Some(output_text) = value
        .get("output_text")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|content| !content.is_empty())
    {
        return Some(output_text.to_string());
    }

    let output = value.get("output").and_then(Value::as_array)?;
    let mut content = String::new();
    for output_item in output {
        let Some(items) = output_item.get("content").and_then(Value::as_array) else {
            continue;
        };
        for item in items {
            if let Some(text) = item.get("text").and_then(Value::as_str) {
                content.push_str(text);
            }
        }
    }

    let content = content.trim();
    (!content.is_empty()).then(|| content.to_string())
}

fn extract_json_array(content: &str) -> Option<&str> {
    let start = content.find('[')?;
    let end = content.rfind(']')?;
    (end > start).then_some(&content[start..=end])
}

fn parse_detection_item(
    item: &Value,
    image_width: u32,
    image_height: u32,
) -> Option<VisionLayoutDetection> {
    let type_string = item
        .get("type")
        .and_then(Value::as_str)
        .unwrap_or("unknown");
    let x_pct = item.get("x").and_then(Value::as_f64)?;
    let y_pct = item.get("y").and_then(Value::as_f64)?;
    let width_pct = item.get("width").and_then(Value::as_f64)?;
    let height_pct = item.get("height").and_then(Value::as_f64)?;
    let confidence = item
        .get("confidence")
        .and_then(Value::as_f64)
        .unwrap_or(0.8) as f32;

    let image_width = f64::from(image_width);
    let image_height = f64::from(image_height);

    Some(VisionLayoutDetection {
        region_type: vision_layout_region_type_from_str(type_string),
        confidence,
        x: x_pct / 100.0 * image_width,
        y: y_pct / 100.0 * image_height,
        width: width_pct / 100.0 * image_width,
        height: height_pct / 100.0 * image_height,
    })
}
