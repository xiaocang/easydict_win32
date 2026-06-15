use std::path::{Path, PathBuf};

const WINDOWS_APP_SDK_AI_PACKAGE_ID: &str = "microsoft.windowsappsdk.ai";
const WINDOWS_AI_METADATA_ENV: &str = "EASYDICT_WINDOWS_APP_SDK_AI_METADATA_DIR";
const WINDOWS_AI_NUGET_ROOT_ENV: &str = "EASYDICT_WINDOWS_APP_SDK_AI_NUGET_ROOT";
const WINDOWS_AI_REQUIRE_BINDINGS_ENV: &str = "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS";
const WINDOWS_AI_WINMD_FILE: &str = "Microsoft.Windows.AI.winmd";
const WINDOWS_AI_FOUNDATION_WINMD_FILE: &str = "Microsoft.Windows.AI.Foundation.winmd";
const WINDOWS_AI_TEXT_WINMD_FILE: &str = "Microsoft.Windows.AI.Text.winmd";

fn main() {
    println!("cargo:rustc-check-cfg=cfg(easydict_windows_ai_winrt_bindings)");
    println!("cargo:rerun-if-env-changed={WINDOWS_AI_METADATA_ENV}");
    println!("cargo:rerun-if-env-changed={WINDOWS_AI_NUGET_ROOT_ENV}");
    println!("cargo:rerun-if-env-changed={WINDOWS_AI_REQUIRE_BINDINGS_ENV}");
    println!("cargo:rerun-if-env-changed=USERPROFILE");

    if std::env::var("CARGO_CFG_TARGET_OS").ok().as_deref() != Some("windows") {
        return;
    }

    let require_bindings = windows_ai_winrt_bindings_required();

    let Some(winmd) = find_windows_ai_winmd_set() else {
        emit_windows_ai_binding_failure(
            &format!(
                "Windows App SDK AI WinMD metadata was not found; set \
                 {WINDOWS_AI_METADATA_ENV} or {WINDOWS_AI_NUGET_ROOT_ENV}"
            ),
            require_bindings,
        );
        return;
    };

    let Some(out_dir) = std::env::var_os("OUT_DIR").map(PathBuf::from) else {
        emit_windows_ai_binding_failure(
            "OUT_DIR is missing; cannot generate Windows AI WinRT bindings",
            require_bindings,
        );
        return;
    };
    let output_file = out_dir.join("windows_ai_bindings.rs");
    let args = windows_ai_bindgen_args(&winmd, &output_file);

    let warnings = windows_bindgen::bindgen(args);
    if warnings.is_empty() {
        println!("cargo:rustc-cfg=easydict_windows_ai_winrt_bindings");
    } else {
        emit_windows_ai_binding_failure(
            &format!("Windows AI binding generation produced warnings: {warnings}"),
            require_bindings,
        );
    }
}

fn windows_ai_winrt_bindings_required() -> bool {
    std::env::var(WINDOWS_AI_REQUIRE_BINDINGS_ENV)
        .ok()
        .is_some_and(|value| env_flag_is_enabled(&value))
}

fn env_flag_is_enabled(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "1" | "true" | "yes" | "on"
    )
}

fn emit_windows_ai_binding_failure(message: &str, require_bindings: bool) {
    if require_bindings {
        panic!(
            "{message}; {WINDOWS_AI_REQUIRE_BINDINGS_ENV}=1 requires generated Windows AI \
             WinRT bindings for rs portable/release builds"
        );
    }

    println!(
        "cargo:warning={message}; easydict_windows_ai will compile with the unsupported \
         WinRT fallback"
    );
}

struct WindowsAiWinmdSet {
    metadata_dir: PathBuf,
}

impl WindowsAiWinmdSet {
    fn from_metadata_dir(metadata_dir: impl AsRef<Path>) -> Option<Self> {
        let metadata_dir = metadata_dir.as_ref();
        let windows_ai = metadata_dir.join(WINDOWS_AI_WINMD_FILE);
        let windows_ai_foundation = metadata_dir.join(WINDOWS_AI_FOUNDATION_WINMD_FILE);
        let windows_ai_text = metadata_dir.join(WINDOWS_AI_TEXT_WINMD_FILE);
        (windows_ai.is_file() && windows_ai_foundation.is_file() && windows_ai_text.is_file()).then(
            || Self {
                metadata_dir: metadata_dir.to_path_buf(),
            },
        )
    }
}

fn find_windows_ai_winmd_set() -> Option<WindowsAiWinmdSet> {
    if let Some(metadata_dir) = std::env::var_os(WINDOWS_AI_METADATA_ENV).map(PathBuf::from) {
        if let Some(set) = WindowsAiWinmdSet::from_metadata_dir(metadata_dir) {
            return Some(set);
        }
    }

    let package_root = std::env::var_os(WINDOWS_AI_NUGET_ROOT_ENV)
        .map(PathBuf::from)
        .or_else(default_windows_app_sdk_ai_nuget_root)?;
    find_latest_windows_ai_winmd_set_under(&package_root)
}

fn find_latest_windows_ai_winmd_set_under(package_root: &Path) -> Option<WindowsAiWinmdSet> {
    let mut candidates = std::fs::read_dir(package_root)
        .ok()?
        .flatten()
        .filter_map(|entry| {
            let path = entry.path();
            if !path.is_dir() {
                return None;
            }
            let version = path.file_name()?.to_string_lossy().to_string();
            let metadata_dir = path.join("metadata");
            WindowsAiWinmdSet::from_metadata_dir(&metadata_dir).map(|set| (version, set))
        })
        .collect::<Vec<_>>();

    candidates.sort_by(|left, right| compare_version_strings(&left.0, &right.0));
    candidates.pop().map(|(_, set)| set)
}

fn windows_ai_bindgen_args(winmd: &WindowsAiWinmdSet, output_file: &Path) -> Vec<String> {
    vec![
        "--in".to_string(),
        "default".to_string(),
        winmd.metadata_dir.to_string_lossy().to_string(),
        "--out".to_string(),
        output_file.to_string_lossy().to_string(),
        "--no-allow".to_string(),
        "--filter".to_string(),
        "Microsoft.Windows.AI.AIFeatureReadyResult".to_string(),
        "Microsoft.Windows.AI.AIFeatureReadyResultState".to_string(),
        "Microsoft.Windows.AI.AIFeatureReadyState".to_string(),
        "Microsoft.Windows.AI.ContentSafety".to_string(),
        "Microsoft.Windows.AI.Foundation".to_string(),
        "Microsoft.Windows.AI.Text".to_string(),
    ]
}

fn default_windows_app_sdk_ai_nuget_root() -> Option<PathBuf> {
    std::env::var_os("USERPROFILE")
        .map(PathBuf::from)
        .map(|home| {
            home.join(".nuget")
                .join("packages")
                .join(WINDOWS_APP_SDK_AI_PACKAGE_ID)
        })
        .filter(|path| path.is_dir())
}

fn compare_version_strings(left: &str, right: &str) -> std::cmp::Ordering {
    let left_parts = version_parts(left);
    let right_parts = version_parts(right);
    left_parts.cmp(&right_parts).then_with(|| left.cmp(right))
}

fn version_parts(value: &str) -> Vec<u64> {
    value
        .split(|character: char| !character.is_ascii_digit())
        .filter(|part| !part.is_empty())
        .map(|part| part.parse::<u64>().unwrap_or(0))
        .collect()
}
