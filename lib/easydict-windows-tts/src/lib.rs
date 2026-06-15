#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;

#[derive(Debug)]
pub enum WindowsTtsError {
    UnsupportedPlatform,
    ThreadSpawnFailed(String),
    NativeCallFailed {
        operation: &'static str,
        code: i32,
    },
    SapiFailed {
        operation: &'static str,
        message: String,
    },
}

impl fmt::Display for WindowsTtsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => write!(f, "Windows TTS is only available on Windows"),
            Self::ThreadSpawnFailed(message) => {
                write!(f, "failed to start Windows TTS thread: {message}")
            }
            Self::NativeCallFailed { operation, code } => {
                write!(f, "{operation} failed with native error {code}")
            }
            Self::SapiFailed { operation, message } => {
                write!(f, "{operation} failed: {message}")
            }
        }
    }
}

impl std::error::Error for WindowsTtsError {}

#[cfg(windows)]
mod platform {
    use super::WindowsTtsError;
    use std::ffi::c_void;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::sync::mpsc;
    use windows::core::{PCWSTR, PWSTR};
    use windows::Win32::Foundation::{RPC_E_CHANGED_MODE, S_OK, WAIT_OBJECT_0, WAIT_TIMEOUT};
    use windows::Win32::Globalization::LocaleNameToLCID;
    use windows::Win32::Media::Speech::{
        ISpObjectToken, ISpObjectTokenCategory, ISpVoice, SpObjectTokenCategory, SpVoice,
        SPF_ASYNC, SPF_IS_NOT_XML, SPF_PURGEBEFORESPEAK,
    };
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_INPROC_SERVER,
        COINIT_APARTMENTTHREADED,
    };
    use windows::Win32::System::Threading::WaitForSingleObject;

    const SAPI_VOICES_CATEGORY: &str = r"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices";
    const SPEECH_WAIT_POLL_MS: u32 = 50;
    static SPEECH_GENERATION: AtomicU64 = AtomicU64::new(0);

    struct ComApartment {
        should_uninitialize: bool,
    }

    impl ComApartment {
        fn initialize() -> Result<Self, WindowsTtsError> {
            let result = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
            if result.is_ok() {
                Ok(Self {
                    should_uninitialize: true,
                })
            } else if result == RPC_E_CHANGED_MODE {
                Ok(Self {
                    should_uninitialize: false,
                })
            } else {
                Err(WindowsTtsError::NativeCallFailed {
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

    struct SpeechSession {
        voice: ISpVoice,
        generation: u64,
        _com: ComApartment,
    }

    impl SpeechSession {
        fn wait_until_done(self) {
            let complete_event = unsafe { self.voice.SpeakCompleteEvent() };
            if complete_event.is_invalid() {
                let _ = unsafe { self.voice.WaitUntilDone(u32::MAX) };
                return;
            }

            loop {
                match unsafe { WaitForSingleObject(complete_event, SPEECH_WAIT_POLL_MS) } {
                    WAIT_OBJECT_0 => break,
                    WAIT_TIMEOUT => {
                        if speech_is_cancelled(self.generation) {
                            let _ = self.purge_pending_speech();
                            break;
                        }
                    }
                    _ => {
                        let _ = unsafe { self.voice.WaitUntilDone(0) };
                        break;
                    }
                }
            }
        }

        fn purge_pending_speech(&self) -> Result<(), WindowsTtsError> {
            let empty = wide_null("");
            let flags = (SPF_PURGEBEFORESPEAK.0 | SPF_ASYNC.0 | SPF_IS_NOT_XML.0) as u32;
            unsafe {
                self.voice
                    .Speak(PCWSTR(empty.as_ptr()), flags, None)
                    .map_err(|error| sapi_error("ISpVoice::Speak(purge)", error))
            }
        }
    }

    pub fn speak_text(
        text: &str,
        language: Option<&str>,
        speaking_rate: f64,
    ) -> Result<(), WindowsTtsError> {
        let text = text.trim();
        if text.is_empty() {
            return Ok(());
        }

        let text = text.to_string();
        let language = language
            .and_then(normalize_language_tag)
            .filter(|value| !value.eq_ignore_ascii_case("auto"));
        let sapi_rate = sapi_rate_for_speaking_rate(speaking_rate);
        let generation = next_speech_generation();

        let (startup_tx, startup_rx) = mpsc::channel();
        std::thread::Builder::new()
            .name("easydict-windows-tts".to_string())
            .spawn(move || {
                let startup = start_speech_on_current_thread(
                    &text,
                    language.as_deref(),
                    sapi_rate,
                    generation,
                );
                match startup {
                    Ok(session) => {
                        let _ = startup_tx.send(Ok(()));
                        session.wait_until_done();
                    }
                    Err(error) => {
                        let _ = startup_tx.send(Err(error));
                    }
                }
            })
            .map_err(|error| WindowsTtsError::ThreadSpawnFailed(error.to_string()))?;

        startup_rx.recv().unwrap_or_else(|error| {
            Err(WindowsTtsError::SapiFailed {
                operation: "Windows TTS startup",
                message: error.to_string(),
            })
        })
    }

    fn start_speech_on_current_thread(
        text: &str,
        language: Option<&str>,
        sapi_rate: i32,
        generation: u64,
    ) -> Result<SpeechSession, WindowsTtsError> {
        let com = ComApartment::initialize()?;
        let voice: ISpVoice = unsafe {
            CoCreateInstance(&SpVoice, None, CLSCTX_INPROC_SERVER)
                .map_err(|error| sapi_error("CoCreateInstance(SpVoice)", error))?
        };

        if let Some(language) = language {
            if let Some(token) = find_voice_for_language(language)? {
                unsafe {
                    voice
                        .SetVoice(&token)
                        .map_err(|error| sapi_error("ISpVoice::SetVoice", error))?;
                }
            }
        }

        unsafe {
            voice
                .SetRate(sapi_rate)
                .map_err(|error| sapi_error("ISpVoice::SetRate", error))?;
        }

        let text = wide_null(text);
        let flags = (SPF_IS_NOT_XML.0 | SPF_ASYNC.0) as u32;
        unsafe {
            voice
                .Speak(PCWSTR(text.as_ptr()), flags, None)
                .map_err(|error| sapi_error("ISpVoice::Speak", error))?;
        };

        Ok(SpeechSession {
            voice,
            generation,
            _com: com,
        })
    }

    fn find_voice_for_language(language: &str) -> Result<Option<ISpObjectToken>, WindowsTtsError> {
        let Some(target_lcid) = language_lcid(language) else {
            return Ok(None);
        };

        let category: ISpObjectTokenCategory = unsafe {
            CoCreateInstance(&SpObjectTokenCategory, None, CLSCTX_INPROC_SERVER)
                .map_err(|error| sapi_error("CoCreateInstance(SpObjectTokenCategory)", error))?
        };

        let category_id = wide_null(SAPI_VOICES_CATEGORY);
        unsafe {
            category
                .SetId(PCWSTR(category_id.as_ptr()), false)
                .map_err(|error| sapi_error("ISpObjectTokenCategory::SetId", error))?;
        }

        let tokens = unsafe {
            category
                .EnumTokens(PCWSTR::null(), PCWSTR::null())
                .map_err(|error| sapi_error("ISpObjectTokenCategory::EnumTokens", error))?
        };

        let mut count = 0u32;
        unsafe {
            tokens
                .GetCount(&mut count)
                .map_err(|error| sapi_error("IEnumSpObjectTokens::GetCount", error))?;
        }

        for index in 0..count {
            let token = unsafe {
                tokens
                    .Item(index)
                    .map_err(|error| sapi_error("IEnumSpObjectTokens::Item", error))?
            };
            if token_matches_language(&token, target_lcid)? {
                return Ok(Some(token));
            }
        }

        Ok(None)
    }

    fn token_matches_language(
        token: &ISpObjectToken,
        target_lcid: u32,
    ) -> Result<bool, WindowsTtsError> {
        let attributes_key = wide_null("Attributes");
        let attributes = unsafe {
            token
                .OpenKey(PCWSTR(attributes_key.as_ptr()))
                .map_err(|error| sapi_error("ISpObjectToken::OpenKey(Attributes)", error))?
        };

        let language_key = wide_null("Language");
        let value = unsafe {
            attributes
                .GetStringValue(PCWSTR(language_key.as_ptr()))
                .map_err(|error| sapi_error("ISpDataKey::GetStringValue(Language)", error))?
        };
        let value = unsafe { pwstr_to_string_and_free(value) }?;
        Ok(sapi_language_attribute_matches(&value, target_lcid))
    }

    fn language_lcid(language: &str) -> Option<u32> {
        let language = normalize_language_tag(language)?;
        let wide = wide_null(&language);
        let lcid = unsafe { LocaleNameToLCID(PCWSTR(wide.as_ptr()), 0) };
        (lcid != 0).then_some(lcid)
    }

    fn sapi_error(operation: &'static str, error: windows::core::Error) -> WindowsTtsError {
        let code = error.code();
        if code == S_OK {
            WindowsTtsError::SapiFailed {
                operation,
                message: error.message().to_string(),
            }
        } else {
            WindowsTtsError::NativeCallFailed {
                operation,
                code: code.0,
            }
        }
    }

    unsafe fn pwstr_to_string_and_free(value: PWSTR) -> Result<String, WindowsTtsError> {
        if value.is_null() {
            return Ok(String::new());
        }

        let result = value
            .to_string()
            .map_err(|error| WindowsTtsError::SapiFailed {
                operation: "PWSTR::to_string",
                message: error.to_string(),
            });
        CoTaskMemFree(Some(value.as_ptr() as *const c_void));
        result
    }

    fn wide_null(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    pub(super) fn normalize_language_tag(language: &str) -> Option<String> {
        let language = language.trim();
        if language.is_empty() {
            return None;
        }
        Some(language.replace('_', "-"))
    }

    pub(super) fn sapi_language_attribute_matches(attribute: &str, target_lcid: u32) -> bool {
        let target_primary_language = target_lcid & 0x03ff;
        attribute
            .split([';', ',', ' ', '\t'])
            .filter_map(parse_sapi_language_hex)
            .any(|candidate| {
                candidate == target_lcid || (candidate & 0x03ff) == target_primary_language
            })
    }

    fn parse_sapi_language_hex(value: &str) -> Option<u32> {
        let value = value
            .trim()
            .trim_start_matches("0x")
            .trim_start_matches("0X");
        if value.is_empty() {
            return None;
        }
        u32::from_str_radix(value, 16).ok()
    }

    pub(super) fn clamp_speaking_rate(value: f64) -> f64 {
        if value.is_finite() {
            value.clamp(0.5, 3.0)
        } else {
            1.0
        }
    }

    pub(super) fn sapi_rate_for_speaking_rate(value: f64) -> i32 {
        let value = clamp_speaking_rate(value);
        let rate = if value <= 1.0 {
            (value - 1.0) * 20.0
        } else {
            (value - 1.0) * 5.0
        };

        (rate.round() as i32).clamp(-10, 10)
    }

    pub(super) fn next_speech_generation() -> u64 {
        SPEECH_GENERATION.fetch_add(1, Ordering::AcqRel) + 1
    }

    pub(super) fn speech_is_cancelled(generation: u64) -> bool {
        SPEECH_GENERATION.load(Ordering::Acquire) != generation
    }
}

#[cfg(not(windows))]
mod platform {
    use super::WindowsTtsError;

    pub fn speak_text(
        _text: &str,
        _language: Option<&str>,
        _speaking_rate: f64,
    ) -> Result<(), WindowsTtsError> {
        Err(WindowsTtsError::UnsupportedPlatform)
    }
}

pub fn speak_text(text: &str, language: Option<&str>) -> Result<(), WindowsTtsError> {
    speak_text_with_rate(text, language, 1.0)
}

pub fn speak_text_with_rate(
    text: &str,
    language: Option<&str>,
    speaking_rate: f64,
) -> Result<(), WindowsTtsError> {
    platform::speak_text(text, language, speaking_rate)
}

#[cfg(test)]
mod tests {
    #[cfg(windows)]
    use super::platform::{
        clamp_speaking_rate, next_speech_generation, normalize_language_tag,
        sapi_language_attribute_matches, sapi_rate_for_speaking_rate, speech_is_cancelled,
    };
    #[cfg(not(windows))]
    use super::*;

    #[test]
    #[cfg(not(windows))]
    fn speak_text_reports_unsupported_platform() {
        assert!(matches!(
            speak_text("hello", Some("en")),
            Err(WindowsTtsError::UnsupportedPlatform)
        ));
    }

    #[test]
    #[cfg(windows)]
    fn language_tag_normalization_keeps_auto_as_neutral_marker() {
        assert_eq!(normalize_language_tag(" zh_CN "), Some("zh-CN".to_string()));
        assert_eq!(normalize_language_tag("auto"), Some("auto".to_string()));
        assert_eq!(normalize_language_tag("   "), None);
    }

    #[test]
    #[cfg(windows)]
    fn sapi_language_attribute_matches_exact_or_primary_language() {
        assert!(sapi_language_attribute_matches("409;809", 0x0409));
        assert!(sapi_language_attribute_matches("40c", 0x000c));
        assert!(sapi_language_attribute_matches("0x0804", 0x0004));
        assert!(!sapi_language_attribute_matches("411", 0x0409));
    }

    #[test]
    #[cfg(windows)]
    fn speaking_rate_maps_to_sapi_rate_adjustment() {
        assert_eq!(clamp_speaking_rate(f64::NAN), 1.0);
        assert_eq!(sapi_rate_for_speaking_rate(0.1), -10);
        assert_eq!(sapi_rate_for_speaking_rate(0.5), -10);
        assert_eq!(sapi_rate_for_speaking_rate(0.75), -5);
        assert_eq!(sapi_rate_for_speaking_rate(1.0), 0);
        assert_eq!(sapi_rate_for_speaking_rate(1.5), 3);
        assert_eq!(sapi_rate_for_speaking_rate(2.0), 5);
        assert_eq!(sapi_rate_for_speaking_rate(3.0), 10);
        assert_eq!(sapi_rate_for_speaking_rate(9.0), 10);
    }

    #[test]
    #[cfg(windows)]
    fn newer_speech_generation_cancels_previous_session() {
        let first = next_speech_generation();
        assert!(!speech_is_cancelled(first));

        let second = next_speech_generation();
        assert!(speech_is_cancelled(first));
        assert!(!speech_is_cancelled(second));
    }

    #[test]
    #[cfg(windows)]
    fn real_sapi_smoke_speaks_when_enabled() {
        if std::env::var("EASYDICT_WINDOWS_TTS_SMOKE").ok().as_deref() != Some("1") {
            eprintln!("skipping real SAPI smoke; set EASYDICT_WINDOWS_TTS_SMOKE=1 to enable");
            return;
        }

        super::speak_text_with_rate("Easydict text to speech smoke test.", Some("en-US"), 1.0)
            .expect("SAPI should accept a short speech request");
    }
}
