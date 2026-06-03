use std::fmt;

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

#[cfg(windows)]
pub fn recognize_bgra_file(
    pixel_data_path: &str,
    pixel_width: u32,
    pixel_height: u32,
    preferred_language_tag: Option<&str>,
) -> Result<WindowsOcrResult, WindowsOcrError> {
    use std::fs;
    use windows::Globalization::Language;
    use windows::Graphics::Imaging::{BitmapAlphaMode, BitmapPixelFormat, SoftwareBitmap};
    use windows::Media::Ocr::{OcrEngine, OcrLine};
    use windows::Storage::Streams::DataWriter;

    fn map_winrt_error(error: windows::core::Error) -> WindowsOcrError {
        WindowsOcrError::new(format!("Windows Native OCR failed: {error}"))
    }

    fn non_auto_language(value: &str) -> Option<&str> {
        let trimmed = value.trim();
        (!trimmed.is_empty() && !trimmed.eq_ignore_ascii_case("auto")).then_some(trimmed)
    }

    fn create_engine(preferred_language_tag: Option<&str>) -> Option<OcrEngine> {
        if let Some(tag) = preferred_language_tag.and_then(non_auto_language) {
            let tag = windows::core::HSTRING::from(tag);
            if let Ok(language) = Language::CreateLanguage(&tag) {
                if let Ok(engine) = OcrEngine::TryCreateFromLanguage(&language) {
                    return Some(engine);
                }
            }
        }

        OcrEngine::TryCreateFromUserProfileLanguages().ok()
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

    let writer = DataWriter::new().map_err(map_winrt_error)?;
    writer.WriteBytes(&pixel_data).map_err(map_winrt_error)?;
    let buffer = writer.DetachBuffer().map_err(map_winrt_error)?;

    let width = i32::try_from(pixel_width)
        .map_err(|_| WindowsOcrError::new("OCR image dimensions are too large"))?;
    let height = i32::try_from(pixel_height)
        .map_err(|_| WindowsOcrError::new("OCR image dimensions are too large"))?;
    let bitmap = SoftwareBitmap::CreateWithAlpha(
        BitmapPixelFormat::Bgra8,
        width,
        height,
        BitmapAlphaMode::Premultiplied,
    )
    .map_err(map_winrt_error)?;
    bitmap.CopyFromBuffer(&buffer).map_err(map_winrt_error)?;
    drop(pixel_data);

    let Some(engine) = create_engine(preferred_language_tag) else {
        return Ok(WindowsOcrResult {
            lines: Vec::new(),
            text_angle: None,
            detected_language: None,
        });
    };

    let win_result = engine
        .RecognizeAsync(&bitmap)
        .map_err(map_winrt_error)?
        .join()
        .map_err(map_winrt_error)?;
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
