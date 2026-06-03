use easydict_app::compat_protocol::SettingsSnapshot;
use easydict_app::{
    build_custom_streaming_grammar_request_plan, build_custom_streaming_translation_request_plan,
    build_doubao_translation_request_plan, build_gemini_translation_request_plan,
    cleanup_doubao_translation_text, correct_custom_streaming_grammar,
    custom_streaming_config_for_service, custom_streaming_error_from_response,
    doubao_language_code, doubao_service_config, execute_custom_streaming_request,
    gemini_service_config, parse_doubao_stream_chunks, parse_gemini_stream_chunks,
    translate_custom_streaming_service, CustomStreamingFormat, CustomStreamingHttpClient,
    CustomStreamingHttpRequestPlan, CustomStreamingServiceConfig, OpenAiExecutionError,
    OpenAiExecutionErrorCode, OpenAiTranslationRequest, TranslationLanguage,
    DOUBAO_DEFAULT_ENDPOINT, DOUBAO_DEFAULT_MODEL,
};
use std::collections::VecDeque;

#[test]
fn gemini_translation_request_plan_matches_legacy_streaming_shape() {
    let config =
        gemini_service_config("gemini-key", Some("gemini-2.5-flash-lite")).with_temperature(2.8);
    let request = translation_request();

    let plan = build_gemini_translation_request_plan(&config, &request).unwrap();

    assert_eq!(plan.method, "POST");
    assert!(plan
        .endpoint
        .starts_with("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite:streamGenerateContent?"));
    assert!(plan.endpoint.contains("alt=sse"));
    assert!(plan.endpoint.contains("key=gemini-key"));
    assert!(plan.headers.is_empty());
    assert_eq!(plan.streaming_format, CustomStreamingFormat::Gemini);
    assert_eq!(plan.body["contents"][0]["role"], "user");
    assert_eq!(
        plan.body["contents"][0]["parts"][0]["text"],
        "Translate the following English text into Chinese (Simplified) text: \"\"\"Hello\"\"\""
    );
    assert_eq!(plan.body["generationConfig"]["temperature"], 2.0);
    assert!(plan.body["systemInstruction"]["parts"][0]["text"]
        .as_str()
        .unwrap()
        .contains("Additional instructions: keep punctuation"));
}

#[test]
fn gemini_grammar_request_reuses_shared_prompt_resources() {
    let config = CustomStreamingServiceConfig::Gemini(gemini_service_config("gemini-key", None));

    let plan = build_custom_streaming_grammar_request_plan(
        &config,
        TranslationLanguage::English,
        "He go home.",
        true,
    )
    .unwrap();

    assert_eq!(plan.streaming_format, CustomStreamingFormat::Gemini);
    assert!(plan.body["systemInstruction"]["parts"][0]["text"]
        .as_str()
        .unwrap()
        .contains("briefly list the key corrections"));
    assert!(plan.body["contents"][0]["parts"][0]["text"]
        .as_str()
        .unwrap()
        .contains("The result MUST remain in English"));
}

#[test]
fn doubao_translation_request_plan_matches_translation_options_contract() {
    let config = doubao_service_config(
        "doubao-key",
        Some("https://ark.example.test/api/v3/responses"),
        Some("doubao-test-model"),
    );
    let request = OpenAiTranslationRequest {
        text: "Bonjour".to_string(),
        from_language: TranslationLanguage::French,
        to_language: TranslationLanguage::English,
        custom_prompt: None,
    };

    let plan = build_doubao_translation_request_plan(&config, &request).unwrap();

    assert_eq!(plan.endpoint, "https://ark.example.test/api/v3/responses");
    assert_eq!(
        plan.headers,
        vec![("Authorization".to_string(), "Bearer doubao-key".to_string())]
    );
    assert_eq!(plan.streaming_format, CustomStreamingFormat::Doubao);
    assert_eq!(plan.body["model"], "doubao-test-model");
    assert_eq!(plan.body["stream"], true);
    assert_eq!(
        plan.body["input"][0]["content"][0]["translation_options"]["source_language"],
        "fr"
    );
    assert_eq!(
        plan.body["input"][0]["content"][0]["translation_options"]["target_language"],
        "en"
    );
}

#[test]
fn custom_streaming_config_uses_settings_snapshot_fields() {
    let settings = SettingsSnapshot {
        gemini_api_key: Some("gemini-key".to_string()),
        gemini_model: Some("gemini-2.5-pro".to_string()),
        doubao_api_key: Some("doubao-key".to_string()),
        doubao_endpoint: Some("https://ark.example.test/api/v3/responses".to_string()),
        doubao_model: Some("doubao-model".to_string()),
        ..SettingsSnapshot::default()
    };

    let gemini = custom_streaming_config_for_service("gemini", &settings).unwrap();
    match gemini {
        CustomStreamingServiceConfig::Gemini(config) => {
            assert_eq!(config.api_key, "gemini-key");
            assert_eq!(config.model, "gemini-2.5-pro");
        }
        _ => panic!("expected Gemini config"),
    }

    let doubao = custom_streaming_config_for_service("doubao", &settings).unwrap();
    match doubao {
        CustomStreamingServiceConfig::Doubao(config) => {
            assert_eq!(config.api_key, "doubao-key");
            assert_eq!(config.endpoint, "https://ark.example.test/api/v3/responses");
            assert_eq!(config.model, "doubao-model");
        }
        _ => panic!("expected Doubao config"),
    }

    let defaults =
        custom_streaming_config_for_service("doubao", &SettingsSnapshot::default()).unwrap();
    match defaults {
        CustomStreamingServiceConfig::Doubao(config) => {
            assert_eq!(config.endpoint, DOUBAO_DEFAULT_ENDPOINT);
            assert_eq!(config.model, DOUBAO_DEFAULT_MODEL);
        }
        _ => panic!("expected Doubao defaults"),
    }
    assert!(custom_streaming_config_for_service("openai", &settings).is_none());
}

#[test]
fn custom_streaming_parsers_skip_malformed_frames_and_extract_chunks() {
    let gemini_sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Bon\"}]}}]}\n\n\
                      not-json\n\
                      data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"jour\"}]}}]}\n\n\
                      data: [DONE]\n\n";
    assert_eq!(
        parse_gemini_stream_chunks(gemini_sse),
        vec!["Bon".to_string(), "jour".to_string()]
    );

    let doubao_sse = "event: ping\n\
                      data: {\"delta\":\"ignored\"}\n\n\
                      event: response.output_text.delta\n\
                      data: {\"delta\":\"你\"}\n\n\
                      event: response.output_text.delta\n\
                      data: {\"delta\":\"好\"}\n\n\
                      data: [DONE]\n\n";
    assert_eq!(
        parse_doubao_stream_chunks(doubao_sse),
        vec!["你".to_string(), "好".to_string()]
    );
}

#[test]
fn custom_streaming_execute_translate_and_grammar_return_dtos() {
    let mut client = RecordingCustomStreamingHttpClient::with_responses([
        Ok(gemini_sse(&["Bonjour ", "le monde"])),
        Ok(gemini_sse(&[
            "[CORRECTED]He goes home.[/CORRECTED]\n[EXPLANATION]Subject-verb agreement.[/EXPLANATION]",
        ])),
    ]);
    let config = CustomStreamingServiceConfig::Gemini(gemini_service_config("gemini-key", None));

    let translation = translate_custom_streaming_service(
        &mut client,
        &config,
        &translation_request(),
        "gemini",
        "Gemini",
    )
    .unwrap();
    assert_eq!(translation.translated_text, "Bonjour le monde");
    assert_eq!(translation.service_id.as_deref(), Some("gemini"));
    assert_eq!(translation.service_name.as_deref(), Some("Gemini"));

    let grammar = correct_custom_streaming_grammar(
        &mut client,
        &config,
        TranslationLanguage::English,
        "He go home.",
        true,
        "gemini",
        "Gemini",
    )
    .unwrap();
    assert_eq!(grammar.corrected_text, "He goes home.");
    assert_eq!(
        grammar.explanation.as_deref(),
        Some("Subject-verb agreement.")
    );
    assert!(grammar.has_corrections);
    assert_eq!(client.requests.len(), 2);
}

#[test]
fn doubao_cleanup_language_codes_and_errors_match_service_contract() {
    assert_eq!(cleanup_doubao_translation_text(" 'hello' "), "hello");
    assert_eq!(
        doubao_language_code(TranslationLanguage::TraditionalChinese),
        "zh-Hant"
    );
    assert_eq!(doubao_language_code(TranslationLanguage::Swedish), "sv");

    let invalid_key = custom_streaming_error_from_response(
        401,
        "Unauthorized",
        r#"{"error":{"message":"bad key"}}"#,
    );
    assert_eq!(invalid_key.code, OpenAiExecutionErrorCode::InvalidApiKey);
    assert_eq!(invalid_key.message, "bad key");

    let unavailable = custom_streaming_error_from_response(503, "Service Unavailable", "{}");
    assert_eq!(
        unavailable.code,
        OpenAiExecutionErrorCode::ServiceUnavailable
    );
}

#[test]
fn custom_streaming_plan_validation_rejects_missing_credentials() {
    let gemini = CustomStreamingServiceConfig::Gemini(gemini_service_config("", None));
    let error = build_custom_streaming_translation_request_plan(&gemini, &translation_request())
        .unwrap_err();
    assert_eq!(error.code, OpenAiExecutionErrorCode::InvalidApiKey);
    assert!(error.message.contains("Gemini API key"));

    let doubao = CustomStreamingServiceConfig::Doubao(doubao_service_config("", None, None));
    let error = build_custom_streaming_translation_request_plan(&doubao, &translation_request())
        .unwrap_err();
    assert_eq!(error.code, OpenAiExecutionErrorCode::InvalidApiKey);
    assert!(error.message.contains("Doubao API key"));
}

#[test]
fn custom_streaming_plan_validation_rejects_service_specific_unsupported_languages() {
    let gemini = CustomStreamingServiceConfig::Gemini(gemini_service_config("gemini-key", None));
    let gemini_error = build_custom_streaming_translation_request_plan(
        &gemini,
        &OpenAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: TranslationLanguage::English,
            to_language: TranslationLanguage::Malay,
            custom_prompt: None,
        },
    )
    .unwrap_err();
    assert_eq!(
        gemini_error.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
    assert_eq!(
        gemini_error.message,
        "Language pair not supported: English -> Malay"
    );

    let doubao = CustomStreamingServiceConfig::Doubao(doubao_service_config(
        "doubao-key",
        Some("https://ark.example.test/api/v3/responses"),
        None,
    ));
    let doubao_error = build_custom_streaming_translation_request_plan(
        &doubao,
        &OpenAiTranslationRequest {
            text: "Hello".to_string(),
            from_language: TranslationLanguage::English,
            to_language: TranslationLanguage::Bengali,
            custom_prompt: None,
        },
    )
    .unwrap_err();
    assert_eq!(
        doubao_error.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
    assert_eq!(
        doubao_error.message,
        "Language pair not supported: English -> Bengali"
    );
}

#[test]
fn execute_custom_streaming_request_posts_plan_and_parses_selected_format() {
    let mut client =
        RecordingCustomStreamingHttpClient::with_responses([Ok(doubao_sse(&["你", "好"]))]);
    let plan = CustomStreamingHttpRequestPlan {
        method: "POST",
        endpoint: "https://ark.example.test/api/v3/responses".to_string(),
        headers: Vec::new(),
        body: serde_json::json!({}),
        streaming_format: CustomStreamingFormat::Doubao,
    };

    let chunks = execute_custom_streaming_request(&mut client, &plan).unwrap();

    assert_eq!(chunks, vec!["你".to_string(), "好".to_string()]);
    assert_eq!(client.requests, vec![plan]);
}

#[derive(Default)]
struct RecordingCustomStreamingHttpClient {
    requests: Vec<CustomStreamingHttpRequestPlan>,
    responses: VecDeque<Result<String, OpenAiExecutionError>>,
}

impl RecordingCustomStreamingHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl CustomStreamingHttpClient for RecordingCustomStreamingHttpClient {
    fn post_sse(
        &mut self,
        request: &CustomStreamingHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        self.requests.push(request.clone());
        self.responses
            .pop_front()
            .expect("test streaming response should be queued")
    }
}

fn translation_request() -> OpenAiTranslationRequest {
    OpenAiTranslationRequest {
        text: "Hello".to_string(),
        from_language: TranslationLanguage::English,
        to_language: TranslationLanguage::SimplifiedChinese,
        custom_prompt: Some("keep punctuation".to_string()),
    }
}

fn gemini_sse(chunks: &[&str]) -> String {
    let mut sse = String::new();
    for chunk in chunks {
        sse.push_str("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":");
        sse.push_str(&serde_json::to_string(chunk).unwrap());
        sse.push_str("}]}}]}\n\n");
    }
    sse.push_str("data: [DONE]\n\n");
    sse
}

fn doubao_sse(chunks: &[&str]) -> String {
    let mut sse = String::new();
    for chunk in chunks {
        sse.push_str("event: response.output_text.delta\n");
        sse.push_str("data: {\"delta\":");
        sse.push_str(&serde_json::to_string(chunk).unwrap());
        sse.push_str("}\n\n");
    }
    sse.push_str("data: [DONE]\n\n");
    sse
}
