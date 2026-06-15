#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FileDialogFilter {
    pub name: String,
    pub patterns: Vec<String>,
}

impl FileDialogFilter {
    pub fn new<I, P>(name: impl Into<String>, patterns: I) -> Self
    where
        I: IntoIterator<Item = P>,
        P: Into<String>,
    {
        Self {
            name: name.into(),
            patterns: patterns.into_iter().map(Into::into).collect(),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenFileDialogOptions {
    pub title: String,
    pub filters: Vec<FileDialogFilter>,
    pub initial_directory: Option<String>,
}

impl OpenFileDialogOptions {
    pub fn new(title: impl Into<String>) -> Self {
        Self {
            title: title.into(),
            filters: Vec::new(),
            initial_directory: None,
        }
    }

    pub fn filter(mut self, filter: FileDialogFilter) -> Self {
        self.filters.push(filter);
        self
    }

    pub fn initial_directory(mut self, directory: impl Into<String>) -> Self {
        self.initial_directory = Some(directory.into());
        self
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OpenFolderDialogOptions {
    pub title: String,
    pub initial_directory: Option<String>,
}

impl OpenFolderDialogOptions {
    pub fn new(title: impl Into<String>) -> Self {
        Self {
            title: title.into(),
            initial_directory: None,
        }
    }

    pub fn initial_directory(mut self, directory: impl Into<String>) -> Self {
        self.initial_directory = Some(directory.into());
        self
    }
}

#[derive(Debug)]
pub enum WindowsDialogError {
    UnsupportedPlatform,
    ThreadSpawnFailed(String),
    ThreadPanicked,
    NativeCallFailed { operation: &'static str, code: i32 },
    Utf16PathFailed,
}

impl fmt::Display for WindowsDialogError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => write!(f, "Windows dialogs are only available on Windows"),
            Self::ThreadSpawnFailed(message) => {
                write!(f, "failed to start Windows dialog thread: {message}")
            }
            Self::ThreadPanicked => write!(f, "Windows dialog thread panicked"),
            Self::NativeCallFailed { operation, code } => {
                write!(f, "{operation} failed with native error {code}")
            }
            Self::Utf16PathFailed => write!(f, "selected Windows path was not valid UTF-16"),
        }
    }
}

impl std::error::Error for WindowsDialogError {}

pub fn open_file_dialog(
    options: OpenFileDialogOptions,
) -> Result<Option<String>, WindowsDialogError> {
    platform::open_file_dialog(options)
}

pub fn open_folder_dialog(
    options: OpenFolderDialogOptions,
) -> Result<Option<String>, WindowsDialogError> {
    platform::open_folder_dialog(options)
}

#[cfg(windows)]
mod platform {
    use super::{
        FileDialogFilter, OpenFileDialogOptions, OpenFolderDialogOptions, WindowsDialogError,
    };
    use std::path::Path;
    use windows::core::{HRESULT, PCWSTR, PWSTR};
    use windows::Win32::Foundation::{RPC_E_CHANGED_MODE, S_OK};
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_INPROC_SERVER,
        COINIT_APARTMENTTHREADED,
    };
    use windows::Win32::UI::Shell::Common::COMDLG_FILTERSPEC;
    use windows::Win32::UI::Shell::{
        FileOpenDialog, IFileOpenDialog, IShellItem, SHCreateItemFromParsingName,
        FOS_FILEMUSTEXIST, FOS_FORCEFILESYSTEM, FOS_PATHMUSTEXIST, FOS_PICKFOLDERS,
        SIGDN_FILESYSPATH,
    };

    const HRESULT_FROM_WIN32_ERROR_CANCELLED: HRESULT = HRESULT(0x800704C7u32 as i32);

    struct ComApartment {
        should_uninitialize: bool,
    }

    impl ComApartment {
        fn initialize() -> Result<Self, WindowsDialogError> {
            let result = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
            if result == S_OK {
                Ok(Self {
                    should_uninitialize: true,
                })
            } else if result == RPC_E_CHANGED_MODE {
                Ok(Self {
                    should_uninitialize: false,
                })
            } else if result.is_ok() {
                Ok(Self {
                    should_uninitialize: true,
                })
            } else {
                Err(WindowsDialogError::NativeCallFailed {
                    operation: "CoInitializeEx",
                    code: result.0,
                })
            }
        }
    }

    impl Drop for ComApartment {
        fn drop(&mut self) {
            if self.should_uninitialize {
                unsafe { CoUninitialize() };
            }
        }
    }

    pub fn open_file_dialog(
        options: OpenFileDialogOptions,
    ) -> Result<Option<String>, WindowsDialogError> {
        run_on_dialog_thread("easydict-open-file-dialog", move || {
            open_file_dialog_on_current_thread(options)
        })
    }

    pub fn open_folder_dialog(
        options: OpenFolderDialogOptions,
    ) -> Result<Option<String>, WindowsDialogError> {
        run_on_dialog_thread("easydict-open-folder-dialog", move || {
            open_folder_dialog_on_current_thread(options)
        })
    }

    fn run_on_dialog_thread<F>(
        name: &'static str,
        work: F,
    ) -> Result<Option<String>, WindowsDialogError>
    where
        F: FnOnce() -> Result<Option<String>, WindowsDialogError> + Send + 'static,
    {
        std::thread::Builder::new()
            .name(name.to_string())
            .spawn(work)
            .map_err(|error| WindowsDialogError::ThreadSpawnFailed(error.to_string()))?
            .join()
            .map_err(|_| WindowsDialogError::ThreadPanicked)?
    }

    fn open_file_dialog_on_current_thread(
        options: OpenFileDialogOptions,
    ) -> Result<Option<String>, WindowsDialogError> {
        let _com = ComApartment::initialize()?;
        let dialog: IFileOpenDialog = unsafe {
            CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)
                .map_err(|error| native_error("CoCreateInstance(FileOpenDialog)", error.code()))?
        };

        set_title(&dialog, &options.title)?;
        set_existing_initial_folder(&dialog, options.initial_directory.as_deref())?;
        set_file_dialog_filters(&dialog, &options.filters)?;
        set_options(
            &dialog,
            FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_FILEMUSTEXIST,
        )?;

        show_dialog_and_get_path(&dialog)
    }

    fn open_folder_dialog_on_current_thread(
        options: OpenFolderDialogOptions,
    ) -> Result<Option<String>, WindowsDialogError> {
        let _com = ComApartment::initialize()?;
        let dialog: IFileOpenDialog = unsafe {
            CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)
                .map_err(|error| native_error("CoCreateInstance(FileOpenDialog)", error.code()))?
        };

        set_title(&dialog, &options.title)?;
        set_existing_initial_folder(&dialog, options.initial_directory.as_deref())?;
        set_options(
            &dialog,
            FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_PICKFOLDERS,
        )?;

        show_dialog_and_get_path(&dialog)
    }

    fn set_title(dialog: &IFileOpenDialog, title: &str) -> Result<(), WindowsDialogError> {
        let title = wide_null(title);
        unsafe { dialog.SetTitle(PCWSTR(title.as_ptr())) }
            .map_err(|error| native_error("IFileDialog::SetTitle", error.code()))
    }

    fn set_options(
        dialog: &IFileOpenDialog,
        options: windows::Win32::UI::Shell::FILEOPENDIALOGOPTIONS,
    ) -> Result<(), WindowsDialogError> {
        let existing = unsafe { dialog.GetOptions() }
            .map_err(|error| native_error("IFileDialog::GetOptions", error.code()))?;
        unsafe { dialog.SetOptions(existing | options) }
            .map_err(|error| native_error("IFileDialog::SetOptions", error.code()))
    }

    fn set_existing_initial_folder(
        dialog: &IFileOpenDialog,
        initial_directory: Option<&str>,
    ) -> Result<(), WindowsDialogError> {
        let Some(directory) = initial_directory
            .map(str::trim)
            .filter(|value| !value.is_empty())
        else {
            return Ok(());
        };

        if !Path::new(directory).is_dir() {
            return Ok(());
        }

        let directory = wide_null(directory);
        let item: IShellItem = unsafe {
            SHCreateItemFromParsingName(PCWSTR(directory.as_ptr()), None)
                .map_err(|error| native_error("SHCreateItemFromParsingName", error.code()))?
        };

        unsafe { dialog.SetFolder(&item) }
            .map_err(|error| native_error("IFileDialog::SetFolder", error.code()))
    }

    fn set_file_dialog_filters(
        dialog: &IFileOpenDialog,
        filters: &[FileDialogFilter],
    ) -> Result<(), WindowsDialogError> {
        let prepared = PreparedFilterSpecs::new(filters);
        if prepared.specs.is_empty() {
            return Ok(());
        }

        unsafe { dialog.SetFileTypes(&prepared.specs) }
            .map_err(|error| native_error("IFileDialog::SetFileTypes", error.code()))?;
        unsafe { dialog.SetFileTypeIndex(1) }
            .map_err(|error| native_error("IFileDialog::SetFileTypeIndex", error.code()))
    }

    fn show_dialog_and_get_path(
        dialog: &IFileOpenDialog,
    ) -> Result<Option<String>, WindowsDialogError> {
        match unsafe { dialog.Show(None) } {
            Ok(()) => {}
            Err(error) if error.code() == HRESULT_FROM_WIN32_ERROR_CANCELLED => return Ok(None),
            Err(error) => return Err(native_error("IFileDialog::Show", error.code())),
        }

        let result = unsafe { dialog.GetResult() }
            .map_err(|error| native_error("IFileDialog::GetResult", error.code()))?;
        shell_item_file_system_path(&result).map(Some)
    }

    fn shell_item_file_system_path(item: &IShellItem) -> Result<String, WindowsDialogError> {
        let path = unsafe { item.GetDisplayName(SIGDN_FILESYSPATH) }
            .map_err(|error| native_error("IShellItem::GetDisplayName", error.code()))?;
        let path_guard = CoTaskMemString(path);
        unsafe { path_guard.0.to_string() }.map_err(|_| WindowsDialogError::Utf16PathFailed)
    }

    struct CoTaskMemString(PWSTR);

    impl Drop for CoTaskMemString {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe { CoTaskMemFree(Some(self.0.as_ptr().cast())) };
            }
        }
    }

    struct PreparedFilterSpecs {
        specs: Vec<COMDLG_FILTERSPEC>,
        _storage: Vec<(Vec<u16>, Vec<u16>)>,
    }

    impl PreparedFilterSpecs {
        fn new(filters: &[FileDialogFilter]) -> Self {
            let mut storage = Vec::new();
            let mut specs = Vec::new();

            for filter in filters {
                let name = wide_null(filter.name.trim());
                let pattern = wide_null(&file_dialog_filter_spec(&filter.patterns));
                specs.push(COMDLG_FILTERSPEC {
                    pszName: PCWSTR(name.as_ptr()),
                    pszSpec: PCWSTR(pattern.as_ptr()),
                });
                storage.push((name, pattern));
            }

            Self {
                specs,
                _storage: storage,
            }
        }
    }

    fn file_dialog_filter_spec(patterns: &[String]) -> String {
        let spec = patterns
            .iter()
            .map(|pattern| pattern.trim())
            .filter(|pattern| !pattern.is_empty())
            .collect::<Vec<_>>()
            .join(";");
        if spec.is_empty() {
            "*.*".to_string()
        } else {
            spec
        }
    }

    fn wide_null(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn native_error(operation: &'static str, code: HRESULT) -> WindowsDialogError {
        WindowsDialogError::NativeCallFailed {
            operation,
            code: code.0,
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn file_dialog_filter_spec_joins_patterns_with_semicolons() {
            let patterns = vec![
                "*.md".to_string(),
                "*.markdown".to_string(),
                "  ".to_string(),
            ];

            assert_eq!(file_dialog_filter_spec(&patterns), "*.md;*.markdown");
        }

        #[test]
        fn file_dialog_filter_spec_falls_back_to_all_files() {
            let patterns = vec![" ".to_string()];

            assert_eq!(file_dialog_filter_spec(&patterns), "*.*");
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::{OpenFileDialogOptions, OpenFolderDialogOptions, WindowsDialogError};

    pub fn open_file_dialog(
        _options: OpenFileDialogOptions,
    ) -> Result<Option<String>, WindowsDialogError> {
        Err(WindowsDialogError::UnsupportedPlatform)
    }

    pub fn open_folder_dialog(
        _options: OpenFolderDialogOptions,
    ) -> Result<Option<String>, WindowsDialogError> {
        Err(WindowsDialogError::UnsupportedPlatform)
    }
}
