#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;
use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(Debug, Eq, PartialEq)]
pub enum WindowsShellError {
    InvalidBundledExecutableName(String),
    CurrentExecutableUnavailable(String),
    CurrentExecutableHasNoParent,
    ProcessLaunchFailed { executable: PathBuf, error: String },
    ProcessExitedWithFailure { executable: PathBuf, status: String },
    NativeCallFailed { operation: &'static str, code: u32 },
}

impl fmt::Display for WindowsShellError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
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
            Self::NativeCallFailed { operation, code } => {
                write!(formatter, "{operation} failed with native error {code}")
            }
        }
    }
}

impl std::error::Error for WindowsShellError {}

pub fn open_url(url: &str) -> Result<(), WindowsShellError> {
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

    if is_plain_file_name {
        Ok(())
    } else {
        Err(WindowsShellError::InvalidBundledExecutableName(
            executable_name.to_string(),
        ))
    }
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
    fn blank_url_is_noop() {
        open_url("   ").expect("blank URL should preserve old no-op behavior");
    }
}
