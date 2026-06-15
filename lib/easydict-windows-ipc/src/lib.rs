#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;
use std::time::Duration;

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

pub struct NamedEventListener {
    inner: platform::NamedEventListener,
}

impl NamedEventListener {
    pub fn create(name: &str, auto_reset: bool) -> Result<Self, WindowsIpcError> {
        platform::NamedEventListener::create(name, auto_reset).map(|inner| Self { inner })
    }

    pub fn name(&self) -> &str {
        self.inner.name()
    }

    pub fn wait(&self, timeout: Duration) -> Result<bool, WindowsIpcError> {
        self.inner.wait(timeout)
    }
}

#[cfg(all(windows, any(test, feature = "test-support")))]
pub use platform::test_support;

#[cfg(windows)]
mod platform {
    use super::WindowsIpcError;
    use std::time::Duration;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{
        CloseHandle, GetLastError, HANDLE, WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT,
    };
    use windows::Win32::System::Threading::{
        CreateEventExW, OpenEventW, SetEvent, WaitForSingleObject, CREATE_EVENT,
        CREATE_EVENT_MANUAL_RESET, EVENT_MODIFY_STATE, SYNCHRONIZATION_SYNCHRONIZE,
    };

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

    pub struct NamedEventListener {
        name: String,
        handle: OwnedHandle,
    }

    impl NamedEventListener {
        pub fn create(name: &str, auto_reset: bool) -> Result<Self, WindowsIpcError> {
            let wide_name = wide_null(name);
            let flags = if auto_reset {
                CREATE_EVENT(0)
            } else {
                CREATE_EVENT_MANUAL_RESET
            };
            let desired_access = EVENT_MODIFY_STATE.0 | SYNCHRONIZATION_SYNCHRONIZE.0;
            let handle =
                unsafe { CreateEventExW(None, PCWSTR(wide_name.as_ptr()), flags, desired_access) }
                    .map_err(|error| native_call_failed("CreateEventExW", error.code().0))?;

            Ok(Self {
                name: name.to_string(),
                handle: OwnedHandle(handle),
            })
        }

        pub fn name(&self) -> &str {
            &self.name
        }

        pub fn wait(&self, timeout: Duration) -> Result<bool, WindowsIpcError> {
            let timeout_ms = timeout.as_millis().try_into().unwrap_or(u32::MAX);
            match unsafe { WaitForSingleObject(self.handle.raw(), timeout_ms) } {
                WAIT_OBJECT_0 => Ok(true),
                WAIT_TIMEOUT => Ok(false),
                WAIT_FAILED => Err(WindowsIpcError::NativeCallFailed {
                    operation: "WaitForSingleObject",
                    code: unsafe { GetLastError() }.0 as i32,
                }),
                result => Err(WindowsIpcError::NativeCallFailed {
                    operation: "WaitForSingleObject",
                    code: result.0 as i32,
                }),
            }
        }
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

    #[cfg(any(test, feature = "test-support"))]
    pub mod test_support {
        use super::{wide_null, OwnedHandle};
        use crate::WindowsIpcError;
        use std::time::Duration;
        use windows::core::PCWSTR;
        use windows::Win32::Foundation::{GetLastError, WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT};
        use windows::Win32::System::Threading::{
            CreateEventExW, ResetEvent, WaitForSingleObject, CREATE_EVENT, EVENT_ALL_ACCESS,
        };

        pub struct TestNamedEvent {
            name: String,
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
                    name: name.to_string(),
                    _handle: OwnedHandle(handle),
                })
            }

            pub fn name(&self) -> &str {
                &self.name
            }

            pub fn is_signaled(&self) -> bool {
                (unsafe { WaitForSingleObject(self._handle.raw(), 0) }) == WAIT_OBJECT_0
            }

            pub fn is_not_signaled(&self) -> bool {
                (unsafe { WaitForSingleObject(self._handle.raw(), 0) }) == WAIT_TIMEOUT
            }

            pub fn wait_signaled(&self, timeout: Duration) -> Result<bool, WindowsIpcError> {
                let timeout_ms = timeout.as_millis().try_into().unwrap_or(u32::MAX);
                match unsafe { WaitForSingleObject(self._handle.raw(), timeout_ms) } {
                    WAIT_OBJECT_0 => Ok(true),
                    WAIT_TIMEOUT => Ok(false),
                    WAIT_FAILED => Err(WindowsIpcError::NativeCallFailed {
                        operation: "WaitForSingleObject",
                        code: unsafe { GetLastError() }.0 as i32,
                    }),
                    result => Err(WindowsIpcError::NativeCallFailed {
                        operation: "WaitForSingleObject",
                        code: result.0 as i32,
                    }),
                }
            }

            pub fn reset(&self) -> Result<(), WindowsIpcError> {
                unsafe { ResetEvent(self._handle.raw()) }.map_err(|error| {
                    WindowsIpcError::NativeCallFailed {
                        operation: "ResetEvent",
                        code: error.code().0,
                    }
                })
            }

            pub fn drain(&self) -> Result<(), WindowsIpcError> {
                while self.wait_signaled(Duration::ZERO)? {}
                Ok(())
            }
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use std::time::Duration;

    use super::WindowsIpcError;

    pub fn signal_named_event(_name: &str) -> Result<bool, WindowsIpcError> {
        Err(WindowsIpcError::UnsupportedPlatform)
    }

    pub struct NamedEventListener;

    impl NamedEventListener {
        pub fn create(_name: &str, _auto_reset: bool) -> Result<Self, WindowsIpcError> {
            Err(WindowsIpcError::UnsupportedPlatform)
        }

        pub fn name(&self) -> &str {
            ""
        }

        pub fn wait(&self, _timeout: Duration) -> Result<bool, WindowsIpcError> {
            Err(WindowsIpcError::UnsupportedPlatform)
        }
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

    #[cfg(windows)]
    #[test]
    fn named_event_listener_waits_and_auto_resets() {
        let name = format!(
            r"Local\Easydict-WindowsIpc-Listen-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        );
        let listener = NamedEventListener::create(&name, true).expect("listener should create");

        assert_eq!(listener.name(), name);
        assert_eq!(listener.wait(Duration::ZERO), Ok(false));
        assert_eq!(signal_named_event(&name), Ok(true));
        assert_eq!(listener.wait(Duration::from_secs(1)), Ok(true));
        assert_eq!(listener.wait(Duration::ZERO), Ok(false));
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
