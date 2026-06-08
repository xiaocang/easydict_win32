use easydict_app::{
    build_vision_layout_request_plan, build_vision_layout_request_plan_from_bgra,
    execute_vision_layout_detection, parse_vision_layout_detection_array,
    parse_vision_layout_response, OpenAiApiFormat, OpenAiCompatibleConfig, OpenAiExecutionError,
    OpenAiExecutionErrorCode, VisionLayoutHttpClient, VisionLayoutHttpRequestPlan,
    VisionLayoutHttpResponse, VisionLayoutRegionType, VISION_LAYOUT_DETECTION_PROMPT,
};

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
    let result =
        execute_vision_layout_detection(&mut client, &local_config, &[0, 0, 255, 255], 1, 1)
            .expect("local endpoint can omit key");
    assert!(result.is_empty());
    assert_eq!(authorization_header(&client.requests[0].headers), None);
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
