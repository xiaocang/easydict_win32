use crate::compat_protocol::{SettingsSnapshot, TranslationResultDto};
use crate::openai_compatible::{OpenAiExecutionError, OpenAiExecutionErrorCode};
use crate::translation_language::TranslationLanguage;
use ring::{digest, hmac};
use ring::rand::{SecureRandom, SystemRandom};
use serde_json::json;
use serde_json::Value;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const GOOGLE_TRANSLATE_ENDPOINT: &str = "https://translate.googleapis.com/translate_a/single";
pub const CAIYUN_TRANSLATE_ENDPOINT: &str = "https://api.interpreter.caiyunai.com/v1/translator";
pub const DEEPL_FREE_API_ENDPOINT: &str = "https://api-free.deepl.com/v2/translate";
pub const DEEPL_PRO_API_ENDPOINT: &str = "https://api.deepl.com/v2/translate";
pub const NIUTRANS_TRANSLATE_ENDPOINT: &str = "https://api.niutrans.com/NiuTransServer/translation";
pub const NIUTRANS_MAX_TEXT_LENGTH_UTF16: usize = 5000;
pub const VOLCANO_TRANSLATE_ENDPOINT: &str =
    "https://translate.volcengineapi.com/?Action=TranslateText&Version=2020-06-01";
pub const VOLCANO_TRANSLATE_HOST: &str = "translate.volcengineapi.com";
pub const VOLCANO_QUERY_STRING: &str = "Action=TranslateText&Version=2020-06-01";
pub const VOLCANO_REGION: &str = "cn-north-1";
pub const VOLCANO_SERVICE_NAME: &str = "translate";
pub const VOLCANO_SIGNING_ALGORITHM: &str = "HMAC-SHA256";
pub const VOLCANO_MAX_TEXT_LENGTH_UTF16: usize = 5000;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TraditionalHttpServiceKind {
    Google,
    Caiyun,
    DeepLApi,
    NiuTrans,
    Volcano,
}

#[derive(Clone, Debug, PartialEq)]
pub struct TraditionalHttpRequestPlan {
    pub method: &'static str,
    pub endpoint: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<String>,
    pub service_kind: TraditionalHttpServiceKind,
}

#[derive(Clone, Debug, PartialEq)]
pub enum TraditionalHttpServiceConfig {
    Google,
    Caiyun {
        api_key: String,
    },
    DeepLApi {
        api_key: String,
        use_quality_optimized: bool,
    },
    NiuTrans {
        api_key: String,
    },
    Volcano {
        access_key_id: String,
        secret_access_key: String,
    },
}

pub trait TraditionalHttpClient {
    fn execute(
        &mut self,
        request: &TraditionalHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError>;
}

pub struct ReqwestTraditionalHttpClient {
    client: reqwest::blocking::Client,
}

impl ReqwestTraditionalHttpClient {
    pub fn from_settings(settings: &SettingsSnapshot) -> Result<Self, OpenAiExecutionError> {
        let mut builder = reqwest::blocking::Client::builder().timeout(Duration::from_secs(60));

        if settings.proxy_enabled.unwrap_or(false) {
            if let Some(proxy_uri) = normalized_optional(settings.proxy_uri.as_deref()) {
                let proxy = if settings.proxy_bypass_local.unwrap_or(false) {
                    let proxy_url = reqwest::Url::parse(&proxy_uri).map_err(|error| {
                        OpenAiExecutionError::new(
                            OpenAiExecutionErrorCode::InvalidResponse,
                            format!("Invalid traditional HTTP proxy URI '{proxy_uri}': {error}"),
                        )
                    })?;
                    reqwest::Proxy::custom(move |url| {
                        (!is_loopback_url(url)).then(|| proxy_url.clone())
                    })
                } else {
                    reqwest::Proxy::all(&proxy_uri).map_err(|error| {
                        OpenAiExecutionError::new(
                            OpenAiExecutionErrorCode::InvalidResponse,
                            format!("Invalid traditional HTTP proxy URI '{proxy_uri}': {error}"),
                        )
                    })?
                };
                builder = builder.proxy(proxy);
            }
        }

        let client = builder.build().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not create traditional HTTP client: {error}"),
            )
        })?;
        Ok(Self { client })
    }
}

impl TraditionalHttpClient for ReqwestTraditionalHttpClient {
    fn execute(
        &mut self,
        request: &TraditionalHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        let mut builder = match request.method {
            "GET" => self.client.get(&request.endpoint),
            "POST" => {
                let mut post = self.client.post(&request.endpoint);
                if let Some(body) = &request.body {
                    post = post.body(body.clone());
                }
                post
            }
            method => {
                return Err(OpenAiExecutionError::new(
                    OpenAiExecutionErrorCode::InvalidResponse,
                    format!("Unsupported traditional HTTP method: {method}"),
                ))
            }
        };
        for (name, value) in &request.headers {
            builder = builder.header(name, value);
        }

        let response = builder.send().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Traditional HTTP request failed: {error}"),
            )
        })?;
        let status = response.status();
        let body = response.text().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not read traditional HTTP response: {error}"),
            )
        })?;

        if !status.is_success() {
            let status_code = status.as_u16();
            let reason = status.canonical_reason().unwrap_or("Unknown");
            let error = match request.service_kind {
                TraditionalHttpServiceKind::DeepLApi => {
                    deepl_api_error_from_status(status_code, reason)
                }
                _ => traditional_http_error_from_status(status_code, reason),
            };
            return Err(error);
        }

        Ok(body)
    }
}

pub fn traditional_http_config_for_service(
    service_id: &str,
    settings: &SettingsSnapshot,
) -> Option<TraditionalHttpServiceConfig> {
    match service_id {
        "google" => Some(TraditionalHttpServiceConfig::Google),
        "caiyun" => Some(TraditionalHttpServiceConfig::Caiyun {
            api_key: settings.caiyun_token.clone().unwrap_or_default(),
        }),
        "deepl" => {
            deepl_uses_native_api(settings).then(|| TraditionalHttpServiceConfig::DeepLApi {
                api_key: settings.deep_l_api_key.clone().unwrap_or_default(),
                use_quality_optimized: settings.deep_l_use_quality_optimized.unwrap_or(false),
            })
        }
        "niutrans" => Some(TraditionalHttpServiceConfig::NiuTrans {
            api_key: settings.niu_trans_api_key.clone().unwrap_or_default(),
        }),
        _ => None,
    }
}

pub fn build_traditional_http_translation_request_plan(
    config: &TraditionalHttpServiceConfig,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    match config {
        TraditionalHttpServiceConfig::Google => {
            build_google_translation_request_plan(text, from_language, to_language)
        }
        TraditionalHttpServiceConfig::Caiyun { api_key } => {
            build_caiyun_translation_request_plan(api_key, text, from_language, to_language)
        }
        TraditionalHttpServiceConfig::DeepLApi {
            api_key,
            use_quality_optimized,
        } => build_deepl_api_translation_request_plan(
            api_key,
            *use_quality_optimized,
            text,
            from_language,
            to_language,
        ),
        TraditionalHttpServiceConfig::NiuTrans { api_key } => {
            build_niutrans_translation_request_plan(api_key, text, from_language, to_language)
        }
    }
}

pub fn build_google_translation_request_plan(
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    let mut url = reqwest::Url::parse(GOOGLE_TRANSLATE_ENDPOINT).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Google Translate endpoint: {error}"),
        )
    })?;
    url.query_pairs_mut()
        .append_pair("client", "gtx")
        .append_pair("sl", google_language_code(from_language))
        .append_pair("tl", google_language_code(to_language));

    for dt in ["t", "bd", "at", "ex", "ld", "md", "qca", "rw", "rm", "ss"] {
        url.query_pairs_mut().append_pair("dt", dt);
    }

    url.query_pairs_mut()
        .append_pair("dj", "1")
        .append_pair("ie", "UTF-8")
        .append_pair("oe", "UTF-8")
        .append_pair("q", text);

    Ok(TraditionalHttpRequestPlan {
        method: "GET",
        endpoint: url.to_string(),
        headers: Vec::new(),
        body: None,
        service_kind: TraditionalHttpServiceKind::Google,
    })
}

pub fn build_caiyun_translation_request_plan(
    api_key: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Caiyun API key", api_key)?;

    let from_code = caiyun_language_code(from_language)?;
    let to_code = caiyun_language_code(to_language)?;
    let request_id = new_request_id()?;
    let body = json!({
        "source": [text],
        "trans_type": format!("{from_code}2{to_code}"),
        "request_id": request_id,
        "media": "text",
    });

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint: CAIYUN_TRANSLATE_ENDPOINT.to_string(),
        headers: vec![
            ("Content-Type".to_string(), "application/json".to_string()),
            (
                "X-Authorization".to_string(),
                format!("token {}", api_key.trim()),
            ),
        ],
        body: Some(body.to_string()),
        service_kind: TraditionalHttpServiceKind::Caiyun,
    })
}

pub fn build_deepl_api_translation_request_plan(
    api_key: &str,
    use_quality_optimized: bool,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("DeepL API key", api_key)?;

    let mut form_fields = vec![
        ("text", text.to_string()),
        ("target_lang", deepl_language_code(to_language, false)),
    ];
    if from_language != TranslationLanguage::Auto {
        form_fields.push(("source_lang", deepl_language_code(from_language, false)));
    }
    if use_quality_optimized {
        form_fields.push(("model_type", "quality_optimized".to_string()));
    }

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint: deepl_api_endpoint(api_key).to_string(),
        headers: vec![
            (
                "Authorization".to_string(),
                format!("DeepL-Auth-Key {}", api_key.trim()),
            ),
            (
                "Content-Type".to_string(),
                "application/x-www-form-urlencoded".to_string(),
            ),
        ],
        body: Some(form_urlencoded_body(&form_fields)?),
        service_kind: TraditionalHttpServiceKind::DeepLApi,
    })
}

pub fn build_niutrans_translation_request_plan(
    api_key: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("NiuTrans API key", api_key)?;

    let text_len = text.encode_utf16().count();
    if text_len > NIUTRANS_MAX_TEXT_LENGTH_UTF16 {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::TextTooLong,
            format!("Text exceeds maximum length of {NIUTRANS_MAX_TEXT_LENGTH_UTF16} characters"),
        ));
    }

    let body = json!({
        "apikey": api_key.trim(),
        "src_text": text,
        "from": niutrans_language_code(from_language)?,
        "to": niutrans_language_code(to_language)?,
        "source": "Easydict",
    });

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint: NIUTRANS_TRANSLATE_ENDPOINT.to_string(),
        headers: vec![("Content-Type".to_string(), "application/json".to_string())],
        body: Some(body.to_string()),
        service_kind: TraditionalHttpServiceKind::NiuTrans,
    })
}

pub fn translate_traditional_http_service<C: TraditionalHttpClient>(
    client: &mut C,
    config: &TraditionalHttpServiceConfig,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    service_id: impl Into<String>,
    service_name: impl Into<String>,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let service_id = service_id.into();
    let service_name = service_name.into();
    let plan =
        build_traditional_http_translation_request_plan(config, text, from_language, to_language)
            .map_err(|error| attach_service_id(error, &service_id))?;
    let body = client
        .execute(&plan)
        .map_err(|error| attach_service_id(error, &service_id))?;

    match config {
        TraditionalHttpServiceConfig::Google => {
            parse_google_translation_response(&body, service_id, service_name)
        }
        TraditionalHttpServiceConfig::Caiyun { .. } => {
            parse_caiyun_translation_response(&body, text, service_id, service_name)
        }
        TraditionalHttpServiceConfig::DeepLApi { .. } => {
            parse_deepl_api_translation_response(&body, service_id, service_name)
        }
        TraditionalHttpServiceConfig::NiuTrans { .. } => {
            parse_niutrans_translation_response(&body, text, service_id, service_name)
        }
    }
}

pub fn parse_google_translation_response(
    json: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Google Translate JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;
    let translated_text = root
        .get("sentences")
        .and_then(Value::as_array)
        .map(|sentences| {
            sentences
                .iter()
                .filter_map(|sentence| sentence.get("trans"))
                .filter_map(Value::as_str)
                .collect::<String>()
        })
        .unwrap_or_default();
    let detected_language = root
        .get("src")
        .and_then(Value::as_str)
        .map(|code| TranslationLanguage::from_code(code).to_code().to_string());

    Ok(TranslationResultDto {
        translated_text,
        service_id: Some(service_id),
        service_name: Some(service_name),
        detected_language,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
    })
}

pub fn parse_caiyun_translation_response(
    json: &str,
    original_text: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Caiyun JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;
    let translated_text = root
        .get("target")
        .and_then(Value::as_array)
        .map(|targets| {
            targets
                .iter()
                .filter_map(Value::as_str)
                .filter(|line| !line.is_empty())
                .collect::<String>()
        })
        .filter(|text| !text.is_empty())
        .unwrap_or_else(|| original_text.to_string());

    Ok(success_result(
        translated_text,
        service_id,
        service_name,
        None,
    ))
}

pub fn parse_deepl_api_translation_response(
    json: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid DeepL API JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    let Some(first_translation) = root
        .get("translations")
        .and_then(Value::as_array)
        .and_then(|translations| translations.first())
    else {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            "Invalid response from DeepL API",
        )
        .with_service_id(service_id));
    };

    let translated_text = first_translation
        .get("text")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string();
    let detected_language = first_translation
        .get("detected_source_language")
        .and_then(Value::as_str)
        .map(|code| {
            TranslationLanguage::from_iso639(code)
                .to_iso639()
                .to_string()
        });

    Ok(success_result(
        translated_text,
        service_id,
        service_name,
        detected_language,
    ))
}

pub fn parse_niutrans_translation_response(
    json: &str,
    original_text: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid NiuTrans JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    if let Some(error_code) = root.get("error_code").and_then(Value::as_str) {
        let error_message = root
            .get("error_msg")
            .and_then(Value::as_str)
            .unwrap_or("Unknown error");
        return Err(
            niutrans_error_from_code(error_code, error_message).with_service_id(service_id.clone())
        );
    }

    let translated_text = root
        .get("tgt_text")
        .and_then(Value::as_str)
        .filter(|text| !text.is_empty())
        .unwrap_or(original_text)
        .to_string();

    Ok(success_result(
        translated_text,
        service_id,
        service_name,
        None,
    ))
}

pub fn google_language_code(language: TranslationLanguage) -> &'static str {
    match language {
        TranslationLanguage::Auto => "auto",
        TranslationLanguage::TraditionalChinese => "zh-TW",
        TranslationLanguage::SimplifiedChinese => "zh-CN",
        TranslationLanguage::Filipino => "tl",
        language => language.to_code(),
    }
}

pub fn caiyun_language_code(
    language: TranslationLanguage,
) -> Result<&'static str, OpenAiExecutionError> {
    match language {
        TranslationLanguage::Auto => Ok("auto"),
        TranslationLanguage::SimplifiedChinese => Ok("zh"),
        TranslationLanguage::TraditionalChinese => Ok("zh-Hant"),
        TranslationLanguage::English => Ok("en"),
        TranslationLanguage::Japanese => Ok("ja"),
        TranslationLanguage::Korean => Ok("ko"),
        TranslationLanguage::Spanish => Ok("es"),
        TranslationLanguage::French => Ok("fr"),
        TranslationLanguage::Russian => Ok("ru"),
        TranslationLanguage::German => Ok("de"),
        TranslationLanguage::Italian => Ok("it"),
        TranslationLanguage::Portuguese => Ok("pt"),
        TranslationLanguage::Arabic => Ok("ar"),
        TranslationLanguage::Hindi => Ok("hi"),
        TranslationLanguage::Indonesian => Ok("id"),
        TranslationLanguage::Malay => Ok("ms"),
        TranslationLanguage::Thai => Ok("th"),
        TranslationLanguage::Vietnamese => Ok("vi"),
        _ => Err(unsupported_language_error("Caiyun", language)),
    }
}

pub fn deepl_language_code(language: TranslationLanguage, is_web: bool) -> String {
    match language {
        TranslationLanguage::SimplifiedChinese => "ZH".to_string(),
        TranslationLanguage::TraditionalChinese => "ZH-HANT".to_string(),
        TranslationLanguage::English => "EN".to_string(),
        TranslationLanguage::Japanese => "JA".to_string(),
        TranslationLanguage::Korean => "KO".to_string(),
        TranslationLanguage::French => "FR".to_string(),
        TranslationLanguage::Spanish => "ES".to_string(),
        TranslationLanguage::Portuguese if is_web => "PT-PT".to_string(),
        TranslationLanguage::Portuguese => "PT".to_string(),
        TranslationLanguage::Italian => "IT".to_string(),
        TranslationLanguage::German => "DE".to_string(),
        TranslationLanguage::Russian => "RU".to_string(),
        TranslationLanguage::Dutch => "NL".to_string(),
        TranslationLanguage::Polish => "PL".to_string(),
        TranslationLanguage::Bulgarian => "BG".to_string(),
        TranslationLanguage::Czech => "CS".to_string(),
        TranslationLanguage::Danish => "DA".to_string(),
        TranslationLanguage::Finnish => "FI".to_string(),
        TranslationLanguage::Greek => "EL".to_string(),
        TranslationLanguage::Hungarian => "HU".to_string(),
        TranslationLanguage::Indonesian => "ID".to_string(),
        TranslationLanguage::Norwegian => "NB".to_string(),
        TranslationLanguage::Romanian => "RO".to_string(),
        TranslationLanguage::Swedish => "SV".to_string(),
        TranslationLanguage::Turkish => "TR".to_string(),
        TranslationLanguage::Ukrainian => "UK".to_string(),
        language => language.to_iso639().to_ascii_uppercase(),
    }
}

pub fn niutrans_language_code(
    language: TranslationLanguage,
) -> Result<&'static str, OpenAiExecutionError> {
    match language {
        TranslationLanguage::Auto => Ok("auto"),
        TranslationLanguage::SimplifiedChinese => Ok("zh"),
        TranslationLanguage::TraditionalChinese => Ok("cht"),
        TranslationLanguage::English => Ok("en"),
        TranslationLanguage::Japanese => Ok("ja"),
        TranslationLanguage::Korean => Ok("ko"),
        TranslationLanguage::French => Ok("fr"),
        TranslationLanguage::Spanish => Ok("es"),
        TranslationLanguage::German => Ok("de"),
        TranslationLanguage::Russian => Ok("ru"),
        TranslationLanguage::Arabic => Ok("ar"),
        TranslationLanguage::Italian => Ok("it"),
        TranslationLanguage::Portuguese => Ok("pt"),
        TranslationLanguage::Dutch => Ok("nl"),
        TranslationLanguage::Polish => Ok("pl"),
        TranslationLanguage::Turkish => Ok("tr"),
        TranslationLanguage::Vietnamese => Ok("vi"),
        TranslationLanguage::Thai => Ok("th"),
        TranslationLanguage::Indonesian => Ok("id"),
        TranslationLanguage::Malay => Ok("ms"),
        TranslationLanguage::Hindi => Ok("hi"),
        TranslationLanguage::Greek => Ok("el"),
        TranslationLanguage::Czech => Ok("cs"),
        TranslationLanguage::Danish => Ok("da"),
        TranslationLanguage::Finnish => Ok("fi"),
        TranslationLanguage::Hungarian => Ok("hu"),
        TranslationLanguage::Norwegian => Ok("no"),
        TranslationLanguage::Romanian => Ok("ro"),
        TranslationLanguage::Slovak => Ok("sk"),
        TranslationLanguage::Swedish => Ok("sv"),
        TranslationLanguage::Bulgarian => Ok("bg"),
        TranslationLanguage::Estonian => Ok("et"),
        TranslationLanguage::Latvian => Ok("lv"),
        TranslationLanguage::Lithuanian => Ok("lt"),
        TranslationLanguage::Slovenian => Ok("sl"),
        TranslationLanguage::Ukrainian => Ok("uk"),
        TranslationLanguage::Persian => Ok("fa"),
        TranslationLanguage::Hebrew => Ok("he"),
        TranslationLanguage::Bengali => Ok("bn"),
        TranslationLanguage::Tamil => Ok("ta"),
        TranslationLanguage::Telugu => Ok("te"),
        TranslationLanguage::Urdu => Ok("ur"),
        TranslationLanguage::Filipino => Ok("fil"),
        _ => Err(unsupported_language_error("NiuTrans", language)),
    }
}

pub fn traditional_http_error_from_status(status_code: u16, reason: &str) -> OpenAiExecutionError {
    let code = match status_code {
        401 | 403 => OpenAiExecutionErrorCode::InvalidApiKey,
        429 => OpenAiExecutionErrorCode::RateLimited,
        408 | 504 => OpenAiExecutionErrorCode::Timeout,
        500..=599 => OpenAiExecutionErrorCode::ServiceUnavailable,
        _ => OpenAiExecutionErrorCode::ServiceUnavailable,
    };
    OpenAiExecutionError::new(
        code,
        format!("Traditional HTTP API error ({status_code}): {reason}"),
    )
}

pub fn deepl_api_error_from_status(status_code: u16, reason: &str) -> OpenAiExecutionError {
    let code = match status_code {
        401 | 403 => OpenAiExecutionErrorCode::InvalidApiKey,
        429 | 456 => OpenAiExecutionErrorCode::RateLimited,
        408 | 504 => OpenAiExecutionErrorCode::Timeout,
        500..=599 => OpenAiExecutionErrorCode::ServiceUnavailable,
        _ => OpenAiExecutionErrorCode::ServiceUnavailable,
    };
    OpenAiExecutionError::new(code, format!("DeepL API error ({status_code}): {reason}"))
}

pub fn niutrans_error_from_code(error_code: &str, error_message: &str) -> OpenAiExecutionError {
    let code = match error_code {
        "13002" | "13003" => OpenAiExecutionErrorCode::InvalidApiKey,
        "13004" => OpenAiExecutionErrorCode::RateLimited,
        "13005" => OpenAiExecutionErrorCode::TextTooLong,
        _ => OpenAiExecutionErrorCode::ServiceUnavailable,
    };
    OpenAiExecutionError::new(
        code,
        format!("NiuTrans API error: {error_message} (code: {error_code})"),
    )
}

fn deepl_uses_native_api(settings: &SettingsSnapshot) -> bool {
    settings.deep_l_use_quality_optimized.unwrap_or(false)
        || !settings.deep_l_use_free_api.unwrap_or(true)
}

fn deepl_api_endpoint(api_key: &str) -> &'static str {
    if api_key.trim().ends_with(":fx") {
        DEEPL_FREE_API_ENDPOINT
    } else {
        DEEPL_PRO_API_ENDPOINT
    }
}

fn validate_required(label: &str, value: &str) -> Result<(), OpenAiExecutionError> {
    if value.trim().is_empty() {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidApiKey,
            format!("{label} not configured"),
        ));
    }
    Ok(())
}

fn form_urlencoded_body(fields: &[(&str, String)]) -> Result<String, OpenAiExecutionError> {
    let mut url = reqwest::Url::parse("https://easydict.local/form").map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Could not initialize form encoder: {error}"),
        )
    })?;
    {
        let mut pairs = url.query_pairs_mut();
        for (key, value) in fields {
            pairs.append_pair(key, value);
        }
    }
    Ok(url.query().unwrap_or_default().to_string())
}

fn unsupported_language_error(
    provider: &str,
    language: TranslationLanguage,
) -> OpenAiExecutionError {
    OpenAiExecutionError::new(
        OpenAiExecutionErrorCode::UnsupportedLanguage,
        format!("{provider} does not support {}", language.display_name()),
    )
}

fn success_result(
    translated_text: String,
    service_id: String,
    service_name: String,
    detected_language: Option<String>,
) -> TranslationResultDto {
    TranslationResultDto {
        translated_text,
        service_id: Some(service_id),
        service_name: Some(service_name),
        detected_language,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
    }
}

fn new_request_id() -> Result<String, OpenAiExecutionError> {
    let rng = SystemRandom::new();
    let mut bytes = [0_u8; 16];
    rng.fill(&mut bytes).map_err(|_| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::Unknown,
            "Could not generate traditional HTTP request id",
        )
    })?;

    let mut id = String::with_capacity(36);
    for (index, byte) in bytes.iter().enumerate() {
        if matches!(index, 4 | 6 | 8 | 10) {
            id.push('-');
        }
        push_hex_byte(&mut id, *byte);
    }
    Ok(id)
}

fn push_hex_byte(buffer: &mut String, byte: u8) {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    buffer.push(HEX[(byte >> 4) as usize] as char);
    buffer.push(HEX[(byte & 0x0f) as usize] as char);
}

fn attach_service_id(mut error: OpenAiExecutionError, service_id: &str) -> OpenAiExecutionError {
    if error.service_id.is_none() {
        error.service_id = Some(service_id.to_string());
    }
    error
}

fn normalized_optional(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
}

fn is_loopback_url(url: &reqwest::Url) -> bool {
    url.host_str()
        .map(|host| {
            host.eq_ignore_ascii_case("localhost")
                || host == "127.0.0.1"
                || host == "::1"
                || host.starts_with("127.")
        })
        .unwrap_or(false)
}
