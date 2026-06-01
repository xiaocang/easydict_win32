use easydict_app::compat_protocol::SettingsSnapshot;
use easydict_app::{
    build_caiyun_translation_request_plan, build_deepl_api_translation_request_plan,
    build_google_translation_request_plan, build_niutrans_translation_request_plan,
    build_volcano_translation_request_plan, caiyun_language_code, compute_volcano_authorization,
    deepl_api_error_from_status, deepl_language_code, google_language_code,
    niutrans_error_from_code, niutrans_language_code, parse_caiyun_translation_response,
    parse_deepl_api_translation_response, parse_google_translation_response,
    parse_niutrans_translation_response, parse_volcano_translation_response,
    traditional_http_config_for_service, traditional_http_error_from_status,
    translate_traditional_http_service, volcano_language_code,
    volcano_timestamps_from_epoch_seconds, OpenAiExecutionError, OpenAiExecutionErrorCode,
    TraditionalHttpClient, TraditionalHttpRequestPlan, TraditionalHttpServiceConfig,
    TraditionalHttpServiceKind, TranslationLanguage, CAIYUN_TRANSLATE_ENDPOINT,
    DEEPL_FREE_API_ENDPOINT, DEEPL_PRO_API_ENDPOINT, NIUTRANS_MAX_TEXT_LENGTH_UTF16,
    NIUTRANS_TRANSLATE_ENDPOINT, VOLCANO_MAX_TEXT_LENGTH_UTF16, VOLCANO_TRANSLATE_ENDPOINT,
    VOLCANO_TRANSLATE_HOST,
};
use serde_json::Value;
use std::collections::VecDeque;

#[test]
fn google_translation_request_plan_matches_legacy_gtx_endpoint() {
    let plan = build_google_translation_request_plan(
        "Hello world",
        TranslationLanguage::Auto,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap();

    assert_eq!(plan.method, "GET");
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::Google);
    assert!(plan
        .endpoint
        .starts_with("https://translate.googleapis.com/translate_a/single?"));
    assert!(plan.endpoint.contains("client=gtx"));
    assert!(plan.endpoint.contains("sl=auto"));
    assert!(plan.endpoint.contains("tl=zh-CN"));
    assert!(plan.endpoint.contains("dt=t"));
    assert!(plan.endpoint.contains("dt=bd"));
    assert!(plan.endpoint.contains("dj=1"));
    assert!(plan.endpoint.contains("ie=UTF-8"));
    assert!(plan.endpoint.contains("oe=UTF-8"));
    assert!(plan.endpoint.contains("q=Hello+world") || plan.endpoint.contains("q=Hello%20world"));
    assert!(plan.headers.is_empty());
    assert!(plan.body.is_none());
}

#[test]
fn google_language_codes_preserve_legacy_special_cases() {
    assert_eq!(google_language_code(TranslationLanguage::Auto), "auto");
    assert_eq!(
        google_language_code(TranslationLanguage::SimplifiedChinese),
        "zh-CN"
    );
    assert_eq!(
        google_language_code(TranslationLanguage::TraditionalChinese),
        "zh-TW"
    );
    assert_eq!(google_language_code(TranslationLanguage::Filipino), "tl");
    assert_eq!(google_language_code(TranslationLanguage::English), "en");
}

#[test]
fn google_response_parser_concatenates_sentence_translations_and_detected_language() {
    let json = r#"{
        "sentences": [
            {"trans": "你好", "orig": "Hello"},
            {"trans": "世界", "orig": "world"}
        ],
        "src": "en"
    }"#;

    let result = parse_google_translation_response(
        json,
        "google".to_string(),
        "Google Translate".to_string(),
    )
    .unwrap();

    assert_eq!(result.translated_text, "你好世界");
    assert_eq!(result.service_id.as_deref(), Some("google"));
    assert_eq!(result.service_name.as_deref(), Some("Google Translate"));
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    assert_eq!(result.result_kind.as_deref(), Some("Success"));
}

#[test]
fn caiyun_translation_request_plan_matches_legacy_json_payload() {
    let plan = build_caiyun_translation_request_plan(
        "caiyun-token",
        "Hello",
        TranslationLanguage::Auto,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.endpoint, CAIYUN_TRANSLATE_ENDPOINT);
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::Caiyun);
    assert_eq!(
        plan.headers,
        vec![
            ("Content-Type".to_string(), "application/json".to_string()),
            (
                "X-Authorization".to_string(),
                "token caiyun-token".to_string()
            )
        ]
    );

    let body: Value = serde_json::from_str(plan.body.as_deref().unwrap()).unwrap();
    assert_eq!(body["source"], serde_json::json!(["Hello"]));
    assert_eq!(body["trans_type"], "auto2zh");
    assert_eq!(body["media"], "text");
    assert_eq!(body["request_id"].as_str().unwrap().len(), 36);
}

#[test]
fn deepl_api_translation_request_plan_matches_legacy_official_api_mode() {
    let plan = build_deepl_api_translation_request_plan(
        "deepl-key:fx",
        true,
        "Hello world",
        TranslationLanguage::English,
        TranslationLanguage::Portuguese,
    )
    .unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.endpoint, DEEPL_FREE_API_ENDPOINT);
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::DeepLApi);
    assert_eq!(
        plan.headers,
        vec![
            (
                "Authorization".to_string(),
                "DeepL-Auth-Key deepl-key:fx".to_string()
            ),
            (
                "Content-Type".to_string(),
                "application/x-www-form-urlencoded".to_string()
            )
        ]
    );
    let body = plan.body.as_deref().unwrap();
    assert!(body.contains("text=Hello+world") || body.contains("text=Hello%20world"));
    assert!(body.contains("target_lang=PT"));
    assert!(body.contains("source_lang=EN"));
    assert!(body.contains("model_type=quality_optimized"));

    let pro_plan = build_deepl_api_translation_request_plan(
        "deepl-pro-key",
        false,
        "Hello",
        TranslationLanguage::Auto,
        TranslationLanguage::TraditionalChinese,
    )
    .unwrap();
    assert_eq!(pro_plan.endpoint, DEEPL_PRO_API_ENDPOINT);
    assert!(!pro_plan.body.as_deref().unwrap().contains("source_lang="));
    assert!(pro_plan
        .body
        .as_deref()
        .unwrap()
        .contains("target_lang=ZH-HANT"));
}

#[test]
fn niutrans_translation_request_plan_matches_legacy_json_payload_and_limits() {
    let plan = build_niutrans_translation_request_plan(
        "niu-key",
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::TraditionalChinese,
    )
    .unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.endpoint, NIUTRANS_TRANSLATE_ENDPOINT);
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::NiuTrans);
    assert_eq!(
        plan.headers,
        vec![("Content-Type".to_string(), "application/json".to_string())]
    );

    let body: Value = serde_json::from_str(plan.body.as_deref().unwrap()).unwrap();
    assert_eq!(body["apikey"], "niu-key");
    assert_eq!(body["src_text"], "Hello");
    assert_eq!(body["from"], "en");
    assert_eq!(body["to"], "cht");
    assert_eq!(body["source"], "Easydict");

    let too_long = "a".repeat(NIUTRANS_MAX_TEXT_LENGTH_UTF16 + 1);
    let error = build_niutrans_translation_request_plan(
        "niu-key",
        &too_long,
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap_err();
    assert_eq!(error.code, OpenAiExecutionErrorCode::TextTooLong);
}

#[test]
fn caiyun_and_niutrans_language_codes_preserve_legacy_special_cases() {
    assert_eq!(
        caiyun_language_code(TranslationLanguage::TraditionalChinese).unwrap(),
        "zh-Hant"
    );
    assert_eq!(
        caiyun_language_code(TranslationLanguage::Indonesian).unwrap(),
        "id"
    );
    assert_eq!(
        niutrans_language_code(TranslationLanguage::TraditionalChinese).unwrap(),
        "cht"
    );
    assert_eq!(
        niutrans_language_code(TranslationLanguage::Filipino).unwrap(),
        "fil"
    );
    assert_eq!(
        caiyun_language_code(TranslationLanguage::ClassicalChinese)
            .unwrap_err()
            .code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
}

#[test]
fn deepl_language_codes_preserve_api_and_web_special_cases() {
    assert_eq!(
        deepl_language_code(TranslationLanguage::SimplifiedChinese, false),
        "ZH"
    );
    assert_eq!(
        deepl_language_code(TranslationLanguage::TraditionalChinese, false),
        "ZH-HANT"
    );
    assert_eq!(
        deepl_language_code(TranslationLanguage::Portuguese, false),
        "PT"
    );
    assert_eq!(
        deepl_language_code(TranslationLanguage::Portuguese, true),
        "PT-PT"
    );
    assert_eq!(
        deepl_language_code(TranslationLanguage::Norwegian, false),
        "NB"
    );
}

#[test]
fn traditional_http_config_routes_native_traditional_providers() {
    let settings = SettingsSnapshot {
        caiyun_token: Some("caiyun-token".to_string()),
        deep_l_api_key: Some("deepl-key".to_string()),
        deep_l_use_free_api: Some(false),
        deep_l_use_quality_optimized: Some(false),
        niu_trans_api_key: Some("niu-key".to_string()),
        volcano_access_key_id: Some("volcano-akid".to_string()),
        volcano_secret_access_key: Some("volcano-secret".to_string()),
        ..SettingsSnapshot::default()
    };

    assert_eq!(
        traditional_http_config_for_service("google", &settings),
        Some(TraditionalHttpServiceConfig::Google)
    );
    assert_eq!(
        traditional_http_config_for_service("caiyun", &settings),
        Some(TraditionalHttpServiceConfig::Caiyun {
            api_key: "caiyun-token".to_string()
        })
    );
    assert_eq!(
        traditional_http_config_for_service("deepl", &settings),
        Some(TraditionalHttpServiceConfig::DeepLApi {
            api_key: "deepl-key".to_string(),
            use_quality_optimized: false
        })
    );
    assert_eq!(
        traditional_http_config_for_service("niutrans", &settings),
        Some(TraditionalHttpServiceConfig::NiuTrans {
            api_key: "niu-key".to_string()
        })
    );
    assert_eq!(
        traditional_http_config_for_service("volcano", &settings),
        Some(TraditionalHttpServiceConfig::Volcano {
            access_key_id: "volcano-akid".to_string(),
            secret_access_key: "volcano-secret".to_string(),
        })
    );
    assert!(traditional_http_config_for_service("deepl", &SettingsSnapshot::default()).is_none());
    assert!(traditional_http_config_for_service("bing", &SettingsSnapshot::default()).is_none());
}

#[test]
fn caiyun_and_niutrans_response_parsers_match_legacy_fallbacks_and_errors() {
    let caiyun = parse_caiyun_translation_response(
        r#"{"target":["你好","世界"]}"#,
        "Hello world",
        "caiyun".to_string(),
        "Caiyun".to_string(),
    )
    .unwrap();
    assert_eq!(caiyun.translated_text, "你好世界");
    assert_eq!(caiyun.detected_language, None);

    let caiyun_fallback = parse_caiyun_translation_response(
        r#"{"target":[]}"#,
        "Hello",
        "caiyun".to_string(),
        "Caiyun".to_string(),
    )
    .unwrap();
    assert_eq!(caiyun_fallback.translated_text, "Hello");

    let deepl = parse_deepl_api_translation_response(
        r#"{"translations":[{"detected_source_language":"EN","text":"Bonjour"}]}"#,
        "deepl".to_string(),
        "DeepL".to_string(),
    )
    .unwrap();
    assert_eq!(deepl.translated_text, "Bonjour");
    assert_eq!(deepl.detected_language.as_deref(), Some("en"));

    let deepl_invalid = parse_deepl_api_translation_response(
        r#"{"translations":[]}"#,
        "deepl".to_string(),
        "DeepL".to_string(),
    )
    .unwrap_err();
    assert_eq!(
        deepl_invalid.code,
        OpenAiExecutionErrorCode::InvalidResponse
    );

    let niutrans = parse_niutrans_translation_response(
        r#"{"tgt_text":"你好","from":"en","to":"zh"}"#,
        "Hello",
        "niutrans".to_string(),
        "NiuTrans".to_string(),
    )
    .unwrap();
    assert_eq!(niutrans.translated_text, "你好");
    assert_eq!(niutrans.detected_language, None);

    let niutrans_error = parse_niutrans_translation_response(
        r#"{"error_code":"13003","error_msg":"apikey is invalid"}"#,
        "Hello",
        "niutrans".to_string(),
        "NiuTrans".to_string(),
    )
    .unwrap_err();
    assert_eq!(niutrans_error.code, OpenAiExecutionErrorCode::InvalidApiKey);
    assert_eq!(niutrans_error.service_id.as_deref(), Some("niutrans"));
}

#[test]
fn translate_traditional_http_service_executes_plan_and_returns_dto() {
    let mut client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"{"sentences":[{"trans":"Bonjour"}],"src":"en"}"#.to_string(),
    )]);

    let result = translate_traditional_http_service(
        &mut client,
        &TraditionalHttpServiceConfig::Google,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::French,
        "google",
        "Google Translate",
    )
    .unwrap();

    assert_eq!(result.translated_text, "Bonjour");
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    assert_eq!(client.requests.len(), 1);
    assert!(client.requests[0].endpoint.contains("sl=en"));
    assert!(client.requests[0].endpoint.contains("tl=fr"));

    let mut caiyun_client =
        RecordingTraditionalHttpClient::with_responses([Ok(r#"{"target":["你好"]}"#.to_string())]);
    let caiyun = translate_traditional_http_service(
        &mut caiyun_client,
        &TraditionalHttpServiceConfig::Caiyun {
            api_key: "caiyun-token".to_string(),
        },
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "caiyun",
        "Caiyun",
    )
    .unwrap();
    assert_eq!(caiyun.translated_text, "你好");
    assert_eq!(caiyun_client.requests[0].method, "POST");
    assert_eq!(
        caiyun_client.requests[0].service_kind,
        TraditionalHttpServiceKind::Caiyun
    );

    let mut deepl_client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"{"translations":[{"detected_source_language":"EN","text":"Salut"}]}"#.to_string(),
    )]);
    let deepl = translate_traditional_http_service(
        &mut deepl_client,
        &TraditionalHttpServiceConfig::DeepLApi {
            api_key: "deepl-key".to_string(),
            use_quality_optimized: false,
        },
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::French,
        "deepl",
        "DeepL",
    )
    .unwrap();
    assert_eq!(deepl.translated_text, "Salut");
    assert_eq!(
        deepl_client.requests[0].service_kind,
        TraditionalHttpServiceKind::DeepLApi
    );

    let mut niutrans_client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"{"tgt_text":"Bonjour"}"#.to_string(),
    )]);
    let niutrans = translate_traditional_http_service(
        &mut niutrans_client,
        &TraditionalHttpServiceConfig::NiuTrans {
            api_key: "niu-key".to_string(),
        },
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::French,
        "niutrans",
        "NiuTrans",
    )
    .unwrap();
    assert_eq!(niutrans.translated_text, "Bonjour");
    assert_eq!(
        niutrans_client.requests[0].service_kind,
        TraditionalHttpServiceKind::NiuTrans
    );
}

#[test]
fn parser_status_and_provider_errors_are_classified() {
    let invalid = parse_google_translation_response(
        "not json",
        "google".to_string(),
        "Google Translate".to_string(),
    )
    .unwrap_err();
    assert_eq!(invalid.code, OpenAiExecutionErrorCode::InvalidResponse);
    assert_eq!(invalid.service_id.as_deref(), Some("google"));

    let invalid_key = traditional_http_error_from_status(401, "Unauthorized");
    assert_eq!(invalid_key.code, OpenAiExecutionErrorCode::InvalidApiKey);

    let rate_limited = traditional_http_error_from_status(429, "Too Many Requests");
    assert_eq!(rate_limited.code, OpenAiExecutionErrorCode::RateLimited);

    let unavailable = traditional_http_error_from_status(503, "Service Unavailable");
    assert_eq!(
        unavailable.code,
        OpenAiExecutionErrorCode::ServiceUnavailable
    );

    assert_eq!(
        niutrans_error_from_code("13004", "balance insufficient").code,
        OpenAiExecutionErrorCode::RateLimited
    );
    assert_eq!(
        deepl_api_error_from_status(456, "Quota Exceeded").code,
        OpenAiExecutionErrorCode::RateLimited
    );
}

#[test]
fn volcano_compute_authorization_matches_dotnet_known_answer() {
    // Known-answer vector cross-checked against the legacy .NET
    // VolcanoService.ComputeAuthorization signing for the exact fixed inputs in
    // VolcanoServiceTests (AKID12345 / SecretKey12345 / fixed body / 20240101T120000Z).
    // This proves the SigV4 canonical-request port byte-for-byte, which the
    // format/determinism assertions alone cannot.
    let body = br#"{"TargetLanguage":"zh","TextList":["Hello"]}"#;
    let authorization = compute_volcano_authorization(
        "AKID12345",
        "SecretKey12345",
        body,
        "20240101T120000Z",
        "20240101",
    );

    assert_eq!(
        authorization,
        "HMAC-SHA256 Credential=AKID12345/20240101/cn-north-1/translate/request, \
         SignedHeaders=content-type;host;x-date, \
         Signature=c2978c8ab175b4f4b1ea0caf6f81d721fd55ab42adcac34f378b56fd3564ba43"
    );
}

#[test]
fn volcano_compute_authorization_is_deterministic_and_body_sensitive() {
    let body_a = br#"{"TargetLanguage":"zh","TextList":["Hello"]}"#;
    let body_b = br#"{"TargetLanguage":"zh","TextList":["World"]}"#;
    let auth_a1 = compute_volcano_authorization(
        "AKID12345",
        "SecretKey12345",
        body_a,
        "20240101T120000Z",
        "20240101",
    );
    let auth_a2 = compute_volcano_authorization(
        "AKID12345",
        "SecretKey12345",
        body_a,
        "20240101T120000Z",
        "20240101",
    );
    let auth_b = compute_volcano_authorization(
        "AKID12345",
        "SecretKey12345",
        body_b,
        "20240101T120000Z",
        "20240101",
    );

    assert_eq!(auth_a1, auth_a2);
    assert_ne!(auth_a1, auth_b);
    assert!(auth_a1
        .starts_with("HMAC-SHA256 Credential=AKID12345/20240101/cn-north-1/translate/request,"));
}

#[test]
fn volcano_timestamps_from_epoch_match_utc_vector() {
    // 1704110400 == 2024-01-01T12:00:00Z. Guards the hand-rolled civil-from-days
    // conversion against leap-year / off-by-one bugs the regex shape test misses.
    let stamps = volcano_timestamps_from_epoch_seconds(1_704_110_400);
    assert_eq!(stamps.x_date, "20240101T120000Z");
    assert_eq!(stamps.short_date, "20240101");

    // Unix epoch itself.
    let epoch = volcano_timestamps_from_epoch_seconds(0);
    assert_eq!(epoch.x_date, "19700101T000000Z");
    assert_eq!(epoch.short_date, "19700101");

    // Mid-year / leap-boundary / year-end vectors exercise the Mar–Dec branch of the
    // civil-from-days conversion (the January vectors above only hit the Jan/Feb branch).
    // Ground truth from datetime.utcfromtimestamp.
    let mid_year = volcano_timestamps_from_epoch_seconds(1_721_051_130);
    assert_eq!(mid_year.x_date, "20240715T134530Z");
    assert_eq!(mid_year.short_date, "20240715");

    let leap_march = volcano_timestamps_from_epoch_seconds(1_709_251_200);
    assert_eq!(leap_march.x_date, "20240301T000000Z");
    assert_eq!(leap_march.short_date, "20240301");

    let year_end = volcano_timestamps_from_epoch_seconds(1_735_689_599);
    assert_eq!(year_end.x_date, "20241231T235959Z");
    assert_eq!(year_end.short_date, "20241231");
}

#[test]
fn volcano_translation_request_plan_signs_and_omits_auto_source() {
    let plan = build_volcano_translation_request_plan(
        "test-access-key",
        "test-secret-key",
        "Hello",
        TranslationLanguage::Auto,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::Volcano);
    assert_eq!(plan.endpoint, VOLCANO_TRANSLATE_ENDPOINT);
    assert!(plan
        .endpoint
        .ends_with("?Action=TranslateText&Version=2020-06-01"));

    let body = plan.body.as_deref().unwrap();
    assert!(body.contains(r#""TargetLanguage":"zh""#));
    assert!(body.contains(r#""TextList":["Hello"]"#));
    assert!(!body.contains("SourceLanguage"));

    let header = |name: &str| {
        plan.headers
            .iter()
            .find(|(key, _)| key == name)
            .map(|(_, value)| value.as_str())
    };
    assert_eq!(header("Host"), Some(VOLCANO_TRANSLATE_HOST));
    assert_eq!(header("Content-Type"), Some("application/json"));

    let x_date = header("X-Date").expect("X-Date header");
    assert_eq!(x_date.len(), 16);
    assert!(x_date.ends_with('Z') && x_date.as_bytes()[8] == b'T');

    let authorization = header("Authorization").expect("Authorization header");
    assert!(authorization.starts_with("HMAC-SHA256 Credential=test-access-key/"));
    assert!(authorization.contains("SignedHeaders=content-type;host;x-date,"));
    assert!(authorization.contains("Signature="));
}

#[test]
fn volcano_translation_request_plan_includes_explicit_source_and_validates() {
    let plan = build_volcano_translation_request_plan(
        "akid",
        "secret",
        "你好",
        TranslationLanguage::SimplifiedChinese,
        TranslationLanguage::English,
    )
    .unwrap();
    let body = plan.body.as_deref().unwrap();
    assert!(body.contains(r#""SourceLanguage":"zh""#));
    assert!(body.contains(r#""TargetLanguage":"en""#));

    // Missing credentials map to InvalidApiKey, matching the legacy not-configured guard.
    let unconfigured = build_volcano_translation_request_plan(
        "",
        "secret",
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap_err();
    assert_eq!(unconfigured.code, OpenAiExecutionErrorCode::InvalidApiKey);

    // Oversized text is rejected before signing.
    let long_text = "a".repeat(VOLCANO_MAX_TEXT_LENGTH_UTF16 + 1);
    let too_long = build_volcano_translation_request_plan(
        "akid",
        "secret",
        &long_text,
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap_err();
    assert_eq!(too_long.code, OpenAiExecutionErrorCode::TextTooLong);
}

#[test]
fn volcano_language_codes_preserve_legacy_special_cases() {
    assert_eq!(
        volcano_language_code(TranslationLanguage::Auto).unwrap(),
        ""
    );
    assert_eq!(
        volcano_language_code(TranslationLanguage::TraditionalChinese).unwrap(),
        "zh-Hant"
    );
    assert_eq!(
        volcano_language_code(TranslationLanguage::ClassicalChinese).unwrap(),
        "lzh"
    );
    assert_eq!(
        volcano_language_code(TranslationLanguage::Norwegian).unwrap(),
        "no"
    );
    assert!(volcano_language_code(TranslationLanguage::Greek).is_err());
}

#[test]
fn volcano_response_parser_matches_legacy_fallbacks_and_errors() {
    let success = parse_volcano_translation_response(
        r#"{"TranslationList":[{"Translation":"你好，世界！","DetectedSourceLanguage":"en"}],"ResponseMetadata":{}}"#,
        "Hello, world!",
        "volcano".to_string(),
        "Volcano".to_string(),
    )
    .unwrap();
    assert_eq!(success.translated_text, "你好，世界！");
    assert_eq!(success.detected_language.as_deref(), Some("en"));

    // Empty/missing translation falls back to the original text.
    let fallback = parse_volcano_translation_response(
        r#"{"TranslationList":[],"ResponseMetadata":{}}"#,
        "Hello",
        "volcano".to_string(),
        "Volcano".to_string(),
    )
    .unwrap();
    assert_eq!(fallback.translated_text, "Hello");
    assert_eq!(fallback.detected_language, None);

    // API-level error is surfaced as ServiceUnavailable with the message text.
    let error = parse_volcano_translation_response(
        r#"{"TranslationList":null,"ResponseMetadata":{"Error":{"Code":"InvalidParameter","Message":"Invalid source language"}}}"#,
        "Hello",
        "volcano".to_string(),
        "Volcano".to_string(),
    )
    .unwrap_err();
    assert_eq!(error.code, OpenAiExecutionErrorCode::ServiceUnavailable);
    assert!(error.message.contains("Invalid source language"));
    assert_eq!(error.service_id.as_deref(), Some("volcano"));
}

#[test]
fn translate_traditional_http_service_executes_volcano_plan() {
    let mut client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"{"TranslationList":[{"Translation":"你好","DetectedSourceLanguage":"en"}],"ResponseMetadata":{}}"#
            .to_string(),
    )]);
    let result = translate_traditional_http_service(
        &mut client,
        &TraditionalHttpServiceConfig::Volcano {
            access_key_id: "akid".to_string(),
            secret_access_key: "secret".to_string(),
        },
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "volcano",
        "Volcano",
    )
    .unwrap();

    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    assert_eq!(client.requests.len(), 1);
    assert_eq!(client.requests[0].method, "POST");
    assert_eq!(
        client.requests[0].service_kind,
        TraditionalHttpServiceKind::Volcano
    );
    assert!(client.requests[0]
        .headers
        .iter()
        .any(|(key, _)| key == "Authorization"));
}

#[derive(Default)]
struct RecordingTraditionalHttpClient {
    requests: Vec<TraditionalHttpRequestPlan>,
    responses: VecDeque<Result<String, OpenAiExecutionError>>,
}

impl RecordingTraditionalHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl TraditionalHttpClient for RecordingTraditionalHttpClient {
    fn execute(
        &mut self,
        request: &TraditionalHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        self.requests.push(request.clone());
        self.responses
            .pop_front()
            .expect("test traditional HTTP response should be queued")
    }
}
