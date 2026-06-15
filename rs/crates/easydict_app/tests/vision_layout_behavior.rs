use easydict_app::protocol::SettingsSnapshot;
use easydict_app::{
    build_vision_layout_request_plan, build_vision_layout_request_plan_from_bgra,
    execute_vision_layout_detection, parse_vision_layout_detection_array,
    parse_vision_layout_response, parse_vision_layout_response_result, OpenAiApiFormat,
    OpenAiCompatibleConfig, OpenAiExecutionError, OpenAiExecutionErrorCode,
    ReqwestVisionLayoutHttpClient, VisionLayoutDetection, VisionLayoutHttpClient,
    VisionLayoutHttpRequestPlan, VisionLayoutHttpResponse, VisionLayoutRegionType,
    VISION_LAYOUT_DETECTION_PROMPT,
};

const VISION_LAYOUT_SMOKE_ENDPOINT_ENV: &str = "EASYDICT_VISION_LAYOUT_SMOKE_ENDPOINT";
const VISION_LAYOUT_SMOKE_MODEL_ENV: &str = "EASYDICT_VISION_LAYOUT_SMOKE_MODEL";
const VISION_LAYOUT_SMOKE_API_KEY_ENV: &str = "EASYDICT_VISION_LAYOUT_SMOKE_API_KEY";

#[test]
fn vision_layout_detection_array_parses_percent_rects_to_pixels() {
    let detections = parse_vision_layout_detection_array(
        r#"[{"type":"title","x":10,"y":5,"width":80,"height":4,"confidence":0.95}]"#,
        200,
        100,
    );

    assert_eq!(detections.len(), 1);
    let detection = &detections[0];
    assert_eq!(detection.region_type, VisionLayoutRegionType::Title);
    assert_close(detection.x, 20.0);
    assert_close(detection.y, 5.0);
    assert_close(detection.width, 160.0);
    assert_close(detection.height, 4.0);
    assert_close_f32(detection.confidence, 0.95);
}

#[test]
fn vision_layout_detection_array_defaults_confidence_and_maps_text_to_body() {
    let detections = parse_vision_layout_detection_array(
        r#"[{"type":"plain text","x":0,"y":10,"width":50,"height":25}]"#,
        300,
        400,
    );

    assert_eq!(detections.len(), 1);
    let detection = &detections[0];
    assert_eq!(detection.region_type, VisionLayoutRegionType::Body);
    assert_close(detection.x, 0.0);
    assert_close(detection.y, 40.0);
    assert_close(detection.width, 150.0);
    assert_close(detection.height, 100.0);
    assert_close_f32(detection.confidence, 0.8);
}

#[test]
fn vision_layout_detection_array_preserves_unknown_region_type() {
    let detections = parse_vision_layout_detection_array(
        r#"[{"type":"sidebar","x":1,"y":2,"width":3,"height":4,"confidence":0.2}]"#,
        100,
        100,
    );

    assert_eq!(detections.len(), 1);
    assert_eq!(detections[0].region_type, VisionLayoutRegionType::Unknown);
}

#[test]
fn vision_layout_response_parser_extracts_chat_completions_content() {
    let detections = parse_vision_layout_response(
        OpenAiApiFormat::ChatCompletions,
        r#"{
            "choices": [{
                "message": {
                    "content": "[{\"type\":\"figure\",\"x\":25,\"y\":10,\"width\":50,\"height\":20,\"confidence\":0.7}]"
                }
            }]
        }"#,
        400,
        200,
    );

    assert_eq!(detections.len(), 1);
    assert_eq!(detections[0].region_type, VisionLayoutRegionType::Figure);
    assert_close(detections[0].x, 100.0);
    assert_close(detections[0].y, 20.0);
    assert_close(detections[0].width, 200.0);
    assert_close(detections[0].height, 40.0);
}

#[test]
fn vision_layout_response_parser_extracts_markdown_wrapped_json_array() {
    let detections = parse_vision_layout_response(
        OpenAiApiFormat::ChatCompletions,
        r#"{
            "choices": [{
                "message": {
                    "content": "```json\n[{\"type\":\"table\",\"x\":5,\"y\":20,\"width\":90,\"height\":30,\"confidence\":0.88}]\n```"
                }
            }]
        }"#,
        1000,
        500,
    );

    assert_eq!(detections.len(), 1);
    assert_eq!(detections[0].region_type, VisionLayoutRegionType::Table);
    assert_close(detections[0].x, 50.0);
    assert_close(detections[0].y, 100.0);
    assert_close(detections[0].width, 900.0);
    assert_close(detections[0].height, 150.0);
}

#[test]
fn vision_layout_response_parser_extracts_responses_output_text() {
    let detections = parse_vision_layout_response(
        OpenAiApiFormat::Responses,
        r#"{
            "output_text": "prefix [{\"type\":\"formula\",\"x\":10,\"y\":40,\"width\":20,\"height\":5,\"confidence\":0.6}] suffix"
        }"#,
        500,
        1000,
    );

    assert_eq!(detections.len(), 1);
    assert_eq!(detections[0].region_type, VisionLayoutRegionType::Formula);
    assert_close(detections[0].x, 50.0);
    assert_close(detections[0].y, 400.0);
    assert_close(detections[0].width, 100.0);
    assert_close(detections[0].height, 50.0);
}

#[test]
fn vision_layout_response_parser_extracts_responses_output_content_text() {
    let detections = parse_vision_layout_response(
        OpenAiApiFormat::Responses,
        r#"{
            "output": [{
                "content": [
                    { "type": "output_text", "text": "ignored prefix " },
                    { "type": "output_text", "text": "[{\"type\":\"caption\",\"x\":0,\"y\":90,\"width\":100,\"height\":8}]" }
                ]
            }]
        }"#,
        640,
        480,
    );

    assert_eq!(detections.len(), 1);
    assert_eq!(detections[0].region_type, VisionLayoutRegionType::Caption);
    assert_close(detections[0].y, 432.0);
    assert_close(detections[0].height, 38.4);
    assert_close_f32(detections[0].confidence, 0.8);
}

#[test]
fn vision_layout_response_parser_returns_empty_for_malformed_json() {
    assert!(parse_vision_layout_detection_array("{nope", 100, 100).is_empty());
    assert!(parse_vision_layout_response(
        OpenAiApiFormat::ChatCompletions,
        r#"{ "choices": [{ "message": { "content": "no array here" } }] }"#,
        100,
        100,
    )
    .is_empty());
    assert!(parse_vision_layout_response(
        OpenAiApiFormat::Responses,
        r#"{ "output_text": "[not valid json]" }"#,
        100,
        100,
    )
    .is_empty());
}

#[test]
fn vision_layout_response_result_surfaces_malformed_provider_payloads() {
    let malformed_json =
        parse_vision_layout_response_result(OpenAiApiFormat::ChatCompletions, "{nope", 100, 100)
            .expect_err("malformed provider JSON should be a backend error");
    assert_eq!(
        malformed_json.code,
        OpenAiExecutionErrorCode::InvalidResponse
    );
    assert!(malformed_json.message.contains("response JSON is invalid"));

    let missing_content = parse_vision_layout_response_result(
        OpenAiApiFormat::ChatCompletions,
        r#"{ "choices": [{ "message": { "content": "" } }] }"#,
        100,
        100,
    )
    .expect_err("missing layout text should be a backend error");
    assert_eq!(
        missing_content.code,
        OpenAiExecutionErrorCode::InvalidResponse
    );
    assert!(missing_content
        .message
        .contains("did not contain layout text"));

    let malformed_detection_json = parse_vision_layout_response_result(
        OpenAiApiFormat::Responses,
        r#"{ "output_text": "[not valid json]" }"#,
        100,
        100,
    )
    .expect_err("malformed detection array should be a backend error");
    assert_eq!(
        malformed_detection_json.code,
        OpenAiExecutionErrorCode::InvalidResponse
    );
    assert!(malformed_detection_json
        .message
        .contains("detection array JSON is invalid"));
}

#[test]
fn vision_layout_request_plan_uses_chat_completions_image_url_payload() {
    let config =
        OpenAiCompatibleConfig::new("https://api.example.test/v1/chat/completions", "gpt-4o")
            .with_api_key(" sk-test ");

    let plan = build_vision_layout_request_plan(&config, "data:image/bmp;base64,BMP");

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.content_type, "application/json");
    assert_eq!(plan.api_format, OpenAiApiFormat::ChatCompletions);
    assert_eq!(authorization_header(&plan.headers), Some("Bearer sk-test"));
    assert_eq!(plan.body["model"].as_str(), Some("gpt-4o"));
    assert_eq!(plan.body["max_tokens"].as_u64(), Some(4096));
    assert_close(plan.body["temperature"].as_f64().expect("temperature"), 0.1);
    assert_eq!(
        plan.body["messages"][0]["content"][0]["text"].as_str(),
        Some(VISION_LAYOUT_DETECTION_PROMPT)
    );
    assert_eq!(
        plan.body["messages"][0]["content"][1]["image_url"]["url"].as_str(),
        Some("data:image/bmp;base64,BMP")
    );
}

#[test]
fn vision_layout_request_plan_uses_responses_input_image_payload() {
    let config = OpenAiCompatibleConfig::new("https://api.example.test/v1/responses", "gpt-vision")
        .with_api_key("sk-responses");

    let plan = build_vision_layout_request_plan(&config, "data:image/jpeg;base64,JPEG");

    assert_eq!(plan.api_format, OpenAiApiFormat::Responses);
    assert_eq!(
        authorization_header(&plan.headers),
        Some("Bearer sk-responses")
    );
    assert_eq!(plan.body["model"].as_str(), Some("gpt-vision"));
    assert_eq!(plan.body["max_output_tokens"].as_u64(), Some(4096));
    assert_eq!(plan.body["store"].as_bool(), Some(false));
    assert_eq!(
        plan.body["input"][0]["content"][0]["type"].as_str(),
        Some("input_text")
    );
    assert_eq!(
        plan.body["input"][0]["content"][0]["text"].as_str(),
        Some(VISION_LAYOUT_DETECTION_PROMPT)
    );
    assert_eq!(
        plan.body["input"][0]["content"][1]["image_url"].as_str(),
        Some("data:image/jpeg;base64,JPEG")
    );
}

#[test]
fn vision_layout_request_plan_omits_authorization_when_api_key_empty_for_local_endpoint() {
    let config = OpenAiCompatibleConfig::new("http://localhost:11434/v1/chat/completions", "llava")
        .without_required_api_key();

    let plan = build_vision_layout_request_plan(&config, "data:image/bmp;base64,BMP");

    assert_eq!(plan.api_format, OpenAiApiFormat::ChatCompletions);
    assert_eq!(authorization_header(&plan.headers), None);
}

#[test]
fn vision_layout_request_plan_from_bgra_uses_bmp_for_chat_and_jpeg_for_responses() {
    let pixel = [0, 0, 255, 255];
    let chat =
        OpenAiCompatibleConfig::new("https://api.example.test/v1/chat/completions", "gpt-4o");
    let chat_plan =
        build_vision_layout_request_plan_from_bgra(&chat, &pixel, 1, 1).expect("chat plan");
    assert!(
        chat_plan.body["messages"][0]["content"][1]["image_url"]["url"]
            .as_str()
            .expect("chat image")
            .starts_with("data:image/bmp;base64,Qk0")
    );

    let responses = OpenAiCompatibleConfig::new("https://api.example.test/v1/responses", "gpt-4o");
    let responses_plan = build_vision_layout_request_plan_from_bgra(&responses, &pixel, 1, 1)
        .expect("responses plan");
    assert!(responses_plan.body["input"][0]["content"][1]["image_url"]
        .as_str()
        .expect("responses image")
        .starts_with("data:image/jpeg;base64,/9j/"));
}

#[test]
fn vision_layout_executor_posts_plan_and_parses_success_response() {
    let config =
        OpenAiCompatibleConfig::new("https://api.example.test/v1/chat/completions", "gpt-4o")
            .with_api_key("sk-vision");
    let mut client = FakeVisionLayoutClient::new(Ok(VisionLayoutHttpResponse {
        status_code: 200,
        reason_phrase: "OK".to_string(),
        body: r#"{
            "choices": [{
                "message": {
                    "content": "[{\"type\":\"table\",\"x\":10,\"y\":20,\"width\":30,\"height\":40,\"confidence\":0.9}]"
                }
            }]
        }"#
        .to_string(),
    }));

    let detections = execute_vision_layout_detection(&mut client, &config, &[0, 0, 255, 255], 1, 1)
        .expect("vision detection");

    assert_eq!(client.requests.len(), 1);
    assert_eq!(
        client.requests[0].endpoint,
        "https://api.example.test/v1/chat/completions"
    );
    assert_eq!(
        authorization_header(&client.requests[0].headers),
        Some("Bearer sk-vision")
    );
    assert_eq!(detections.len(), 1);
    assert_eq!(detections[0].region_type, VisionLayoutRegionType::Table);
}

#[test]
fn vision_layout_executor_maps_http_errors() {
    let config =
        OpenAiCompatibleConfig::new("https://api.example.test/v1/chat/completions", "gpt-4o")
            .with_api_key("bad-key");
    let mut client = FakeVisionLayoutClient::new(Ok(VisionLayoutHttpResponse {
        status_code: 401,
        reason_phrase: "Unauthorized".to_string(),
        body: "invalid key".to_string(),
    }));

    let error = execute_vision_layout_detection(&mut client, &config, &[0, 0, 255, 255], 1, 1)
        .expect_err("HTTP error");

    assert_eq!(error.code, OpenAiExecutionErrorCode::InvalidApiKey);
    assert!(error.message.contains("invalid key"));
}

#[test]
fn vision_layout_executor_surfaces_malformed_success_response() {
    let config =
        OpenAiCompatibleConfig::new("https://api.example.test/v1/chat/completions", "gpt-4o")
            .with_api_key("sk-vision");
    let mut client = FakeVisionLayoutClient::new(Ok(VisionLayoutHttpResponse {
        status_code: 200,
        reason_phrase: "OK".to_string(),
        body: r#"{ "choices": [{ "message": { "content": "[not valid json]" } }] }"#.to_string(),
    }));

    let error = execute_vision_layout_detection(&mut client, &config, &[0, 0, 255, 255], 1, 1)
        .expect_err("malformed provider success response should surface");

    assert_eq!(error.code, OpenAiExecutionErrorCode::InvalidResponse);
    assert!(error.message.contains("detection array JSON is invalid"));
}

#[test]
fn vision_layout_executor_requires_api_key_unless_config_allows_local_endpoint() {
    let config =
        OpenAiCompatibleConfig::new("https://api.example.test/v1/chat/completions", "gpt-4o");
    let mut client = FakeVisionLayoutClient::new(Ok(VisionLayoutHttpResponse {
        status_code: 200,
        reason_phrase: "OK".to_string(),
        body: "{}".to_string(),
    }));

    let error = execute_vision_layout_detection(&mut client, &config, &[0, 0, 255, 255], 1, 1)
        .expect_err("missing key");

    assert_eq!(error.code, OpenAiExecutionErrorCode::InvalidApiKey);
    assert!(client.requests.is_empty());

    let local_config =
        OpenAiCompatibleConfig::new("http://localhost:11434/v1/chat/completions", "llava")
            .without_required_api_key();
    client.response = Ok(VisionLayoutHttpResponse {
        status_code: 200,
        reason_phrase: "OK".to_string(),
        body: r#"{ "choices": [{ "message": { "content": "[]" } }] }"#.to_string(),
    });
    let result =
        execute_vision_layout_detection(&mut client, &local_config, &[0, 0, 255, 255], 1, 1)
            .expect("local endpoint can omit key");
    assert!(result.is_empty());
    assert_eq!(authorization_header(&client.requests[0].headers), None);
}

#[test]
fn vision_layout_real_provider_smoke_when_env_configured() {
    let Some(mut config) = smoke_vision_config() else {
        eprintln!(
            "skipping real Vision layout smoke; set {VISION_LAYOUT_SMOKE_ENDPOINT_ENV} and {VISION_LAYOUT_SMOKE_MODEL_ENV}"
        );
        return;
    };
    if let Some(api_key) = optional_env(VISION_LAYOUT_SMOKE_API_KEY_ENV) {
        config = config.with_api_key(api_key);
    } else if is_local_vision_endpoint(&config.endpoint) {
        config = config.without_required_api_key();
    } else {
        panic!(
            "{VISION_LAYOUT_SMOKE_API_KEY_ENV} must be set for non-local Vision layout endpoint {}",
            config.endpoint
        );
    }

    let mut client = ReqwestVisionLayoutHttpClient::from_settings(&SettingsSnapshot::default())
        .expect("real Vision layout HTTP client should build");
    let width = 256u32;
    let height = 160u32;
    let bgra = synthetic_document_bgra(width as usize, height as usize);

    let detections = execute_vision_layout_detection(&mut client, &config, &bgra, width, height)
        .expect("real Vision layout endpoint should return a parseable response");

    assert_vision_detection_bounds(&detections, f64::from(width), f64::from(height));
}

struct FakeVisionLayoutClient {
    response: Result<VisionLayoutHttpResponse, OpenAiExecutionError>,
    requests: Vec<VisionLayoutHttpRequestPlan>,
}

impl FakeVisionLayoutClient {
    fn new(response: Result<VisionLayoutHttpResponse, OpenAiExecutionError>) -> Self {
        Self {
            response,
            requests: Vec::new(),
        }
    }
}

impl VisionLayoutHttpClient for FakeVisionLayoutClient {
    fn post_json(
        &mut self,
        request: &VisionLayoutHttpRequestPlan,
    ) -> Result<VisionLayoutHttpResponse, OpenAiExecutionError> {
        self.requests.push(request.clone());
        self.response.clone()
    }
}

fn authorization_header(headers: &[(String, String)]) -> Option<&str> {
    headers
        .iter()
        .find(|(name, _)| name.eq_ignore_ascii_case("authorization"))
        .map(|(_, value)| value.as_str())
}

fn smoke_vision_config() -> Option<OpenAiCompatibleConfig> {
    let endpoint = optional_env(VISION_LAYOUT_SMOKE_ENDPOINT_ENV);
    let model = optional_env(VISION_LAYOUT_SMOKE_MODEL_ENV);
    if endpoint.is_none() && model.is_none() {
        return None;
    }

    let endpoint = endpoint.unwrap_or_else(|| {
        panic!(
            "{VISION_LAYOUT_SMOKE_ENDPOINT_ENV} must be set when {VISION_LAYOUT_SMOKE_MODEL_ENV} is set"
        )
    });
    let model = model.unwrap_or_else(|| {
        panic!(
            "{VISION_LAYOUT_SMOKE_MODEL_ENV} must be set when {VISION_LAYOUT_SMOKE_ENDPOINT_ENV} is set"
        )
    });
    Some(OpenAiCompatibleConfig::new(endpoint, model))
}

fn optional_env(name: &str) -> Option<String> {
    std::env::var(name)
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

fn is_local_vision_endpoint(endpoint: &str) -> bool {
    let endpoint = endpoint.to_ascii_lowercase();
    endpoint.contains("://localhost")
        || endpoint.contains("://127.0.0.1")
        || endpoint.contains("://[::1]")
}

fn synthetic_document_bgra(width: usize, height: usize) -> Vec<u8> {
    let mut bgra = vec![255u8; width * height * 4];
    fill_rect(&mut bgra, width, 24, 20, 208, 20);
    fill_rect(&mut bgra, width, 32, 58, 192, 16);
    fill_rect(&mut bgra, width, 32, 90, 192, 16);
    fill_rect(&mut bgra, width, 68, 124, 120, 20);
    bgra
}

fn fill_rect(bgra: &mut [u8], image_width: usize, x: usize, y: usize, width: usize, height: usize) {
    let image_height = bgra.len() / image_width / 4;
    for row in y..(y + height).min(image_height) {
        for column in x..(x + width).min(image_width) {
            let offset = (row * image_width + column) * 4;
            bgra[offset..offset + 4].copy_from_slice(&[0, 0, 0, 255]);
        }
    }
}

fn assert_vision_detection_bounds(
    detections: &[VisionLayoutDetection],
    image_width: f64,
    image_height: f64,
) {
    for detection in detections {
        assert!(detection.confidence.is_finite());
        assert!(detection.x.is_finite());
        assert!(detection.y.is_finite());
        assert!(detection.width.is_finite());
        assert!(detection.height.is_finite());
        assert!(detection.x >= 0.0);
        assert!(detection.y >= 0.0);
        assert!(detection.width >= 0.0);
        assert!(detection.height >= 0.0);
        assert!(detection.x + detection.width <= image_width + 1.0);
        assert!(detection.y + detection.height <= image_height + 1.0);
    }
}

fn assert_close(actual: f64, expected: f64) {
    assert!(
        (actual - expected).abs() < 1e-6,
        "expected {actual} to be close to {expected}"
    );
}

fn assert_close_f32(actual: f32, expected: f32) {
    assert!(
        (actual - expected).abs() < 1e-6,
        "expected {actual} to be close to {expected}"
    );
}
