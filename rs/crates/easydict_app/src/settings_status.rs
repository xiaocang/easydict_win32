//! Real (filesystem-backed) runtime status for the settings page.
//!
//! Replaces the static placeholder status strings with a genuine on-disk check
//! of the layout-detection model and CJK font under the conventional Easydict
//! data directory. This is the work the settings entry loading overlay waits on.

use std::path::{Path, PathBuf};

/// Resolved availability of the optional downloadable assets.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SettingsRuntimeStatus {
    pub layout_model: String,
    pub cjk_font: String,
}

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

/// Performs the real filesystem check. Reports "Available" when the asset is
/// present on disk and "Not downloaded" otherwise (matching the prior
/// placeholder wording for the not-present case, but now truthful).
pub fn load_runtime_status() -> SettingsRuntimeStatus {
    let dir = data_directory();
    status_for_directory(&dir)
}

/// Testable core that checks a given base directory.
pub fn status_for_directory(base: &Path) -> SettingsRuntimeStatus {
    let layout_model = if directory_has_file_with_extension(&base.join("models"), &["onnx"]) {
        "Available".to_string()
    } else {
        "Not downloaded".to_string()
    };

    let cjk_font = if directory_has_file_with_extension(&base.join("fonts"), &["ttf", "otf", "ttc"])
    {
        "Available".to_string()
    } else {
        "Not downloaded".to_string()
    };

    SettingsRuntimeStatus {
        layout_model,
        cjk_font,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn reports_not_downloaded_when_assets_absent() {
        let dir =
            std::env::temp_dir().join(format!("easydict-status-empty-{}", std::process::id()));
        let _ = fs::create_dir_all(&dir);

        let status = status_for_directory(&dir);

        assert_eq!(status.layout_model, "Not downloaded");
        assert_eq!(status.cjk_font, "Not downloaded");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reports_available_when_model_and_font_present() {
        let dir =
            std::env::temp_dir().join(format!("easydict-status-present-{}", std::process::id()));
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
}
