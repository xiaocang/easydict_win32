use std::path::PathBuf;

pub const RUST_APP_DATA_ROOT_NAME: &str = "EasydictRs";
pub const LEGACY_APP_DATA_ROOT_NAME: &str = "Easydict";

pub fn default_user_data_directory() -> PathBuf {
    user_data_directory(RUST_APP_DATA_ROOT_NAME)
}

pub fn legacy_user_data_directory() -> PathBuf {
    user_data_directory(LEGACY_APP_DATA_ROOT_NAME)
}

pub fn user_data_directory(root_name: &str) -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join(root_name)
}
