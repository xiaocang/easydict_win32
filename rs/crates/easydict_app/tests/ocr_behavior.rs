use base64::{engine::general_purpose, Engine as _};
use easydict_app::protocol::SettingsSnapshot;
use easydict_app::{
    apply_capture_background_result, apply_ocr_outcome, begin_ocr_recognize, bgra_to_base64_bmp,
    bgra_to_base64_jpeg_data_url, build_custom_api_ocr_request, build_ollama_ocr_request,
    group_and_sort_ocr_lines, merge_ocr_lines, merge_ocr_words, merged_ocr_text,
    parse_ocr_http_response, run_ocr_recognize, run_ocr_recognize_with_app_dir,
    run_ocr_recognize_with_current_app_dir,
    windows_native_ocr_availability_with_recognizer, CaptureBackground, CapturePhase, CapturePoint,
    CaptureRect, DetectedWindow, EasydictApp, EasydictUiState, Message, NativeOcrBackend,
    OcrAvailabilityDto, OcrBackend, OcrBackendError, OcrCaptureResult, OcrEngineConfig,
    OcrEngineKind, OcrHttpClient, OcrHttpRequestPlan, OcrHttpResponseParser, OcrImageEncodeError,
    OcrLanguageDto, OcrLineDto, OcrMode, OcrOutcome, OcrRecognizeParams, OcrRectDto, OcrResultDto,
    ScreenWindowRect, ScreenWindowSnapshot, WindowsNativeOcrRecognizer,
};
use serde_json::json;
use std::{
    collections::VecDeque,
    fs,
    io::{Read, Write},
    net::TcpListener,
    path::PathBuf,
    sync::Mutex,
    thread,
    time::Duration,
};
use win_fluent::prelude::{Application, PlatformCommand, Task, WindowCommand};

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

#[test]
fn begin_ocr_recognize_builds_native_request_from_capture() {
    let mut state = EasydictUiState::default();

    let request = begin_ocr_recognize(
        &mut state,
        OcrMode::Translate,
        capture(r"C:\Temp\easydict-ocr.bgra", 16, 9),
    )
    .expect("OCR request should start");

    assert_eq!(request.query_id, 1);
    assert_eq!(request.mode, OcrMode::Translate);
    assert_eq!(request.params.pixel_data_path, r"C:\Temp\easydict-ocr.bgra");
    assert_eq!(request.params.pixel_width, 16);
    assert_eq!(request.params.pixel_height, 9);
    assert_eq!(
        request.params.preferred_language_tag.as_deref(),
        Some("ja-JP")
    );
    assert_eq!(state.next_ocr_query_id, 2);
    assert_eq!(state.active_ocr_query_id, Some(1));
    assert_eq!(state.active_ocr_mode, Some(OcrMode::Translate));
    assert_eq!(state.pending_ocr_mode, None);
    assert_eq!(state.ocr_status_text, "OCR Translate: recognizing text");
}

#[test]
fn begin_ocr_recognize_rejects_missing_pixels_or_dimensions() {
    let mut state = EasydictUiState::default();

    let missing =
        begin_ocr_recognize(&mut state, OcrMode::Translate, capture("   ", 16, 9)).unwrap_err();
    assert_eq!(missing.to_string(), "OCR capture pixel data is missing");
    assert_eq!(state.next_ocr_query_id, 1);
    assert_eq!(state.active_ocr_query_id, None);

    let invalid = begin_ocr_recognize(
        &mut state,
        OcrMode::SilentClipboard,
        capture(r"C:\Temp\easydict-ocr.bgra", 0, 9),
    )
    .unwrap_err();
    assert_eq!(invalid.to_string(), "OCR capture dimensions are invalid");
    assert_eq!(state.next_ocr_query_id, 1);
}

#[test]
fn begin_ocr_recognize_uses_settings_language_when_capture_has_no_preference() {
    let mut state = EasydictUiState::default();
    state.settings.ocr_language = "ko".to_string();

    let request = begin_ocr_recognize(
        &mut state,
        OcrMode::Translate,
        OcrCaptureResult::new(r"C:\Temp\easydict-ocr.bgra", 16, 9),
    )
    .expect("OCR should start");

    assert_eq!(request.params.preferred_language_tag.as_deref(), Some("ko"));

    let request = begin_ocr_recognize(
        &mut state,
        OcrMode::Translate,
        capture(r"C:\Temp\easydict-ocr.bgra", 16, 9),
    )
    .expect("OCR should start with explicit capture language");

    assert_eq!(
        request.params.preferred_language_tag.as_deref(),
        Some("ja-JP")
    );
}

#[test]
fn ocr_engine_config_resolves_snapshot_defaults_and_normalizes_kind() {
    let default = OcrEngineConfig::from_settings(&SettingsSnapshot::default());
    assert_eq!(default.kind, OcrEngineKind::WindowsNative);
    assert_eq!(default.endpoint, "http://localhost:11434/api/generate");
    assert_eq!(default.model, "glm-ocr");
    assert_eq!(
        default.system_prompt,
        "Extract all the text from this image perfectly. Output ONLY the extracted text, without any conversational filler, markdown formatting, or introductory words."
    );
    assert_eq!(default.api_key, None);
    assert_eq!(default.language, None);

    let custom = OcrEngineConfig::from_settings(&SettingsSnapshot {
        ocr_engine: Some("custom-api".to_string()),
        ocr_api_key: Some(" key ".to_string()),
        ocr_endpoint: Some(" https://ocr.example.test/v1/responses ".to_string()),
        ocr_model: Some(" vision ".to_string()),
        ocr_system_prompt: Some(" prompt ".to_string()),
        ocr_language: Some("auto".to_string()),
        ..Default::default()
    });

    assert_eq!(custom.kind, OcrEngineKind::CustomApi);
    assert_eq!(custom.api_key.as_deref(), Some("key"));
    assert_eq!(custom.endpoint, "https://ocr.example.test/v1/responses");
    assert_eq!(custom.model, "vision");
    assert_eq!(custom.system_prompt, "prompt");
    assert_eq!(custom.language, None);

    let ollama = OcrEngineConfig::from_settings(&SettingsSnapshot {
        ocr_engine: Some("OLLAMA".to_string()),
        ocr_language: Some(" zh-CN ".to_string()),
        ..Default::default()
    });

    assert_eq!(ollama.kind, OcrEngineKind::Ollama);
    assert_eq!(ollama.endpoint, "http://localhost:11434/api/generate");
    assert_eq!(ollama.model, "glm-ocr");
    assert_eq!(ollama.language.as_deref(), Some("zh-CN"));
}

#[test]
fn ollama_ocr_request_matches_legacy_generate_shape() {
    let config = OcrEngineConfig::from_settings(&SettingsSnapshot {
        ocr_engine: Some("Ollama".to_string()),
        ocr_endpoint: Some("http://localhost:11434/api/generate".to_string()),
        ocr_model: Some("glm-ocr".to_string()),
        ocr_system_prompt: Some("Extract text only.".to_string()),
        ..Default::default()
    });

    let plan = build_ollama_ocr_request(&config, "BASE64BMP");

    assert_eq!(plan.endpoint, "http://localhost:11434/api/generate");
    assert_eq!(plan.authorization_bearer, None);
    assert_eq!(plan.response_parser, OcrHttpResponseParser::OllamaGenerate);
    assert_eq!(plan.body["model"].as_str(), Some("glm-ocr"));
    assert_eq!(plan.body["prompt"].as_str(), Some("Extract text only."));
    assert_eq!(plan.body["images"][0].as_str(), Some("BASE64BMP"));
    assert_eq!(plan.body["stream"].as_bool(), Some(false));
}

#[test]
fn custom_api_ocr_request_uses_chat_or_responses_shape() {
    let responses_config = OcrEngineConfig::from_settings(&SettingsSnapshot {
        ocr_engine: Some("CustomApi".to_string()),
        ocr_api_key: Some("sk-test".to_string()),
        ocr_endpoint: Some("https://api.example.test/v1/responses/".to_string()),
        ocr_model: Some("gpt-vision".to_string()),
        ocr_system_prompt: Some("Read it.".to_string()),
        ..Default::default()
    });
    let responses_plan =
        build_custom_api_ocr_request(&responses_config, "data:image/jpeg;base64,AAA");

    assert_eq!(
        responses_plan.authorization_bearer.as_deref(),
        Some("sk-test")
    );
    assert_eq!(
        responses_plan.response_parser,
        OcrHttpResponseParser::Responses
    );
    assert_eq!(responses_plan.body["model"].as_str(), Some("gpt-vision"));
    assert_eq!(
        responses_plan.body["max_output_tokens"].as_u64(),
        Some(2048)
    );
    assert_eq!(responses_plan.body["store"].as_bool(), Some(false));
    assert_eq!(
        responses_plan.body["input"][0]["content"][0]["type"].as_str(),
        Some("input_text")
    );
    assert_eq!(
        responses_plan.body["input"][0]["content"][0]["text"].as_str(),
        Some("Read it.")
    );
    assert_eq!(
        responses_plan.body["input"][0]["content"][1]["image_url"].as_str(),
        Some("data:image/jpeg;base64,AAA")
    );

    let chat_config = OcrEngineConfig::from_settings(&SettingsSnapshot {
        ocr_engine: Some("CustomApi".to_string()),
        ocr_api_key: Some("sk-chat".to_string()),
        ocr_endpoint: Some("https://api.example.test/v1/chat/completions".to_string()),
        ocr_model: Some("gpt-vision".to_string()),
        ocr_system_prompt: Some("Read it.".to_string()),
        ..Default::default()
    });
    let chat_plan = build_custom_api_ocr_request(&chat_config, "data:image/jpeg;base64,BBB");

    assert_eq!(chat_plan.authorization_bearer.as_deref(), Some("sk-chat"));
    assert_eq!(
        chat_plan.response_parser,
        OcrHttpResponseParser::ChatCompletions
    );
    assert_eq!(
        chat_plan.body["messages"][0]["content"].as_str(),
        Some("Read it.")
    );
    assert_eq!(
        chat_plan.body["messages"][1]["content"][0]["image_url"]["url"].as_str(),
        Some("data:image/jpeg;base64,BBB")
    );
}

#[test]
fn ocr_http_response_parsers_extract_text_and_allow_empty_text_fields() {
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::OllamaGenerate,
            r#"{ "response": " recognized " }"#
        )
        .expect("Ollama response should parse")
        .text,
        "recognized"
    );
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::ChatCompletions,
            r#"{ "choices": [{ "message": { "content": " text " } }] }"#
        )
        .expect("Chat Completions response should parse")
        .text,
        "text"
    );
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::Responses,
            r#"{ "output_text": " direct " }"#
        )
        .expect("Responses output_text should parse")
        .text,
        "direct"
    );
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::Responses,
            r#"{ "output": [{ "content": [{ "text": " first " }, { "text": " second " }] }] }"#
        )
        .expect("Responses output array should parse")
        .text,
        "first  second"
    );
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::OllamaGenerate,
            r#"{ "response": " " }"#
        )
        .expect("present empty text should remain a valid empty OCR result")
        .text,
        ""
    );
}

#[test]
fn ocr_http_response_parsers_surface_malformed_json_and_missing_text_fields() {
    let malformed = parse_ocr_http_response(OcrHttpResponseParser::OllamaGenerate, "{nope")
        .expect_err("malformed OCR provider JSON should be visible");
    assert!(malformed
        .message
        .contains("Could not parse OCR HTTP response"));

    for (parser, json, expected) in [
        (
            OcrHttpResponseParser::OllamaGenerate,
            r#"{ "done": true }"#,
            "missing Ollama response text",
        ),
        (
            OcrHttpResponseParser::ChatCompletions,
            r#"{ "choices": [{ "message": { "role": "assistant" } }] }"#,
            "missing chat completion message content",
        ),
        (
            OcrHttpResponseParser::Responses,
            r#"{ "output": [{ "content": [{ "type": "output_text" }] }] }"#,
            "missing Responses output text",
        ),
    ] {
        let error = parse_ocr_http_response(parser, json)
            .expect_err("missing OCR text field should be visible");
        assert!(
            error.message.contains(expected),
            "expected {expected:?} in {:?}",
            error.message
        );
    }
}

#[test]
fn bgra_to_base64_bmp_validates_and_encodes_bmp_payload() {
    let encoded = bgra_to_base64_bmp(&[0, 0, 255, 255], 1, 1).expect("BMP should encode");

    assert!(encoded.starts_with("Qk0"));
    assert_eq!(
        encoded,
        "Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAD/AA=="
    );
    assert_eq!(encoded.len(), 80);
    let decoded = general_purpose::STANDARD
        .decode(encoded.as_bytes())
        .expect("BMP base64 should decode with the standard padded engine");
    assert_eq!(&decoded[..2], b"BM");
    assert_eq!(decoded.len(), 58);

    assert_eq!(
        bgra_to_base64_bmp(&[0, 0, 255, 255], 0, 1).unwrap_err(),
        OcrImageEncodeError::InvalidDimensions
    );
    assert_eq!(
        bgra_to_base64_bmp(&[0, 0, 255], 1, 1).unwrap_err(),
        OcrImageEncodeError::BufferTooShort {
            expected: 4,
            actual: 3
        }
    );
}

#[test]
fn bgra_to_base64_jpeg_data_url_validates_and_encodes_custom_api_payload() {
    let encoded =
        bgra_to_base64_jpeg_data_url(&[0, 0, 255, 255], 1, 1).expect("JPEG should encode");

    assert!(encoded.starts_with("data:image/jpeg;base64,/9j/"));
    let payload = encoded
        .strip_prefix("data:image/jpeg;base64,")
        .expect("JPEG data URL prefix");
    let decoded = general_purpose::STANDARD
        .decode(payload.as_bytes())
        .expect("JPEG base64 should decode with the standard padded engine");
    assert_eq!(&decoded[..2], &[0xff, 0xd8]);
    assert_eq!(&decoded[decoded.len() - 2..], &[0xff, 0xd9]);

    assert_eq!(
        bgra_to_base64_jpeg_data_url(&[0, 0, 255, 255], 1, 0).unwrap_err(),
        OcrImageEncodeError::InvalidDimensions
    );
    assert_eq!(
        bgra_to_base64_jpeg_data_url(&[0, 0, 255], 1, 1).unwrap_err(),
        OcrImageEncodeError::BufferTooShort {
            expected: 4,
            actual: 3
        }
    );
}

#[test]
fn native_ocr_backend_runs_ollama_provider_from_bgra_file() {
    let path = write_temp_bgra("ollama", &[0, 0, 255, 255]);
    let mut backend = NativeOcrBackend::new(RecordingOcrHttpClient::with_responses([Ok(
        r#"{ "response": " native ollama text " }"#.to_string(),
    )]));
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 7,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("Ollama".to_string()),
            ocr_endpoint: Some("http://localhost:11434/api/generate".to_string()),
            ocr_model: Some("glm-ocr".to_string()),
            ocr_system_prompt: Some("Only OCR.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize(&mut backend, &request);

    fs::remove_file(&path).ok();
    assert_eq!(
        outcome.result.expect("OCR result").text,
        "native ollama text"
    );
    assert_eq!(backend.http_client().requests.len(), 1);
    let plan = &backend.http_client().requests[0];
    assert_eq!(plan.response_parser, OcrHttpResponseParser::OllamaGenerate);
    assert_eq!(plan.authorization_bearer, None);
    assert_eq!(plan.body["model"].as_str(), Some("glm-ocr"));
    assert_eq!(plan.body["prompt"].as_str(), Some("Only OCR."));
    assert!(plan.body["images"][0]
        .as_str()
        .expect("image")
        .starts_with("Qk0"));
}

#[test]
fn native_ocr_backend_runs_custom_api_provider_from_bgra_file() {
    let path = write_temp_bgra("custom", &[0, 0, 255, 255]);
    let mut backend = NativeOcrBackend::new(RecordingOcrHttpClient::with_responses([Ok(
        r#"{ "output_text": " custom api text " }"#.to_string(),
    )]));
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 8,
        mode: OcrMode::SilentClipboard,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("CustomApi".to_string()),
            ocr_api_key: Some("sk-native".to_string()),
            ocr_endpoint: Some("https://api.example.test/v1/responses".to_string()),
            ocr_model: Some("gpt-vision".to_string()),
            ocr_system_prompt: Some("Only OCR.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize(&mut backend, &request);

    fs::remove_file(&path).ok();
    assert_eq!(outcome.result.expect("OCR result").text, "custom api text");
    assert_eq!(backend.http_client().requests.len(), 1);
    let plan = &backend.http_client().requests[0];
    assert_eq!(plan.response_parser, OcrHttpResponseParser::Responses);
    assert_eq!(plan.authorization_bearer.as_deref(), Some("sk-native"));
    assert!(plan.body["input"][0]["content"][1]["image_url"]
        .as_str()
        .expect("image data URL")
        .starts_with("data:image/jpeg;base64,/9j/"));
}

#[test]
fn ocr_http_provider_malformed_json_surfaces_backend_error() {
    let path = write_temp_bgra("ollama-malformed", &[0, 0, 255, 255]);
    let mut backend = NativeOcrBackend::new(RecordingOcrHttpClient::with_responses([Ok(
        "{nope".to_string()
    )]));
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 10,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("Ollama".to_string()),
            ocr_endpoint: Some("http://localhost:11434/api/generate".to_string()),
            ocr_model: Some("glm-ocr".to_string()),
            ocr_system_prompt: Some("Only OCR.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize(&mut backend, &request);

    fs::remove_file(&path).ok();
    let error = outcome
        .result
        .expect_err("malformed provider JSON should fail OCR backend");
    assert!(error.message.contains("Could not parse OCR HTTP response"));
}

#[test]
fn ocr_http_provider_missing_expected_text_surfaces_backend_error() {
    let path = write_temp_bgra("custom-missing-text", &[0, 0, 255, 255]);
    let mut backend = NativeOcrBackend::new(RecordingOcrHttpClient::with_responses([Ok(
        r#"{ "output": [{ "content": [{ "type": "output_text" }] }] }"#.to_string(),
    )]));
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 11,
        mode: OcrMode::SilentClipboard,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("CustomApi".to_string()),
            ocr_api_key: Some("sk-native".to_string()),
            ocr_endpoint: Some("https://api.example.test/v1/responses".to_string()),
            ocr_model: Some("gpt-vision".to_string()),
            ocr_system_prompt: Some("Only OCR.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize(&mut backend, &request);

    fs::remove_file(&path).ok();
    let error = outcome
        .result
        .expect_err("missing provider text field should fail OCR backend");
    assert!(error.message.contains("missing Responses output text"));
}

#[test]
fn native_ocr_backend_runs_windows_native_provider_without_worker() {
    let mut backend = NativeOcrBackend::with_windows_recognizer(
        RecordingOcrHttpClient::with_responses([]),
        RecordingWindowsNativeOcrRecognizer::with_responses([Ok(ocr_result(
            "windows native text",
            Some(("ja-JP", "Japanese")),
        ))]),
    );
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 9,
        mode: OcrMode::Translate,
        params: capture(r"C:\Temp\pixels.bgra", 1, 1).into_params_for_test(),
        settings: SettingsSnapshot {
            ocr_engine: Some("WindowsNative".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize(&mut backend, &request);

    assert_eq!(
        outcome.result.expect("OCR result").text,
        "windows native text"
    );
    assert!(backend.http_client().requests.is_empty());
    assert_eq!(
        backend.windows_recognizer().calls,
        vec![(request.params, Some("ja-JP".to_string()))]
    );
}

#[test]
fn windows_native_ocr_availability_maps_languages_without_recognition() {
    let mut recognizer = RecordingWindowsNativeOcrRecognizer::with_languages([
        ("en-US", "English"),
        ("ja-JP", "Japanese"),
    ]);

    let availability = windows_native_ocr_availability_with_recognizer(&mut recognizer)
        .expect("Windows OCR availability should be queryable");

    assert_eq!(
        availability,
        OcrAvailabilityDto {
            is_available: true,
            available_languages: vec![
                OcrLanguageDto {
                    tag: "en-US".to_string(),
                    display_name: "English".to_string(),
                },
                OcrLanguageDto {
                    tag: "ja-JP".to_string(),
                    display_name: "Japanese".to_string(),
                },
            ],
        }
    );
    assert_eq!(recognizer.is_available_calls, 1);
    assert_eq!(recognizer.available_languages_calls, 1);
    assert!(recognizer.calls.is_empty());
    assert_eq!(
        serde_json::to_value(&availability).expect("availability should serialize"),
        json!({
            "isAvailable": true,
            "availableLanguages": [
                { "tag": "en-US", "displayName": "English" },
                { "tag": "ja-JP", "displayName": "Japanese" },
            ],
        })
    );
}

#[test]
fn windows_native_ocr_availability_skips_language_query_when_unavailable() {
    let mut recognizer = RecordingWindowsNativeOcrRecognizer::unavailable();

    let availability = windows_native_ocr_availability_with_recognizer(&mut recognizer)
        .expect("unavailable Windows OCR should still report status");

    assert_eq!(
        availability,
        OcrAvailabilityDto {
            is_available: false,
            available_languages: Vec::new(),
        }
    );
    assert_eq!(recognizer.is_available_calls, 1);
    assert_eq!(recognizer.available_languages_calls, 0);
    assert!(recognizer.calls.is_empty());
}

#[test]
fn app_dir_runner_uses_native_provider_for_advanced_ocr_engine() {
    let (endpoint, server) =
        serve_one_http_response(r#"{ "response": " routed native provider " }"#);
    let path = write_temp_bgra("routed", &[0, 0, 255, 255]);
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 10,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("Ollama".to_string()),
            ocr_endpoint: Some(endpoint),
            ocr_model: Some("glm-ocr".to_string()),
            ocr_system_prompt: Some("Route natively.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize_with_app_dir(request, r"C:\MissingWorkerApp");
    let http_request = server.join().expect("HTTP test server should finish");

    fs::remove_file(&path).ok();
    assert_eq!(
        outcome.result.expect("OCR result").text,
        "routed native provider"
    );
    assert!(http_request.starts_with("POST /api/generate "));
    assert!(http_request.contains(r#""model":"glm-ocr""#));
    assert!(http_request.contains(r#""prompt":"Route natively.""#));
}

#[test]
fn app_dir_runner_uses_ollama_provider_without_legacy_ocr_worker_probe() {
    let (endpoint, server) = serve_one_http_response(r#"{ "response": " ollama native text " }"#);
    let app_dir = write_temp_legacy_ocr_app_dir("ollama_no_legacy_runtime");
    let path = write_temp_bgra("ollama_app_dir", &[0, 255, 0, 255]);
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 13,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("Ollama".to_string()),
            ocr_endpoint: Some(endpoint),
            ocr_model: Some("glm-ocr".to_string()),
            ocr_system_prompt: Some("Read via Ollama.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize_with_app_dir(request, &app_dir);
    let http_request = server.join().expect("HTTP test server should finish");

    fs::remove_file(&path).ok();
    fs::remove_dir_all(&app_dir).ok();
    assert_eq!(
        outcome.result.expect("OCR result").text,
        "ollama native text"
    );
    assert!(http_request.starts_with("POST /api/generate "));
    assert!(http_request.contains(r#""model":"glm-ocr""#));
    assert!(http_request.contains(r#""prompt":"Read via Ollama.""#));
    assert!(!http_request.contains("Easydict.Workers.Ocr"));
    assert!(!http_request.contains("CompatHost"));
}

#[test]
fn app_dir_runner_uses_custom_api_provider_without_legacy_ocr_worker_probe() {
    let (endpoint, server) = serve_one_http_response(
        r#"{ "choices": [{ "message": { "content": " custom api native text " } }] }"#,
    );
    let custom_endpoint = endpoint.replace("/api/generate", "/v1/chat/completions");
    let app_dir = write_temp_legacy_ocr_app_dir("custom_api_no_legacy_runtime");
    let path = write_temp_bgra("custom_api_app_dir", &[255, 0, 0, 255]);
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 12,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("CustomApi".to_string()),
            ocr_api_key: Some("sk-ocr".to_string()),
            ocr_endpoint: Some(custom_endpoint),
            ocr_model: Some("gpt-vision".to_string()),
            ocr_system_prompt: Some("Read via custom API.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize_with_app_dir(request, &app_dir);
    let http_request = server.join().expect("HTTP test server should finish");

    fs::remove_file(&path).ok();
    fs::remove_dir_all(&app_dir).ok();
    assert_eq!(
        outcome.result.expect("OCR result").text,
        "custom api native text"
    );
    assert!(http_request.starts_with("POST /v1/chat/completions "));
    assert!(contains_http_header(
        &http_request,
        "authorization",
        "bearer sk-ocr"
    ));
    assert!(http_request.contains(r#""model":"gpt-vision""#));
    assert!(http_request.contains(r#""content":"Read via custom API.""#));
    assert!(!http_request.contains("Easydict.Workers.Ocr"));
    assert!(!http_request.contains("CompatHost"));
}

#[test]
fn current_app_dir_runner_uses_custom_api_provider_despite_hybrid_runtime_profile() {
    let _environment = ENVIRONMENT_LOCK.lock().expect("environment lock");
    let _runtime_profile = EnvironmentVariableGuard::set("EASYDICT_RUNTIME_PROFILE", "hybrid");
    let _generic_runtime_profile = EnvironmentVariableGuard::set("RUNTIME_PROFILE", "hybrid");
    let (endpoint, server) = serve_one_http_response(
        r#"{ "choices": [{ "message": { "content": " current app dir native text " } }] }"#,
    );
    let custom_endpoint = endpoint.replace("/api/generate", "/v1/chat/completions");
    let path = write_temp_bgra("custom_api_current_app_dir", &[0, 0, 255, 255]);
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 14,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("CustomApi".to_string()),
            ocr_api_key: Some("sk-current-ocr".to_string()),
            ocr_endpoint: Some(custom_endpoint),
            ocr_model: Some("gpt-vision-current".to_string()),
            ocr_system_prompt: Some("Read via current app dir.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize_with_current_app_dir(request);
    let http_request = server.join().expect("HTTP test server should finish");

    fs::remove_file(&path).ok();
    assert_eq!(
        outcome.result.expect("OCR result").text,
        "current app dir native text"
    );
    assert!(http_request.starts_with("POST /v1/chat/completions "));
    assert!(contains_http_header(
        &http_request,
        "authorization",
        "bearer sk-current-ocr"
    ));
    assert!(http_request.contains(r#""model":"gpt-vision-current""#));
    assert!(http_request.contains(r#""content":"Read via current app dir.""#));
    assert!(!http_request.contains("Easydict.Workers.Ocr"));
    assert!(!http_request.contains("CompatHost"));
}

#[test]
fn current_app_dir_runner_uses_ollama_provider_despite_hybrid_runtime_profile() {
    let _environment = ENVIRONMENT_LOCK.lock().expect("environment lock");
    let _runtime_profile = EnvironmentVariableGuard::set("EASYDICT_RUNTIME_PROFILE", "hybrid");
    let _generic_runtime_profile = EnvironmentVariableGuard::set("RUNTIME_PROFILE", "hybrid");
    let (endpoint, server) =
        serve_one_http_response(r#"{ "response": " current app dir ollama native text " }"#);
    let path = write_temp_bgra("ollama_current_app_dir", &[255, 255, 0, 255]);
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 15,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("Ollama".to_string()),
            ocr_endpoint: Some(endpoint),
            ocr_model: Some("llava-current".to_string()),
            ocr_system_prompt: Some("Read via current app dir Ollama.".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize_with_current_app_dir(request);
    let http_request = server.join().expect("HTTP test server should finish");

    fs::remove_file(&path).ok();
    assert_eq!(
        outcome.result.expect("OCR result").text,
        "current app dir ollama native text"
    );
    assert!(http_request.starts_with("POST /api/generate "));
    assert!(http_request.contains(r#""model":"llava-current""#));
    assert!(http_request.contains(r#""prompt":"Read via current app dir Ollama.""#));
    assert!(!http_request.contains("Easydict.Workers.Ocr"));
    assert!(!http_request.contains("CompatHost"));
}

#[test]
fn current_app_dir_runner_uses_default_windows_native_despite_hybrid_profile_and_stale_worker_markers(
) {
    let _environment = ENVIRONMENT_LOCK.lock().expect("environment lock");
    let _runtime_profile = EnvironmentVariableGuard::set("EASYDICT_RUNTIME_PROFILE", "hybrid");
    let _generic_runtime_profile = EnvironmentVariableGuard::set("RUNTIME_PROFILE", "hybrid");
    let _legacy_markers = write_current_app_dir_legacy_ocr_markers();
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 16,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: r"C:\Missing\current-app-dir-pixels.bgra".to_string(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot::default(),
    };

    let outcome = run_ocr_recognize_with_current_app_dir(request);
    let error = outcome.result.unwrap_err().message;
    let lower_error = error.to_ascii_lowercase();

    assert!(error.contains("Could not read OCR pixel data"));
    assert!(!lower_error.contains("worker"));
    assert!(!lower_error.contains("fallback"));
    assert!(!error.contains("CompatHost"));
    assert!(!error.contains(".NET"));
    assert!(!lower_error.contains("dotnet"));
}

#[test]
fn app_dir_runner_uses_native_windows_ocr_without_legacy_runtime() {
    let app_dir = write_temp_legacy_ocr_app_dir("native_windows_no_legacy_runtime");
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 11,
        mode: OcrMode::Translate,
        params: OcrRecognizeParams {
            pixel_data_path: r"C:\Missing\pixels.bgra".to_string(),
            pixel_width: 1,
            pixel_height: 1,
            preferred_language_tag: None,
        },
        settings: SettingsSnapshot {
            ocr_engine: Some("WindowsNative".to_string()),
            ..Default::default()
        },
    };

    let outcome = run_ocr_recognize_with_app_dir(request, &app_dir);
    let error = outcome.result.unwrap_err().message;

    fs::remove_dir_all(&app_dir).ok();
    assert!(error.contains("Could not read OCR pixel data"));
    assert!(!error.contains("OCR worker"));
    assert!(!error.contains("CompatHost"));
}

#[test]
fn run_ocr_recognize_configures_backend_and_forwards_params() {
    let mut backend = RecordingOcrBackend::with_responses([Ok(ocr_result(
        "recognized text",
        Some(("en-US", "English")),
    ))]);
    let request = easydict_app::OcrRecognizeRequest {
        query_id: 42,
        mode: OcrMode::SilentClipboard,
        params: capture(r"C:\Temp\pixels.bgra", 4, 3).into_params_for_test(),
        settings: SettingsSnapshot::default(),
    };

    let outcome = run_ocr_recognize(&mut backend, &request);

    assert_eq!(backend.configure_calls.len(), 1);
    assert_eq!(backend.recognize_calls, vec![request.params]);
    assert_eq!(outcome.query_id, 42);
    assert_eq!(outcome.mode, OcrMode::SilentClipboard);
    assert_eq!(outcome.result.expect("OCR result").text, "recognized text");
}

#[test]
fn merge_ocr_words_matches_cjk_latin_spacing_contract() {
    assert_eq!(merge_ocr_words::<&str>(&[]), "");
    assert_eq!(merge_ocr_words(&["Hello"]), "Hello");
    assert_eq!(
        merge_ocr_words(&["Hello", "World", "Test"]),
        "Hello World Test"
    );
    assert_eq!(merge_ocr_words(&["你", "好", "世", "界"]), "你好世界");
    assert_eq!(merge_ocr_words(&["你好", "世界"]), "你好世界");
    assert_eq!(merge_ocr_words(&["Hello", "你好"]), "Hello 你好");
    assert_eq!(merge_ocr_words(&["你好", "World"]), "你好 World");
    assert_eq!(merge_ocr_words(&["こん", "にち", "は"]), "こんにちは");
    assert_eq!(merge_ocr_words(&["カタ", "カナ"]), "カタカナ");
    assert_eq!(merge_ocr_words(&["안녕", "하세요"]), "안녕하세요");
    assert_eq!(merge_ocr_words(&["Hello", "", "World"]), "HelloWorld");
    assert_eq!(merge_ocr_words(&["（", "测试", "）"]), "（测试）");
    assert_eq!(merge_ocr_words(&["你好", "。", "世界"]), "你好。世界");
}

#[test]
fn merge_ocr_lines_preserves_line_breaks_and_empty_lines() {
    assert_eq!(merge_ocr_lines(&[]), "");
    assert_eq!(
        merge_ocr_lines(&[line("Hello World", 0.0, 0.0, 10.0, 10.0)]),
        "Hello World"
    );
    assert_eq!(
        merge_ocr_lines(&[
            line("Line 1", 0.0, 0.0, 10.0, 10.0),
            line("Line 2", 0.0, 20.0, 10.0, 10.0),
            line("Line 3", 0.0, 40.0, 10.0, 10.0),
        ]),
        "Line 1\r\nLine 2\r\nLine 3"
    );
    assert_eq!(
        merge_ocr_lines(&[
            line("Before", 0.0, 0.0, 10.0, 10.0),
            line("", 0.0, 20.0, 10.0, 10.0),
            line("After", 0.0, 40.0, 10.0, 10.0),
        ]),
        "Before\r\n\r\nAfter"
    );
}

#[test]
fn group_and_sort_ocr_lines_orders_visual_rows_left_to_right() {
    assert!(group_and_sort_ocr_lines(&[], 0.5).is_empty());

    let single = [line("Only", 10.0, 10.0, 100.0, 20.0)];
    assert_eq!(group_and_sort_ocr_lines(&single, 0.5), single);

    let same_row = [
        line("Right", 200.0, 10.0, 100.0, 20.0),
        line("Left", 10.0, 10.0, 100.0, 20.0),
    ];
    assert_eq!(
        group_and_sort_ocr_lines(&same_row, 0.5)
            .iter()
            .map(|line| line.text.as_str())
            .collect::<Vec<_>>(),
        ["Left", "Right"]
    );

    let columns = [
        line("D", 200.0, 50.0, 100.0, 20.0),
        line("A", 10.0, 10.0, 100.0, 20.0),
        line("C", 10.0, 50.0, 100.0, 20.0),
        line("B", 200.0, 10.0, 100.0, 20.0),
    ];
    assert_eq!(
        group_and_sort_ocr_lines(&columns, 0.5)
            .iter()
            .map(|line| line.text.as_str())
            .collect::<Vec<_>>(),
        ["A", "B", "C", "D"]
    );

    let slight_y_variation = [
        line("Word2", 150.0, 15.0, 80.0, 20.0),
        line("Word1", 10.0, 10.0, 80.0, 20.0),
    ];
    assert_eq!(
        group_and_sort_ocr_lines(&slight_y_variation, 0.5)
            .iter()
            .map(|line| line.text.as_str())
            .collect::<Vec<_>>(),
        ["Word1", "Word2"]
    );

    assert_eq!(
        group_and_sort_ocr_lines(&slight_y_variation, 0.1)
            .iter()
            .map(|line| line.text.as_str())
            .collect::<Vec<_>>(),
        ["Word1", "Word2"]
    );

    let zero_height = [
        line("B", 150.0, 10.0, 80.0, 0.0),
        line("A", 10.0, 10.0, 80.0, 0.0),
    ];
    assert_eq!(
        group_and_sort_ocr_lines(&zero_height, 0.5)
            .iter()
            .map(|line| line.text.as_str())
            .collect::<Vec<_>>(),
        ["A", "B"]
    );
}

#[test]
fn merged_ocr_text_prefers_backend_text_and_falls_back_to_sorted_lines() {
    let with_text = OcrResultDto {
        text: "backend text".to_string(),
        lines: vec![line("line text", 0.0, 0.0, 10.0, 10.0)],
        detected_language: None,
        text_angle: None,
    };
    assert_eq!(merged_ocr_text(&with_text), "backend text");

    let line_fallback = OcrResultDto {
        text: String::new(),
        lines: vec![
            line("Bottom", 0.0, 40.0, 10.0, 10.0),
            line("Right", 100.0, 0.0, 10.0, 10.0),
            line("Left", 0.0, 0.0, 10.0, 10.0),
        ],
        detected_language: None,
        text_angle: None,
    };
    assert_eq!(merged_ocr_text(&line_fallback), "Left\r\nRight\r\nBottom");
}

#[test]
fn app_ocr_capture_finished_starts_native_ocr_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::OcrCaptureFinished(capture(
        r"C:\Temp\pixels.bgra",
        20,
        10,
    )));

    assert_eq!(app.state.active_ocr_query_id, Some(1));
    assert_eq!(app.state.active_ocr_mode, Some(OcrMode::Translate));
    assert_eq!(app.state.ocr_status_text, "OCR Translate: recognizing text");
    assert!(contains_future_task(&task));
    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::Hide(id) if id.as_str() == "capture-overlay"
    )));
}

#[test]
fn app_ocr_capture_failure_surfaces_native_screen_capture_error() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));
    app.update(Message::CaptureLeftButtonDown(CapturePoint::new(10, 10)));
    app.update(Message::CaptureMouseMoved(CapturePoint::new(40, 40)));
    app.update(Message::CaptureSelectionChanged(Some(CaptureRect::new(
        10, 10, 40, 40,
    ))));

    let task = app.update(Message::OcrCaptureFailed {
        mode: OcrMode::Translate,
        error: "invalid screen capture request: region width overflow".to_string(),
    });

    assert_eq!(app.state.pending_ocr_mode, None);
    assert_eq!(app.state.active_ocr_query_id, None);
    assert_eq!(app.state.active_ocr_mode, None);
    assert_eq!(app.state.capture_selection, None);
    assert_eq!(app.state.capture_interaction.phase, CapturePhase::Detecting);
    assert_eq!(
        app.state.ocr_status_text,
        "OCR Translate capture failed: invalid screen capture request: region width overflow"
    );
    assert_eq!(
        app.state.last_ocr_error.as_deref(),
        Some("invalid screen capture request: region width overflow")
    );
    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::Hide(id) if id.as_str() == "capture-overlay"
    )));
}

#[test]
fn capture_overlay_confirm_without_selection_waits_for_region() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));

    let task = app.update(Message::ConfirmCapture);

    assert_eq!(app.state.pending_ocr_mode, Some(OcrMode::Translate));
    assert_eq!(app.state.ocr_status_text, "Select a region before OCR");
    assert!(!contains_capture_screen_region_task(&task));
}

#[test]
fn ocr_hotkey_captures_window_snapshot_for_double_click_detection() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));

    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "capture-overlay"
    )));

    assert!(contains_future_task(&task));
    app.update(Message::CaptureWindowsSnapshotFinished(Ok(
        easydict_app::detected_windows_from_screen_windows(vec![
            ScreenWindowSnapshot::new(1, None, ScreenWindowRect::new(0, 0, 500, 400))
                .class_name("Top"),
            ScreenWindowSnapshot::new(2, Some(1), ScreenWindowRect::new(40, 30, 160, 120))
                .class_name("Child"),
        ]),
    )));

    assert_eq!(
        app.state
            .capture_window_detector
            .find_region_at_point(CapturePoint::new(60, 50), 0),
        Some(CaptureRect::new(40, 30, 200, 150))
    );
    assert_eq!(
        app.state
            .capture_window_detector
            .find_region_at_point(CapturePoint::new(60, 50), 1),
        Some(CaptureRect::new(0, 0, 500, 400))
    );
}

#[test]
fn capture_window_snapshot_failure_preserves_manual_region_capture() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));
    let task = app.update(Message::CaptureWindowsSnapshotFinished(Err(
        "EnumWindows failed with native error 5".to_string(),
    )));

    assert!(matches!(task, Task::None));
    assert_eq!(app.state.pending_ocr_mode, Some(OcrMode::Translate));
    assert_eq!(
        app.state.ocr_status_text,
        "Select a region for OCR Translate"
    );
    assert_eq!(
        app.state.last_ocr_error.as_deref(),
        Some("Screen window snapshot failed: EnumWindows failed with native error 5")
    );
    assert_eq!(
        app.state
            .capture_window_detector
            .find_region_at_point(CapturePoint::new(60, 50), 0),
        None
    );

    // Drag without releasing: releasing now confirms immediately, so an active
    // (in-progress) drag stays in the selecting phase.
    app.update(Message::CaptureLeftButtonDown(CapturePoint::new(10, 10)));
    app.update(Message::CaptureMouseMoved(CapturePoint::new(40, 40)));

    assert_eq!(app.state.capture_interaction.phase, CapturePhase::Selecting);
    assert_eq!(
        app.state.capture_selection,
        Some(CaptureRect::new(10, 10, 40, 40))
    );

    app.update(Message::CaptureWindowsSnapshotFinished(Ok(
        easydict_app::detected_windows_from_screen_windows(vec![ScreenWindowSnapshot::new(
            1,
            None,
            ScreenWindowRect::new(0, 0, 500, 400),
        )]),
    )));
    assert_eq!(app.state.last_ocr_error, None);
}

#[test]
fn capture_background_failure_preserves_overlay_and_success_clears_only_background_error() {
    let mut state = EasydictUiState::default();

    apply_capture_background_result(
        &mut state,
        Err("BitBlt failed with native error 5".to_string()),
    );

    assert_eq!(state.capture_background, None);
    assert_eq!(
        state.last_ocr_error.as_deref(),
        Some("Screen capture background failed: BitBlt failed with native error 5")
    );

    let background = CaptureBackground {
        bgra_path: r"C:\Temp\easydict-capture.bgra".to_string(),
        pixel_width: 1920,
        pixel_height: 1080,
        scale_factor: 1.0,
    };
    apply_capture_background_result(&mut state, Ok(background.clone()));

    assert_eq!(state.capture_background, Some(background));
    assert_eq!(state.last_ocr_error, None);

    state.last_ocr_error = Some("Screen window snapshot failed: EnumWindows failed".to_string());
    apply_capture_background_result(
        &mut state,
        Ok(CaptureBackground {
            bgra_path: r"C:\Temp\easydict-capture-2.bgra".to_string(),
            pixel_width: 100,
            pixel_height: 80,
            scale_factor: 1.0,
        }),
    );

    assert_eq!(
        state.last_ocr_error.as_deref(),
        Some("Screen window snapshot failed: EnumWindows failed")
    );
}

#[test]
fn capture_window_snapshot_does_not_reset_active_drag_selection() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    let task = app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));

    app.update(Message::CaptureLeftButtonDown(CapturePoint::new(10, 10)));
    app.update(Message::CaptureMouseMoved(CapturePoint::new(40, 40)));
    assert_eq!(app.state.capture_interaction.phase, CapturePhase::Selecting);
    assert_eq!(
        app.state.capture_selection,
        Some(CaptureRect::new(10, 10, 40, 40))
    );

    assert!(contains_future_task(&task));
    app.update(Message::CaptureWindowsChanged(
        easydict_app::detected_windows_from_screen_windows(vec![ScreenWindowSnapshot::new(
            1,
            None,
            ScreenWindowRect::new(0, 0, 500, 400),
        )]),
    ));

    assert_eq!(app.state.capture_interaction.phase, CapturePhase::Selecting);
    assert_eq!(
        app.state.capture_selection,
        Some(CaptureRect::new(10, 10, 40, 40))
    );
}

#[test]
fn capture_overlay_confirm_crops_selected_region_from_frozen_background() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));
    inject_capture_background(&mut app, "ocr-confirm", 400, 300);
    app.update(Message::CaptureSelectionChanged(Some(CaptureRect::new(
        310, 220, 10, 20,
    ))));

    // Confirming crops the frozen snapshot and starts OCR (a future task),
    // never a fresh live screen grab.
    let task = app.update(Message::ConfirmCapture);

    assert!(contains_future_task(&task));
    assert!(!contains_capture_screen_region_task(&task));
    assert_eq!(app.state.active_ocr_mode, Some(OcrMode::Translate));
    assert_eq!(app.state.capture_selection, None);
}

#[test]
fn capture_overlay_drag_release_confirms_and_crops_selected_region() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));
    inject_capture_background(&mut app, "ocr-drag", 400, 300);

    assert!(matches!(
        app.update(Message::CaptureLeftButtonDown(CapturePoint::new(120, 90))),
        Task::None
    ));
    assert!(matches!(
        app.update(Message::CaptureMouseMoved(CapturePoint::new(200, 160))),
        Task::None
    ));
    assert_eq!(app.state.capture_interaction.phase, CapturePhase::Selecting);

    // Releasing the drag confirms immediately — no separate adjust/confirm step.
    let task = app.update(Message::CaptureLeftButtonUp(CapturePoint::new(200, 160)));

    assert!(contains_future_task(&task));
    assert!(!contains_capture_screen_region_task(&task));
    assert_eq!(app.state.active_ocr_mode, Some(OcrMode::Translate));
    assert_eq!(app.state.capture_selection, None);
}

#[test]
fn capture_overlay_double_click_detected_window_confirms_window_region() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_SILENT_OCR.to_string(),
    ));
    inject_capture_background(&mut app, "ocr-dblclick", 400, 300);
    app.update(Message::CaptureWindowsChanged(vec![DetectedWindow::new(
        1,
        CaptureRect::new(0, 0, 400, 300),
    )
    .with_children([DetectedWindow::new(
        2,
        CaptureRect::new(50, 40, 220, 180),
    )])]));
    app.update(Message::CaptureMouseMoved(CapturePoint::new(80, 70)));

    // Double-clicking the detected window confirms its region immediately.
    let task = app.update(Message::CaptureDoubleClick(CapturePoint::new(80, 70)));

    assert!(contains_future_task(&task));
    assert!(!contains_capture_screen_region_task(&task));
    assert_eq!(app.state.active_ocr_mode, Some(OcrMode::SilentClipboard));
    assert_eq!(app.state.capture_selection, None);
}

#[test]
fn capture_overlay_copy_requests_silent_platform_screen_capture() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));
    inject_capture_background(&mut app, "ocr-copy", 400, 300);
    app.update(Message::CaptureSelectionChanged(Some(CaptureRect::new(
        40, 20, 200, 160,
    ))));

    // "Copy text" forces silent-clipboard mode and crops the frozen region.
    let task = app.update(Message::CopyResult);

    assert_eq!(app.state.active_ocr_mode, Some(OcrMode::SilentClipboard));
    assert!(contains_future_task(&task));
    assert!(!contains_capture_screen_region_task(&task));
}

#[test]
fn capture_overlay_copy_without_selection_waits_for_region() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));

    let task = app.update(Message::CopyResult);

    assert_eq!(app.state.pending_ocr_mode, Some(OcrMode::Translate));
    assert_eq!(app.state.ocr_status_text, "Select a region before OCR");
    assert!(!contains_capture_screen_region_task(&task));
}

#[test]
fn app_ocr_screen_capture_uses_native_helper_instead_of_winfluent_task_surface() {
    let app_source = include_str!("../src/lib.rs");
    let screen_capture_source = include_str!("../src/screen_capture.rs");
    let screen_capture_native_source = include_str!("../src/screen_capture_native.rs");
    let ocr_source = include_str!("../src/ocr.rs");
    let state_source = include_str!("../src/state.rs");
    let app_manifest = include_str!("../Cargo.toml");

    // Region capture now happens once when the overlay freezes the desktop
    // (via the lib-owned native helper); confirmation crops that frozen snapshot
    // rather than issuing a second live grab.
    assert!(state_source.contains("screen_capture_native::capture_screen_region_result"));
    assert!(state_source.contains("pub fn crop_capture_background"));
    assert!(app_source.contains("crate::state::crop_capture_background"));
    assert!(app_source.contains("screen_capture_native::capture_screen_windows_result_task"));
    assert!(
        app_manifest.contains("easydict_windows_screen_capture"),
        "default app should depend on the lib-owned Rust-native screen capture helper"
    );
    assert!(
        !screen_capture_source.contains("win_fluent::"),
        "OCR window detection core should use app-owned screen-window DTOs"
    );
    assert!(
        !screen_capture_native_source.contains("win_fluent::prelude::ScreenCapture"),
        "native screen capture facade should use lib-owned capture request/result DTOs"
    );
    assert!(
        !screen_capture_native_source.contains("win_fluent::platform::ScreenWindowSnapshotRequest"),
        "native screen capture facade should use the lib-owned window snapshot request DTO"
    );
    assert!(
        !ocr_source.contains("win_fluent::prelude::ScreenCaptureResult"),
        "OCR capture result conversion should use the lib-owned screen capture result DTO"
    );
    assert!(
        state_source.contains("pub fn capture_screen_background_result()"),
        "capture overlay background freeze should expose Result diagnostics"
    );
    assert!(
        app_source.contains("state::capture_screen_background_result()"),
        "capture overlay background freeze should use the Rust-native screen capture helper"
    );
    assert!(
        app_source.contains("state::apply_capture_background_result("),
        "capture overlay background freeze should preserve native backend errors"
    );
    assert!(
        !app_source.contains("Task::capture_screen_region"),
        "OCR capture should not route through WinFluent screen-region task helpers"
    );
    assert!(
        !app_source.contains("Task::capture_screen_windows"),
        "OCR window snapshots should not route through WinFluent screen-window task helpers"
    );
    assert!(
        !state_source.contains("WindowsPlatformAdapter::capture_screen_region"),
        "capture overlay background freeze should not call WinFluent platform capture directly"
    );
    assert!(
        !app_source.contains("win_fluent_platform_win"),
        "default app source should not directly import the WinFluent Windows platform adapter"
    );
    assert!(
        !app_manifest.contains("win_fluent_platform_win"),
        "default app should not directly depend on the WinFluent Windows platform adapter"
    );
}

#[test]
fn capture_overlay_cancel_clears_selected_region() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));
    app.update(Message::CaptureSelectionChanged(Some(CaptureRect::new(
        1, 2, 30, 40,
    ))));

    let task = app.update(Message::CancelCapture);

    assert_eq!(app.state.capture_selection, None);
    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::Hide(id) if id.as_str() == "capture-overlay"
    )));
}

#[test]
fn ocr_translate_outcome_populates_mini_and_starts_translation() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    begin_ocr_recognize(
        &mut app.state,
        OcrMode::Translate,
        capture(r"C:\Temp\pixels.bgra", 20, 10),
    )
    .expect("OCR should start");

    let task = app.update(Message::OcrRecognizeFinished(success_outcome(
        1,
        OcrMode::Translate,
        "Text from screenshot",
        Some(("en-US", "English")),
    )));

    assert_eq!(app.state.active_ocr_query_id, None);
    assert_eq!(
        app.state.last_ocr_text.as_deref(),
        Some("Text from screenshot")
    );
    assert_eq!(app.state.mini.text, "Text from screenshot");
    assert_eq!(app.state.mini.source_language, "en-US");
    assert!(app.state.mini.is_translating);
    assert_eq!(app.state.mini.active_query_id, Some(1));
    assert!(contains_future_task(&task));
    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "mini"
    )));
}

#[test]
fn ocr_translate_outcome_uses_sorted_line_text_when_result_text_is_empty() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    begin_ocr_recognize(
        &mut app.state,
        OcrMode::Translate,
        capture(r"C:\Temp\pixels.bgra", 20, 10),
    )
    .expect("OCR should start");

    let task = app.update(Message::OcrRecognizeFinished(OcrOutcome {
        query_id: 1,
        mode: OcrMode::Translate,
        result: Ok(OcrResultDto {
            text: String::new(),
            lines: vec![
                line("Second row", 0.0, 40.0, 10.0, 10.0),
                line("Right", 100.0, 0.0, 10.0, 10.0),
                line("Left", 0.0, 0.0, 10.0, 10.0),
            ],
            detected_language: Some(OcrLanguageDto {
                tag: "en-US".to_string(),
                display_name: "English".to_string(),
            }),
            text_angle: None,
        }),
    }));

    assert_eq!(
        app.state.last_ocr_text.as_deref(),
        Some("Left\r\nRight\r\nSecond row")
    );
    assert_eq!(app.state.mini.text, "Left\r\nRight\r\nSecond row");
    assert!(app.state.mini.is_translating);
    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "mini"
    )));
}

#[test]
fn silent_ocr_outcome_uses_rust_clipboard_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    begin_ocr_recognize(
        &mut app.state,
        OcrMode::SilentClipboard,
        capture(r"C:\Temp\pixels.bgra", 20, 10),
    )
    .expect("OCR should start");

    let task = app.update(Message::OcrRecognizeFinished(success_outcome(
        1,
        OcrMode::SilentClipboard,
        "Copied OCR text",
        None,
    )));

    assert_eq!(app.state.active_ocr_query_id, None);
    assert_eq!(app.state.last_ocr_text.as_deref(), Some("Copied OCR text"));
    assert!(contains_future_task(&task));
    assert!(!contains_platform_command(
        &task,
        &PlatformCommand::WriteClipboardText("Copied OCR text".to_string())
    ));
    assert!(!app.state.mini.is_translating);
}

#[test]
fn stale_ocr_outcome_is_ignored() {
    let mut state = EasydictUiState::default();
    begin_ocr_recognize(
        &mut state,
        OcrMode::Translate,
        capture(r"C:\Temp\pixels.bgra", 20, 10),
    )
    .expect("OCR should start");

    let action = apply_ocr_outcome(
        &mut state,
        success_outcome(999, OcrMode::Translate, "stale text", None),
    );

    assert_eq!(action, None);
    assert_eq!(state.active_ocr_query_id, Some(1));
    assert_ne!(state.mini.text, "stale text");
    assert_eq!(state.last_ocr_text, None);
}

struct RecordingOcrBackend {
    configure_calls: Vec<SettingsSnapshot>,
    recognize_calls: Vec<OcrRecognizeParams>,
    responses: VecDeque<Result<OcrResultDto, OcrBackendError>>,
}

impl RecordingOcrBackend {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<OcrResultDto, OcrBackendError>>,
    ) -> Self {
        Self {
            configure_calls: Vec::new(),
            recognize_calls: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl OcrBackend for RecordingOcrBackend {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), OcrBackendError> {
        self.configure_calls.push(settings.clone());
        Ok(())
    }

    fn recognize(&mut self, params: &OcrRecognizeParams) -> Result<OcrResultDto, OcrBackendError> {
        self.recognize_calls.push(params.clone());
        self.responses
            .pop_front()
            .expect("test OCR response should be queued")
    }
}

struct RecordingOcrHttpClient {
    requests: Vec<OcrHttpRequestPlan>,
    responses: VecDeque<Result<String, OcrBackendError>>,
}

impl RecordingOcrHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OcrBackendError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl OcrHttpClient for RecordingOcrHttpClient {
    fn post_json(&mut self, request: &OcrHttpRequestPlan) -> Result<String, OcrBackendError> {
        self.requests.push(request.clone());
        self.responses
            .pop_front()
            .expect("test OCR HTTP response should be queued")
    }
}

struct RecordingWindowsNativeOcrRecognizer {
    calls: Vec<(OcrRecognizeParams, Option<String>)>,
    responses: VecDeque<Result<OcrResultDto, OcrBackendError>>,
    is_available_calls: usize,
    available_languages_calls: usize,
    is_available: bool,
    available_languages: Vec<OcrLanguageDto>,
}

impl RecordingWindowsNativeOcrRecognizer {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<OcrResultDto, OcrBackendError>>,
    ) -> Self {
        Self {
            calls: Vec::new(),
            responses: responses.into_iter().collect(),
            is_available_calls: 0,
            available_languages_calls: 0,
            is_available: true,
            available_languages: Vec::new(),
        }
    }

    fn with_languages(languages: impl IntoIterator<Item = (&'static str, &'static str)>) -> Self {
        Self {
            available_languages: languages
                .into_iter()
                .map(|(tag, display_name)| OcrLanguageDto {
                    tag: tag.to_string(),
                    display_name: display_name.to_string(),
                })
                .collect(),
            ..Self::with_responses([])
        }
    }

    fn unavailable() -> Self {
        Self {
            is_available: false,
            ..Self::with_responses([])
        }
    }
}

impl WindowsNativeOcrRecognizer for RecordingWindowsNativeOcrRecognizer {
    fn is_available(&mut self) -> Result<bool, OcrBackendError> {
        self.is_available_calls += 1;
        Ok(self.is_available)
    }

    fn available_languages(&mut self) -> Result<Vec<OcrLanguageDto>, OcrBackendError> {
        self.available_languages_calls += 1;
        Ok(self.available_languages.clone())
    }

    fn recognize(
        &mut self,
        params: &OcrRecognizeParams,
        preferred_language_tag: Option<&str>,
    ) -> Result<OcrResultDto, OcrBackendError> {
        self.calls.push((
            params.clone(),
            preferred_language_tag.map(ToOwned::to_owned),
        ));
        self.responses
            .pop_front()
            .expect("test Windows OCR response should be queued")
    }
}

fn capture(path: &str, width: u32, height: u32) -> OcrCaptureResult {
    OcrCaptureResult::new(path, width, height).preferred_language_tag("ja-JP")
}

fn write_temp_bgra(name: &str, bytes: &[u8]) -> PathBuf {
    let path = std::env::temp_dir().join(format!(
        "easydict-{name}-{}-{}.bgra",
        std::process::id(),
        bytes.len()
    ));
    fs::write(&path, bytes).expect("test BGRA file should be written");
    path
}

/// Installs a deterministic frozen-desktop snapshot so capture confirmation can
/// crop a reproducible region without depending on a live screen grab.
fn inject_capture_background(app: &mut EasydictApp, name: &str, width: u32, height: u32) {
    let path = write_temp_bgra(name, &vec![0u8; (width as usize) * (height as usize) * 4]);
    app.state.capture_background = Some(CaptureBackground {
        bgra_path: path.to_string_lossy().into_owned(),
        pixel_width: width,
        pixel_height: height,
        scale_factor: 1.0,
    });
}

fn write_temp_legacy_ocr_app_dir(name: &str) -> PathBuf {
    let root = std::env::temp_dir().join(format!("easydict-{name}-{}", std::process::id()));
    let worker_dir = root.join("workers").join("ocr");
    fs::create_dir_all(&worker_dir).expect("legacy OCR worker directory should be created");
    fs::write(root.join("Easydict.CompatHost.exe"), b"legacy compat host")
        .expect("legacy CompatHost marker should be written");
    fs::write(
        worker_dir.join("Easydict.Workers.Ocr.exe"),
        b"legacy OCR worker",
    )
    .expect("legacy OCR worker marker should be written");
    root
}

fn write_current_app_dir_legacy_ocr_markers() -> CurrentAppDirLegacyMarkers {
    let app_dir = std::env::current_exe()
        .expect("test executable path should be available")
        .parent()
        .expect("test executable should have an app directory")
        .to_path_buf();
    let mut markers = CurrentAppDirLegacyMarkers::default();
    let workers_dir = app_dir.join("workers");
    let ocr_worker_dir = workers_dir.join("ocr");
    let dotnet_dir = app_dir.join("dotnet");

    markers.ensure_dir(workers_dir);
    markers.ensure_dir(ocr_worker_dir.clone());
    markers.ensure_file(
        app_dir.join("Easydict.CompatHost.exe"),
        b"stale compat host marker",
    );
    markers.ensure_file(
        ocr_worker_dir.join("Easydict.Workers.Ocr.exe"),
        b"stale OCR worker marker",
    );
    markers.ensure_dir(dotnet_dir.clone());
    markers.ensure_file(dotnet_dir.join("dotnet.exe"), b"stale dotnet marker");
    markers.ensure_file(app_dir.join("dotnet.exe"), b"stale root dotnet marker");
    markers
}

#[derive(Default)]
struct CurrentAppDirLegacyMarkers {
    created_files: Vec<PathBuf>,
    created_dirs: Vec<PathBuf>,
}

impl CurrentAppDirLegacyMarkers {
    fn ensure_dir(&mut self, path: PathBuf) {
        if path.exists() {
            return;
        }

        fs::create_dir(&path).expect("legacy app-dir marker directory should be created");
        self.created_dirs.push(path);
    }

    fn ensure_file(&mut self, path: PathBuf, contents: &[u8]) {
        if path.exists() {
            return;
        }

        fs::write(&path, contents).expect("legacy app-dir marker should be written");
        self.created_files.push(path);
    }
}

impl Drop for CurrentAppDirLegacyMarkers {
    fn drop(&mut self) {
        for path in self.created_files.iter().rev() {
            fs::remove_file(path).ok();
        }

        for path in self.created_dirs.iter().rev() {
            fs::remove_dir(path).ok();
        }
    }
}

struct EnvironmentVariableGuard {
    name: &'static str,
    previous: Option<String>,
}

impl EnvironmentVariableGuard {
    fn set(name: &'static str, value: &str) -> Self {
        let previous = std::env::var(name).ok();
        std::env::set_var(name, value);
        Self { name, previous }
    }
}

impl Drop for EnvironmentVariableGuard {
    fn drop(&mut self) {
        if let Some(value) = &self.previous {
            std::env::set_var(self.name, value);
        } else {
            std::env::remove_var(self.name);
        }
    }
}

fn serve_one_http_response(body: &'static str) -> (String, thread::JoinHandle<String>) {
    let listener = TcpListener::bind("127.0.0.1:0").expect("test HTTP listener should bind");
    let address = listener
        .local_addr()
        .expect("test HTTP listener should have an address");
    let handle = thread::spawn(move || {
        let (mut stream, _) = listener.accept().expect("test HTTP request should arrive");
        stream
            .set_read_timeout(Some(Duration::from_secs(5)))
            .expect("test HTTP stream timeout should be set");

        let mut request = Vec::new();
        let mut buffer = [0u8; 1024];
        loop {
            let read = stream
                .read(&mut buffer)
                .expect("test HTTP request should be readable");
            if read == 0 {
                break;
            }

            request.extend_from_slice(&buffer[..read]);
            if http_request_is_complete(&request) {
                break;
            }
        }

        let response = format!(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
            body.len(),
            body
        );
        stream
            .write_all(response.as_bytes())
            .expect("test HTTP response should be written");
        String::from_utf8_lossy(&request).to_string()
    });

    (format!("http://{address}/api/generate"), handle)
}

fn http_request_is_complete(request: &[u8]) -> bool {
    let Some(header_end) = request.windows(4).position(|window| window == b"\r\n\r\n") else {
        return false;
    };

    let headers = String::from_utf8_lossy(&request[..header_end]);
    let content_length = headers
        .lines()
        .find_map(|line| {
            let (name, value) = line.split_once(':')?;
            name.eq_ignore_ascii_case("content-length")
                .then(|| value.trim().parse::<usize>().ok())
                .flatten()
        })
        .unwrap_or(0);

    request.len() >= header_end + 4 + content_length
}

fn contains_http_header(request: &str, expected_name: &str, expected_value: &str) -> bool {
    request.lines().any(|line| {
        let Some((name, value)) = line.split_once(':') else {
            return false;
        };

        name.eq_ignore_ascii_case(expected_name)
            && value.trim().eq_ignore_ascii_case(expected_value)
    })
}

trait OcrCaptureResultTestExt {
    fn into_params_for_test(self) -> OcrRecognizeParams;
}

impl OcrCaptureResultTestExt for OcrCaptureResult {
    fn into_params_for_test(self) -> OcrRecognizeParams {
        OcrRecognizeParams {
            pixel_data_path: self.pixel_data_path,
            pixel_width: self.pixel_width,
            pixel_height: self.pixel_height,
            preferred_language_tag: self.preferred_language_tag,
        }
    }
}

fn success_outcome(
    query_id: u64,
    mode: OcrMode,
    text: &str,
    language: Option<(&str, &str)>,
) -> OcrOutcome {
    OcrOutcome {
        query_id,
        mode,
        result: Ok(ocr_result(text, language)),
    }
}

fn ocr_result(text: &str, language: Option<(&str, &str)>) -> OcrResultDto {
    OcrResultDto {
        text: text.to_string(),
        lines: Vec::new(),
        detected_language: language.map(|(tag, display_name)| OcrLanguageDto {
            tag: tag.to_string(),
            display_name: display_name.to_string(),
        }),
        text_angle: None,
    }
}

fn line(text: &str, x: f64, y: f64, width: f64, height: f64) -> OcrLineDto {
    OcrLineDto {
        text: text.to_string(),
        bounding_rect: OcrRectDto {
            x,
            y,
            width,
            height,
        },
    }
}

fn contains_future_task(task: &Task<Message>) -> bool {
    match task {
        Task::Future(_) => true,
        Task::Batch(tasks) => tasks.iter().any(contains_future_task),
        _ => false,
    }
}

fn contains_capture_screen_region_task(task: &Task<Message>) -> bool {
    match task {
        Task::CaptureScreenRegion { .. } => true,
        Task::Batch(tasks) => tasks.iter().any(contains_capture_screen_region_task),
        _ => false,
    }
}

fn contains_window_command(
    task: &Task<Message>,
    predicate: impl Fn(&WindowCommand<Message>) -> bool + Copy,
) -> bool {
    match task {
        Task::Window(command) => predicate(command),
        Task::Batch(tasks) => tasks
            .iter()
            .any(|task| contains_window_command(task, predicate)),
        _ => false,
    }
}

fn contains_platform_command(task: &Task<Message>, expected: &PlatformCommand) -> bool {
    match task {
        Task::Platform(command) => command == expected,
        Task::Batch(tasks) => tasks
            .iter()
            .any(|task| contains_platform_command(task, expected)),
        _ => false,
    }
}
