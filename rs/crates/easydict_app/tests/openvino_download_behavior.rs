use std::collections::{HashMap, VecDeque};
use std::fs;
use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use easydict_app::{
    default_openvino_data_directory, ensure_openvino_assets_available_for_directory,
    ensure_openvino_model_available_for_directory, ensure_openvino_runtime_available_for_directory,
    openvino_download_status_for_directory, openvino_ep_path_injection_enabled,
    openvino_runtime_path_with_directory, EasydictUiState, Message, OpenVinoDownloadConfig,
    OpenVinoDownloadError, OpenVinoDownloadStatus, OpenVinoModelDownloadFile,
    ResourceDownloadClient, ResourceDownloadError, ResourceDownloadProgress, ResourceProbeResult,
};
use easydict_nllb::{
    NllbModelPaths, MODEL_COMPLETION_SENTINEL, OPENVINO_RUNTIME_PACKAGE_SHA256,
    OPENVINO_RUNTIME_PACKAGE_URL,
};
use ring::digest::{digest, SHA256};

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

#[derive(Default)]
struct FakeResourceDownloadClient {
    downloads: HashMap<String, VecDeque<Result<Vec<u8>, &'static str>>>,
    requested_urls: Vec<String>,
    requested_stages: Vec<String>,
}

impl FakeResourceDownloadClient {
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
        _url: &str,
        _timeout: Duration,
    ) -> Result<ResourceProbeResult, ResourceDownloadError> {
        Ok(ResourceProbeResult {
            ok: true,
            elapsed_ms: 1,
        })
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
        "easydict-openvino-download-{name}-{}-{nanos}",
        std::process::id()
    ))
}

struct EnvironmentVariableGuard {
    name: &'static str,
    previous: Option<String>,
}

impl EnvironmentVariableGuard {
    fn set(name: &'static str, value: &str) -> Self {
        let previous = std::env::var(name).ok();
        std::env::set_var(name, value);
        Self { name, previous }
    }
}

impl Drop for EnvironmentVariableGuard {
    fn drop(&mut self) {
        if let Some(previous) = &self.previous {
            std::env::set_var(self.name, previous);
        } else {
            std::env::remove_var(self.name);
        }
    }
}

#[test]
fn default_openvino_data_directory_uses_rs_specific_root() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let local_app_data = temp_dir("openvino-default-root");
    let _local_app_data_guard =
        EnvironmentVariableGuard::set("LOCALAPPDATA", &local_app_data.to_string_lossy());

    assert_eq!(
        default_openvino_data_directory(),
        local_app_data.join("EasydictRs")
    );

    let _ = fs::remove_dir_all(&local_app_data);
}

fn sha256_lower(bytes: &[u8]) -> String {
    hex_lower(digest(&SHA256, bytes).as_ref())
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

fn zip_with_entries(entries: &[(&str, &[u8])]) -> Vec<u8> {
    let cursor = Cursor::new(Vec::new());
    let mut writer = zip::ZipWriter::new(cursor);
    let options: zip::write::FileOptions<'_, ()> = zip::write::FileOptions::default();
    for (entry, body) in entries {
        writer.start_file(*entry, options).expect("start zip entry");
        writer.write_all(body).expect("write zip entry");
    }
    writer.finish().expect("finish zip").into_inner()
}

fn tiny_config() -> OpenVinoDownloadConfig {
    tiny_config_with_runtime_package(&runtime_zip())
}

fn tiny_config_with_runtime_package(runtime_package: &[u8]) -> OpenVinoDownloadConfig {
    OpenVinoDownloadConfig {
        model_files: vec![
            OpenVinoModelDownloadFile {
                local_file_name: "encoder_model_quantized.onnx".to_string(),
                download_urls: vec!["https://model.example/encoder.onnx".to_string()],
                approximate_bytes: 7,
                sha256: Some(sha256_lower(b"encoder")),
            },
            OpenVinoModelDownloadFile {
                local_file_name: "config.json".to_string(),
                download_urls: vec!["https://model.example/config.json".to_string()],
                approximate_bytes: 2,
                sha256: None,
            },
        ],
        runtime_package_urls: vec!["https://runtime.example/openvino.nupkg".to_string()],
        runtime_package_sha256: sha256_lower(runtime_package),
        runtime_package_file_name: "openvino-test.nupkg".to_string(),
        runtime_files: vec!["onnxruntime.dll".to_string(), "openvino.dll".to_string()],
        runtime_zip_native_dir: "runtimes/win-x64/native".to_string(),
        require_supported_runtime_architecture: false,
    }
}

fn runtime_zip() -> Vec<u8> {
    zip_with_entries(&[
        ("runtimes/win-x64/native/onnxruntime.dll", b"ort"),
        ("runtimes/win-x64/native/openvino.dll", b"ov"),
    ])
}

#[test]
fn openvino_default_download_config_matches_pinned_manifest() {
    let config = OpenVinoDownloadConfig::default();

    assert_eq!(
        config.runtime_package_urls,
        vec![OPENVINO_RUNTIME_PACKAGE_URL.to_string()]
    );
    assert_eq!(
        config.runtime_package_sha256,
        OPENVINO_RUNTIME_PACKAGE_SHA256
    );
    assert_eq!(
        config.model_files[0].download_urls[0],
        "https://huggingface.co/Xenova/nllb-200-distilled-600M/resolve/261c31d1a5732c67cdd16d80e8d6088507c7ccea/onnx/encoder_model_quantized.onnx"
    );
    assert_eq!(
        config
            .model_files
            .iter()
            .map(|file| file.approximate_bytes)
            .sum::<u64>(),
        easydict_app::default_nllb_model_approximate_bytes()
    );
}

#[test]
fn openvino_model_downloads_manifest_files_and_writes_sentinel_last() {
    let dir = temp_dir("model-success");
    let config = tiny_config();
    let mut client = FakeResourceDownloadClient::default()
        .with_download("https://model.example/encoder.onnx", b"encoder")
        .with_download("https://model.example/config.json", b"{}");
    let mut stages = Vec::new();

    let model_dir = ensure_openvino_model_available_for_directory(
        &mut client,
        &dir,
        &config,
        &mut |progress| stages.push(progress.stage),
    )
    .expect("model download succeeds");

    assert_eq!(
        fs::read(model_dir.join("encoder_model_quantized.onnx")).unwrap(),
        b"encoder"
    );
    assert_eq!(fs::read(model_dir.join("config.json")).unwrap(), b"{}");
    assert!(model_dir.join(MODEL_COMPLETION_SENTINEL).is_file());
    assert_eq!(
        client.requested_urls,
        vec![
            "https://model.example/encoder.onnx",
            "https://model.example/config.json"
        ]
    );
    assert_eq!(
        stages,
        vec![
            "openvino-model-encoder_model_quantized.onnx",
            "openvino-model-config.json"
        ]
    );

    let status = openvino_download_status_for_directory(&dir, &config);
    assert!(status.model_ready);
    assert!(!status.runtime_ready);

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn openvino_model_skips_complete_existing_cache_without_downloading() {
    let dir = temp_dir("model-skip");
    let config = tiny_config();
    let paths = NllbModelPaths::from_cache_base(&dir);
    fs::create_dir_all(&paths.model_dir).expect("create model dir");
    fs::write(
        paths.model_dir.join("encoder_model_quantized.onnx"),
        b"encoder",
    )
    .unwrap();
    fs::write(paths.model_dir.join("config.json"), b"{}").unwrap();
    fs::write(paths.model_dir.join(MODEL_COMPLETION_SENTINEL), b"x").unwrap();
    let mut client = FakeResourceDownloadClient::default();

    ensure_openvino_model_available_for_directory(&mut client, &dir, &config, &mut |_| {})
        .expect("existing complete model cache is enough");

    assert!(client.requested_urls.is_empty());
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn openvino_model_sha_mismatch_deletes_bad_file_and_keeps_sentinel_absent() {
    let dir = temp_dir("model-sha-mismatch");
    let mut config = tiny_config();
    config.model_files.truncate(1);
    let mut client = FakeResourceDownloadClient::default()
        .with_download("https://model.example/encoder.onnx", b"bad");

    let error =
        ensure_openvino_model_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect_err("SHA mismatch should fail");

    assert!(matches!(
        error,
        OpenVinoDownloadError::Sha256Mismatch { .. }
    ));
    let paths = NllbModelPaths::from_cache_base(&dir);
    assert!(!paths
        .model_dir
        .join("encoder_model_quantized.onnx")
        .exists());
    assert!(!paths.model_dir.join(MODEL_COMPLETION_SENTINEL).exists());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn openvino_runtime_download_verifies_package_extracts_whitelist_and_writes_sentinel() {
    let dir = temp_dir("runtime-success");
    let zip = runtime_zip();
    let config = tiny_config_with_runtime_package(&zip);
    let mut client = FakeResourceDownloadClient::default()
        .with_download("https://runtime.example/openvino.nupkg", zip);

    let runtime_dir =
        ensure_openvino_runtime_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect("runtime download succeeds");

    assert_eq!(
        fs::read(runtime_dir.join("onnxruntime.dll")).unwrap(),
        b"ort"
    );
    assert_eq!(fs::read(runtime_dir.join("openvino.dll")).unwrap(), b"ov");
    assert!(runtime_dir.join(MODEL_COMPLETION_SENTINEL).is_file());
    assert_eq!(
        client.requested_stages,
        vec!["openvino-runtime-package".to_string()]
    );

    let status = openvino_download_status_for_directory(&dir, &config);
    assert!(status.runtime_ready);
    assert!(!status.model_ready);

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn openvino_runtime_path_injection_is_env_gated_and_idempotent() {
    let runtime_dir = PathBuf::from(
        r"C:\Users\me\AppData\Local\Easydict\runtimes\openvino\1.21.0\win-x64\native",
    );

    assert!(openvino_ep_path_injection_enabled(Some("1")));
    assert!(openvino_ep_path_injection_enabled(Some("true")));
    assert!(openvino_ep_path_injection_enabled(Some("TRUE")));
    assert!(!openvino_ep_path_injection_enabled(Some("0")));
    assert!(!openvino_ep_path_injection_enabled(None));

    let updated = openvino_runtime_path_with_directory("C:\\Windows\\System32", &runtime_dir)
        .expect("runtime dir should be prepended when absent");
    assert!(updated
        .to_ascii_lowercase()
        .starts_with(&runtime_dir.to_string_lossy().to_ascii_lowercase()));
    assert!(updated.contains("C:\\Windows\\System32"));
    assert_eq!(
        openvino_runtime_path_with_directory(&updated, &runtime_dir),
        None
    );
}

#[test]
fn openvino_runtime_sha_mismatch_does_not_extract_or_write_sentinel() {
    let dir = temp_dir("runtime-sha-mismatch");
    let zip = runtime_zip();
    let mut config = tiny_config_with_runtime_package(&zip);
    config.runtime_package_sha256 = sha256_lower(b"something else");
    let mut client = FakeResourceDownloadClient::default()
        .with_download("https://runtime.example/openvino.nupkg", zip);

    let error =
        ensure_openvino_runtime_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect_err("runtime SHA mismatch should fail");

    assert!(matches!(
        error,
        OpenVinoDownloadError::Sha256Mismatch { .. }
    ));
    let paths = NllbModelPaths::from_cache_base(&dir);
    assert!(!paths.runtime_dir.join("onnxruntime.dll").exists());
    assert!(!paths.runtime_dir.join(MODEL_COMPLETION_SENTINEL).exists());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn openvino_runtime_missing_manifest_entry_is_reported_without_sentinel() {
    let dir = temp_dir("runtime-missing-entry");
    let mut config = tiny_config();
    let zip = zip_with_entries(&[("runtimes/win-x64/native/onnxruntime.dll", b"ort")]);
    config.runtime_package_sha256 = sha256_lower(&zip);
    let mut client = FakeResourceDownloadClient::default()
        .with_download("https://runtime.example/openvino.nupkg", zip);

    let error =
        ensure_openvino_runtime_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect_err("missing runtime entry should fail");

    assert!(matches!(
        error,
        OpenVinoDownloadError::MissingRuntimePackageEntry(_)
    ));
    let paths = NllbModelPaths::from_cache_base(&dir);
    assert!(!paths.runtime_dir.join(MODEL_COMPLETION_SENTINEL).exists());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn openvino_assets_download_model_and_runtime_into_same_cache_contract() {
    let dir = temp_dir("assets-success");
    let zip = runtime_zip();
    let config = tiny_config_with_runtime_package(&zip);
    let mut client = FakeResourceDownloadClient::default()
        .with_download("https://model.example/encoder.onnx", b"encoder")
        .with_download("https://model.example/config.json", b"{}")
        .with_download("https://runtime.example/openvino.nupkg", zip);

    let status =
        ensure_openvino_assets_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect("assets download succeeds");

    assert!(status.is_ready());
    assert_eq!(
        client.requested_urls,
        vec![
            "https://model.example/encoder.onnx",
            "https://model.example/config.json",
            "https://runtime.example/openvino.nupkg"
        ]
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn openvino_assets_download_stops_before_runtime_when_model_hash_fails() {
    let dir = temp_dir("assets-model-hash-fail");
    let zip = runtime_zip();
    let config = tiny_config_with_runtime_package(&zip);
    let mut client = FakeResourceDownloadClient::default()
        .with_download("https://model.example/encoder.onnx", b"bad")
        .with_download("https://runtime.example/openvino.nupkg", zip);

    let error =
        ensure_openvino_assets_available_for_directory(&mut client, &dir, &config, &mut |_| {})
            .expect_err("model hash failure stops combined ensure");

    assert!(matches!(
        error,
        OpenVinoDownloadError::Sha256Mismatch { .. }
    ));
    assert_eq!(
        client.requested_urls,
        vec!["https://model.example/encoder.onnx"]
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn openvino_download_finished_updates_settings_status() {
    let dir = temp_dir("state-finished");
    let mut state = EasydictUiState::default();
    state.apply(Message::DownloadOpenVinoModel);
    assert_eq!(state.settings.open_vino_download_progress, "Queued");

    state.apply(Message::OpenVinoDownloadFinished(Ok(
        OpenVinoDownloadStatus {
            paths: NllbModelPaths::from_cache_base(&dir),
            model_ready: true,
            runtime_ready: true,
        },
    )));

    assert_eq!(state.settings.open_vino_status, "NLLB-200 model ready");
    assert_eq!(state.settings.open_vino_download_progress, "Idle");

    state.apply(Message::OpenVinoDownloadFinished(Err(
        "network unavailable".to_string(),
    )));

    assert_eq!(
        state.settings.open_vino_status,
        "Download failed: network unavailable"
    );
    assert_eq!(state.settings.open_vino_download_progress, "Failed");

    let _ = fs::remove_dir_all(&dir);
}
