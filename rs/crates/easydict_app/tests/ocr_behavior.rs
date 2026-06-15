use base64::{engine::general_purpose, Engine as _};
use easydict_app::protocol::SettingsSnapshot;
use easydict_app::{
    apply_ocr_outcome, begin_ocr_recognize, bgra_to_base64_bmp, bgra_to_base64_jpeg_data_url,
    build_custom_api_ocr_request, build_ollama_ocr_request, group_and_sort_ocr_lines,
    merge_ocr_lines, merge_ocr_words, merged_ocr_text, parse_ocr_http_response, run_ocr_recognize,
    run_ocr_recognize_with_app_dir, windows_native_ocr_availability_with_recognizer, CapturePhase,
    CapturePoint, CaptureRect, DetectedWindow, EasydictApp, EasydictUiState, Message,
    NativeOcrBackend, OcrAvailabilityDto, OcrBackend, OcrBackendError, OcrCaptureResult,
    OcrEngineConfig, OcrEngineKind, OcrHttpClient, OcrHttpRequestPlan, OcrHttpResponseParser,
    OcrImageEncodeError, OcrLanguageDto, OcrLineDto, OcrMode, OcrOutcome, OcrRecognizeParams,
    OcrRectDto, OcrResultDto, WindowsNativeOcrRecognizer,
};
use serde_json::json;
use std::{
    collections::VecDeque,
    fs,
    io::{Read, Write},
    net::TcpListener,
    path::PathBuf,
    thread,
    time::Duration,
};
use win_fluent::prelude::{
    Application, PlatformCommand, ScreenCaptureRequest, ScreenCaptureResult, ScreenRect,
    ScreenWindow, Task, WindowCommand,
};

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
fn ocr_http_response_parsers_extract_text_without_panicking_on_malformed_json() {
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::OllamaGenerate,
            r#"{ "response": " recognized " }"#
        )
        .text,
        "recognized"
    );
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::ChatCompletions,
            r#"{ "choices": [{ "message": { "content": " text " } }] }"#
        )
        .text,
        "text"
    );
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::Responses,
            r#"{ "output_text": " direct " }"#
        )
        .text,
        "direct"
    );
    assert_eq!(
        parse_ocr_http_response(
            OcrHttpResponseParser::Responses,
            r#"{ "output": [{ "content": [{ "text": " first " }, { "text": " second " }] }] }"#
        )
        .text,
        "first  second"
    );
    assert_eq!(
        parse_ocr_http_response(OcrHttpResponseParser::OllamaGenerate, "{nope").text,
        ""
    );
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

    let message = map_capture_screen_windows_task(
        &task,
        vec![
            ScreenWindow::new(1, None, ScreenRect::new(0, 0, 500, 400)).class_name("Top"),
            ScreenWindow::new(2, Some(1), ScreenRect::new(40, 30, 160, 120)).class_name("Child"),
        ],
    )
    .expect("hotkey should request a window snapshot");
    app.update(message);

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

    let message = map_capture_screen_windows_task(
        &task,
        vec![ScreenWindow::new(1, None, ScreenRect::new(0, 0, 500, 400))],
    )
    .expect("hotkey should request a window snapshot");
    app.update(message);

    assert_eq!(app.state.capture_interaction.phase, CapturePhase::Selecting);
    assert_eq!(
        app.state.capture_selection,
        Some(CaptureRect::new(10, 10, 40, 40))
    );
}

#[test]
fn capture_overlay_confirm_uses_selected_screen_region() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));
    app.update(Message::CaptureSelectionChanged(Some(CaptureRect::new(
        310, 220, -10, 20,
    ))));

    let task = app.update(Message::ConfirmCapture);

    assert_eq!(
        capture_screen_region_request(&task),
        Some(ScreenCaptureRequest::region(ScreenRect::new(
            -10, 20, 320, 200
        )))
    );
    assert_eq!(app.state.capture_selection, None);
}

#[test]
fn capture_overlay_drag_interaction_enters_adjusting_then_confirms_selected_screen_region() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));

    assert!(matches!(
        app.update(Message::CaptureLeftButtonDown(CapturePoint::new(120, 90))),
        Task::None
    ));
    assert!(matches!(
        app.update(Message::CaptureMouseMoved(CapturePoint::new(200, 160))),
        Task::None
    ));

    let task = app.update(Message::CaptureLeftButtonUp(CapturePoint::new(200, 160)));

    assert!(!contains_capture_screen_region_task(&task));
    assert_eq!(app.state.capture_interaction.phase, CapturePhase::Adjusting);
    assert_eq!(
        app.state.capture_selection,
        Some(CaptureRect::new(120, 90, 200, 160))
    );

    let task = app.update(Message::ConfirmCapture);

    assert_eq!(
        capture_screen_region_request(&task),
        Some(ScreenCaptureRequest::region(ScreenRect::new(
            120, 90, 80, 70
        )))
    );
    assert_eq!(app.state.pending_ocr_mode, Some(OcrMode::Translate));
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
    app.update(Message::CaptureWindowsChanged(vec![DetectedWindow::new(
        1,
        CaptureRect::new(0, 0, 400, 300),
    )
    .with_children([DetectedWindow::new(
        2,
        CaptureRect::new(50, 40, 220, 180),
    )])]));
    app.update(Message::CaptureMouseMoved(CapturePoint::new(80, 70)));

    let task = app.update(Message::CaptureDoubleClick(CapturePoint::new(80, 70)));

    assert_eq!(
        capture_screen_region_request(&task),
        Some(ScreenCaptureRequest::region(ScreenRect::new(
            50, 40, 170, 140
        )))
    );
    assert_eq!(app.state.pending_ocr_mode, Some(OcrMode::SilentClipboard));
    assert_eq!(app.state.ocr_status_text, "Silent OCR capture requested");
}

#[test]
fn capture_overlay_copy_requests_silent_platform_screen_capture() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::HotkeyTriggered(
        easydict_app::HOTKEY_OCR_TRANSLATE.to_string(),
    ));
    app.update(Message::CaptureSelectionChanged(Some(CaptureRect::new(
        -10, 20, -7, 23,
    ))));

    let task = app.update(Message::CopyResult);

    assert_eq!(app.state.pending_ocr_mode, Some(OcrMode::SilentClipboard));
    assert_eq!(app.state.ocr_status_text, "Silent OCR capture requested");

    let message = map_capture_screen_region_task(
        &task,
        Some(ScreenCaptureResult {
            pixel_data_path: r"C:\Temp\screen.bgra".to_string(),
            pixel_width: 3,
            pixel_height: 2,
            screen_rect: ScreenRect {
                x: -10,
                y: 20,
                width: 3,
                height: 2,
            },
        }),
    )
    .expect("capture task should map screen result");
    assert_eq!(
        message,
        Message::SilentOcrCaptureFinished(OcrCaptureResult::new(r"C:\Temp\screen.bgra", 3, 2))
    );
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
fn silent_ocr_outcome_writes_text_to_clipboard() {
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
    assert!(contains_platform_command(
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

fn capture_screen_region_request(task: &Task<Message>) -> Option<ScreenCaptureRequest> {
    match task {
        Task::CaptureScreenRegion { request, .. } => Some(*request),
        Task::Batch(tasks) => tasks.iter().find_map(capture_screen_region_request),
        _ => None,
    }
}

fn map_capture_screen_region_task(
    task: &Task<Message>,
    capture: Option<ScreenCaptureResult>,
) -> Option<Message> {
    match task {
        Task::CaptureScreenRegion { map, .. } => Some(map(capture)),
        Task::Batch(tasks) => tasks
            .iter()
            .find_map(|task| map_capture_screen_region_task(task, capture.clone())),
        _ => None,
    }
}

fn map_capture_screen_windows_task(
    task: &Task<Message>,
    windows: Vec<ScreenWindow>,
) -> Option<Message> {
    match task {
        Task::CaptureScreenWindows { map, .. } => Some(map(windows)),
        Task::Batch(tasks) => tasks
            .iter()
            .find_map(|task| map_capture_screen_windows_task(task, windows.clone())),
        _ => None,
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
