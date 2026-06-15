#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(Debug, Eq, PartialEq)]
pub enum WindowsShellError {
    InvalidUrlTarget(String),
    InvalidBundledExecutableName(String),
    CurrentExecutableUnavailable(String),
    CurrentExecutableHasNoParent,
    ProcessLaunchFailed { executable: PathBuf, error: String },
    ProcessExitedWithFailure { executable: PathBuf, status: String },
    InvalidBundledExecutableTarget { executable: PathBuf, reason: String },
    NativeCallFailed { operation: &'static str, code: u32 },
}

impl fmt::Display for WindowsShellError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidUrlTarget(target) => {
                write!(formatter, "invalid URL target: {target}")
            }
            Self::InvalidBundledExecutableName(name) => {
                write!(formatter, "invalid bundled executable name: {name}")
            }
            Self::CurrentExecutableUnavailable(error) => {
                write!(
                    formatter,
                    "failed to resolve current executable path: {error}"
                )
            }
            Self::CurrentExecutableHasNoParent => {
                formatter.write_str("current executable has no parent directory")
            }
            Self::ProcessLaunchFailed { executable, error } => {
                write!(
                    formatter,
                    "failed to run bundled executable {}: {error}",
                    executable.display()
                )
            }
            Self::ProcessExitedWithFailure { executable, status } => {
                write!(
                    formatter,
                    "bundled executable {} exited with {status}",
                    executable.display()
                )
            }
            Self::InvalidBundledExecutableTarget { executable, reason } => {
                write!(
                    formatter,
                    "invalid bundled executable target {}: {reason}",
                    executable.display()
                )
            }
            Self::NativeCallFailed { operation, code } => {
                write!(formatter, "{operation} failed with native error {code}")
            }
        }
    }
}

impl std::error::Error for WindowsShellError {}

pub fn open_url(url: &str) -> Result<(), WindowsShellError> {
    let url = validate_open_url_target(url)?;
    if url.is_empty() {
        return Ok(());
    }

    platform::open_url(url)
}

pub fn run_bundled_executable(
    executable_name: &str,
    arguments: &[String],
) -> Result<(), WindowsShellError> {
    let executable = bundled_executable_path(executable_name)?;
    run_executable(&executable, arguments)
}

pub fn bundled_executable_path(executable_name: &str) -> Result<PathBuf, WindowsShellError> {
    let current_exe = std::env::current_exe()
        .map_err(|error| WindowsShellError::CurrentExecutableUnavailable(error.to_string()))?;
    bundled_executable_path_next_to(&current_exe, executable_name)
}

pub fn bundled_executable_path_next_to(
    current_exe: &Path,
    executable_name: &str,
) -> Result<PathBuf, WindowsShellError> {
    validate_bundled_executable_name(executable_name)?;
    let parent = current_exe
        .parent()
        .ok_or(WindowsShellError::CurrentExecutableHasNoParent)?;
    Ok(parent.join(executable_name))
}

fn run_executable(executable: &Path, arguments: &[String]) -> Result<(), WindowsShellError> {
    validate_bundled_executable_target(executable)?;
    let mut command = Command::new(executable);
    command.args(arguments);
    hide_process_window(&mut command);

    let status = command
        .status()
        .map_err(|error| WindowsShellError::ProcessLaunchFailed {
            executable: executable.to_path_buf(),
            error: error.to_string(),
        })?;
    if !status.success() {
        return Err(WindowsShellError::ProcessExitedWithFailure {
            executable: executable.to_path_buf(),
            status: status.to_string(),
        });
    }

    Ok(())
}

fn validate_bundled_executable_name(executable_name: &str) -> Result<(), WindowsShellError> {
    let trimmed = executable_name.trim();
    let is_plain_file_name = !trimmed.is_empty()
        && trimmed == executable_name
        && !trimmed.contains(['/', '\\', ':'])
        && trimmed != "."
        && trimmed != "..";

    if is_plain_file_name && !bundled_executable_name_is_forbidden(trimmed) {
        Ok(())
    } else {
        Err(WindowsShellError::InvalidBundledExecutableName(
            executable_name.to_string(),
        ))
    }
}

fn bundled_executable_name_is_forbidden(executable_name: &str) -> bool {
    easydict_runtime_guards::command_target_is_retained_runtime_or_script_marker(executable_name)
}

fn validate_open_url_target(url: &str) -> Result<&str, WindowsShellError> {
    let trimmed = url.trim();
    if trimmed.is_empty() {
        return Ok("");
    }

    let lower = trimmed.to_ascii_lowercase();
    let has_allowed_scheme = lower.starts_with("https://") || lower.starts_with("http://");
    if has_allowed_scheme
        && !easydict_runtime_guards::command_target_is_retained_runtime_or_script_marker(trimmed)
    {
        Ok(trimmed)
    } else {
        Err(WindowsShellError::InvalidUrlTarget(url.to_string()))
    }
}

fn validate_bundled_executable_target(executable: &Path) -> Result<(), WindowsShellError> {
    let metadata = fs::symlink_metadata(executable).map_err(|error| {
        WindowsShellError::InvalidBundledExecutableTarget {
            executable: executable.to_path_buf(),
            reason: error.to_string(),
        }
    })?;
    let file_type = metadata.file_type();
    if bundled_executable_target_is_unsupported_by_flags(
        file_type.is_file(),
        file_type.is_symlink(),
        bundled_executable_target_is_reparse_point(&metadata),
    ) {
        return Err(WindowsShellError::InvalidBundledExecutableTarget {
            executable: executable.to_path_buf(),
            reason: "expected a regular non-link executable file".to_string(),
        });
    }

    let bytes = fs::read(executable).map_err(|error| {
        WindowsShellError::InvalidBundledExecutableTarget {
            executable: executable.to_path_buf(),
            reason: error.to_string(),
        }
    })?;
    if easydict_runtime_guards::bytes_contain_retained_runtime_marker(&bytes) {
        return Err(WindowsShellError::InvalidBundledExecutableTarget {
            executable: executable.to_path_buf(),
            reason: "contains retained runtime marker".to_string(),
        });
    }

    Ok(())
}

fn bundled_executable_target_is_unsupported_by_flags(
    is_file: bool,
    is_symlink: bool,
    is_reparse_point: bool,
) -> bool {
    !is_file || is_symlink || is_reparse_point
}

#[cfg(windows)]
fn bundled_executable_target_is_reparse_point(metadata: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0000_0400;
    metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
}

#[cfg(not(windows))]
fn bundled_executable_target_is_reparse_point(metadata: &fs::Metadata) -> bool {
    let _ = metadata;
    false
}

fn hide_process_window(command: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }

    #[cfg(not(windows))]
    {
        let _ = command;
    }
}

#[cfg(windows)]
mod platform {
    use super::WindowsShellError;
    use windows::core::PCWSTR;
    use windows::Win32::UI::Shell::ShellExecuteW;
    use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

    pub fn open_url(url: &str) -> Result<(), WindowsShellError> {
        if url.trim().is_empty() {
            return Ok(());
        }

        let operation = wide_null("open");
        let file = wide_null(url);
        let result = unsafe {
            ShellExecuteW(
                None,
                PCWSTR(operation.as_ptr()),
                PCWSTR(file.as_ptr()),
                PCWSTR::null(),
                PCWSTR::null(),
                SW_SHOWNORMAL,
            )
        };

        if result.0 as isize <= 32 {
            return Err(WindowsShellError::NativeCallFailed {
                operation: "ShellExecuteW",
                code: result.0 as u32,
            });
        }

        Ok(())
    }

    fn wide_null(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }
}

#[cfg(not(windows))]
mod platform {
    use super::WindowsShellError;

    pub fn open_url(url: &str) -> Result<(), WindowsShellError> {
        let _ = url;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bundled_executable_path_resolves_next_to_current_exe() {
        let path = bundled_executable_path_next_to(
            Path::new(r"C:\Program Files\Easydict\Easydict.Rust.exe"),
            "easydict_browser_registrar.exe",
        )
        .expect("plain bundled executable name should resolve");

        assert_eq!(
            path,
            PathBuf::from(r"C:\Program Files\Easydict").join("easydict_browser_registrar.exe")
        );
    }

    #[test]
    fn bundled_executable_name_rejects_paths() {
        for value in [
            "",
            " easydict_browser_registrar.exe",
            "easydict_browser_registrar.exe ",
            ".",
            "..",
            r"..\easydict_browser_registrar.exe",
            r"helpers\easydict_browser_registrar.exe",
            "helpers/easydict_browser_registrar.exe",
            r"C:\Easydict\easydict_browser_registrar.exe",
        ] {
            assert_eq!(
                validate_bundled_executable_name(value),
                Err(WindowsShellError::InvalidBundledExecutableName(
                    value.to_string()
                ))
            );
        }
    }

    #[test]
    fn bundled_executable_name_rejects_retained_runtime_and_script_helpers() {
        for value in [
            "dotnet.exe",
            "dotnet.cmd",
            "cmd.exe",
            "cmd.bat",
            "powershell.exe",
            "PowerShell.CMD",
            "legacy-backend.ps1",
            "legacy-backend.psm1",
            "legacy-backend.cmd",
            "legacy-backend.bat",
            "legacy-backend.vbs",
            "legacy-backend.vbe",
            "legacy-backend.js",
            "legacy-backend.jse",
            "legacy-backend.wsf",
            "legacy-backend.wsh",
            "legacy-backend.hta",
            "pwsh.exe",
            "pwsh.bat",
            "wscript.exe",
            "cscript.exe",
            "mshta.exe",
            "hostfxr.dll",
            "System.Private.CoreLib.dll",
            "Easydict.WinUI.runtimeconfig.json",
            "Easydict.CompatHost.exe",
            "Easydict.NativeBridge.exe",
            "Easydict.Workers.LongDoc.exe",
            "Easydict.Workers.LocalAi.exe",
        ] {
            assert_eq!(
                validate_bundled_executable_name(value),
                Err(WindowsShellError::InvalidBundledExecutableName(
                    value.to_string()
                )),
                "{value} must not be accepted as a bundled Rust helper"
            );
        }
    }

    #[test]
    fn bundled_executable_name_allows_rust_native_helpers() {
        for value in [
            "easydict_browser_registrar.exe",
            "easydict_native_bridge.exe",
            "easydict_cli.exe",
            "easydict_js_native_helper.exe",
            "easydict_hta_script_helper.exe",
            "easydict_json_helper.exe",
        ] {
            validate_bundled_executable_name(value)
                .unwrap_or_else(|error| panic!("{value} should be accepted: {error}"));
        }
    }

    #[test]
    fn bundled_executable_target_rejects_links_reparse_points_and_non_files() {
        assert!(!bundled_executable_target_is_unsupported_by_flags(
            true, false, false
        ));
        assert!(bundled_executable_target_is_unsupported_by_flags(
            false, false, false
        ));
        assert!(bundled_executable_target_is_unsupported_by_flags(
            true, true, false
        ));
        assert!(bundled_executable_target_is_unsupported_by_flags(
            true, false, true
        ));
    }

    #[test]
    fn bundled_executable_target_accepts_regular_file() {
        let dir = unique_temp_dir("regular-helper");
        std::fs::create_dir_all(&dir).expect("create temp dir");
        let exe = dir.join("easydict_browser_registrar.exe");
        std::fs::write(&exe, b"fake rust helper").expect("write helper");

        validate_bundled_executable_target(&exe).expect("regular file should be accepted");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn bundled_executable_target_rejects_retained_runtime_content_markers() {
        let dir = unique_temp_dir("retained-marker-helper");
        std::fs::create_dir_all(&dir).expect("create temp dir");
        let exe = dir.join("easydict_browser_registrar.exe");
        let mut bytes = b"fake helper with stale runtime marker hostfxr.dll\n".to_vec();
        bytes.extend(
            "System.Windows.Forms"
                .encode_utf16()
                .flat_map(u16::to_le_bytes),
        );
        std::fs::write(&exe, bytes).expect("write helper");

        let error = validate_bundled_executable_target(&exe)
            .expect_err("retained runtime content must be rejected before spawn");

        assert_eq!(
            error,
            WindowsShellError::InvalidBundledExecutableTarget {
                executable: exe,
                reason: "contains retained runtime marker".to_string(),
            }
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn blank_url_is_noop() {
        open_url("   ").expect("blank URL should preserve old no-op behavior");
    }

    #[test]
    fn open_url_target_allows_only_http_and_https() {
        assert_eq!(
            validate_open_url_target(" https://example.test/path ").expect("https URL allowed"),
            "https://example.test/path"
        );
        assert_eq!(
            validate_open_url_target("HTTP://example.test").expect("http URL allowed"),
            "HTTP://example.test"
        );
    }

    #[test]
    fn open_url_target_rejects_local_script_and_retained_runtime_targets() {
        for value in [
            "powershell.exe",
            "pwsh -NoProfile",
            "legacy-backend.ps1",
            "file:///C:/Easydict/workers/localai/Easydict.Workers.LocalAi.exe",
            r"C:\Easydict\dotnet\host\fxr\8.0.11\hostfxr.dll",
            "https://example.test/scripts/legacy-backend.ps1",
            "https://example.test/dotnet/host/fxr/8.0.11/hostfxr.dll",
            "https://example.test/workers/localai/Easydict.Workers.LocalAi.exe",
        ] {
            assert_eq!(
                validate_open_url_target(value),
                Err(WindowsShellError::InvalidUrlTarget(value.to_string())),
                "{value} must not be accepted as a ShellExecute URL target"
            );
        }
    }

    fn unique_temp_dir(label: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "easydict-windows-shell-{label}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("system time")
                .as_nanos()
        ))
    }
}
