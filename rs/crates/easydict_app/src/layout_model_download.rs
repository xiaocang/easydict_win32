use std::fmt;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use crate::compat_protocol::SettingsSnapshot;
use crate::resource_download::{
    download_with_retry, is_file_valid, ordered_urls_by_probe, try_delete_file,
    ReqwestResourceDownloadClient, ResourceDownloadClient, ResourceDownloadError,
    ResourceDownloadProgress,
};

pub const MODELS_SUBDIR: &str = "Models";
pub const ONNX_RUNTIME_FILE_NAME: &str = "onnxruntime.dll";
pub const DOC_LAYOUT_MODEL_FILE_NAME: &str = "doclayout_yolo.onnx";
pub const TATR_MODEL_FILE_NAME: &str = "tatr_structure.onnx";
pub const ONNX_RUNTIME_TEMP_ZIP_FILE_NAME: &str = "onnxruntime_temp.zip";
pub const ONNX_RUNTIME_ZIP_ENTRY_PATH: &str = "onnxruntime-win-x64-1.21.0/lib/onnxruntime.dll";

pub const MIN_RUNTIME_FILE_SIZE: u64 = 5 * 1024 * 1024;
pub const MIN_DOC_LAYOUT_MODEL_FILE_SIZE: u64 = 20 * 1024 * 1024;
pub const MIN_TATR_MODEL_FILE_SIZE: u64 = 60 * 1024 * 1024;

pub const ONNX_RUNTIME_URLS: &[&str] = &[
    "https://github.com/microsoft/onnxruntime/releases/download/v1.21.0/onnxruntime-win-x64-1.21.0.zip",
];
pub const DOC_LAYOUT_MODEL_URLS: &[&str] = &[
    "https://huggingface.co/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/main/doclayout_yolo_docstructbench_imgsz1024.onnx",
    "https://hf-mirror.com/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/main/doclayout_yolo_docstructbench_imgsz1024.onnx",
    "https://www.modelscope.cn/models/AI-ModelScope/DocLayout-YOLO-DocStructBench-onnx/resolve/master/doclayout_yolo_docstructbench_imgsz1024.onnx",
];
pub const TATR_MODEL_URLS: &[&str] = &[
    "https://huggingface.co/Xenova/table-transformer-structure-recognition/resolve/main/onnx/model.onnx",
    "https://hf-mirror.com/Xenova/table-transformer-structure-recognition/resolve/main/onnx/model.onnx",
];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LayoutModelDownloadConfig {
    pub runtime_urls: Vec<String>,
    pub doc_layout_model_urls: Vec<String>,
    pub tatr_model_urls: Vec<String>,
    pub runtime_zip_entry_path: String,
    pub min_runtime_file_size: u64,
    pub min_doc_layout_model_file_size: u64,
    pub min_tatr_model_file_size: u64,
}

impl Default for LayoutModelDownloadConfig {
    fn default() -> Self {
        Self {
            runtime_urls: ONNX_RUNTIME_URLS
                .iter()
                .map(|url| (*url).to_string())
                .collect(),
            doc_layout_model_urls: DOC_LAYOUT_MODEL_URLS
                .iter()
                .map(|url| (*url).to_string())
                .collect(),
            tatr_model_urls: TATR_MODEL_URLS
                .iter()
                .map(|url| (*url).to_string())
                .collect(),
            runtime_zip_entry_path: ONNX_RUNTIME_ZIP_ENTRY_PATH.to_string(),
            min_runtime_file_size: MIN_RUNTIME_FILE_SIZE,
            min_doc_layout_model_file_size: MIN_DOC_LAYOUT_MODEL_FILE_SIZE,
            min_tatr_model_file_size: MIN_TATR_MODEL_FILE_SIZE,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LayoutModelPaths {
    pub models_dir: PathBuf,
    pub native_lib_path: PathBuf,
    pub doc_layout_model_path: PathBuf,
    pub tatr_model_path: PathBuf,
}

impl LayoutModelPaths {
    pub fn for_base(base: impl AsRef<Path>) -> Self {
        let models_dir = model_cache_dir(base);
        Self {
            native_lib_path: models_dir.join(ONNX_RUNTIME_FILE_NAME),
            doc_layout_model_path: models_dir.join(DOC_LAYOUT_MODEL_FILE_NAME),
            tatr_model_path: models_dir.join(TATR_MODEL_FILE_NAME),
            models_dir,
        }
    }

    pub fn runtime_temp_zip_path(&self) -> PathBuf {
        self.models_dir.join(ONNX_RUNTIME_TEMP_ZIP_FILE_NAME)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LayoutModelStatus {
    pub runtime_ready: bool,
    pub doc_layout_model_ready: bool,
    pub tatr_model_ready: bool,
    pub native_library_dir: Option<PathBuf>,
    pub native_library_path: Option<PathBuf>,
    pub doc_layout_model_path: Option<PathBuf>,
    pub tatr_model_path: Option<PathBuf>,
}

impl LayoutModelStatus {
    pub fn is_ready(&self) -> bool {
        self.runtime_ready && self.doc_layout_model_ready
    }
}

#[derive(Debug)]
pub enum LayoutModelDownloadError {
    Download(ResourceDownloadError),
    Io(String),
    Zip(String),
    MissingRuntimeZipEntry(String),
    InvalidRuntimeFile { path: PathBuf, min_size: u64 },
    InvalidDocLayoutModelFile { path: PathBuf, min_size: u64 },
    InvalidTatrModelFile { path: PathBuf, min_size: u64 },
}

impl fmt::Display for LayoutModelDownloadError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Download(error) => write!(formatter, "{error}"),
            Self::Io(message) => write!(formatter, "Layout model file error: {message}"),
            Self::Zip(message) => write!(formatter, "Layout model zip error: {message}"),
            Self::MissingRuntimeZipEntry(entry) => {
                write!(formatter, "Entry '{entry}' not found in ONNX Runtime zip")
            }
            Self::InvalidRuntimeFile { path, min_size } => write!(
                formatter,
                "Extracted runtime file '{}' is smaller than {min_size} bytes",
                path.display()
            ),
            Self::InvalidDocLayoutModelFile { path, min_size } => write!(
                formatter,
                "Downloaded DocLayout model '{}' is smaller than {min_size} bytes",
                path.display()
            ),
            Self::InvalidTatrModelFile { path, min_size } => write!(
                formatter,
                "Downloaded TATR model '{}' is smaller than {min_size} bytes",
                path.display()
            ),
        }
    }
}

impl std::error::Error for LayoutModelDownloadError {}

impl From<ResourceDownloadError> for LayoutModelDownloadError {
    fn from(value: ResourceDownloadError) -> Self {
        Self::Download(value)
    }
}

impl From<io::Error> for LayoutModelDownloadError {
    fn from(value: io::Error) -> Self {
        Self::Io(value.to_string())
    }
}

impl From<zip::result::ZipError> for LayoutModelDownloadError {
    fn from(value: zip::result::ZipError) -> Self {
        Self::Zip(value.to_string())
    }
}

pub fn default_model_cache_dir() -> PathBuf {
    default_data_directory().join(MODELS_SUBDIR)
}

pub fn model_cache_dir(base: impl AsRef<Path>) -> PathBuf {
    base.as_ref().join(MODELS_SUBDIR)
}

pub fn layout_model_status_for_directory(
    base: impl AsRef<Path>,
    config: &LayoutModelDownloadConfig,
) -> LayoutModelStatus {
    let paths = LayoutModelPaths::for_base(base);
    let runtime_ready = is_file_valid(&paths.native_lib_path, config.min_runtime_file_size);
    let doc_layout_model_ready = is_file_valid(
        &paths.doc_layout_model_path,
        config.min_doc_layout_model_file_size,
    );
    let tatr_model_ready = is_file_valid(&paths.tatr_model_path, config.min_tatr_model_file_size);

    LayoutModelStatus {
        runtime_ready,
        doc_layout_model_ready,
        tatr_model_ready,
        native_library_dir: runtime_ready.then(|| paths.models_dir.clone()),
        native_library_path: runtime_ready.then(|| paths.native_lib_path.clone()),
        doc_layout_model_path: doc_layout_model_ready.then(|| paths.doc_layout_model_path.clone()),
        tatr_model_path: tatr_model_ready.then(|| paths.tatr_model_path.clone()),
    }
}

pub fn is_layout_model_ready_for_directory(
    base: impl AsRef<Path>,
    config: &LayoutModelDownloadConfig,
) -> bool {
    layout_model_status_for_directory(base, config).is_ready()
}

pub fn cleanup_invalid_layout_model_files_for_directory(
    base: impl AsRef<Path>,
    config: &LayoutModelDownloadConfig,
) {
    let paths = LayoutModelPaths::for_base(base);
    if paths.native_lib_path.exists()
        && !is_file_valid(&paths.native_lib_path, config.min_runtime_file_size)
    {
        try_delete_file(&paths.native_lib_path);
    }
    if paths.doc_layout_model_path.exists()
        && !is_file_valid(
            &paths.doc_layout_model_path,
            config.min_doc_layout_model_file_size,
        )
    {
        try_delete_file(&paths.doc_layout_model_path);
    }
}

pub fn delete_all_layout_model_files_for_directory(base: impl AsRef<Path>) {
    let paths = LayoutModelPaths::for_base(base);
    try_delete_file(paths.native_lib_path);
    try_delete_file(paths.doc_layout_model_path);
    try_delete_file(paths.tatr_model_path);
}

pub fn ensure_layout_model_available_for_directory<C: ResourceDownloadClient>(
    client: &mut C,
    base: impl AsRef<Path>,
    config: &LayoutModelDownloadConfig,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<LayoutModelStatus, LayoutModelDownloadError> {
    let base = base.as_ref();
    let paths = LayoutModelPaths::for_base(base);
    fs::create_dir_all(&paths.models_dir)?;
    cleanup_invalid_layout_model_files_for_directory(base, config);

    if !is_file_valid(&paths.native_lib_path, config.min_runtime_file_size) {
        download_onnx_runtime_for_directory(client, base, config, progress)?;
    }

    if !is_file_valid(
        &paths.doc_layout_model_path,
        config.min_doc_layout_model_file_size,
    ) {
        download_doc_layout_model_for_directory(client, base, config, progress)?;
    }

    Ok(layout_model_status_for_directory(base, config))
}

pub fn ensure_tatr_model_available_for_directory<C: ResourceDownloadClient>(
    client: &mut C,
    base: impl AsRef<Path>,
    config: &LayoutModelDownloadConfig,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, LayoutModelDownloadError> {
    let base = base.as_ref();
    let paths = LayoutModelPaths::for_base(base);
    fs::create_dir_all(&paths.models_dir)?;

    if paths.tatr_model_path.exists()
        && !is_file_valid(&paths.tatr_model_path, config.min_tatr_model_file_size)
    {
        try_delete_file(&paths.tatr_model_path);
    }

    if is_file_valid(&paths.tatr_model_path, config.min_tatr_model_file_size) {
        return Ok(paths.tatr_model_path);
    }

    let ordered_urls = ordered_urls_by_probe(client, &config.tatr_model_urls);
    download_with_retry(
        client,
        &ordered_urls,
        &paths.tatr_model_path,
        "tatr",
        progress,
    )?;

    if !is_file_valid(&paths.tatr_model_path, config.min_tatr_model_file_size) {
        try_delete_file(&paths.tatr_model_path);
        return Err(LayoutModelDownloadError::InvalidTatrModelFile {
            path: paths.tatr_model_path,
            min_size: config.min_tatr_model_file_size,
        });
    }

    Ok(paths.tatr_model_path)
}

pub fn ensure_layout_model_available(
    settings: &SettingsSnapshot,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<LayoutModelStatus, LayoutModelDownloadError> {
    let mut client = ReqwestResourceDownloadClient::from_settings(settings)?;
    ensure_layout_model_available_for_directory(
        &mut client,
        default_data_directory(),
        &LayoutModelDownloadConfig::default(),
        progress,
    )
}

pub fn ensure_tatr_model_available(
    settings: &SettingsSnapshot,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, LayoutModelDownloadError> {
    let mut client = ReqwestResourceDownloadClient::from_settings(settings)?;
    ensure_tatr_model_available_for_directory(
        &mut client,
        default_data_directory(),
        &LayoutModelDownloadConfig::default(),
        progress,
    )
}

fn download_onnx_runtime_for_directory<C: ResourceDownloadClient>(
    client: &mut C,
    base: &Path,
    config: &LayoutModelDownloadConfig,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, LayoutModelDownloadError> {
    let paths = LayoutModelPaths::for_base(base);
    let temp_zip_path = paths.runtime_temp_zip_path();
    download_with_retry(
        client,
        &config.runtime_urls,
        &temp_zip_path,
        "runtime",
        progress,
    )?;

    let extraction_result = extract_runtime_from_zip(
        &temp_zip_path,
        &paths.native_lib_path,
        &config.runtime_zip_entry_path,
    );
    try_delete_file(&temp_zip_path);
    extraction_result?;

    if !is_file_valid(&paths.native_lib_path, config.min_runtime_file_size) {
        try_delete_file(&paths.native_lib_path);
        return Err(LayoutModelDownloadError::InvalidRuntimeFile {
            path: paths.native_lib_path,
            min_size: config.min_runtime_file_size,
        });
    }

    Ok(paths.native_lib_path)
}

fn download_doc_layout_model_for_directory<C: ResourceDownloadClient>(
    client: &mut C,
    base: &Path,
    config: &LayoutModelDownloadConfig,
    progress: &mut dyn FnMut(ResourceDownloadProgress),
) -> Result<PathBuf, LayoutModelDownloadError> {
    let paths = LayoutModelPaths::for_base(base);
    let ordered_urls = ordered_urls_by_probe(client, &config.doc_layout_model_urls);
    download_with_retry(
        client,
        &ordered_urls,
        &paths.doc_layout_model_path,
        "model",
        progress,
    )?;

    if !is_file_valid(
        &paths.doc_layout_model_path,
        config.min_doc_layout_model_file_size,
    ) {
        try_delete_file(&paths.doc_layout_model_path);
        return Err(LayoutModelDownloadError::InvalidDocLayoutModelFile {
            path: paths.doc_layout_model_path,
            min_size: config.min_doc_layout_model_file_size,
        });
    }

    Ok(paths.doc_layout_model_path)
}

fn extract_runtime_from_zip(
    zip_path: &Path,
    output_path: &Path,
    entry_path: &str,
) -> Result<(), LayoutModelDownloadError> {
    let file = fs::File::open(zip_path)?;
    let mut archive = zip::ZipArchive::new(file)?;
    let mut entry = archive.by_name(entry_path).map_err(|error| match error {
        zip::result::ZipError::FileNotFound => {
            LayoutModelDownloadError::MissingRuntimeZipEntry(entry_path.to_string())
        }
        other => LayoutModelDownloadError::Zip(other.to_string()),
    })?;

    if let Some(parent) = output_path.parent() {
        fs::create_dir_all(parent)?;
    }
    let mut output = fs::File::create(output_path)?;
    io::copy(&mut entry, &mut output)?;
    Ok(())
}

fn default_data_directory() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Easydict")
}
