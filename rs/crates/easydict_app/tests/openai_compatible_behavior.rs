use easydict_app::compat_protocol::SettingsSnapshot;
use easydict_app::{
    build_openai_grammar_messages, build_openai_grammar_request_plan, build_openai_request_body,
    build_openai_translation_messages, build_openai_translation_request_plan,
    built_in_ai_direct_endpoint_for_model, built_in_ai_direct_service_config,
    built_in_ai_proxy_headers, clamp_openai_temperature, cleanup_openai_translation_text,
    correct_grammar_openai_compatible, custom_openai_service_config, deepseek_service_config,
    detect_openai_api_format_from_url, execute_openai_stream_request, github_models_service_config,
    groq_service_config, ollama_model_refresh_fallback, ollama_service_config,
    ollama_tags_url_from_endpoint, openai_api_format_from_setting,
    openai_compatible_config_for_service, openai_effective_temperature, openai_error_from_response,
    openai_responses_reasoning_effort, openai_service_config, parse_ollama_model_names,
    resolve_ollama_model_refresh, resolve_openai_api_format, translate_openai_compatible,
    validate_openai_config, zhipu_service_config, ChatMessage, ChatRole, OpenAiApiFormat,
    OpenAiCompatibleConfig, OpenAiExecutionError, OpenAiExecutionErrorCode, OpenAiHttpClient,
    OpenAiHttpRequestPlan, OpenAiPlanError, OpenAiTranslationRequest, TranslationLanguage,
    BUILT_IN_AI_DEFAULT_MODEL, CUSTOM_OPENAI_DEFAULT_MODEL, DEEPSEEK_DEFAULT_ENDPOINT,
    DEEPSEEK_DEFAULT_MODEL, GITHUB_MODELS_DEFAULT_ENDPOINT, GITHUB_MODELS_DEFAULT_MODEL,
    GROQ_DEFAULT_ENDPOINT, GROQ_DEFAULT_MODEL, OLLAMA_DEFAULT_ENDPOINT, OLLAMA_DEFAULT_MODEL,
    OPENAI_DEFAULT_ENDPOINT, OPENAI_DEFAULT_MODEL, OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT,
    ZHIPU_DEFAULT_ENDPOINT,
};
use std::collections::VecDeque;

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

    let builtin_without_user_key = SettingsSnapshot {
        built_in_ai_model: Some("glm-4-flash-250414".to_string()),
        ..SettingsSnapshot::default()
    };
    assert!(openai_compatible_config_for_service("builtin", &builtin_without_user_key).is_none());
    assert!(openai_compatible_config_for_service("gemini", &settings).is_none());
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
}

impl RecordingOpenAiHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
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
}
