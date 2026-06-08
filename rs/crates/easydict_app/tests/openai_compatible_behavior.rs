use easydict_app::protocol::SettingsSnapshot;
use easydict_app::{
    build_built_in_ai_device_registration_request_plan, build_foundry_local_models_request_plan,
    build_openai_grammar_messages, build_openai_grammar_request_plan, build_openai_request_body,
    build_openai_translation_messages, build_openai_translation_request_plan,
    built_in_ai_device_registration_endpoint, built_in_ai_direct_endpoint_for_model,
    built_in_ai_direct_service_config, built_in_ai_embedded_device_registration_request_plan,
    built_in_ai_embedded_proxy_service_config, built_in_ai_proxy_headers,
    built_in_ai_proxy_model_or_default, built_in_ai_proxy_service_config,
    check_foundry_local_runtime_status, clamp_openai_temperature, cleanup_openai_translation_text,
    correct_grammar_openai_compatible, custom_openai_service_config, deepseek_service_config,
    detect_openai_api_format_from_url, execute_openai_stream_request,
    extract_foundry_local_chat_completions_endpoint,
    extract_foundry_local_chat_completions_endpoint_from_logs,
    foundry_local_models_endpoint_from_chat_completions_endpoint, foundry_local_service_config,
    github_models_service_config, groq_service_config,
    normalize_foundry_local_chat_completions_endpoint, ollama_model_refresh_fallback,
    ollama_service_config, ollama_tags_url_from_endpoint, openai_api_format_from_setting,
    openai_compatible_config_for_service, openai_compatible_service_can_route_natively,
    openai_effective_temperature, openai_error_from_response, openai_responses_reasoning_effort,
    openai_service_config, parse_built_in_ai_device_registration_response,
    parse_foundry_local_runtime_status, parse_ollama_model_names, register_built_in_ai_device,
    resolve_foundry_local_model_id_for_config, resolve_ollama_model_refresh,
    resolve_openai_api_format, resolve_openai_compatible_config_for_service,
    translate_openai_compatible, try_resolve_foundry_local_model_id, validate_openai_config,
    zhipu_service_config, BuiltInAiDeviceRegistrationHttpClient,
    BuiltInAiDeviceRegistrationHttpResponse, ChatMessage, ChatRole, FoundryLocalEndpointResolver,
    FoundryLocalError, FoundryLocalModelState, FoundryLocalRuntimeController,
    FoundryLocalRuntimeState, FoundryLocalRuntimeStatus, OpenAiApiFormat, OpenAiCompatibleConfig,
    OpenAiExecutionError, OpenAiExecutionErrorCode, OpenAiHttpClient, OpenAiHttpGetRequestPlan,
    OpenAiHttpRequestPlan, OpenAiHttpTextResponse, OpenAiPlanError, OpenAiTranslationRequest,
    TranslationLanguage, BUILT_IN_AI_ALLOWED_PROXY_MODELS, BUILT_IN_AI_DEFAULT_MODEL,
    CUSTOM_OPENAI_DEFAULT_MODEL, DEEPSEEK_DEFAULT_ENDPOINT, DEEPSEEK_DEFAULT_MODEL,
    FOUNDRY_LOCAL_DEFAULT_MODEL, GITHUB_MODELS_DEFAULT_ENDPOINT, GITHUB_MODELS_DEFAULT_MODEL,
    GROQ_DEFAULT_ENDPOINT, GROQ_DEFAULT_MODEL, OLLAMA_DEFAULT_ENDPOINT, OLLAMA_DEFAULT_MODEL,
    OPENAI_DEFAULT_ENDPOINT, OPENAI_DEFAULT_MODEL, OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT,
    ZHIPU_DEFAULT_ENDPOINT,
};
use std::collections::VecDeque;
use std::fs;
use std::path::PathBuf;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

#[test]
fn openai_format_detection_recognizes_known_suffixes() {
    let cases = [
        (
            "https://api.openai.com/v1/responses",
            OpenAiApiFormat::Responses,
        ),
        (
            "https://api.openai.com/v1/responses/",
            OpenAiApiFormat::Responses,
        ),
        (
            "https://my-proxy.example.com/openai/v1/responses",
            OpenAiApiFormat::Responses,
        ),
        (
            "https://API.OPENAI.COM/v1/RESPONSES?api-version=2025-01-01",
            OpenAiApiFormat::Responses,
        ),
        (
            "https://api.openai.com/v1/chat/completions",
            OpenAiApiFormat::ChatCompletions,
        ),
        (
            "http://localhost:11434/v1/chat/completions",
            OpenAiApiFormat::ChatCompletions,
        ),
        (
            "https://api.openai.com/v1/",
            OpenAiApiFormat::ChatCompletions,
        ),
        ("https://example.com/api", OpenAiApiFormat::ChatCompletions),
        ("not-a-url", OpenAiApiFormat::ChatCompletions),
    ];

    for (endpoint, expected) in cases {
        assert_eq!(
            detect_openai_api_format_from_url(endpoint),
            expected,
            "{endpoint}"
        );
    }
}

#[test]
fn openai_format_override_bypasses_url_detection() {
    assert_eq!(
        resolve_openai_api_format(
            "https://api.openai.com/v1/chat/completions",
            OpenAiApiFormat::Responses,
        ),
        OpenAiApiFormat::Responses
    );
    assert_eq!(
        resolve_openai_api_format(
            "https://api.openai.com/v1/responses",
            OpenAiApiFormat::ChatCompletions,
        ),
        OpenAiApiFormat::ChatCompletions
    );
    assert_eq!(
        resolve_openai_api_format("https://api.openai.com/v1/responses", OpenAiApiFormat::Auto,),
        OpenAiApiFormat::Responses
    );
}

#[test]
fn openai_chat_completions_body_uses_messages_shape() {
    let messages = sample_messages();
    let body = build_openai_request_body(
        OpenAiApiFormat::ChatCompletions,
        &messages,
        "test-model",
        0.3,
        Some("low"),
    );

    assert_eq!(body["model"], "test-model");
    assert_eq!(body["temperature"], 0.3);
    assert_eq!(body["stream"], true);
    assert_eq!(body["reasoning_effort"], "low");
    assert!(body.get("instructions").is_none());
    assert!(body.get("input").is_none());
    assert_eq!(body["messages"][0]["role"], "system");
    assert_eq!(body["messages"][0]["content"], "System instructions");
    assert_eq!(body["messages"][1]["role"], "user");
    assert_eq!(body["messages"][1]["content"], "Hello");
}

#[test]
fn openai_responses_body_uses_instructions_and_input_shape() {
    let messages = sample_messages();
    let body = build_openai_request_body(
        OpenAiApiFormat::Responses,
        &messages,
        "test-model",
        1.0,
        Some("medium"),
    );

    assert_eq!(body["model"], "test-model");
    assert_eq!(body["instructions"], "System instructions");
    assert_eq!(body["input"], "Hello\n\nEarlier context");
    assert_eq!(body["temperature"], 1.0);
    assert_eq!(body["stream"], true);
    assert_eq!(body["store"], false);
    assert_eq!(body["reasoning"]["effort"], "medium");
    assert!(body.get("messages").is_none());
}

#[test]
fn openai_request_body_skips_blank_reasoning_effort() {
    let body = build_openai_request_body(
        OpenAiApiFormat::ChatCompletions,
        &sample_messages(),
        "test-model",
        0.3,
        Some("   "),
    );

    assert!(body.get("reasoning_effort").is_none());
}

#[test]
fn openai_translation_messages_match_base_service_prompt_shape() {
    let messages = build_openai_translation_messages(&OpenAiTranslationRequest {
        text: "Hello".to_string(),
        from_language: TranslationLanguage::English,
        to_language: TranslationLanguage::SimplifiedChinese,
        custom_prompt: Some("Keep product names unchanged.".to_string()),
    });

    assert_eq!(messages.len(), 2);
    assert_eq!(messages[0].role, ChatRole::System);
    assert!(messages[0]
        .content
        .contains("You are a translation expert proficient in various languages"));
    assert!(messages[0]
        .content
        .contains("Additional instructions: Keep product names unchanged."));
    assert_eq!(messages[1].role, ChatRole::User);
    assert_eq!(
        messages[1].content,
        "Translate the following English text into Chinese (Simplified) text: \"\"\"Hello\"\"\""
    );
}

#[test]
fn openai_translation_messages_use_detected_language_for_auto_source() {
    let messages = build_openai_translation_messages(&OpenAiTranslationRequest {
        text: "Bonjour".to_string(),
        from_language: TranslationLanguage::Auto,
        to_language: TranslationLanguage::English,
        custom_prompt: None,
    });

    assert_eq!(
        messages[1].content,
        "Translate the following the detected language text into English text: \"\"\"Bonjour\"\"\""
    );
}

#[test]
fn openai_grammar_messages_reuse_shared_prompt_resources() {
    let messages = build_openai_grammar_messages(TranslationLanguage::English, "He go home.", true);

    assert_eq!(messages.len(), 2);
    assert_eq!(messages[0].role, ChatRole::System);
    assert!(messages[0].content.contains("No errors found."));
    assert_eq!(messages[1].role, ChatRole::User);
    assert_eq!(
        messages[1].content,
        "Correct the grammar in the following English text. The result MUST remain in English:\n\nHe go home."
    );
}

#[test]
fn openai_service_config_preserves_default_endpoint_model_and_reasoning_rules() {
    let modern = openai_service_config("sk-test", None, None, Some(0.3), OpenAiApiFormat::Auto);

    assert_eq!(modern.endpoint, OPENAI_DEFAULT_ENDPOINT);
    assert_eq!(modern.model, OPENAI_DEFAULT_MODEL);
    assert_eq!(modern.temperature, 0.3);
    assert_eq!(modern.reasoning_effort.as_deref(), Some("none"));
    assert!(modern.requires_api_key);
    assert!(modern.is_configured());

    let legacy = openai_service_config(
        "sk-test",
        Some(OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT),
        Some("gpt-5-mini"),
        Some(0.3),
        OpenAiApiFormat::Auto,
    );

    assert_eq!(legacy.temperature, 1.0);
    assert_eq!(legacy.reasoning_effort.as_deref(), Some("minimal"));
    assert_eq!(legacy.resolved_format(), OpenAiApiFormat::ChatCompletions);
}

#[test]
fn openai_temperature_and_reasoning_helpers_match_openai_service() {
    assert_eq!(clamp_openai_temperature(-1.0), 0.0);
    assert_eq!(clamp_openai_temperature(5.0), 2.0);
    assert_eq!(openai_effective_temperature("gpt-5-mini", 0.3), 1.0);
    assert_eq!(openai_effective_temperature("gpt-5.4-mini", 0.3), 0.3);
    assert_eq!(
        openai_responses_reasoning_effort("gpt-5.4-mini"),
        Some("none")
    );
    assert_eq!(
        openai_responses_reasoning_effort("gpt-5-mini"),
        Some("minimal")
    );
    assert_eq!(openai_responses_reasoning_effort("gpt-4o-mini"), None);
}

#[test]
fn ollama_and_custom_openai_configs_do_not_require_api_keys() {
    let ollama = ollama_service_config(None, None);

    assert_eq!(ollama.endpoint, OLLAMA_DEFAULT_ENDPOINT);
    assert_eq!(ollama.model, OLLAMA_DEFAULT_MODEL);
    assert!(!ollama.requires_api_key);
    assert!(validate_openai_config(&ollama).is_ok());

    let custom = custom_openai_service_config(
        "http://localhost:8080/v1/chat/completions",
        None,
        None,
        None,
    );

    assert_eq!(custom.model, CUSTOM_OPENAI_DEFAULT_MODEL);
    assert!(!custom.requires_api_key);
    assert!(custom.api_key.is_empty());
    assert!(validate_openai_config(&custom).is_ok());
}

#[test]
fn foundry_local_config_normalizes_openai_compatible_endpoint() {
    let config = foundry_local_service_config("http://127.0.0.1:5273/v1", None);

    assert_eq!(config.endpoint, "http://127.0.0.1:5273/v1/chat/completions");
    assert_eq!(config.model, FOUNDRY_LOCAL_DEFAULT_MODEL);
    assert!(!config.requires_api_key);
    assert_eq!(config.format_override, OpenAiApiFormat::ChatCompletions);
    assert!(validate_openai_config(&config).is_ok());

    assert_eq!(
        normalize_foundry_local_chat_completions_endpoint(
            "http://localhost:5273/openai/status?x=1"
        ),
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(
        normalize_foundry_local_chat_completions_endpoint(
            "http://localhost:5273/openai/load/qwen2.5-0.5b"
        ),
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(
        normalize_foundry_local_chat_completions_endpoint(
            "http://localhost:5273/v1/chat/completions/"
        ),
        "http://localhost:5273/v1/chat/completions"
    );
}

#[test]
fn foundry_local_endpoint_extraction_prefers_loopback_status_urls() {
    let output = "Remote service: https://example.test/openai/status\n\
        Foundry Local service: http://127.0.0.1:5273/openai/status.\n";

    assert_eq!(
        extract_foundry_local_chat_completions_endpoint(output).as_deref(),
        Some("http://127.0.0.1:5273/v1/chat/completions")
    );

    assert_eq!(
        extract_foundry_local_chat_completions_endpoint(
            "{\"endpoint\":\"http://localhost:5273/v1\"}"
        )
        .as_deref(),
        Some("http://localhost:5273/v1/chat/completions")
    );
}

#[test]
fn foundry_local_models_endpoint_is_derived_from_chat_completions_endpoint() {
    assert_eq!(
        foundry_local_models_endpoint_from_chat_completions_endpoint(
            "http://localhost:5273/v1/chat/completions?ignored=1#fragment"
        )
        .as_deref(),
        Some("http://localhost:5273/v1/models")
    );
    assert!(
        foundry_local_models_endpoint_from_chat_completions_endpoint(
            "http://localhost:5273/openai/status"
        )
        .is_none()
    );

    let config = foundry_local_service_config("http://localhost:5273/v1/chat/completions", None);
    let plan = build_foundry_local_models_request_plan(&config).expect("models plan");
    assert_eq!(plan.method, "GET");
    assert_eq!(plan.endpoint, "http://localhost:5273/v1/models");
    assert!(plan.headers.is_empty());
}

#[test]
fn foundry_local_model_parser_prefers_exact_then_npu_gpu_cpu_aliases() {
    let models = r#"{
        "data": [
            { "id": "qwen2.5-0.5b-instruct-openvino-cpu" },
            { "id": "qwen2.5-0.5b-instruct-openvino-gpu" },
            { "id": "qwen2.5-0.5b-instruct-openvino-npu" },
            { "id": "other-model-instruct-openvino-npu" }
        ]
    }"#;

    assert_eq!(
        try_resolve_foundry_local_model_id(models, "qwen2.5-0.5b").as_deref(),
        Some("qwen2.5-0.5b-instruct-openvino-npu")
    );

    let exact = r#"{"data":[{"id":"QWEN2.5-0.5B"},{"id":"qwen2.5-0.5b-instruct-openvino-npu"}]}"#;
    assert_eq!(
        try_resolve_foundry_local_model_id(exact, "qwen2.5-0.5b").as_deref(),
        Some("QWEN2.5-0.5B")
    );

    assert!(try_resolve_foundry_local_model_id(r#"{"data":[]}"#, "qwen2.5-0.5b").is_none());
    assert!(try_resolve_foundry_local_model_id("not json", "qwen2.5-0.5b").is_none());
}

#[test]
fn foundry_local_model_resolution_is_best_effort() {
    let config = foundry_local_service_config(
        "http://localhost:5273/v1/chat/completions",
        Some("qwen2.5-0.5b"),
    );
    let models = r#"{
        "data": [
            { "id": "qwen2.5-0.5b-instruct-openvino-gpu" },
            { "id": "qwen2.5-0.5b-instruct-openvino-cpu" }
        ]
    }"#;
    let mut client =
        RecordingOpenAiHttpClient::with_get_responses([Ok(Some(OpenAiHttpTextResponse {
            status_code: 200,
            reason_phrase: "OK".to_string(),
            body: models.to_string(),
        }))]);

    let resolved = resolve_foundry_local_model_id_for_config(&mut client, &config);

    assert_eq!(resolved.model, "qwen2.5-0.5b-instruct-openvino-gpu");
    assert_eq!(client.get_requests.len(), 1);
    assert_eq!(
        client.get_requests[0].endpoint,
        "http://localhost:5273/v1/models"
    );

    let mut failing_client =
        RecordingOpenAiHttpClient::with_get_responses([Ok(Some(OpenAiHttpTextResponse {
            status_code: 503,
            reason_phrase: "Service Unavailable".to_string(),
            body: String::new(),
        }))]);
    assert_eq!(
        resolve_foundry_local_model_id_for_config(&mut failing_client, &config).model,
        "qwen2.5-0.5b"
    );
}

#[test]
fn foundry_local_log_endpoint_extraction_uses_latest_foundry_log() {
    let log_dir = unique_temp_dir("easydict-foundry-log-endpoint");
    fs::create_dir_all(&log_dir).expect("log dir should be created");
    fs::write(
        log_dir.join("other.log"),
        "remote http://example.test/openai/status",
    )
    .expect("other log should be written");
    fs::write(
        log_dir.join("foundry-old.log"),
        "old endpoint http://localhost:1111/openai/status",
    )
    .expect("old Foundry log should be written");
    std::thread::sleep(Duration::from_millis(20));
    fs::write(
        log_dir.join("foundry-new.log"),
        "current endpoint http://127.0.0.1:5273/openai/load/qwen2.5-0.5b",
    )
    .expect("new Foundry log should be written");

    assert_eq!(
        extract_foundry_local_chat_completions_endpoint_from_logs(&log_dir).as_deref(),
        Some("http://127.0.0.1:5273/v1/chat/completions")
    );

    fs::remove_dir_all(&log_dir).expect("log dir should be removed");
}

#[test]
fn foundry_local_runtime_status_parser_maps_cli_outputs() {
    let stopped = parse_foundry_local_runtime_status(
        "\u{61C4} Model management service is not running!\r\n\
         To start the service, run the following command: foundry service start",
    );
    assert_eq!(stopped.state, FoundryLocalRuntimeState::NotRunning);
    assert!(stopped
        .detail_message
        .as_deref()
        .unwrap()
        .starts_with("Model management service is not running!"));
    assert!(!stopped.detail_message.unwrap().contains('\u{61C4}'));

    let missing = parse_foundry_local_runtime_status(
        "'foundry' is not recognized as an internal or external command.",
    );
    assert_eq!(missing.state, FoundryLocalRuntimeState::NotInstalled);

    let ready = parse_foundry_local_runtime_status(
        "Model management service is running on http://127.0.0.1:5273/openai/status",
    );
    assert_eq!(ready.state, FoundryLocalRuntimeState::Running);
    assert_eq!(
        ready.endpoint.as_deref(),
        Some("http://127.0.0.1:5273/v1/chat/completions")
    );

    let already_running = parse_foundry_local_runtime_status(
        "Service is already running on http://127.0.0.1:12192/.",
    );
    assert_eq!(
        already_running.endpoint.as_deref(),
        Some("http://127.0.0.1:12192/v1/chat/completions")
    );

    let running_without_endpoint =
        parse_foundry_local_runtime_status("Foundry Local service is running.");
    assert_eq!(
        running_without_endpoint.state,
        FoundryLocalRuntimeState::Running
    );
    assert!(running_without_endpoint.endpoint.is_none());
}

#[test]
fn foundry_local_runtime_status_check_matches_dotnet_state_mapping() {
    let mut not_running = RecordingFoundryLocalRuntimeController::with_statuses(
        [Ok(FoundryLocalRuntimeStatus::new(
            FoundryLocalRuntimeState::NotRunning,
        ))],
        [],
    );
    let needs_preparation =
        check_foundry_local_runtime_status(&mut not_running, &SettingsSnapshot::default())
            .expect("status check should map not-running runtime");
    assert_eq!(
        needs_preparation.state,
        FoundryLocalModelState::NeedsPreparation
    );
    assert_eq!(
        needs_preparation.resource_key,
        "FoundryLocal_Status_NotRunning"
    );

    let settings = SettingsSnapshot {
        foundry_local_endpoint: Some("http://192.0.2.10:5273/v1".to_string()),
        ..SettingsSnapshot::default()
    };
    let mut user_managed = RecordingFoundryLocalRuntimeController::with_statuses(
        [Ok(FoundryLocalRuntimeStatus::new(
            FoundryLocalRuntimeState::NotInstalled,
        ))],
        [],
    );
    let ready = check_foundry_local_runtime_status(&mut user_managed, &settings)
        .expect("non-loopback user endpoint should be ready without CLI lifecycle");
    assert_eq!(ready.state, FoundryLocalModelState::Ready);
    assert_eq!(ready.resource_key, "FoundryLocal_Status_Ready");
    assert!(user_managed.calls.is_empty());

    let mut running_without_endpoint = RecordingFoundryLocalRuntimeController::with_statuses(
        [Ok(FoundryLocalRuntimeStatus::with_detail(
            FoundryLocalRuntimeState::Running,
            "Foundry Local service is running.",
        ))],
        [Ok(None)],
    );
    let failed = check_foundry_local_runtime_status(
        &mut running_without_endpoint,
        &SettingsSnapshot::default(),
    )
    .expect("running runtime without endpoint should be a failed status");
    assert_eq!(failed.state, FoundryLocalModelState::Failed);
    assert_eq!(failed.resource_key, "FoundryLocal_Status_StartFailed");
    assert!(failed
        .detail_message
        .unwrap()
        .contains("did not report a local endpoint"));

    let mut resolver_fallback = RecordingFoundryLocalRuntimeController::with_statuses(
        [Ok(FoundryLocalRuntimeStatus::with_detail(
            FoundryLocalRuntimeState::Running,
            "Foundry Local service is running.",
        ))],
        [Ok(Some("http://127.0.0.1:5273/openai/status".to_string()))],
    );
    let ready =
        check_foundry_local_runtime_status(&mut resolver_fallback, &SettingsSnapshot::default())
            .expect("resolver fallback should provide endpoint");
    assert_eq!(ready.state, FoundryLocalModelState::Ready);
    assert_eq!(
        ready.endpoint.as_deref(),
        Some("http://127.0.0.1:5273/v1/chat/completions")
    );
}

#[test]
fn openai_plan_validation_matches_base_service_configuration_gates() {
    let missing_endpoint = OpenAiCompatibleConfig::new("", "model").with_api_key("sk-test");
    assert_eq!(
        validate_openai_config(&missing_endpoint),
        Err(OpenAiPlanError::EndpointNotConfigured)
    );

    let missing_key = OpenAiCompatibleConfig::new(OPENAI_DEFAULT_ENDPOINT, "model");
    assert_eq!(
        validate_openai_config(&missing_key),
        Err(OpenAiPlanError::ApiKeyRequired)
    );

    let optional_key =
        OpenAiCompatibleConfig::new("http://localhost:8080/v1/chat/completions", "model")
            .without_required_api_key();
    assert!(validate_openai_config(&optional_key).is_ok());
}

#[test]
fn openai_translation_request_plan_sets_authorization_and_streaming_format() {
    let config = openai_service_config(
        "sk-test",
        Some(OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT),
        Some("gpt-5.4-mini"),
        Some(0.3),
        OpenAiApiFormat::Auto,
    );
    let request = OpenAiTranslationRequest {
        text: "Hello".to_string(),
        from_language: TranslationLanguage::English,
        to_language: TranslationLanguage::SimplifiedChinese,
        custom_prompt: None,
    };

    let plan = build_openai_translation_request_plan(&config, &request).unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.endpoint, OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT);
    assert_eq!(plan.content_type, "application/json");
    assert_eq!(plan.api_format, OpenAiApiFormat::ChatCompletions);
    assert_eq!(
        plan.headers[0],
        ("Authorization".to_string(), "Bearer sk-test".to_string())
    );
    assert_eq!(plan.body["stream"], true);
    assert_eq!(plan.body["reasoning_effort"], "none");
    assert_eq!(plan.body["messages"][0]["role"], "system");
}

#[test]
fn openai_grammar_request_plan_uses_responses_shape() {
    let config = openai_service_config(
        "sk-test",
        Some(OPENAI_DEFAULT_ENDPOINT),
        Some("gpt-5.4-mini"),
        Some(0.3),
        OpenAiApiFormat::Auto,
    );

    let plan = build_openai_grammar_request_plan(
        &config,
        TranslationLanguage::English,
        "He go home.",
        true,
    )
    .unwrap();

    assert_eq!(plan.api_format, OpenAiApiFormat::Responses);
    assert_eq!(plan.body["stream"], true);
    assert_eq!(plan.body["store"], false);
    assert_eq!(plan.body["reasoning"]["effort"], "none");
    assert!(plan.body["instructions"]
        .as_str()
        .unwrap()
        .contains("No errors found."));
    assert!(plan.body["input"].as_str().unwrap().contains("He go home."));
}

#[test]
fn custom_openai_plan_omits_authorization_when_key_empty() {
    let config = custom_openai_service_config(
        "http://localhost:8080/v1/chat/completions",
        None,
        Some("local-model"),
        Some(0.8),
    );

    let plan = build_openai_translation_request_plan(
        &config,
        &OpenAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: TranslationLanguage::English,
            to_language: TranslationLanguage::SimplifiedChinese,
            custom_prompt: None,
        },
    )
    .unwrap();

    assert!(plan.headers.is_empty());
    assert_eq!(plan.body["model"], "local-model");
    assert_eq!(plan.body["temperature"], 0.8);
}

#[test]
fn builtin_proxy_headers_follow_device_id_and_token_rules() {
    assert!(built_in_ai_proxy_headers("", "token").is_empty());
    assert_eq!(
        built_in_ai_proxy_headers("device-id", ""),
        vec![("X-Device-Id".to_string(), "device-id".to_string())]
    );
    assert_eq!(
        built_in_ai_proxy_headers("device-id", "token"),
        vec![
            ("X-Device-Id".to_string(), "device-id".to_string()),
            ("X-Device-Token".to_string(), "token".to_string())
        ]
    );
}

#[test]
fn builtin_proxy_config_uses_embedded_endpoint_key_and_device_headers() {
    assert_eq!(
        BUILT_IN_AI_ALLOWED_PROXY_MODELS,
        &[
            "glm-4-flash",
            "glm-4-flash-250414",
            "llama-3.3-70b-versatile",
            "llama-3.1-8b-instant"
        ]
    );
    assert_eq!(
        built_in_ai_proxy_model_or_default(Some("nonexistent-model")),
        BUILT_IN_AI_DEFAULT_MODEL
    );

    let explicit = built_in_ai_proxy_service_config(
        "proxy-key",
        "https://proxy.example.test/v1/chat/completions",
        Some("llama-3.3-70b-versatile"),
        Some("device-id"),
        Some("device-token"),
    )
    .expect("valid proxy config");
    assert_eq!(
        explicit.endpoint,
        "https://proxy.example.test/v1/chat/completions"
    );
    assert_eq!(explicit.model, "llama-3.3-70b-versatile");
    assert_eq!(explicit.api_key, "proxy-key");
    assert_eq!(
        explicit.extra_headers,
        vec![
            ("X-Device-Id".to_string(), "device-id".to_string()),
            ("X-Device-Token".to_string(), "device-token".to_string())
        ]
    );
    assert!(built_in_ai_proxy_service_config(
        "",
        "https://proxy.example.test/v1/chat/completions",
        None,
        None,
        None
    )
    .is_none());

    let embedded = built_in_ai_embedded_proxy_service_config(
        Some("glm-4-flash"),
        Some("device-id"),
        Some("device-token"),
    )
    .expect("embedded built-in AI proxy secrets should decrypt");
    assert!(embedded.endpoint.starts_with("https://"));
    assert!(!embedded.api_key.is_empty());
    assert_eq!(embedded.model, "glm-4-flash");
    assert_eq!(
        embedded.extra_headers,
        vec![
            ("X-Device-Id".to_string(), "device-id".to_string()),
            ("X-Device-Token".to_string(), "device-token".to_string())
        ]
    );
}

#[test]
fn builtin_device_registration_plan_and_parser_match_proxy_contract() {
    assert_eq!(
        built_in_ai_device_registration_endpoint("https://proxy.example.test/v1/chat/completions")
            .as_deref(),
        Some("https://proxy.example.test/v1/device/register")
    );
    assert_eq!(
        built_in_ai_device_registration_endpoint("https://proxy.example.test:8443/openai/chat?x=1")
            .as_deref(),
        Some("https://proxy.example.test:8443/v1/device/register")
    );
    assert!(built_in_ai_device_registration_endpoint("not-a-url").is_none());

    let plan = build_built_in_ai_device_registration_request_plan(
        "proxy-key",
        "https://proxy.example.test/v1/chat/completions",
        " device-id ",
    )
    .expect("registration plan");
    assert_eq!(plan.method, "POST");
    assert_eq!(
        plan.endpoint,
        "https://proxy.example.test/v1/device/register"
    );
    assert_eq!(
        plan.headers,
        vec![
            ("X-Device-Id".to_string(), "device-id".to_string()),
            ("Authorization".to_string(), "Bearer proxy-key".to_string())
        ]
    );
    assert!(build_built_in_ai_device_registration_request_plan(
        "proxy-key",
        "https://proxy.example.test/v1/chat/completions",
        "   "
    )
    .is_none());

    let embedded = built_in_ai_embedded_device_registration_request_plan("device-id")
        .expect("embedded registration plan should decrypt");
    assert!(embedded.endpoint.starts_with("https://"));
    assert!(embedded
        .headers
        .contains(&("X-Device-Id".to_string(), "device-id".to_string())));
    assert!(embedded
        .headers
        .iter()
        .any(|(name, value)| name == "Authorization" && value.starts_with("Bearer ")));

    assert_eq!(
        parse_built_in_ai_device_registration_response(r#"{"device_token":" token-value "}"#)
            .as_deref(),
        Some("token-value")
    );
    assert!(parse_built_in_ai_device_registration_response(r#"{"device_token":""}"#).is_none());
    assert!(parse_built_in_ai_device_registration_response(r#"{"ok":true}"#).is_none());
    assert!(parse_built_in_ai_device_registration_response("not json").is_none());
}

#[test]
fn builtin_device_registration_executor_returns_token_only_for_success_response() {
    let plan = build_built_in_ai_device_registration_request_plan(
        "proxy-key",
        "https://proxy.example.test/v1/chat/completions",
        "device-id",
    )
    .expect("registration plan");
    let mut client = RecordingBuiltInAiDeviceRegistrationHttpClient::with_responses([Ok(
        BuiltInAiDeviceRegistrationHttpResponse {
            status_code: 200,
            reason_phrase: "OK".to_string(),
            body: r#"{"device_token":"registered-token"}"#.to_string(),
        },
    )]);

    let token = register_built_in_ai_device(&mut client, &plan)
        .expect("registration request should succeed");

    assert_eq!(token.as_deref(), Some("registered-token"));
    assert_eq!(client.requests, vec![plan.clone()]);

    let mut non_success = RecordingBuiltInAiDeviceRegistrationHttpClient::with_responses([Ok(
        BuiltInAiDeviceRegistrationHttpResponse {
            status_code: 429,
            reason_phrase: "Too Many Requests".to_string(),
            body: "{}".to_string(),
        },
    )]);
    assert!(register_built_in_ai_device(&mut non_success, &plan)
        .unwrap()
        .is_none());

    let mut missing_token = RecordingBuiltInAiDeviceRegistrationHttpClient::with_responses([Ok(
        BuiltInAiDeviceRegistrationHttpResponse {
            status_code: 200,
            reason_phrase: "OK".to_string(),
            body: "{}".to_string(),
        },
    )]);
    assert!(register_built_in_ai_device(&mut missing_token, &plan)
        .unwrap()
        .is_none());
}

#[test]
fn cleanup_openai_translation_text_matches_base_translation_service() {
    assert_eq!(cleanup_openai_translation_text("  hello  "), "hello");
    assert_eq!(cleanup_openai_translation_text(" \"hello\" "), "hello");
    assert_eq!(cleanup_openai_translation_text(" \"hello"), "\"hello");
}

#[test]
fn ollama_tags_url_is_derived_from_configured_endpoint() {
    assert_eq!(
        ollama_tags_url_from_endpoint("http://localhost:11434/v1/chat/completions").as_deref(),
        Some("http://localhost:11434/api/tags")
    );
    assert_eq!(
        ollama_tags_url_from_endpoint("http://192.168.1.100:11434/v1/chat/completions").as_deref(),
        Some("http://192.168.1.100:11434/api/tags")
    );
    assert_eq!(
        ollama_tags_url_from_endpoint("https://ollama.example.com/v1/chat/completions").as_deref(),
        Some("https://ollama.example.com:443/api/tags")
    );
    assert!(ollama_tags_url_from_endpoint("not-a-url").is_none());
}

#[test]
fn ollama_model_parser_matches_refresh_local_models() {
    let json = r#"{
        "models": [
            { "name": "llama3.2" },
            { "name": "mistral" },
            { "name": "" },
            { "digest": "missing-name" },
            { "name": "codellama" }
        ]
    }"#;

    assert_eq!(
        parse_ollama_model_names(json).unwrap(),
        vec!["llama3.2", "mistral", "codellama"]
    );
    assert!(parse_ollama_model_names("not json").is_err());
    assert!(parse_ollama_model_names(r#"{"models":[]}"#)
        .unwrap()
        .is_empty());
}

#[test]
fn ollama_refresh_outcome_switches_to_first_model_only_when_needed() {
    let current_available = resolve_ollama_model_refresh(
        "llama3.2",
        vec!["mistral".to_string(), "llama3.2".to_string()],
    );
    assert_eq!(current_available.selected_model, "llama3.2");

    let switched = resolve_ollama_model_refresh(
        "nonexistent-model",
        vec!["mistral".to_string(), "llama3.2".to_string()],
    );
    assert_eq!(switched.selected_model, "mistral");

    let fallback = ollama_model_refresh_fallback();
    assert_eq!(
        fallback.available_models,
        vec![OLLAMA_DEFAULT_MODEL.to_string()]
    );
    assert_eq!(fallback.selected_model, OLLAMA_DEFAULT_MODEL);
}

#[test]
fn service_specific_configs_preserve_openai_compatible_defaults() {
    let deepseek = deepseek_service_config("sk-deepseek", None);
    assert_eq!(deepseek.endpoint, DEEPSEEK_DEFAULT_ENDPOINT);
    assert_eq!(deepseek.model, DEEPSEEK_DEFAULT_MODEL);
    assert_eq!(deepseek.api_key, "sk-deepseek");

    let groq = groq_service_config("sk-groq", None);
    assert_eq!(groq.endpoint, GROQ_DEFAULT_ENDPOINT);
    assert_eq!(groq.model, GROQ_DEFAULT_MODEL);

    let zhipu = zhipu_service_config("sk-zhipu", Some("glm-4-flash-250414"));
    assert_eq!(zhipu.model, "glm-4-flash-250414");

    let github = github_models_service_config("ghp-token", None);
    assert_eq!(github.endpoint, GITHUB_MODELS_DEFAULT_ENDPOINT);
    assert_eq!(github.model, GITHUB_MODELS_DEFAULT_MODEL);

    let builtin = built_in_ai_direct_service_config("builtin-key", None);
    assert_eq!(builtin.endpoint, ZHIPU_DEFAULT_ENDPOINT);
    assert_eq!(builtin.model, BUILT_IN_AI_DEFAULT_MODEL);
    assert_eq!(builtin.api_key, "builtin-key");

    let builtin_groq = built_in_ai_direct_service_config("groq-key", Some("llama-3.1-8b-instant"));
    assert_eq!(builtin_groq.endpoint, GROQ_DEFAULT_ENDPOINT);
    assert_eq!(
        built_in_ai_direct_endpoint_for_model("unlisted-direct-model"),
        ZHIPU_DEFAULT_ENDPOINT
    );
}

#[test]
fn openai_format_setting_parser_accepts_persisted_enum_strings() {
    assert_eq!(
        openai_api_format_from_setting(Some("Responses")),
        OpenAiApiFormat::Responses
    );
    assert_eq!(
        openai_api_format_from_setting(Some("ChatCompletions")),
        OpenAiApiFormat::ChatCompletions
    );
    assert_eq!(
        openai_api_format_from_setting(Some("Auto")),
        OpenAiApiFormat::Auto
    );
    assert_eq!(
        openai_api_format_from_setting(Some("unknown")),
        OpenAiApiFormat::Auto
    );
    assert_eq!(openai_api_format_from_setting(None), OpenAiApiFormat::Auto);
}

#[test]
fn config_for_service_uses_settings_snapshot_fields() {
    let settings = SettingsSnapshot {
        open_ai_api_key: Some("sk-openai".to_string()),
        open_ai_endpoint: Some(OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT.to_string()),
        open_ai_model: Some("gpt-5-mini".to_string()),
        open_ai_temperature: Some(0.3),
        open_ai_api_format_override: Some("Responses".to_string()),
        custom_open_ai_endpoint: Some("http://localhost:8080/v1/chat/completions".to_string()),
        custom_open_ai_api_key: Some("custom-key".to_string()),
        custom_open_ai_model: Some("local-model".to_string()),
        ollama_endpoint: Some("http://localhost:11434/v1/chat/completions".to_string()),
        ollama_model: Some("qwen2.5".to_string()),
        built_in_ai_api_key: Some("builtin-key".to_string()),
        built_in_ai_model: Some("llama-3.3-70b-versatile".to_string()),
        deep_seek_api_key: Some("deepseek-key".to_string()),
        deep_seek_model: Some("deepseek-reasoner".to_string()),
        foundry_local_endpoint: Some("http://127.0.0.1:5273/v1".to_string()),
        foundry_local_model: Some("phi-3-mini".to_string()),
        local_ai_provider: Some("FoundryLocal".to_string()),
        ..SettingsSnapshot::default()
    };

    let openai = openai_compatible_config_for_service("openai", &settings).unwrap();
    assert_eq!(openai.api_key, "sk-openai");
    assert_eq!(openai.model, "gpt-5-mini");
    assert_eq!(openai.temperature, 1.0);
    assert_eq!(openai.format_override, OpenAiApiFormat::Responses);

    let custom = openai_compatible_config_for_service("custom-openai", &settings).unwrap();
    assert_eq!(custom.api_key, "custom-key");
    assert_eq!(custom.model, "local-model");
    assert!(!custom.requires_api_key);

    let ollama = openai_compatible_config_for_service("ollama", &settings).unwrap();
    assert_eq!(ollama.model, "qwen2.5");
    assert!(!ollama.requires_api_key);

    let builtin = openai_compatible_config_for_service("builtin", &settings).unwrap();
    assert_eq!(builtin.api_key, "builtin-key");
    assert_eq!(builtin.model, "llama-3.3-70b-versatile");
    assert_eq!(builtin.endpoint, GROQ_DEFAULT_ENDPOINT);

    let deepseek = openai_compatible_config_for_service("deepseek", &settings).unwrap();
    assert_eq!(deepseek.api_key, "deepseek-key");
    assert_eq!(deepseek.model, "deepseek-reasoner");

    let foundry = openai_compatible_config_for_service("windows-local-ai", &settings).unwrap();
    assert_eq!(
        foundry.endpoint,
        "http://127.0.0.1:5273/v1/chat/completions"
    );
    assert_eq!(foundry.model, "phi-3-mini");
    assert!(!foundry.requires_api_key);
    assert_eq!(foundry.format_override, OpenAiApiFormat::ChatCompletions);

    let auto_local_ai = SettingsSnapshot {
        foundry_local_endpoint: Some("http://127.0.0.1:5273/v1".to_string()),
        foundry_local_model: Some("phi-3-mini".to_string()),
        local_ai_provider: Some("Auto".to_string()),
        ..SettingsSnapshot::default()
    };
    let auto_foundry =
        openai_compatible_config_for_service("windows-local-ai", &auto_local_ai).unwrap();
    assert_eq!(
        auto_foundry.endpoint,
        "http://127.0.0.1:5273/v1/chat/completions"
    );
    assert_eq!(auto_foundry.model, "phi-3-mini");
    assert!(openai_compatible_service_can_route_natively(
        "windows-local-ai",
        &settings
    ));
    assert!(openai_compatible_service_can_route_natively(
        "windows-local-ai",
        &auto_local_ai
    ));

    let auto_without_endpoint = SettingsSnapshot {
        local_ai_provider: Some("Auto".to_string()),
        ..SettingsSnapshot::default()
    };
    assert!(
        openai_compatible_config_for_service("windows-local-ai", &auto_without_endpoint).is_none()
    );
    assert!(!openai_compatible_service_can_route_natively(
        "windows-local-ai",
        &auto_without_endpoint
    ));

    let foundry_auto_endpoint = SettingsSnapshot {
        foundry_local_model: Some("qwen2.5-0.5b".to_string()),
        local_ai_provider: Some("FoundryLocal".to_string()),
        ..SettingsSnapshot::default()
    };
    assert!(openai_compatible_service_can_route_natively(
        "windows-local-ai",
        &foundry_auto_endpoint
    ));
    let mut resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://localhost:5273/status".into(),
    ))]);
    let discovered = resolve_openai_compatible_config_for_service(
        "windows-local-ai",
        &foundry_auto_endpoint,
        &mut resolver,
    )
    .unwrap()
    .unwrap();
    assert_eq!(
        discovered.endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(discovered.model, "qwen2.5-0.5b");
    assert_eq!(resolver.calls, 1);
    assert_eq!(resolver.status_calls, 2);
    assert_eq!(resolver.start_calls, 1);
    assert_eq!(resolver.load_model_calls, vec!["qwen2.5-0.5b".to_string()]);

    let builtin_without_user_key = SettingsSnapshot {
        built_in_ai_model: Some("glm-4-flash-250414".to_string()),
        device_id: Some("device-id".to_string()),
        device_token: Some("device-token".to_string()),
        ..SettingsSnapshot::default()
    };
    let builtin_proxy =
        openai_compatible_config_for_service("builtin", &builtin_without_user_key).unwrap();
    assert!(builtin_proxy.endpoint.starts_with("https://"));
    assert!(!builtin_proxy.api_key.is_empty());
    assert_eq!(builtin_proxy.model, "glm-4-flash-250414");
    assert_eq!(
        builtin_proxy.extra_headers,
        vec![
            ("X-Device-Id".to_string(), "device-id".to_string()),
            ("X-Device-Token".to_string(), "device-token".to_string())
        ]
    );
    assert!(openai_compatible_service_can_route_natively(
        "builtin",
        &builtin_without_user_key
    ));
    assert!(openai_compatible_config_for_service("gemini", &settings).is_none());
}

#[test]
fn foundry_local_prepare_starts_loads_and_resolves_runtime_endpoint() {
    let settings = SettingsSnapshot {
        foundry_local_model: Some("qwen2.5-0.5b".to_string()),
        ..SettingsSnapshot::default()
    };
    let mut controller = RecordingFoundryLocalRuntimeController::new([Ok(Some(
        "http://localhost:5273/openai/status".to_string(),
    ))]);

    let outcome = easydict_app::prepare_foundry_local_service(&mut controller, &settings)
        .expect("Foundry Local prepare should succeed");

    assert!(outcome.ready);
    assert_eq!(outcome.model, "qwen2.5-0.5b");
    assert_eq!(
        outcome.endpoint.as_deref(),
        Some("http://localhost:5273/v1/chat/completions")
    );
    assert!(outcome.status_message.contains("Foundry Local is ready"));
    assert_eq!(
        controller.calls,
        vec![
            "get_status".to_string(),
            "start_service".to_string(),
            "load_model:qwen2.5-0.5b".to_string(),
            "get_status".to_string(),
            "resolve_endpoint".to_string()
        ]
    );
}

#[test]
fn foundry_local_prepare_loads_model_when_runtime_is_already_running() {
    let settings = SettingsSnapshot {
        foundry_local_model: Some("qwen2.5-0.5b".to_string()),
        ..SettingsSnapshot::default()
    };
    let mut controller = RecordingFoundryLocalRuntimeController::with_statuses(
        [
            Ok(FoundryLocalRuntimeStatus::with_endpoint(
                FoundryLocalRuntimeState::Running,
                "http://127.0.0.1:5273/openai/status",
            )),
            Ok(FoundryLocalRuntimeStatus::with_endpoint(
                FoundryLocalRuntimeState::Running,
                "http://127.0.0.1:5273/openai/status",
            )),
        ],
        [],
    );

    let outcome = easydict_app::prepare_foundry_local_service(&mut controller, &settings)
        .expect("already-running Foundry runtime should still load model");

    assert!(outcome.ready);
    assert_eq!(
        outcome.endpoint.as_deref(),
        Some("http://127.0.0.1:5273/v1/chat/completions")
    );
    assert_eq!(
        controller.calls,
        vec![
            "get_status".to_string(),
            "load_model:qwen2.5-0.5b".to_string(),
            "get_status".to_string()
        ]
    );
}

#[test]
fn foundry_local_prepare_reports_not_installed_without_start_or_load() {
    let settings = SettingsSnapshot {
        foundry_local_model: Some("qwen2.5-0.5b".to_string()),
        ..SettingsSnapshot::default()
    };
    let mut controller = RecordingFoundryLocalRuntimeController::with_statuses(
        [Ok(FoundryLocalRuntimeStatus::with_detail(
            FoundryLocalRuntimeState::NotInstalled,
            "Foundry Local CLI is not installed or is not available on PATH.",
        ))],
        [],
    );

    let outcome = easydict_app::prepare_foundry_local_service(&mut controller, &settings)
        .expect("not-installed Foundry runtime should be a local non-ready status");

    assert!(!outcome.ready);
    assert!(outcome.status_message.contains("not installed"));
    assert_eq!(controller.calls, vec!["get_status".to_string()]);
}

#[test]
fn foundry_local_prepare_skips_cli_for_user_managed_endpoint() {
    let settings = SettingsSnapshot {
        foundry_local_endpoint: Some("https://foundry.example.test/v1".to_string()),
        foundry_local_model: Some("custom-model".to_string()),
        ..SettingsSnapshot::default()
    };
    let mut controller = RecordingFoundryLocalRuntimeController::new([]);

    let outcome = easydict_app::prepare_foundry_local_service(&mut controller, &settings)
        .expect("user-managed Foundry endpoint should be ready");

    assert!(outcome.ready);
    assert_eq!(outcome.model, "custom-model");
    assert_eq!(
        outcome.endpoint.as_deref(),
        Some("https://foundry.example.test/v1/chat/completions")
    );
    assert!(controller.calls.is_empty());
}

#[test]
fn foundry_local_prepare_refreshes_configured_loopback_endpoint_from_runtime_status() {
    let settings = SettingsSnapshot {
        foundry_local_endpoint: Some("http://127.0.0.1:9890/v1".to_string()),
        foundry_local_model: Some("qwen2.5-0.5b".to_string()),
        ..SettingsSnapshot::default()
    };
    let mut controller = RecordingFoundryLocalRuntimeController::with_statuses(
        [
            Ok(FoundryLocalRuntimeStatus::with_endpoint(
                FoundryLocalRuntimeState::Running,
                "http://127.0.0.1:5273/openai/status",
            )),
            Ok(FoundryLocalRuntimeStatus::with_endpoint(
                FoundryLocalRuntimeState::Running,
                "http://127.0.0.1:5273/openai/status",
            )),
        ],
        [],
    );

    let outcome = easydict_app::prepare_foundry_local_service(&mut controller, &settings)
        .expect("loopback endpoint should be refreshed through runtime lifecycle");

    assert!(outcome.ready);
    assert_eq!(
        outcome.endpoint.as_deref(),
        Some("http://127.0.0.1:5273/v1/chat/completions")
    );
}

#[test]
fn foundry_local_prepare_reports_missing_endpoint_after_start_and_load() {
    let settings = SettingsSnapshot {
        foundry_local_model: Some("qwen2.5-0.5b".to_string()),
        ..SettingsSnapshot::default()
    };
    let mut controller = RecordingFoundryLocalRuntimeController::new([Ok(None)]);

    let outcome = easydict_app::prepare_foundry_local_service(&mut controller, &settings)
        .expect("missing endpoint is reported as non-ready outcome");

    assert!(!outcome.ready);
    assert!(outcome.endpoint.is_none());
    assert!(outcome
        .status_message
        .contains("did not report a local endpoint"));
}

#[test]
fn execute_openai_stream_request_posts_plan_and_parses_chunks() {
    let mut client = RecordingOpenAiHttpClient::with_responses([Ok(
        "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n\
         data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n\
         data: [DONE]\n\n"
            .to_string(),
    )]);
    let plan = build_openai_translation_request_plan(
        &openai_service_config(
            "sk-test",
            Some(OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT),
            Some("gpt-4o-mini"),
            Some(0.3),
            OpenAiApiFormat::Auto,
        ),
        &OpenAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: TranslationLanguage::English,
            to_language: TranslationLanguage::SimplifiedChinese,
            custom_prompt: None,
        },
    )
    .unwrap();

    let chunks = execute_openai_stream_request(&mut client, &plan).unwrap();

    assert_eq!(chunks, vec!["你", "好"]);
    assert_eq!(client.requests.len(), 1);
    assert_eq!(
        client.requests[0].headers[0],
        ("Authorization".to_string(), "Bearer sk-test".to_string())
    );
}

#[test]
fn openai_translation_plan_rejects_dotnet_unsupported_language_pairs() {
    let config = openai_service_config(
        "sk-test",
        Some(OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT),
        Some("gpt-4o-mini"),
        Some(0.3),
        OpenAiApiFormat::Auto,
    );

    let unsupported_target = build_openai_translation_request_plan(
        &config,
        &OpenAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: TranslationLanguage::English,
            to_language: TranslationLanguage::Malay,
            custom_prompt: None,
        },
    )
    .expect_err("Malay is not in .NET BaseOpenAIService.OpenAILanguages");
    assert_eq!(
        unsupported_target,
        OpenAiPlanError::UnsupportedLanguagePair {
            from: TranslationLanguage::English,
            to: TranslationLanguage::Malay,
        }
    );

    let auto_target = build_openai_translation_request_plan(
        &config,
        &OpenAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: TranslationLanguage::English,
            to_language: TranslationLanguage::Auto,
            custom_prompt: None,
        },
    )
    .expect_err("OpenAI-compatible services do not support target Auto");
    assert_eq!(
        auto_target,
        OpenAiPlanError::UnsupportedLanguagePair {
            from: TranslationLanguage::English,
            to: TranslationLanguage::Auto,
        }
    );

    build_openai_translation_request_plan(
        &config,
        &OpenAiTranslationRequest {
            text: "Bonjour".to_string(),
            from_language: TranslationLanguage::Auto,
            to_language: TranslationLanguage::English,
            custom_prompt: None,
        },
    )
    .expect("source Auto with supported target should match .NET BaseTranslationService");
}

#[test]
fn translate_openai_compatible_rejects_unsupported_language_without_http_request() {
    let mut client = RecordingOpenAiHttpClient::default();
    let request = OpenAiTranslationRequest {
        text: "Hello".to_string(),
        from_language: TranslationLanguage::English,
        to_language: TranslationLanguage::Malay,
        custom_prompt: None,
    };

    let error = translate_openai_compatible(
        &mut client,
        &openai_service_config(
            "sk-test",
            Some(OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT),
            Some("gpt-4o-mini"),
            Some(0.3),
            OpenAiApiFormat::Auto,
        ),
        &request,
        "openai",
        "OpenAI",
    )
    .expect_err("unsupported language should fail before the HTTP client");

    assert_eq!(error.code, OpenAiExecutionErrorCode::UnsupportedLanguage);
    assert_eq!(error.service_id.as_deref(), Some("openai"));
    assert_eq!(
        error.message,
        "Language pair not supported: English -> Malay"
    );
    assert!(client.requests.is_empty());
}

#[test]
fn translate_openai_compatible_uses_service_specific_language_tables_before_http_request() {
    let cases = [
        (
            "ollama",
            "Ollama",
            ollama_service_config(None, None),
            TranslationLanguage::Ukrainian,
            "Language pair not supported: English -> Ukrainian",
        ),
        (
            "builtin",
            "Built-in AI",
            built_in_ai_direct_service_config("builtin-user-key", Some("glm-4-flash")),
            TranslationLanguage::Thai,
            "Language pair not supported: English -> Thai",
        ),
    ];

    for (service_id, service_name, config, to_language, expected_message) in cases {
        let mut client = RecordingOpenAiHttpClient::default();
        let request = OpenAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: TranslationLanguage::English,
            to_language,
            custom_prompt: None,
        };

        let error =
            translate_openai_compatible(&mut client, &config, &request, service_id, service_name)
                .expect_err("unsupported language should fail before the HTTP client");

        assert_eq!(error.code, OpenAiExecutionErrorCode::UnsupportedLanguage);
        assert_eq!(error.service_id.as_deref(), Some(service_id));
        assert_eq!(error.message, expected_message);
        assert!(client.requests.is_empty());
    }
}

#[test]
fn translate_openai_compatible_returns_cleaned_translation_dto() {
    let mut client =
        RecordingOpenAiHttpClient::with_responses([Ok("event: response.output_text.delta\n\
         data: {\"type\":\"response.output_text.delta\",\"delta\":\" \\\"Hello\\\" \"}\n\n\
         data: [DONE]\n\n"
            .to_string())]);
    let request = OpenAiTranslationRequest {
        text: "你好".to_string(),
        from_language: TranslationLanguage::SimplifiedChinese,
        to_language: TranslationLanguage::English,
        custom_prompt: None,
    };

    let result = translate_openai_compatible(
        &mut client,
        &openai_service_config("sk-test", None, None, None, OpenAiApiFormat::Auto),
        &request,
        "openai",
        "OpenAI",
    )
    .unwrap();

    assert_eq!(result.translated_text, "Hello");
    assert_eq!(result.service_id.as_deref(), Some("openai"));
    assert_eq!(result.service_name.as_deref(), Some("OpenAI"));
    assert_eq!(result.detected_language.as_deref(), Some("zh"));
    assert_eq!(result.result_kind.as_deref(), Some("Success"));
}

#[test]
fn correct_grammar_openai_compatible_returns_parsed_grammar_dto() {
    let mut client = RecordingOpenAiHttpClient::with_responses([Ok(
        "data: {\"choices\":[{\"delta\":{\"content\":\"[CORRECTED]He goes home.[/CORRECTED]\\n[EXPLANATION]Subject-verb agreement.[/EXPLANATION]\"}}]}\n\n\
         data: [DONE]\n\n"
            .to_string(),
    )]);
    let config = openai_service_config(
        "sk-test",
        Some(OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT),
        Some("gpt-4o-mini"),
        Some(0.3),
        OpenAiApiFormat::Auto,
    );

    let result = correct_grammar_openai_compatible(
        &mut client,
        &config,
        TranslationLanguage::English,
        "He go home.",
        true,
        "openai",
        "OpenAI",
    )
    .unwrap();

    assert_eq!(result.original_text, "He go home.");
    assert_eq!(result.corrected_text, "He goes home.");
    assert_eq!(
        result.explanation.as_deref(),
        Some("Subject-verb agreement.")
    );
    assert!(result.has_corrections);
    assert_eq!(result.service_id.as_deref(), Some("openai"));
}

#[test]
fn openai_error_from_response_maps_status_and_extracts_error_message() {
    let invalid_key = openai_error_from_response(
        401,
        "Unauthorized",
        r#"{"error":{"message":"invalid api key"}}"#,
    );
    assert_eq!(invalid_key.code, OpenAiExecutionErrorCode::InvalidApiKey);
    assert_eq!(invalid_key.message, "invalid api key");

    let rate_limited = openai_error_from_response(429, "Too Many Requests", "");
    assert_eq!(rate_limited.code, OpenAiExecutionErrorCode::RateLimited);
    assert_eq!(rate_limited.message, "API error (429): Too Many Requests");

    let bad_request = openai_error_from_response(400, "Bad Request", "{}");
    assert_eq!(bad_request.code, OpenAiExecutionErrorCode::InvalidResponse);

    let unavailable = openai_error_from_response(503, "Service Unavailable", "{}");
    assert_eq!(
        unavailable.code,
        OpenAiExecutionErrorCode::ServiceUnavailable
    );

    let timeout = openai_error_from_response(504, "Gateway Timeout", "{}");
    assert_eq!(timeout.code, OpenAiExecutionErrorCode::Timeout);
}

fn sample_messages() -> Vec<ChatMessage> {
    vec![
        ChatMessage::new(ChatRole::System, "System instructions"),
        ChatMessage::new(ChatRole::User, "Hello"),
        ChatMessage::new(ChatRole::Assistant, "Earlier context"),
    ]
}

#[derive(Default)]
struct RecordingOpenAiHttpClient {
    requests: Vec<OpenAiHttpRequestPlan>,
    responses: VecDeque<Result<String, OpenAiExecutionError>>,
    get_requests: Vec<OpenAiHttpGetRequestPlan>,
    get_responses: VecDeque<Result<Option<OpenAiHttpTextResponse>, OpenAiExecutionError>>,
}

impl RecordingOpenAiHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
            get_requests: Vec::new(),
            get_responses: VecDeque::new(),
        }
    }

    fn with_get_responses(
        responses: impl IntoIterator<
            Item = Result<Option<OpenAiHttpTextResponse>, OpenAiExecutionError>,
        >,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: VecDeque::new(),
            get_requests: Vec::new(),
            get_responses: responses.into_iter().collect(),
        }
    }
}

impl OpenAiHttpClient for RecordingOpenAiHttpClient {
    fn post_sse(
        &mut self,
        request: &OpenAiHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        self.requests.push(request.clone());
        self.responses
            .pop_front()
            .expect("test OpenAI response should be queued")
    }

    fn get_text(
        &mut self,
        request: &OpenAiHttpGetRequestPlan,
    ) -> Result<Option<OpenAiHttpTextResponse>, OpenAiExecutionError> {
        self.get_requests.push(request.clone());
        self.get_responses
            .pop_front()
            .expect("test OpenAI GET response should be queued")
    }
}

#[derive(Default)]
struct RecordingBuiltInAiDeviceRegistrationHttpClient {
    requests: Vec<easydict_app::BuiltInAiDeviceRegistrationRequestPlan>,
    responses: VecDeque<Result<BuiltInAiDeviceRegistrationHttpResponse, OpenAiExecutionError>>,
}

impl RecordingBuiltInAiDeviceRegistrationHttpClient {
    fn with_responses(
        responses: impl IntoIterator<
            Item = Result<BuiltInAiDeviceRegistrationHttpResponse, OpenAiExecutionError>,
        >,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl BuiltInAiDeviceRegistrationHttpClient for RecordingBuiltInAiDeviceRegistrationHttpClient {
    fn post_device_registration(
        &mut self,
        request: &easydict_app::BuiltInAiDeviceRegistrationRequestPlan,
    ) -> Result<BuiltInAiDeviceRegistrationHttpResponse, OpenAiExecutionError> {
        self.requests.push(request.clone());
        self.responses
            .pop_front()
            .expect("test Built-in AI registration response should be queued")
    }
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    std::env::temp_dir().join(format!("{prefix}-{}-{nanos}", std::process::id()))
}

struct RecordingFoundryLocalEndpointResolver {
    calls: usize,
    status_calls: usize,
    start_calls: usize,
    load_model_calls: Vec<String>,
    responses: VecDeque<Result<Option<String>, FoundryLocalError>>,
}

impl RecordingFoundryLocalEndpointResolver {
    fn new(responses: impl IntoIterator<Item = Result<Option<String>, FoundryLocalError>>) -> Self {
        Self {
            calls: 0,
            status_calls: 0,
            start_calls: 0,
            load_model_calls: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl FoundryLocalEndpointResolver for RecordingFoundryLocalEndpointResolver {
    fn resolve_chat_completions_endpoint(&mut self) -> Result<Option<String>, FoundryLocalError> {
        self.calls += 1;
        self.responses
            .pop_front()
            .expect("test Foundry Local endpoint response should be queued")
    }
}

impl FoundryLocalRuntimeController for RecordingFoundryLocalEndpointResolver {
    fn get_status(&mut self) -> Result<FoundryLocalRuntimeStatus, FoundryLocalError> {
        self.status_calls += 1;
        let state = if self.status_calls == 1 {
            FoundryLocalRuntimeState::NotRunning
        } else {
            FoundryLocalRuntimeState::Running
        };
        Ok(FoundryLocalRuntimeStatus::new(state))
    }

    fn start_service(&mut self) -> Result<(), FoundryLocalError> {
        self.start_calls += 1;
        Ok(())
    }

    fn load_model(&mut self, model: &str) -> Result<(), FoundryLocalError> {
        self.load_model_calls.push(model.to_string());
        Ok(())
    }
}

struct RecordingFoundryLocalRuntimeController {
    calls: Vec<String>,
    endpoint_responses: VecDeque<Result<Option<String>, FoundryLocalError>>,
    status_responses: VecDeque<Result<FoundryLocalRuntimeStatus, FoundryLocalError>>,
}

impl RecordingFoundryLocalRuntimeController {
    fn new(
        endpoint_responses: impl IntoIterator<Item = Result<Option<String>, FoundryLocalError>>,
    ) -> Self {
        Self::with_statuses(
            [
                Ok(FoundryLocalRuntimeStatus::new(
                    FoundryLocalRuntimeState::NotRunning,
                )),
                Ok(FoundryLocalRuntimeStatus::new(
                    FoundryLocalRuntimeState::Running,
                )),
            ],
            endpoint_responses,
        )
    }

    fn with_statuses(
        status_responses: impl IntoIterator<Item = Result<FoundryLocalRuntimeStatus, FoundryLocalError>>,
        endpoint_responses: impl IntoIterator<Item = Result<Option<String>, FoundryLocalError>>,
    ) -> Self {
        Self {
            calls: Vec::new(),
            endpoint_responses: endpoint_responses.into_iter().collect(),
            status_responses: status_responses.into_iter().collect(),
        }
    }
}

impl FoundryLocalEndpointResolver for RecordingFoundryLocalRuntimeController {
    fn resolve_chat_completions_endpoint(&mut self) -> Result<Option<String>, FoundryLocalError> {
        self.calls.push("resolve_endpoint".to_string());
        self.endpoint_responses
            .pop_front()
            .expect("test Foundry endpoint response should be queued")
    }
}

impl FoundryLocalRuntimeController for RecordingFoundryLocalRuntimeController {
    fn get_status(&mut self) -> Result<FoundryLocalRuntimeStatus, FoundryLocalError> {
        self.calls.push("get_status".to_string());
        self.status_responses.pop_front().unwrap_or_else(|| {
            Ok(FoundryLocalRuntimeStatus::new(
                FoundryLocalRuntimeState::Running,
            ))
        })
    }

    fn start_service(&mut self) -> Result<(), FoundryLocalError> {
        self.calls.push("start_service".to_string());
        Ok(())
    }

    fn load_model(&mut self, model: &str) -> Result<(), FoundryLocalError> {
        self.calls.push(format!("load_model:{model}"));
        Ok(())
    }
}
