//! Real (filesystem-backed) runtime status for the settings page.
//!
//! Replaces the static placeholder status strings with genuine on-disk checks
//! under the conventional Easydict data directory. This is the work the
//! settings entry loading overlay waits on.

use std::path::{Path, PathBuf};

use crate::font_download;
use crate::layout_model_download::{self, LayoutModelDownloadConfig};
#[cfg(test)]
use crate::openai_compatible::FoundryLocalError;
use crate::openai_compatible::{self, FoundryLocalModelState, FoundryLocalRuntimeController};
use crate::protocol::SettingsSnapshot;
use easydict_nllb::NllbModelPaths;
use easydict_windows_ai::{
    default_windows_ai_language_model_client, windows_ai_status, WindowsAiLanguageModelProbe,
};

#[cfg(test)]
use easydict_nllb::{MODEL_COMPLETION_SENTINEL, NLLB_MODEL_FILES, OPENVINO_RUNTIME_FILES};

/// Resolved availability of the optional downloadable assets.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SettingsRuntimeStatus {
    pub layout_model: String,
    pub cjk_font: String,
    pub windows_ai_status: String,
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

/// Conventional Easydict per-user data directory (`%LOCALAPPDATA%/Easydict`).
fn data_directory() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Easydict")
}

fn is_open_vino_supported_current_architecture() -> bool {
    cfg!(target_os = "windows") && cfg!(target_arch = "x86_64")
}

pub fn open_vino_cache_status_for_settings(settings: &SettingsSnapshot) -> OpenVinoCacheStatus {
    let base = settings.cache_dir_path().unwrap_or_else(data_directory);
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

    if NllbModelPaths::from_cache_base(base).is_cache_complete() {
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
    let mut windows_ai_client = default_windows_ai_language_model_client();
    load_runtime_status_for_settings_with_windows_ai_probe(settings, &mut windows_ai_client)
}

pub fn load_runtime_status_for_settings_with_windows_ai_probe<P>(
    settings: SettingsSnapshot,
    windows_ai_probe: &mut P,
) -> SettingsRuntimeStatus
where
    P: WindowsAiLanguageModelProbe,
{
    let dir = settings.cache_dir_path().unwrap_or_else(data_directory);
    let foundry_local_status = foundry_local_status_for_settings_without_probe(&settings);
    let windows_ai_status = windows_ai_status_from_probe(windows_ai_probe);
    status_for_directory_with_open_vino_support_and_foundry_status(
        &dir,
        is_open_vino_supported_current_architecture(),
        windows_ai_status,
        foundry_local_status,
    )
}

/// Testable core that checks a given base directory.
pub fn status_for_directory(base: &Path) -> SettingsRuntimeStatus {
    let mut windows_ai_client = default_windows_ai_language_model_client();
    status_for_directory_with_windows_ai_probe(base, &mut windows_ai_client)
}

fn status_for_directory_with_windows_ai_probe<P>(
    base: &Path,
    windows_ai_probe: &mut P,
) -> SettingsRuntimeStatus
where
    P: WindowsAiLanguageModelProbe,
{
    status_for_directory_with_open_vino_support_and_foundry_status(
        base,
        is_open_vino_supported_current_architecture(),
        windows_ai_status_from_probe(windows_ai_probe),
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

fn foundry_local_status_for_settings_without_probe(settings: &SettingsSnapshot) -> String {
    let Some(endpoint) = settings
        .foundry_local_endpoint
        .as_deref()
        .map(str::trim)
        .filter(|endpoint| !endpoint.is_empty())
    else {
        return default_foundry_local_status();
    };

    let endpoint = openai_compatible::normalize_foundry_local_chat_completions_endpoint(endpoint);
    format!("Foundry Local endpoint configured at {endpoint}.")
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

fn windows_ai_status_from_probe<P>(probe: &mut P) -> String
where
    P: WindowsAiLanguageModelProbe,
{
    windows_ai_status(probe).message
}

fn status_for_directory_with_open_vino_support_and_foundry_status(
    base: &Path,
    open_vino_supported: bool,
    windows_ai_status: String,
    foundry_local_status: String,
) -> SettingsRuntimeStatus {
    let layout_model = if layout_model_download::is_layout_model_ready_for_directory(
        base,
        &LayoutModelDownloadConfig::default(),
    ) {
        "Available".to_string()
    } else {
        "Not downloaded".to_string()
    };

    let cjk_font = if font_download::has_any_cjk_font_for_directory(base) {
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
        windows_ai_status,
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
        easydict_windows_ai::status_for_ready_state(
            easydict_windows_ai::WindowsAiReadyState::NotSupportedOnCurrentSystem,
        )
        .message,
        default_foundry_local_status(),
    )
}

#[allow(dead_code)]
#[cfg(test)]
mod tests {
    use super::*;
    use crate::openai_compatible::{
        FoundryLocalEndpointResolver, FoundryLocalRuntimeState, FoundryLocalRuntimeStatus,
    };
    use std::collections::VecDeque;
    use std::fs;
    use std::sync::Mutex;
    use std::time::{SystemTime, UNIX_EPOCH};

    static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

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
        fs::write(dir.join(MODEL_COMPLETION_SENTINEL), b"x").expect("write completion sentinel");
    }

    fn install_open_vino_model(base: &Path) {
        let paths = NllbModelPaths::from_cache_base(base);
        install_complete_file_set(&paths.model_dir, NLLB_MODEL_FILES);
    }

    fn install_open_vino_runtime(base: &Path) {
        let paths = NllbModelPaths::from_cache_base(base);
        install_complete_file_set(&paths.runtime_dir, OPENVINO_RUNTIME_FILES);
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

    struct FakeFoundryRuntimeController {
        status_responses: VecDeque<FoundryLocalRuntimeStatus>,
        endpoint_responses: VecDeque<Option<String>>,
        calls: Vec<&'static str>,
    }

    struct FakeWindowsAiProbe {
        ready_state: easydict_windows_ai::WindowsAiReadyState,
        calls: usize,
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
        ) -> Result<Option<String>, FoundryLocalError> {
            self.calls.push("resolve_endpoint");
            Ok(self.endpoint_responses.pop_front().flatten())
        }
    }

    impl FoundryLocalRuntimeController for FakeFoundryRuntimeController {
        fn get_status(&mut self) -> Result<FoundryLocalRuntimeStatus, FoundryLocalError> {
            self.calls.push("get_status");
            Ok(self.status_responses.pop_front().unwrap_or_else(|| {
                FoundryLocalRuntimeStatus::new(FoundryLocalRuntimeState::Running)
            }))
        }

        fn start_service(&mut self) -> Result<(), FoundryLocalError> {
            self.calls.push("start_service");
            Ok(())
        }

        fn load_model(&mut self, _model: &str) -> Result<(), FoundryLocalError> {
            self.calls.push("load_model");
            Ok(())
        }
    }

    impl FakeWindowsAiProbe {
        fn new(ready_state: easydict_windows_ai::WindowsAiReadyState) -> Self {
            Self {
                ready_state,
                calls: 0,
            }
        }
    }

    impl WindowsAiLanguageModelProbe for FakeWindowsAiProbe {
        fn ready_state(&mut self) -> easydict_windows_ai::WindowsAiReadyState {
            self.calls += 1;
            self.ready_state
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
    fn load_runtime_status_for_settings_does_not_probe_foundry_when_endpoint_is_empty() {
        let dir = temp_status_dir("no-foundry-probe");
        fs::create_dir_all(&dir).expect("create status dir");
        let settings = SettingsSnapshot {
            cache_dir: Some(dir.to_string_lossy().to_string()),
            foundry_local_endpoint: None,
            ..SettingsSnapshot::default()
        };

        let status = load_runtime_status_for_settings(settings);

        assert_eq!(
            status.foundry_local_status,
            "Endpoint auto-detected at runtime"
        );
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn load_runtime_status_for_settings_reports_configured_foundry_endpoint_without_cli_status() {
        let dir = temp_status_dir("configured-foundry-endpoint");
        fs::create_dir_all(&dir).expect("create status dir");
        let settings = SettingsSnapshot {
            cache_dir: Some(dir.to_string_lossy().to_string()),
            foundry_local_endpoint: Some("http://127.0.0.1:5273/status".to_string()),
            ..SettingsSnapshot::default()
        };

        let status = load_runtime_status_for_settings(settings);

        assert_eq!(
            status.foundry_local_status,
            "Foundry Local endpoint configured at http://127.0.0.1:5273/v1/chat/completions."
        );
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn load_runtime_status_for_settings_uses_injected_windows_ai_probe() {
        let dir = temp_status_dir("windows-ai-ready");
        fs::create_dir_all(&dir).expect("create status dir");
        let settings = SettingsSnapshot {
            cache_dir: Some(dir.to_string_lossy().to_string()),
            ..SettingsSnapshot::default()
        };
        let mut probe = FakeWindowsAiProbe::new(easydict_windows_ai::WindowsAiReadyState::Ready);

        let status = load_runtime_status_for_settings_with_windows_ai_probe(settings, &mut probe);

        assert_eq!(status.windows_ai_status, "Phi Silica is ready.");
        assert_eq!(probe.calls, 1);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn status_for_directory_core_uses_injected_windows_ai_probe() {
        let dir = temp_status_dir("directory-windows-ai-ready");
        fs::create_dir_all(&dir).expect("create status dir");
        let mut probe = FakeWindowsAiProbe::new(easydict_windows_ai::WindowsAiReadyState::Ready);

        let status = status_for_directory_with_windows_ai_probe(&dir, &mut probe);

        assert_eq!(status.windows_ai_status, "Phi Silica is ready.");
        assert_eq!(probe.calls, 1);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_not_downloaded_for_unmanaged_layout_onnx_and_font_files() {
        let dir = temp_status_dir("unmanaged-assets");
        let models = dir.join("models");
        let fonts = dir.join("fonts");
        fs::create_dir_all(&models).expect("create unmanaged models dir");
        fs::create_dir_all(&fonts).expect("create unmanaged fonts dir");
        fs::write(models.join("layout.onnx"), b"x").expect("write unmanaged model");
        fs::write(fonts.join("noto.ttf"), b"x").expect("write unmanaged font");

        let status = status_for_directory_with_open_vino_support(&dir, true);

        assert_eq!(status.layout_model, "Not downloaded");
        assert_eq!(status.cjk_font, "Not downloaded");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_available_when_managed_layout_model_assets_are_present() {
        let dir = temp_status_dir("managed-layout-model");
        let paths = layout_model_download::LayoutModelPaths::for_base(&dir);
        fs::create_dir_all(&paths.models_dir).expect("create managed models dir");
        fs::File::create(&paths.native_lib_path)
            .expect("create managed ONNX runtime")
            .set_len(layout_model_download::MIN_RUNTIME_FILE_SIZE)
            .expect("size managed ONNX runtime");
        fs::File::create(&paths.doc_layout_model_path)
            .expect("create managed DocLayout model")
            .set_len(layout_model_download::MIN_DOC_LAYOUT_MODEL_FILE_SIZE)
            .expect("size managed DocLayout model");

        let status = status_for_directory_with_open_vino_support(&dir, true);

        assert_eq!(status.layout_model, "Available");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_available_when_known_cjk_font_asset_is_present() {
        let dir = temp_status_dir("known-cjk-font");
        let fonts = font_download::font_cache_dir(&dir);
        fs::create_dir_all(&fonts).expect("create fonts dir");
        fs::write(fonts.join("NotoSansSC-Regular.ttf"), b"x").expect("write known CJK font");

        let status = status_for_directory_with_open_vino_support(&dir, true);

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
        let model_dir = NllbModelPaths::from_cache_base(&dir).model_dir;
        fs::create_dir_all(&model_dir).expect("create model dir");
        fs::write(model_dir.join(MODEL_COMPLETION_SENTINEL), b"x").expect("write sentinel");
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
    fn open_vino_cache_status_for_settings_treats_blank_cache_dir_as_default() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let local_app_data = temp_status_dir("openvino-settings-blank-cache-dir");
        let default_base = local_app_data.join("Easydict");
        install_open_vino_model(&default_base);
        install_open_vino_runtime(&default_base);
        let _local_app_data_guard =
            EnvironmentVariableGuard::set("LOCALAPPDATA", &local_app_data.to_string_lossy());
        let settings = SettingsSnapshot {
            cache_dir: Some(" \t\r\n ".to_string()),
            ..SettingsSnapshot::default()
        };

        assert_eq!(
            open_vino_cache_status_for_settings(&settings),
            OpenVinoCacheStatus::Ready
        );

        let _ = fs::remove_dir_all(&local_app_data);
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
