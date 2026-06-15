use easydict_windows_dialogs::{FileDialogFilter, OpenFileDialogOptions, OpenFolderDialogOptions};

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AppOpenFileDialogOptions {
    pub title: String,
    pub filters: Vec<FileDialogFilter>,
    pub initial_directory: Option<String>,
}

impl AppOpenFileDialogOptions {
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
pub struct AppOpenFolderDialogOptions {
    pub title: String,
    pub initial_directory: Option<String>,
}

impl AppOpenFolderDialogOptions {
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

pub fn file_filter<I, P>(name: impl Into<String>, patterns: I) -> FileDialogFilter
where
    I: IntoIterator<Item = P>,
    P: Into<String>,
{
    FileDialogFilter::new(name, patterns)
}

pub fn open_file_dialog(options: AppOpenFileDialogOptions) -> Option<String> {
    easydict_windows_dialogs::open_file_dialog(OpenFileDialogOptions {
        title: options.title,
        filters: options.filters,
        initial_directory: options.initial_directory,
    })
    .ok()
    .flatten()
}

pub fn open_folder_dialog(options: AppOpenFolderDialogOptions) -> Option<String> {
    easydict_windows_dialogs::open_folder_dialog(OpenFolderDialogOptions {
        title: options.title,
        initial_directory: options.initial_directory,
    })
    .ok()
    .flatten()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn open_file_options_preserve_filters_and_initial_directory() {
        let options = AppOpenFileDialogOptions::new("Open document")
            .filter(file_filter("Markdown", ["*.md", "*.markdown"]))
            .initial_directory(r"C:\Docs");

        assert_eq!(options.title, "Open document");
        assert_eq!(options.filters[0].name, "Markdown");
        assert_eq!(options.filters[0].patterns, ["*.md", "*.markdown"]);
        assert_eq!(options.initial_directory.as_deref(), Some(r"C:\Docs"));
    }

    #[test]
    fn open_folder_options_preserve_initial_directory() {
        let options =
            AppOpenFolderDialogOptions::new("Select output folder").initial_directory(r"C:\Docs");

        assert_eq!(options.title, "Select output folder");
        assert_eq!(options.initial_directory.as_deref(), Some(r"C:\Docs"));
    }
}
