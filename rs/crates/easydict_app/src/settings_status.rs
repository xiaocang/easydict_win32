//! Real (filesystem-backed) runtime status for the settings page.
//!
//! Replaces the static placeholder status strings with genuine on-disk checks
//! under the conventional Easydict data directory. This is the work the
//! settings entry loading overlay waits on.

use std::path::{Path, PathBuf};

use crate::compat_protocol::SettingsSnapshot;
use crate::font_download;
use crate::layout_model_download::{self, LayoutModelDownloadConfig};
use crate::openai_compatible::{
    self, CommandFoundryLocalEndpointResolver, FoundryLocalModelState,
    FoundryLocalRuntimeController,
};

/// Resolved availability of the optional downloadable assets.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SettingsRuntimeStatus {
    pub layout_model: String,
    pub cjk_font: String,
    pub foundry_local_status: String,
    pub open_vino_status: String,
    pub open_vino_download_progress: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OpenVinoCacheStatus {
    Ready,
    NotDownloaded,
    NotCompatible,
}

const OPEN_VINO_MODEL_DIRECTORY: &str = "nllb-200-distilled-600M";
const OPEN_VINO_COMPLETION_SENTINEL: &str = ".complete";
const OPEN_VINO_RUNTIME_VERSION: &str = "1.21.0";
const OPEN_VINO_RUNTIME_IDENTIFIER: &str = "win-x64";
const OPEN_VINO_MODEL_FILES: &[&str] = &[
    "encoder_model_quantized.onnx",
    "decoder_model_quantized.onnx",
    "sentencepiece.bpe.model",
    "tokenizer.json",
    "config.json",
];
const OPEN_VINO_RUNTIME_FILES: &[&str] = &[
    "onnxruntime.dll",
    "onnxruntime.lib",
    "onnxruntime_providers_openvino.dll",
    "onnxruntime_providers_shared.dll",
    "openvino.dll",
    "openvino_auto_batch_plugin.dll",
    "openvino_auto_plugin.dll",
    "openvino_c.dll",
    "openvino_hetero_plugin.dll",
    "openvino_intel_cpu_plugin.dll",
    "openvino_intel_gpu_plugin.dll",
    "openvino_intel_npu_plugin.dll",
    "openvino_ir_frontend.dll",
    "openvino_onnx_frontend.dll",
    "openvino_paddle_frontend.dll",
    "openvino_pytorch_frontend.dll",
    "openvino_tensorflow_frontend.dll",
    "openvino_tensorflow_lite_frontend.dll",
    "tbb12.dll",
    "tbb12_debug.dll",
    "tbbbind_2_5.dll",
    "tbbbind_2_5_debug.dll",
    "tbbmalloc.dll",
    "tbbmalloc_debug.dll",
    "tbbmalloc_proxy.dll",
    "tbbmalloc_proxy_debug.dll",
];

/// Conventional Easydict per-user data directory (`%LOCALAPPDATA%/Easydict`).
fn data_directory() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Easydict")
}

/// Returns true if `dir` exists and contains at least one file whose extension
/// (case-insensitive) is in `extensions`.
fn directory_has_file_with_extension(dir: &Path, extensions: &[&str]) -> bool {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return false;
    };
    for entry in entries.flatten() {
        if let Some(extension) = entry.path().extension().and_then(|ext| ext.to_str()) {
            let lowered = extension.to_ascii_lowercase();
            if extensions.iter().any(|candidate| *candidate == lowered) {
                return true;
            }
        }
    }
    false
}

fn directory_has_complete_file_set(dir: &Path, files: &[&str]) -> bool {
    if !dir.join(OPEN_VINO_COMPLETION_SENTINEL).is_file() {
        return false;
    }

    files.iter().all(|file| dir.join(file).is_file())
}

fn is_open_vino_supported_current_architecture() -> bool {
    cfg!(target_os = "windows") && cfg!(target_arch = "x86_64")
}

pub fn open_vino_cache_status_for_settings(settings: &SettingsSnapshot) -> OpenVinoCacheStatus {
    let base = settings
        .cache_dir
        .as_deref()
        .map(PathBuf::from)
        .unwrap_or_else(data_directory);
    open_vino_cache_status_for_directory(&base)
}

pub fn open_vino_cache_status_for_directory(base: &Path) -> OpenVinoCacheStatus {
    open_vino_cache_status_for_directory_with_arch(
        base,
        is_open_vino_supported_current_architecture(),
    )
}

fn open_vino_cache_status_for_directory_with_arch(
    base: &Path,
    open_vino_supported: bool,
) -> OpenVinoCacheStatus {
    if !open_vino_supported {
        return OpenVinoCacheStatus::NotCompatible;
    }

    let open_vino_model_dir = base.join("models").join(OPEN_VINO_MODEL_DIRECTORY);
    let open_vino_runtime_dir = base
        .join("runtimes")
        .join("openvino")
        .join(OPEN_VINO_RUNTIME_VERSION)
        .join(OPEN_VINO_RUNTIME_IDENTIFIER)
        .join("native");
    let open_vino_model_installed =
        directory_has_complete_file_set(&open_vino_model_dir, OPEN_VINO_MODEL_FILES);
    let open_vino_runtime_installed =
        directory_has_complete_file_set(&open_vino_runtime_dir, OPEN_VINO_RUNTIME_FILES);

    if open_vino_model_installed && open_vino_runtime_installed {
        OpenVinoCacheStatus::Ready
    } else {
        OpenVinoCacheStatus::NotDownloaded
    }
}

/// Performs the real filesystem check. Reports "Available" when the asset is
/// present on disk and "Not downloaded" otherwise (matching the prior
/// placeholder wording for the not-present case, but now truthful).
pub fn load_runtime_status() -> SettingsRuntimeStatus {
    load_runtime_status_for_settings(SettingsSnapshot::default())
}

pub fn load_runtime_status_for_settings(settings: SettingsSnapshot) -> SettingsRuntimeStatus {
    let dir = settings
        .cache_dir
        .as_deref()
        .map(PathBuf::from)
        .unwrap_or_else(data_directory);
    let mut foundry_controller = CommandFoundryLocalEndpointResolver::default();
    let foundry_local_status =
        foundry_local_status_for_settings_with_controller(&settings, &mut foundry_controller);
    status_for_directory_with_open_vino_support_and_foundry_status(
        &dir,
        is_open_vino_supported_current_architecture(),
        foundry_local_status,
    )
}

/// Testable core that checks a given base directory.
pub fn status_for_directory(base: &Path) -> SettingsRuntimeStatus {
    status_for_directory_with_open_vino_support_and_foundry_status(
        base,
        is_open_vino_supported_current_architecture(),
        default_foundry_local_status(),
    )
}

fn default_foundry_local_status() -> String {
    "Endpoint auto-detected at runtime".to_string()
}

pub fn foundry_local_status_for_settings_with_controller<C>(
    settings: &SettingsSnapshot,
    controller: &mut C,
) -> String
where
    C: FoundryLocalRuntimeController,
{
    match openai_compatible::check_foundry_local_runtime_status(controller, settings) {
        Ok(status) => foundry_status_message(status),
        Err(error) => error.message,
    }
}

fn foundry_status_message(status: openai_compatible::FoundryLocalStatusCheck) -> String {
    match status.state {
        FoundryLocalModelState::Ready => status
            .endpoint
            .map(|endpoint| format!("Foundry Local is ready at {endpoint}."))
            .unwrap_or_else(|| "Foundry Local is ready.".to_string()),
        FoundryLocalModelState::NotCompatible => status.detail_message.unwrap_or_else(|| {
            "Foundry Local CLI is not installed or is not available on PATH.".to_string()
        }),
        FoundryLocalModelState::NeedsPreparation => status
            .detail_message
            .unwrap_or_else(|| "Foundry Local service is not running.".to_string()),
        FoundryLocalModelState::Failed => status
            .detail_message
            .unwrap_or_else(|| "Foundry Local status check failed.".to_string()),
    }
}

fn status_for_directory_with_open_vino_support_and_foundry_status(
    base: &Path,
    open_vino_supported: bool,
    foundry_local_status: String,
) -> SettingsRuntimeStatus {
    let layout_model = if layout_model_download::is_layout_model_ready_for_directory(
        base,
        &LayoutModelDownloadConfig::default(),
    ) || directory_has_file_with_extension(&base.join("models"), &["onnx"])
    {
        "Available".to_string()
    } else {
        "Not downloaded".to_string()
    };

    let cjk_font = if font_download::has_any_cjk_font_for_directory(base)
        || directory_has_file_with_extension(&base.join("fonts"), &["ttf", "otf", "ttc"])
    {
        "Available".to_string()
    } else {
        "Not downloaded".to_string()
    };

    let open_vino_status =
        match open_vino_cache_status_for_directory_with_arch(base, open_vino_supported) {
            OpenVinoCacheStatus::Ready => "NLLB-200 model ready",
            OpenVinoCacheStatus::NotDownloaded => "Model not downloaded",
            OpenVinoCacheStatus::NotCompatible => "OpenVINO local translation requires Windows x64",
        }
        .to_string();

    SettingsRuntimeStatus {
        layout_model,
        cjk_font,
        foundry_local_status,
        open_vino_status,
        open_vino_download_progress: "Idle".to_string(),
    }
}

#[cfg(test)]
fn status_for_directory_with_open_vino_support(
    base: &Path,
    open_vino_supported: bool,
) -> SettingsRuntimeStatus {
    status_for_directory_with_open_vino_support_and_foundry_status(
        base,
        open_vino_supported,
        default_foundry_local_status(),
    )
}

#[allow(dead_code)]
#[cfg(test)]
mod tests {
    use super::*;
    use crate::openai_compatible::{
        FoundryLocalEndpointResolver, FoundryLocalRuntimeState, FoundryLocalRuntimeStatus,
        OpenAiExecutionError,
    };
    use std::collections::VecDeque;
    use std::fs;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_status_dir(name: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time after unix epoch")
            .as_nanos();
        std::env::temp_dir().join(format!(
            "easydict-status-{name}-{}-{nanos}",
            std::process::id()
        ))
    }

    fn install_complete_file_set(dir: &Path, files: &[&str]) {
        fs::create_dir_all(dir).expect("create complete file set dir");
        for file in files {
            fs::write(dir.join(file), b"x").expect("write complete file set member");
        }
        fs::write(dir.join(OPEN_VINO_COMPLETION_SENTINEL), b"x")
            .expect("write completion sentinel");
    }

    fn install_open_vino_model(base: &Path) {
        install_complete_file_set(
            &base.join("models").join(OPEN_VINO_MODEL_DIRECTORY),
            OPEN_VINO_MODEL_FILES,
        );
    }

    fn install_open_vino_runtime(base: &Path) {
        install_complete_file_set(
            &base
                .join("runtimes")
                .join("openvino")
                .join(OPEN_VINO_RUNTIME_VERSION)
                .join(OPEN_VINO_RUNTIME_IDENTIFIER)
                .join("native"),
            OPEN_VINO_RUNTIME_FILES,
        );
    }

    struct FakeFoundryRuntimeController {
        status_responses: VecDeque<FoundryLocalRuntimeStatus>,
        endpoint_responses: VecDeque<Option<String>>,
        calls: Vec<&'static str>,
    }

    impl FakeFoundryRuntimeController {
        fn new(
            status_responses: impl IntoIterator<Item = FoundryLocalRuntimeStatus>,
            endpoint_responses: impl IntoIterator<Item = Option<String>>,
        ) -> Self {
            Self {
                status_responses: status_responses.into_iter().collect(),
                endpoint_responses: endpoint_responses.into_iter().collect(),
                calls: Vec::new(),
            }
        }
    }

    impl FoundryLocalEndpointResolver for FakeFoundryRuntimeController {
        fn resolve_chat_completions_endpoint(
            &mut self,
        ) -> Result<Option<String>, OpenAiExecutionError> {
            self.calls.push("resolve_endpoint");
            Ok(self.endpoint_responses.pop_front().flatten())
        }
    }

    impl FoundryLocalRuntimeController for FakeFoundryRuntimeController {
        fn get_status(&mut self) -> Result<FoundryLocalRuntimeStatus, OpenAiExecutionError> {
            self.calls.push("get_status");
            Ok(self.status_responses.pop_front().unwrap_or_else(|| {
                FoundryLocalRuntimeStatus::new(FoundryLocalRuntimeState::Running)
            }))
        }

        fn start_service(&mut self) -> Result<(), OpenAiExecutionError> {
            self.calls.push("start_service");
            Ok(())
        }

        fn load_model(&mut self, _model: &str) -> Result<(), OpenAiExecutionError> {
            self.calls.push("load_model");
            Ok(())
        }
    }

    #[test]
    fn reports_not_downloaded_when_assets_absent() {
        let dir = temp_status_dir("empty");
        let _ = fs::create_dir_all(&dir);

        let status = status_for_directory_with_open_vino_support(&dir, true);

        assert_eq!(status.layout_model, "Not downloaded");
        assert_eq!(status.cjk_font, "Not downloaded");
        assert_eq!(status.open_vino_status, "Model not downloaded");
        assert_eq!(status.open_vino_download_progress, "Idle");
        assert_eq!(
            status.foundry_local_status,
            "Endpoint auto-detected at runtime"
        );

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_available_when_model_and_font_present() {
        let dir = temp_status_dir("present");
        let models = dir.join("models");
        let fonts = dir.join("fonts");
        fs::create_dir_all(&models).expect("create models dir");
        fs::create_dir_all(&fonts).expect("create fonts dir");
        fs::write(models.join("layout.onnx"), b"x").expect("write model");
        fs::write(fonts.join("noto.ttf"), b"x").expect("write font");

        let status = status_for_directory(&dir);

        assert_eq!(status.layout_model, "Available");
        assert_eq!(status.cjk_font, "Available");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_available_when_known_cjk_font_asset_is_present() {
        let dir = temp_status_dir("known-cjk-font");
        let fonts = font_download::font_cache_dir(&dir);
        fs::create_dir_all(&fonts).expect("create fonts dir");
        fs::write(fonts.join("NotoSansSC-Regular.ttf"), b"x").expect("write known CJK font");

        let status = status_for_directory(&dir);

        assert_eq!(status.cjk_font, "Available");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_open_vino_model_ready_when_model_and_runtime_file_sets_are_complete() {
        let dir = temp_status_dir("openvino-ready");
        install_open_vino_model(&dir);
        install_open_vino_runtime(&dir);

        let status = status_for_directory_with_open_vino_support(&dir, true);

        assert_eq!(status.open_vino_status, "NLLB-200 model ready");
        assert_eq!(status.open_vino_download_progress, "Idle");
        assert_eq!(
            open_vino_cache_status_for_directory_with_arch(&dir, true),
            OpenVinoCacheStatus::Ready
        );

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_open_vino_not_downloaded_when_model_is_complete_but_runtime_is_absent() {
        let dir = temp_status_dir("openvino-runtime-missing");
        install_open_vino_model(&dir);

        let status = status_for_directory_with_open_vino_support(&dir, true);

        assert_eq!(status.open_vino_status, "Model not downloaded");
        assert_eq!(
            open_vino_cache_status_for_directory_with_arch(&dir, true),
            OpenVinoCacheStatus::NotDownloaded
        );

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_open_vino_model_missing_when_sentinel_or_manifest_files_are_absent() {
        let dir = temp_status_dir("openvino-model-missing");
        let model_dir = dir.join("models").join(OPEN_VINO_MODEL_DIRECTORY);
        fs::create_dir_all(&model_dir).expect("create model dir");
        fs::write(model_dir.join(OPEN_VINO_COMPLETION_SENTINEL), b"x").expect("write sentinel");
        install_open_vino_runtime(&dir);

        let status = status_for_directory_with_open_vino_support(&dir, true);

        assert_eq!(status.open_vino_status, "Model not downloaded");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_open_vino_unsupported_on_non_windows_x64() {
        let dir = temp_status_dir("openvino-unsupported");
        install_open_vino_model(&dir);
        install_open_vino_runtime(&dir);

        let status = status_for_directory_with_open_vino_support(&dir, false);

        assert_eq!(
            status.open_vino_status,
            "OpenVINO local translation requires Windows x64"
        );
        assert_eq!(
            open_vino_cache_status_for_directory_with_arch(&dir, false),
            OpenVinoCacheStatus::NotCompatible
        );

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn open_vino_cache_status_for_settings_uses_explicit_cache_dir_when_present() {
        let dir = temp_status_dir("openvino-settings-cache-dir");
        install_open_vino_model(&dir);
        install_open_vino_runtime(&dir);
        let settings = SettingsSnapshot {
            cache_dir: Some(dir.to_string_lossy().to_string()),
            ..SettingsSnapshot::default()
        };

        assert_eq!(
            open_vino_cache_status_for_settings(&settings),
            OpenVinoCacheStatus::Ready
        );

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn foundry_local_status_for_settings_reports_runtime_not_installed() {
        let mut controller = FakeFoundryRuntimeController::new(
            [FoundryLocalRuntimeStatus::with_detail(
                FoundryLocalRuntimeState::NotInstalled,
                "Foundry Local CLI is not installed or is not available on PATH.",
            )],
            [],
        );

        let status = foundry_local_status_for_settings_with_controller(
            &SettingsSnapshot::default(),
            &mut controller,
        );

        assert_eq!(
            status,
            "Foundry Local CLI is not installed or is not available on PATH."
        );
        assert_eq!(controller.calls, vec!["get_status"]);
    }

    #[test]
    fn foundry_local_status_for_settings_uses_runtime_endpoint_when_ready() {
        let mut controller = FakeFoundryRuntimeController::new(
            [FoundryLocalRuntimeStatus::with_endpoint(
                FoundryLocalRuntimeState::Running,
                "http://127.0.0.1:5273/openai/status",
            )],
            [],
        );

        let status = foundry_local_status_for_settings_with_controller(
            &SettingsSnapshot::default(),
            &mut controller,
        );

        assert_eq!(
            status,
            "Foundry Local is ready at http://127.0.0.1:5273/v1/chat/completions."
        );
        assert_eq!(controller.calls, vec!["get_status"]);
    }

    #[test]
    fn foundry_local_status_for_user_managed_endpoint_skips_cli_lifecycle() {
        let mut controller = FakeFoundryRuntimeController::new(
            [FoundryLocalRuntimeStatus::new(
                FoundryLocalRuntimeState::NotInstalled,
            )],
            [],
        );
        let settings = SettingsSnapshot {
            foundry_local_endpoint: Some("https://foundry.example.test/v1".to_string()),
            ..SettingsSnapshot::default()
        };

        let status = foundry_local_status_for_settings_with_controller(&settings, &mut controller);

        assert_eq!(
            status,
            "Foundry Local is ready at https://foundry.example.test/v1/chat/completions."
        );
        assert!(controller.calls.is_empty());
    }
}
