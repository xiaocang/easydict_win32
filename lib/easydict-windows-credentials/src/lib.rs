#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DataProtectionScope {
    CurrentUser,
    LocalMachine,
}

#[derive(Debug, Eq, PartialEq)]
pub enum WindowsCredentialsError {
    UnsupportedPlatform,
    NativeCallFailed { operation: &'static str, code: i32 },
}

impl fmt::Display for WindowsCredentialsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => {
                f.write_str("Windows credentials are only available on Windows")
            }
            Self::NativeCallFailed { operation, code } => {
                write!(f, "{operation} failed with native error {code}")
            }
        }
    }
}

impl std::error::Error for WindowsCredentialsError {}

pub fn protect_data(
    plaintext: &[u8],
    optional_entropy: &[u8],
    scope: DataProtectionScope,
) -> Result<Vec<u8>, WindowsCredentialsError> {
    platform::protect_data(plaintext, optional_entropy, scope)
}

pub fn unprotect_data(
    protected_bytes: &[u8],
    optional_entropy: &[u8],
    scope: DataProtectionScope,
) -> Result<Vec<u8>, WindowsCredentialsError> {
    platform::unprotect_data(protected_bytes, optional_entropy, scope)
}

pub fn read_local_machine_registry_value_string(
    key_path: &str,
    value_name: Option<&str>,
) -> Result<Option<String>, WindowsCredentialsError> {
    platform::read_local_machine_registry_value_string(key_path, value_name)
}

#[cfg(windows)]
mod platform {
    use super::{DataProtectionScope, WindowsCredentialsError};
    use std::ptr::null_mut;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{
        LocalFree, ERROR_FILE_NOT_FOUND, ERROR_SUCCESS, HLOCAL, WIN32_ERROR,
    };
    use windows::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPTPROTECT_LOCAL_MACHINE,
        CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
    };
    use windows::Win32::System::Registry::{
        RegCloseKey, RegOpenKeyExW, RegQueryValueExW, HKEY, HKEY_LOCAL_MACHINE, KEY_READ, REG_SZ,
        REG_VALUE_TYPE,
    };

    pub fn protect_data(
        plaintext: &[u8],
        optional_entropy: &[u8],
        scope: DataProtectionScope,
    ) -> Result<Vec<u8>, WindowsCredentialsError> {
        execute_data_protection(plaintext, optional_entropy, scope, true)
    }

    pub fn unprotect_data(
        protected_bytes: &[u8],
        optional_entropy: &[u8],
        scope: DataProtectionScope,
    ) -> Result<Vec<u8>, WindowsCredentialsError> {
        execute_data_protection(protected_bytes, optional_entropy, scope, false)
    }

    fn execute_data_protection(
        input: &[u8],
        optional_entropy: &[u8],
        scope: DataProtectionScope,
        protect: bool,
    ) -> Result<Vec<u8>, WindowsCredentialsError> {
        let input_blob = blob_from_slice(input);
        let entropy_blob = blob_from_slice(optional_entropy);
        let mut output_blob = LocalBlob::default();
        let flags = data_protection_flags(scope, protect);

        let result = if protect {
            unsafe {
                CryptProtectData(
                    &input_blob,
                    PCWSTR::null(),
                    Some(&entropy_blob),
                    None,
                    None,
                    flags,
                    output_blob.as_mut_ptr(),
                )
            }
        } else {
            unsafe {
                CryptUnprotectData(
                    &input_blob,
                    None,
                    Some(&entropy_blob),
                    None,
                    None,
                    flags,
                    output_blob.as_mut_ptr(),
                )
            }
        };

        result.map_err(|error| native_call_failed(operation_name(protect), error.code().0))?;

        Ok(output_blob.bytes())
    }

    fn operation_name(protect: bool) -> &'static str {
        if protect {
            "CryptProtectData"
        } else {
            "CryptUnprotectData"
        }
    }

    fn data_protection_flags(scope: DataProtectionScope, protect: bool) -> u32 {
        let mut flags = CRYPTPROTECT_UI_FORBIDDEN;
        if protect && scope == DataProtectionScope::LocalMachine {
            flags |= CRYPTPROTECT_LOCAL_MACHINE;
        }
        flags
    }

    fn blob_from_slice(bytes: &[u8]) -> CRYPT_INTEGER_BLOB {
        if bytes.is_empty() {
            return CRYPT_INTEGER_BLOB {
                cbData: 0,
                pbData: null_mut(),
            };
        }

        CRYPT_INTEGER_BLOB {
            cbData: bytes.len() as u32,
            pbData: bytes.as_ptr() as *mut u8,
        }
    }

    fn bytes_from_blob(blob: &CRYPT_INTEGER_BLOB) -> Vec<u8> {
        if blob.pbData.is_null() || blob.cbData == 0 {
            return Vec::new();
        }

        unsafe { std::slice::from_raw_parts(blob.pbData, blob.cbData as usize).to_vec() }
    }

    struct LocalBlob(CRYPT_INTEGER_BLOB);

    impl Default for LocalBlob {
        fn default() -> Self {
            Self(CRYPT_INTEGER_BLOB::default())
        }
    }

    impl LocalBlob {
        fn as_mut_ptr(&mut self) -> *mut CRYPT_INTEGER_BLOB {
            &mut self.0
        }

        fn bytes(&self) -> Vec<u8> {
            bytes_from_blob(&self.0)
        }
    }

    impl Drop for LocalBlob {
        fn drop(&mut self) {
            if !self.0.pbData.is_null() {
                let _ = unsafe { LocalFree(Some(HLOCAL(self.0.pbData.cast()))) };
            }
        }
    }

    pub fn read_local_machine_registry_value_string(
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsCredentialsError> {
        read_registry_value_string(HKEY_LOCAL_MACHINE, key_path, value_name)
    }

    fn read_registry_value_string(
        root: HKEY,
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsCredentialsError> {
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

    fn open_registry_key(
        root: HKEY,
        key_path: &str,
    ) -> Result<Option<RegistryKey>, WindowsCredentialsError> {
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

    fn wide_null(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn win32_error(operation: &'static str, error: WIN32_ERROR) -> WindowsCredentialsError {
        native_call_failed(operation, error.0 as i32)
    }

    fn native_call_failed(operation: &'static str, code: i32) -> WindowsCredentialsError {
        WindowsCredentialsError::NativeCallFailed { operation, code }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::{DataProtectionScope, WindowsCredentialsError};

    pub fn protect_data(
        _plaintext: &[u8],
        _optional_entropy: &[u8],
        _scope: DataProtectionScope,
    ) -> Result<Vec<u8>, WindowsCredentialsError> {
        Err(WindowsCredentialsError::UnsupportedPlatform)
    }

    pub fn unprotect_data(
        _protected_bytes: &[u8],
        _optional_entropy: &[u8],
        _scope: DataProtectionScope,
    ) -> Result<Vec<u8>, WindowsCredentialsError> {
        Err(WindowsCredentialsError::UnsupportedPlatform)
    }

    pub fn read_local_machine_registry_value_string(
        _key_path: &str,
        _value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsCredentialsError> {
        Err(WindowsCredentialsError::UnsupportedPlatform)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    #[test]
    fn current_user_data_protection_roundtrips() {
        let protected = protect_data(
            b"secret",
            b"easydict-test-entropy",
            DataProtectionScope::CurrentUser,
        )
        .expect("DPAPI protect should succeed");

        assert!(!protected.is_empty());
        assert_ne!(protected, b"secret");
        let plaintext = unprotect_data(
            &protected,
            b"easydict-test-entropy",
            DataProtectionScope::CurrentUser,
        )
        .expect("DPAPI unprotect should succeed");
        assert_eq!(plaintext, b"secret");
    }

    #[cfg(windows)]
    #[test]
    fn different_entropy_does_not_unprotect() {
        let protected = protect_data(b"secret", b"entropy-a", DataProtectionScope::CurrentUser)
            .expect("DPAPI protect should succeed");

        assert!(
            unprotect_data(&protected, b"entropy-b", DataProtectionScope::CurrentUser).is_err()
        );
    }

    #[cfg(windows)]
    #[test]
    fn machine_guid_registry_read_is_optional_string() {
        let value = read_local_machine_registry_value_string(
            r"SOFTWARE\Microsoft\Cryptography",
            Some("MachineGuid"),
        )
        .expect("registry read should not fail");

        assert!(value
            .as_deref()
            .is_none_or(|value| !value.trim().is_empty()));
    }
}
