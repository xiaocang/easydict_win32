use std::collections::BTreeSet;
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfOverlayRect {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

impl PdfOverlayRect {
    pub const fn new(x: f32, y: f32, width: f32, height: f32) -> Self {
        Self {
            x,
            y,
            width,
            height,
        }
    }

    fn as_array(self) -> [f32; 4] {
        [self.x, self.y, self.width, self.height]
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfOverlayBlock {
    pub page_number: u32,
    pub rect: PdfOverlayRect,
    pub text: String,
    pub font_size: f32,
    pub color: [f32; 3],
    pub line_height: f32,
    pub fill_background: bool,
}

impl PdfOverlayBlock {
    pub fn new(
        page_number: u32,
        rect: PdfOverlayRect,
        text: impl Into<String>,
        font_size: f32,
    ) -> Self {
        Self {
            page_number,
            rect,
            text: text.into(),
            font_size,
            color: [0.0, 0.0, 0.0],
            line_height: 0.0,
            fill_background: true,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfOverlayOptions {
    pub source_pdf: PathBuf,
    pub output_pdf: PathBuf,
    pub font_path: PathBuf,
    pub blocks: Vec<PdfOverlayBlock>,
    pub selected_page_numbers: Option<Vec<u32>>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfOverlaySummary {
    pub source_pdf: PathBuf,
    pub output_pdf: PathBuf,
    pub font_path: PathBuf,
    pub page_count: u32,
    pub blocks_requested: usize,
    pub blocks_written: usize,
}

#[derive(Debug)]
pub struct PdfOverlayError {
    pub kind: PdfOverlayErrorKind,
    pub message: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PdfOverlayErrorKind {
    InvalidArgument,
    Io,
    Pdf,
}

impl PdfOverlayError {
    fn invalid(message: impl Into<String>) -> Self {
        Self {
            kind: PdfOverlayErrorKind::InvalidArgument,
            message: message.into(),
        }
    }

    fn io(error: std::io::Error) -> Self {
        Self {
            kind: PdfOverlayErrorKind::Io,
            message: error.to_string(),
        }
    }

    fn pdf(error: harumi::Error) -> Self {
        Self {
            kind: PdfOverlayErrorKind::Pdf,
            message: error.to_string(),
        }
    }
}

impl fmt::Display for PdfOverlayError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for PdfOverlayError {}

impl From<std::io::Error> for PdfOverlayError {
    fn from(value: std::io::Error) -> Self {
        Self::io(value)
    }
}

impl From<harumi::Error> for PdfOverlayError {
    fn from(value: harumi::Error) -> Self {
        Self::pdf(value)
    }
}

pub fn overlay_pdf_text_blocks(
    options: &PdfOverlayOptions,
) -> Result<PdfOverlaySummary, PdfOverlayError> {
    validate_existing_file(&options.source_pdf, "source PDF")?;
    validate_existing_file(&options.font_path, "font")?;
    validate_output_path(&options.output_pdf)?;
    validate_distinct_output_path(&options.source_pdf, &options.output_pdf)?;
    if options.blocks.is_empty() {
        return Err(PdfOverlayError::invalid(
            "At least one PDF overlay block is required",
        ));
    }

    let font_bytes = fs::read(&options.font_path)?;
    let mut document = harumi::Document::from_file(&options.source_pdf)?;
    let page_count = document.page_count();
    let selected_page_numbers =
        validate_selected_page_numbers(options.selected_page_numbers.as_deref(), page_count)?;
    let font = document.embed_font(&font_bytes)?;
    let mut blocks_written = 0usize;

    for block in &options.blocks {
        validate_block_page(block, page_count)?;
        validate_block_selected(block, selected_page_numbers.as_ref())?;
        let page_size = document.page(block.page_number)?.size()?;
        validate_block(block, page_count, page_size)?;
        let rect = block.rect.as_array();
        let mut page = document.page(block.page_number)?;
        if block.fill_background {
            page.add_rect(rect, [1.0, 1.0, 1.0], 1.0)?;
        }
        page.add_text_box(
            block.text.trim(),
            font,
            rect,
            block.font_size,
            block.color,
            block.line_height,
        )?;
        blocks_written += 1;
    }

    retain_selected_pages(&mut document, selected_page_numbers.as_ref())?;
    let page_count = document.page_count();
    document.save(&options.output_pdf)?;
    Ok(PdfOverlaySummary {
        source_pdf: options.source_pdf.clone(),
        output_pdf: options.output_pdf.clone(),
        font_path: options.font_path.clone(),
        page_count,
        blocks_requested: options.blocks.len(),
        blocks_written,
    })
}

fn validate_existing_file(path: &Path, label: &str) -> Result<(), PdfOverlayError> {
    if path.as_os_str().is_empty() {
        return Err(PdfOverlayError::invalid(format!("{label} path is empty")));
    }

    match fs::metadata(path) {
        Ok(metadata) if metadata.is_file() => Ok(()),
        Ok(_) => Err(PdfOverlayError::invalid(format!(
            "{label} path '{}' is not a file",
            path.display()
        ))),
        Err(error) => Err(PdfOverlayError::invalid(format!(
            "{label} path '{}' is not readable: {error}",
            path.display()
        ))),
    }
}

fn validate_output_path(path: &Path) -> Result<(), PdfOverlayError> {
    if path.as_os_str().is_empty() {
        return Err(PdfOverlayError::invalid("output PDF path is empty"));
    }

    if let Ok(metadata) = fs::metadata(path) {
        if metadata.is_dir() {
            return Err(PdfOverlayError::invalid(format!(
                "output PDF path '{}' is a directory",
                path.display()
            )));
        }
    }

    if let Some(parent) = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    {
        match fs::metadata(parent) {
            Ok(metadata) if metadata.is_dir() => {}
            Ok(_) => {
                return Err(PdfOverlayError::invalid(format!(
                    "output PDF parent '{}' is not a directory",
                    parent.display()
                )));
            }
            Err(error) => {
                return Err(PdfOverlayError::invalid(format!(
                    "output PDF parent '{}' is not readable: {error}",
                    parent.display()
                )));
            }
        }
    }

    Ok(())
}

fn validate_distinct_output_path(source: &Path, output: &Path) -> Result<(), PdfOverlayError> {
    let source = fs::canonicalize(source)?;
    let output = match fs::canonicalize(output) {
        Ok(path) => path,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => output.to_path_buf(),
        Err(error) => return Err(PdfOverlayError::from(error)),
    };
    if source == output {
        return Err(PdfOverlayError::invalid(
            "source PDF and output PDF paths must be different",
        ));
    }
    Ok(())
}

fn validate_selected_page_numbers(
    page_numbers: Option<&[u32]>,
    page_count: u32,
) -> Result<Option<BTreeSet<u32>>, PdfOverlayError> {
    let Some(page_numbers) = page_numbers else {
        return Ok(None);
    };
    if page_numbers.is_empty() {
        return Err(PdfOverlayError::invalid(
            "selected PDF overlay pages must not be empty",
        ));
    }

    let selected = page_numbers.iter().copied().collect::<BTreeSet<_>>();
    for page_number in &selected {
        if *page_number == 0 || *page_number > page_count {
            return Err(PdfOverlayError::invalid(format!(
                "selected PDF overlay page {page_number} is outside the document page range 1..={page_count}",
            )));
        }
    }
    Ok(Some(selected))
}

fn validate_block(
    block: &PdfOverlayBlock,
    page_count: u32,
    page_size: (f32, f32),
) -> Result<(), PdfOverlayError> {
    validate_block_page(block, page_count)?;
    validate_block_values(block)?;
    validate_block_page_bounds(block, page_size)?;
    Ok(())
}

fn validate_block_page(block: &PdfOverlayBlock, page_count: u32) -> Result<(), PdfOverlayError> {
    if block.page_number == 0 || block.page_number > page_count {
        return Err(PdfOverlayError::invalid(format!(
            "PDF overlay block page {} is outside the document page range 1..={page_count}",
            block.page_number
        )));
    }
    Ok(())
}

fn validate_block_selected(
    block: &PdfOverlayBlock,
    selected_page_numbers: Option<&BTreeSet<u32>>,
) -> Result<(), PdfOverlayError> {
    if selected_page_numbers.is_some_and(|selected| !selected.contains(&block.page_number)) {
        return Err(PdfOverlayError::invalid(format!(
            "PDF overlay block page {} is not included in the selected page set",
            block.page_number
        )));
    }
    Ok(())
}

fn validate_block_values(block: &PdfOverlayBlock) -> Result<(), PdfOverlayError> {
    if block.text.trim().is_empty() {
        return Err(PdfOverlayError::invalid(
            "PDF overlay block text must not be empty",
        ));
    }

    let values = [
        block.rect.x,
        block.rect.y,
        block.rect.width,
        block.rect.height,
        block.font_size,
        block.line_height,
        block.color[0],
        block.color[1],
        block.color[2],
    ];
    if values.iter().any(|value| !value.is_finite()) {
        return Err(PdfOverlayError::invalid(
            "PDF overlay block contains non-finite geometry, font, or color values",
        ));
    }
    if block.rect.width <= 0.0 || block.rect.height <= 0.0 {
        return Err(PdfOverlayError::invalid(
            "PDF overlay block rectangle must have positive width and height",
        ));
    }
    if block.rect.x < 0.0 || block.rect.y < 0.0 {
        return Err(PdfOverlayError::invalid(
            "PDF overlay block rectangle must be inside the page coordinate space",
        ));
    }
    if block.font_size <= 0.0 {
        return Err(PdfOverlayError::invalid(
            "PDF overlay block font size must be positive",
        ));
    }
    if block.line_height < 0.0 {
        return Err(PdfOverlayError::invalid(
            "PDF overlay block line height must not be negative",
        ));
    }
    if block.color.iter().any(|value| !(0.0..=1.0).contains(value)) {
        return Err(PdfOverlayError::invalid(
            "PDF overlay block color components must be within 0.0..=1.0",
        ));
    }

    Ok(())
}

fn retain_selected_pages(
    document: &mut harumi::Document,
    selected_page_numbers: Option<&BTreeSet<u32>>,
) -> Result<(), PdfOverlayError> {
    let Some(selected_page_numbers) = selected_page_numbers else {
        return Ok(());
    };
    let page_count = document.page_count();
    if selected_page_numbers.len() == page_count as usize {
        return Ok(());
    }

    for page_number in (1..=page_count).rev() {
        if !selected_page_numbers.contains(&page_number) {
            document.remove_page(page_number)?;
        }
    }
    Ok(())
}

fn validate_block_page_bounds(
    block: &PdfOverlayBlock,
    page_size: (f32, f32),
) -> Result<(), PdfOverlayError> {
    let right = block.rect.x + block.rect.width;
    let top = block.rect.y + block.rect.height;
    if right > page_size.0 || top > page_size.1 {
        return Err(PdfOverlayError::invalid(format!(
            "PDF overlay block rectangle exceeds page {} size {:.1} x {:.1}",
            block.page_number, page_size.0, page_size.1
        )));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn rejects_empty_overlay_blocks_before_opening_font() {
        let dir = unique_temp_dir("empty-blocks");
        fs::create_dir_all(&dir).expect("temp dir");
        let input = dir.join("input.pdf");
        let output = dir.join("output.pdf");
        let font = dir.join("font.ttf");
        fs::write(&input, minimal_pdf()).expect("input pdf");
        fs::write(&font, b"not a font").expect("font placeholder");

        let error = overlay_pdf_text_blocks(&PdfOverlayOptions {
            source_pdf: input,
            output_pdf: output,
            font_path: font,
            blocks: Vec::new(),
            selected_page_numbers: None,
        })
        .expect_err("empty overlay blocks should be rejected");

        assert!(error.to_string().contains("At least one"));
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn rejects_invalid_block_geometry() {
        let block = PdfOverlayBlock::new(
            1,
            PdfOverlayRect::new(10.0, 10.0, 0.0, 20.0),
            "Translated",
            12.0,
        );

        let error = validate_block(&block, 1, (595.0, 842.0)).expect_err("zero width should fail");

        assert!(error.to_string().contains("positive width"));
    }

    #[test]
    fn rejects_overlay_rectangles_outside_page_bounds() {
        let block = PdfOverlayBlock::new(
            1,
            PdfOverlayRect::new(580.0, 10.0, 40.0, 20.0),
            "Translated",
            12.0,
        );

        let error = validate_block(&block, 1, (595.0, 842.0))
            .expect_err("rectangle beyond page width should fail");

        assert_eq!(error.kind, PdfOverlayErrorKind::InvalidArgument);
        assert!(error.to_string().contains("exceeds page"));
    }

    #[test]
    fn rejects_blocks_outside_selected_pages() {
        let block = PdfOverlayBlock::new(
            1,
            PdfOverlayRect::new(72.0, 620.0, 240.0, 48.0),
            "Translated",
            14.0,
        );
        let selected = BTreeSet::from([2]);

        let error = validate_block_selected(&block, Some(&selected))
            .expect_err("unselected page should fail");

        assert_eq!(error.kind, PdfOverlayErrorKind::InvalidArgument);
        assert!(error.to_string().contains("selected page set"));
    }

    #[test]
    fn rejects_same_source_and_output_path() {
        let dir = unique_temp_dir("same-path");
        fs::create_dir_all(&dir).expect("temp dir");
        let input = dir.join("input.pdf");
        let font = dir.join("font.ttf");
        fs::write(&input, minimal_pdf()).expect("input pdf");
        fs::write(&font, b"not a font").expect("font placeholder");

        let error = overlay_pdf_text_blocks(&PdfOverlayOptions {
            source_pdf: input.clone(),
            output_pdf: input,
            font_path: font,
            blocks: vec![PdfOverlayBlock::new(
                1,
                PdfOverlayRect::new(72.0, 620.0, 240.0, 48.0),
                "Translated",
                14.0,
            )],
            selected_page_numbers: None,
        })
        .expect_err("source and output paths should differ");

        assert_eq!(error.kind, PdfOverlayErrorKind::InvalidArgument);
        assert!(error.to_string().contains("must be different"));
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn writes_visible_cjk_overlay_when_windows_font_is_available() {
        let Some(font_path) = windows_cjk_font_path() else {
            eprintln!("Skipping CJK overlay smoke test: no Windows CJK font found");
            return;
        };

        let dir = unique_temp_dir("cjk-overlay");
        fs::create_dir_all(&dir).expect("temp dir");
        let input = dir.join("input.pdf");
        let output = dir.join("output.pdf");
        fs::write(&input, minimal_pdf()).expect("input pdf");

        let summary = overlay_pdf_text_blocks(&PdfOverlayOptions {
            source_pdf: input.clone(),
            output_pdf: output.clone(),
            font_path,
            blocks: vec![PdfOverlayBlock::new(
                1,
                PdfOverlayRect::new(72.0, 620.0, 240.0, 48.0),
                "你好，Easydict",
                14.0,
            )],
            selected_page_numbers: None,
        })
        .expect("CJK overlay should be written");

        assert_eq!(summary.page_count, 1);
        assert_eq!(summary.blocks_written, 1);
        assert!(output.is_file());

        let document = harumi::Document::from_file(&output).expect("output should open");
        let extracted = document
            .extract_text_runs(1)
            .expect("output text should extract")
            .into_iter()
            .map(|fragment| fragment.text)
            .collect::<String>();
        assert!(extracted.contains("你好"));

        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn retains_selected_pages_after_overlay_when_windows_font_is_available() {
        let Some(font_path) = windows_cjk_font_path() else {
            eprintln!("Skipping selected-page overlay smoke test: no Windows CJK font found");
            return;
        };

        let dir = unique_temp_dir("selected-pages");
        fs::create_dir_all(&dir).expect("temp dir");
        let input = dir.join("input.pdf");
        let output = dir.join("output.pdf");
        fs::write(&input, minimal_pdf_with_page_count(3)).expect("input pdf");

        let summary = overlay_pdf_text_blocks(&PdfOverlayOptions {
            source_pdf: input,
            output_pdf: output.clone(),
            font_path,
            blocks: vec![PdfOverlayBlock::new(
                2,
                PdfOverlayRect::new(72.0, 620.0, 240.0, 48.0),
                "只保留第二页",
                14.0,
            )],
            selected_page_numbers: Some(vec![2]),
        })
        .expect("selected-page CJK overlay should be written");

        assert_eq!(summary.page_count, 1);
        assert_eq!(summary.blocks_written, 1);
        let document = harumi::Document::from_file(&output).expect("output should open");
        assert_eq!(document.page_count(), 1);
        let extracted = document
            .extract_text_runs(1)
            .expect("output text should extract")
            .into_iter()
            .map(|fragment| fragment.text)
            .collect::<String>();
        assert!(extracted.contains("第二页"));

        fs::remove_dir_all(&dir).ok();
    }

    fn windows_cjk_font_path() -> Option<PathBuf> {
        [
            r"C:\Windows\Fonts\simhei.ttf",
            r"C:\Windows\Fonts\msyh.ttc",
            r"C:\Windows\Fonts\simsun.ttc",
            r"C:\Windows\Fonts\msgothic.ttc",
        ]
        .iter()
        .map(PathBuf::from)
        .find(|path| path.is_file())
    }

    fn unique_temp_dir(prefix: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or_default();
        std::env::temp_dir().join(format!("easydict-pdf-overlay-{prefix}-{stamp}"))
    }

    fn minimal_pdf() -> Vec<u8> {
        minimal_pdf_with_page_count(1)
    }

    fn minimal_pdf_with_page_count(page_count: usize) -> Vec<u8> {
        let mut pdf = b"%PDF-1.4\n".to_vec();
        let page_count = page_count.max(1);
        let page_object_numbers = (0..page_count)
            .map(|index| 3 + index * 2)
            .collect::<Vec<_>>();
        let kids = page_object_numbers
            .iter()
            .map(|object_number| format!("{object_number} 0 R"))
            .collect::<Vec<_>>()
            .join(" ");
        let mut objects = vec![
            b"<< /Type /Catalog /Pages 2 0 R >>".to_vec(),
            format!("<< /Type /Pages /Kids [{kids}] /Count {page_count} >>").into_bytes(),
        ];
        for page_object_number in page_object_numbers {
            let content_object_number = page_object_number + 1;
            objects.push(format!("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Contents {content_object_number} 0 R >>").into_bytes());
            objects.push(b"<< /Length 5 >>\nstream\nq Q\nendstream".to_vec());
        }
        let mut offsets = Vec::with_capacity(objects.len());
        for (index, object) in objects.iter().enumerate() {
            offsets.push(pdf.len());
            pdf.extend_from_slice(format!("{} 0 obj\n", index + 1).as_bytes());
            pdf.extend_from_slice(object);
            pdf.extend_from_slice(b"\nendobj\n");
        }
        let xref_offset = pdf.len();
        pdf.extend_from_slice(format!("xref\n0 {}\n", objects.len() + 1).as_bytes());
        pdf.extend_from_slice(b"0000000000 65535 f \n");
        for offset in offsets {
            pdf.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
        }
        pdf.extend_from_slice(
            format!(
                "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{xref_offset}\n%%EOF\n",
                objects.len() + 1
            )
            .as_bytes(),
        );
        pdf
    }
}
