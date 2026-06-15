use std::fmt;

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum TextToSpeechOutcome {
    Spoken,
    SkippedEmptyText,
}

#[derive(Debug)]
pub enum TextToSpeechError {
    Backend(String),
}

impl fmt::Display for TextToSpeechError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Backend(message) => write!(f, "{message}"),
        }
    }
}

impl std::error::Error for TextToSpeechError {}

pub trait TextToSpeechBackend {
    fn speak_text(&self, text: &str, language: Option<&str>) -> Result<(), TextToSpeechError>;
}

#[derive(Clone, Copy, Debug, Default)]
pub struct NativeTextToSpeechBackend;

impl TextToSpeechBackend for NativeTextToSpeechBackend {
    fn speak_text(&self, text: &str, language: Option<&str>) -> Result<(), TextToSpeechError> {
        easydict_windows_tts::speak_text(text, language)
            .map_err(|error| TextToSpeechError::Backend(error.to_string()))
    }
}

pub fn speak_text(
    text: String,
    language: Option<String>,
) -> Result<TextToSpeechOutcome, TextToSpeechError> {
    speak_text_with_backend(&NativeTextToSpeechBackend, text, language)
}

pub fn speak_text_with_backend<B: TextToSpeechBackend>(
    backend: &B,
    text: String,
    language: Option<String>,
) -> Result<TextToSpeechOutcome, TextToSpeechError> {
    let text = text.trim();
    if text.is_empty() {
        return Ok(TextToSpeechOutcome::SkippedEmptyText);
    }

    let language = language
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .filter(|value| !value.eq_ignore_ascii_case("auto"));

    backend.speak_text(text, language)?;
    Ok(TextToSpeechOutcome::Spoken)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct FakeTextToSpeechBackend {
        calls: std::sync::Mutex<Vec<(String, Option<String>)>>,
        error: Option<String>,
    }

    impl FakeTextToSpeechBackend {
        fn calls(&self) -> Vec<(String, Option<String>)> {
            self.calls.lock().expect("calls lock").clone()
        }
    }

    impl TextToSpeechBackend for FakeTextToSpeechBackend {
        fn speak_text(&self, text: &str, language: Option<&str>) -> Result<(), TextToSpeechError> {
            if let Some(error) = &self.error {
                return Err(TextToSpeechError::Backend(error.clone()));
            }

            self.calls
                .lock()
                .expect("calls lock")
                .push((text.to_string(), language.map(str::to_string)));
            Ok(())
        }
    }

    #[test]
    fn speak_text_skips_empty_text_before_backend() {
        let backend = FakeTextToSpeechBackend::default();
        let outcome = speak_text_with_backend(&backend, "   ".to_string(), Some("fr".to_string()))
            .expect("empty text should be skipped");

        assert_eq!(outcome, TextToSpeechOutcome::SkippedEmptyText);
        assert!(backend.calls().is_empty());
    }

    #[test]
    fn speak_text_trims_text_and_filters_auto_language() {
        let backend = FakeTextToSpeechBackend::default();
        let outcome = speak_text_with_backend(
            &backend,
            "  bonjour  ".to_string(),
            Some(" auto ".to_string()),
        )
        .expect("speech should succeed");

        assert_eq!(outcome, TextToSpeechOutcome::Spoken);
        assert_eq!(backend.calls(), vec![("bonjour".to_string(), None)]);
    }

    #[test]
    fn speak_text_passes_language_and_backend_errors() {
        let backend = FakeTextToSpeechBackend {
            calls: std::sync::Mutex::new(Vec::new()),
            error: Some("voice unavailable".to_string()),
        };

        let error =
            speak_text_with_backend(&backend, "hello".to_string(), Some("en-US".to_string()))
                .expect_err("backend error should propagate");

        assert_eq!(error.to_string(), "voice unavailable");
    }
}
