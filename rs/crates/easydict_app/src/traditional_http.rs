use crate::compat_protocol::{SettingsSnapshot, TranslationResultDto};
use crate::openai_compatible::{OpenAiExecutionError, OpenAiExecutionErrorCode};
use crate::translation_language::TranslationLanguage;
use ring::rand::{SecureRandom, SystemRandom};
use ring::{digest, hmac};
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
pub const BING_GLOBAL_HOST: &str = "www.bing.com";
pub const BING_CHINA_HOST: &str = "cn.bing.com";
pub const BING_TRANSLATOR_PATH: &str = "/translator";
pub const BING_TRANSLATE_API_PATH: &str = "/ttranslatev3";
/// Full Edge browser User-Agent string (required for Bing EPT mode).
pub const BING_USER_AGENT: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 \
     (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
pub const BING_MAX_TEXT_LENGTH_UTF16: usize = 3000;
pub const BING_DEFAULT_IID: &str = "translator.5023.1";
pub const BING_DEFAULT_EXPIRY_INTERVAL_MS: i64 = 3_600_000;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TraditionalHttpServiceKind {
    Google,
    Caiyun,
    DeepLApi,
    NiuTrans,
    Volcano,
    Bing,
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

/// UTC timestamps used by the Volcano (火山) AWS SigV4-style signing process.
/// `x_date` is `yyyyMMddTHHmmssZ`; `short_date` is `yyyyMMdd`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct VolcanoTimestamps {
    pub x_date: String,
    pub short_date: String,
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
        "volcano" => Some(TraditionalHttpServiceConfig::Volcano {
            access_key_id: settings.volcano_access_key_id.clone().unwrap_or_default(),
            secret_access_key: settings
                .volcano_secret_access_key
                .clone()
                .unwrap_or_default(),
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
        TraditionalHttpServiceConfig::Volcano {
            access_key_id,
            secret_access_key,
        } => build_volcano_translation_request_plan(
            access_key_id,
            secret_access_key,
            text,
            from_language,
            to_language,
        ),
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

pub fn build_volcano_translation_request_plan(
    access_key_id: &str,
    secret_access_key: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Volcano AccessKeyID", access_key_id)?;
    validate_required("Volcano SecretAccessKey", secret_access_key)?;

    let text_len = text.encode_utf16().count();
    if text_len > VOLCANO_MAX_TEXT_LENGTH_UTF16 {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::TextTooLong,
            format!("Text exceeds maximum length of {VOLCANO_MAX_TEXT_LENGTH_UTF16} characters"),
        ));
    }

    let from_code = volcano_language_code(from_language)?;
    let to_code = volcano_language_code(to_language)?;

    // Mirror the legacy insertion order: TargetLanguage, TextList, then optional
    // SourceLanguage. The body bytes are signed exactly as sent.
    let mut body = serde_json::Map::new();
    body.insert(
        "TargetLanguage".to_string(),
        Value::String(to_code.to_string()),
    );
    body.insert(
        "TextList".to_string(),
        Value::Array(vec![Value::String(text.to_string())]),
    );
    if !from_code.is_empty() {
        body.insert(
            "SourceLanguage".to_string(),
            Value::String(from_code.to_string()),
        );
    }
    let body_json = Value::Object(body).to_string();

    let timestamps = volcano_timestamps_now()?;
    let authorization = compute_volcano_authorization(
        access_key_id.trim(),
        secret_access_key,
        body_json.as_bytes(),
        &timestamps.x_date,
        &timestamps.short_date,
    );

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint: VOLCANO_TRANSLATE_ENDPOINT.to_string(),
        headers: vec![
            ("Content-Type".to_string(), "application/json".to_string()),
            ("Host".to_string(), VOLCANO_TRANSLATE_HOST.to_string()),
            ("X-Date".to_string(), timestamps.x_date),
            ("Authorization".to_string(), authorization),
        ],
        body: Some(body_json),
        service_kind: TraditionalHttpServiceKind::Volcano,
    })
}

/// Compute the Volcano Engine HMAC-SHA256 Authorization header (AWS SigV4-style).
/// Pure and deterministic given the timestamp inputs, mirroring the legacy
/// .NET `VolcanoService.ComputeAuthorization`. Docs: https://www.volcengine.com/docs/6369/67269
pub fn compute_volcano_authorization(
    access_key_id: &str,
    secret_access_key: &str,
    body: &[u8],
    x_date: &str,
    short_date: &str,
) -> String {
    let credential_scope = format!("{short_date}/{VOLCANO_REGION}/{VOLCANO_SERVICE_NAME}/request");

    // Canonical headers (sorted, lowercase). The trailing '\n' here, combined with
    // the join below, intentionally inserts a blank line before the signed headers,
    // matching the legacy signing string byte-for-byte.
    let canonical_headers =
        format!("content-type:application/json\nhost:{VOLCANO_TRANSLATE_HOST}\nx-date:{x_date}\n");
    let signed_headers = "content-type;host;x-date";

    let body_hash = sha256_hex(body);
    let canonical_request = [
        "POST",
        "/",
        VOLCANO_QUERY_STRING,
        &canonical_headers,
        signed_headers,
        &body_hash,
    ]
    .join("\n");

    let canonical_request_hash = sha256_hex(canonical_request.as_bytes());
    let string_to_sign = [
        VOLCANO_SIGNING_ALGORITHM,
        x_date,
        &credential_scope,
        &canonical_request_hash,
    ]
    .join("\n");

    let k_date = hmac_sha256(secret_access_key.as_bytes(), short_date.as_bytes());
    let k_region = hmac_sha256(&k_date, VOLCANO_REGION.as_bytes());
    let k_service = hmac_sha256(&k_region, VOLCANO_SERVICE_NAME.as_bytes());
    let k_signing = hmac_sha256(&k_service, b"request");
    let signature = hex_encode_lower(&hmac_sha256(&k_signing, string_to_sign.as_bytes()));

    format!(
        "{VOLCANO_SIGNING_ALGORITHM} Credential={access_key_id}/{credential_scope}, \
         SignedHeaders={signed_headers}, Signature={signature}"
    )
}

pub fn parse_volcano_translation_response(
    json: &str,
    original_text: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Volcano JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    // API-level error reported inside ResponseMetadata.Error.
    if let Some(error) = root
        .get("ResponseMetadata")
        .and_then(|metadata| metadata.get("Error"))
    {
        let code = error
            .get("Code")
            .and_then(Value::as_str)
            .unwrap_or("Unknown");
        let message = error
            .get("Message")
            .and_then(Value::as_str)
            .unwrap_or("Unknown error");
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::ServiceUnavailable,
            format!("Volcano API error: {message} (code: {code})"),
        )
        .with_service_id(service_id));
    }

    let first_item = root
        .get("TranslationList")
        .and_then(Value::as_array)
        .and_then(|list| list.first());

    let translated_text = first_item
        .and_then(|item| item.get("Translation"))
        .and_then(Value::as_str)
        .filter(|text| !text.is_empty())
        .unwrap_or(original_text)
        .to_string();

    let detected_language = first_item
        .and_then(|item| item.get("DetectedSourceLanguage"))
        .and_then(Value::as_str)
        .filter(|code| !code.is_empty())
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

pub fn volcano_language_code(
    language: TranslationLanguage,
) -> Result<&'static str, OpenAiExecutionError> {
    match language {
        // Auto resolves to an empty code so the caller omits SourceLanguage.
        TranslationLanguage::Auto => Ok(""),
        TranslationLanguage::SimplifiedChinese => Ok("zh"),
        TranslationLanguage::TraditionalChinese => Ok("zh-Hant"),
        TranslationLanguage::ClassicalChinese => Ok("lzh"),
        TranslationLanguage::English => Ok("en"),
        TranslationLanguage::Japanese => Ok("ja"),
        TranslationLanguage::Korean => Ok("ko"),
        TranslationLanguage::French => Ok("fr"),
        TranslationLanguage::German => Ok("de"),
        TranslationLanguage::Spanish => Ok("es"),
        TranslationLanguage::Portuguese => Ok("pt"),
        TranslationLanguage::Italian => Ok("it"),
        TranslationLanguage::Russian => Ok("ru"),
        TranslationLanguage::Arabic => Ok("ar"),
        TranslationLanguage::Thai => Ok("th"),
        TranslationLanguage::Vietnamese => Ok("vi"),
        TranslationLanguage::Indonesian => Ok("id"),
        TranslationLanguage::Hindi => Ok("hi"),
        TranslationLanguage::Hebrew => Ok("he"),
        TranslationLanguage::Ukrainian => Ok("uk"),
        TranslationLanguage::Urdu => Ok("ur"),
        TranslationLanguage::Turkish => Ok("tr"),
        TranslationLanguage::Tamil => Ok("ta"),
        TranslationLanguage::Telugu => Ok("te"),
        TranslationLanguage::Slovenian => Ok("sl"),
        TranslationLanguage::Slovak => Ok("sk"),
        TranslationLanguage::Swedish => Ok("sv"),
        TranslationLanguage::Norwegian => Ok("no"),
        TranslationLanguage::Bengali => Ok("bn"),
        TranslationLanguage::Malay => Ok("ms"),
        TranslationLanguage::Romanian => Ok("ro"),
        TranslationLanguage::Lithuanian => Ok("lt"),
        TranslationLanguage::Latvian => Ok("lv"),
        TranslationLanguage::Czech => Ok("cs"),
        TranslationLanguage::Dutch => Ok("nl"),
        TranslationLanguage::Finnish => Ok("fi"),
        TranslationLanguage::Danish => Ok("da"),
        TranslationLanguage::Persian => Ok("fa"),
        TranslationLanguage::Polish => Ok("pl"),
        TranslationLanguage::Bulgarian => Ok("bg"),
        TranslationLanguage::Estonian => Ok("et"),
        TranslationLanguage::Hungarian => Ok("hu"),
        _ => Err(unsupported_language_error("Volcano", language)),
    }
}

// ----------------------------------------------------------------------------
// Bing Translate (free web API, no key).
//
// Bing is a two-phase, stateful provider: a GET to the translator page yields
// session credentials (IG/IID/token/key/expiry) parsed from inline HTML, then a
// POST to `ttranslatev3` performs the translation. These are the pure,
// network-free building blocks for that flow; live two-phase execution and
// credential caching are wired in a follow-up slice, so `bing` is intentionally
// not yet in `traditional_http_config_for_service` (it stays on the .NET host).
// ----------------------------------------------------------------------------

/// Bing session credentials scraped from the translator page HTML.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BingCredentials {
    pub ig: String,
    pub iid: String,
    pub token: String,
    pub key: i64,
    pub expiry_interval_ms: i64,
}

/// Resolve the Bing host for the configured region.
pub fn bing_host(use_china_host: bool) -> &'static str {
    if use_china_host {
        BING_CHINA_HOST
    } else {
        BING_GLOBAL_HOST
    }
}

/// Parse IG, IID, token, key, and expiry interval from the Bing translator page.
/// Mirrors the legacy regex extraction: a missing IID falls back to the default,
/// a missing IG yields an empty string (the caller substitutes a generated value),
/// and a missing/empty token is a hard `ServiceUnavailable` error.
pub fn parse_bing_credentials_from_html(
    html: &str,
) -> Result<BingCredentials, OpenAiExecutionError> {
    let ig = extract_delimited(html, "IG:\"", '"')
        .unwrap_or_default()
        .to_string();
    let iid = extract_delimited(html, "data-iid=\"", '"')
        .unwrap_or(BING_DEFAULT_IID)
        .to_string();

    let params = parse_bing_abuse_prevention_params(html);
    match params {
        Some((key, token, expiry_interval_ms)) if !token.is_empty() => Ok(BingCredentials {
            ig,
            iid,
            token,
            key,
            expiry_interval_ms,
        }),
        _ => Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::ServiceUnavailable,
            "Failed to extract Bing session credentials. The page format may have changed.",
        )
        .with_service_id("bing")),
    }
}

/// Whether cached Bing credentials created at `created_at_ms` are expired at `now_ms`.
pub fn bing_credentials_expired(created_at_ms: i64, now_ms: i64, expiry_interval_ms: i64) -> bool {
    now_ms - created_at_ms > expiry_interval_ms
}

/// Build the signed Bing `ttranslatev3` request plan. `sfx` is the per-request
/// EPT counter the legacy service increments for each call.
pub fn build_bing_translate_request_plan(
    credentials: &BingCredentials,
    host: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    sfx: u64,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    let text = truncate_to_utf16_units(text, BING_MAX_TEXT_LENGTH_UTF16);
    let from_code = bing_language_code(from_language);
    let to_code = bing_language_code(to_language);

    let endpoint = format!(
        "https://{host}{BING_TRANSLATE_API_PATH}?isVertical=1&IG={ig}&IID={iid}\
         &ref=TThis&edgepdftranslator=1&SFX={sfx}",
        ig = credentials.ig,
        iid = credentials.iid,
    );

    let body = form_urlencoded_body(&[
        ("fromLang", from_code.to_string()),
        ("to", to_code.to_string()),
        ("text", text),
        ("token", credentials.token.clone()),
        ("key", credentials.key.to_string()),
        ("tryFetchingGenderDebiasedTranslations", "true".to_string()),
    ])?;

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint,
        headers: vec![
            (
                "Content-Type".to_string(),
                "application/x-www-form-urlencoded".to_string(),
            ),
            ("User-Agent".to_string(), BING_USER_AGENT.to_string()),
            ("Referer".to_string(), format!("https://{host}/translator")),
            ("Origin".to_string(), format!("https://{host}")),
        ],
        body: Some(body),
        service_kind: TraditionalHttpServiceKind::Bing,
    })
}

pub fn parse_bing_translation_response(
    json: &str,
    original_text: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Bing JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    // Success shape: [{"detectedLanguage":{"language":"en"},"translations":[{"text":"...","to":"zh-Hans"}]}]
    if let Some(first) = root.as_array().and_then(|items| items.first()) {
        let translated_text = first
            .get("translations")
            .and_then(Value::as_array)
            .and_then(|translations| translations.first())
            .and_then(|translation| translation.get("text"))
            .and_then(Value::as_str)
            .filter(|text| !text.is_empty())
            .unwrap_or(original_text)
            .to_string();

        let detected_language = first
            .get("detectedLanguage")
            .and_then(|detected| detected.get("language"))
            .and_then(Value::as_str)
            .filter(|code| !code.is_empty())
            .map(|code| from_bing_language_code(code).to_iso639().to_string());

        return Ok(success_result(
            translated_text,
            service_id,
            service_name,
            detected_language,
        ));
    }

    // Error shape: {"statusCode":400,"errorMessage":"..."}
    if let Some(status_code) = root.get("statusCode") {
        let message = root
            .get("errorMessage")
            .and_then(Value::as_str)
            .unwrap_or("Unknown error");
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::ServiceUnavailable,
            format!("Bing API error {status_code}: {message}"),
        )
        .with_service_id(service_id));
    }

    Err(OpenAiExecutionError::new(
        OpenAiExecutionErrorCode::InvalidResponse,
        "Unexpected response format from Bing Translate",
    )
    .with_service_id(service_id))
}

pub fn bing_language_code(language: TranslationLanguage) -> String {
    match language {
        TranslationLanguage::SimplifiedChinese => "zh-Hans".to_string(),
        TranslationLanguage::TraditionalChinese => "zh-Hant".to_string(),
        TranslationLanguage::Auto => "auto-detect".to_string(),
        TranslationLanguage::Norwegian => "nb".to_string(),
        TranslationLanguage::Filipino => "fil".to_string(),
        language => language.to_iso639().to_string(),
    }
}

pub fn from_bing_language_code(code: &str) -> TranslationLanguage {
    match code.to_ascii_lowercase().as_str() {
        "zh-hans" => TranslationLanguage::SimplifiedChinese,
        "zh-hant" => TranslationLanguage::TraditionalChinese,
        "fil" => TranslationLanguage::Filipino,
        "nb" => TranslationLanguage::Norwegian,
        _ => TranslationLanguage::from_iso639(code),
    }
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
        TraditionalHttpServiceConfig::Volcano { .. } => {
            parse_volcano_translation_response(&body, text, service_id, service_name)
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

/// Return the substring of `html` between `prefix` and the next `terminator`
/// character after it, or `None` if `prefix` is absent or unterminated.
fn extract_delimited<'a>(html: &'a str, prefix: &str, terminator: char) -> Option<&'a str> {
    let start = html.find(prefix)? + prefix.len();
    let rest = &html[start..];
    let end = rest.find(terminator)?;
    Some(&rest[..end])
}

/// Parse `params_AbusePreventionHelper = [key,"token",expiry]` into its parts,
/// mirroring the legacy regex without pulling in a regex dependency.
fn parse_bing_abuse_prevention_params(html: &str) -> Option<(i64, String, i64)> {
    let marker = html.find("params_AbusePreventionHelper")?;
    let after_marker = &html[marker..];
    let open = after_marker.find('[')?;
    let inner = &after_marker[open + 1..];
    let close = inner.find(']')?;
    let inner = &inner[..close];

    let comma1 = inner.find(',')?;
    let key: i64 = inner[..comma1].trim().parse().ok()?;

    let after_key = &inner[comma1 + 1..];
    let quote1 = after_key.find('"')?;
    let after_quote1 = &after_key[quote1 + 1..];
    let quote2 = after_quote1.find('"')?;
    let token = after_quote1[..quote2].to_string();

    let after_token = &after_quote1[quote2 + 1..];
    let comma2 = after_token.find(',')?;
    let expiry: i64 = after_token[comma2 + 1..].trim().parse().ok()?;

    Some((key, token, expiry))
}

/// Truncate `text` to at most `max` UTF-16 code units on a char boundary,
/// matching the legacy `text[..MaxTextLength]` cap without splitting a code point.
fn truncate_to_utf16_units(text: &str, max: usize) -> String {
    if text.encode_utf16().count() <= max {
        return text.to_string();
    }
    let mut units = 0;
    let mut result = String::new();
    for ch in text.chars() {
        let ch_units = ch.len_utf16();
        if units + ch_units > max {
            break;
        }
        units += ch_units;
        result.push(ch);
    }
    result
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

fn hex_encode_lower(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        push_hex_byte(&mut out, *byte);
    }
    out
}

fn sha256_hex(data: &[u8]) -> String {
    hex_encode_lower(digest::digest(&digest::SHA256, data).as_ref())
}

fn hmac_sha256(key: &[u8], data: &[u8]) -> Vec<u8> {
    let key = hmac::Key::new(hmac::HMAC_SHA256, key);
    hmac::sign(&key, data).as_ref().to_vec()
}

fn volcano_timestamps_now() -> Result<VolcanoTimestamps, OpenAiExecutionError> {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::Unknown,
                format!("System clock is before the Unix epoch: {error}"),
            )
        })?
        .as_secs();
    Ok(volcano_timestamps_from_epoch_seconds(secs))
}

/// Format `yyyyMMddTHHmmssZ` and `yyyyMMdd` UTC strings from Unix epoch seconds.
pub fn volcano_timestamps_from_epoch_seconds(secs: u64) -> VolcanoTimestamps {
    let (year, month, day, hour, minute, second) = epoch_seconds_to_utc(secs);
    VolcanoTimestamps {
        x_date: format!("{year:04}{month:02}{day:02}T{hour:02}{minute:02}{second:02}Z"),
        short_date: format!("{year:04}{month:02}{day:02}"),
    }
}

/// Convert Unix epoch seconds to UTC (year, month, day, hour, minute, second)
/// using Howard Hinnant's civil-from-days algorithm. Avoids a date-crate dependency.
fn epoch_seconds_to_utc(secs: u64) -> (i64, u32, u32, u32, u32, u32) {
    let days = (secs / 86_400) as i64;
    let rem = (secs % 86_400) as u32;
    let hour = rem / 3_600;
    let minute = (rem % 3_600) / 60;
    let second = rem % 60;

    // days since 1970-01-01 -> civil date
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as i64; // [0, 146096]
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365; // [0, 399]
    let year = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100); // [0, 365]
    let mp = (5 * doy + 2) / 153; // [0, 11]
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32; // [1, 31]
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32; // [1, 12]
    let year = if month <= 2 { year + 1 } else { year };

    (year, month, day, hour, minute, second)
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
