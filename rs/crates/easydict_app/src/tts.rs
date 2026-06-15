use std::fmt;

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum TextToSpeechOutcome {
    Spoken,
    SkippedEmptyText,
}

const DEFAULT_SPEAKING_RATE: f64 = 1.0;
const MIN_SPEAKING_RATE: f64 = 0.5;
const MAX_SPEAKING_RATE: f64 = 3.0;

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
    fn speak_text(
        &self,
        text: &str,
        language: Option<&str>,
        speaking_rate: f64,
    ) -> Result<(), TextToSpeechError>;
}

#[derive(Clone, Copy, Debug, Default)]
pub struct NativeTextToSpeechBackend;

impl TextToSpeechBackend for NativeTextToSpeechBackend {
    fn speak_text(
        &self,
        text: &str,
        language: Option<&str>,
        speaking_rate: f64,
    ) -> Result<(), TextToSpeechError> {
        easydict_windows_tts::speak_text_with_rate(text, language, speaking_rate)
            .map_err(|error| TextToSpeechError::Backend(error.to_string()))
    }
}

pub fn speak_text(
    text: String,
    language: Option<String>,
    speaking_rate: f64,
) -> Result<TextToSpeechOutcome, TextToSpeechError> {
    speak_text_with_backend(&NativeTextToSpeechBackend, text, language, speaking_rate)
}

pub fn speak_text_with_backend<B: TextToSpeechBackend>(
    backend: &B,
    text: String,
    language: Option<String>,
    speaking_rate: f64,
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

    backend.speak_text(text, language, clamp_speaking_rate(speaking_rate))?;
    Ok(TextToSpeechOutcome::Spoken)
}

pub fn parse_speaking_rate(value: &str) -> f64 {
    value
        .trim()
        .parse::<f64>()
        .map(clamp_speaking_rate)
        .unwrap_or(DEFAULT_SPEAKING_RATE)
}

fn clamp_speaking_rate(value: f64) -> f64 {
    if value.is_finite() {
        value.clamp(MIN_SPEAKING_RATE, MAX_SPEAKING_RATE)
    } else {
        DEFAULT_SPEAKING_RATE
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct FakeTextToSpeechBackend {
        calls: std::sync::Mutex<Vec<(String, Option<String>, f64)>>,
        error: Option<String>,
    }

    impl FakeTextToSpeechBackend {
        fn calls(&self) -> Vec<(String, Option<String>, f64)> {
            self.calls.lock().expect("calls lock").clone()
        }
    }

    impl TextToSpeechBackend for FakeTextToSpeechBackend {
        fn speak_text(
            &self,
            text: &str,
            language: Option<&str>,
            speaking_rate: f64,
        ) -> Result<(), TextToSpeechError> {
            if let Some(error) = &self.error {
                return Err(TextToSpeechError::Backend(error.clone()));
            }

            self.calls.lock().expect("calls lock").push((
                text.to_string(),
                language.map(str::to_string),
                speaking_rate,
            ));
            Ok(())
        }
    }

    #[test]
    fn speak_text_skips_empty_text_before_backend() {
        let backend = FakeTextToSpeechBackend::default();
        let outcome =
            speak_text_with_backend(&backend, "   ".to_string(), Some("fr".to_string()), 1.5)
                .expect("empty text should be skipped");

        assert_eq!(outcome, TextToSpeechOutcome::SkippedEmptyText);
        assert!(backend.calls().is_empty());
    }

    #[test]
    fn speak_text_trims_text_filters_auto_language_and_clamps_speed() {
        let backend = FakeTextToSpeechBackend::default();
        let outcome = speak_text_with_backend(
            &backend,
            "  bonjour  ".to_string(),
            Some(" auto ".to_string()),
            5.0,
        )
        .expect("speech should succeed");

        assert_eq!(outcome, TextToSpeechOutcome::Spoken);
        assert_eq!(backend.calls(), vec![("bonjour".to_string(), None, 3.0)]);
    }

    #[test]
    fn speak_text_passes_language_and_backend_errors() {
        let backend = FakeTextToSpeechBackend {
            calls: std::sync::Mutex::new(Vec::new()),
            error: Some("voice unavailable".to_string()),
        };

        let error = speak_text_with_backend(
            &backend,
            "hello".to_string(),
            Some("en-US".to_string()),
            0.75,
        )
        .expect_err("backend error should propagate");

        assert_eq!(error.to_string(), "voice unavailable");
    }

    #[test]
    fn parse_speaking_rate_uses_dotnet_compatible_bounds() {
        assert_eq!(parse_speaking_rate("0.1"), 0.5);
        assert_eq!(parse_speaking_rate("0.75"), 0.75);
        assert_eq!(parse_speaking_rate("1.5"), 1.5);
        assert_eq!(parse_speaking_rate("9.0"), 3.0);
        assert_eq!(parse_speaking_rate("not-a-number"), 1.0);
        assert_eq!(clamp_speaking_rate(f64::INFINITY), 1.0);
    }
}
