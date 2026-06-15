use std::collections::{HashMap, VecDeque};
use std::fs;
use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use easydict_app::protocol::SettingsSnapshot;
use easydict_app::{
    cleanup_invalid_layout_model_files_for_directory, delete_all_layout_model_files_for_directory,
    ensure_full_layout_model_available, ensure_full_layout_model_available_for_directory,
    ensure_layout_model_available_for_directory, ensure_tatr_model_available_for_directory,
    is_full_layout_model_ready_for_directory, layout_model_status_for_directory, model_cache_dir,
    LayoutModelDownloadConfig, LayoutModelDownloadError, LayoutModelPaths, ResourceDownloadClient,
    ResourceDownloadError, ResourceDownloadProgress, ResourceProbeResult,
    DOC_LAYOUT_MODEL_FILE_NAME, DOC_LAYOUT_MODEL_URLS, MIN_DOC_LAYOUT_MODEL_FILE_SIZE,
    MIN_RUNTIME_FILE_SIZE, MIN_TATR_MODEL_FILE_SIZE, ONNX_RUNTIME_FILE_NAME, ONNX_RUNTIME_URLS,
    ONNX_RUNTIME_ZIP_ENTRY_PATH, TATR_MODEL_FILE_NAME, TATR_MODEL_URLS,
};

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

#[derive(Default)]
struct FakeResourceDownloadClient {
    probes: HashMap<String, ResourceProbeResult>,
    downloads: HashMap<String, VecDeque<Result<Vec<u8>, &'static str>>>,
    requested_urls: Vec<String>,
    requested_stages: Vec<String>,
}

impl FakeResourceDownloadClient {
    fn with_probe(mut self, url: &str, ok: bool, elapsed_ms: u128) -> Self {
        self.probes
            .insert(url.to_string(), ResourceProbeResult { ok, elapsed_ms });
        self
    }

    fn with_download(mut self, url: &str, body: impl Into<Vec<u8>>) -> Self {
        self.downloads
            .entry(url.to_string())
            .or_default()
            .push_back(Ok(body.into()));
        self
    }
}

impl ResourceDownloadClient for FakeResourceDownloadClient {
    fn probe_url(
        &mut self,
        url: &str,
        _timeout: Duration,
    ) -> Result<ResourceProbeResult, ResourceDownloadError> {
        Ok(*self.probes.get(url).unwrap_or(&ResourceProbeResult {
            ok: false,
            elapsed_ms: u128::MAX,
        }))
    }

    fn download_to(
        &mut self,
        url: &str,
        output_path: &Path,
        stage: &str,
        progress: &mut dyn FnMut(ResourceDownloadProgress),
    ) -> Result<(), ResourceDownloadError> {
        self.requested_urls.push(url.to_string());
        self.requested_stages.push(stage.to_string());
        let body = self
            .downloads
            .get_mut(url)
            .and_then(VecDeque::pop_front)
            .unwrap_or(Err("missing fake response"))
            .map_err(|message| ResourceDownloadError::Network(message.to_string()))?;

        if let Some(parent) = output_path.parent() {
            fs::create_dir_all(parent).expect("create fake download parent");
        }
        fs::write(output_path, &body).expect("write fake download body");
        progress(ResourceDownloadProgress {
            stage: stage.to_string(),
            bytes_downloaded: body.len() as u64,
            total_bytes: body.len() as i64,
            percentage: if body.is_empty() { -1.0 } else { 100.0 },
        });
        Ok(())
    }
}

fn temp_dir(name: &str) -> PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system time after unix epoch")
        .as_nanos();
    std::env::temp_dir().join(format!(
        "easydict-layout-model-download-{name}-{}-{nanos}",
        std::process::id()
    ))
}

struct EnvironmentVariableGuard {
    name: &'static str,
    original: Option<String>,
}

impl EnvironmentVariableGuard {
    fn set(name: &'static str, value: &str) -> Self {
        let original = std::env::var(name).ok();
        std::env::set_var(name, value);
        Self { name, original }
    }
}

impl Drop for EnvironmentVariableGuard {
    fn drop(&mut self) {
        if let Some(value) = self.original.as_ref() {
            std::env::set_var(self.name, value);
        } else {
            std::env::remove_var(self.name);
        }
    }
}

fn tiny_config() -> LayoutModelDownloadConfig {
    LayoutModelDownloadConfig {
        runtime_urls: vec!["https://runtime.example/ort.zip".to_string()],
        doc_layout_model_urls: vec![
            "https://slow.example/doclayout.onnx".to_string(),
            "https://fast.example/doclayout.onnx".to_string(),
        ],
        tatr_model_urls: vec![
            "https://slow.example/tatr.onnx".to_string(),
            "https://fast.example/tatr.onnx".to_string(),
        ],
        runtime_zip_entry_path: "onnxruntime-win-x64-1.21.0/lib/onnxruntime.dll".to_string(),
        min_runtime_file_size: 3,
        min_doc_layout_model_file_size: 4,
        min_tatr_model_file_size: 5,
    }
}

fn zip_with_entry(entry: &str, body: &[u8]) -> Vec<u8> {
    let cursor = Cursor::new(Vec::new());
    let mut writer = zip::ZipWriter::new(cursor);
    let options: zip::write::FileOptions<'_, ()> = zip::write::FileOptions::default();
    writer.start_file(entry, options).expect("start zip entry");
    writer.write_all(body).expect("write zip entry");
    writer.finish().expect("finish zip").into_inner()
}

fn create_valid_sized_file(path: &Path, min_size: u64) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("create sized file parent");
    }
    fs::File::create(path)
        .expect("create sized file")
        .set_len(min_size + 1)
        .expect("set sized file length");
}

#[test]
fn layout_model_default_config_matches_legacy_contract() {
    let config = LayoutModelDownloadConfig::default();

    assert_eq!(ONNX_RUNTIME_FILE_NAME, "onnxruntime.dll");
    assert_eq!(DOC_LAYOUT_MODEL_FILE_NAME, "doclayout_yolo.onnx");
    assert_eq!(TATR_MODEL_FILE_NAME, "tatr_structure.onnx");
    assert_eq!(
        ONNX_RUNTIME_ZIP_ENTRY_PATH,
        "onnxruntime-win-x64-1.21.0/lib/onnxruntime.dll"
    );
    assert_eq!(MIN_RUNTIME_FILE_SIZE, 5 * 1024 * 1024);
    assert_eq!(MIN_DOC_LAYOUT_MODEL_FILE_SIZE, 20 * 1024 * 1024);
    assert_eq!(MIN_TATR_MODEL_FILE_SIZE, 60 * 1024 * 1024);
    assert_eq!(config.runtime_urls, ONNX_RUNTIME_URLS);
    assert_eq!(config.doc_layout_model_urls, DOC_LAYOUT_MODEL_URLS);
    assert_eq!(config.tatr_model_urls, TATR_MODEL_URLS);
}

#[test]
fn layout_model_status_reflects_valid_file_sizes_and_paths() {
    let dir = temp_dir("status");
    let config = tiny_config();
    let paths = LayoutModelPaths::for_base(&dir);
    fs::create_dir_all(&paths.models_dir).expect("create models dir");
    fs::write(&paths.native_lib_path, b"ort").expect("write runtime");
    fs::write(&paths.doc_layout_model_path, b"model").expect("write model");
    fs::write(&paths.tatr_model_path, b"tatr!").expect("write tatr");

    let status = layout_model_status_for_directory(&dir, &config);

    assert!(status.is_ready());
    assert!(status.is_full_layout_ready());
    assert!(status.tatr_model_ready);
    assert_eq!(status.native_library_dir, Some(model_cache_dir(&dir)));
    assert_eq!(status.native_library_path, Some(paths.native_lib_path));
    assert_eq!(
        status.doc_layout_model_path,
        Some(paths.doc_layout_model_path)
    );
    assert_eq!(status.tatr_model_path, Some(paths.tatr_model_path));

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_full_ready_requires_runtime_doc_layout_and_tatr_models() {
    let dir = temp_dir("full-ready");
    let config = tiny_config();
    let paths = LayoutModelPaths::for_base(&dir);
    fs::create_dir_all(&paths.models_dir).expect("create models dir");
    fs::write(&paths.native_lib_path, b"ort").expect("write runtime");
    fs::write(&paths.doc_layout_model_path, b"model").expect("write model");

    let status = layout_model_status_for_directory(&dir, &config);

    assert!(status.is_ready());
    assert!(!status.is_full_layout_ready());
    assert!(!is_full_layout_model_ready_for_directory(&dir, &config));

    fs::write(&paths.tatr_model_path, b"tatr!").expect("write tatr");

    assert!(is_full_layout_model_ready_for_directory(&dir, &config));

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_ensure_downloads_runtime_zip_and_fastest_model_source() {
    let dir = temp_dir("ensure");
    let config = tiny_config();
    let runtime_zip = zip_with_entry(&config.runtime_zip_entry_path, b"ort");
    let mut client = FakeResourceDownloadClient::default()
        .with_probe("https://slow.example/doclayout.onnx", true, 80)
        .with_probe("https://fast.example/doclayout.onnx", true, 5)
        .with_download("https://runtime.example/ort.zip", runtime_zip)
        .with_download("https://fast.example/doclayout.onnx", b"model");
    let mut stages = Vec::new();

    let status =
        ensure_layout_model_available_for_directory(&mut client, &dir, &config, &mut |progress| {
            stages.push(progress.stage)
        })
        .expect("layout model ensure succeeds");

    let paths = LayoutModelPaths::for_base(&dir);
    assert!(status.is_ready());
    assert_eq!(
        fs::read(&paths.native_lib_path).expect("read runtime"),
        b"ort"
    );
    assert_eq!(
        fs::read(&paths.doc_layout_model_path).expect("read model"),
        b"model"
    );
    assert!(!paths.runtime_temp_zip_path().exists());
    assert_eq!(
        client.requested_urls,
        vec![
            "https://runtime.example/ort.zip",
            "https://fast.example/doclayout.onnx"
        ]
    );
    assert_eq!(client.requested_stages, vec!["runtime", "model"]);
    assert_eq!(stages, vec!["runtime", "model"]);

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_ensure_skips_existing_valid_runtime_and_model() {
    let dir = temp_dir("skip-existing");
    let config = tiny_config();
    let paths = LayoutModelPaths::for_base(&dir);
    fs::create_dir_all(&paths.models_dir).expect("create models dir");
    fs::write(&paths.native_lib_path, b"ort").expect("write runtime");
    fs::write(&paths.doc_layout_model_path, b"model").expect("write model");
    let mut client = FakeResourceDownloadClient::default();

    let status =
        ensure_layout_model_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect("existing files are enough");

    assert!(status.is_ready());
    assert!(client.requested_urls.is_empty());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_cleanup_removes_invalid_runtime_and_doc_model_but_not_tatr() {
    let dir = temp_dir("cleanup");
    let config = LayoutModelDownloadConfig {
        min_runtime_file_size: 10,
        min_doc_layout_model_file_size: 10,
        min_tatr_model_file_size: 10,
        ..tiny_config()
    };
    let paths = LayoutModelPaths::for_base(&dir);
    fs::create_dir_all(&paths.models_dir).expect("create models dir");
    fs::write(&paths.native_lib_path, b"x").expect("write small runtime");
    fs::write(&paths.doc_layout_model_path, b"x").expect("write small model");
    fs::write(&paths.tatr_model_path, b"x").expect("write small tatr");

    cleanup_invalid_layout_model_files_for_directory(&dir, &config);

    assert!(!paths.native_lib_path.exists());
    assert!(!paths.doc_layout_model_path.exists());
    assert!(paths.tatr_model_path.exists());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_tatr_download_is_separate_and_cleans_invalid_file() {
    let dir = temp_dir("tatr");
    let config = tiny_config();
    let paths = LayoutModelPaths::for_base(&dir);
    fs::create_dir_all(&paths.models_dir).expect("create models dir");
    fs::write(&paths.tatr_model_path, b"x").expect("write invalid tatr");
    let mut client = FakeResourceDownloadClient::default()
        .with_probe("https://slow.example/tatr.onnx", true, 50)
        .with_probe("https://fast.example/tatr.onnx", true, 4)
        .with_download("https://fast.example/tatr.onnx", b"tatr!");

    let path = ensure_tatr_model_available_for_directory(&mut client, &dir, &config, &mut |_| {})
        .expect("tatr download succeeds");

    assert_eq!(path, paths.tatr_model_path);
    assert_eq!(fs::read(path).expect("read tatr"), b"tatr!");
    assert_eq!(
        client.requested_urls,
        vec!["https://fast.example/tatr.onnx"]
    );
    assert_eq!(client.requested_stages, vec!["tatr"]);

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_full_ensure_downloads_runtime_doc_layout_and_tatr_models() {
    let dir = temp_dir("full-ensure");
    let config = tiny_config();
    let runtime_zip = zip_with_entry(&config.runtime_zip_entry_path, b"ort");
    let mut client = FakeResourceDownloadClient::default()
        .with_probe("https://slow.example/doclayout.onnx", true, 80)
        .with_probe("https://fast.example/doclayout.onnx", true, 5)
        .with_probe("https://slow.example/tatr.onnx", true, 40)
        .with_probe("https://fast.example/tatr.onnx", true, 4)
        .with_download("https://runtime.example/ort.zip", runtime_zip)
        .with_download("https://fast.example/doclayout.onnx", b"model")
        .with_download("https://fast.example/tatr.onnx", b"tatr!");
    let mut stages = Vec::new();

    let status = ensure_full_layout_model_available_for_directory(
        &mut client,
        &dir,
        &config,
        &mut |progress| stages.push(progress.stage),
    )
    .expect("full layout model ensure succeeds");

    let paths = LayoutModelPaths::for_base(&dir);
    assert!(status.is_full_layout_ready());
    assert_eq!(
        fs::read(&paths.native_lib_path).expect("read runtime"),
        b"ort"
    );
    assert_eq!(
        fs::read(&paths.doc_layout_model_path).expect("read doc layout"),
        b"model"
    );
    assert_eq!(
        fs::read(&paths.tatr_model_path).expect("read tatr"),
        b"tatr!"
    );
    assert_eq!(
        client.requested_urls,
        vec![
            "https://runtime.example/ort.zip",
            "https://fast.example/doclayout.onnx",
            "https://fast.example/tatr.onnx"
        ]
    );
    assert_eq!(client.requested_stages, vec!["runtime", "model", "tatr"]);
    assert_eq!(stages, vec!["runtime", "model", "tatr"]);

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_settings_entry_uses_configured_cache_dir() {
    let dir = temp_dir("settings-cache-dir");
    let paths = LayoutModelPaths::for_base(&dir);
    create_valid_sized_file(&paths.native_lib_path, MIN_RUNTIME_FILE_SIZE);
    create_valid_sized_file(&paths.doc_layout_model_path, MIN_DOC_LAYOUT_MODEL_FILE_SIZE);
    create_valid_sized_file(&paths.tatr_model_path, MIN_TATR_MODEL_FILE_SIZE);
    let settings = SettingsSnapshot {
        cache_dir: Some(dir.display().to_string()),
        ..SettingsSnapshot::default()
    };

    let status = ensure_full_layout_model_available(&settings, &mut |_| {})
        .expect("cached layout models should be found without downloading");

    assert!(status.is_full_layout_ready());
    assert_eq!(status.native_library_path, Some(paths.native_lib_path));
    assert_eq!(
        status.doc_layout_model_path,
        Some(paths.doc_layout_model_path)
    );
    assert_eq!(status.tatr_model_path, Some(paths.tatr_model_path));

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_settings_entry_treats_blank_cache_dir_as_default() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let local_app_data = temp_dir("settings-blank-cache-dir");
    let default_base = local_app_data.join("Easydict");
    let paths = LayoutModelPaths::for_base(&default_base);
    create_valid_sized_file(&paths.native_lib_path, MIN_RUNTIME_FILE_SIZE);
    create_valid_sized_file(&paths.doc_layout_model_path, MIN_DOC_LAYOUT_MODEL_FILE_SIZE);
    create_valid_sized_file(&paths.tatr_model_path, MIN_TATR_MODEL_FILE_SIZE);
    let _local_app_data_guard =
        EnvironmentVariableGuard::set("LOCALAPPDATA", &local_app_data.to_string_lossy());
    let settings = SettingsSnapshot {
        cache_dir: Some(" \t\r\n ".to_string()),
        ..SettingsSnapshot::default()
    };

    let status = ensure_full_layout_model_available(&settings, &mut |_| {})
        .expect("blank cache_dir should use default layout model cache");

    assert!(status.is_full_layout_ready());
    assert_eq!(status.native_library_path, Some(paths.native_lib_path));
    assert_eq!(
        status.doc_layout_model_path,
        Some(paths.doc_layout_model_path)
    );
    assert_eq!(status.tatr_model_path, Some(paths.tatr_model_path));

    let _ = fs::remove_dir_all(&local_app_data);
}

#[test]
fn layout_model_invalid_downloaded_doc_model_is_deleted_and_reported() {
    let dir = temp_dir("invalid-model");
    let config = tiny_config();
    let runtime_zip = zip_with_entry(&config.runtime_zip_entry_path, b"ort");
    let mut client = FakeResourceDownloadClient::default()
        .with_probe("https://slow.example/doclayout.onnx", true, 1)
        .with_probe("https://fast.example/doclayout.onnx", true, 5)
        .with_download("https://runtime.example/ort.zip", runtime_zip)
        .with_download("https://slow.example/doclayout.onnx", b"x");

    let error =
        ensure_layout_model_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect_err("small model is rejected");

    let paths = LayoutModelPaths::for_base(&dir);
    assert!(matches!(
        error,
        LayoutModelDownloadError::InvalidDocLayoutModelFile { .. }
    ));
    assert!(!paths.doc_layout_model_path.exists());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_missing_runtime_zip_entry_is_reported_and_temp_zip_deleted() {
    let dir = temp_dir("missing-entry");
    let config = LayoutModelDownloadConfig {
        doc_layout_model_urls: vec!["https://model.example/doclayout.onnx".to_string()],
        ..tiny_config()
    };
    let runtime_zip = zip_with_entry("wrong/path.dll", b"ort");
    let mut client = FakeResourceDownloadClient::default()
        .with_download("https://runtime.example/ort.zip", runtime_zip)
        .with_download("https://model.example/doclayout.onnx", b"model");

    let error =
        ensure_layout_model_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect_err("missing runtime entry is rejected");

    let paths = LayoutModelPaths::for_base(&dir);
    assert!(matches!(
        error,
        LayoutModelDownloadError::MissingRuntimeZipEntry(_)
    ));
    assert!(!paths.runtime_temp_zip_path().exists());
    assert!(!paths.native_lib_path.exists());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn layout_model_delete_all_removes_managed_files_only() {
    let dir = temp_dir("delete");
    let paths = LayoutModelPaths::for_base(&dir);
    fs::create_dir_all(&paths.models_dir).expect("create models dir");
    fs::write(&paths.native_lib_path, b"ort").expect("write runtime");
    fs::write(&paths.doc_layout_model_path, b"model").expect("write model");
    fs::write(&paths.tatr_model_path, b"tatr").expect("write tatr");
    let unmanaged = paths.models_dir.join("keep.onnx");
    fs::write(&unmanaged, b"keep").expect("write unmanaged");

    delete_all_layout_model_files_for_directory(&dir);

    assert!(!paths.native_lib_path.exists());
    assert!(!paths.doc_layout_model_path.exists());
    assert!(!paths.tatr_model_path.exists());
    assert!(unmanaged.exists());

    let _ = fs::remove_dir_all(&dir);
}
