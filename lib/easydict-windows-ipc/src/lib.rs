#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;

#[derive(Debug, Eq, PartialEq)]
pub enum WindowsIpcError {
    UnsupportedPlatform,
    NativeCallFailed { operation: &'static str, code: i32 },
}

impl fmt::Display for WindowsIpcError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => f.write_str("Windows IPC is only available on Windows"),
            Self::NativeCallFailed { operation, code } => {
                write!(f, "{operation} failed with native error {code}")
            }
        }
    }
}

impl std::error::Error for WindowsIpcError {}

pub fn signal_named_event(name: &str) -> Result<bool, WindowsIpcError> {
    platform::signal_named_event(name)
}

#[cfg(windows)]
mod platform {
    use super::WindowsIpcError;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{CloseHandle, HANDLE};
    use windows::Win32::System::Threading::{OpenEventW, SetEvent, EVENT_MODIFY_STATE};

    const HRESULT_FROM_WIN32_ERROR_FILE_NOT_FOUND: i32 = 0x8007_0002_u32 as i32;

    pub fn signal_named_event(name: &str) -> Result<bool, WindowsIpcError> {
        let wide_name = wide_null(name);
        let handle =
            match unsafe { OpenEventW(EVENT_MODIFY_STATE, false, PCWSTR(wide_name.as_ptr())) } {
                Ok(handle) => OwnedHandle(handle),
                Err(error) if error.code().0 == HRESULT_FROM_WIN32_ERROR_FILE_NOT_FOUND => {
                    return Ok(false);
                }
                Err(error) => {
                    return Err(native_call_failed("OpenEventW", error.code().0));
                }
            };

        unsafe { SetEvent(handle.raw()) }
            .map_err(|error| native_call_failed("SetEvent", error.code().0))?;
        Ok(true)
    }

    struct OwnedHandle(HANDLE);

    impl OwnedHandle {
        fn raw(&self) -> HANDLE {
            self.0
        }
    }

    impl Drop for OwnedHandle {
        fn drop(&mut self) {
            if !self.0.is_invalid() {
                let _ = unsafe { CloseHandle(self.0) };
            }
        }
    }

    fn wide_null(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn native_call_failed(operation: &'static str, code: i32) -> WindowsIpcError {
        WindowsIpcError::NativeCallFailed { operation, code }
    }

    #[cfg(test)]
    pub(super) mod test_support {
        use super::{wide_null, OwnedHandle};
        use crate::WindowsIpcError;
        use windows::core::PCWSTR;
        use windows::Win32::Foundation::{WAIT_OBJECT_0, WAIT_TIMEOUT};
        use windows::Win32::System::Threading::{
            CreateEventExW, WaitForSingleObject, CREATE_EVENT, EVENT_ALL_ACCESS,
        };

        pub struct TestNamedEvent {
            _handle: OwnedHandle,
        }

        impl TestNamedEvent {
            pub fn create(name: &str) -> Result<Self, WindowsIpcError> {
                let wide_name = wide_null(name);
                let handle = unsafe {
                    CreateEventExW(
                        None,
                        PCWSTR(wide_name.as_ptr()),
                        CREATE_EVENT(0),
                        EVENT_ALL_ACCESS.0,
                    )
                }
                .map_err(|error| WindowsIpcError::NativeCallFailed {
                    operation: "CreateEventExW",
                    code: error.code().0,
                })?;

                Ok(Self {
                    _handle: OwnedHandle(handle),
                })
            }

            pub fn is_signaled(&self) -> bool {
                (unsafe { WaitForSingleObject(self._handle.raw(), 0) }) == WAIT_OBJECT_0
            }

            pub fn is_not_signaled(&self) -> bool {
                (unsafe { WaitForSingleObject(self._handle.raw(), 0) }) == WAIT_TIMEOUT
            }
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::WindowsIpcError;

    pub fn signal_named_event(_name: &str) -> Result<bool, WindowsIpcError> {
        Err(WindowsIpcError::UnsupportedPlatform)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    #[test]
    fn missing_named_event_returns_false() {
        let name = format!(r"Local\Easydict-WindowsIpc-Missing-{}", std::process::id());

        assert_eq!(signal_named_event(&name), Ok(false));
    }

    #[cfg(windows)]
    #[test]
    fn signal_named_event_sets_existing_event() {
        let name = format!(
            r"Local\Easydict-WindowsIpc-Signal-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        );
        let event = platform::test_support::TestNamedEvent::create(&name)
            .expect("test named event should be created");
        assert!(event.is_not_signaled());

        assert_eq!(signal_named_event(&name), Ok(true));
        assert!(event.is_signaled());
    }

    #[cfg(not(windows))]
    #[test]
    fn signal_named_event_is_unsupported_off_windows() {
        assert_eq!(
            signal_named_event("Local\\Easydict-Test"),
            Err(WindowsIpcError::UnsupportedPlatform)
        );
    }
}
