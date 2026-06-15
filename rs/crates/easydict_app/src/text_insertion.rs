use std::fmt;
use std::sync::{Mutex, OnceLock};

use crate::mouse_selection::EASYDICT_SYNTHETIC_KEY;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextInsertionTarget {
    pub hwnd: isize,
    pub process_id: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum TextInsertionOutcome {
    Captured(TextInsertionTarget),
    Inserted {
        target: TextInsertionTarget,
        text: String,
    },
    SkippedEmptyText,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum TextInsertionError {
    UnsupportedPlatform,
    NoCapturedTarget,
    TargetUnavailable,
    StateUnavailable,
    Backend(String),
}

impl fmt::Display for TextInsertionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => {
                write!(formatter, "text insertion is only available on Windows")
            }
            Self::NoCapturedTarget => {
                write!(formatter, "text insertion target has not been captured")
            }
            Self::TargetUnavailable => write!(formatter, "text insertion target is unavailable"),
            Self::StateUnavailable => write!(formatter, "text insertion state is unavailable"),
            Self::Backend(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for TextInsertionError {}

pub trait TextInsertionBackend {
    fn capture_target(&mut self) -> Result<TextInsertionTarget, TextInsertionError>;
    fn target_is_valid(&mut self, target: &TextInsertionTarget) -> bool;
    fn insert_text(
        &mut self,
        target: &TextInsertionTarget,
        text: &str,
    ) -> Result<(), TextInsertionError>;
}

#[derive(Default)]
pub struct NativeTextInsertionBackend;

impl TextInsertionBackend for NativeTextInsertionBackend {
    fn capture_target(&mut self) -> Result<TextInsertionTarget, TextInsertionError> {
        easydict_windows_text_selection::foreground_text_insertion_target()
            .map(TextInsertionTarget::from)
            .map_err(TextInsertionError::from)
    }

    fn target_is_valid(&mut self, target: &TextInsertionTarget) -> bool {
        easydict_windows_text_selection::text_insertion_target_is_valid(
            &easydict_windows_text_selection::TextInsertionTarget::from(target.clone()),
        )
    }

    fn insert_text(
        &mut self,
        target: &TextInsertionTarget,
        text: &str,
    ) -> Result<(), TextInsertionError> {
        easydict_windows_text_selection::insert_text_into_target(
            &easydict_windows_text_selection::TextInsertionTarget::from(target.clone()),
            text,
            EASYDICT_SYNTHETIC_KEY,
        )
        .map_err(TextInsertionError::from)
    }
}

impl From<easydict_windows_text_selection::TextInsertionTarget> for TextInsertionTarget {
    fn from(target: easydict_windows_text_selection::TextInsertionTarget) -> Self {
        Self {
            hwnd: target.hwnd,
            process_id: target.process_id,
        }
    }
}

impl From<TextInsertionTarget> for easydict_windows_text_selection::TextInsertionTarget {
    fn from(target: TextInsertionTarget) -> Self {
        Self {
            hwnd: target.hwnd,
            process_id: target.process_id,
        }
    }
}

impl From<easydict_windows_text_selection::WindowsTextSelectionError> for TextInsertionError {
    fn from(error: easydict_windows_text_selection::WindowsTextSelectionError) -> Self {
        match error {
            easydict_windows_text_selection::WindowsTextSelectionError::UnsupportedPlatform => {
                Self::UnsupportedPlatform
            }
            easydict_windows_text_selection::WindowsTextSelectionError::InvalidWindow => {
                Self::TargetUnavailable
            }
            other => Self::Backend(other.to_string()),
        }
    }
}

static CAPTURED_TEXT_INSERTION_TARGET: OnceLock<Mutex<Option<TextInsertionTarget>>> =
    OnceLock::new();

pub fn capture_text_insertion_target() -> Result<TextInsertionOutcome, TextInsertionError> {
    let mut backend = NativeTextInsertionBackend;
    capture_text_insertion_target_with_backend(&mut backend)
}

pub fn capture_text_insertion_target_with_backend<B: TextInsertionBackend>(
    backend: &mut B,
) -> Result<TextInsertionOutcome, TextInsertionError> {
    let target = backend.capture_target()?;
    store_captured_text_insertion_target(Some(target.clone()))?;
    Ok(TextInsertionOutcome::Captured(target))
}

pub fn insert_text_into_captured_target(
    text: impl Into<String>,
) -> Result<TextInsertionOutcome, TextInsertionError> {
    let mut backend = NativeTextInsertionBackend;
    insert_text_into_captured_target_with_backend(text, &mut backend)
}

pub fn insert_text_into_captured_target_with_backend<B: TextInsertionBackend>(
    text: impl Into<String>,
    backend: &mut B,
) -> Result<TextInsertionOutcome, TextInsertionError> {
    let text = text.into();
    if text.is_empty() {
        return Ok(TextInsertionOutcome::SkippedEmptyText);
    }

    let target = captured_text_insertion_target()?.ok_or(TextInsertionError::NoCapturedTarget)?;
    if !backend.target_is_valid(&target) {
        return Err(TextInsertionError::TargetUnavailable);
    }

    backend.insert_text(&target, &text)?;
    Ok(TextInsertionOutcome::Inserted { target, text })
}

pub fn store_captured_text_insertion_target(
    target: Option<TextInsertionTarget>,
) -> Result<(), TextInsertionError> {
    let state = CAPTURED_TEXT_INSERTION_TARGET.get_or_init(|| Mutex::new(None));
    let mut state = state
        .lock()
        .map_err(|_| TextInsertionError::StateUnavailable)?;
    *state = target;
    Ok(())
}

pub fn captured_text_insertion_target() -> Result<Option<TextInsertionTarget>, TextInsertionError> {
    let state = CAPTURED_TEXT_INSERTION_TARGET.get_or_init(|| Mutex::new(None));
    state
        .lock()
        .map(|state| state.clone())
        .map_err(|_| TextInsertionError::StateUnavailable)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    static TEST_LOCK: Mutex<()> = Mutex::new(());

    #[derive(Default)]
    struct FakeTextInsertionBackend {
        captured: Option<TextInsertionTarget>,
        valid: bool,
        inserted: Vec<(TextInsertionTarget, String)>,
    }

    impl TextInsertionBackend for FakeTextInsertionBackend {
        fn capture_target(&mut self) -> Result<TextInsertionTarget, TextInsertionError> {
            self.captured
                .clone()
                .ok_or(TextInsertionError::TargetUnavailable)
        }

        fn target_is_valid(&mut self, _target: &TextInsertionTarget) -> bool {
            self.valid
        }

        fn insert_text(
            &mut self,
            target: &TextInsertionTarget,
            text: &str,
        ) -> Result<(), TextInsertionError> {
            self.inserted.push((target.clone(), text.to_string()));
            Ok(())
        }
    }

    #[test]
    fn capture_stores_target_for_later_replace_action() {
        let _guard = TEST_LOCK.lock().expect("text insertion test lock");
        let target = TextInsertionTarget {
            hwnd: 42,
            process_id: 7,
        };
        let mut backend = FakeTextInsertionBackend {
            captured: Some(target.clone()),
            valid: true,
            inserted: Vec::new(),
        };

        let outcome =
            capture_text_insertion_target_with_backend(&mut backend).expect("capture target");

        assert_eq!(outcome, TextInsertionOutcome::Captured(target.clone()));
        assert_eq!(captured_text_insertion_target().unwrap(), Some(target));
    }

    #[test]
    fn insert_uses_captured_target_and_skips_empty_text_before_backend() {
        let _guard = TEST_LOCK.lock().expect("text insertion test lock");
        let target = TextInsertionTarget {
            hwnd: 99,
            process_id: 11,
        };
        store_captured_text_insertion_target(Some(target.clone())).unwrap();
        let mut backend = FakeTextInsertionBackend {
            captured: None,
            valid: true,
            inserted: Vec::new(),
        };

        let empty = insert_text_into_captured_target_with_backend("", &mut backend)
            .expect("empty insert should be a no-op");
        assert_eq!(empty, TextInsertionOutcome::SkippedEmptyText);
        assert!(backend.inserted.is_empty());

        let inserted = insert_text_into_captured_target_with_backend("bonjour", &mut backend)
            .expect("insert text");

        assert_eq!(
            inserted,
            TextInsertionOutcome::Inserted {
                target: target.clone(),
                text: "bonjour".to_string(),
            }
        );
        assert_eq!(backend.inserted, vec![(target, "bonjour".to_string())]);
    }

    #[test]
    fn insert_requires_a_valid_captured_target() {
        let _guard = TEST_LOCK.lock().expect("text insertion test lock");
        store_captured_text_insertion_target(None).unwrap();
        let mut backend = FakeTextInsertionBackend {
            captured: None,
            valid: true,
            inserted: Vec::new(),
        };
        assert_eq!(
            insert_text_into_captured_target_with_backend("bonjour", &mut backend),
            Err(TextInsertionError::NoCapturedTarget)
        );

        store_captured_text_insertion_target(Some(TextInsertionTarget {
            hwnd: 13,
            process_id: 17,
        }))
        .unwrap();
        backend.valid = false;
        assert_eq!(
            insert_text_into_captured_target_with_backend("bonjour", &mut backend),
            Err(TextInsertionError::TargetUnavailable)
        );
        assert!(backend.inserted.is_empty());
    }
}
