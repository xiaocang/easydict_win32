use std::fmt;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use crate::compat_protocol::SettingsSnapshot;

const DEFAULT_MAX_RETRIES: usize = 3;
const DEFAULT_RETRY_DELAYS: [Duration; 3] = [
    Duration::from_secs(2),
    Duration::from_secs(4),
    Duration::from_secs(8),
];
const SOURCE_PROBE_TIMEOUT: Duration = Duration::from_secs(5);
const DOWNLOAD_TIMEOUT: Duration = Duration::from_secs(600);
const USER_AGENT: &str = "Easydict-Win32/1.0";

#[derive(Clone, Debug, PartialEq)]
pub struct ResourceDownloadProgress {
    pub stage: String,
    pub bytes_downloaded: u64,
    pub total_bytes: i64,
    pub percentage: f64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ResourceProbeResult {
    pub ok: bool,
    pub elapsed_ms: u128,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ResourceDownloadRetryPolicy {
    pub max_retries: usize,
    pub retry_delays: Vec<Duration>,
}

impl Default for ResourceDownloadRetryPolicy {
    fn default() -> Self {
        Self {
            max_retries: DEFAULT_MAX_RETRIES,
            retry_delays: DEFAULT_RETRY_DELAYS.to_vec(),
        }
    }
}

#[derive(Debug)]
pub enum ResourceDownloadError {
    EmptyUrlList,
    InvalidProxyUri {
        uri: String,
        message: String,
    },
    Network(String),
    Io(String),
    AllSourcesFailed {
        stage: String,
        source_count: usize,
        last_error: Box<ResourceDownloadError>,
    },
}

impl ResourceDownloadError {
    fn io(error: impl ToString) -> Self {
        Self::Io(error.to_string())
    }

    fn network(error: impl ToString) -> Self {
        Self::Network(error.to_string())
    }
}

impl fmt::Display for ResourceDownloadError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::EmptyUrlList => write!(formatter, "No download URLs were provided"),
            Self::InvalidProxyUri { uri, message } => {
                write!(
                    formatter,
                    "Invalid resource download proxy URI '{uri}': {message}"
                )
            }
            Self::Network(message) => write!(formatter, "Resource download failed: {message}"),
            Self::Io(message) => write!(formatter, "Resource download file error: {message}"),
            Self::AllSourcesFailed {
                stage,
                source_count,
                last_error,
            } => write!(
                formatter,
                "Failed to download {stage} from {source_count} source(s): {last_error}"
            ),
        }
    }
}

impl std::error::Error for ResourceDownloadError {}

impl From<std::io::Error> for ResourceDownloadError {
    fn from(value: std::io::Error) -> Self {
        Self::io(value)
    }
}

pub trait ResourceDownloadClient {
    fn probe_url(
        &mut self,
        url: &str,
        timeout: Duration,
    ) -> Result<ResourceProbeResult, ResourceDownloadError>;

    fn download_to(
        &mut self,
        url: &str,
        output_path: &Path,
        stage: &str,
        progress: &mut dyn FnMut(ResourceDownloadProgress),
    ) -> Result<(), ResourceDownloadError>;
}

pub struct ReqwestResourceDownloadClient {
    client: reqwest::blocking::Client,
}

impl ReqwestResourceDownloadClient {
    pub fn new() -> Result<Self, ResourceDownloadError> {
        Self::from_settings(&SettingsSnapshot::default())
    }

    pub fn from_settings(settings: &SettingsSnapshot) -> Result<Self, ResourceDownloadError> {
        let mut builder = reqwest::blocking::Client::builder()
            .timeout(DOWNLOAD_TIMEOUT)
            .user_agent(USER_AGENT);

        if settings.proxy_enabled.unwrap_or(false) {
            if let Some(proxy_uri) = normalized_optional(settings.proxy_uri.as_deref()) {
                let proxy = if settings.proxy_bypass_local.unwrap_or(false) {
                    let proxy_url = reqwest::Url::parse(&proxy_uri).map_err(|error| {
                        ResourceDownloadError::InvalidProxyUri {
                            uri: proxy_uri.clone(),
                            message: error.to_string(),
                        }
                    })?;
                    reqwest::Proxy::custom(move |url| {
                        (!is_loopback_url(url)).then(|| proxy_url.clone())
                    })
                } else {
                    reqwest::Proxy::all(&proxy_uri).map_err(|error| {
                        ResourceDownloadError::InvalidProxyUri {
                            uri: proxy_uri.clone(),
                            message: error.to_string(),
                        }
                    })?
                };
                builder = builder.proxy(proxy);
            }
        }

        let client = builder
            .build()
            .map_err(|error| ResourceDownloadError::network(error.to_string()))?;
        Ok(Self { client })
    }
}

impl ResourceDownloadClient for ReqwestResourceDownloadClient {
    fn probe_url(
        &mut self,
        url: &str,
        timeout: Duration,
    ) -> Result<ResourceProbeResult, ResourceDownloadError> {
        let started = Instant::now();
        let result = self
            .client
            .head(url)
            .timeout(timeout)
            .send()
            .map(|response| response.status().is_success())
            .unwrap_or(false);

        Ok(ResourceProbeResult {
            ok: result,
            elapsed_ms: started.elapsed().as_millis(),
        })
    }

    fn download_to(
        &mut self,
        url: &str,
        output_path: &Path,
        stage: &str,
        progress: &mut dyn FnMut(ResourceDownloadProgress),
    ) -> Result<(), ResourceDownloadError> {
        if let Some(parent) = output_path.parent() {
            fs::create_dir_all(parent)?;
        }

        let mut response = self
            .client
            .get(url)
            .send()
            .map_err(|error| ResourceDownloadError::network(error.to_string()))?;
        if !response.status().is_success() {
            return Err(ResourceDownloadError::network(format!(
                "HTTP {} for {url}",
                response.status()
            )));
        }

        let total_bytes = response
            .content_length()
            .and_then(|value| i64::try_from(value).ok())
            .unwrap_or(-1);
        let mut file = fs::File::create(output_path)?;
        let mut buffer = [0_u8; 81920];
        let mut bytes_downloaded = 0_u64;

        loop {
            let read = response
                .read(&mut buffer)
                .map_err(|error| ResourceDownloadError::network(error.to_string()))?;
            if read == 0 {
                break;
            }
            file.write_all(&buffer[..read])?;
            bytes_downloaded += read as u64;
            let percentage = if total_bytes > 0 {
                (bytes_downloaded as f64 / total_bytes as f64) * 100.0
            } else {
                -1.0
            };
            progress(ResourceDownloadProgress {
                stage: stage.to_string(),
                bytes_downloaded,
                total_bytes,
                percentage,
            });
        }

        Ok(())
    }
}

pub fn download_with_retry<C: ResourceDownloadClient>(
    client: &mut C,
    urls: &[String],
    output_path: impl AsRef<Path>,
    stage: &str,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<(), ResourceDownloadError> {
    download_with_retry_and_policy(
        client,
        urls,
        output_path,
        stage,
        progress,
        &ResourceDownloadRetryPolicy::default(),
    )
}

pub fn download_with_retry_and_policy<C: ResourceDownloadClient>(
    client: &mut C,
    urls: &[String],
    output_path: impl AsRef<Path>,
    stage: &str,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
    policy: &ResourceDownloadRetryPolicy,
) -> Result<(), ResourceDownloadError> {
    if urls.is_empty() {
        return Err(ResourceDownloadError::EmptyUrlList);
    }

    let output_path = output_path.as_ref();
    let temp_path = temporary_download_path(output_path);
    let mut last_error = ResourceDownloadError::EmptyUrlList;

    for url in urls {
        for attempt in 0..=policy.max_retries {
            if attempt > 0 {
                let delay = policy
                    .retry_delays
                    .get(attempt - 1)
                    .copied()
                    .or_else(|| policy.retry_delays.last().copied())
                    .unwrap_or(Duration::ZERO);
                if delay > Duration::ZERO {
                    std::thread::sleep(delay);
                }
            }

            match client.download_to(url, &temp_path, stage, progress) {
                Ok(()) => {
                    replace_file(&temp_path, output_path)?;
                    return Ok(());
                }
                Err(error) => {
                    last_error = error;
                    try_delete_file(&temp_path);
                }
            }
        }
    }

    Err(ResourceDownloadError::AllSourcesFailed {
        stage: stage.to_string(),
        source_count: urls.len(),
        last_error: Box::new(last_error),
    })
}

pub fn ordered_urls_by_probe<C: ResourceDownloadClient>(
    client: &mut C,
    urls: &[String],
) -> Vec<String> {
    if urls.len() <= 1 {
        return urls.to_vec();
    }

    let mut results = Vec::with_capacity(urls.len());
    for url in urls {
        let result = client
            .probe_url(url, SOURCE_PROBE_TIMEOUT)
            .unwrap_or(ResourceProbeResult {
                ok: false,
                elapsed_ms: u128::MAX,
            });
        results.push((url.clone(), result));
    }

    results.sort_by(|(_, left), (_, right)| {
        (if left.ok { 0 } else { 1 }, left.elapsed_ms)
            .cmp(&(if right.ok { 0 } else { 1 }, right.elapsed_ms))
    });
    results.into_iter().map(|(url, _)| url).collect()
}

pub fn is_file_valid(path: impl AsRef<Path>, min_size: u64) -> bool {
    fs::metadata(path)
        .map(|metadata| metadata.is_file() && metadata.len() >= min_size)
        .unwrap_or(false)
}

pub fn try_delete_file(path: impl AsRef<Path>) {
    let _ = fs::remove_file(path);
}

fn temporary_download_path(output_path: &Path) -> PathBuf {
    let mut os = output_path.as_os_str().to_os_string();
    os.push(".tmp");
    PathBuf::from(os)
}

fn replace_file(source: &Path, destination: &Path) -> Result<(), ResourceDownloadError> {
    if let Some(parent) = destination.parent() {
        fs::create_dir_all(parent)?;
    }
    if destination.exists() {
        fs::remove_file(destination)?;
    }
    fs::rename(source, destination)?;
    Ok(())
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
