use easydict_app::compat_protocol::SettingsSnapshot;
use easydict_app::{
    apply_deepl_dynamic_spacing, bing_credentials_expired, bing_host, bing_language_code,
    build_bing_translate_request_plan, build_caiyun_translation_request_plan,
    build_deepl_api_translation_request_plan, build_deepl_web_translation_request_plan_with_values,
    build_google_translation_request_plan, build_google_web_translation_request_plan,
    build_linguee_translation_request_plan, build_niutrans_translation_request_plan,
    build_volcano_translation_request_plan,
    build_youdao_openapi_translation_request_plan_with_nonce,
    build_youdao_web_dict_translation_request_plan,
    build_youdao_web_translate_key_request_plan_with_time,
    build_youdao_web_translate_request_plan_with_time, caiyun_language_code,
    compute_volcano_authorization, compute_youdao_openapi_sign, compute_youdao_web_dict_sign,
    compute_youdao_web_translate_sign, decrypt_youdao_web_translate_response,
    deepl_aligned_timestamp, deepl_api_error_from_status, deepl_i_count, deepl_language_code,
    deepl_web_error_from_status, from_bing_language_code, google_language_code,
    linguee_language_code, niutrans_error_from_code, niutrans_language_code,
    parse_bing_credentials_from_html, parse_bing_translation_response,
    parse_caiyun_translation_response, parse_deepl_api_translation_response,
    parse_deepl_web_translation_response, parse_google_translation_response,
    parse_google_web_translation_response, parse_linguee_translation_response,
    parse_niutrans_translation_response, parse_volcano_translation_response,
    parse_youdao_openapi_response, parse_youdao_web_dict_response,
    parse_youdao_web_translate_key_response, parse_youdao_web_translate_response,
    traditional_http_config_for_request, traditional_http_config_for_service,
    traditional_http_error_from_status, traditional_http_supports_language_pair_for_kind,
    translate_bing_service, translate_deepl_web_service, translate_traditional_http_service,
    translate_youdao_web_translate_service, volcano_language_code,
    volcano_timestamps_from_epoch_seconds, youdao_language_code, youdao_openapi_error_from_code,
    youdao_openapi_signature_input, youdao_web_dict_language_code, youdao_web_dict_time,
    youdao_web_translate_error_from_code, BingCredentials, BingHttpClient, BingHttpResponse,
    BingTranslatorPage, OpenAiExecutionError, OpenAiExecutionErrorCode, TraditionalHttpClient,
    TraditionalHttpRequestPlan, TraditionalHttpServiceConfig, TraditionalHttpServiceKind,
    TranslationLanguage, BING_CHINA_HOST, BING_GLOBAL_HOST, BING_MAX_TEXT_LENGTH_UTF16,
    BING_USER_AGENT, CAIYUN_TRANSLATE_ENDPOINT, DEEPL_FREE_API_ENDPOINT, DEEPL_PRO_API_ENDPOINT,
    DEEPL_WEB_ENDPOINT, DEEPL_WEB_USER_AGENT, GOOGLE_TRANSLATE_ENDPOINT,
    LINGUEE_TRANSLATE_ENDPOINT, NIUTRANS_MAX_TEXT_LENGTH_UTF16, NIUTRANS_TRANSLATE_ENDPOINT,
    VOLCANO_MAX_TEXT_LENGTH_UTF16, VOLCANO_TRANSLATE_ENDPOINT, VOLCANO_TRANSLATE_HOST,
    YOUDAO_DICT_VOICE_ENDPOINT, YOUDAO_OPENAPI_ENDPOINT, YOUDAO_WEB_DICT_ENDPOINT,
    YOUDAO_WEB_INITIAL_SIGN_KEY, YOUDAO_WEB_TRANSLATE_ENDPOINT, YOUDAO_WEB_TRANSLATE_KEY_ENDPOINT,
    YOUDAO_WEB_USER_AGENT,
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
fn google_web_request_plan_and_parser_preserve_rich_dictionary_data() {
    let plan = build_google_web_translation_request_plan(
        "hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap();

    assert_eq!(plan.method, "GET");
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::GoogleWeb);
    assert!(plan.endpoint.starts_with(GOOGLE_TRANSLATE_ENDPOINT));
    assert!(plan.endpoint.contains("client=gtx"));
    assert!(plan.endpoint.contains("sl=en"));
    assert!(plan.endpoint.contains("tl=zh-CN"));
    assert!(plan.endpoint.contains("dt=md"));
    assert!(plan.endpoint.contains("dt=ex"));
    assert!(!plan.endpoint.contains("dj=1"));

    let json = r#"[
        [["你好","hello",null,"heh-loh"]],
        [
            ["int.",["used as a greeting"]],
            ["noun",[],[["greeting"]]]
        ],
        "en",
        null,null,null,null,null,
        [["en"]],
        null,null,null,null,
        [[["<b>Hello</b>, world."]]]
    ]"#;
    let result = parse_google_web_translation_response(
        json,
        "google_web".to_string(),
        "Google Dict".to_string(),
    )
    .unwrap();

    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("google_web"));
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    let word = result.word_result.expect("word result");
    assert_eq!(
        word.phonetics.as_deref().unwrap()[0].text.as_deref(),
        Some("heh-loh")
    );
    assert_eq!(
        word.phonetics.as_deref().unwrap()[0].accent.as_deref(),
        Some("src")
    );
    assert_eq!(word.definitions.as_deref().unwrap().len(), 2);
    assert_eq!(
        word.definitions.as_deref().unwrap()[0]
            .part_of_speech
            .as_deref(),
        Some("int.")
    );
    assert_eq!(
        word.definitions.as_deref().unwrap()[1].meanings.as_deref(),
        Some(&["greeting".to_string()][..])
    );
    assert_eq!(
        word.examples.as_deref(),
        Some(&["Hello, world.".to_string()][..])
    );
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
fn deepl_web_request_plan_matches_legacy_jsonrpc_payload_and_anti_detection_values() {
    assert_eq!(deepl_i_count("initial idiom"), 5);
    assert_eq!(deepl_i_count("INITIAL"), 0);
    assert_eq!(
        deepl_aligned_timestamp(1_700_000_000_001, 0),
        1_700_000_000_001
    );
    assert_eq!(
        deepl_aligned_timestamp(1_700_000_000_001, 2),
        1_700_000_000_004
    );
    assert_eq!(
        apply_deepl_dynamic_spacing(r#"{"method":"LMT_handle_texts"}"#, 100_000_000),
        r#"{"method": "LMT_handle_texts"}"#
    );
    assert_eq!(
        apply_deepl_dynamic_spacing(r#"{"method":"LMT_handle_texts"}"#, 100_002_000),
        r#"{"method" : "LMT_handle_texts"}"#
    );

    let plan = build_deepl_web_translation_request_plan_with_values(
        "initial idiom",
        TranslationLanguage::Auto,
        TranslationLanguage::Portuguese,
        100_000_000,
        1_700_000_000_002,
    )
    .unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.endpoint, DEEPL_WEB_ENDPOINT);
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::DeepLWeb);
    assert_eq!(
        plan.headers,
        vec![
            ("Accept".to_string(), "*/*".to_string()),
            ("Accept-Language".to_string(), "en-US,en;q=0.9".to_string()),
            ("Origin".to_string(), "https://www.deepl.com".to_string()),
            ("Referer".to_string(), "https://www.deepl.com/".to_string()),
            ("User-Agent".to_string(), DEEPL_WEB_USER_AGENT.to_string()),
            ("Content-Type".to_string(), "application/json".to_string())
        ]
    );
    let body = plan.body.as_deref().unwrap();
    assert!(body.contains(r#""method": "LMT_handle_texts""#));
    let payload: Value = serde_json::from_str(body).unwrap();
    assert_eq!(payload["jsonrpc"], "2.0");
    assert_eq!(payload["method"], "LMT_handle_texts");
    assert_eq!(payload["id"], 100_000_000);
    assert_eq!(payload["params"]["texts"][0]["text"], "initial idiom");
    assert_eq!(payload["params"]["texts"][0]["requestAlternatives"], 3);
    assert_eq!(payload["params"]["splitting"], "newlines");
    assert_eq!(
        payload["params"]["lang"]["source_lang_user_selected"],
        "AUTO"
    );
    assert_eq!(payload["params"]["lang"]["target_lang"], "PT-PT");
    assert_eq!(payload["params"]["timestamp"], 1_700_000_000_002_i64);
    assert_eq!(
        payload["params"]["commonJobParams"]["wasSpoken"],
        serde_json::json!(false)
    );
    assert_eq!(payload["params"]["commonJobParams"]["transcribe_as"], "");

    let explicit_source = build_deepl_web_translation_request_plan_with_values(
        "Hallo",
        TranslationLanguage::German,
        TranslationLanguage::TraditionalChinese,
        100_002_000,
        1_700_000_000_000,
    )
    .unwrap();
    let explicit_body = explicit_source.body.as_deref().unwrap();
    assert!(explicit_body.contains(r#""method" : "LMT_handle_texts""#));
    let explicit_payload: Value = serde_json::from_str(explicit_body).unwrap();
    assert_eq!(
        explicit_payload["params"]["lang"]["source_lang_user_selected"],
        "DE"
    );
    assert_eq!(explicit_payload["params"]["lang"]["target_lang"], "ZH-HANT");
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
fn youdao_openapi_request_plan_matches_legacy_signing_fields() {
    assert_eq!(youdao_openapi_signature_input("Hello world"), "Hello world");
    assert_eq!(
        youdao_openapi_signature_input("abcdefghijklmnopqrstuvwxyz"),
        "abcdefghij26qrstuvwxyz"
    );
    assert_eq!(
        compute_youdao_openapi_sign(
            "app-key",
            "Hello world",
            "salt-123",
            "1700000000",
            "secret-key",
        ),
        "2e8576d1d1176176f0f319c87951e5162e8528ac84938d8e8bb601fcc227f706"
    );

    let plan = build_youdao_openapi_translation_request_plan_with_nonce(
        "app-key",
        "secret-key",
        "Hello world",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "salt-123",
        "1700000000",
    )
    .unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.endpoint, YOUDAO_OPENAPI_ENDPOINT);
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::YoudaoOpenApi);
    assert_eq!(
        plan.headers,
        vec![(
            "Content-Type".to_string(),
            "application/x-www-form-urlencoded".to_string()
        )]
    );

    let body = plan.body.as_deref().unwrap();
    assert_eq!(form_field(body, "q").as_deref(), Some("Hello world"));
    assert_eq!(form_field(body, "from").as_deref(), Some("en"));
    assert_eq!(form_field(body, "to").as_deref(), Some("zh-CHS"));
    assert_eq!(form_field(body, "appKey").as_deref(), Some("app-key"));
    assert_eq!(form_field(body, "salt").as_deref(), Some("salt-123"));
    assert_eq!(form_field(body, "signType").as_deref(), Some("v3"));
    assert_eq!(form_field(body, "curtime").as_deref(), Some("1700000000"));
    assert_eq!(
        form_field(body, "sign").as_deref(),
        Some("2e8576d1d1176176f0f319c87951e5162e8528ac84938d8e8bb601fcc227f706")
    );

    let missing_key = build_youdao_openapi_translation_request_plan_with_nonce(
        "",
        "secret-key",
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "salt",
        "1700000000",
    )
    .unwrap_err();
    assert_eq!(missing_key.code, OpenAiExecutionErrorCode::InvalidApiKey);
}

#[test]
fn youdao_web_dict_request_plan_matches_legacy_signing_fields() {
    assert_eq!(youdao_web_dict_time("hello"), 2);
    assert_eq!(
        compute_youdao_web_dict_sign("hello", 2, "792d60634898f156a2922172dbba9de2"),
        "3c71569a04e3231adce6ef811c67148a"
    );
    assert_eq!(
        youdao_web_dict_language_code(
            TranslationLanguage::SimplifiedChinese,
            TranslationLanguage::Japanese,
        ),
        "ja"
    );
    assert_eq!(
        youdao_web_dict_language_code(TranslationLanguage::French, TranslationLanguage::English),
        "fr"
    );
    assert_eq!(
        youdao_web_dict_language_code(TranslationLanguage::Spanish, TranslationLanguage::English),
        "en"
    );

    let plan = build_youdao_web_dict_translation_request_plan(
        "hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(
        plan.endpoint,
        format!("{YOUDAO_WEB_DICT_ENDPOINT}?doctype=json&jsonversion=4")
    );
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::YoudaoWebDict);
    assert_eq!(
        plan.headers,
        vec![
            (
                "Content-Type".to_string(),
                "application/x-www-form-urlencoded".to_string()
            ),
            (
                "User-Agent".to_string(),
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36".to_string()
            ),
            (
                "Referer".to_string(),
                "https://dict.youdao.com/".to_string()
            )
        ]
    );

    let body = plan.body.as_deref().unwrap();
    assert_eq!(form_field(body, "q").as_deref(), Some("hello"));
    assert_eq!(form_field(body, "le").as_deref(), Some("en"));
    assert_eq!(form_field(body, "client").as_deref(), Some("web"));
    assert_eq!(form_field(body, "t").as_deref(), Some("2"));
    assert_eq!(form_field(body, "keyfrom").as_deref(), Some("webdict"));
    assert_eq!(
        form_field(body, "sign").as_deref(),
        Some("3c71569a04e3231adce6ef811c67148a")
    );

    let missing_text = build_youdao_web_dict_translation_request_plan(
        " ",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
    )
    .unwrap_err();
    assert_eq!(missing_text.code, OpenAiExecutionErrorCode::InvalidApiKey);
}

#[test]
fn youdao_webtranslate_request_plans_match_legacy_dynamic_key_flow() {
    assert_eq!(
        compute_youdao_web_translate_sign(YOUDAO_WEB_INITIAL_SIGN_KEY, "1700000000000"),
        "c5590df3965ca5ab9a84cc67267ab8f0"
    );
    assert_eq!(
        compute_youdao_web_translate_sign("secret-key", "1700000001234"),
        "a59f89f68a03bf124843fdc3ee9bffa7"
    );

    let key_plan = build_youdao_web_translate_key_request_plan_with_time("1700000000000").unwrap();
    assert_eq!(key_plan.method, "GET");
    assert_eq!(
        key_plan.service_kind,
        TraditionalHttpServiceKind::YoudaoWebTranslateKey
    );
    assert!(key_plan.endpoint.starts_with(&format!(
        "{YOUDAO_WEB_TRANSLATE_KEY_ENDPOINT}?keyid=webfanyi-key-getter"
    )));
    assert!(key_plan
        .endpoint
        .contains("sign=c5590df3965ca5ab9a84cc67267ab8f0"));
    assert!(key_plan.endpoint.contains("mysticTime=1700000000000"));
    assert_eq!(
        key_plan.headers,
        vec![
            ("User-Agent".to_string(), YOUDAO_WEB_USER_AGENT.to_string()),
            (
                "Referer".to_string(),
                "https://fanyi.youdao.com/".to_string()
            ),
            ("Origin".to_string(), "https://fanyi.youdao.com".to_string())
        ]
    );

    let translate_plan = build_youdao_web_translate_request_plan_with_time(
        "secret-key",
        "Hello world",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "1700000001234",
    )
    .unwrap();
    assert_eq!(translate_plan.method, "POST");
    assert_eq!(translate_plan.endpoint, YOUDAO_WEB_TRANSLATE_ENDPOINT);
    assert_eq!(
        translate_plan.service_kind,
        TraditionalHttpServiceKind::YoudaoWebTranslate
    );
    assert_eq!(
        translate_plan.headers,
        vec![
            (
                "Content-Type".to_string(),
                "application/x-www-form-urlencoded".to_string()
            ),
            ("User-Agent".to_string(), YOUDAO_WEB_USER_AGENT.to_string()),
            (
                "Referer".to_string(),
                "https://fanyi.youdao.com/".to_string()
            ),
            ("Origin".to_string(), "https://fanyi.youdao.com".to_string()),
            (
                "Cookie".to_string(),
                "OUTFOX_SEARCH_USER_ID=0@0.0.0.0".to_string()
            )
        ]
    );
    let body = translate_plan.body.as_deref().unwrap();
    assert_eq!(form_field(body, "i").as_deref(), Some("Hello world"));
    assert_eq!(form_field(body, "from").as_deref(), Some("en"));
    assert_eq!(form_field(body, "to").as_deref(), Some("zh-CHS"));
    assert_eq!(form_field(body, "dictResult").as_deref(), Some("true"));
    assert_eq!(form_field(body, "keyid").as_deref(), Some("webfanyi"));
    assert_eq!(
        form_field(body, "sign").as_deref(),
        Some("a59f89f68a03bf124843fdc3ee9bffa7")
    );
    assert_eq!(form_field(body, "client").as_deref(), Some("fanyideskweb"));
    assert_eq!(form_field(body, "product").as_deref(), Some("webfanyi"));
    assert_eq!(
        form_field(body, "mysticTime").as_deref(),
        Some("1700000001234")
    );
    assert_eq!(form_field(body, "keyfrom").as_deref(), Some("fanyi.web"));
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
fn youdao_language_codes_preserve_legacy_special_cases() {
    assert_eq!(youdao_language_code(TranslationLanguage::Auto), "auto");
    assert_eq!(
        youdao_language_code(TranslationLanguage::SimplifiedChinese),
        "zh-CHS"
    );
    assert_eq!(
        youdao_language_code(TranslationLanguage::TraditionalChinese),
        "zh-CHT"
    );
    assert_eq!(youdao_language_code(TranslationLanguage::Indonesian), "id");
    assert_eq!(youdao_language_code(TranslationLanguage::Persian), "en");
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
fn traditional_http_language_preflight_matches_dotnet_supported_language_tables() {
    assert!(traditional_http_supports_language_pair_for_kind(
        TraditionalHttpServiceKind::Google,
        TranslationLanguage::Auto,
        TranslationLanguage::English,
    ));
    assert!(!traditional_http_supports_language_pair_for_kind(
        TraditionalHttpServiceKind::Google,
        TranslationLanguage::English,
        TranslationLanguage::Auto,
    ));
    assert!(!traditional_http_supports_language_pair_for_kind(
        TraditionalHttpServiceKind::DeepLWeb,
        TranslationLanguage::English,
        TranslationLanguage::Arabic,
    ));
    assert!(traditional_http_supports_language_pair_for_kind(
        TraditionalHttpServiceKind::Caiyun,
        TranslationLanguage::Auto,
        TranslationLanguage::Auto,
    ));
    assert!(traditional_http_supports_language_pair_for_kind(
        TraditionalHttpServiceKind::Linguee,
        TranslationLanguage::Auto,
        TranslationLanguage::German,
    ));
    assert!(!traditional_http_supports_language_pair_for_kind(
        TraditionalHttpServiceKind::Linguee,
        TranslationLanguage::English,
        TranslationLanguage::Korean,
    ));

    let google_auto_target = build_google_translation_request_plan(
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::Auto,
    )
    .unwrap_err();
    assert_eq!(
        google_auto_target.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
    assert_eq!(
        google_auto_target.message,
        "Language pair not supported: English -> Auto"
    );

    let deepl_unsupported = build_deepl_web_translation_request_plan_with_values(
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::Arabic,
        123,
        456,
    )
    .unwrap_err();
    assert_eq!(
        deepl_unsupported.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );

    let youdao_unsupported = build_youdao_openapi_translation_request_plan_with_nonce(
        "app-key",
        "secret-key",
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::Persian,
        "salt",
        "1700000000",
    )
    .unwrap_err();
    assert_eq!(
        youdao_unsupported.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
    assert_eq!(
        youdao_unsupported.message,
        "Language pair not supported: English -> Persian"
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
        youdao_app_key: Some("youdao-key".to_string()),
        youdao_app_secret: Some("youdao-secret".to_string()),
        youdao_use_official_api: Some(true),
        ..SettingsSnapshot::default()
    };

    assert_eq!(
        traditional_http_config_for_service("google", &settings),
        Some(TraditionalHttpServiceConfig::Google)
    );
    assert_eq!(
        traditional_http_config_for_service("google_web", &settings),
        Some(TraditionalHttpServiceConfig::GoogleWeb)
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
    assert_eq!(
        traditional_http_config_for_service("youdao", &settings),
        Some(TraditionalHttpServiceConfig::YoudaoOpenApi {
            app_key: "youdao-key".to_string(),
            app_secret: "youdao-secret".to_string(),
        })
    );
    assert_eq!(
        traditional_http_config_for_service("deepl", &SettingsSnapshot::default()),
        Some(TraditionalHttpServiceConfig::DeepLWeb {
            fallback_api_key: None
        })
    );
    assert_eq!(
        traditional_http_config_for_service(
            "deepl",
            &SettingsSnapshot {
                deep_l_api_key: Some("deepl-key:fx".to_string()),
                deep_l_use_free_api: Some(true),
                deep_l_use_quality_optimized: Some(false),
                ..SettingsSnapshot::default()
            },
        ),
        Some(TraditionalHttpServiceConfig::DeepLWeb {
            fallback_api_key: Some("deepl-key:fx".to_string())
        })
    );
    assert!(traditional_http_config_for_service("bing", &SettingsSnapshot::default()).is_none());
    assert!(traditional_http_config_for_service("youdao", &SettingsSnapshot::default()).is_none());

    assert_eq!(
        traditional_http_config_for_request("youdao", &SettingsSnapshot::default(), "hello"),
        Some(TraditionalHttpServiceConfig::YoudaoWebDict)
    );
    assert_eq!(
        traditional_http_config_for_request(
            "youdao",
            &SettingsSnapshot::default(),
            "Hello world. This should use sentence translation.",
        ),
        Some(TraditionalHttpServiceConfig::YoudaoWebTranslate)
    );
    assert_eq!(
        traditional_http_config_for_request("youdao", &settings, "hello"),
        Some(TraditionalHttpServiceConfig::YoudaoOpenApi {
            app_key: "youdao-key".to_string(),
            app_secret: "youdao-secret".to_string(),
        })
    );
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

    let deepl_web = parse_deepl_web_translation_response(
        r#"{"jsonrpc":"2.0","id":100000000,"result":{"texts":[{"text":"Bonjour"}],"lang":"EN"}}"#,
        "deepl".to_string(),
        "DeepL".to_string(),
    )
    .unwrap();
    assert_eq!(deepl_web.translated_text, "Bonjour");
    assert_eq!(deepl_web.detected_language.as_deref(), Some("en"));

    let deepl_web_auto = parse_deepl_web_translation_response(
        r#"{"result":{"texts":[{"text":"Bonjour"}],"lang":"AUTO"}}"#,
        "deepl".to_string(),
        "DeepL".to_string(),
    )
    .unwrap();
    assert_eq!(deepl_web_auto.detected_language, None);

    let deepl_web_error = parse_deepl_web_translation_response(
        r#"{"error":{"message":"blocked"}}"#,
        "deepl".to_string(),
        "DeepL".to_string(),
    )
    .unwrap_err();
    assert_eq!(
        deepl_web_error.code,
        OpenAiExecutionErrorCode::ServiceUnavailable
    );
    assert_eq!(deepl_web_error.service_id.as_deref(), Some("deepl"));

    let deepl_web_invalid = parse_deepl_web_translation_response(
        r#"{"result":{"texts":[]}}"#,
        "deepl".to_string(),
        "DeepL".to_string(),
    )
    .unwrap_err();
    assert_eq!(
        deepl_web_invalid.code,
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
fn youdao_openapi_response_parser_matches_legacy_phonetics_definitions_and_errors() {
    let result = parse_youdao_openapi_response(
        r#"{
            "errorCode":"0",
            "translation":["你好","世界"],
            "basic":{
                "us-phonetic":"həˈloʊ",
                "us-speech":"https://dict.youdao.com/dictvoice?audio=hello&type=1",
                "uk-phonetic":"həˈləʊ",
                "uk-speech":"https://dict.youdao.com/dictvoice?audio=hello&type=2",
                "explains":["int. 喂；你好","a test"]
            },
            "l":"en2zh-CHS"
        }"#,
        "hello",
        TranslationLanguage::Auto,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap();

    assert_eq!(result.translated_text, "你好 世界");
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    let word = result.word_result.expect("word result");
    assert_eq!(word.phonetics.as_deref().unwrap().len(), 2);
    assert_eq!(
        word.phonetics.as_deref().unwrap()[0].accent.as_deref(),
        Some("US")
    );
    assert_eq!(
        word.phonetics.as_deref().unwrap()[0].audio_url.as_deref(),
        Some("https://dict.youdao.com/dictvoice?audio=hello&type=1")
    );
    assert_eq!(
        word.definitions.as_deref().unwrap()[0]
            .part_of_speech
            .as_deref(),
        Some("int")
    );
    assert_eq!(
        word.definitions.as_deref().unwrap()[0].meanings.as_deref(),
        Some(&["喂；你好".to_string()][..])
    );
    assert_eq!(
        word.definitions.as_deref().unwrap()[1]
            .part_of_speech
            .as_deref(),
        None
    );

    let fallback = parse_youdao_openapi_response(
        r#"{"errorCode":"0","translation":[]}"#,
        "hello",
        TranslationLanguage::English,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap();
    assert_eq!(fallback.translated_text, "hello");
    assert_eq!(fallback.detected_language.as_deref(), Some("en"));

    let invalid_key = parse_youdao_openapi_response(
        r#"{"errorCode":"401"}"#,
        "hello",
        TranslationLanguage::English,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap_err();
    assert_eq!(invalid_key.code, OpenAiExecutionErrorCode::InvalidApiKey);

    let rate_limited = youdao_openapi_error_from_code("411");
    assert_eq!(rate_limited.code, OpenAiExecutionErrorCode::RateLimited);
}

#[test]
fn youdao_web_dict_response_parser_preserves_rich_dictionary_payloads() {
    let json = r#"{
        "simple": {
            "word": [{
                "usphone": "həˈloʊ",
                "usspeech": "hello&type=1",
                "ukphone": "həˈləʊ",
                "ukspeech": "hello&type=2"
            }]
        },
        "ec": {
            "word": {
                "trs": [
                    {"pos": "int.", "tran": "喂；你好"},
                    {"pos": "n.", "tran": "招呼"}
                ],
                "wfs": [
                    {"wf": {"name": "复数", "value": "hellos"}},
                    {"wf": {"name": "过去式", "value": "ran或run"}}
                ]
            }
        },
        "syno": {
            "synos": [
                {
                    "pos": "n.",
                    "tran": "问候",
                    "ws": ["greeting", {"w": "salutation"}, {"w": ""}]
                }
            ]
        }
    }"#;

    let result = parse_youdao_web_dict_response(
        json,
        "hello",
        TranslationLanguage::Auto,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap();

    assert_eq!(result.translated_text, "int. 喂；你好\nn. 招呼");
    assert_eq!(result.service_id.as_deref(), Some("youdao"));
    assert_eq!(result.service_name.as_deref(), Some("Youdao"));
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    let word = result.word_result.expect("word result");
    let phonetics = word.phonetics.as_deref().unwrap();
    assert_eq!(phonetics.len(), 2);
    assert_eq!(phonetics[0].text.as_deref(), Some("həˈloʊ"));
    assert_eq!(phonetics[0].accent.as_deref(), Some("US"));
    let expected_audio_url = format!("{YOUDAO_DICT_VOICE_ENDPOINT}?audio=hello%26type%3D1");
    assert_eq!(
        phonetics[0].audio_url.as_deref(),
        Some(expected_audio_url.as_str())
    );
    assert_eq!(phonetics[1].accent.as_deref(), Some("UK"));

    let definitions = word.definitions.as_deref().unwrap();
    assert_eq!(definitions.len(), 2);
    assert_eq!(definitions[0].part_of_speech.as_deref(), Some("int."));
    assert_eq!(
        definitions[0].meanings.as_deref(),
        Some(&["喂；你好".to_string()][..])
    );

    let forms = word.word_forms.as_deref().unwrap();
    assert_eq!(forms.len(), 3);
    assert_eq!(forms[0].name.as_deref(), Some("复数"));
    assert_eq!(forms[0].value.as_deref(), Some("hellos"));
    assert_eq!(forms[1].value.as_deref(), Some("ran"));
    assert_eq!(forms[2].value.as_deref(), Some("run"));

    let synonyms = word.synonyms.as_deref().unwrap();
    assert_eq!(synonyms.len(), 1);
    assert_eq!(synonyms[0].part_of_speech.as_deref(), Some("n."));
    assert_eq!(synonyms[0].meaning.as_deref(), Some("问候"));
    assert_eq!(
        synonyms[0].words.as_deref(),
        Some(&["greeting".to_string(), "salutation".to_string()][..])
    );

    let phonetic_from_ec = parse_youdao_web_dict_response(
        r#"{"ec":{"word":[{"usphone":"test","trs":[{"tran":"测试"}]}]}}"#,
        "test",
        TranslationLanguage::English,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap();
    assert_eq!(phonetic_from_ec.translated_text, "测试");
    assert!(phonetic_from_ec
        .word_result
        .and_then(|word| word.phonetics)
        .is_some());

    let empty = parse_youdao_web_dict_response(
        r#"{}"#,
        "unknownword",
        TranslationLanguage::English,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap();
    assert_eq!(empty.translated_text, "unknownword");
    assert!(empty.word_result.is_none());
}

#[test]
fn youdao_webtranslate_key_parser_decryptor_and_response_parser_match_legacy_flow() {
    assert_eq!(
        parse_youdao_web_translate_key_response(
            r#"{"code":0,"data":{"secretKey":"secret-key"}}"#,
            "youdao",
        )
        .unwrap(),
        "secret-key"
    );
    assert_eq!(
        parse_youdao_web_translate_key_response(
            r#"{"code":"0","data":{"secretKey":"secret-key"}}"#,
            "youdao",
        )
        .unwrap(),
        "secret-key"
    );
    let key_error =
        parse_youdao_web_translate_key_response(r#"{"code":50,"msg":"blocked"}"#, "youdao")
            .unwrap_err();
    assert_eq!(key_error.code, OpenAiExecutionErrorCode::ServiceUnavailable);
    assert_eq!(key_error.service_id.as_deref(), Some("youdao"));

    let encrypted =
        "ScQHaWyubalVm0oANN1RKWxp0NJCkqMCE0kvVUaF9zo5aq4Y3OyhyY08w4GiOzFkOG_Zz2ODjqT4BVKtooveKQ";
    let decrypted = decrypt_youdao_web_translate_response(encrypted).unwrap();
    assert_eq!(
        decrypted,
        r#"{"translateResult":[[{"tgt":"你好","src":"Hello"}]],"code":0}"#
    );

    let nested = parse_youdao_web_translate_response(
        &decrypted,
        "Hello",
        TranslationLanguage::English,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap();
    assert_eq!(nested.translated_text, "你好");
    assert_eq!(nested.detected_language.as_deref(), Some("en"));
    assert!(nested.word_result.is_none());

    let flat = parse_youdao_web_translate_response(
        r#"{"translateResult":[{"tgt":"Bonjour","src":"Hello"}],"code":"0"}"#,
        "Hello",
        TranslationLanguage::Auto,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap();
    assert_eq!(flat.translated_text, "Bonjour");
    assert_eq!(flat.detected_language.as_deref(), Some("auto"));

    let rate_limited = parse_youdao_web_translate_response(
        r#"{"code":50}"#,
        "Hello",
        TranslationLanguage::English,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap_err();
    assert_eq!(rate_limited.code, OpenAiExecutionErrorCode::RateLimited);
    assert_eq!(
        youdao_web_translate_error_from_code(51).code,
        OpenAiExecutionErrorCode::ServiceUnavailable
    );

    let no_result = parse_youdao_web_translate_response(
        r#"{"translateResult":[],"code":0}"#,
        "Hello",
        TranslationLanguage::English,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap_err();
    assert_eq!(no_result.code, OpenAiExecutionErrorCode::ServiceUnavailable);
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

    let mut google_web_client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"[[["你好","hello",null,"heh-loh"]],[],"en"]"#.to_string(),
    )]);
    let google_web = translate_traditional_http_service(
        &mut google_web_client,
        &TraditionalHttpServiceConfig::GoogleWeb,
        "hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "google_web",
        "Google Dict",
    )
    .unwrap();
    assert_eq!(google_web.translated_text, "你好");
    assert_eq!(
        google_web_client.requests[0].service_kind,
        TraditionalHttpServiceKind::GoogleWeb
    );
    assert!(google_web.word_result.unwrap().phonetics.is_some());

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

    let mut deepl_web_client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"{"result":{"texts":[{"text":"Hallo Welt"}],"lang":"EN"}}"#.to_string(),
    )]);
    let deepl_web = translate_traditional_http_service(
        &mut deepl_web_client,
        &TraditionalHttpServiceConfig::DeepLWeb {
            fallback_api_key: None,
        },
        "Hello world",
        TranslationLanguage::English,
        TranslationLanguage::German,
        "deepl",
        "DeepL",
    )
    .unwrap();
    assert_eq!(deepl_web.translated_text, "Hallo Welt");
    assert_eq!(deepl_web.detected_language.as_deref(), Some("en"));
    assert_eq!(
        deepl_web_client.requests[0].service_kind,
        TraditionalHttpServiceKind::DeepLWeb
    );

    let mut deepl_fallback_client = RecordingTraditionalHttpClient::with_responses([
        Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::ServiceUnavailable,
            "web unavailable",
        )),
        Ok(r#"{"translations":[{"detected_source_language":"EN","text":"Fallback"}]}"#.to_string()),
    ]);
    let deepl_fallback = translate_deepl_web_service(
        &mut deepl_fallback_client,
        Some("deepl-key:fx"),
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::French,
        "deepl".to_string(),
        "DeepL".to_string(),
    )
    .unwrap();
    assert_eq!(deepl_fallback.translated_text, "Fallback");
    assert_eq!(deepl_fallback_client.requests.len(), 2);
    assert_eq!(
        deepl_fallback_client.requests[0].service_kind,
        TraditionalHttpServiceKind::DeepLWeb
    );
    assert_eq!(
        deepl_fallback_client.requests[1].service_kind,
        TraditionalHttpServiceKind::DeepLApi
    );
    assert_eq!(
        form_field(
            deepl_fallback_client.requests[1].body.as_deref().unwrap(),
            "target_lang"
        )
        .as_deref(),
        Some("FR")
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

    let mut youdao_client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"{"errorCode":"0","translation":["你好"],"l":"en2zh-CHS"}"#.to_string(),
    )]);
    let youdao = translate_traditional_http_service(
        &mut youdao_client,
        &TraditionalHttpServiceConfig::YoudaoOpenApi {
            app_key: "youdao-key".to_string(),
            app_secret: "youdao-secret".to_string(),
        },
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "youdao",
        "Youdao",
    )
    .unwrap();
    assert_eq!(youdao.translated_text, "你好");
    assert_eq!(
        youdao_client.requests[0].service_kind,
        TraditionalHttpServiceKind::YoudaoOpenApi
    );
    assert_eq!(
        form_field(youdao_client.requests[0].body.as_deref().unwrap(), "appKey").as_deref(),
        Some("youdao-key")
    );

    let mut youdao_dict_client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"{"ec":{"word":{"trs":[{"pos":"int.","tran":"喂；你好"}]}}}"#.to_string(),
    )]);
    let youdao_dict = translate_traditional_http_service(
        &mut youdao_dict_client,
        &TraditionalHttpServiceConfig::YoudaoWebDict,
        "hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "youdao",
        "Youdao",
    )
    .unwrap();
    assert_eq!(youdao_dict.translated_text, "int. 喂；你好");
    assert_eq!(
        youdao_dict_client.requests[0].service_kind,
        TraditionalHttpServiceKind::YoudaoWebDict
    );
    assert_eq!(
        form_field(
            youdao_dict_client.requests[0].body.as_deref().unwrap(),
            "keyfrom"
        )
        .as_deref(),
        Some("webdict")
    );

    let mut youdao_webtranslate_client = RecordingTraditionalHttpClient::with_responses([
        Ok(r#"{"code":0,"data":{"secretKey":"secret-key"}}"#.to_string()),
        Ok(r#"{"translateResult":[[{"tgt":"你好","src":"Hello"}]],"code":0}"#.to_string()),
    ]);
    let youdao_webtranslate = translate_traditional_http_service(
        &mut youdao_webtranslate_client,
        &TraditionalHttpServiceConfig::YoudaoWebTranslate,
        "Hello world.",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "youdao",
        "Youdao",
    )
    .unwrap();
    assert_eq!(youdao_webtranslate.translated_text, "你好");
    assert_eq!(youdao_webtranslate_client.requests.len(), 2);
    assert_eq!(
        youdao_webtranslate_client.requests[0].service_kind,
        TraditionalHttpServiceKind::YoudaoWebTranslateKey
    );
    assert_eq!(
        youdao_webtranslate_client.requests[1].service_kind,
        TraditionalHttpServiceKind::YoudaoWebTranslate
    );

    let mut youdao_dict_fallback_client = RecordingTraditionalHttpClient::with_responses([
        Ok(r#"{}"#.to_string()),
        Ok(r#"{"code":0,"data":{"secretKey":"secret-key"}}"#.to_string()),
        Ok(r#"{"translateResult":[{"tgt":"fallback","src":"unknownword"}],"code":0}"#.to_string()),
    ]);
    let youdao_dict_fallback = translate_traditional_http_service(
        &mut youdao_dict_fallback_client,
        &TraditionalHttpServiceConfig::YoudaoWebDict,
        "unknownword",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "youdao",
        "Youdao",
    )
    .unwrap();
    assert_eq!(youdao_dict_fallback.translated_text, "fallback");
    assert_eq!(youdao_dict_fallback_client.requests.len(), 3);
    assert_eq!(
        youdao_dict_fallback_client.requests[0].service_kind,
        TraditionalHttpServiceKind::YoudaoWebDict
    );
    assert_eq!(
        youdao_dict_fallback_client.requests[2].service_kind,
        TraditionalHttpServiceKind::YoudaoWebTranslate
    );
}

#[test]
fn traditional_http_language_preflight_runs_before_two_phase_http_requests() {
    let mut bing_client = FakeBingHttpClient::new(
        BING_HTML_SAMPLE,
        Vec::<Result<BingHttpResponse, OpenAiExecutionError>>::new(),
    );
    let bing_error = translate_bing_service(
        &mut bing_client,
        BING_GLOBAL_HOST,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::ClassicalChinese,
        "bing",
        "Bing Translate",
    )
    .unwrap_err();
    assert_eq!(
        bing_error.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
    assert_eq!(bing_error.service_id.as_deref(), Some("bing"));
    assert_eq!(bing_client.fetch_count, 0);
    assert!(bing_client.translate_plans.is_empty());

    let mut youdao_client = RecordingTraditionalHttpClient::default();
    let youdao_error = translate_youdao_web_translate_service(
        &mut youdao_client,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::Persian,
        "youdao".to_string(),
        "Youdao".to_string(),
    )
    .unwrap_err();
    assert_eq!(
        youdao_error.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
    assert_eq!(youdao_error.service_id.as_deref(), Some("youdao"));
    assert!(youdao_client.requests.is_empty());

    let mut youdao_dict_client = RecordingTraditionalHttpClient::default();
    let youdao_dict_error = translate_traditional_http_service(
        &mut youdao_dict_client,
        &TraditionalHttpServiceConfig::YoudaoWebDict,
        "hello",
        TranslationLanguage::English,
        TranslationLanguage::Persian,
        "youdao",
        "Youdao",
    )
    .unwrap_err();
    assert_eq!(
        youdao_dict_error.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
    assert!(youdao_dict_client.requests.is_empty());
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
    assert_eq!(
        deepl_web_error_from_status(429, "Too Many Requests").code,
        OpenAiExecutionErrorCode::ServiceUnavailable
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

const BING_HTML_SAMPLE: &str = r#"
<html><head>
<script>var _G = {IG:"A1B2C3D4E5F6",}; </script>
<div class="rms_iml" data-iid="translator.5028.3"></div>
<script>
var params_AbusePreventionHelper = [1700000000000,"abusetoken_XyZ",3600000];
</script>
</head></html>
"#;

#[test]
fn bing_credentials_parse_extracts_session_fields() {
    let credentials = parse_bing_credentials_from_html(BING_HTML_SAMPLE).unwrap();
    assert_eq!(
        credentials,
        BingCredentials {
            ig: "A1B2C3D4E5F6".to_string(),
            iid: "translator.5028.3".to_string(),
            token: "abusetoken_XyZ".to_string(),
            key: 1_700_000_000_000,
            expiry_interval_ms: 3_600_000,
        }
    );
}

#[test]
fn bing_credentials_parse_defaults_iid_and_rejects_missing_token() {
    // No data-iid → legacy default IID; no IG → empty (caller generates one).
    let html = r#"<script>params_AbusePreventionHelper = [42,"tok",60000];</script>"#;
    let credentials = parse_bing_credentials_from_html(html).unwrap();
    assert_eq!(credentials.iid, "translator.5023.1");
    assert_eq!(credentials.ig, "");
    assert_eq!(credentials.token, "tok");

    // Missing abuse-prevention params is a hard service error.
    let error = parse_bing_credentials_from_html("<html>no creds here</html>").unwrap_err();
    assert_eq!(error.code, OpenAiExecutionErrorCode::ServiceUnavailable);
    assert_eq!(error.service_id.as_deref(), Some("bing"));
}

#[test]
fn bing_translate_request_plan_matches_legacy_endpoint_headers_and_body() {
    let credentials = BingCredentials {
        ig: "IGVALUE".to_string(),
        iid: "translator.5028.3".to_string(),
        token: "tok123".to_string(),
        key: 987654321,
        expiry_interval_ms: 3_600_000,
    };
    let plan = build_bing_translate_request_plan(
        &credentials,
        BING_GLOBAL_HOST,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        7,
    )
    .unwrap();

    assert_eq!(plan.method, "POST");
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::Bing);
    assert!(plan
        .endpoint
        .starts_with("https://www.bing.com/ttranslatev3?isVertical=1"));
    assert!(plan.endpoint.contains("IG=IGVALUE"));
    assert!(plan.endpoint.contains("IID=translator.5028.3"));
    assert!(plan.endpoint.contains("SFX=7"));

    let body = plan.body.as_deref().unwrap();
    assert!(body.contains("fromLang=en"));
    assert!(body.contains("to=zh-Hans"));
    assert!(body.contains("text=Hello"));
    assert!(body.contains("token=tok123"));
    assert!(body.contains("key=987654321"));
    assert!(body.contains("tryFetchingGenderDebiasedTranslations=true"));

    let header = |name: &str| {
        plan.headers
            .iter()
            .find(|(key, _)| key == name)
            .map(|(_, value)| value.as_str())
    };
    assert_eq!(
        header("Content-Type"),
        Some("application/x-www-form-urlencoded")
    );
    assert_eq!(header("User-Agent"), Some(BING_USER_AGENT));
    assert_eq!(header("Referer"), Some("https://www.bing.com/translator"));
    assert_eq!(header("Origin"), Some("https://www.bing.com"));
}

#[test]
fn bing_request_plan_uses_china_host_and_truncates_to_legacy_length() {
    assert_eq!(bing_host(true), BING_CHINA_HOST);
    assert_eq!(bing_host(false), BING_GLOBAL_HOST);

    let credentials = BingCredentials {
        ig: "IG".to_string(),
        iid: "iid".to_string(),
        token: "t".to_string(),
        key: 1,
        expiry_interval_ms: 3_600_000,
    };
    let long_text = "a".repeat(BING_MAX_TEXT_LENGTH_UTF16 + 500);
    let plan = build_bing_translate_request_plan(
        &credentials,
        bing_host(true),
        &long_text,
        TranslationLanguage::Auto,
        TranslationLanguage::English,
        1,
    )
    .unwrap();

    assert!(plan
        .endpoint
        .starts_with("https://cn.bing.com/ttranslatev3"));
    // Auto maps to the legacy "auto-detect" source code.
    assert!(plan
        .body
        .as_deref()
        .unwrap()
        .contains("fromLang=auto-detect"));

    // The form `text` field is truncated to the 3000 UTF-16 cap (ASCII 'a' is unescaped).
    let body = plan.body.as_deref().unwrap();
    let text_field = body
        .split('&')
        .find_map(|pair| pair.strip_prefix("text="))
        .unwrap();
    assert_eq!(text_field.chars().count(), BING_MAX_TEXT_LENGTH_UTF16);
}

#[test]
fn bing_language_codes_preserve_legacy_special_cases() {
    assert_eq!(bing_language_code(TranslationLanguage::Auto), "auto-detect");
    assert_eq!(
        bing_language_code(TranslationLanguage::SimplifiedChinese),
        "zh-Hans"
    );
    assert_eq!(
        bing_language_code(TranslationLanguage::TraditionalChinese),
        "zh-Hant"
    );
    assert_eq!(bing_language_code(TranslationLanguage::Norwegian), "nb");
    assert_eq!(bing_language_code(TranslationLanguage::Filipino), "fil");
    assert_eq!(bing_language_code(TranslationLanguage::English), "en");

    assert_eq!(
        from_bing_language_code("zh-Hans"),
        TranslationLanguage::SimplifiedChinese
    );
    assert_eq!(
        from_bing_language_code("ZH-HANT"),
        TranslationLanguage::TraditionalChinese
    );
    assert_eq!(
        from_bing_language_code("nb"),
        TranslationLanguage::Norwegian
    );
    assert_eq!(
        from_bing_language_code("fil"),
        TranslationLanguage::Filipino
    );
    assert_eq!(from_bing_language_code("en"), TranslationLanguage::English);
}

#[test]
fn bing_response_parser_matches_legacy_success_and_errors() {
    let success = parse_bing_translation_response(
        r#"[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]"#,
        "Hello",
        "bing".to_string(),
        "Bing Translate".to_string(),
    )
    .unwrap();
    assert_eq!(success.translated_text, "你好");
    assert_eq!(success.detected_language.as_deref(), Some("en"));

    // Bing error object surfaces as ServiceUnavailable.
    let error = parse_bing_translation_response(
        r#"{"statusCode":400,"errorMessage":"Invalid request"}"#,
        "Hello",
        "bing".to_string(),
        "Bing Translate".to_string(),
    )
    .unwrap_err();
    assert_eq!(error.code, OpenAiExecutionErrorCode::ServiceUnavailable);
    assert!(error.message.contains("Invalid request"));
    assert_eq!(error.service_id.as_deref(), Some("bing"));

    // Empty array / unexpected shape is an invalid response.
    let invalid = parse_bing_translation_response(
        "[]",
        "Hello",
        "bing".to_string(),
        "Bing Translate".to_string(),
    )
    .unwrap_err();
    assert_eq!(invalid.code, OpenAiExecutionErrorCode::InvalidResponse);
}

#[test]
fn bing_credentials_expiry_tracks_legacy_interval() {
    assert!(bing_credentials_expired(0, 4000, 3600));
    assert!(!bing_credentials_expired(0, 3000, 3600));
    assert!(!bing_credentials_expired(1000, 1000, 3600));
}

#[test]
fn linguee_translation_request_plan_matches_legacy_proxy_endpoint() {
    let plan = build_linguee_translation_request_plan(
        "hello world",
        TranslationLanguage::English,
        TranslationLanguage::German,
    )
    .unwrap();

    assert_eq!(plan.method, "GET");
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::Linguee);
    assert!(plan.endpoint.starts_with(LINGUEE_TRANSLATE_ENDPOINT));
    assert!(plan.endpoint.contains("src=en"));
    assert!(plan.endpoint.contains("dst=de"));
    assert!(
        plan.endpoint.contains("query=hello+world")
            || plan.endpoint.contains("query=hello%20world")
    );
    assert!(plan.headers.is_empty());
    assert!(plan.body.is_none());

    let unsupported = build_linguee_translation_request_plan(
        "hi",
        TranslationLanguage::English,
        TranslationLanguage::Korean,
    )
    .unwrap_err();
    assert_eq!(
        unsupported.code,
        OpenAiExecutionErrorCode::UnsupportedLanguage
    );
}

#[test]
fn linguee_language_codes_preserve_legacy_special_cases() {
    assert_eq!(
        linguee_language_code(TranslationLanguage::Auto).unwrap(),
        "auto"
    );
    assert_eq!(
        linguee_language_code(TranslationLanguage::SimplifiedChinese).unwrap(),
        "zh"
    );
    assert_eq!(
        linguee_language_code(TranslationLanguage::Japanese).unwrap(),
        "ja"
    );
    assert!(linguee_language_code(TranslationLanguage::Korean).is_err());
}

#[test]
fn linguee_response_parser_extracts_primary_translation_with_fallback() {
    let result = parse_linguee_translation_response(
        r#"[{"featured":true,"translations":[{"text":"Hallo"},{"text":"Servus"}]}]"#,
        "Hello",
        "linguee".to_string(),
        "Linguee Dictionary".to_string(),
    )
    .unwrap();
    assert_eq!(result.translated_text, "Hallo");
    assert_eq!(result.detected_language, None);
    // Secondary translations are preserved as alternatives.
    assert_eq!(
        result.alternatives.as_deref(),
        Some(&["Servus".to_string()][..])
    );

    // A single translation produces no alternatives.
    let single = parse_linguee_translation_response(
        r#"[{"translations":[{"text":"Hallo"}]}]"#,
        "Hello",
        "linguee".to_string(),
        "Linguee Dictionary".to_string(),
    )
    .unwrap();
    assert_eq!(single.translated_text, "Hallo");
    assert_eq!(single.alternatives, None);

    // Empty / missing translations fall back to the original text.
    let fallback = parse_linguee_translation_response(
        "[]",
        "Hello",
        "linguee".to_string(),
        "Linguee Dictionary".to_string(),
    )
    .unwrap();
    assert_eq!(fallback.translated_text, "Hello");
    assert_eq!(fallback.alternatives, None);
}

#[test]
fn translate_traditional_http_service_executes_linguee_plan() {
    let mut client = RecordingTraditionalHttpClient::with_responses([Ok(
        r#"[{"translations":[{"text":"Bonjour"}]}]"#.to_string(),
    )]);
    let result = translate_traditional_http_service(
        &mut client,
        &TraditionalHttpServiceConfig::Linguee,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::French,
        "linguee",
        "Linguee Dictionary",
    )
    .unwrap();

    assert_eq!(result.translated_text, "Bonjour");
    assert_eq!(client.requests[0].method, "GET");
    assert_eq!(
        client.requests[0].service_kind,
        TraditionalHttpServiceKind::Linguee
    );
}

#[cfg(feature = "enable-linguee-service")]
#[test]
fn traditional_http_config_routes_linguee_only_when_feature_enabled() {
    assert_eq!(
        traditional_http_config_for_service("linguee", &SettingsSnapshot::default()),
        Some(TraditionalHttpServiceConfig::Linguee)
    );
}

#[cfg(not(feature = "enable-linguee-service"))]
#[test]
fn traditional_http_config_omits_linguee_without_feature() {
    assert!(traditional_http_config_for_service("linguee", &SettingsSnapshot::default()).is_none());
}

struct FakeBingHttpClient {
    html: String,
    resolved_host: String,
    translate_responses: VecDeque<Result<BingHttpResponse, OpenAiExecutionError>>,
    translate_plans: Vec<TraditionalHttpRequestPlan>,
    fetch_count: usize,
}

impl FakeBingHttpClient {
    fn new(
        html: &str,
        responses: impl IntoIterator<Item = Result<BingHttpResponse, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            html: html.to_string(),
            resolved_host: "www.bing.com".to_string(),
            translate_responses: responses.into_iter().collect(),
            translate_plans: Vec::new(),
            fetch_count: 0,
        }
    }
}

impl BingHttpClient for FakeBingHttpClient {
    fn fetch_translator_html(
        &mut self,
        _host: &str,
    ) -> Result<BingTranslatorPage, OpenAiExecutionError> {
        self.fetch_count += 1;
        Ok(BingTranslatorPage {
            html: self.html.clone(),
            resolved_host: self.resolved_host.clone(),
        })
    }

    fn execute_translate(
        &mut self,
        plan: &TraditionalHttpRequestPlan,
    ) -> Result<BingHttpResponse, OpenAiExecutionError> {
        self.translate_plans.push(plan.clone());
        self.translate_responses
            .pop_front()
            .expect("test Bing translate response should be queued")
    }
}

fn ok_bing_response(status: u16, body: &str) -> Result<BingHttpResponse, OpenAiExecutionError> {
    Ok(BingHttpResponse {
        status,
        body: body.to_string(),
    })
}

const BING_TRANSLATE_SUCCESS: &str = r#"[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]"#;

#[test]
fn bing_two_phase_executor_fetches_credentials_then_translates() {
    let mut client = FakeBingHttpClient::new(
        BING_HTML_SAMPLE,
        [ok_bing_response(200, BING_TRANSLATE_SUCCESS)],
    );
    let result = translate_bing_service(
        &mut client,
        BING_GLOBAL_HOST,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "bing",
        "Bing Translate",
    )
    .unwrap();

    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    assert_eq!(result.service_id.as_deref(), Some("bing"));
    assert_eq!(client.fetch_count, 1);
    assert_eq!(client.translate_plans.len(), 1);
    // Credentials parsed from the page flow into the translate request.
    assert!(client.translate_plans[0]
        .endpoint
        .contains("IG=A1B2C3D4E5F6"));
    assert!(client.translate_plans[0].endpoint.contains("SFX=1"));
    assert!(client.translate_plans[0]
        .body
        .as_deref()
        .unwrap()
        .contains("token=abusetoken_XyZ"));
}

#[test]
fn bing_two_phase_executor_retries_on_rate_limit_with_fresh_credentials() {
    let mut client = FakeBingHttpClient::new(
        BING_HTML_SAMPLE,
        [
            ok_bing_response(429, "rate limited"),
            ok_bing_response(200, BING_TRANSLATE_SUCCESS),
        ],
    );
    let result = translate_bing_service(
        &mut client,
        BING_GLOBAL_HOST,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "bing",
        "Bing Translate",
    )
    .unwrap();

    assert_eq!(result.translated_text, "你好");
    // Retry refetches credentials and bumps the SFX cache-buster.
    assert_eq!(client.fetch_count, 2);
    assert_eq!(client.translate_plans.len(), 2);
    assert!(client.translate_plans[1].endpoint.contains("SFX=2"));
}

#[test]
fn bing_two_phase_executor_retries_on_non_json_body() {
    let mut client = FakeBingHttpClient::new(
        BING_HTML_SAMPLE,
        [
            ok_bing_response(200, "<html>captcha challenge</html>"),
            ok_bing_response(200, BING_TRANSLATE_SUCCESS),
        ],
    );
    let result = translate_bing_service(
        &mut client,
        BING_GLOBAL_HOST,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "bing",
        "Bing Translate",
    )
    .unwrap();
    assert_eq!(result.translated_text, "你好");
    assert_eq!(client.translate_plans.len(), 2);

    // A persistent non-JSON body fails as InvalidResponse after the retry.
    let mut failing = FakeBingHttpClient::new(
        BING_HTML_SAMPLE,
        [
            ok_bing_response(200, "<html>captcha</html>"),
            ok_bing_response(200, "still not json"),
        ],
    );
    let error = translate_bing_service(
        &mut failing,
        BING_GLOBAL_HOST,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "bing",
        "Bing Translate",
    )
    .unwrap_err();
    assert_eq!(error.code, OpenAiExecutionErrorCode::InvalidResponse);
    assert_eq!(error.service_id.as_deref(), Some("bing"));
}

#[test]
fn bing_two_phase_executor_generates_ig_when_page_omits_it() {
    // HTML without an IG token but with valid abuse-prevention params.
    let html = r#"<script>params_AbusePreventionHelper = [42,"tok",60000];</script>"#;
    let mut client = FakeBingHttpClient::new(html, [ok_bing_response(200, BING_TRANSLATE_SUCCESS)]);
    translate_bing_service(
        &mut client,
        BING_GLOBAL_HOST,
        "Hello",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "bing",
        "Bing Translate",
    )
    .unwrap();

    let endpoint = &client.translate_plans[0].endpoint;
    let ig = endpoint
        .split('&')
        .find_map(|pair| pair.strip_prefix("IG="))
        .unwrap();
    // The generated fallback IG is a 32-char uppercase hex string.
    assert_eq!(ig.len(), 32);
    assert!(ig
        .chars()
        .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_lowercase()));
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

fn form_field(body: &str, key: &str) -> Option<String> {
    let url = reqwest::Url::parse(&format!("https://easydict.local/form?{body}")).ok()?;
    url.query_pairs()
        .find(|(candidate, _)| candidate == key)
        .map(|(_, value)| value.into_owned())
}
