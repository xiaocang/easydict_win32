#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;

#[derive(Debug, Eq, PartialEq)]
pub enum WindowsRegistryError {
    UnsupportedPlatform,
    NativeCallFailed { operation: &'static str, code: u32 },
}

impl fmt::Display for WindowsRegistryError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => {
                f.write_str("Windows registry is only available on Windows")
            }
            Self::NativeCallFailed { operation, code } => {
                write!(f, "{operation} failed with native error {code}")
            }
        }
    }
}

impl std::error::Error for WindowsRegistryError {}

pub fn write_current_user_default_string(
    key_path: &str,
    value: &str,
) -> Result<(), WindowsRegistryError> {
    platform::write_current_user_default_string(key_path, value)
}

pub fn write_current_user_string_value(
    key_path: &str,
    value_name: Option<&str>,
    value: &str,
) -> Result<(), WindowsRegistryError> {
    platform::write_current_user_string_value(key_path, value_name, value)
}

pub fn read_current_user_default_string(
    key_path: &str,
) -> Result<Option<String>, WindowsRegistryError> {
    platform::read_current_user_default_string(key_path)
}

pub fn read_current_user_string_value(
    key_path: &str,
    value_name: Option<&str>,
) -> Result<Option<String>, WindowsRegistryError> {
    platform::read_current_user_string_value(key_path, value_name)
}

pub fn delete_current_user_key(key_path: &str) -> Result<(), WindowsRegistryError> {
    platform::delete_current_user_key(key_path)
}

pub fn delete_current_user_tree(key_path: &str) -> Result<(), WindowsRegistryError> {
    platform::delete_current_user_tree(key_path)
}

pub fn delete_current_user_value(
    key_path: &str,
    value_name: Option<&str>,
) -> Result<(), WindowsRegistryError> {
    platform::delete_current_user_value(key_path, value_name)
}

#[cfg(windows)]
mod platform {
    use super::WindowsRegistryError;
    use std::ptr::null_mut;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_SUCCESS, WIN32_ERROR};
    use windows::Win32::System::Registry::{
        RegCloseKey, RegCreateKeyExW, RegDeleteKeyW, RegDeleteTreeW, RegDeleteValueW,
        RegOpenKeyExW, RegQueryValueExW, RegSetValueExW, HKEY, HKEY_CURRENT_USER, KEY_READ,
        KEY_SET_VALUE, REG_OPEN_CREATE_OPTIONS, REG_SZ, REG_VALUE_TYPE,
    };

    pub fn write_current_user_default_string(
        key_path: &str,
        value: &str,
    ) -> Result<(), WindowsRegistryError> {
        write_current_user_string_value(key_path, None, value)
    }

    pub fn write_current_user_string_value(
        key_path: &str,
        value_name: Option<&str>,
        value: &str,
    ) -> Result<(), WindowsRegistryError> {
        let key = create_current_user_key(key_path)?;
        let wide_value_name = value_name.map(wide_null);
        let value_name_ptr = wide_value_name
            .as_ref()
            .map_or(PCWSTR::null(), |value| PCWSTR(value.as_ptr()));
        let wide_value = wide_null(value);
        let bytes = wide_value
            .iter()
            .flat_map(|unit| unit.to_le_bytes())
            .collect::<Vec<_>>();

        let result =
            unsafe { RegSetValueExW(key.raw(), value_name_ptr, None, REG_SZ, Some(&bytes)) };
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegSetValueExW", result));
        }

        Ok(())
    }

    pub fn read_current_user_default_string(
        key_path: &str,
    ) -> Result<Option<String>, WindowsRegistryError> {
        read_current_user_string_value(key_path, None)
    }

    pub fn read_current_user_string_value(
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsRegistryError> {
        read_registry_value_string(HKEY_CURRENT_USER, key_path, value_name)
    }

    pub fn delete_current_user_key(key_path: &str) -> Result<(), WindowsRegistryError> {
        let wide_path = wide_null(key_path);
        let result = unsafe { RegDeleteKeyW(HKEY_CURRENT_USER, PCWSTR(wide_path.as_ptr())) };
        if result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND {
            return Ok(());
        }

        Err(win32_error("RegDeleteKeyW", result))
    }

    pub fn delete_current_user_tree(key_path: &str) -> Result<(), WindowsRegistryError> {
        let wide_path = wide_null(key_path);
        let result = unsafe { RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(wide_path.as_ptr())) };
        if result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND {
            return Ok(());
        }

        Err(win32_error("RegDeleteTreeW", result))
    }

    pub fn delete_current_user_value(
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<(), WindowsRegistryError> {
        let Some(key) = open_current_user_key_for_set_value(key_path)? else {
            return Ok(());
        };
        let wide_value_name = value_name.map(wide_null);
        let value_name_ptr = wide_value_name
            .as_ref()
            .map_or(PCWSTR::null(), |value| PCWSTR(value.as_ptr()));

        let result = unsafe { RegDeleteValueW(key.raw(), value_name_ptr) };
        if result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND {
            return Ok(());
        }

        Err(win32_error("RegDeleteValueW", result))
    }

    fn read_registry_value_string(
        root: HKEY,
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsRegistryError> {
        let Some(key) = open_registry_key(root, key_path)? else {
            return Ok(None);
        };

        let wide_value_name = value_name.map(wide_null);
        let value_name_ptr = wide_value_name
            .as_ref()
            .map_or(PCWSTR::null(), |value| PCWSTR(value.as_ptr()));
        let mut value_type = REG_VALUE_TYPE(0);
        let mut byte_count = 0_u32;
        let result = unsafe {
            RegQueryValueExW(
                key.raw(),
                value_name_ptr,
                None,
                Some(&mut value_type),
                None,
                Some(&mut byte_count),
            )
        };
        if result == ERROR_FILE_NOT_FOUND {
            return Ok(None);
        }
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegQueryValueExW", result));
        }
        if value_type != REG_SZ || byte_count == 0 {
            return Ok(None);
        }

        let mut bytes = vec![0_u8; byte_count as usize];
        let result = unsafe {
            RegQueryValueExW(
                key.raw(),
                value_name_ptr,
                None,
                Some(&mut value_type),
                Some(bytes.as_mut_ptr()),
                Some(&mut byte_count),
            )
        };
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegQueryValueExW", result));
        }

        let mut units = bytes
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .collect::<Vec<_>>();
        while units.last() == Some(&0) {
            units.pop();
        }

        Ok(Some(String::from_utf16_lossy(&units)))
    }

    struct RegistryKey(HKEY);

    impl RegistryKey {
        fn raw(&self) -> HKEY {
            self.0
        }
    }

    impl Drop for RegistryKey {
        fn drop(&mut self) {
            if !self.0.is_invalid() {
                let _ = unsafe { RegCloseKey(self.0) };
            }
        }
    }

    fn create_current_user_key(key_path: &str) -> Result<RegistryKey, WindowsRegistryError> {
        let wide_path = wide_null(key_path);
        let mut key = HKEY(null_mut());
        let result = unsafe {
            RegCreateKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(wide_path.as_ptr()),
                None,
                PCWSTR::null(),
                REG_OPEN_CREATE_OPTIONS(0),
                KEY_SET_VALUE,
                None,
                &mut key,
                None,
            )
        };
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegCreateKeyExW", result));
        }

        Ok(RegistryKey(key))
    }

    fn open_registry_key(
        root: HKEY,
        key_path: &str,
    ) -> Result<Option<RegistryKey>, WindowsRegistryError> {
        let wide_path = wide_null(key_path);
        let mut key = HKEY(null_mut());
        let result =
            unsafe { RegOpenKeyExW(root, PCWSTR(wide_path.as_ptr()), None, KEY_READ, &mut key) };
        if result == ERROR_FILE_NOT_FOUND {
            return Ok(None);
        }
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegOpenKeyExW", result));
        }

        Ok(Some(RegistryKey(key)))
    }

    fn open_current_user_key_for_set_value(
        key_path: &str,
    ) -> Result<Option<RegistryKey>, WindowsRegistryError> {
        let wide_path = wide_null(key_path);
        let mut key = HKEY(null_mut());
        let result = unsafe {
            RegOpenKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(wide_path.as_ptr()),
                None,
                KEY_SET_VALUE,
                &mut key,
            )
        };
        if result == ERROR_FILE_NOT_FOUND {
            return Ok(None);
        }
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegOpenKeyExW", result));
        }

        Ok(Some(RegistryKey(key)))
    }

    fn wide_null(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn win32_error(operation: &'static str, error: WIN32_ERROR) -> WindowsRegistryError {
        WindowsRegistryError::NativeCallFailed {
            operation,
            code: error.0,
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::WindowsRegistryError;

    pub fn write_current_user_default_string(
        _key_path: &str,
        _value: &str,
    ) -> Result<(), WindowsRegistryError> {
        Err(WindowsRegistryError::UnsupportedPlatform)
    }

    pub fn write_current_user_string_value(
        _key_path: &str,
        _value_name: Option<&str>,
        _value: &str,
    ) -> Result<(), WindowsRegistryError> {
        Err(WindowsRegistryError::UnsupportedPlatform)
    }

    pub fn read_current_user_default_string(
        _key_path: &str,
    ) -> Result<Option<String>, WindowsRegistryError> {
        Err(WindowsRegistryError::UnsupportedPlatform)
    }

    pub fn read_current_user_string_value(
        _key_path: &str,
        _value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsRegistryError> {
        Err(WindowsRegistryError::UnsupportedPlatform)
    }

    pub fn delete_current_user_key(_key_path: &str) -> Result<(), WindowsRegistryError> {
        Err(WindowsRegistryError::UnsupportedPlatform)
    }

    pub fn delete_current_user_tree(_key_path: &str) -> Result<(), WindowsRegistryError> {
        Err(WindowsRegistryError::UnsupportedPlatform)
    }

    pub fn delete_current_user_value(
        _key_path: &str,
        _value_name: Option<&str>,
    ) -> Result<(), WindowsRegistryError> {
        Err(WindowsRegistryError::UnsupportedPlatform)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    #[test]
    fn current_user_default_string_roundtrips_and_deletes() {
        let key_path = unique_test_key_path("roundtrip");
        delete_current_user_key(&key_path).expect("pre-test cleanup should succeed");

        assert_eq!(
            read_current_user_default_string(&key_path).expect("missing read should succeed"),
            None
        );
        write_current_user_default_string(&key_path, r"C:\Temp\easydict-native-bridge.json")
            .expect("registry write should succeed");
        assert_eq!(
            read_current_user_default_string(&key_path)
                .expect("registry read should succeed")
                .as_deref(),
            Some(r"C:\Temp\easydict-native-bridge.json")
        );

        delete_current_user_key(&key_path).expect("registry delete should succeed");
        assert_eq!(
            read_current_user_default_string(&key_path).expect("post-delete read should succeed"),
            None
        );
    }

    #[cfg(windows)]
    #[test]
    fn current_user_named_string_and_tree_delete_roundtrip() {
        let key_path = unique_test_key_path("named");
        let child_path = format!(r"{key_path}\command");
        delete_current_user_tree(&key_path).expect("pre-test cleanup should succeed");

        write_current_user_string_value(&key_path, None, "Inspect")
            .expect("default value should be written");
        write_current_user_string_value(&key_path, Some("Icon"), r"C:\Demo\demo.exe")
            .expect("named value should be written");
        write_current_user_string_value(&child_path, None, r#""C:\Demo\demo.exe" --inspect"#)
            .expect("child command should be written");

        assert_eq!(
            read_current_user_default_string(&key_path).expect("default read should succeed"),
            Some("Inspect".to_string())
        );
        assert_eq!(
            read_current_user_string_value(&key_path, Some("Icon"))
                .expect("named read should succeed"),
            Some(r"C:\Demo\demo.exe".to_string())
        );
        assert_eq!(
            read_current_user_default_string(&child_path).expect("child read should succeed"),
            Some(r#""C:\Demo\demo.exe" --inspect"#.to_string())
        );

        delete_current_user_tree(&key_path).expect("tree delete should succeed");
        assert_eq!(
            read_current_user_default_string(&child_path)
                .expect("deleted child read should succeed"),
            None
        );
    }

    #[cfg(windows)]
    #[test]
    fn current_user_named_string_delete_value_roundtrip() {
        let key_path = unique_test_key_path("delete-value");
        delete_current_user_tree(&key_path).expect("pre-test cleanup should succeed");

        write_current_user_string_value(&key_path, Some("Easydict"), r#""C:\Demo\easydict.exe""#)
            .expect("named value should be written");
        assert_eq!(
            read_current_user_string_value(&key_path, Some("Easydict"))
                .expect("named read should succeed"),
            Some(r#""C:\Demo\easydict.exe""#.to_string())
        );

        delete_current_user_value(&key_path, Some("Easydict"))
            .expect("named value delete should succeed");
        assert_eq!(
            read_current_user_string_value(&key_path, Some("Easydict"))
                .expect("deleted value read should succeed"),
            None
        );

        delete_current_user_tree(&key_path).expect("tree cleanup should succeed");
    }

    #[cfg(windows)]
    #[test]
    fn deleting_missing_current_user_key_is_ok() {
        let key_path = unique_test_key_path("missing");

        delete_current_user_key(&key_path).expect("missing key delete should be idempotent");
    }

    #[cfg(windows)]
    fn unique_test_key_path(label: &str) -> String {
        format!(
            r"Software\EasydictRs\Tests\Registry-{}-{}-{label}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("system time")
                .as_nanos()
        )
    }
}
