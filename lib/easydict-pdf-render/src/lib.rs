use pdfium_render::prelude::*;
use std::collections::BTreeSet;
use std::env;
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

const DEFAULT_DPI: f64 = 144.0;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PdfImageFormat {
    Png,
    Jpg,
}

impl PdfImageFormat {
    pub fn parse(value: &str) -> Result<Self, PdfRenderToolError> {
        match value.trim().to_ascii_lowercase().as_str() {
            "png" => Ok(Self::Png),
            "jpg" | "jpeg" => Ok(Self::Jpg),
            _ => Err(PdfRenderToolError::InvalidArgument(
                "Only png and jpg are supported.".to_string(),
            )),
        }
    }

    pub fn extension(self) -> &'static str {
        match self {
            Self::Png => "png",
            Self::Jpg => "jpg",
        }
    }
}

impl fmt::Display for PdfImageFormat {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.extension())
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfToImagesOptions {
    pub input_pdf: PathBuf,
    pub output_dir: Option<PathBuf>,
    pub dpi: f64,
    pub scale: Option<f64>,
    pub format: PdfImageFormat,
    pub page_selection: Option<String>,
    pub pdfium_dir: Option<PathBuf>,
}

impl PdfToImagesOptions {
    pub fn effective_scale(&self) -> f64 {
        self.scale.unwrap_or(self.dpi / 72.0)
    }

    pub fn effective_dpi(&self) -> f64 {
        self.effective_scale() * 72.0
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RenderedPage {
    pub page_number: usize,
    pub output_path: PathBuf,
}

#[derive(Clone, Debug, PartialEq)]
pub struct RenderSummary {
    pub input_pdf: PathBuf,
    pub output_dir: PathBuf,
    pub page_summary: String,
    pub format: PdfImageFormat,
    pub scale: f64,
    pub effective_dpi: f64,
    pub rendered_pages: Vec<RenderedPage>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfToBgraOptions {
    pub input_pdf: PathBuf,
    pub output_dir: PathBuf,
    pub dpi: f64,
    pub scale: Option<f64>,
    pub page_selection: Option<String>,
    pub pdfium_dir: Option<PathBuf>,
}

impl PdfToBgraOptions {
    pub fn new(input_pdf: impl Into<PathBuf>, output_dir: impl Into<PathBuf>) -> Self {
        Self {
            input_pdf: input_pdf.into(),
            output_dir: output_dir.into(),
            dpi: DEFAULT_DPI,
            scale: None,
            page_selection: None,
            pdfium_dir: None,
        }
    }

    pub fn effective_scale(&self) -> f64 {
        self.scale.unwrap_or(self.dpi / 72.0)
    }

    pub fn effective_dpi(&self) -> f64 {
        self.effective_scale() * 72.0
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RenderedBgraPage {
    pub page_number: usize,
    pub pixel_width: u32,
    pub pixel_height: u32,
    pub pixel_data_path: PathBuf,
}

#[derive(Clone, Debug, PartialEq)]
pub struct BgraRenderSummary {
    pub input_pdf: PathBuf,
    pub output_dir: PathBuf,
    pub page_summary: String,
    pub scale: f64,
    pub effective_dpi: f64,
    pub rendered_pages: Vec<RenderedBgraPage>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfTextExtractionOptions {
    pub input_pdf: PathBuf,
    pub page_selection: Option<String>,
    pub pdfium_dir: Option<PathBuf>,
    pub prefer_loose_bounds: bool,
}

impl PdfTextExtractionOptions {
    pub fn new(input_pdf: impl Into<PathBuf>) -> Self {
        Self {
            input_pdf: input_pdf.into(),
            page_selection: None,
            pdfium_dir: None,
            prefer_loose_bounds: false,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfTextBounds {
    pub left: f64,
    pub bottom: f64,
    pub right: f64,
    pub top: f64,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfTextMatrix {
    pub a: f64,
    pub b: f64,
    pub c: f64,
    pub d: f64,
    pub e: f64,
    pub f: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ExtractedPdfTextChar {
    pub page_number: usize,
    pub char_index: usize,
    pub value: String,
    pub unicode_value: u32,
    pub font_name: String,
    pub scaled_font_size: f64,
    pub unscaled_font_size: f64,
    pub bounds: PdfTextBounds,
    pub origin_x: Option<f64>,
    pub origin_y: Option<f64>,
    pub matrix: Option<PdfTextMatrix>,
    pub angle_degrees: Option<f64>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ExtractedPdfTextPage {
    pub page_number: usize,
    pub width: f64,
    pub height: f64,
    pub chars: Vec<ExtractedPdfTextChar>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfTextExtractionSummary {
    pub input_pdf: PathBuf,
    pub page_summary: String,
    pub pages: Vec<ExtractedPdfTextPage>,
}

#[derive(Debug)]
pub enum PdfRenderToolError {
    InvalidArgument(String),
    Io(String),
    Pdfium(String),
    Image(String),
}

impl fmt::Display for PdfRenderToolError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidArgument(message)
            | Self::Io(message)
            | Self::Pdfium(message)
            | Self::Image(message) => formatter.write_str(message),
        }
    }
}

impl std::error::Error for PdfRenderToolError {}

pub fn parse_pdf_to_images_args<I, S>(args: I) -> Result<PdfToImagesOptions, PdfRenderToolError>
where
    I: IntoIterator<Item = S>,
    S: Into<String>,
{
    let args: Vec<String> = args.into_iter().map(Into::into).collect();
    let mut input_pdf: Option<PathBuf> = None;
    let mut output_dir: Option<PathBuf> = None;
    let mut dpi = DEFAULT_DPI;
    let mut scale: Option<f64> = None;
    let mut format = PdfImageFormat::Png;
    let mut page_selection: Option<String> = None;
    let mut pdfium_dir: Option<PathBuf> = None;

    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--input" | "-i" => {
                input_pdf = Some(normalize_path(read_value(&args, &mut index, "--input")?));
            }
            "--output-dir" | "-o" => {
                output_dir = Some(normalize_path(read_value(
                    &args,
                    &mut index,
                    "--output-dir",
                )?));
            }
            "--dpi" => {
                dpi = parse_positive_f64(read_value(&args, &mut index, "--dpi")?, "--dpi")?;
            }
            "--scale" => {
                scale = Some(parse_positive_f64(
                    read_value(&args, &mut index, "--scale")?,
                    "--scale",
                )?);
            }
            "--format" | "-f" => {
                format = PdfImageFormat::parse(read_value(&args, &mut index, "--format")?)?;
            }
            "--page" => {
                let page =
                    parse_positive_usize(read_value(&args, &mut index, "--page")?, "--page")?;
                page_selection = Some(page.to_string());
            }
            "--page-range" | "--pages" => {
                page_selection = Some(read_value(&args, &mut index, "--page-range")?.to_string());
            }
            "--pdfium-dir" => {
                pdfium_dir = Some(normalize_path(read_value(
                    &args,
                    &mut index,
                    "--pdfium-dir",
                )?));
            }
            value if value.starts_with('-') => {
                return Err(PdfRenderToolError::InvalidArgument(format!(
                    "Unknown argument: {value}"
                )));
            }
            value => {
                if input_pdf.is_none() {
                    input_pdf = Some(normalize_path(value));
                }
            }
        }

        index += 1;
    }

    let input_pdf = input_pdf
        .ok_or_else(|| PdfRenderToolError::InvalidArgument("Input PDF is required.".to_string()))?;

    Ok(PdfToImagesOptions {
        input_pdf,
        output_dir,
        dpi,
        scale,
        format,
        page_selection,
        pdfium_dir,
    })
}

pub fn render_pdf_to_images(
    options: &PdfToImagesOptions,
) -> Result<RenderSummary, PdfRenderToolError> {
    if !options.input_pdf.exists() {
        return Err(PdfRenderToolError::Io(format!(
            "Input PDF not found: {}",
            options.input_pdf.display()
        )));
    }

    let scale = options.effective_scale();
    if scale <= 0.0 {
        return Err(PdfRenderToolError::InvalidArgument(
            "Scale must be greater than 0.".to_string(),
        ));
    }

    let output_dir = options
        .output_dir
        .clone()
        .unwrap_or_else(|| build_default_output_dir(&options.input_pdf));
    fs::create_dir_all(&output_dir).map_err(|error| {
        PdfRenderToolError::Io(format!(
            "Failed to create output directory {}: {error}",
            output_dir.display()
        ))
    })?;

    let pdfium = bind_pdfium(options.pdfium_dir.as_deref())?;
    let document = pdfium
        .load_pdf_from_file(&options.input_pdf, None)
        .map_err(|error| {
            PdfRenderToolError::Pdfium(format!(
                "Failed to open PDF {}: {error}",
                options.input_pdf.display()
            ))
        })?;
    let total_pages = document.pages().len() as usize;
    let selected_pages = resolve_selected_pages(options.page_selection.as_deref(), total_pages)?;
    let pages_to_render: Vec<usize> = selected_pages
        .clone()
        .unwrap_or_else(|| (1..=total_pages).collect());
    let page_summary = format_page_summary(selected_pages.as_deref(), total_pages);
    let base_name = options
        .input_pdf
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("document");
    let render_config = PdfRenderConfig::new()
        .scale_page_by_factor(scale as f32)
        .render_annotations(true)
        .render_form_data(true);

    let mut rendered_pages = Vec::with_capacity(pages_to_render.len());
    for page_number in pages_to_render {
        let page_index = (page_number - 1) as PdfPageIndex;
        let output_path = output_dir.join(format!(
            "{base_name}_p{page_number:04}.{}",
            options.format.extension()
        ));
        let page = document.pages().get(page_index).map_err(|error| {
            PdfRenderToolError::Pdfium(format!("Failed to load page {page_number}: {error}"))
        })?;
        let image = page
            .render_with_config(&render_config)
            .and_then(|bitmap| bitmap.as_image())
            .map_err(|error| {
                PdfRenderToolError::Pdfium(format!("Failed to render page {page_number}: {error}"))
            })?;
        let save_result = match options.format {
            PdfImageFormat::Png => image.save(&output_path),
            PdfImageFormat::Jpg => image.into_rgb8().save(&output_path),
        };
        save_result.map_err(|error| {
            PdfRenderToolError::Image(format!(
                "Failed to save page {page_number} to {}: {error}",
                output_path.display()
            ))
        })?;

        rendered_pages.push(RenderedPage {
            page_number,
            output_path,
        });
    }

    Ok(RenderSummary {
        input_pdf: options.input_pdf.clone(),
        output_dir,
        page_summary,
        format: options.format,
        scale,
        effective_dpi: options.effective_dpi(),
        rendered_pages,
    })
}

pub fn render_pdf_pages_to_bgra_files(
    options: &PdfToBgraOptions,
) -> Result<BgraRenderSummary, PdfRenderToolError> {
    if !options.input_pdf.exists() {
        return Err(PdfRenderToolError::Io(format!(
            "Input PDF not found: {}",
            options.input_pdf.display()
        )));
    }

    let scale = options.effective_scale();
    if scale <= 0.0 {
        return Err(PdfRenderToolError::InvalidArgument(
            "Scale must be greater than 0.".to_string(),
        ));
    }

    fs::create_dir_all(&options.output_dir).map_err(|error| {
        PdfRenderToolError::Io(format!(
            "Failed to create output directory {}: {error}",
            options.output_dir.display()
        ))
    })?;

    let pdfium = bind_pdfium(options.pdfium_dir.as_deref())?;
    let document = pdfium
        .load_pdf_from_file(&options.input_pdf, None)
        .map_err(|error| {
            PdfRenderToolError::Pdfium(format!(
                "Failed to open PDF {}: {error}",
                options.input_pdf.display()
            ))
        })?;
    let total_pages = document.pages().len() as usize;
    let selected_pages = resolve_selected_pages(options.page_selection.as_deref(), total_pages)?;
    let pages_to_render: Vec<usize> = selected_pages
        .clone()
        .unwrap_or_else(|| (1..=total_pages).collect());
    let page_summary = format_page_summary(selected_pages.as_deref(), total_pages);
    let base_name = options
        .input_pdf
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("document");
    let render_config = PdfRenderConfig::new()
        .scale_page_by_factor(scale as f32)
        .set_reverse_byte_order(false)
        .render_annotations(true)
        .render_form_data(true);

    let mut rendered_pages = Vec::with_capacity(pages_to_render.len());
    for page_number in pages_to_render {
        let page_index = (page_number - 1) as PdfPageIndex;
        let page = document.pages().get(page_index).map_err(|error| {
            PdfRenderToolError::Pdfium(format!("Failed to load page {page_number}: {error}"))
        })?;
        let bitmap = page.render_with_config(&render_config).map_err(|error| {
            PdfRenderToolError::Pdfium(format!("Failed to render page {page_number}: {error}"))
        })?;
        let pixel_width = u32::try_from(bitmap.width()).map_err(|_| {
            PdfRenderToolError::Image(format!("Rendered page {page_number} width is invalid"))
        })?;
        let pixel_height = u32::try_from(bitmap.height()).map_err(|_| {
            PdfRenderToolError::Image(format!("Rendered page {page_number} height is invalid"))
        })?;
        let pixel_data = bitmap.as_raw_bytes();
        let expected_bytes = (pixel_width as usize)
            .checked_mul(pixel_height as usize)
            .and_then(|pixels| pixels.checked_mul(4))
            .ok_or_else(|| {
                PdfRenderToolError::Image(format!(
                    "Rendered page {page_number} dimensions are too large"
                ))
            })?;
        if pixel_data.len() < expected_bytes {
            return Err(PdfRenderToolError::Image(format!(
                "Rendered page {page_number} BGRA buffer is too short: expected at least {expected_bytes} bytes, got {}",
                pixel_data.len()
            )));
        }

        let pixel_data_path = options
            .output_dir
            .join(format!("{base_name}_p{page_number:04}.bgra"));
        fs::write(&pixel_data_path, &pixel_data[..expected_bytes]).map_err(|error| {
            PdfRenderToolError::Image(format!(
                "Failed to save page {page_number} BGRA data to {}: {error}",
                pixel_data_path.display()
            ))
        })?;

        rendered_pages.push(RenderedBgraPage {
            page_number,
            pixel_width,
            pixel_height,
            pixel_data_path,
        });
    }

    Ok(BgraRenderSummary {
        input_pdf: options.input_pdf.clone(),
        output_dir: options.output_dir.clone(),
        page_summary,
        scale,
        effective_dpi: options.effective_dpi(),
        rendered_pages,
    })
}

pub fn extract_pdf_text_chars(
    options: &PdfTextExtractionOptions,
) -> Result<PdfTextExtractionSummary, PdfRenderToolError> {
    if !options.input_pdf.exists() {
        return Err(PdfRenderToolError::Io(format!(
            "Input PDF not found: {}",
            options.input_pdf.display()
        )));
    }

    let pdfium = bind_pdfium(options.pdfium_dir.as_deref())?;
    let document = pdfium
        .load_pdf_from_file(&options.input_pdf, None)
        .map_err(|error| {
            PdfRenderToolError::Pdfium(format!(
                "Failed to open PDF {}: {error}",
                options.input_pdf.display()
            ))
        })?;
    let total_pages = document.pages().len() as usize;
    let selected_pages = resolve_selected_pages(options.page_selection.as_deref(), total_pages)?;
    let pages_to_extract = selected_pages
        .clone()
        .unwrap_or_else(|| (1..=total_pages).collect::<Vec<_>>());
    let page_summary = format_page_summary(selected_pages.as_deref(), total_pages);

    let mut pages = Vec::with_capacity(pages_to_extract.len());
    for page_number in pages_to_extract {
        let page_index = (page_number - 1) as PdfPageIndex;
        let page = document.pages().get(page_index).map_err(|error| {
            PdfRenderToolError::Pdfium(format!("Failed to load page {page_number}: {error}"))
        })?;
        let text = page.text().map_err(|error| {
            PdfRenderToolError::Pdfium(format!(
                "Failed to load text for page {page_number}: {error}"
            ))
        })?;

        let mut chars = Vec::with_capacity(text.chars().len());
        for text_char in text.chars().iter() {
            let Some(value) = text_char.unicode_string() else {
                continue;
            };
            let bounds = if options.prefer_loose_bounds {
                text_char
                    .loose_bounds()
                    .or_else(|_| text_char.tight_bounds())
            } else {
                text_char
                    .tight_bounds()
                    .or_else(|_| text_char.loose_bounds())
            }
            .map_err(|error| {
                PdfRenderToolError::Pdfium(format!(
                    "Failed to read bounds for page {page_number} char {}: {error}",
                    text_char.index()
                ))
            })?;

            let (origin_x, origin_y) = text_char
                .origin()
                .map(|(x, y)| (Some(x.value as f64), Some(y.value as f64)))
                .unwrap_or((None, None));
            let matrix = text_char.matrix().ok().map(pdfium_matrix_to_text_matrix);
            let angle_degrees = text_char.angle_degrees().ok().map(|value| value as f64);

            chars.push(ExtractedPdfTextChar {
                page_number,
                char_index: text_char.index(),
                value,
                unicode_value: text_char.unicode_value(),
                font_name: text_char.font_name(),
                scaled_font_size: text_char.scaled_font_size().value as f64,
                unscaled_font_size: text_char.unscaled_font_size().value as f64,
                bounds: pdfium_rect_to_text_bounds(bounds),
                origin_x,
                origin_y,
                matrix,
                angle_degrees,
            });
        }

        pages.push(ExtractedPdfTextPage {
            page_number,
            width: page.width().value as f64,
            height: page.height().value as f64,
            chars,
        });
    }

    Ok(PdfTextExtractionSummary {
        input_pdf: options.input_pdf.clone(),
        page_summary,
        pages,
    })
}

pub fn resolve_selected_pages(
    page_selection: Option<&str>,
    total_pages: usize,
) -> Result<Option<Vec<usize>>, PdfRenderToolError> {
    let Some(page_selection) = page_selection
        .map(str::trim)
        .filter(|value| !value.is_empty())
    else {
        return Ok(None);
    };

    if page_selection.eq_ignore_ascii_case("all") {
        return Ok(None);
    }

    let mut pages = BTreeSet::new();
    for part in page_selection
        .split(',')
        .map(str::trim)
        .filter(|part| !part.is_empty())
    {
        if let Some((start, end)) = part.split_once('-') {
            let Ok(mut start) = start.trim().parse::<isize>() else {
                continue;
            };
            let Ok(mut end) = end.trim().parse::<isize>() else {
                continue;
            };
            start = start.max(1);
            end = end.min(total_pages as isize);
            for page in start..=end {
                pages.insert(page as usize);
            }
        } else if let Ok(page) = part.parse::<usize>() {
            if (1..=total_pages).contains(&page) {
                pages.insert(page);
            }
        }
    }

    if pages.is_empty() {
        return Err(PdfRenderToolError::InvalidArgument(format!(
            "Page selection '{page_selection}' is invalid or does not match any page in this PDF."
        )));
    }

    Ok(Some(pages.into_iter().collect()))
}

pub fn format_page_summary(selected_pages: Option<&[usize]>, total_pages: usize) -> String {
    match selected_pages {
        None => format!("{total_pages} (all)"),
        Some(pages) => {
            let page_count = pages.len();
            let pages = pages
                .iter()
                .map(|page| page.to_string())
                .collect::<Vec<_>>()
                .join(", ");
            format!("{page_count} selected ({pages})")
        }
    }
}

pub fn build_default_output_dir(input_pdf: &Path) -> PathBuf {
    let source_dir = input_pdf.parent().unwrap_or_else(|| Path::new("."));
    let base_name = input_pdf
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("document");
    source_dir.join(format!("{base_name}_pages"))
}

fn bind_pdfium(pdfium_dir: Option<&Path>) -> Result<Pdfium, PdfRenderToolError> {
    let mut attempted = Vec::new();
    for path in pdfium_binding_candidates(pdfium_dir) {
        attempted.push(path.display().to_string());
        if let Ok(bindings) =
            Pdfium::bind_to_library(Pdfium::pdfium_platform_library_name_at_path(&path))
        {
            return Ok(Pdfium::new(bindings));
        }
    }

    Pdfium::bind_to_system_library()
        .map(Pdfium::new)
        .map_err(|error| {
            let attempted = if attempted.is_empty() {
                "none".to_string()
            } else {
                attempted.join(", ")
            };
            PdfRenderToolError::Pdfium(format!(
                "Failed to bind Pdfium. Set --pdfium-dir or EASYDICT_PDFIUM_DIR to a directory containing pdfium.dll. Tried directories: {attempted}. System lookup error: {error}"
            ))
        })
}

fn pdfium_binding_candidates(pdfium_dir: Option<&Path>) -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    if let Some(path) = pdfium_dir {
        candidates.push(path.to_path_buf());
    }

    for variable in [
        "EASYDICT_PDFIUM_DIR",
        "PDFIUM_LIBRARY_PATH",
        "PDFIUM_DYNAMIC_LIB_PATH",
    ] {
        if let Some(path) = env::var_os(variable).filter(|value| !value.is_empty()) {
            candidates.push(PathBuf::from(path));
        }
    }

    if let Ok(exe_path) = env::current_exe() {
        if let Some(dir) = exe_path.parent() {
            candidates.push(dir.to_path_buf());
        }
    }

    dedupe_paths(candidates)
}

fn dedupe_paths(paths: Vec<PathBuf>) -> Vec<PathBuf> {
    let mut result = Vec::new();
    for path in paths {
        if !result.iter().any(|existing: &PathBuf| existing == &path) {
            result.push(path);
        }
    }
    result
}

fn pdfium_rect_to_text_bounds(rect: PdfRect) -> PdfTextBounds {
    PdfTextBounds {
        left: rect.left().value as f64,
        bottom: rect.bottom().value as f64,
        right: rect.right().value as f64,
        top: rect.top().value as f64,
    }
}

fn pdfium_matrix_to_text_matrix(matrix: PdfMatrix) -> PdfTextMatrix {
    PdfTextMatrix {
        a: matrix.a() as f64,
        b: matrix.b() as f64,
        c: matrix.c() as f64,
        d: matrix.d() as f64,
        e: matrix.e() as f64,
        f: matrix.f() as f64,
    }
}

fn read_value<'a>(
    args: &'a [String],
    index: &mut usize,
    option: &str,
) -> Result<&'a str, PdfRenderToolError> {
    if *index + 1 >= args.len() {
        return Err(PdfRenderToolError::InvalidArgument(format!(
            "Missing value for {option}."
        )));
    }

    *index += 1;
    Ok(&args[*index])
}

fn parse_positive_f64(value: &str, option: &str) -> Result<f64, PdfRenderToolError> {
    let parsed = value.parse::<f64>().map_err(|_| {
        PdfRenderToolError::InvalidArgument(format!("{option} must be a positive number."))
    })?;
    if parsed <= 0.0 {
        return Err(PdfRenderToolError::InvalidArgument(format!(
            "{option} must be a positive number."
        )));
    }

    Ok(parsed)
}

fn parse_positive_usize(value: &str, option: &str) -> Result<usize, PdfRenderToolError> {
    let parsed = value.parse::<usize>().map_err(|_| {
        PdfRenderToolError::InvalidArgument(format!("{option} must be an integer >= 1."))
    })?;
    if parsed == 0 {
        return Err(PdfRenderToolError::InvalidArgument(format!(
            "{option} must be an integer >= 1."
        )));
    }

    Ok(parsed)
}

fn normalize_path(path: &str) -> PathBuf {
    let path = PathBuf::from(path);
    if path.is_absolute() {
        path
    } else {
        env::current_dir()
            .map(|current| current.join(&path))
            .unwrap_or(path)
    }
}

#[cfg(test)]
mod tests {
    use super::{
        build_default_output_dir, format_page_summary, parse_pdf_to_images_args,
        resolve_selected_pages, PdfImageFormat, PdfTextExtractionOptions, PdfToBgraOptions,
    };
    use std::path::Path;

    #[test]
    fn parse_args_preserves_legacy_options() {
        let options = parse_pdf_to_images_args([
            "--input",
            "paper.pdf",
            "--output-dir",
            "pages",
            "--dpi",
            "216",
            "--scale",
            "3",
            "--format",
            "jpeg",
            "--page-range",
            "1-3,5",
            "--pdfium-dir",
            "pdfium",
        ])
        .expect("options");

        assert!(options.input_pdf.ends_with("paper.pdf"));
        assert!(options.output_dir.unwrap().ends_with("pages"));
        assert_eq!(options.dpi, 216.0);
        assert_eq!(options.scale, Some(3.0));
        assert_eq!(options.format, PdfImageFormat::Jpg);
        assert_eq!(options.page_selection.as_deref(), Some("1-3,5"));
        assert!(options.pdfium_dir.unwrap().ends_with("pdfium"));
    }

    #[test]
    fn page_ranges_match_legacy_parser_behavior() {
        assert_eq!(resolve_selected_pages(None, 10).unwrap(), None);
        assert_eq!(resolve_selected_pages(Some("all"), 10).unwrap(), None);
        assert_eq!(
            resolve_selected_pages(Some("1-3,5,100"), 10).unwrap(),
            Some(vec![1, 2, 3, 5])
        );
        assert_eq!(
            resolve_selected_pages(Some("-2,0,3-12"), 5).unwrap(),
            Some(vec![3, 4, 5])
        );
        assert!(resolve_selected_pages(Some("99"), 5).is_err());
    }

    #[test]
    fn default_output_dir_and_summary_match_legacy_shape() {
        assert_eq!(
            build_default_output_dir(Path::new("C:/docs/paper.pdf"))
                .to_string_lossy()
                .replace('\\', "/"),
            "C:/docs/paper_pages"
        );
        assert_eq!(format_page_summary(None, 7), "7 (all)");
        assert_eq!(
            format_page_summary(Some(&[1, 3, 4]), 7),
            "3 selected (1, 3, 4)"
        );
    }

    #[test]
    fn text_extraction_options_default_to_tight_bounds() {
        let options = PdfTextExtractionOptions::new("paper.pdf");

        assert!(options.input_pdf.ends_with("paper.pdf"));
        assert!(options.page_selection.is_none());
        assert!(options.pdfium_dir.is_none());
        assert!(!options.prefer_loose_bounds);
    }

    #[test]
    fn bgra_render_options_use_pdf_ocr_defaults() {
        let options = PdfToBgraOptions::new("paper.pdf", "ocr-pages");

        assert!(options.input_pdf.ends_with("paper.pdf"));
        assert!(options.output_dir.ends_with("ocr-pages"));
        assert_eq!(options.dpi, 144.0);
        assert_eq!(options.effective_scale(), 2.0);
        assert_eq!(options.effective_dpi(), 144.0);
        assert!(options.scale.is_none());
        assert!(options.page_selection.is_none());
    }
}
