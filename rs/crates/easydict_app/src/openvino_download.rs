use std::fmt;
use std::fs;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

use crate::protocol::SettingsSnapshot;
use crate::resource_download::{
    download_with_retry, try_delete_file, ReqwestResourceDownloadClient, ResourceDownloadClient,
    ResourceDownloadError, ResourceDownloadProgress,
};
use easydict_nllb::{
    nllb_model_total_approximate_bytes, openvino_runtime_package_file_name, NllbModelPaths,
    MODEL_COMPLETION_SENTINEL, NLLB_MODEL_MANIFEST_FILES, OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE,
    OPENVINO_RUNTIME_FILES, OPENVINO_RUNTIME_IDENTIFIER, OPENVINO_RUNTIME_PACKAGE_SHA256,
    OPENVINO_RUNTIME_PACKAGE_URL,
};
use ring::digest::{Context, SHA256};

const OPENVINO_RUNTIME_PACKAGE_STAGE: &str = "openvino-runtime-package";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenVinoModelDownloadFile {
    pub local_file_name: String,
    pub download_urls: Vec<String>,
    pub approximate_bytes: u64,
    pub sha256: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenVinoDownloadConfig {
    pub model_files: Vec<OpenVinoModelDownloadFile>,
    pub runtime_package_urls: Vec<String>,
    pub runtime_package_sha256: String,
    pub runtime_package_file_name: String,
    pub runtime_files: Vec<String>,
    pub runtime_zip_native_dir: String,
    pub require_supported_runtime_architecture: bool,
}

impl Default for OpenVinoDownloadConfig {
    fn default() -> Self {
        Self {
            model_files: NLLB_MODEL_MANIFEST_FILES
                .iter()
                .map(|file| OpenVinoModelDownloadFile {
                    local_file_name: file.local_file_name.to_string(),
                    download_urls: vec![file.download_url()],
                    approximate_bytes: file.approximate_bytes,
                    sha256: file.sha256.map(str::to_string),
                })
                .collect(),
            runtime_package_urls: vec![OPENVINO_RUNTIME_PACKAGE_URL.to_string()],
            runtime_package_sha256: OPENVINO_RUNTIME_PACKAGE_SHA256.to_string(),
            runtime_package_file_name: openvino_runtime_package_file_name(),
            runtime_files: OPENVINO_RUNTIME_FILES
                .iter()
                .map(|file| (*file).to_string())
                .collect(),
            runtime_zip_native_dir: format!("runtimes/{OPENVINO_RUNTIME_IDENTIFIER}/native"),
            require_supported_runtime_architecture: true,
        }
    }
}

impl OpenVinoDownloadConfig {
    pub fn model_total_approximate_bytes(&self) -> u64 {
        self.model_files
            .iter()
            .map(|file| file.approximate_bytes)
            .sum()
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenVinoDownloadStatus {
    pub paths: NllbModelPaths,
    pub model_ready: bool,
    pub runtime_ready: bool,
}

impl OpenVinoDownloadStatus {
    pub fn is_ready(&self) -> bool {
        self.model_ready && self.runtime_ready
    }
}

#[derive(Debug)]
pub enum OpenVinoDownloadError {
    UnsupportedArchitecture,
    Download(ResourceDownloadError),
    Io(String),
    Zip(String),
    MissingRuntimePackageEntry(String),
    Sha256Mismatch {
        path: PathBuf,
        expected: String,
        actual: String,
    },
    EmptyUnverifiedFile(PathBuf),
}

impl fmt::Display for OpenVinoDownloadError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedArchitecture => formatter
                .write_str("OpenVINO local translation runtime is only available for Windows x64"),
            Self::Download(error) => write!(formatter, "{error}"),
            Self::Io(message) => write!(formatter, "OpenVINO download file error: {message}"),
            Self::Zip(message) => {
                write!(formatter, "OpenVINO runtime package zip error: {message}")
            }
            Self::MissingRuntimePackageEntry(entry) => {
                write!(
                    formatter,
                    "OpenVINO runtime package is missing native file: {entry}"
                )
            }
            Self::Sha256Mismatch {
                path,
                expected,
                actual,
            } => write!(
                formatter,
                "SHA-256 mismatch for '{}'. Expected {expected}, got {actual}",
                path.display()
            ),
            Self::EmptyUnverifiedFile(path) => write!(
                formatter,
                "Downloaded OpenVINO/NLLB file '{}' is empty and has no SHA-256 manifest",
                path.display()
            ),
        }
    }
}

impl std::error::Error for OpenVinoDownloadError {}

impl From<ResourceDownloadError> for OpenVinoDownloadError {
    fn from(value: ResourceDownloadError) -> Self {
        Self::Download(value)
    }
}

impl From<io::Error> for OpenVinoDownloadError {
    fn from(value: io::Error) -> Self {
        Self::Io(value.to_string())
    }
}

impl From<zip::result::ZipError> for OpenVinoDownloadError {
    fn from(value: zip::result::ZipError) -> Self {
        Self::Zip(value.to_string())
    }
}

pub fn default_openvino_data_directory() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Easydict")
}

pub fn openvino_download_status_for_directory(
    base: impl AsRef<Path>,
    config: &OpenVinoDownloadConfig,
) -> OpenVinoDownloadStatus {
    let paths = NllbModelPaths::from_cache_base(base);
    OpenVinoDownloadStatus {
        model_ready: complete_file_set(
            &paths.model_dir,
            config
                .model_files
                .iter()
                .map(|file| file.local_file_name.as_str()),
        ),
        runtime_ready: complete_file_set(
            &paths.runtime_dir,
            config.runtime_files.iter().map(String::as_str),
        ),
        paths,
    }
}

pub fn ensure_openvino_assets_available(
    settings: &SettingsSnapshot,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<OpenVinoDownloadStatus, OpenVinoDownloadError> {
    let mut client = ReqwestResourceDownloadClient::from_settings(settings)?;
    let base = settings
        .cache_dir_path()
        .unwrap_or_else(default_openvino_data_directory);
    ensure_openvino_assets_available_for_directory(
        &mut client,
        base,
        &OpenVinoDownloadConfig::default(),
        progress,
    )
}

pub fn ensure_openvino_assets_available_for_directory<C: ResourceDownloadClient>(
    client: &mut C,
    base: impl AsRef<Path>,
    config: &OpenVinoDownloadConfig,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<OpenVinoDownloadStatus, OpenVinoDownloadError> {
    let base = base.as_ref();
    ensure_openvino_model_available_for_directory(client, base, config, progress)?;
    ensure_openvino_runtime_available_for_directory(client, base, config, progress)?;
    Ok(openvino_download_status_for_directory(base, config))
}

pub fn ensure_openvino_model_available_for_directory<C: ResourceDownloadClient>(
    client: &mut C,
    base: impl AsRef<Path>,
    config: &OpenVinoDownloadConfig,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, OpenVinoDownloadError> {
    let paths = NllbModelPaths::from_cache_base(base);
    if openvino_download_status_for_paths(&paths, config).model_ready {
        return Ok(paths.model_dir);
    }

    fs::create_dir_all(&paths.model_dir)?;
    try_delete_file(paths.model_dir.join(MODEL_COMPLETION_SENTINEL));

    for file in &config.model_files {
        let path = paths.model_dir.join(&file.local_file_name);
        if model_file_ready(&path, file)? {
            continue;
        }

        try_delete_file(&path);
        download_with_retry(
            client,
            &file.download_urls,
            &path,
            &format!("openvino-model-{}", file.local_file_name),
            progress,
        )?;
        validate_model_file(&path, file)?;
    }

    write_completion_sentinel(&paths.model_dir)?;
    Ok(paths.model_dir)
}

pub fn ensure_openvino_runtime_available_for_directory<C: ResourceDownloadClient>(
    client: &mut C,
    base: impl AsRef<Path>,
    config: &OpenVinoDownloadConfig,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, OpenVinoDownloadError> {
    if config.require_supported_runtime_architecture
        && !is_openvino_runtime_supported_current_architecture()
    {
        return Err(OpenVinoDownloadError::UnsupportedArchitecture);
    }

    let paths = NllbModelPaths::from_cache_base(base);
    if openvino_download_status_for_paths(&paths, config).runtime_ready {
        return Ok(paths.runtime_dir);
    }

    fs::create_dir_all(&paths.runtime_dir)?;
    try_delete_file(paths.runtime_dir.join(MODEL_COMPLETION_SENTINEL));

    let package_path = runtime_package_path(&paths, config)?;
    if !package_file_ready(&package_path, &config.runtime_package_sha256)? {
        try_delete_file(&package_path);
        download_with_retry(
            client,
            &config.runtime_package_urls,
            &package_path,
            OPENVINO_RUNTIME_PACKAGE_STAGE,
            progress,
        )?;
        verify_sha256(&package_path, &config.runtime_package_sha256)?;
    }

    extract_openvino_runtime_package(&package_path, &paths.runtime_dir, config)?;
    write_completion_sentinel(&paths.runtime_dir)?;
    ensure_openvino_runtime_directory_on_path(&paths.runtime_dir);
    Ok(paths.runtime_dir)
}

pub fn ensure_openvino_runtime_directory_on_path(runtime_dir: &Path) {
    let env_value = std::env::var(OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE).ok();
    if !openvino_ep_path_injection_enabled(env_value.as_deref()) {
        return;
    }

    let current_path = std::env::var("PATH").unwrap_or_default();
    if let Some(updated_path) = openvino_runtime_path_with_directory(&current_path, runtime_dir) {
        std::env::set_var("PATH", updated_path);
    }
}

pub fn openvino_ep_path_injection_enabled(value: Option<&str>) -> bool {
    value
        .map(str::trim)
        .is_some_and(|value| value == "1" || value.eq_ignore_ascii_case("true"))
}

pub fn openvino_runtime_path_with_directory(
    current_path: &str,
    runtime_dir: &Path,
) -> Option<String> {
    let runtime_dir = normalized_path_for_compare(runtime_dir);
    let already_present = current_path
        .split(path_list_separator())
        .any(|entry| paths_equal_lossy(entry, &runtime_dir));
    if already_present {
        None
    } else if current_path.is_empty() {
        Some(runtime_dir)
    } else {
        Some(format!(
            "{}{}{}",
            runtime_dir,
            path_list_separator(),
            current_path
        ))
    }
}

fn path_list_separator() -> char {
    if cfg!(windows) {
        ';'
    } else {
        ':'
    }
}

fn normalized_path_for_compare(path: &Path) -> String {
    let path = fs::canonicalize(path).unwrap_or_else(|_| path.to_path_buf());
    normalize_path_text(&path.to_string_lossy())
}

fn paths_equal_lossy(left: &str, right: &str) -> bool {
    normalize_path_text(left) == normalize_path_text(right)
}

fn normalize_path_text(value: &str) -> String {
    let trimmed = value.trim().trim_end_matches(['\\', '/']);
    if cfg!(windows) {
        trimmed.to_ascii_lowercase()
    } else {
        trimmed.to_string()
    }
}

fn openvino_download_status_for_paths(
    paths: &NllbModelPaths,
    config: &OpenVinoDownloadConfig,
) -> OpenVinoDownloadStatus {
    OpenVinoDownloadStatus {
        paths: paths.clone(),
        model_ready: complete_file_set(
            &paths.model_dir,
            config
                .model_files
                .iter()
                .map(|file| file.local_file_name.as_str()),
        ),
        runtime_ready: complete_file_set(
            &paths.runtime_dir,
            config.runtime_files.iter().map(String::as_str),
        ),
    }
}

fn complete_file_set<'a>(dir: &Path, files: impl IntoIterator<Item = &'a str>) -> bool {
    dir.join(MODEL_COMPLETION_SENTINEL).is_file()
        && files.into_iter().all(|file| dir.join(file).is_file())
}

fn model_file_ready(
    path: &Path,
    file: &OpenVinoModelDownloadFile,
) -> Result<bool, OpenVinoDownloadError> {
    if !path.is_file() {
        return Ok(false);
    }
    if let Some(expected) = file.sha256.as_deref() {
        return Ok(sha256_lower(path)? == expected.to_ascii_lowercase());
    }
    Ok(path
        .metadata()
        .map(|metadata| metadata.len() > 0)
        .unwrap_or(false))
}

fn validate_model_file(
    path: &Path,
    file: &OpenVinoModelDownloadFile,
) -> Result<(), OpenVinoDownloadError> {
    if let Some(expected) = file.sha256.as_deref() {
        verify_sha256(path, expected)
    } else if path.metadata().map(|metadata| metadata.len()).unwrap_or(0) == 0 {
        try_delete_file(path);
        Err(OpenVinoDownloadError::EmptyUnverifiedFile(
            path.to_path_buf(),
        ))
    } else {
        Ok(())
    }
}

fn package_file_ready(path: &Path, expected_sha256: &str) -> Result<bool, OpenVinoDownloadError> {
    if !path.is_file() {
        return Ok(false);
    }
    Ok(sha256_lower(path)? == expected_sha256.to_ascii_lowercase())
}

fn runtime_package_path(
    paths: &NllbModelPaths,
    config: &OpenVinoDownloadConfig,
) -> Result<PathBuf, OpenVinoDownloadError> {
    let package_root = paths
        .runtime_dir
        .parent()
        .and_then(Path::parent)
        .and_then(Path::parent)
        .ok_or_else(|| {
            OpenVinoDownloadError::Io(format!(
                "OpenVINO runtime directory '{}' has no package root",
                paths.runtime_dir.display()
            ))
        })?;
    Ok(package_root.join(&config.runtime_package_file_name))
}

fn extract_openvino_runtime_package(
    package_path: &Path,
    runtime_dir: &Path,
    config: &OpenVinoDownloadConfig,
) -> Result<(), OpenVinoDownloadError> {
    let file = fs::File::open(package_path)?;
    let mut archive = zip::ZipArchive::new(file)?;
    fs::create_dir_all(runtime_dir)?;

    for runtime_file in &config.runtime_files {
        let entry_path = format!("{}/{}", config.runtime_zip_native_dir, runtime_file);
        let mut entry = archive.by_name(&entry_path).map_err(|error| match error {
            zip::result::ZipError::FileNotFound => {
                OpenVinoDownloadError::MissingRuntimePackageEntry(entry_path.clone())
            }
            other => OpenVinoDownloadError::Zip(other.to_string()),
        })?;
        let mut output = fs::File::create(runtime_dir.join(runtime_file))?;
        io::copy(&mut entry, &mut output)?;
    }

    Ok(())
}

fn verify_sha256(path: &Path, expected_sha256: &str) -> Result<(), OpenVinoDownloadError> {
    let expected = expected_sha256.to_ascii_lowercase();
    let actual = sha256_lower(path)?;
    if actual == expected {
        Ok(())
    } else {
        try_delete_file(path);
        Err(OpenVinoDownloadError::Sha256Mismatch {
            path: path.to_path_buf(),
            expected,
            actual,
        })
    }
}

fn sha256_lower(path: &Path) -> Result<String, OpenVinoDownloadError> {
    let mut file = fs::File::open(path)?;
    let mut context = Context::new(&SHA256);
    let mut buffer = [0_u8; 81920];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        context.update(&buffer[..read]);
    }
    Ok(hex_lower(context.finish().as_ref()))
}

fn hex_lower(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        output.push(HEX[(byte >> 4) as usize] as char);
        output.push(HEX[(byte & 0x0f) as usize] as char);
    }
    output
}

fn write_completion_sentinel(dir: &Path) -> Result<(), OpenVinoDownloadError> {
    fs::create_dir_all(dir)?;
    let mut file = fs::File::create(dir.join(MODEL_COMPLETION_SENTINEL))?;
    writeln!(file, "{}", completion_timestamp_text())?;
    Ok(())
}

fn completion_timestamp_text() -> String {
    match std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH) {
        Ok(duration) => format!("unix-nanos:{}", duration.as_nanos()),
        Err(_) => "unix-nanos:0".to_string(),
    }
}

pub fn is_openvino_runtime_supported_current_architecture() -> bool {
    cfg!(target_os = "windows") && cfg!(target_arch = "x86_64")
}

pub fn default_nllb_model_approximate_bytes() -> u64 {
    nllb_model_total_approximate_bytes()
}
