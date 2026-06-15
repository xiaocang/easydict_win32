use crate::openai_compatible::{OpenAiExecutionError, OpenAiExecutionErrorCode};
use crate::protocol::{
    DefinitionDto, PhoneticDto, SettingsSnapshot, SynonymDto, TranslationResultDto, WordFormDto,
    WordResultDto,
};
use crate::translation_cache::is_youdao_word_query;
use crate::translation_language::TranslationLanguage;
use aes::cipher::{block_padding::Pkcs7, BlockDecryptMut, KeyIvInit};
use base64::{engine::general_purpose, Engine as _};
use md5::{Digest as Md5Digest, Md5};
use ring::rand::{SecureRandom, SystemRandom};
use ring::{digest, hmac};
use serde_json::json;
use serde_json::Value;
use std::borrow::Cow;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const GOOGLE_TRANSLATE_ENDPOINT: &str = "https://translate.googleapis.com/translate_a/single";
pub const CAIYUN_TRANSLATE_ENDPOINT: &str = "https://api.interpreter.caiyunai.com/v1/translator";
pub const DEEPL_FREE_API_ENDPOINT: &str = "https://api-free.deepl.com/v2/translate";
pub const DEEPL_PRO_API_ENDPOINT: &str = "https://api.deepl.com/v2/translate";
pub const DEEPL_WEB_ENDPOINT: &str = "https://www2.deepl.com/jsonrpc";
pub const DEEPL_WEB_USER_AGENT: &str =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
pub const NIUTRANS_TRANSLATE_ENDPOINT: &str = "https://api.niutrans.com/NiuTransServer/translation";
pub const NIUTRANS_MAX_TEXT_LENGTH_UTF16: usize = 5000;
pub const VOLCANO_TRANSLATE_ENDPOINT: &str =
    "https://translate.volcengineapi.com/?Action=TranslateText&Version=2020-06-01";
pub const YOUDAO_OPENAPI_ENDPOINT: &str = "https://openapi.youdao.com/api";
pub const YOUDAO_WEB_DICT_ENDPOINT: &str = "https://dict.youdao.com/jsonapi_s";
pub const YOUDAO_DICT_VOICE_ENDPOINT: &str = "https://dict.youdao.com/dictvoice";
pub const YOUDAO_WEB_TRANSLATE_ENDPOINT: &str = "https://dict.youdao.com/webtranslate";
pub const YOUDAO_WEB_TRANSLATE_KEY_ENDPOINT: &str = "https://dict.youdao.com/webtranslate/key";
pub const YOUDAO_WEB_INITIAL_SIGN_KEY: &str = "asdjnjfenknafdfsdfsd";
pub const YOUDAO_WEB_AES_KEY_SOURCE: &str =
    "ydsecret://query/key/B*RGygVywfNBwpmBaZg*WT7SIOUP2T0C9WHMZN39j^DAdaZhAnxvGcCY6VYFwnHl";
pub const YOUDAO_WEB_AES_IV_SOURCE: &str =
    "ydsecret://query/iv/C@lZe2YzHtZ2CYgaXKSVfsb7Y4QWHjITPPZ0nQp87fBeJ!Iv6v^6fvi2WN@bYpJ4";
pub const YOUDAO_WEB_USER_AGENT: &str =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 \
     (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
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
pub const LINGUEE_TRANSLATE_ENDPOINT: &str = "https://linguee-api.fly.dev/api/v2/translations";

const GOOGLE_SUPPORTED_LANGUAGES: &[TranslationLanguage] = &[
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::French,
    TranslationLanguage::Spanish,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Italian,
    TranslationLanguage::German,
    TranslationLanguage::Russian,
    TranslationLanguage::Arabic,
    TranslationLanguage::Swedish,
    TranslationLanguage::Romanian,
    TranslationLanguage::Thai,
    TranslationLanguage::Dutch,
    TranslationLanguage::Hungarian,
    TranslationLanguage::Greek,
    TranslationLanguage::Danish,
    TranslationLanguage::Finnish,
    TranslationLanguage::Polish,
    TranslationLanguage::Czech,
    TranslationLanguage::Turkish,
    TranslationLanguage::Ukrainian,
    TranslationLanguage::Bulgarian,
    TranslationLanguage::Indonesian,
    TranslationLanguage::Malay,
    TranslationLanguage::Vietnamese,
    TranslationLanguage::Persian,
    TranslationLanguage::Hindi,
    TranslationLanguage::Telugu,
    TranslationLanguage::Tamil,
    TranslationLanguage::Urdu,
    TranslationLanguage::Filipino,
    TranslationLanguage::Bengali,
    TranslationLanguage::Norwegian,
    TranslationLanguage::Hebrew,
];

const DEEPL_SUPPORTED_LANGUAGES: &[TranslationLanguage] = &[
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::French,
    TranslationLanguage::Spanish,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Italian,
    TranslationLanguage::German,
    TranslationLanguage::Russian,
    TranslationLanguage::Dutch,
    TranslationLanguage::Polish,
    TranslationLanguage::Bulgarian,
    TranslationLanguage::Czech,
    TranslationLanguage::Danish,
    TranslationLanguage::Estonian,
    TranslationLanguage::Finnish,
    TranslationLanguage::Greek,
    TranslationLanguage::Hungarian,
    TranslationLanguage::Indonesian,
    TranslationLanguage::Latvian,
    TranslationLanguage::Lithuanian,
    TranslationLanguage::Norwegian,
    TranslationLanguage::Romanian,
    TranslationLanguage::Slovak,
    TranslationLanguage::Slovenian,
    TranslationLanguage::Swedish,
    TranslationLanguage::Turkish,
    TranslationLanguage::Ukrainian,
];

const CAIYUN_SUPPORTED_LANGUAGES: &[TranslationLanguage] = &[
    TranslationLanguage::Auto,
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::Spanish,
    TranslationLanguage::French,
    TranslationLanguage::Russian,
    TranslationLanguage::German,
    TranslationLanguage::Italian,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Arabic,
    TranslationLanguage::Hindi,
    TranslationLanguage::Indonesian,
    TranslationLanguage::Malay,
    TranslationLanguage::Thai,
    TranslationLanguage::Vietnamese,
];

const NIUTRANS_SUPPORTED_LANGUAGES: &[TranslationLanguage] = &[
    TranslationLanguage::Auto,
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::French,
    TranslationLanguage::Spanish,
    TranslationLanguage::German,
    TranslationLanguage::Russian,
    TranslationLanguage::Arabic,
    TranslationLanguage::Italian,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Dutch,
    TranslationLanguage::Polish,
    TranslationLanguage::Turkish,
    TranslationLanguage::Vietnamese,
    TranslationLanguage::Thai,
    TranslationLanguage::Indonesian,
    TranslationLanguage::Malay,
    TranslationLanguage::Hindi,
    TranslationLanguage::Greek,
    TranslationLanguage::Czech,
    TranslationLanguage::Danish,
    TranslationLanguage::Finnish,
    TranslationLanguage::Hungarian,
    TranslationLanguage::Norwegian,
    TranslationLanguage::Romanian,
    TranslationLanguage::Slovak,
    TranslationLanguage::Swedish,
    TranslationLanguage::Bulgarian,
    TranslationLanguage::Estonian,
    TranslationLanguage::Latvian,
    TranslationLanguage::Lithuanian,
    TranslationLanguage::Slovenian,
    TranslationLanguage::Ukrainian,
    TranslationLanguage::Persian,
    TranslationLanguage::Hebrew,
    TranslationLanguage::Bengali,
    TranslationLanguage::Tamil,
    TranslationLanguage::Telugu,
    TranslationLanguage::Urdu,
    TranslationLanguage::Filipino,
];

const YOUDAO_SUPPORTED_LANGUAGES: &[TranslationLanguage] = &[
    TranslationLanguage::Auto,
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::French,
    TranslationLanguage::Spanish,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Italian,
    TranslationLanguage::German,
    TranslationLanguage::Russian,
    TranslationLanguage::Arabic,
    TranslationLanguage::Swedish,
    TranslationLanguage::Thai,
    TranslationLanguage::Dutch,
    TranslationLanguage::Indonesian,
    TranslationLanguage::Vietnamese,
    TranslationLanguage::Hindi,
];

const VOLCANO_SUPPORTED_LANGUAGES: &[TranslationLanguage] = &[
    TranslationLanguage::Auto,
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::TraditionalChinese,
    TranslationLanguage::ClassicalChinese,
    TranslationLanguage::English,
    TranslationLanguage::Japanese,
    TranslationLanguage::Korean,
    TranslationLanguage::French,
    TranslationLanguage::German,
    TranslationLanguage::Spanish,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Italian,
    TranslationLanguage::Russian,
    TranslationLanguage::Arabic,
    TranslationLanguage::Thai,
    TranslationLanguage::Vietnamese,
    TranslationLanguage::Indonesian,
    TranslationLanguage::Hindi,
    TranslationLanguage::Hebrew,
    TranslationLanguage::Ukrainian,
    TranslationLanguage::Urdu,
    TranslationLanguage::Turkish,
    TranslationLanguage::Tamil,
    TranslationLanguage::Telugu,
    TranslationLanguage::Slovenian,
    TranslationLanguage::Slovak,
    TranslationLanguage::Swedish,
    TranslationLanguage::Norwegian,
    TranslationLanguage::Bengali,
    TranslationLanguage::Malay,
    TranslationLanguage::Romanian,
    TranslationLanguage::Lithuanian,
    TranslationLanguage::Latvian,
    TranslationLanguage::Czech,
    TranslationLanguage::Dutch,
    TranslationLanguage::Finnish,
    TranslationLanguage::Danish,
    TranslationLanguage::Persian,
    TranslationLanguage::Polish,
    TranslationLanguage::Bulgarian,
    TranslationLanguage::Estonian,
    TranslationLanguage::Hungarian,
];

const LINGUEE_SUPPORTED_LANGUAGES: &[TranslationLanguage] = &[
    TranslationLanguage::English,
    TranslationLanguage::German,
    TranslationLanguage::French,
    TranslationLanguage::Spanish,
    TranslationLanguage::Italian,
    TranslationLanguage::Portuguese,
    TranslationLanguage::Dutch,
    TranslationLanguage::Polish,
    TranslationLanguage::Russian,
    TranslationLanguage::Bulgarian,
    TranslationLanguage::Czech,
    TranslationLanguage::Danish,
    TranslationLanguage::Greek,
    TranslationLanguage::Estonian,
    TranslationLanguage::Finnish,
    TranslationLanguage::Hungarian,
    TranslationLanguage::Lithuanian,
    TranslationLanguage::Latvian,
    TranslationLanguage::Romanian,
    TranslationLanguage::Slovak,
    TranslationLanguage::Slovenian,
    TranslationLanguage::Swedish,
    TranslationLanguage::SimplifiedChinese,
    TranslationLanguage::Japanese,
];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TraditionalHttpServiceKind {
    Google,
    GoogleWeb,
    Caiyun,
    DeepLApi,
    DeepLWeb,
    NiuTrans,
    Volcano,
    Bing,
    Linguee,
    YoudaoOpenApi,
    YoudaoWebDict,
    YoudaoWebTranslateKey,
    YoudaoWebTranslate,
}

pub fn traditional_http_supports_language_pair_for_kind(
    service_kind: TraditionalHttpServiceKind,
    from: TranslationLanguage,
    to: TranslationLanguage,
) -> bool {
    let supported_languages = traditional_http_supported_languages_for_kind(service_kind);
    if from == TranslationLanguage::Auto {
        return supported_languages.contains(&to);
    }

    supported_languages.contains(&from) && supported_languages.contains(&to)
}

fn traditional_http_supported_languages_for_kind(
    service_kind: TraditionalHttpServiceKind,
) -> &'static [TranslationLanguage] {
    match service_kind {
        TraditionalHttpServiceKind::Google | TraditionalHttpServiceKind::GoogleWeb => {
            GOOGLE_SUPPORTED_LANGUAGES
        }
        TraditionalHttpServiceKind::Caiyun => CAIYUN_SUPPORTED_LANGUAGES,
        TraditionalHttpServiceKind::DeepLApi | TraditionalHttpServiceKind::DeepLWeb => {
            DEEPL_SUPPORTED_LANGUAGES
        }
        TraditionalHttpServiceKind::NiuTrans => NIUTRANS_SUPPORTED_LANGUAGES,
        TraditionalHttpServiceKind::Volcano => VOLCANO_SUPPORTED_LANGUAGES,
        TraditionalHttpServiceKind::Bing => GOOGLE_SUPPORTED_LANGUAGES,
        TraditionalHttpServiceKind::Linguee => LINGUEE_SUPPORTED_LANGUAGES,
        TraditionalHttpServiceKind::YoudaoOpenApi
        | TraditionalHttpServiceKind::YoudaoWebDict
        | TraditionalHttpServiceKind::YoudaoWebTranslateKey
        | TraditionalHttpServiceKind::YoudaoWebTranslate => YOUDAO_SUPPORTED_LANGUAGES,
    }
}

fn validate_traditional_http_language_pair(
    service_kind: TraditionalHttpServiceKind,
    from: TranslationLanguage,
    to: TranslationLanguage,
) -> Result<(), OpenAiExecutionError> {
    if !traditional_http_supports_language_pair_for_kind(service_kind, from, to) {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::UnsupportedLanguage,
            format!("Language pair not supported: {from:?} -> {to:?}"),
        ));
    }

    Ok(())
}

#[derive(Clone, Debug, PartialEq)]
pub struct TraditionalHttpRequestPlan {
    pub method: &'static str,
    pub endpoint: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<String>,
    pub service_kind: TraditionalHttpServiceKind,
}

type Aes128CbcDec = cbc::Decryptor<aes::Aes128>;

#[derive(Clone, Debug, PartialEq)]
pub enum TraditionalHttpServiceConfig {
    Google,
    GoogleWeb,
    Caiyun {
        api_key: String,
    },
    DeepLApi {
        api_key: String,
        use_quality_optimized: bool,
    },
    DeepLWeb {
        fallback_api_key: Option<String>,
    },
    NiuTrans {
        api_key: String,
    },
    Volcano {
        access_key_id: String,
        secret_access_key: String,
    },
    Linguee,
    YoudaoOpenApi {
        app_key: String,
        app_secret: String,
    },
    YoudaoWebDict,
    YoudaoWebTranslate,
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
        Self::from_settings_with_timeout(settings, None)
    }

    pub fn from_settings_with_timeout(
        settings: &SettingsSnapshot,
        timeout_ms: Option<u32>,
    ) -> Result<Self, OpenAiExecutionError> {
        Ok(Self {
            client: build_proxy_aware_blocking_client(settings, timeout_ms)?,
        })
    }
}

/// Build a 60s-timeout blocking reqwest client honoring the proxy snapshot
/// settings (with loopback bypass), shared by the traditional-HTTP providers.
fn build_proxy_aware_blocking_client(
    settings: &SettingsSnapshot,
    timeout_ms: Option<u32>,
) -> Result<reqwest::blocking::Client, OpenAiExecutionError> {
    let timeout = request_timeout_duration(timeout_ms, Duration::from_secs(60));
    let mut builder = reqwest::blocking::Client::builder().timeout(timeout);

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

    builder.build().map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::NetworkError,
            format!("Could not create traditional HTTP client: {error}"),
        )
    })
}

fn request_timeout_duration(timeout_ms: Option<u32>, default: Duration) -> Duration {
    timeout_ms
        .filter(|value| *value > 0)
        .map(|value| Duration::from_millis(u64::from(value)))
        .unwrap_or(default)
}

impl TraditionalHttpClient for ReqwestTraditionalHttpClient {
    fn execute(
        &mut self,
        request: &TraditionalHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        let endpoint = traditional_http_request_endpoint(request);
        let mut builder = match request.method {
            "GET" => self.client.get(endpoint.as_ref()),
            "POST" => {
                let mut post = self.client.post(endpoint.as_ref());
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
                TraditionalHttpServiceKind::DeepLWeb => {
                    deepl_web_error_from_status(status_code, reason)
                }
                _ => traditional_http_error_from_status(status_code, reason),
            };
            return Err(error);
        }

        Ok(body)
    }
}

#[cfg(debug_assertions)]
fn traditional_http_request_endpoint(request: &TraditionalHttpRequestPlan) -> Cow<'_, str> {
    if let Some(env_key) = traditional_http_endpoint_override_env_key(request.service_kind) {
        if let Ok(endpoint) = std::env::var(env_key) {
            let endpoint = endpoint.trim();
            if !endpoint.is_empty() {
                if request.method == "GET"
                    || request.service_kind == TraditionalHttpServiceKind::Bing
                {
                    if let Some(endpoint_with_query) =
                        debug_endpoint_with_original_query(endpoint, &request.endpoint)
                    {
                        return Cow::Owned(endpoint_with_query);
                    }
                }
                return Cow::Owned(endpoint.to_string());
            }
        }
    }

    Cow::Borrowed(&request.endpoint)
}

#[cfg(debug_assertions)]
fn debug_endpoint_with_original_query(
    override_endpoint: &str,
    original_endpoint: &str,
) -> Option<String> {
    let mut override_url = reqwest::Url::parse(override_endpoint).ok()?;
    if override_url.query().is_some() {
        return Some(override_url.to_string());
    }

    let original_url = reqwest::Url::parse(original_endpoint).ok()?;
    if let Some(query) = original_url.query() {
        override_url.set_query(Some(query));
    }
    Some(override_url.to_string())
}

#[cfg(not(debug_assertions))]
fn traditional_http_request_endpoint(request: &TraditionalHttpRequestPlan) -> Cow<'_, str> {
    Cow::Borrowed(&request.endpoint)
}

#[cfg(debug_assertions)]
fn traditional_http_endpoint_override_env_key(
    service_kind: TraditionalHttpServiceKind,
) -> Option<&'static str> {
    match service_kind {
        TraditionalHttpServiceKind::Google => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_GOOGLE")
        }
        TraditionalHttpServiceKind::GoogleWeb => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_GOOGLE_WEB")
        }
        TraditionalHttpServiceKind::Caiyun => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_CAIYUN")
        }
        TraditionalHttpServiceKind::DeepLApi => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_DEEPL_API")
        }
        TraditionalHttpServiceKind::DeepLWeb => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_DEEPL_WEB")
        }
        TraditionalHttpServiceKind::NiuTrans => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_NIUTRANS")
        }
        TraditionalHttpServiceKind::Linguee => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_LINGUEE")
        }
        TraditionalHttpServiceKind::Volcano => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_VOLCANO")
        }
        TraditionalHttpServiceKind::Bing => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_BING_TRANSLATE")
        }
        TraditionalHttpServiceKind::YoudaoOpenApi => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_YOUDAO_OPENAPI")
        }
        TraditionalHttpServiceKind::YoudaoWebDict => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_YOUDAO_WEB_DICT")
        }
        TraditionalHttpServiceKind::YoudaoWebTranslateKey => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_YOUDAO_WEB_TRANSLATE_KEY")
        }
        TraditionalHttpServiceKind::YoudaoWebTranslate => {
            Some("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_YOUDAO_WEB_TRANSLATE")
        }
    }
}

pub fn traditional_http_config_for_service(
    service_id: &str,
    settings: &SettingsSnapshot,
) -> Option<TraditionalHttpServiceConfig> {
    match service_id {
        "google" => Some(TraditionalHttpServiceConfig::Google),
        "google_web" => Some(TraditionalHttpServiceConfig::GoogleWeb),
        "caiyun" => Some(TraditionalHttpServiceConfig::Caiyun {
            api_key: settings.caiyun_token.clone().unwrap_or_default(),
        }),
        "deepl" => {
            if deepl_uses_native_api(settings) {
                Some(TraditionalHttpServiceConfig::DeepLApi {
                    api_key: settings.deep_l_api_key.clone().unwrap_or_default(),
                    use_quality_optimized: settings.deep_l_use_quality_optimized.unwrap_or(false),
                })
            } else {
                Some(TraditionalHttpServiceConfig::DeepLWeb {
                    fallback_api_key: normalized_optional(settings.deep_l_api_key.as_deref()),
                })
            }
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
        "youdao"
            if settings.youdao_use_official_api.unwrap_or(false)
                && normalized_optional(settings.youdao_app_key.as_deref()).is_some()
                && normalized_optional(settings.youdao_app_secret.as_deref()).is_some() =>
        {
            Some(TraditionalHttpServiceConfig::YoudaoOpenApi {
                app_key: settings.youdao_app_key.clone().unwrap_or_default(),
                app_secret: settings.youdao_app_secret.clone().unwrap_or_default(),
            })
        }
        // Linguee is keyless and has a Rust-owned request plan/parser, so the
        // default rs route no longer needs the legacy ENABLE_LINGUEE_SERVICE gate.
        "linguee" => Some(TraditionalHttpServiceConfig::Linguee),
        _ => None,
    }
}

pub fn traditional_http_config_for_request(
    service_id: &str,
    settings: &SettingsSnapshot,
    text: &str,
) -> Option<TraditionalHttpServiceConfig> {
    if let Some(config) = traditional_http_config_for_service(service_id, settings) {
        return Some(config);
    }

    if service_id == "youdao" {
        return Some(if is_youdao_word_query(text) {
            TraditionalHttpServiceConfig::YoudaoWebDict
        } else {
            TraditionalHttpServiceConfig::YoudaoWebTranslate
        });
    }

    None
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
        TraditionalHttpServiceConfig::GoogleWeb => {
            build_google_web_translation_request_plan(text, from_language, to_language)
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
        TraditionalHttpServiceConfig::DeepLWeb { .. } => {
            build_deepl_web_translation_request_plan(text, from_language, to_language)
        }
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
        TraditionalHttpServiceConfig::Linguee => {
            build_linguee_translation_request_plan(text, from_language, to_language)
        }
        TraditionalHttpServiceConfig::YoudaoOpenApi {
            app_key,
            app_secret,
        } => build_youdao_openapi_translation_request_plan(
            app_key,
            app_secret,
            text,
            from_language,
            to_language,
        ),
        TraditionalHttpServiceConfig::YoudaoWebDict => {
            build_youdao_web_dict_translation_request_plan(text, from_language, to_language)
        }
        TraditionalHttpServiceConfig::YoudaoWebTranslate => Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            "Youdao webtranslate requires the two-step native executor",
        )),
    }
}

pub fn build_google_web_translation_request_plan(
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::GoogleWeb,
        from_language,
        to_language,
    )?;

    let mut url = reqwest::Url::parse(GOOGLE_TRANSLATE_ENDPOINT).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Google WebApp endpoint: {error}"),
        )
    })?;
    url.query_pairs_mut()
        .append_pair("client", "gtx")
        .append_pair("sl", google_language_code(from_language))
        .append_pair("tl", google_language_code(to_language));

    for dt in ["at", "bd", "ex", "ld", "md", "qca", "rw", "rm", "ss", "t"] {
        url.query_pairs_mut().append_pair("dt", dt);
    }

    url.query_pairs_mut()
        .append_pair("ie", "UTF-8")
        .append_pair("oe", "UTF-8")
        .append_pair("q", text);

    Ok(TraditionalHttpRequestPlan {
        method: "GET",
        endpoint: url.to_string(),
        headers: Vec::new(),
        body: None,
        service_kind: TraditionalHttpServiceKind::GoogleWeb,
    })
}

pub fn build_google_translation_request_plan(
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::Google,
        from_language,
        to_language,
    )?;

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
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::Caiyun,
        from_language,
        to_language,
    )?;

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
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::DeepLApi,
        from_language,
        to_language,
    )?;

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

pub fn build_deepl_web_translation_request_plan(
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    let request_id = new_deepl_web_request_id()?;
    let timestamp = deepl_aligned_timestamp(current_unix_time_millis()?, deepl_i_count(text));
    build_deepl_web_translation_request_plan_with_values(
        text,
        from_language,
        to_language,
        request_id,
        timestamp,
    )
}

pub fn build_deepl_web_translation_request_plan_with_values(
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    request_id: i64,
    timestamp: i64,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("DeepL query text", text)?;
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::DeepLWeb,
        from_language,
        to_language,
    )?;

    let target_code = deepl_language_code(to_language, true).to_ascii_uppercase();
    let source_code = if from_language == TranslationLanguage::Auto {
        "auto".to_string()
    } else {
        deepl_language_code(from_language, true)
    }
    .to_ascii_uppercase();

    let payload = json!({
        "jsonrpc": "2.0",
        "method": "LMT_handle_texts",
        "id": request_id,
        "params": {
            "texts": [{ "text": text, "requestAlternatives": 3 }],
            "splitting": "newlines",
            "lang": {
                "source_lang_user_selected": source_code,
                "target_lang": target_code,
            },
            "timestamp": timestamp,
            "commonJobParams": {
                "wasSpoken": false,
                "transcribe_as": "",
            },
        },
    });
    let body = apply_deepl_dynamic_spacing(&payload.to_string(), request_id);

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint: DEEPL_WEB_ENDPOINT.to_string(),
        headers: vec![
            ("Accept".to_string(), "*/*".to_string()),
            ("Accept-Language".to_string(), "en-US,en;q=0.9".to_string()),
            ("Origin".to_string(), "https://www.deepl.com".to_string()),
            ("Referer".to_string(), "https://www.deepl.com/".to_string()),
            ("User-Agent".to_string(), DEEPL_WEB_USER_AGENT.to_string()),
            ("Content-Type".to_string(), "application/json".to_string()),
        ],
        body: Some(body),
        service_kind: TraditionalHttpServiceKind::DeepLWeb,
    })
}

pub fn build_niutrans_translation_request_plan(
    api_key: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("NiuTrans API key", api_key)?;
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::NiuTrans,
        from_language,
        to_language,
    )?;

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

pub fn build_youdao_openapi_translation_request_plan(
    app_key: &str,
    app_secret: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    let salt = new_request_id()?;
    let curtime = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::Unknown,
                format!("Could not compute Youdao request timestamp: {error}"),
            )
        })?
        .as_secs()
        .to_string();

    build_youdao_openapi_translation_request_plan_with_nonce(
        app_key,
        app_secret,
        text,
        from_language,
        to_language,
        &salt,
        &curtime,
    )
}

pub fn build_youdao_openapi_translation_request_plan_with_nonce(
    app_key: &str,
    app_secret: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    salt: &str,
    curtime: &str,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Youdao AppKey", app_key)?;
    validate_required("Youdao AppSecret", app_secret)?;
    validate_required("Youdao salt", salt)?;
    validate_required("Youdao curtime", curtime)?;
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::YoudaoOpenApi,
        from_language,
        to_language,
    )?;

    let signature_input = youdao_openapi_signature_input(text);
    let sign = compute_youdao_openapi_sign(
        app_key.trim(),
        &signature_input,
        salt,
        curtime,
        app_secret.trim(),
    );
    let fields = vec![
        ("q", text.to_string()),
        ("from", youdao_language_code(from_language).to_string()),
        ("to", youdao_language_code(to_language).to_string()),
        ("appKey", app_key.trim().to_string()),
        ("salt", salt.to_string()),
        ("sign", sign),
        ("signType", "v3".to_string()),
        ("curtime", curtime.to_string()),
    ];

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint: YOUDAO_OPENAPI_ENDPOINT.to_string(),
        headers: vec![(
            "Content-Type".to_string(),
            "application/x-www-form-urlencoded".to_string(),
        )],
        body: Some(form_urlencoded_body(&fields)?),
        service_kind: TraditionalHttpServiceKind::YoudaoOpenApi,
    })
}

pub fn build_youdao_web_dict_translation_request_plan(
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Youdao query text", text)?;
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::YoudaoWebDict,
        from_language,
        to_language,
    )?;

    let ww = format!("{text}webdict");
    let time = youdao_web_dict_time(text);
    let salt = md5_hex(&ww);
    let sign = compute_youdao_web_dict_sign(text, time, &salt);
    let fields = vec![
        ("q", text.to_string()),
        (
            "le",
            youdao_web_dict_language_code(from_language, to_language).to_string(),
        ),
        ("client", "web".to_string()),
        ("t", time.to_string()),
        ("sign", sign),
        ("keyfrom", "webdict".to_string()),
    ];

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint: format!("{YOUDAO_WEB_DICT_ENDPOINT}?doctype=json&jsonversion=4"),
        headers: vec![
            (
                "Content-Type".to_string(),
                "application/x-www-form-urlencoded".to_string(),
            ),
            (
                "User-Agent".to_string(),
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36".to_string(),
            ),
            (
                "Referer".to_string(),
                "https://dict.youdao.com/".to_string(),
            ),
        ],
        body: Some(form_urlencoded_body(&fields)?),
        service_kind: TraditionalHttpServiceKind::YoudaoWebDict,
    })
}

pub fn build_youdao_web_translate_key_request_plan(
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    build_youdao_web_translate_key_request_plan_with_time(&current_unix_time_millis_string()?)
}

pub fn build_youdao_web_translate_key_request_plan_with_time(
    mystic_time: &str,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Youdao mysticTime", mystic_time)?;

    let sign = compute_youdao_web_translate_sign(YOUDAO_WEB_INITIAL_SIGN_KEY, mystic_time);
    let mut url = reqwest::Url::parse(YOUDAO_WEB_TRANSLATE_KEY_ENDPOINT).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Youdao webtranslate key endpoint: {error}"),
        )
    })?;
    url.query_pairs_mut()
        .append_pair("keyid", "webfanyi-key-getter")
        .append_pair("sign", &sign)
        .append_pair("client", "fanyideskweb")
        .append_pair("product", "webfanyi")
        .append_pair("appVersion", "1.0.0")
        .append_pair("vendor", "web")
        .append_pair("pointParam", "client,mysticTime,product")
        .append_pair("mysticTime", mystic_time)
        .append_pair("keyfrom", "fanyi.web");

    Ok(TraditionalHttpRequestPlan {
        method: "GET",
        endpoint: url.to_string(),
        headers: youdao_web_headers(false),
        body: None,
        service_kind: TraditionalHttpServiceKind::YoudaoWebTranslateKey,
    })
}

pub fn build_youdao_web_translate_request_plan(
    sign_key: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    build_youdao_web_translate_request_plan_with_time(
        sign_key,
        text,
        from_language,
        to_language,
        &current_unix_time_millis_string()?,
    )
}

pub fn build_youdao_web_translate_request_plan_with_time(
    sign_key: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    mystic_time: &str,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_required("Youdao webtranslate sign key", sign_key)?;
    validate_required("Youdao query text", text)?;
    validate_required("Youdao mysticTime", mystic_time)?;
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::YoudaoWebTranslate,
        from_language,
        to_language,
    )?;

    let sign = compute_youdao_web_translate_sign(sign_key, mystic_time);
    let fields = vec![
        ("i", text.to_string()),
        ("from", youdao_language_code(from_language).to_string()),
        ("to", youdao_language_code(to_language).to_string()),
        ("dictResult", "true".to_string()),
        ("keyid", "webfanyi".to_string()),
        ("sign", sign),
        ("client", "fanyideskweb".to_string()),
        ("product", "webfanyi".to_string()),
        ("appVersion", "1.0.0".to_string()),
        ("vendor", "web".to_string()),
        ("pointParam", "client,mysticTime,product".to_string()),
        ("mysticTime", mystic_time.to_string()),
        ("keyfrom", "fanyi.web".to_string()),
    ];

    Ok(TraditionalHttpRequestPlan {
        method: "POST",
        endpoint: YOUDAO_WEB_TRANSLATE_ENDPOINT.to_string(),
        headers: youdao_web_headers(true),
        body: Some(form_urlencoded_body(&fields)?),
        service_kind: TraditionalHttpServiceKind::YoudaoWebTranslate,
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
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::Volcano,
        from_language,
        to_language,
    )?;

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
// network-free building blocks for that flow. `bing` stays outside
// `traditional_http_config_for_service` because it needs the stateful two-phase
// executor, but Quick Translate routes it through `NativeBingQuickTranslateBackend`.
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
    let ig = extract_js_property_string_value(html, "IG").unwrap_or_default();
    let iid = extract_html_attribute_value(html, "data-iid")
        .unwrap_or_else(|| BING_DEFAULT_IID.to_string());

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
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::Bing,
        from_language,
        to_language,
    )?;

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

/// The Bing translator page fetch result: raw HTML plus the host after any
/// redirect (Bing may redirect `cn.bing.com`), used to address the translate API.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BingTranslatorPage {
    pub html: String,
    pub resolved_host: String,
}

/// A raw Bing translate HTTP response, kept unparsed so the executor can branch
/// on retryable statuses and non-JSON (captcha/redirect) bodies before parsing.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BingHttpResponse {
    pub status: u16,
    pub body: String,
}

/// Two-phase Bing transport: fetch the translator page for session credentials,
/// then post the translate request. Split out so the executor is unit-testable.
pub trait BingHttpClient {
    fn fetch_translator_html(
        &mut self,
        host: &str,
    ) -> Result<BingTranslatorPage, OpenAiExecutionError>;
    fn execute_translate(
        &mut self,
        plan: &TraditionalHttpRequestPlan,
    ) -> Result<BingHttpResponse, OpenAiExecutionError>;
}

pub struct ReqwestBingHttpClient {
    client: reqwest::blocking::Client,
}

impl ReqwestBingHttpClient {
    pub fn from_settings(settings: &SettingsSnapshot) -> Result<Self, OpenAiExecutionError> {
        Self::from_settings_with_timeout(settings, None)
    }

    pub fn from_settings_with_timeout(
        settings: &SettingsSnapshot,
        timeout_ms: Option<u32>,
    ) -> Result<Self, OpenAiExecutionError> {
        Ok(Self {
            client: build_proxy_aware_blocking_client(settings, timeout_ms)?,
        })
    }
}

impl BingHttpClient for ReqwestBingHttpClient {
    fn fetch_translator_html(
        &mut self,
        host: &str,
    ) -> Result<BingTranslatorPage, OpenAiExecutionError> {
        let url = bing_translator_page_endpoint(host);
        let response = self
            .client
            .get(&url)
            .header("User-Agent", BING_USER_AGENT)
            .send()
            .map_err(|error| {
                OpenAiExecutionError::new(
                    OpenAiExecutionErrorCode::NetworkError,
                    format!("Bing translator page request failed: {error}"),
                )
            })?;
        let resolved_host = response.url().host_str().unwrap_or(host).to_string();
        let status = response.status();
        let html = response.text().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not read Bing translator page: {error}"),
            )
        })?;
        if !status.is_success() {
            return Err(traditional_http_error_from_status(
                status.as_u16(),
                status.canonical_reason().unwrap_or("Unknown"),
            )
            .with_service_id("bing"));
        }
        Ok(BingTranslatorPage {
            html,
            resolved_host,
        })
    }

    fn execute_translate(
        &mut self,
        plan: &TraditionalHttpRequestPlan,
    ) -> Result<BingHttpResponse, OpenAiExecutionError> {
        let endpoint = traditional_http_request_endpoint(plan);
        let mut builder = self.client.post(endpoint.as_ref());
        if let Some(body) = &plan.body {
            builder = builder.body(body.clone());
        }
        for (name, value) in &plan.headers {
            builder = builder.header(name, value);
        }
        let response = builder.send().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Bing translate request failed: {error}"),
            )
        })?;
        let status = response.status().as_u16();
        let body = response.text().map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::NetworkError,
                format!("Could not read Bing translate response: {error}"),
            )
        })?;
        Ok(BingHttpResponse { status, body })
    }
}

#[cfg(debug_assertions)]
fn bing_translator_page_endpoint(host: &str) -> String {
    std::env::var("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_BING_TRANSLATOR")
        .ok()
        .and_then(|value| {
            let value = value.trim();
            (!value.is_empty()).then(|| value.to_string())
        })
        .unwrap_or_else(|| format!("https://{host}{BING_TRANSLATOR_PATH}"))
}

#[cfg(not(debug_assertions))]
fn bing_translator_page_endpoint(host: &str) -> String {
    format!("https://{host}{BING_TRANSLATOR_PATH}")
}

/// Run the full two-phase Bing translation (fetch credentials, then translate),
/// retrying once with fresh credentials on a 429/401 or a non-JSON 200 body —
/// mirroring the legacy `BingTranslateService` flow. Credentials are fetched per
/// call (no cross-call cache in this per-request backend model); the attempt
/// index doubles as the SFX cache-buster.
pub fn translate_bing_service<C: BingHttpClient>(
    client: &mut C,
    host: &str,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    service_id: impl Into<String>,
    service_name: impl Into<String>,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let service_id = service_id.into();
    let service_name = service_name.into();
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::Bing,
        from_language,
        to_language,
    )
    .map_err(|error| attach_service_id(error, &service_id))?;
    const MAX_ATTEMPTS: u32 = 2;

    for attempt in 1..=MAX_ATTEMPTS {
        let page = client
            .fetch_translator_html(host)
            .map_err(|error| attach_service_id(error, &service_id))?;
        let mut credentials = parse_bing_credentials_from_html(&page.html)
            .map_err(|error| attach_service_id(error, &service_id))?;
        if credentials.ig.is_empty() {
            credentials.ig = generate_bing_ig()?;
        }

        let plan = build_bing_translate_request_plan(
            &credentials,
            &page.resolved_host,
            text,
            from_language,
            to_language,
            attempt as u64,
        )
        .map_err(|error| attach_service_id(error, &service_id))?;

        let response = client
            .execute_translate(&plan)
            .map_err(|error| attach_service_id(error, &service_id))?;

        let retryable_status = response.status == 429 || response.status == 401;
        if response.status < 200 || response.status >= 300 {
            if retryable_status && attempt < MAX_ATTEMPTS {
                continue;
            }
            return Err(traditional_http_error_from_status(
                response.status,
                "Bing translate error",
            )
            .with_service_id(service_id));
        }

        // A 200 can still carry HTML (captcha/redirect); retry once before failing.
        let trimmed = response.body.trim_start();
        if !trimmed.starts_with('[') && !trimmed.starts_with('{') {
            if attempt < MAX_ATTEMPTS {
                continue;
            }
            return Err(OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::InvalidResponse,
                "Bing returned a non-JSON response",
            )
            .with_service_id(service_id));
        }

        return parse_bing_translation_response(&response.body, text, service_id, service_name);
    }

    Err(OpenAiExecutionError::new(
        OpenAiExecutionErrorCode::ServiceUnavailable,
        "Bing translation failed after retries",
    )
    .with_service_id(service_id))
}

/// Generate a random 32-char uppercase-hex IG value, matching the legacy
/// `Convert.ToHexString` fallback when the page omits the IG token.
pub fn generate_bing_ig() -> Result<String, OpenAiExecutionError> {
    let rng = SystemRandom::new();
    let mut bytes = [0_u8; 16];
    rng.fill(&mut bytes).map_err(|_| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::Unknown,
            "Could not generate Bing IG value",
        )
    })?;
    Ok(hex_encode_lower(&bytes).to_ascii_uppercase())
}

// ----------------------------------------------------------------------------
// Linguee dictionary (keyless public proxy, default Rust-native registration).
// A single GET returns translations with context; the first translation is the
// primary text and subsequent translations are carried as alternatives.
// ----------------------------------------------------------------------------

pub fn build_linguee_translation_request_plan(
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> Result<TraditionalHttpRequestPlan, OpenAiExecutionError> {
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::Linguee,
        from_language,
        to_language,
    )?;

    let from_code = linguee_language_code(from_language)?;
    let to_code = linguee_language_code(to_language)?;

    let mut url = reqwest::Url::parse(LINGUEE_TRANSLATE_ENDPOINT).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Linguee endpoint: {error}"),
        )
    })?;
    url.query_pairs_mut()
        .append_pair("query", text)
        .append_pair("src", from_code)
        .append_pair("dst", to_code);

    Ok(TraditionalHttpRequestPlan {
        method: "GET",
        endpoint: url.to_string(),
        headers: Vec::new(),
        body: None,
        service_kind: TraditionalHttpServiceKind::Linguee,
    })
}

pub fn parse_linguee_translation_response(
    json: &str,
    original_text: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Linguee JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    // Root is an array of dictionary entries; the first entry's translations
    // give the primary text (index 0) and the alternatives (indices 1..).
    let translation_texts: Vec<String> = root
        .as_array()
        .and_then(|entries| entries.first())
        .and_then(|entry| entry.get("translations"))
        .and_then(Value::as_array)
        .map(|translations| {
            translations
                .iter()
                .filter_map(|translation| translation.get("text"))
                .filter_map(Value::as_str)
                .filter(|text| !text.is_empty())
                .map(str::to_string)
                .collect()
        })
        .unwrap_or_default();

    let mut texts = translation_texts.into_iter();
    let translated_text = texts.next().unwrap_or_else(|| original_text.to_string());
    let alternatives: Vec<String> = texts.collect();

    let mut result = success_result(translated_text, service_id, service_name, None);
    if !alternatives.is_empty() {
        result.alternatives = Some(alternatives);
    }
    Ok(result)
}

pub fn linguee_language_code(
    language: TranslationLanguage,
) -> Result<&'static str, OpenAiExecutionError> {
    match language {
        TranslationLanguage::Auto => Ok("auto"),
        TranslationLanguage::English => Ok("en"),
        TranslationLanguage::German => Ok("de"),
        TranslationLanguage::French => Ok("fr"),
        TranslationLanguage::Spanish => Ok("es"),
        TranslationLanguage::Italian => Ok("it"),
        TranslationLanguage::Portuguese => Ok("pt"),
        TranslationLanguage::Dutch => Ok("nl"),
        TranslationLanguage::Polish => Ok("pl"),
        TranslationLanguage::Russian => Ok("ru"),
        TranslationLanguage::Bulgarian => Ok("bg"),
        TranslationLanguage::Czech => Ok("cs"),
        TranslationLanguage::Danish => Ok("da"),
        TranslationLanguage::Greek => Ok("el"),
        TranslationLanguage::Estonian => Ok("et"),
        TranslationLanguage::Finnish => Ok("fi"),
        TranslationLanguage::Hungarian => Ok("hu"),
        TranslationLanguage::Lithuanian => Ok("lt"),
        TranslationLanguage::Latvian => Ok("lv"),
        TranslationLanguage::Romanian => Ok("ro"),
        TranslationLanguage::Slovak => Ok("sk"),
        TranslationLanguage::Slovenian => Ok("sl"),
        TranslationLanguage::Swedish => Ok("sv"),
        TranslationLanguage::SimplifiedChinese => Ok("zh"),
        TranslationLanguage::Japanese => Ok("ja"),
        _ => Err(unsupported_language_error("Linguee", language)),
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

    match config {
        TraditionalHttpServiceConfig::DeepLWeb { fallback_api_key } => {
            return translate_deepl_web_service(
                client,
                fallback_api_key.as_deref(),
                text,
                from_language,
                to_language,
                service_id,
                service_name,
            );
        }
        TraditionalHttpServiceConfig::YoudaoWebDict => {
            return translate_youdao_web_dict_service(
                client,
                text,
                from_language,
                to_language,
                service_id,
                service_name,
            );
        }
        TraditionalHttpServiceConfig::YoudaoWebTranslate => {
            return translate_youdao_web_translate_service(
                client,
                text,
                from_language,
                to_language,
                service_id,
                service_name,
            );
        }
        _ => {}
    }

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
        TraditionalHttpServiceConfig::GoogleWeb => {
            parse_google_web_translation_response(&body, service_id, service_name)
        }
        TraditionalHttpServiceConfig::Caiyun { .. } => {
            parse_caiyun_translation_response(&body, text, service_id, service_name)
        }
        TraditionalHttpServiceConfig::DeepLApi { .. } => {
            parse_deepl_api_translation_response(&body, service_id, service_name)
        }
        TraditionalHttpServiceConfig::DeepLWeb { .. } => {
            parse_deepl_web_translation_response(&body, service_id, service_name)
        }
        TraditionalHttpServiceConfig::NiuTrans { .. } => {
            parse_niutrans_translation_response(&body, text, service_id, service_name)
        }
        TraditionalHttpServiceConfig::Volcano { .. } => {
            parse_volcano_translation_response(&body, text, service_id, service_name)
        }
        TraditionalHttpServiceConfig::Linguee => {
            parse_linguee_translation_response(&body, text, service_id, service_name)
        }
        TraditionalHttpServiceConfig::YoudaoOpenApi { .. } => {
            parse_youdao_openapi_response(&body, text, from_language, service_id, service_name)
        }
        TraditionalHttpServiceConfig::YoudaoWebDict => {
            parse_youdao_web_dict_response(&body, text, from_language, service_id, service_name)
        }
        TraditionalHttpServiceConfig::YoudaoWebTranslate => parse_youdao_web_translate_response(
            &body,
            text,
            from_language,
            service_id,
            service_name,
        ),
    }
}

pub fn translate_deepl_web_service<C: TraditionalHttpClient>(
    client: &mut C,
    fallback_api_key: Option<&str>,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let web_result = (|| {
        let plan = build_deepl_web_translation_request_plan(text, from_language, to_language)
            .map_err(|error| attach_service_id(error, &service_id))?;
        let body = client
            .execute(&plan)
            .map_err(|error| attach_service_id(error, &service_id))?;
        parse_deepl_web_translation_response(&body, service_id.clone(), service_name.clone())
    })();

    match web_result {
        Ok(result) => Ok(result),
        Err(web_error) => {
            let Some(api_key) = normalized_optional(fallback_api_key) else {
                return Err(web_error);
            };
            let api_config = TraditionalHttpServiceConfig::DeepLApi {
                api_key,
                use_quality_optimized: false,
            };
            let plan = build_traditional_http_translation_request_plan(
                &api_config,
                text,
                from_language,
                to_language,
            )
            .map_err(|error| attach_service_id(error, &service_id))?;
            let body = client
                .execute(&plan)
                .map_err(|error| attach_service_id(error, &service_id))?;
            parse_deepl_api_translation_response(&body, service_id, service_name)
        }
    }
}

pub fn translate_youdao_web_dict_service<C: TraditionalHttpClient>(
    client: &mut C,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let plan = build_youdao_web_dict_translation_request_plan(text, from_language, to_language)
        .map_err(|error| attach_service_id(error, &service_id))?;
    let body = client
        .execute(&plan)
        .map_err(|error| attach_service_id(error, &service_id))?;
    let result = parse_youdao_web_dict_response(
        &body,
        text,
        from_language,
        service_id.clone(),
        service_name.clone(),
    )?;

    if youdao_web_dict_result_is_meaningful(&result, text) {
        return Ok(result);
    }

    translate_youdao_web_translate_service(
        client,
        text,
        from_language,
        to_language,
        service_id,
        service_name,
    )
}

pub fn translate_youdao_web_translate_service<C: TraditionalHttpClient>(
    client: &mut C,
    text: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    validate_traditional_http_language_pair(
        TraditionalHttpServiceKind::YoudaoWebTranslate,
        from_language,
        to_language,
    )
    .map_err(|error| attach_service_id(error, &service_id))?;

    let key_plan = build_youdao_web_translate_key_request_plan()
        .map_err(|error| attach_service_id(error, &service_id))?;
    let key_body = client
        .execute(&key_plan)
        .map_err(|error| attach_service_id(error, &service_id))?;
    let sign_key = parse_youdao_web_translate_key_response(&key_body, &service_id)?;

    let translate_plan =
        build_youdao_web_translate_request_plan(&sign_key, text, from_language, to_language)
            .map_err(|error| attach_service_id(error, &service_id))?;
    let response_text = client
        .execute(&translate_plan)
        .map_err(|error| attach_service_id(error, &service_id))?;
    let json = if response_text.trim_start().starts_with('{') {
        response_text
    } else {
        decrypt_youdao_web_translate_response(&response_text)
            .map_err(|error| attach_service_id(error, &service_id))?
    };

    parse_youdao_web_translate_response(&json, text, from_language, service_id, service_name)
}

fn youdao_web_dict_result_is_meaningful(
    result: &TranslationResultDto,
    original_text: &str,
) -> bool {
    let has_definitions = result
        .word_result
        .as_ref()
        .and_then(|word| word.definitions.as_ref())
        .is_some_and(|definitions| !definitions.is_empty());
    let has_translation = result.translated_text != original_text;
    has_definitions || has_translation
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
        alternatives: None,
        word_result: None,
        raw_html: None,
    })
}

pub fn parse_google_web_translation_response(
    json: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Google WebApp JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    if !root.is_array() {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            "Unexpected response format from Google WebApp API",
        )
        .with_service_id(service_id));
    }

    let translated_text = google_web_translated_text(&root);
    let detected_language = google_web_detected_language(&root);
    let phonetic = google_web_phonetic(&root);
    let definitions = google_web_definitions(&root);
    let examples = google_web_examples(&root);
    let word_result =
        (phonetic.is_some() || definitions.is_some() || examples.is_some()).then(|| {
            WordResultDto {
                phonetics: phonetic.map(|value| vec![value]),
                definitions,
                examples,
                word_forms: None,
                synonyms: None,
            }
        });

    Ok(TranslationResultDto {
        translated_text,
        service_id: Some(service_id),
        service_name: Some(service_name),
        detected_language,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: None,
        word_result,
        raw_html: None,
    })
}

fn google_web_translated_text(root: &Value) -> String {
    root.get(0)
        .and_then(Value::as_array)
        .map(|sentences| {
            sentences
                .iter()
                .filter_map(|sentence| sentence.as_array())
                .filter_map(|sentence| sentence.first())
                .filter_map(Value::as_str)
                .filter(|part| !part.is_empty())
                .collect::<String>()
        })
        .unwrap_or_default()
}

fn google_web_detected_language(root: &Value) -> Option<String> {
    root.get(8)
        .and_then(Value::as_array)
        .and_then(|items| items.last())
        .and_then(Value::as_array)
        .and_then(|item| item.first())
        .and_then(Value::as_str)
        .or_else(|| root.get(2).and_then(Value::as_str))
        .map(|code| TranslationLanguage::from_code(code).to_code().to_string())
}

fn google_web_phonetic(root: &Value) -> Option<PhoneticDto> {
    let text = root
        .get(0)
        .and_then(Value::as_array)
        .and_then(|sentences| sentences.last())
        .and_then(Value::as_array)
        .and_then(|sentence| sentence.get(3))
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())?;

    Some(PhoneticDto {
        text: Some(text.to_string()),
        audio_url: None,
        accent: Some("src".to_string()),
    })
}

fn google_web_definitions(root: &Value) -> Option<Vec<DefinitionDto>> {
    let definitions = root.get(1)?.as_array()?;
    let values: Vec<DefinitionDto> = definitions
        .iter()
        .filter_map(|entry| {
            let entry = entry.as_array()?;
            let part_of_speech = entry
                .first()
                .and_then(Value::as_str)
                .filter(|value| !value.is_empty())
                .map(str::to_string);
            let mut meanings: Vec<String> = entry
                .get(1)
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(Value::as_str)
                .filter(|meaning| !meaning.is_empty())
                .map(str::to_string)
                .collect();

            if meanings.is_empty() {
                meanings = entry
                    .get(2)
                    .and_then(Value::as_array)
                    .into_iter()
                    .flatten()
                    .filter_map(Value::as_array)
                    .filter_map(|simple_word| simple_word.first())
                    .filter_map(Value::as_str)
                    .filter(|meaning| !meaning.is_empty())
                    .map(str::to_string)
                    .collect();
            }

            (!meanings.is_empty()).then_some(DefinitionDto {
                part_of_speech,
                meanings: Some(meanings),
            })
        })
        .collect();

    (!values.is_empty()).then_some(values)
}

fn google_web_examples(root: &Value) -> Option<Vec<String>> {
    let values: Vec<String> = root
        .get(13)?
        .get(0)?
        .as_array()?
        .iter()
        .filter_map(Value::as_array)
        .filter_map(|example| example.first())
        .filter_map(Value::as_str)
        .map(strip_html_tags)
        .filter(|example| !example.is_empty())
        .collect();

    (!values.is_empty()).then_some(values)
}

fn strip_html_tags(value: &str) -> String {
    let mut result = String::with_capacity(value.len());
    let mut in_tag = false;
    for character in value.chars() {
        match character {
            '<' => in_tag = true,
            '>' => in_tag = false,
            _ if !in_tag => result.push(character),
            _ => {}
        }
    }
    result
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

pub fn parse_deepl_web_translation_response(
    json: &str,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Failed to parse DeepL web response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    if let Some(error) = root.get("error") {
        let message = error
            .get("message")
            .and_then(Value::as_str)
            .unwrap_or("Unknown error");
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::ServiceUnavailable,
            format!("DeepL web error: {message}"),
        )
        .with_service_id(service_id));
    }

    let Some(result) = root.get("result") else {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            "Invalid response from DeepL web",
        )
        .with_service_id(service_id));
    };
    let Some(first_text) = result
        .get("texts")
        .and_then(Value::as_array)
        .and_then(|texts| texts.first())
    else {
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            "No translation result from DeepL web",
        )
        .with_service_id(service_id));
    };

    let translated_text = first_text
        .get("text")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string();
    let detected_language = result
        .get("lang")
        .and_then(Value::as_str)
        .map(|code| {
            TranslationLanguage::from_iso639(code)
                .to_iso639()
                .to_string()
        })
        .filter(|code| code != "auto");

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

pub fn parse_youdao_openapi_response(
    json: &str,
    original_text: &str,
    from_language: TranslationLanguage,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Youdao OpenAPI JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    if let Some(code) = root.get("errorCode").and_then(Value::as_str) {
        if code != "0" {
            return Err(youdao_openapi_error_from_code(code).with_service_id(service_id));
        }
    }

    let translated_text = root
        .get("translation")
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_str)
                .filter(|text| !text.is_empty())
                .collect::<Vec<_>>()
                .join(" ")
        })
        .filter(|text| !text.is_empty())
        .unwrap_or_else(|| original_text.to_string());

    let phonetics = root.get("basic").and_then(youdao_openapi_phonetics);
    let definitions = root.get("basic").and_then(youdao_openapi_definitions);
    let word_result = (phonetics.is_some() || definitions.is_some()).then(|| WordResultDto {
        phonetics,
        definitions,
        examples: None,
        word_forms: None,
        synonyms: None,
    });
    let detected_language = if from_language == TranslationLanguage::Auto {
        root.get("l")
            .and_then(Value::as_str)
            .and_then(|pair| pair.split_once('2').map(|(from, _)| from))
            .map(youdao_language_from_code)
            .map(|language| language.to_code().to_string())
    } else {
        Some(from_language.to_code().to_string())
    };

    Ok(TranslationResultDto {
        translated_text,
        service_id: Some(service_id),
        service_name: Some(service_name),
        detected_language,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: None,
        word_result,
        raw_html: None,
    })
}

fn youdao_openapi_phonetics(basic: &Value) -> Option<Vec<PhoneticDto>> {
    let mut phonetics = Vec::new();
    if let Some(text) = basic
        .get("us-phonetic")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
    {
        phonetics.push(PhoneticDto {
            text: Some(text.to_string()),
            audio_url: basic
                .get("us-speech")
                .and_then(Value::as_str)
                .filter(|value| !value.is_empty())
                .map(str::to_string),
            accent: Some("US".to_string()),
        });
    }

    if let Some(text) = basic
        .get("uk-phonetic")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
    {
        phonetics.push(PhoneticDto {
            text: Some(text.to_string()),
            audio_url: basic
                .get("uk-speech")
                .and_then(Value::as_str)
                .filter(|value| !value.is_empty())
                .map(str::to_string),
            accent: Some("UK".to_string()),
        });
    }

    (!phonetics.is_empty()).then_some(phonetics)
}

fn youdao_openapi_definitions(basic: &Value) -> Option<Vec<DefinitionDto>> {
    let definitions: Vec<DefinitionDto> = basic
        .get("explains")?
        .as_array()?
        .iter()
        .filter_map(Value::as_str)
        .filter(|text| !text.is_empty())
        .map(|text| {
            let (part_of_speech, meaning) = text
                .split_once(". ")
                .filter(|(part, _)| part.len() <= 10)
                .map_or((None, text), |(part, meaning)| {
                    (Some(part.to_string()), meaning)
                });

            DefinitionDto {
                part_of_speech,
                meanings: Some(vec![meaning.to_string()]),
            }
        })
        .collect();

    (!definitions.is_empty()).then_some(definitions)
}

pub fn parse_youdao_web_dict_response(
    json: &str,
    original_text: &str,
    from_language: TranslationLanguage,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Youdao web dictionary JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    let phonetics = youdao_web_dict_word(&root, "simple")
        .and_then(youdao_web_dict_phonetics)
        .or_else(|| youdao_web_dict_word(&root, "ec").and_then(youdao_web_dict_phonetics));
    let definitions = youdao_web_dict_definitions(&root);
    let word_forms = youdao_web_dict_word_forms(&root);
    let synonyms = youdao_web_dict_synonyms(&root);
    let translated_text = youdao_web_dict_translated_text(original_text, definitions.as_deref());
    let detected_language = Some(
        if from_language == TranslationLanguage::Auto {
            TranslationLanguage::English
        } else {
            from_language
        }
        .to_code()
        .to_string(),
    );

    let has_word_result = option_vec_has_items(&phonetics)
        || option_vec_has_items(&definitions)
        || option_vec_has_items(&word_forms)
        || option_vec_has_items(&synonyms);
    let word_result = has_word_result.then(|| WordResultDto {
        phonetics,
        definitions,
        examples: None,
        word_forms,
        synonyms,
    });

    Ok(TranslationResultDto {
        translated_text,
        service_id: Some(service_id),
        service_name: Some(service_name),
        detected_language,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: None,
        word_result,
        raw_html: None,
    })
}

fn youdao_web_dict_word<'a>(root: &'a Value, section: &str) -> Option<&'a Value> {
    let word = root.get(section)?.get("word")?;
    first_json_value(word)
}

fn first_json_value(value: &Value) -> Option<&Value> {
    value.as_array().map_or(Some(value), |items| items.first())
}

fn youdao_web_dict_phonetics(word: &Value) -> Option<Vec<PhoneticDto>> {
    let mut phonetics = Vec::new();
    if let Some(text) = non_empty_string_field(word, "usphone") {
        phonetics.push(PhoneticDto {
            text: Some(text),
            audio_url: non_empty_string_field(word, "usspeech")
                .and_then(|path| youdao_dict_voice_audio_url(&path)),
            accent: Some("US".to_string()),
        });
    }

    if let Some(text) = non_empty_string_field(word, "ukphone") {
        phonetics.push(PhoneticDto {
            text: Some(text),
            audio_url: non_empty_string_field(word, "ukspeech")
                .and_then(|path| youdao_dict_voice_audio_url(&path)),
            accent: Some("UK".to_string()),
        });
    }

    (!phonetics.is_empty()).then_some(phonetics)
}

fn youdao_web_dict_definitions(root: &Value) -> Option<Vec<DefinitionDto>> {
    let definitions: Vec<DefinitionDto> = youdao_web_dict_word(root, "ec")?
        .get("trs")?
        .as_array()?
        .iter()
        .filter_map(|item| {
            let meaning = non_empty_string_field(item, "tran")?;
            Some(DefinitionDto {
                part_of_speech: non_empty_string_field(item, "pos"),
                meanings: Some(vec![meaning]),
            })
        })
        .collect();

    (!definitions.is_empty()).then_some(definitions)
}

fn youdao_web_dict_word_forms(root: &Value) -> Option<Vec<WordFormDto>> {
    let mut word_forms = Vec::new();
    let Some(wfs) = youdao_web_dict_word(root, "ec")
        .and_then(|word| word.get("wfs"))
        .and_then(Value::as_array)
    else {
        return None;
    };

    for item in wfs {
        let Some(wf) = item.get("wf") else {
            continue;
        };
        let name = non_empty_string_field(wf, "name");
        let Some(value) = non_empty_string_field(wf, "value") else {
            continue;
        };

        for form in value.split('\u{6216}') {
            let form = form.trim();
            if !form.is_empty() {
                word_forms.push(WordFormDto {
                    name: name.clone(),
                    value: Some(form.to_string()),
                });
            }
        }
    }

    (!word_forms.is_empty()).then_some(word_forms)
}

fn youdao_web_dict_synonyms(root: &Value) -> Option<Vec<SynonymDto>> {
    let mut synonyms = Vec::new();
    let Some(synos) = root
        .get("syno")
        .and_then(|syno| syno.get("synos"))
        .and_then(Value::as_array)
    else {
        return None;
    };

    for item in synos {
        let words = item
            .get("ws")
            .and_then(Value::as_array)
            .map(|items| {
                items
                    .iter()
                    .filter_map(youdao_web_dict_synonym_word)
                    .collect::<Vec<_>>()
            })
            .filter(|words| !words.is_empty());

        if let Some(words) = words {
            synonyms.push(SynonymDto {
                part_of_speech: non_empty_string_field(item, "pos"),
                meaning: non_empty_string_field(item, "tran"),
                words: Some(words),
            });
        }
    }

    (!synonyms.is_empty()).then_some(synonyms)
}

fn youdao_web_dict_synonym_word(value: &Value) -> Option<String> {
    value
        .as_str()
        .filter(|word| !word.is_empty())
        .map(str::to_string)
        .or_else(|| non_empty_string_field(value, "w"))
}

fn youdao_web_dict_translated_text(
    original_text: &str,
    definitions: Option<&[DefinitionDto]>,
) -> String {
    let Some(definitions) = definitions.filter(|items| !items.is_empty()) else {
        return original_text.to_string();
    };

    definitions
        .iter()
        .take(3)
        .filter_map(|definition| {
            let meanings = definition.meanings.as_ref()?;
            let text = meanings.join("; ");
            if text.is_empty() {
                return None;
            }

            Some(match definition.part_of_speech.as_deref() {
                Some(part) if !part.is_empty() => format!("{part} {text}"),
                _ => text,
            })
        })
        .collect::<Vec<_>>()
        .join("\n")
}

fn youdao_dict_voice_audio_url(audio_path: &str) -> Option<String> {
    let mut url = reqwest::Url::parse(YOUDAO_DICT_VOICE_ENDPOINT).ok()?;
    url.query_pairs_mut().append_pair("audio", audio_path);
    Some(url.to_string())
}

fn non_empty_string_field(value: &Value, field: &str) -> Option<String> {
    value
        .get(field)
        .and_then(Value::as_str)
        .filter(|text| !text.is_empty())
        .map(str::to_string)
}

fn option_vec_has_items<T>(value: &Option<Vec<T>>) -> bool {
    value.as_ref().is_some_and(|items| !items.is_empty())
}

pub fn parse_youdao_web_translate_key_response(
    json: &str,
    service_id: &str,
) -> Result<String, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Youdao webtranslate key JSON response: {error}"),
        )
        .with_service_id(service_id.to_string())
    })?;

    let code = json_code_as_i64(root.get("code")).unwrap_or(-1);
    if code != 0 {
        let message = root
            .get("msg")
            .and_then(Value::as_str)
            .unwrap_or("Unknown error");
        return Err(OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::ServiceUnavailable,
            format!("Youdao key API error: {message}"),
        )
        .with_service_id(service_id.to_string()));
    }

    root.get("data")
        .and_then(|data| data.get("secretKey"))
        .and_then(Value::as_str)
        .filter(|key| !key.is_empty())
        .map(str::to_string)
        .ok_or_else(|| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::ServiceUnavailable,
                "Invalid Youdao key API response",
            )
            .with_service_id(service_id.to_string())
        })
}

pub fn parse_youdao_web_translate_response(
    json: &str,
    _original_text: &str,
    from_language: TranslationLanguage,
    service_id: String,
    service_name: String,
) -> Result<TranslationResultDto, OpenAiExecutionError> {
    let root: Value = serde_json::from_str(json).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Invalid Youdao webtranslate JSON response: {error}"),
        )
        .with_service_id(service_id.clone())
    })?;

    if let Some(code) = json_code_as_i64(root.get("code")) {
        if code != 0 {
            return Err(youdao_web_translate_error_from_code(code).with_service_id(service_id));
        }
    }

    let translated_text = youdao_web_translate_text(&root).ok_or_else(|| {
        let mut preview = json.to_string();
        if preview.len() > 500 {
            preview.truncate(500);
            preview.push_str("...");
        }
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::ServiceUnavailable,
            format!("Youdao web translate returned no result. Response: {preview}"),
        )
        .with_service_id(service_id.clone())
    })?;

    Ok(TranslationResultDto {
        translated_text,
        service_id: Some(service_id),
        service_name: Some(service_name),
        detected_language: Some(from_language.to_code().to_string()),
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: None,
        word_result: None,
        raw_html: None,
    })
}

fn youdao_web_translate_text(root: &Value) -> Option<String> {
    let mut translated_text = String::new();
    for item in root.get("translateResult")?.as_array()? {
        if let Some(segments) = item.as_array() {
            for segment in segments {
                if let Some(text) = non_empty_string_field(segment, "tgt") {
                    translated_text.push_str(&text);
                }
            }
        } else if item.is_object() {
            if let Some(text) = non_empty_string_field(item, "tgt") {
                translated_text.push_str(&text);
            }
        }
    }

    (!translated_text.is_empty()).then_some(translated_text)
}

fn json_code_as_i64(value: Option<&Value>) -> Option<i64> {
    value.and_then(|value| {
        value
            .as_i64()
            .or_else(|| value.as_str().and_then(|text| text.parse().ok()))
    })
}

pub fn decrypt_youdao_web_translate_response(
    encrypted_text: &str,
) -> Result<String, OpenAiExecutionError> {
    let key = md5_bytes(YOUDAO_WEB_AES_KEY_SOURCE);
    let iv = md5_bytes(YOUDAO_WEB_AES_IV_SOURCE);
    let mut base64_text = encrypted_text.trim().replace('-', "+").replace('_', "/");
    let padding = base64_text.len() % 4;
    if padding > 0 {
        base64_text.push_str(&"=".repeat(4 - padding));
    }

    let encrypted_bytes = general_purpose::STANDARD
        .decode(base64_text.as_bytes())
        .map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::ServiceUnavailable,
                format!("Failed to decode Youdao encrypted response: {error}"),
            )
        })?;
    let decrypted = Aes128CbcDec::new(&key.into(), &iv.into())
        .decrypt_padded_vec_mut::<Pkcs7>(&encrypted_bytes)
        .map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::ServiceUnavailable,
                format!("Failed to decrypt Youdao response: {error}"),
            )
        })?;

    String::from_utf8(decrypted).map_err(|error| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::InvalidResponse,
            format!("Decrypted Youdao response is not UTF-8: {error}"),
        )
    })
}

pub fn youdao_web_translate_error_from_code(code: i64) -> OpenAiExecutionError {
    let kind = match code {
        50 => OpenAiExecutionErrorCode::RateLimited,
        _ => OpenAiExecutionErrorCode::ServiceUnavailable,
    };
    OpenAiExecutionError::new(kind, format!("Youdao web translate error: {code}"))
}

pub fn youdao_openapi_error_from_code(code: &str) -> OpenAiExecutionError {
    let kind = match code {
        "401" | "108" => OpenAiExecutionErrorCode::InvalidApiKey,
        "411" => OpenAiExecutionErrorCode::RateLimited,
        _ => OpenAiExecutionErrorCode::ServiceUnavailable,
    };
    OpenAiExecutionError::new(kind, format!("Youdao API error: {code}"))
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

pub fn youdao_language_code(language: TranslationLanguage) -> &'static str {
    match language {
        TranslationLanguage::Auto => "auto",
        TranslationLanguage::SimplifiedChinese => "zh-CHS",
        TranslationLanguage::TraditionalChinese => "zh-CHT",
        TranslationLanguage::English => "en",
        TranslationLanguage::Japanese => "ja",
        TranslationLanguage::Korean => "ko",
        TranslationLanguage::French => "fr",
        TranslationLanguage::Spanish => "es",
        TranslationLanguage::Portuguese => "pt",
        TranslationLanguage::Italian => "it",
        TranslationLanguage::German => "de",
        TranslationLanguage::Russian => "ru",
        TranslationLanguage::Arabic => "ar",
        TranslationLanguage::Swedish => "sv",
        TranslationLanguage::Thai => "th",
        TranslationLanguage::Dutch => "nl",
        TranslationLanguage::Indonesian => "id",
        TranslationLanguage::Vietnamese => "vi",
        TranslationLanguage::Hindi => "hi",
        _ => "en",
    }
}

pub fn youdao_web_dict_language_code(
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
) -> &'static str {
    let target_language = match from_language {
        TranslationLanguage::SimplifiedChinese | TranslationLanguage::TraditionalChinese => {
            to_language
        }
        _ => from_language,
    };

    match target_language {
        TranslationLanguage::English => "en",
        TranslationLanguage::Japanese => "ja",
        TranslationLanguage::French => "fr",
        TranslationLanguage::Korean => "ko",
        _ => "en",
    }
}

fn youdao_language_from_code(code: &str) -> TranslationLanguage {
    match code {
        "zh-CHS" => TranslationLanguage::SimplifiedChinese,
        "zh-CHT" => TranslationLanguage::TraditionalChinese,
        "en" => TranslationLanguage::English,
        "ja" => TranslationLanguage::Japanese,
        "ko" => TranslationLanguage::Korean,
        "fr" => TranslationLanguage::French,
        "es" => TranslationLanguage::Spanish,
        "pt" => TranslationLanguage::Portuguese,
        "it" => TranslationLanguage::Italian,
        "de" => TranslationLanguage::German,
        "ru" => TranslationLanguage::Russian,
        "ar" => TranslationLanguage::Arabic,
        "sv" => TranslationLanguage::Swedish,
        "th" => TranslationLanguage::Thai,
        "nl" => TranslationLanguage::Dutch,
        "id" => TranslationLanguage::Indonesian,
        "vi" => TranslationLanguage::Vietnamese,
        "hi" => TranslationLanguage::Hindi,
        _ => TranslationLanguage::Auto,
    }
}

pub fn youdao_openapi_signature_input(text: &str) -> String {
    let chars: Vec<char> = text.chars().collect();
    if chars.len() <= 20 {
        return text.to_string();
    }

    let prefix: String = chars.iter().take(10).collect();
    let suffix: String = chars.iter().skip(chars.len() - 10).collect();
    format!("{}{}{}", prefix, chars.len(), suffix)
}

pub fn compute_youdao_openapi_sign(
    app_key: &str,
    input: &str,
    salt: &str,
    curtime: &str,
    app_secret: &str,
) -> String {
    sha256_hex(format!("{app_key}{input}{salt}{curtime}{app_secret}").as_bytes())
}

pub fn youdao_web_dict_time(text: &str) -> usize {
    format!("{text}webdict").encode_utf16().count() % 10
}

pub fn compute_youdao_web_dict_sign(text: &str, time: usize, salt: &str) -> String {
    const KEY: &str = "Mk6hqtUp33DGGtoS63tTJbMUYjRrG1Lu";
    md5_hex(&format!("web{text}{time}{KEY}{salt}"))
}

pub fn compute_youdao_web_translate_sign(sign_key: &str, mystic_time: &str) -> String {
    md5_hex(&format!(
        "client=fanyideskweb&mysticTime={mystic_time}&product=webfanyi&key={sign_key}"
    ))
}

pub fn deepl_i_count(text: &str) -> i64 {
    text.chars().filter(|character| *character == 'i').count() as i64
}

pub fn deepl_aligned_timestamp(timestamp_millis: i64, i_count: i64) -> i64 {
    if i_count <= 0 {
        return timestamp_millis;
    }

    let count = i_count + 1;
    timestamp_millis - (timestamp_millis % count) + count
}

pub fn apply_deepl_dynamic_spacing(json: &str, request_id: i64) -> String {
    if (request_id + 5) % 29 == 0 || (request_id + 3) % 13 == 0 {
        json.replace("\"method\":\"", "\"method\" : \"")
    } else {
        json.replace("\"method\":\"", "\"method\": \"")
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

pub fn deepl_web_error_from_status(status_code: u16, reason: &str) -> OpenAiExecutionError {
    OpenAiExecutionError::new(
        OpenAiExecutionErrorCode::ServiceUnavailable,
        format!("DeepL web translation failed ({status_code}): {reason}"),
    )
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

fn current_unix_time_millis_string() -> Result<String, OpenAiExecutionError> {
    Ok(current_unix_time_millis()?.to_string())
}

fn current_unix_time_millis() -> Result<i64, OpenAiExecutionError> {
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| {
            OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::Unknown,
                format!("Could not compute request timestamp: {error}"),
            )
        })?
        .as_millis();
    i64::try_from(millis).map_err(|_| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::Unknown,
            "Current request timestamp does not fit in i64",
        )
    })
}

fn youdao_web_headers(include_content_type: bool) -> Vec<(String, String)> {
    let mut headers = Vec::new();
    if include_content_type {
        headers.push((
            "Content-Type".to_string(),
            "application/x-www-form-urlencoded".to_string(),
        ));
    }
    headers.extend([
        ("User-Agent".to_string(), YOUDAO_WEB_USER_AGENT.to_string()),
        (
            "Referer".to_string(),
            "https://fanyi.youdao.com/".to_string(),
        ),
        ("Origin".to_string(), "https://fanyi.youdao.com".to_string()),
    ]);
    if include_content_type {
        headers.push((
            "Cookie".to_string(),
            "OUTFOX_SEARCH_USER_ID=0@0.0.0.0".to_string(),
        ));
    }
    headers
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

fn extract_html_attribute_value(html: &str, attribute_name: &str) -> Option<String> {
    let lower_html = html.to_ascii_lowercase();
    let lower_attribute = attribute_name.to_ascii_lowercase();
    let mut cursor = 0;

    while let Some(relative_index) = lower_html[cursor..].find(&lower_attribute) {
        let start = cursor + relative_index;
        let after_name = start + lower_attribute.len();
        cursor = after_name;

        if html[..start]
            .chars()
            .next_back()
            .is_some_and(is_html_attribute_name_character)
        {
            continue;
        }
        if html[after_name..]
            .chars()
            .next()
            .is_some_and(is_html_attribute_name_character)
        {
            continue;
        }

        let after_name = trim_ascii_start(&html[after_name..]);
        let Some(after_equals) = after_name.strip_prefix('=') else {
            continue;
        };
        let after_equals = trim_ascii_start(after_equals);
        if let Some((value, _)) = parse_html_attribute_value(after_equals) {
            return Some(value);
        }
    }

    None
}

fn parse_html_attribute_value(input: &str) -> Option<(String, &str)> {
    let input = trim_ascii_start(input);
    let quote = input.chars().next()?;
    if quote == '"' || quote == '\'' {
        let after_quote = &input[quote.len_utf8()..];
        let end = after_quote.find(quote)?;
        return Some((
            after_quote[..end].to_string(),
            &after_quote[end + quote.len_utf8()..],
        ));
    }

    let end = input
        .char_indices()
        .find(|(_, ch)| ch.is_ascii_whitespace() || *ch == '>')
        .map(|(index, _)| index)
        .unwrap_or(input.len());
    (end > 0).then(|| (input[..end].to_string(), &input[end..]))
}

fn is_html_attribute_name_character(ch: char) -> bool {
    ch.is_ascii_alphanumeric() || matches!(ch, '-' | '_' | ':')
}

fn extract_js_property_string_value(source: &str, property_name: &str) -> Option<String> {
    let mut cursor = 0;
    while let Some(relative_index) = source[cursor..].find(property_name) {
        let start = cursor + relative_index;
        let bare_name_end = start + property_name.len();
        cursor = bare_name_end;

        let Some(name_end) = js_property_name_end(source, start, property_name) else {
            continue;
        };
        let after_name = trim_ascii_start(&source[name_end..]);
        let Some(after_colon) = after_name.strip_prefix(':') else {
            continue;
        };
        let after_colon = trim_ascii_start(after_colon);
        if let Some((value, _)) = parse_js_string_literal(after_colon) {
            return Some(value);
        }
    }

    None
}

fn js_property_name_end(source: &str, start: usize, property_name: &str) -> Option<usize> {
    let bare_name_end = start + property_name.len();
    let previous = source[..start].chars().next_back();
    let next = source[bare_name_end..].chars().next();

    if previous.is_some_and(|ch| ch == '"' || ch == '\'') && next == previous {
        return Some(bare_name_end + next?.len_utf8());
    }

    if previous.is_some_and(is_js_identifier_character)
        || next.is_some_and(is_js_identifier_character)
    {
        return None;
    }
    Some(bare_name_end)
}

fn is_js_identifier_character(ch: char) -> bool {
    ch.is_ascii_alphanumeric() || matches!(ch, '_' | '$')
}

/// Parse `params_AbusePreventionHelper = [key,"token",expiry]` into its parts,
/// mirroring the legacy regex without pulling in a regex dependency.
fn parse_bing_abuse_prevention_params(html: &str) -> Option<(i64, String, i64)> {
    const MARKER: &str = "params_AbusePreventionHelper";

    let mut cursor = 0;
    while let Some(relative_index) = html[cursor..].find(MARKER) {
        let marker = cursor + relative_index;
        let after_marker_index = marker + MARKER.len();
        cursor = after_marker_index;

        if html[..marker]
            .chars()
            .next_back()
            .is_some_and(is_js_identifier_character)
            || html[after_marker_index..]
                .chars()
                .next()
                .is_some_and(is_js_identifier_character)
        {
            continue;
        }

        let after_marker = &html[after_marker_index..];
        let Some(open) = after_marker.find('[') else {
            continue;
        };
        if after_marker[..open].contains(MARKER) {
            continue;
        }

        let after_open = &after_marker[open + 1..];
        if let Some(params) = parse_bing_abuse_prevention_array(after_open) {
            return Some(params);
        }
    }

    None
}

fn parse_bing_abuse_prevention_array(after_open: &str) -> Option<(i64, String, i64)> {
    let (key, after_key) = parse_js_i64(after_open)?;
    let after_comma = consume_js_comma(after_key)?;
    let (token, after_token) = parse_js_string_literal(after_comma)?;
    let after_comma = consume_js_comma(after_token)?;
    let (expiry, _) = parse_js_i64(after_comma)?;
    Some((key, token, expiry))
}

fn parse_js_i64(input: &str) -> Option<(i64, &str)> {
    let input = trim_ascii_start(input);
    let mut end = 0;
    for (index, ch) in input.char_indices() {
        if index == 0 && ch == '-' {
            end = ch.len_utf8();
            continue;
        }
        if ch.is_ascii_digit() {
            end = index + ch.len_utf8();
            continue;
        }
        break;
    }

    let value = input[..end].parse().ok()?;
    Some((value, &input[end..]))
}

fn consume_js_comma(input: &str) -> Option<&str> {
    trim_ascii_start(input)
        .strip_prefix(',')
        .map(trim_ascii_start)
}

fn parse_js_string_literal(input: &str) -> Option<(String, &str)> {
    let input = trim_ascii_start(input);
    let quote = input.chars().next()?;
    if quote != '"' && quote != '\'' {
        return None;
    }

    let mut output = String::new();
    let mut index = quote.len_utf8();
    while index < input.len() {
        let ch = input[index..].chars().next()?;
        if ch == quote {
            let rest = &input[index + ch.len_utf8()..];
            return Some((output, rest));
        }
        if ch != '\\' {
            output.push(ch);
            index += ch.len_utf8();
            continue;
        }

        index += ch.len_utf8();
        let escaped = input[index..].chars().next()?;
        match escaped {
            'b' => {
                output.push('\u{0008}');
                index += escaped.len_utf8();
            }
            'f' => {
                output.push('\u{000c}');
                index += escaped.len_utf8();
            }
            'n' => {
                output.push('\n');
                index += escaped.len_utf8();
            }
            'r' => {
                output.push('\r');
                index += escaped.len_utf8();
            }
            't' => {
                output.push('\t');
                index += escaped.len_utf8();
            }
            'u' => {
                let hex_start = index + escaped.len_utf8();
                let hex_end = hex_start + 4;
                let hex = input.get(hex_start..hex_end)?;
                let scalar = u32::from_str_radix(hex, 16).ok()?;
                output.push(char::from_u32(scalar)?);
                index = hex_end;
            }
            other => {
                output.push(other);
                index += other.len_utf8();
            }
        }
    }

    None
}

fn trim_ascii_start(input: &str) -> &str {
    input.trim_start_matches(|ch: char| ch.is_ascii_whitespace())
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
        alternatives: None,
        word_result: None,
        raw_html: None,
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

fn new_deepl_web_request_id() -> Result<i64, OpenAiExecutionError> {
    let rng = SystemRandom::new();
    let mut bytes = [0_u8; 4];
    rng.fill(&mut bytes).map_err(|_| {
        OpenAiExecutionError::new(
            OpenAiExecutionErrorCode::Unknown,
            "Could not generate DeepL web request id",
        )
    })?;

    let value = u32::from_le_bytes(bytes);
    Ok((100_000 + (value % 89_999)) as i64 * 1_000)
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

fn md5_hex(data: &str) -> String {
    hex_encode_lower(&Md5::digest(data.as_bytes()))
}

fn md5_bytes(data: &str) -> [u8; 16] {
    let digest = Md5::digest(data.as_bytes());
    let mut bytes = [0u8; 16];
    bytes.copy_from_slice(&digest);
    bytes
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
