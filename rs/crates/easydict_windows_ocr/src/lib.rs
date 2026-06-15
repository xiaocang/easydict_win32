use std::fmt;

pub const DEFAULT_WINDOWS_OCR_RECOGNIZE_TIMEOUT_MS: u64 = 30_000;

#[derive(Clone, Debug, PartialEq)]
pub struct WindowsOcrResult {
    pub lines: Vec<WindowsOcrLine>,
    pub text_angle: Option<f64>,
    pub detected_language: Option<WindowsOcrLanguage>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct WindowsOcrLine {
    pub words: Vec<String>,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsOcrLanguage {
    pub tag: String,
    pub display_name: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsOcrError {
    message: String,
}

impl WindowsOcrError {
    fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl fmt::Display for WindowsOcrError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for WindowsOcrError {}

#[derive(Clone, Debug, Eq, PartialEq)]
enum EngineCreationAttempt {
    PreferredLanguage(String),
    UserProfileLanguages,
}

fn engine_creation_attempts(preferred_language_tag: Option<&str>) -> Vec<EngineCreationAttempt> {
    let mut attempts = Vec::with_capacity(2);
    if let Some(tag) = preferred_language_tag.and_then(normalized_preferred_language_tag) {
        attempts.push(EngineCreationAttempt::PreferredLanguage(tag.to_string()));
    }
    attempts.push(EngineCreationAttempt::UserProfileLanguages);
    attempts
}

fn normalized_preferred_language_tag(value: &str) -> Option<&str> {
    let trimmed = value.trim();
    (!trimmed.is_empty() && !trimmed.eq_ignore_ascii_case("auto")).then_some(trimmed)
}

#[cfg(windows)]
pub fn is_available() -> bool {
    windows::Media::Ocr::OcrEngine::TryCreateFromUserProfileLanguages().is_ok()
}

#[cfg(not(windows))]
pub fn is_available() -> bool {
    false
}

#[cfg(windows)]
pub fn available_languages() -> Result<Vec<WindowsOcrLanguage>, WindowsOcrError> {
    use windows::Media::Ocr::OcrEngine;

    fn map_winrt_error(error: windows::core::Error) -> WindowsOcrError {
        WindowsOcrError::new(format!("Windows Native OCR failed: {error}"))
    }

    fn hstring_to_string(value: windows::core::HSTRING) -> String {
        value.to_string_lossy()
    }

    let languages = OcrEngine::AvailableRecognizerLanguages().map_err(map_winrt_error)?;
    let mut output = Vec::with_capacity(languages.Size().map_err(map_winrt_error)? as usize);
    for index in 0..languages.Size().map_err(map_winrt_error)? {
        let language = languages.GetAt(index).map_err(map_winrt_error)?;
        output.push(WindowsOcrLanguage {
            tag: hstring_to_string(language.LanguageTag().map_err(map_winrt_error)?),
            display_name: hstring_to_string(language.DisplayName().map_err(map_winrt_error)?),
        });
    }

    Ok(output)
}

#[cfg(not(windows))]
pub fn available_languages() -> Result<Vec<WindowsOcrLanguage>, WindowsOcrError> {
    Ok(Vec::new())
}

#[cfg(windows)]
pub fn recognize_bgra_file(
    pixel_data_path: &str,
    pixel_width: u32,
    pixel_height: u32,
    preferred_language_tag: Option<&str>,
) -> Result<WindowsOcrResult, WindowsOcrError> {
    use std::fs;
    use std::sync::mpsc::{self, RecvTimeoutError};
    use std::time::Duration;
    use windows::Globalization::Language;
    use windows::Graphics::Imaging::{BitmapAlphaMode, BitmapPixelFormat, SoftwareBitmap};
    use windows::Media::Ocr::{OcrEngine, OcrLine, OcrResult as WinOcrResult};
    use windows::Storage::Streams::DataWriter;

    fn map_winrt_error(error: windows::core::Error) -> WindowsOcrError {
        WindowsOcrError::new(format!("Windows Native OCR failed: {error}"))
    }

    fn create_engine(preferred_language_tag: Option<&str>) -> Option<OcrEngine> {
        for attempt in engine_creation_attempts(preferred_language_tag) {
            match attempt {
                EngineCreationAttempt::PreferredLanguage(tag) => {
                    let tag = windows::core::HSTRING::from(tag);
                    if let Ok(language) = Language::CreateLanguage(&tag) {
                        if let Ok(engine) = OcrEngine::TryCreateFromLanguage(&language) {
                            return Some(engine);
                        }
                    }
                }
                EngineCreationAttempt::UserProfileLanguages => {
                    if let Ok(engine) = OcrEngine::TryCreateFromUserProfileLanguages() {
                        return Some(engine);
                    }
                }
            }
        }

        None
    }

    fn hstring_to_string(value: windows::core::HSTRING) -> String {
        value.to_string_lossy()
    }

    fn convert_line(line: &OcrLine) -> Result<WindowsOcrLine, WindowsOcrError> {
        let words = line.Words().map_err(map_winrt_error)?;
        let mut word_texts = Vec::new();
        let mut min_x = f64::MAX;
        let mut min_y = f64::MAX;
        let mut max_x = f64::MIN;
        let mut max_y = f64::MIN;

        for index in 0..words.Size().map_err(map_winrt_error)? {
            let word = words.GetAt(index).map_err(map_winrt_error)?;
            let text = hstring_to_string(word.Text().map_err(map_winrt_error)?);
            if !text.trim().is_empty() {
                word_texts.push(text);
            }

            let rect = word.BoundingRect().map_err(map_winrt_error)?;
            let x = rect.X as f64;
            let y = rect.Y as f64;
            let width = rect.Width as f64;
            let height = rect.Height as f64;
            min_x = min_x.min(x);
            min_y = min_y.min(y);
            max_x = max_x.max(x + width);
            max_y = max_y.max(y + height);
        }

        if min_x == f64::MAX {
            return Ok(WindowsOcrLine {
                words: word_texts,
                x: 0.0,
                y: 0.0,
                width: 0.0,
                height: 0.0,
            });
        }

        Ok(WindowsOcrLine {
            words: word_texts,
            x: min_x,
            y: min_y,
            width: max_x - min_x,
            height: max_y - min_y,
        })
    }

    let pixel_data = fs::read(pixel_data_path).map_err(|error| {
        WindowsOcrError::new(format!(
            "Could not read OCR pixel data '{pixel_data_path}': {error}"
        ))
    })?;
    validate_bgra_buffer(&pixel_data, pixel_width, pixel_height)?;

    let Some(engine) = create_engine(preferred_language_tag) else {
        return Ok(WindowsOcrResult {
            lines: Vec::new(),
            text_angle: None,
            detected_language: None,
        });
    };

    let max_dimension = OcrEngine::MaxImageDimension().map_err(map_winrt_error)?;
    validate_ocr_engine_image_dimensions(pixel_width, pixel_height, max_dimension)?;

    let width = i32::try_from(pixel_width)
        .map_err(|_| WindowsOcrError::new("OCR image dimensions are too large"))?;
    let height = i32::try_from(pixel_height)
        .map_err(|_| WindowsOcrError::new("OCR image dimensions are too large"))?;

    let writer = DataWriter::new().map_err(map_winrt_error)?;
    writer.WriteBytes(&pixel_data).map_err(map_winrt_error)?;
    let buffer = writer.DetachBuffer().map_err(map_winrt_error)?;

    let bitmap = SoftwareBitmap::CreateWithAlpha(
        BitmapPixelFormat::Bgra8,
        width,
        height,
        BitmapAlphaMode::Premultiplied,
    )
    .map_err(map_winrt_error)?;
    bitmap.CopyFromBuffer(&buffer).map_err(map_winrt_error)?;
    drop(pixel_data);

    let operation = engine.RecognizeAsync(&bitmap).map_err(map_winrt_error)?;
    let (sender, receiver) = mpsc::channel();
    operation
        .when(move |result: Result<WinOcrResult, windows::core::Error>| {
            let _ = sender.send(result);
        })
        .map_err(map_winrt_error)?;
    let win_result = match receiver.recv_timeout(Duration::from_millis(
        DEFAULT_WINDOWS_OCR_RECOGNIZE_TIMEOUT_MS,
    )) {
        Ok(result) => result.map_err(map_winrt_error)?,
        Err(RecvTimeoutError::Timeout) => {
            let _ = operation.Cancel();
            return Err(windows_ocr_timeout_error(
                DEFAULT_WINDOWS_OCR_RECOGNIZE_TIMEOUT_MS,
            ));
        }
        Err(RecvTimeoutError::Disconnected) => {
            return Err(WindowsOcrError::new(
                "Windows Native OCR completion channel closed",
            ));
        }
    };
    let win_lines = win_result.Lines().map_err(map_winrt_error)?;
    let mut lines = Vec::with_capacity(win_lines.Size().map_err(map_winrt_error)? as usize);
    for index in 0..win_lines.Size().map_err(map_winrt_error)? {
        let line = win_lines.GetAt(index).map_err(map_winrt_error)?;
        lines.push(convert_line(&line)?);
    }

    let text_angle = win_result
        .TextAngle()
        .ok()
        .and_then(|angle| angle.Value().ok());
    let detected_language = engine.RecognizerLanguage().ok().and_then(|language| {
        Some(WindowsOcrLanguage {
            tag: hstring_to_string(language.LanguageTag().ok()?),
            display_name: hstring_to_string(language.DisplayName().ok()?),
        })
    });

    Ok(WindowsOcrResult {
        lines,
        text_angle,
        detected_language,
    })
}

#[cfg(not(windows))]
pub fn recognize_bgra_file(
    pixel_data_path: &str,
    pixel_width: u32,
    pixel_height: u32,
    preferred_language_tag: Option<&str>,
) -> Result<WindowsOcrResult, WindowsOcrError> {
    let _ = pixel_data_path;
    let _ = pixel_width;
    let _ = pixel_height;
    let _ = preferred_language_tag;
    Err(WindowsOcrError::new(
        "Windows Native OCR is only available on Windows",
    ))
}

fn validate_bgra_buffer(bgra: &[u8], width: u32, height: u32) -> Result<(), WindowsOcrError> {
    if width == 0 || height == 0 {
        return Err(WindowsOcrError::new("OCR image dimensions are invalid"));
    }

    let width = usize::try_from(width)
        .map_err(|_| WindowsOcrError::new("OCR image dimensions are too large"))?;
    let height = usize::try_from(height)
        .map_err(|_| WindowsOcrError::new("OCR image dimensions are too large"))?;
    let expected = width
        .checked_mul(height)
        .and_then(|pixels| pixels.checked_mul(4))
        .ok_or_else(|| WindowsOcrError::new("OCR image dimensions are too large"))?;

    if bgra.len() < expected {
        return Err(WindowsOcrError::new(format!(
            "OCR image buffer is too short: expected at least {expected} bytes, got {}",
            bgra.len()
        )));
    }

    Ok(())
}

fn validate_ocr_engine_image_dimensions(
    width: u32,
    height: u32,
    max_dimension: u32,
) -> Result<(), WindowsOcrError> {
    if max_dimension == 0 {
        return Err(WindowsOcrError::new(
            "Windows Native OCR reported an invalid maximum image dimension",
        ));
    }

    if width > max_dimension || height > max_dimension {
        return Err(WindowsOcrError::new(format!(
            "OCR image dimensions exceed Windows Native OCR maximum {max_dimension}: {width}x{height}"
        )));
    }

    Ok(())
}

fn windows_ocr_timeout_error(timeout_ms: u64) -> WindowsOcrError {
    WindowsOcrError::new(format!("Windows Native OCR timed out after {timeout_ms}ms"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn engine_creation_attempts_always_fall_back_to_user_profile_languages() {
        assert_eq!(
            engine_creation_attempts(Some(" zh-CN ")),
            vec![
                EngineCreationAttempt::PreferredLanguage("zh-CN".to_string()),
                EngineCreationAttempt::UserProfileLanguages
            ]
        );
        assert_eq!(
            engine_creation_attempts(Some("auto")),
            vec![EngineCreationAttempt::UserProfileLanguages]
        );
        assert_eq!(
            engine_creation_attempts(Some("  ")),
            vec![EngineCreationAttempt::UserProfileLanguages]
        );
        assert_eq!(
            engine_creation_attempts(None),
            vec![EngineCreationAttempt::UserProfileLanguages]
        );
    }

    #[cfg(not(windows))]
    #[test]
    fn non_windows_reports_unavailable_with_empty_language_list() {
        assert!(!is_available());
        assert_eq!(
            available_languages().expect("language status should be queryable"),
            Vec::new()
        );
    }

    #[test]
    fn ocr_engine_image_dimensions_accept_engine_limit() {
        validate_ocr_engine_image_dimensions(1024, 768, 1024)
            .expect("edge dimensions should be accepted");
        validate_ocr_engine_image_dimensions(1, 1, 1)
            .expect("minimum positive edge dimensions should be accepted");
    }

    #[test]
    fn ocr_engine_image_dimensions_reject_width_above_engine_limit() {
        let error = validate_ocr_engine_image_dimensions(1025, 768, 1024)
            .expect_err("width above the engine limit should be rejected");
        assert_eq!(
            error.to_string(),
            "OCR image dimensions exceed Windows Native OCR maximum 1024: 1025x768"
        );
    }

    #[test]
    fn ocr_engine_image_dimensions_reject_height_above_engine_limit() {
        let error = validate_ocr_engine_image_dimensions(640, 1025, 1024)
            .expect_err("height above the engine limit should be rejected");
        assert_eq!(
            error.to_string(),
            "OCR image dimensions exceed Windows Native OCR maximum 1024: 640x1025"
        );
    }

    #[test]
    fn ocr_engine_image_dimensions_reject_invalid_zero_engine_limit() {
        let error = validate_ocr_engine_image_dimensions(1, 1, 0)
            .expect_err("invalid engine limit should be rejected");
        assert_eq!(
            error.to_string(),
            "Windows Native OCR reported an invalid maximum image dimension"
        );
    }

    #[test]
    fn windows_ocr_timeout_error_is_descriptive() {
        assert_eq!(
            DEFAULT_WINDOWS_OCR_RECOGNIZE_TIMEOUT_MS, 30_000,
            "default timeout should stay intentionally generous for screenshot OCR"
        );
        assert_eq!(
            windows_ocr_timeout_error(DEFAULT_WINDOWS_OCR_RECOGNIZE_TIMEOUT_MS).to_string(),
            "Windows Native OCR timed out after 30000ms"
        );
    }

    #[cfg(windows)]
    #[test]
    fn windows_available_languages_smoke_has_valid_shape() {
        let languages = available_languages().expect("Windows OCR language query should not fail");
        for language in languages {
            assert!(!language.tag.trim().is_empty());
            assert!(!language.display_name.trim().is_empty());
        }
    }
}
