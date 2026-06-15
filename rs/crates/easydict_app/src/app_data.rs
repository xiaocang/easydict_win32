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
    user_data_directory_from_local_app_data(
        std::env::var_os("LOCALAPPDATA").map(PathBuf::from),
        root_name,
    )
}

fn user_data_directory_from_local_app_data(
    local_app_data: Option<PathBuf>,
    root_name: &str,
) -> PathBuf {
    local_app_data
        .unwrap_or_else(|| PathBuf::from("."))
        .join(root_name)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_and_legacy_roots_remain_separate() {
        assert_eq!(RUST_APP_DATA_ROOT_NAME, "EasydictRs");
        assert_eq!(LEGACY_APP_DATA_ROOT_NAME, "Easydict");
        assert_ne!(RUST_APP_DATA_ROOT_NAME, LEGACY_APP_DATA_ROOT_NAME);
    }

    #[test]
    fn default_user_data_directory_uses_rs_root() {
        let local_app_data = PathBuf::from(r"C:\Users\Test\AppData\Local");

        assert_eq!(
            user_data_directory_from_local_app_data(
                Some(local_app_data.clone()),
                RUST_APP_DATA_ROOT_NAME
            ),
            local_app_data.join("EasydictRs")
        );
        assert_eq!(
            user_data_directory_from_local_app_data(
                Some(local_app_data),
                LEGACY_APP_DATA_ROOT_NAME
            ),
            PathBuf::from(r"C:\Users\Test\AppData\Local").join("Easydict")
        );
    }

    #[test]
    fn missing_local_app_data_falls_back_to_relative_root() {
        assert_eq!(
            user_data_directory_from_local_app_data(None, RUST_APP_DATA_ROOT_NAME),
            PathBuf::from(".").join("EasydictRs")
        );
    }
}
