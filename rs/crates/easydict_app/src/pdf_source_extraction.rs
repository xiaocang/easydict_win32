use crate::character_paragraph::{strip_subset_prefix, TextMatrix};
use crate::content_preservation::{BlockContext, SourceBlockType};
use crate::doc_layout_yolo::{DocLayoutRegionType, DocLayoutYoloDetection};
use crate::formula_protection::FormulaToken;
use crate::formula_text_reconstruction::{
    looks_like_formula_continuation_text, previous_line_likely_expects_formula_tail,
};
use crate::pdf_export_blocks::{
    PdfExportBlockTextStyle, PdfExportChunkMetadata, PdfExportSourceBlockType,
};
use crate::pdf_formula_adapter::{
    build_formula_aware_pdf_block_text, PdfBlockBounds, PdfBlockFormulaEvidence, PdfGlyph,
    PdfGlyphBounds, PdfTextOrientation,
};
use crate::table_structure::{TableCellBounds, TableStructure, TableSubDetection};
use crate::PdfRect;
use easydict_pdf_render::{
    ExtractedPdfTextChar, ExtractedPdfTextPage, PdfTextBounds, PdfTextExtractionSummary,
    PdfTextMatrix,
};
use regex::Regex;
use std::collections::BTreeSet;
use std::sync::OnceLock;

const DEFAULT_LINE_BASELINE_TOLERANCE_PT: f64 = 3.0;
const DEFAULT_BLOCK_GAP_SCALE: f64 = 1.3;
const DOC_LAYOUT_YOLO_MIN_BLOCK_CONFIDENCE: f32 = 0.3;
const DOC_LAYOUT_YOLO_MIN_BLOCK_COVERAGE: f64 = 0.25;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PdfSourceLayoutRegion {
    Unknown,
    Body,
    Header,
    Footer,
    LeftColumn,
    RightColumn,
    TableLike,
    Figure,
    Formula,
    Caption,
    Title,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PdfSourceLayoutRegionSource {
    Unknown,
    Heuristic,
    OnnxModel,
    BlockIdFallback,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfSourceLayoutRegionInfo {
    pub region_type: PdfSourceLayoutRegion,
    pub confidence: f64,
    pub source: PdfSourceLayoutRegionSource,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfSourceLayoutProfile {
    pub page_width: f64,
    pub page_height: f64,
    pub is_two_column: bool,
    pub left_column_boundary: f64,
    pub right_column_boundary: f64,
    pub header_top_threshold: f64,
    pub footer_bottom_threshold: f64,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfSourceExtractionOptions {
    pub line_baseline_tolerance: f64,
    pub block_gap_scale: f64,
}

impl Default for PdfSourceExtractionOptions {
    fn default() -> Self {
        Self {
            line_baseline_tolerance: DEFAULT_LINE_BASELINE_TOLERANCE_PT,
            block_gap_scale: DEFAULT_BLOCK_GAP_SCALE,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfSourceLine {
    pub page_number: usize,
    pub line_index: usize,
    pub text: String,
    pub bounds: PdfBlockBounds,
    pub glyphs: Vec<PdfGlyph>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfSourceBlock {
    pub page_number: usize,
    pub block_index: usize,
    pub source_block_id: String,
    pub region_type: PdfSourceLayoutRegion,
    pub line_texts: Vec<String>,
    pub text: String,
    pub fallback_text: Option<String>,
    pub source_block_type: SourceBlockType,
    pub bounds: PdfBlockBounds,
    pub text_style: Option<PdfExportBlockTextStyle>,
    pub detected_font_names: Vec<String>,
    pub character_level_protected_text: Option<String>,
    pub character_level_tokens: Vec<FormulaToken>,
    pub evidence: PdfBlockFormulaEvidence,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfSourcePage {
    pub page_number: usize,
    pub width: f64,
    pub height: f64,
    pub lines: Vec<PdfSourceLine>,
    pub blocks: Vec<PdfSourceBlock>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfSourceDocument {
    pub pages: Vec<PdfSourcePage>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfSourcePageLayoutDetections {
    pub page_number: usize,
    pub pixel_width: usize,
    pub pixel_height: usize,
    pub detections: Vec<DocLayoutYoloDetection>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfSourcePageTableStructures {
    pub page_number: usize,
    pub pixel_width: usize,
    pub pixel_height: usize,
    pub tables: Vec<TableStructure>,
}

pub fn pdf_source_document_from_text_summary(
    summary: &PdfTextExtractionSummary,
) -> PdfSourceDocument {
    pdf_source_document_from_text_summary_with_options(
        summary,
        PdfSourceExtractionOptions::default(),
    )
}

pub fn pdf_source_document_from_text_summary_with_options(
    summary: &PdfTextExtractionSummary,
    options: PdfSourceExtractionOptions,
) -> PdfSourceDocument {
    PdfSourceDocument {
        pages: summary
            .pages
            .iter()
            .map(|page| pdf_source_page_from_extracted_page(page, options))
            .collect(),
    }
}

pub fn pdf_source_document_with_doc_layout_yolo_detections(
    document: &PdfSourceDocument,
    layouts: &[PdfSourcePageLayoutDetections],
) -> PdfSourceDocument {
    PdfSourceDocument {
        pages: document
            .pages
            .iter()
            .map(|page| {
                layouts
                    .iter()
                    .find(|layout| layout.page_number == page.page_number)
                    .map(|layout| apply_doc_layout_yolo_detections_to_pdf_source_page(page, layout))
                    .unwrap_or_else(|| page.clone())
            })
            .collect(),
    }
}

pub fn pdf_source_document_with_tatr_table_structures(
    document: &PdfSourceDocument,
    layouts: &[PdfSourcePageTableStructures],
) -> PdfSourceDocument {
    PdfSourceDocument {
        pages: document
            .pages
            .iter()
            .map(|page| {
                layouts
                    .iter()
                    .find(|layout| layout.page_number == page.page_number)
                    .map(|layout| apply_tatr_table_structures_to_pdf_source_page(page, layout))
                    .unwrap_or_else(|| page.clone())
            })
            .collect(),
    }
}

pub fn apply_doc_layout_yolo_detections_to_pdf_source_page(
    page: &PdfSourcePage,
    layout: &PdfSourcePageLayoutDetections,
) -> PdfSourcePage {
    if page.blocks.is_empty()
        || layout.pixel_width == 0
        || layout.pixel_height == 0
        || page.width <= 0.0
        || page.height <= 0.0
    {
        return page.clone();
    }

    let image_scale_x = layout.pixel_width as f64 / page.width;
    let image_scale_y = layout.pixel_height as f64 / page.height;
    if image_scale_x <= 0.0 || image_scale_y <= 0.0 {
        return page.clone();
    }

    let regions = layout
        .detections
        .iter()
        .filter_map(|detection| {
            pdf_source_onnx_region_from_detection(
                detection,
                image_scale_x,
                image_scale_y,
                page.height,
            )
        })
        .collect::<Vec<_>>();
    if regions.is_empty() {
        return page.clone();
    }

    let mut updated = page.clone();
    updated.blocks = page
        .blocks
        .iter()
        .map(|block| {
            best_onnx_region_for_pdf_source_block(block, &regions)
                .map(|region| pdf_source_block_with_onnx_region(block, region))
                .unwrap_or_else(|| block.clone())
        })
        .collect();
    updated
}

pub fn apply_tatr_table_structures_to_pdf_source_page(
    page: &PdfSourcePage,
    layout: &PdfSourcePageTableStructures,
) -> PdfSourcePage {
    if page.lines.is_empty()
        || layout.tables.is_empty()
        || layout.pixel_width == 0
        || layout.pixel_height == 0
        || page.width <= 0.0
        || page.height <= 0.0
    {
        return page.clone();
    }

    let image_scale_x = layout.pixel_width as f64 / page.width;
    let image_scale_y = layout.pixel_height as f64 / page.height;
    if image_scale_x <= 0.0 || image_scale_y <= 0.0 {
        return page.clone();
    }

    let Some(tatr_layout) =
        pdf_source_tatr_layout_from_structures(layout, image_scale_x, image_scale_y, page.height)
    else {
        return page.clone();
    };

    let table_blocks = build_tatr_table_blocks_for_page(page, &tatr_layout);
    if table_blocks.iter().all(|blocks| blocks.is_empty()) {
        return page.clone();
    }

    let mut rebuilt = Vec::new();
    let mut inserted = vec![false; table_blocks.len()];
    for block in &page.blocks {
        let matching_table = tatr_layout_table_for_block(block, &tatr_layout).filter(|index| {
            table_blocks
                .get(*index)
                .is_some_and(|blocks| !blocks.is_empty())
        });

        if let Some(table_index) = matching_table {
            if !inserted[table_index] {
                rebuilt.extend(table_blocks[table_index].iter().cloned());
                inserted[table_index] = true;
            }
            continue;
        }

        rebuilt.push(block.clone());
    }

    for (table_index, blocks) in table_blocks.iter().enumerate() {
        if !inserted[table_index] {
            rebuilt.extend(blocks.iter().cloned());
        }
    }

    if rebuilt.is_empty() {
        return page.clone();
    }

    let mut updated = page.clone();
    updated.blocks = reindex_pdf_source_blocks(rebuilt);
    updated
}

pub fn pdf_source_page_from_extracted_page(
    page: &ExtractedPdfTextPage,
    options: PdfSourceExtractionOptions,
) -> PdfSourcePage {
    let glyphs = page
        .chars
        .iter()
        .filter(|ch| !ch.value.is_empty())
        .map(pdf_glyph_from_extracted_text_char)
        .collect::<Vec<_>>();
    let lines = group_pdf_glyphs_into_lines(page.page_number, &glyphs, page.width, options);
    let blocks =
        group_pdf_lines_into_blocks(page.page_number, &lines, page.width, page.height, options);

    PdfSourcePage {
        page_number: page.page_number,
        width: page.width,
        height: page.height,
        lines,
        blocks,
    }
}

pub fn pdf_glyph_from_extracted_text_char(ch: &ExtractedPdfTextChar) -> PdfGlyph {
    let bounds = pdf_glyph_bounds_from_text_bounds(ch.bounds);
    let point_size = positive_or_fallback(ch.unscaled_font_size, ch.scaled_font_size);
    let matrix = ch.matrix.map(text_matrix_from_pdfium);
    let orientation = pdf_text_orientation(ch.angle_degrees, ch.matrix.as_ref());
    let baseline_y = ch.origin_y.unwrap_or(bounds.bottom);

    PdfGlyph::new(&ch.value, bounds, point_size, &ch.font_name)
        .with_baseline_y(baseline_y)
        .with_orientation(orientation)
        .with_codes(ch.unicode_value, ch.unicode_value)
        .with_current_transformation_matrix(TextMatrix::IDENTITY)
        .with_optional_text_matrix(matrix)
}

pub fn block_context_for_pdf_source_block(
    block: &PdfSourceBlock,
    retry_attempt: usize,
) -> BlockContext {
    let mut context = BlockContext::paragraph(block.text.clone());
    context.block_type = block.source_block_type;
    context.is_formula_like = block.source_block_type == SourceBlockType::Formula;
    context.detected_font_names =
        (!block.detected_font_names.is_empty()).then(|| block.detected_font_names.clone());
    context.formula_characters = block.evidence.formula_characters.clone();
    context.character_level_protected_text = block.character_level_protected_text.clone();
    context.character_level_tokens =
        (!block.character_level_tokens.is_empty()).then(|| block.character_level_tokens.clone());
    context.retry_attempt = retry_attempt;
    context
}

pub fn pdf_export_chunk_metadata_for_source_block(
    block: &PdfSourceBlock,
    chunk_index: usize,
    page_block_count: usize,
    retry_count: i32,
    translation_skipped: bool,
) -> PdfExportChunkMetadata {
    PdfExportChunkMetadata {
        chunk_index,
        page_number: block.page_number as i32,
        source_block_id: pdf_source_block_id(block).to_string(),
        source_block_type: pdf_export_source_block_type(block.source_block_type),
        order_in_page: block.block_index as i32,
        reading_order_score: calculate_pdf_source_reading_order_score(
            block.block_index,
            page_block_count,
        ),
        bounding_box: Some(pdf_rect_from_block_bounds(block.bounds)),
        text_style: block.text_style,
        translation_skipped,
        preserve_original_text_in_pdf_export: translation_skipped
            || block.source_block_type == SourceBlockType::Formula,
        retry_count,
        fallback_text: block.fallback_text.clone(),
        detected_font_names: (!block.detected_font_names.is_empty())
            .then(|| block.detected_font_names.clone()),
    }
}

pub fn pdf_source_block_id(block: &PdfSourceBlock) -> &str {
    &block.source_block_id
}

pub fn infer_region_info_from_source_block_id(source_block_id: &str) -> PdfSourceLayoutRegionInfo {
    let lower = source_block_id.to_ascii_lowercase();
    if lower.contains("-header-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::Header,
            confidence: 0.92,
            source: PdfSourceLayoutRegionSource::Heuristic,
        };
    }
    if lower.contains("-footer-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::Footer,
            confidence: 0.92,
            source: PdfSourceLayoutRegionSource::Heuristic,
        };
    }
    if lower.contains("-left-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::LeftColumn,
            confidence: 0.80,
            source: PdfSourceLayoutRegionSource::Heuristic,
        };
    }
    if lower.contains("-right-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::RightColumn,
            confidence: 0.80,
            source: PdfSourceLayoutRegionSource::Heuristic,
        };
    }
    if lower.contains("-table-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::TableLike,
            confidence: 0.88,
            source: PdfSourceLayoutRegionSource::Heuristic,
        };
    }
    if lower.contains("-figure-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::Figure,
            confidence: 0.90,
            source: PdfSourceLayoutRegionSource::OnnxModel,
        };
    }
    if lower.contains("-formula-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::Formula,
            confidence: 0.90,
            source: PdfSourceLayoutRegionSource::OnnxModel,
        };
    }
    if lower.contains("-caption-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::Caption,
            confidence: 0.85,
            source: PdfSourceLayoutRegionSource::OnnxModel,
        };
    }
    if lower.contains("-title-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::Title,
            confidence: 0.88,
            source: PdfSourceLayoutRegionSource::OnnxModel,
        };
    }
    if lower.contains("-body-") {
        return PdfSourceLayoutRegionInfo {
            region_type: PdfSourceLayoutRegion::Body,
            confidence: 0.72,
            source: PdfSourceLayoutRegionSource::BlockIdFallback,
        };
    }

    PdfSourceLayoutRegionInfo {
        region_type: PdfSourceLayoutRegion::Unknown,
        confidence: 0.35,
        source: PdfSourceLayoutRegionSource::Unknown,
    }
}

pub fn guess_pdf_source_block_type(text: &str) -> SourceBlockType {
    if text.trim().is_empty() {
        return SourceBlockType::Unknown;
    }

    let trimmed = text.trim();
    if let Some(formula_match) = formula_heuristic_regex().find(trimmed) {
        let natural_word_count = natural_word_regex().find_iter(trimmed).count();
        let prose_dominant_inline_equation = trimmed.len() > 80
            && natural_word_count >= 6
            && (formula_match.as_str().len() as f64) < (trimmed.len() as f64) * 0.45;

        if !prose_dominant_inline_equation {
            return SourceBlockType::Formula;
        }
    }

    if trimmed.len() < 80
        && trimmed
            .chars()
            .all(|ch| !ch.is_alphabetic() || ch.is_uppercase())
    {
        return SourceBlockType::Heading;
    }

    SourceBlockType::Paragraph
}

pub fn group_pdf_glyphs_into_lines(
    page_number: usize,
    glyphs: &[PdfGlyph],
    page_width: f64,
    options: PdfSourceExtractionOptions,
) -> Vec<PdfSourceLine> {
    if glyphs.is_empty() {
        return Vec::new();
    }

    let tolerance = line_tolerance(glyphs, options);
    let mut ordered = glyphs.to_vec();
    ordered.sort_by(|left, right| {
        right
            .bounds
            .top
            .total_cmp(&left.bounds.top)
            .then_with(|| left.bounds.left.total_cmp(&right.bounds.left))
    });

    let mut line_glyphs = Vec::<Vec<PdfGlyph>>::new();
    for glyph in ordered {
        let mut best_index = None;
        let mut best_distance = f64::MAX;
        for (index, candidate) in line_glyphs.iter().enumerate() {
            let distance = (line_baseline_y(candidate) - glyph.baseline_y).abs();
            if distance <= tolerance && distance < best_distance {
                best_index = Some(index);
                best_distance = distance;
            }
        }

        if let Some(index) = best_index {
            line_glyphs[index].push(glyph);
        } else {
            line_glyphs.push(vec![glyph]);
        }
    }

    let mut lines = line_glyphs
        .into_iter()
        .map(|mut glyphs| {
            glyphs.sort_by(|left, right| {
                left.bounds
                    .left
                    .total_cmp(&right.bounds.left)
                    .then_with(|| left.bounds.top.total_cmp(&right.bounds.top).reverse())
            });
            glyphs
        })
        .filter(|glyphs| !glyphs.is_empty())
        .flat_map(|glyphs| split_glyph_line_at_column_gaps(glyphs, page_width))
        .map(|glyphs| {
            let bounds = bounds_for_glyphs(&glyphs);
            let text = glyphs
                .iter()
                .map(|glyph| glyph.value.as_str())
                .collect::<Vec<_>>()
                .join("");
            (bounds, text, glyphs)
        })
        .collect::<Vec<_>>();

    lines.sort_by(|left, right| {
        right
            .0
            .top
            .total_cmp(&left.0.top)
            .then_with(|| left.0.left.total_cmp(&right.0.left))
    });

    lines
        .into_iter()
        .enumerate()
        .map(|(line_index, (bounds, text, glyphs))| PdfSourceLine {
            page_number,
            line_index,
            text,
            bounds,
            glyphs,
        })
        .collect()
}

pub fn group_pdf_lines_into_blocks(
    page_number: usize,
    lines: &[PdfSourceLine],
    page_width: f64,
    page_height: f64,
    options: PdfSourceExtractionOptions,
) -> Vec<PdfSourceBlock> {
    if lines.is_empty() {
        return Vec::new();
    }

    let layout_profile = build_pdf_source_layout_profile(lines, page_width, page_height);
    let ordered_lines = order_pdf_lines_by_layout(lines, layout_profile.page_width);
    let block_gap_threshold = block_gap_threshold(&ordered_lines, options);
    let same_row_threshold = same_row_threshold(&ordered_lines);
    let mut groups = Vec::<Vec<PdfSourceLine>>::new();
    for line in ordered_lines {
        if let Some(current) = groups.last_mut() {
            let previous = current.last().expect("current group has a line");
            let same_row = (previous.bounds.top - line.bounds.top).abs() <= same_row_threshold;
            let vertical_gap = (previous.bounds.bottom - line.bounds.top).abs();
            let horizontal_offset = (previous.bounds.left - line.bounds.left).abs();
            let should_merge_formula_continuation = should_merge_formula_continuation(
                previous,
                &line,
                vertical_gap,
                block_gap_threshold,
                same_row,
            );
            let should_split = !should_merge_formula_continuation
                && (same_row
                    || vertical_gap > block_gap_threshold
                    || horizontal_offset > 30.0_f64.max(pdf_source_line_width(previous) * 0.6));

            if !should_split {
                current.push(line);
                continue;
            }
        }

        groups.push(vec![line]);
    }

    groups
        .into_iter()
        .enumerate()
        .map(|(block_index, lines)| {
            build_pdf_source_block(page_number, block_index, &lines, layout_profile)
        })
        .collect()
}

pub fn build_pdf_source_block(
    page_number: usize,
    block_index: usize,
    lines: &[PdfSourceLine],
    layout_profile: PdfSourceLayoutProfile,
) -> PdfSourceBlock {
    let glyphs = lines
        .iter()
        .flat_map(|line| line.glyphs.iter().cloned())
        .collect::<Vec<_>>();
    let bounds = bounds_for_glyphs(&glyphs);
    let line_texts = lines
        .iter()
        .map(|line| line.text.clone())
        .collect::<Vec<_>>();
    let output = build_formula_aware_pdf_block_text(&line_texts, &glyphs, bounds);
    let character_level_protected_text = output
        .evidence
        .character_level_protection
        .as_ref()
        .map(|protection| protection.protected_text.clone());
    let character_level_tokens = output
        .evidence
        .character_level_protection
        .as_ref()
        .map(|protection| protection.tokens.clone())
        .unwrap_or_default();
    let region_type = infer_pdf_source_region_type(layout_profile, bounds, &output.block_text);
    let source_block_type = if region_type == PdfSourceLayoutRegion::TableLike {
        SourceBlockType::TableCell
    } else {
        guess_pdf_source_block_type(&output.block_text)
    };

    PdfSourceBlock {
        page_number,
        block_index,
        source_block_id: build_pdf_source_block_id(page_number, block_index, region_type),
        region_type,
        line_texts,
        text: output.block_text,
        fallback_text: output.fallback_text,
        source_block_type,
        bounds,
        text_style: text_style_for_source_block(lines, &glyphs),
        detected_font_names: detected_font_names(&glyphs),
        character_level_protected_text,
        character_level_tokens,
        evidence: output.evidence,
    }
}

fn pdf_glyph_bounds_from_text_bounds(bounds: PdfTextBounds) -> PdfGlyphBounds {
    PdfGlyphBounds::from_lbrt(bounds.left, bounds.bottom, bounds.right, bounds.top)
}

fn text_matrix_from_pdfium(matrix: PdfTextMatrix) -> TextMatrix {
    TextMatrix::from_values(matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f)
}

fn pdf_text_orientation(
    angle_degrees: Option<f64>,
    matrix: Option<&PdfTextMatrix>,
) -> PdfTextOrientation {
    if let Some(angle) = angle_degrees {
        let normalized = normalize_angle(angle);
        if within_degrees(normalized, 90.0, 15.0) {
            return PdfTextOrientation::Rotate90;
        }
        if within_degrees(normalized, 270.0, 15.0) {
            return PdfTextOrientation::Rotate270;
        }
        if within_degrees(normalized, 180.0, 15.0) {
            return PdfTextOrientation::Rotate180;
        }
        if within_degrees(normalized, 0.0, 15.0) || within_degrees(normalized, 360.0, 15.0) {
            return PdfTextOrientation::Horizontal;
        }
        return PdfTextOrientation::Other;
    }

    if let Some(matrix) = matrix {
        let vertical = matrix.a.abs() < 0.001
            && matrix.d.abs() < 0.001
            && (matrix.b.abs() > 0.001 || matrix.c.abs() > 0.001);
        if vertical {
            return if matrix.b >= 0.0 {
                PdfTextOrientation::Rotate90
            } else {
                PdfTextOrientation::Rotate270
            };
        }
    }

    PdfTextOrientation::Horizontal
}

fn normalize_angle(angle: f64) -> f64 {
    let normalized = angle % 360.0;
    if normalized < 0.0 {
        normalized + 360.0
    } else {
        normalized
    }
}

fn within_degrees(value: f64, target: f64, tolerance: f64) -> bool {
    (value - target).abs() <= tolerance
}

fn positive_or_fallback(value: f64, fallback: f64) -> f64 {
    if value.is_finite() && value > 0.0 {
        value
    } else {
        fallback.max(0.0)
    }
}

fn line_tolerance(glyphs: &[PdfGlyph], options: PdfSourceExtractionOptions) -> f64 {
    let median_size = median_positive(glyphs.iter().map(|glyph| glyph.point_size), 12.0);
    options
        .line_baseline_tolerance
        .max(median_size * 0.25)
        .max(1.2)
}

fn block_gap_threshold(lines: &[PdfSourceLine], options: PdfSourceExtractionOptions) -> f64 {
    let median_height = median_positive(
        lines
            .iter()
            .map(|line| (line.bounds.top - line.bounds.bottom).max(0.0)),
        12.0,
    );
    (median_height * options.block_gap_scale).max(4.0)
}

fn line_baseline_y(glyphs: &[PdfGlyph]) -> f64 {
    median_positive(glyphs.iter().map(|glyph| glyph.baseline_y), 0.0)
}

fn split_glyph_line_at_column_gaps(
    mut glyphs: Vec<PdfGlyph>,
    page_width: f64,
) -> Vec<Vec<PdfGlyph>> {
    if glyphs.len() < 2 {
        return vec![glyphs];
    }

    glyphs.sort_by(|left, right| {
        left.bounds
            .left
            .total_cmp(&right.bounds.left)
            .then_with(|| left.bounds.top.total_cmp(&right.bounds.top).reverse())
    });

    let mut gaps = Vec::with_capacity(glyphs.len().saturating_sub(1));
    for pair in glyphs.windows(2) {
        gaps.push((pair[1].bounds.left - pair[0].bounds.right).max(0.0));
    }

    let mut sorted_gaps = gaps.clone();
    sorted_gaps.sort_by(f64::total_cmp);
    let median_gap = sorted_gaps
        .get(sorted_gaps.len() / 2)
        .copied()
        .unwrap_or_default();
    let median_point_size = median_positive(glyphs.iter().map(|glyph| glyph.point_size), 12.0);
    let line_bounds = bounds_for_glyphs(&glyphs);
    let line_width = (line_bounds.right - line_bounds.left).max(0.0);
    let effective_page_width = positive_or_fallback(page_width, line_width);
    let likely_multi_column_line =
        effective_page_width > 0.0 && line_width >= effective_page_width * 0.45;
    let relative_multiplier = if likely_multi_column_line { 2.5 } else { 3.0 };
    let gap_threshold = (median_gap * relative_multiplier).max(median_point_size * 1.5);
    let absolute_gap_threshold = if likely_multi_column_line {
        28.0_f64.max(median_point_size * 3.0)
    } else {
        50.0_f64.max(median_point_size * 4.0)
    };

    let split_after = gaps
        .iter()
        .enumerate()
        .filter_map(|(index, gap)| {
            (*gap > gap_threshold || *gap > absolute_gap_threshold).then_some(index)
        })
        .collect::<Vec<_>>();
    if split_after.is_empty() {
        return vec![glyphs];
    }

    let mut split_lines = Vec::with_capacity(split_after.len() + 1);
    let mut start = 0;
    for end in split_after {
        split_lines.push(glyphs[start..=end].to_vec());
        start = end + 1;
    }
    if start < glyphs.len() {
        split_lines.push(glyphs[start..].to_vec());
    }

    split_lines
}

fn order_pdf_lines_by_layout(lines: &[PdfSourceLine], page_width: f64) -> Vec<PdfSourceLine> {
    if lines.len() < 8 {
        let mut ordered = lines.to_vec();
        sort_pdf_lines_row_wise(&mut ordered);
        return ordered;
    }

    let width = positive_or_fallback(page_width, inferred_page_width(lines));
    let mid = width / 2.0;
    let left_lines = lines
        .iter()
        .filter(|line| pdf_source_line_center_x(line) < mid * 0.92)
        .cloned()
        .collect::<Vec<_>>();
    let right_lines = lines
        .iter()
        .filter(|line| pdf_source_line_center_x(line) > mid * 1.08)
        .cloned()
        .collect::<Vec<_>>();

    if looks_like_row_aligned_grid(lines, width) {
        return order_pdf_lines_row_wise(lines);
    }

    let is_two_column = (left_lines.len() as f64) >= (lines.len() as f64) * 0.25
        && (right_lines.len() as f64) >= (lines.len() as f64) * 0.25;
    if !is_two_column {
        return order_pdf_lines_row_wise(lines);
    }

    let mut ordered_left = left_lines;
    sort_pdf_lines_row_wise(&mut ordered_left);
    let mut ordered_right = right_lines;
    sort_pdf_lines_row_wise(&mut ordered_right);

    let mut ordered = Vec::with_capacity(lines.len());
    ordered.extend(ordered_left);
    ordered.extend(ordered_right);

    let mut remaining = lines
        .iter()
        .filter(|line| {
            let center_x = pdf_source_line_center_x(line);
            !(center_x < mid * 0.92 || center_x > mid * 1.08)
        })
        .cloned()
        .collect::<Vec<_>>();
    sort_pdf_lines_row_wise(&mut remaining);
    ordered.extend(remaining);
    ordered
}

fn order_pdf_lines_row_wise(lines: &[PdfSourceLine]) -> Vec<PdfSourceLine> {
    let mut ordered = lines.to_vec();
    sort_pdf_lines_row_wise(&mut ordered);
    ordered
}

fn sort_pdf_lines_row_wise(lines: &mut [PdfSourceLine]) {
    lines.sort_by(|left, right| {
        right
            .bounds
            .top
            .total_cmp(&left.bounds.top)
            .then_with(|| left.bounds.left.total_cmp(&right.bounds.left))
    });
}

fn looks_like_row_aligned_grid(lines: &[PdfSourceLine], page_width: f64) -> bool {
    if lines.len() < 6 {
        return false;
    }

    let row_tolerance = same_row_threshold(lines);
    let rows = group_pdf_lines_into_rows(lines, row_tolerance);
    if rows.len() < 3 {
        return false;
    }

    let multi_cell_rows = rows.iter().filter(|row| row.len() >= 2).count();
    if multi_cell_rows < 2 {
        return false;
    }

    let wide_rows = rows
        .iter()
        .filter(|row| row.len() >= 2)
        .filter(|row| {
            let left = row
                .iter()
                .map(|line| line.bounds.left)
                .reduce(f64::min)
                .unwrap_or_default();
            let right = row
                .iter()
                .map(|line| line.bounds.right)
                .reduce(f64::max)
                .unwrap_or_default();
            (right - left) > page_width * 0.45
        })
        .count();
    let ratio = (multi_cell_rows as f64) / (rows.len().max(1) as f64);
    ratio >= 0.20 && wide_rows >= 1
}

fn group_pdf_lines_into_rows(
    lines: &[PdfSourceLine],
    row_tolerance: f64,
) -> Vec<Vec<PdfSourceLine>> {
    let mut ordered = order_pdf_lines_row_wise(lines);
    let mut rows = Vec::<Vec<PdfSourceLine>>::new();
    for line in ordered.drain(..) {
        if let Some(row) = rows
            .iter_mut()
            .find(|row| (row[0].bounds.top - line.bounds.top).abs() <= row_tolerance)
        {
            row.push(line);
        } else {
            rows.push(vec![line]);
        }
    }

    for row in &mut rows {
        row.sort_by(|left, right| left.bounds.left.total_cmp(&right.bounds.left));
    }
    rows
}

fn should_merge_formula_continuation(
    previous: &PdfSourceLine,
    current: &PdfSourceLine,
    vertical_gap: f64,
    paragraph_gap_threshold: f64,
    same_row: bool,
) -> bool {
    if !same_row && vertical_gap > 6.0_f64.max(paragraph_gap_threshold * 0.6) {
        return false;
    }

    looks_like_formula_continuation_text(&current.text)
        || previous_line_likely_expects_formula_tail(&previous.text)
}

fn same_row_threshold(lines: &[PdfSourceLine]) -> f64 {
    let median_height = median_positive(
        lines
            .iter()
            .map(|line| (line.bounds.top - line.bounds.bottom).max(0.0)),
        12.0,
    );
    2.5_f64.max(median_height * 0.35)
}

fn pdf_source_line_width(line: &PdfSourceLine) -> f64 {
    (line.bounds.right - line.bounds.left).max(1.0)
}

fn pdf_source_line_center_x(line: &PdfSourceLine) -> f64 {
    line.bounds.left + pdf_source_line_width(line) / 2.0
}

pub fn build_pdf_source_layout_profile(
    lines: &[PdfSourceLine],
    page_width: f64,
    page_height: f64,
) -> PdfSourceLayoutProfile {
    let page_width = positive_or_fallback(page_width, inferred_page_width(lines));
    let page_height = positive_or_fallback(page_height, inferred_page_height(lines));

    if lines.is_empty() {
        return PdfSourceLayoutProfile {
            page_width,
            page_height,
            is_two_column: false,
            left_column_boundary: page_width * 0.45,
            right_column_boundary: page_width * 0.55,
            header_top_threshold: page_height * 0.92,
            footer_bottom_threshold: page_height * 0.08,
        };
    }

    let mut centers = lines
        .iter()
        .map(|line| (line.bounds.left + line.bounds.right) / 2.0)
        .filter(|value| value.is_finite())
        .collect::<Vec<_>>();
    centers.sort_by(f64::total_cmp);
    let p25 = percentile_by_floor(&centers, 0.25).unwrap_or(page_width * 0.45);
    let p75 = percentile_by_floor(&centers, 0.75).unwrap_or(page_width * 0.55);
    let is_two_column = (p75 - p25) > page_width * 0.22;

    let max_top = lines
        .iter()
        .map(|line| line.bounds.top)
        .filter(|value| value.is_finite())
        .reduce(f64::max)
        .unwrap_or(page_height);
    let min_bottom = lines
        .iter()
        .map(|line| line.bounds.bottom)
        .filter(|value| value.is_finite())
        .reduce(f64::min)
        .unwrap_or(0.0);

    PdfSourceLayoutProfile {
        page_width,
        page_height,
        is_two_column,
        left_column_boundary: if is_two_column {
            p25
        } else {
            page_width * 0.45
        },
        right_column_boundary: if is_two_column {
            p75
        } else {
            page_width * 0.55
        },
        header_top_threshold: (page_height * 0.88).max(max_top - page_height * 0.05),
        footer_bottom_threshold: (page_height * 0.08).min(min_bottom + page_height * 0.05),
    }
}

pub fn infer_pdf_source_region_type(
    profile: PdfSourceLayoutProfile,
    bounds: PdfBlockBounds,
    block_text: &str,
) -> PdfSourceLayoutRegion {
    let center_x = (bounds.left + bounds.right) / 2.0;
    let block_height = (bounds.top - bounds.bottom).max(1.0);
    let block_width = (bounds.right - bounds.left).max(1.0);

    if bounds.top >= profile.header_top_threshold {
        return PdfSourceLayoutRegion::Header;
    }

    if bounds.bottom <= profile.footer_bottom_threshold {
        return PdfSourceLayoutRegion::Footer;
    }

    if pdf_source_text_looks_like_table(block_text)
        || (block_width > profile.page_width * 0.8 && block_height < profile.page_height * 0.1)
    {
        return PdfSourceLayoutRegion::TableLike;
    }

    if profile.is_two_column {
        if center_x <= profile.left_column_boundary {
            return PdfSourceLayoutRegion::LeftColumn;
        }

        if center_x >= profile.right_column_boundary {
            return PdfSourceLayoutRegion::RightColumn;
        }
    } else {
        if center_x < profile.page_width * 0.46 {
            return PdfSourceLayoutRegion::LeftColumn;
        }

        if center_x > profile.page_width * 0.54 {
            return PdfSourceLayoutRegion::RightColumn;
        }
    }

    PdfSourceLayoutRegion::Body
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct PdfSourceOnnxRegion {
    region_type: PdfSourceLayoutRegion,
    confidence: f32,
    bounds: PdfBlockBounds,
}

#[derive(Clone, Debug, PartialEq)]
struct PdfSourceTatrCellRegion {
    bounds: PdfBlockBounds,
}

#[derive(Clone, Debug, PartialEq)]
struct PdfSourceTatrTableRegion {
    bounds: PdfBlockBounds,
    cells: Vec<PdfSourceTatrCellRegion>,
}

#[derive(Clone, Debug, PartialEq)]
struct PdfSourceTatrLayout {
    tables: Vec<PdfSourceTatrTableRegion>,
}

fn pdf_source_onnx_region_from_detection(
    detection: &DocLayoutYoloDetection,
    image_scale_x: f64,
    image_scale_y: f64,
    page_height: f64,
) -> Option<PdfSourceOnnxRegion> {
    if detection.confidence < DOC_LAYOUT_YOLO_MIN_BLOCK_CONFIDENCE {
        return None;
    }

    let region_type = pdf_source_region_from_doc_layout_yolo_region(detection.region_type)?;
    let bounds = pdf_bounds_from_image_rect(
        detection.x,
        detection.y,
        detection.width,
        detection.height,
        image_scale_x,
        image_scale_y,
        page_height,
    )?;
    Some(PdfSourceOnnxRegion {
        region_type,
        confidence: detection.confidence,
        bounds,
    })
}

fn pdf_source_tatr_layout_from_structures(
    layout: &PdfSourcePageTableStructures,
    image_scale_x: f64,
    image_scale_y: f64,
    page_height: f64,
) -> Option<PdfSourceTatrLayout> {
    let tables = layout
        .tables
        .iter()
        .filter_map(|structure| {
            pdf_source_tatr_table_region_from_structure(
                structure,
                image_scale_x,
                image_scale_y,
                page_height,
            )
        })
        .collect::<Vec<_>>();

    (!tables.is_empty()).then_some(PdfSourceTatrLayout { tables })
}

fn pdf_source_tatr_table_region_from_structure(
    structure: &TableStructure,
    image_scale_x: f64,
    image_scale_y: f64,
    page_height: f64,
) -> Option<PdfSourceTatrTableRegion> {
    let cells = structure
        .cells
        .iter()
        .filter_map(|cell| {
            pdf_bounds_from_table_cell(cell, image_scale_x, image_scale_y, page_height)
                .map(|bounds| PdfSourceTatrCellRegion { bounds })
        })
        .collect::<Vec<_>>();
    if cells.is_empty() {
        return None;
    }

    let table_bounds =
        table_structure_pdf_bounds(structure, image_scale_x, image_scale_y, page_height)
            .or_else(|| union_pdf_bounds(cells.iter().map(|cell| cell.bounds)))?;

    Some(PdfSourceTatrTableRegion {
        bounds: table_bounds,
        cells,
    })
}

fn table_structure_pdf_bounds(
    structure: &TableStructure,
    image_scale_x: f64,
    image_scale_y: f64,
    page_height: f64,
) -> Option<PdfBlockBounds> {
    let row_bounds = structure.rows.iter().filter_map(|detection| {
        pdf_bounds_from_table_detection(detection, image_scale_x, image_scale_y, page_height)
    });
    let column_bounds = structure.columns.iter().filter_map(|detection| {
        pdf_bounds_from_table_detection(detection, image_scale_x, image_scale_y, page_height)
    });
    let cell_bounds = structure.cells.iter().filter_map(|cell| {
        pdf_bounds_from_table_cell(cell, image_scale_x, image_scale_y, page_height)
    });

    union_pdf_bounds(row_bounds.chain(column_bounds).chain(cell_bounds))
}

fn pdf_bounds_from_table_detection(
    detection: &TableSubDetection,
    image_scale_x: f64,
    image_scale_y: f64,
    page_height: f64,
) -> Option<PdfBlockBounds> {
    pdf_bounds_from_image_rect(
        detection.x,
        detection.y,
        detection.width,
        detection.height,
        image_scale_x,
        image_scale_y,
        page_height,
    )
}

fn pdf_bounds_from_table_cell(
    cell: &TableCellBounds,
    image_scale_x: f64,
    image_scale_y: f64,
    page_height: f64,
) -> Option<PdfBlockBounds> {
    pdf_bounds_from_image_rect(
        cell.x,
        cell.y,
        cell.width,
        cell.height,
        image_scale_x,
        image_scale_y,
        page_height,
    )
}

fn pdf_bounds_from_image_rect(
    image_x: f64,
    image_y: f64,
    image_width: f64,
    image_height: f64,
    image_scale_x: f64,
    image_scale_y: f64,
    page_height: f64,
) -> Option<PdfBlockBounds> {
    let x = image_x / image_scale_x;
    let y = page_height - (image_y + image_height) / image_scale_y;
    let width = image_width / image_scale_x;
    let height = image_height / image_scale_y;
    if !x.is_finite()
        || !y.is_finite()
        || !width.is_finite()
        || !height.is_finite()
        || width <= 0.0
        || height <= 0.0
    {
        return None;
    }

    Some(PdfBlockBounds::from_lrtb(
        x.max(0.0),
        (x + width).max(0.0),
        (y + height).max(0.0),
        y.max(0.0),
    ))
}

fn build_tatr_table_blocks_for_page(
    page: &PdfSourcePage,
    layout: &PdfSourceTatrLayout,
) -> Vec<Vec<PdfSourceBlock>> {
    let mut table_cell_lines = layout
        .tables
        .iter()
        .map(|table| vec![Vec::<PdfSourceLine>::new(); table.cells.len()])
        .collect::<Vec<_>>();
    let mut table_orphan_lines = vec![Vec::<PdfSourceLine>::new(); layout.tables.len()];

    for line in &page.lines {
        assign_line_to_tatr_tables(line, layout, &mut table_cell_lines, &mut table_orphan_lines);
    }

    let layout_profile = build_pdf_source_layout_profile(&page.lines, page.width, page.height);
    layout
        .tables
        .iter()
        .enumerate()
        .map(|(table_index, _)| {
            build_tatr_blocks_for_table(
                page.page_number,
                layout_profile,
                &table_cell_lines[table_index],
                &table_orphan_lines[table_index],
            )
        })
        .collect()
}

fn assign_line_to_tatr_tables(
    line: &PdfSourceLine,
    layout: &PdfSourceTatrLayout,
    table_cell_lines: &mut [Vec<Vec<PdfSourceLine>>],
    table_orphan_lines: &mut [Vec<PdfSourceLine>],
) {
    if line.glyphs.is_empty() {
        let center_x = (line.bounds.left + line.bounds.right) / 2.0;
        let center_y = (line.bounds.bottom + line.bounds.top) / 2.0;
        if let Some((table_index, cell_index)) =
            best_tatr_cell_for_point(layout, center_x, center_y)
        {
            table_cell_lines[table_index][cell_index].push(line.clone());
        } else if let Some(table_index) = best_tatr_table_for_point(layout, center_x, center_y) {
            table_orphan_lines[table_index].push(line.clone());
        }
        return;
    }

    let mut cell_glyphs = layout
        .tables
        .iter()
        .map(|table| vec![Vec::<PdfGlyph>::new(); table.cells.len()])
        .collect::<Vec<_>>();
    let mut orphan_glyphs = vec![Vec::<PdfGlyph>::new(); layout.tables.len()];

    for glyph in &line.glyphs {
        let center_x = (glyph.bounds.left + glyph.bounds.right) / 2.0;
        let center_y = (glyph.bounds.bottom + glyph.bounds.top) / 2.0;
        if let Some((table_index, cell_index)) =
            best_tatr_cell_for_point(layout, center_x, center_y)
        {
            cell_glyphs[table_index][cell_index].push(glyph.clone());
        } else if let Some(table_index) = best_tatr_table_for_point(layout, center_x, center_y) {
            orphan_glyphs[table_index].push(glyph.clone());
        }
    }

    for (table_index, cells) in cell_glyphs.into_iter().enumerate() {
        for (cell_index, glyphs) in cells.into_iter().enumerate() {
            if let Some(cell_line) = pdf_source_line_from_glyphs(line, glyphs) {
                table_cell_lines[table_index][cell_index].push(cell_line);
            }
        }
    }
    for (table_index, glyphs) in orphan_glyphs.into_iter().enumerate() {
        if let Some(orphan_line) = pdf_source_line_from_glyphs(line, glyphs) {
            table_orphan_lines[table_index].push(orphan_line);
        }
    }
}

fn build_tatr_blocks_for_table(
    page_number: usize,
    layout_profile: PdfSourceLayoutProfile,
    cell_lines: &[Vec<PdfSourceLine>],
    orphan_lines: &[PdfSourceLine],
) -> Vec<PdfSourceBlock> {
    let mut blocks = Vec::new();
    for lines in cell_lines {
        if lines.is_empty() {
            continue;
        }
        blocks.push(pdf_source_block_as_tatr_table_cell(build_pdf_source_block(
            page_number,
            blocks.len(),
            lines,
            layout_profile,
        )));
    }

    if !orphan_lines.is_empty() {
        blocks.push(pdf_source_block_as_tatr_table_cell(build_pdf_source_block(
            page_number,
            blocks.len(),
            orphan_lines,
            layout_profile,
        )));
    }

    blocks
}

fn pdf_source_block_as_tatr_table_cell(block: PdfSourceBlock) -> PdfSourceBlock {
    let mut updated = block;
    updated.region_type = PdfSourceLayoutRegion::TableLike;
    updated.source_block_type = SourceBlockType::TableCell;
    updated.source_block_id = build_pdf_source_block_id(
        updated.page_number,
        updated.block_index,
        updated.region_type,
    );
    updated
}

fn pdf_source_line_from_glyphs(
    source_line: &PdfSourceLine,
    glyphs: Vec<PdfGlyph>,
) -> Option<PdfSourceLine> {
    if glyphs.is_empty() {
        return None;
    }

    let bounds = bounds_for_glyphs(&glyphs);
    let text = glyphs
        .iter()
        .map(|glyph| glyph.value.as_str())
        .collect::<Vec<_>>()
        .join("");
    Some(PdfSourceLine {
        page_number: source_line.page_number,
        line_index: source_line.line_index,
        text,
        bounds,
        glyphs,
    })
}

fn best_tatr_cell_for_point(
    layout: &PdfSourceTatrLayout,
    x: f64,
    y: f64,
) -> Option<(usize, usize)> {
    layout
        .tables
        .iter()
        .enumerate()
        .flat_map(|(table_index, table)| {
            table
                .cells
                .iter()
                .enumerate()
                .map(move |(cell_index, cell)| (table_index, cell_index, cell))
        })
        .filter(|(_, _, cell)| pdf_bounds_contains_point(cell.bounds, x, y))
        .min_by(|(_, _, left), (_, _, right)| {
            pdf_bounds_area(left.bounds).total_cmp(&pdf_bounds_area(right.bounds))
        })
        .map(|(table_index, cell_index, _)| (table_index, cell_index))
}

fn best_tatr_table_for_point(layout: &PdfSourceTatrLayout, x: f64, y: f64) -> Option<usize> {
    layout
        .tables
        .iter()
        .enumerate()
        .filter(|(_, table)| pdf_bounds_contains_point(table.bounds, x, y))
        .min_by(|(_, left), (_, right)| {
            pdf_bounds_area(left.bounds).total_cmp(&pdf_bounds_area(right.bounds))
        })
        .map(|(index, _)| index)
}

fn tatr_layout_table_for_block(
    block: &PdfSourceBlock,
    layout: &PdfSourceTatrLayout,
) -> Option<usize> {
    let center_x = (block.bounds.left + block.bounds.right) / 2.0;
    let center_y = (block.bounds.bottom + block.bounds.top) / 2.0;
    if let Some(table_index) = best_tatr_table_for_point(layout, center_x, center_y) {
        return Some(table_index);
    }

    layout
        .tables
        .iter()
        .enumerate()
        .filter_map(|(index, table)| {
            let coverage = pdf_bounds_intersection_area(block.bounds, table.bounds)
                / pdf_bounds_area(block.bounds).max(1.0);
            (coverage >= DOC_LAYOUT_YOLO_MIN_BLOCK_COVERAGE).then_some((index, coverage))
        })
        .max_by(|(_, left), (_, right)| left.total_cmp(right))
        .map(|(index, _)| index)
}

fn union_pdf_bounds(bounds: impl IntoIterator<Item = PdfBlockBounds>) -> Option<PdfBlockBounds> {
    bounds.into_iter().fold(None, |acc, bounds| {
        Some(match acc {
            None => bounds,
            Some(current) => PdfBlockBounds {
                left: current.left.min(bounds.left),
                right: current.right.max(bounds.right),
                top: current.top.max(bounds.top),
                bottom: current.bottom.min(bounds.bottom),
            },
        })
    })
}

fn pdf_source_region_from_doc_layout_yolo_region(
    region_type: DocLayoutRegionType,
) -> Option<PdfSourceLayoutRegion> {
    match region_type {
        DocLayoutRegionType::Body => Some(PdfSourceLayoutRegion::Body),
        DocLayoutRegionType::Table | DocLayoutRegionType::TableLike => {
            Some(PdfSourceLayoutRegion::TableLike)
        }
        DocLayoutRegionType::Figure => Some(PdfSourceLayoutRegion::Figure),
        DocLayoutRegionType::Formula | DocLayoutRegionType::IsolatedFormula => {
            Some(PdfSourceLayoutRegion::Formula)
        }
        DocLayoutRegionType::Caption => Some(PdfSourceLayoutRegion::Caption),
        DocLayoutRegionType::Title => Some(PdfSourceLayoutRegion::Title),
        DocLayoutRegionType::Header
        | DocLayoutRegionType::Footer
        | DocLayoutRegionType::LeftColumn
        | DocLayoutRegionType::RightColumn
        | DocLayoutRegionType::Unknown => None,
    }
}

fn best_onnx_region_for_pdf_source_block<'a>(
    block: &PdfSourceBlock,
    regions: &'a [PdfSourceOnnxRegion],
) -> Option<&'a PdfSourceOnnxRegion> {
    let center_x = (block.bounds.left + block.bounds.right) / 2.0;
    let center_y = (block.bounds.bottom + block.bounds.top) / 2.0;

    if let Some(region) = regions
        .iter()
        .filter(|region| pdf_bounds_contains_point(region.bounds, center_x, center_y))
        .min_by(|left, right| {
            pdf_bounds_area(left.bounds)
                .total_cmp(&pdf_bounds_area(right.bounds))
                .then_with(|| right.confidence.total_cmp(&left.confidence))
        })
    {
        return Some(region);
    }

    regions
        .iter()
        .filter_map(|region| {
            let coverage = pdf_bounds_intersection_area(block.bounds, region.bounds)
                / pdf_bounds_area(block.bounds).max(1.0);
            (coverage >= DOC_LAYOUT_YOLO_MIN_BLOCK_COVERAGE).then_some((region, coverage))
        })
        .max_by(
            |(left_region, left_coverage), (right_region, right_coverage)| {
                left_coverage
                    .total_cmp(right_coverage)
                    .then_with(|| left_region.confidence.total_cmp(&right_region.confidence))
            },
        )
        .map(|(region, _)| region)
}

fn pdf_source_block_with_onnx_region(
    block: &PdfSourceBlock,
    region: &PdfSourceOnnxRegion,
) -> PdfSourceBlock {
    let mut updated = block.clone();
    updated.region_type = region.region_type;
    updated.source_block_id =
        build_pdf_source_block_id(block.page_number, block.block_index, region.region_type);
    updated.source_block_type = pdf_source_block_type_for_onnx_region(region.region_type, block);
    updated
}

fn reindex_pdf_source_blocks(blocks: Vec<PdfSourceBlock>) -> Vec<PdfSourceBlock> {
    blocks
        .into_iter()
        .enumerate()
        .map(|(block_index, mut block)| {
            block.block_index = block_index;
            block.source_block_id =
                build_pdf_source_block_id(block.page_number, block_index, block.region_type);
            block
        })
        .collect()
}

fn pdf_source_block_type_for_onnx_region(
    region_type: PdfSourceLayoutRegion,
    block: &PdfSourceBlock,
) -> SourceBlockType {
    match region_type {
        PdfSourceLayoutRegion::TableLike => SourceBlockType::TableCell,
        PdfSourceLayoutRegion::Formula => SourceBlockType::Formula,
        PdfSourceLayoutRegion::Caption => SourceBlockType::Caption,
        PdfSourceLayoutRegion::Title => SourceBlockType::Heading,
        PdfSourceLayoutRegion::Figure => SourceBlockType::Unknown,
        PdfSourceLayoutRegion::Unknown
        | PdfSourceLayoutRegion::Body
        | PdfSourceLayoutRegion::Header
        | PdfSourceLayoutRegion::Footer
        | PdfSourceLayoutRegion::LeftColumn
        | PdfSourceLayoutRegion::RightColumn => block.source_block_type,
    }
}

fn pdf_bounds_contains_point(bounds: PdfBlockBounds, x: f64, y: f64) -> bool {
    x >= bounds.left && x <= bounds.right && y >= bounds.bottom && y <= bounds.top
}

fn pdf_bounds_intersection_area(left: PdfBlockBounds, right: PdfBlockBounds) -> f64 {
    let x1 = left.left.max(right.left);
    let y1 = left.bottom.max(right.bottom);
    let x2 = left.right.min(right.right);
    let y2 = left.top.min(right.top);
    ((x2 - x1).max(0.0)) * ((y2 - y1).max(0.0))
}

fn pdf_bounds_area(bounds: PdfBlockBounds) -> f64 {
    ((bounds.right - bounds.left).max(0.0)) * ((bounds.top - bounds.bottom).max(0.0))
}

fn build_pdf_source_block_id(
    page_number: usize,
    block_index: usize,
    region_type: PdfSourceLayoutRegion,
) -> String {
    format!(
        "p{}-{}-b{}",
        page_number,
        pdf_source_region_tag(region_type),
        block_index + 1
    )
}

fn pdf_source_region_tag(region_type: PdfSourceLayoutRegion) -> &'static str {
    match region_type {
        PdfSourceLayoutRegion::Header => "header",
        PdfSourceLayoutRegion::Footer => "footer",
        PdfSourceLayoutRegion::LeftColumn => "left",
        PdfSourceLayoutRegion::RightColumn => "right",
        PdfSourceLayoutRegion::TableLike => "table",
        PdfSourceLayoutRegion::Figure => "figure",
        PdfSourceLayoutRegion::Formula => "formula",
        PdfSourceLayoutRegion::Caption => "caption",
        PdfSourceLayoutRegion::Title => "title",
        PdfSourceLayoutRegion::Unknown | PdfSourceLayoutRegion::Body => "body",
    }
}

fn pdf_source_text_looks_like_table(text: &str) -> bool {
    text.contains('\t')
        || text.contains("  ")
        || text.contains('|')
        || table_number_regex().is_match(text)
}

fn table_number_regex() -> &'static Regex {
    static TABLE_NUMBER_REGEX: OnceLock<Regex> = OnceLock::new();
    TABLE_NUMBER_REGEX.get_or_init(|| {
        Regex::new(r"\b\d+(\.\d+)?\b\s+\b\d+(\.\d+)?\b").expect("table number regex should compile")
    })
}

fn percentile_by_floor(values: &[f64], percentile: f64) -> Option<f64> {
    if values.is_empty() {
        return None;
    }

    let index = ((values.len().saturating_sub(1) as f64) * percentile).floor() as usize;
    values.get(index).copied()
}

fn inferred_page_width(lines: &[PdfSourceLine]) -> f64 {
    lines
        .iter()
        .map(|line| line.bounds.right)
        .filter(|value| value.is_finite())
        .reduce(f64::max)
        .unwrap_or(1.0)
        .max(1.0)
}

fn inferred_page_height(lines: &[PdfSourceLine]) -> f64 {
    lines
        .iter()
        .map(|line| line.bounds.top)
        .filter(|value| value.is_finite())
        .reduce(f64::max)
        .unwrap_or(1.0)
        .max(1.0)
}

fn bounds_for_glyphs(glyphs: &[PdfGlyph]) -> PdfBlockBounds {
    PdfBlockBounds {
        left: glyphs
            .iter()
            .map(|glyph| glyph.bounds.left)
            .reduce(f64::min)
            .unwrap_or(0.0),
        right: glyphs
            .iter()
            .map(|glyph| glyph.bounds.right)
            .reduce(f64::max)
            .unwrap_or(0.0),
        top: glyphs
            .iter()
            .map(|glyph| glyph.bounds.top)
            .reduce(f64::max)
            .unwrap_or(0.0),
        bottom: glyphs
            .iter()
            .map(|glyph| glyph.bounds.bottom)
            .reduce(f64::min)
            .unwrap_or(0.0),
    }
}

fn detected_font_names(glyphs: &[PdfGlyph]) -> Vec<String> {
    glyphs
        .iter()
        .map(|glyph| strip_subset_prefix(glyph.font_name.trim()))
        .filter(|font_name| !font_name.is_empty())
        .map(str::to_string)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect()
}

fn median_positive(values: impl Iterator<Item = f64>, default_value: f64) -> f64 {
    let mut values = values
        .filter(|value| value.is_finite() && *value > 0.0)
        .collect::<Vec<_>>();
    if values.is_empty() {
        return default_value;
    }
    values.sort_by(f64::total_cmp);
    values[values.len() / 2]
}

fn text_style_for_source_block(
    lines: &[PdfSourceLine],
    glyphs: &[PdfGlyph],
) -> Option<PdfExportBlockTextStyle> {
    if glyphs.is_empty() {
        return None;
    }

    let font_size = round_half_away_from_zero(median_positive(
        glyphs.iter().map(|glyph| glyph.point_size),
        0.0,
    ));
    let line_spacing = line_spacing_for_source_lines(lines);
    let rotation_angle = dominant_rotation_angle(glyphs);

    Some(PdfExportBlockTextStyle {
        font_size,
        line_spacing,
        rotation_angle,
    })
}

fn line_spacing_for_source_lines(lines: &[PdfSourceLine]) -> f64 {
    if lines.len() < 2 {
        return 0.0;
    }

    let mut baselines = lines
        .iter()
        .map(|line| line.bounds.bottom)
        .filter(|value| value.is_finite())
        .collect::<Vec<_>>();
    baselines.sort_by(|left, right| right.total_cmp(left));

    let mut gaps = baselines
        .windows(2)
        .map(|window| (window[0] - window[1]).abs())
        .filter(|gap| *gap > 0.5)
        .collect::<Vec<_>>();
    if gaps.is_empty() {
        return 0.0;
    }
    gaps.sort_by(f64::total_cmp);
    gaps[gaps.len() / 2]
}

fn dominant_rotation_angle(glyphs: &[PdfGlyph]) -> f64 {
    let mut counts = [0usize; 5];
    for glyph in glyphs {
        let index = match glyph.orientation {
            PdfTextOrientation::Horizontal => 0,
            PdfTextOrientation::Rotate90 => 1,
            PdfTextOrientation::Rotate180 => 2,
            PdfTextOrientation::Rotate270 => 3,
            PdfTextOrientation::Other => 4,
        };
        counts[index] += 1;
    }

    let (dominant, _) = counts
        .iter()
        .enumerate()
        .max_by_key(|(_, count)| *count)
        .unwrap_or((0, &0));
    match dominant {
        1 => 90.0,
        2 => 180.0,
        3 => -90.0,
        _ => 0.0,
    }
}

fn round_half_away_from_zero(value: f64) -> f64 {
    if !value.is_finite() || value <= 0.0 {
        return value;
    }

    (value * 2.0).round() / 2.0
}

fn pdf_rect_from_block_bounds(bounds: PdfBlockBounds) -> PdfRect {
    PdfRect::new(
        bounds.left,
        bounds.bottom,
        (bounds.right - bounds.left).max(1.0),
        (bounds.top - bounds.bottom).max(1.0),
    )
}

fn pdf_export_source_block_type(source_block_type: SourceBlockType) -> PdfExportSourceBlockType {
    match source_block_type {
        SourceBlockType::Unknown => PdfExportSourceBlockType::Unknown,
        SourceBlockType::Paragraph => PdfExportSourceBlockType::Paragraph,
        SourceBlockType::Heading => PdfExportSourceBlockType::Heading,
        SourceBlockType::Caption => PdfExportSourceBlockType::Caption,
        SourceBlockType::TableCell => PdfExportSourceBlockType::TableCell,
        SourceBlockType::Formula => PdfExportSourceBlockType::Formula,
    }
}

pub fn calculate_pdf_source_reading_order_score(
    order_in_page: usize,
    page_block_count: usize,
) -> f64 {
    if page_block_count <= 1 {
        return 1.0;
    }

    let denominator = page_block_count.saturating_sub(1).max(1) as f64;
    let normalized = 1.0 - ((order_in_page as f64) / denominator).clamp(0.0, 1.0);
    (normalized * 10_000.0).round() / 10_000.0
}

fn formula_heuristic_regex() -> &'static Regex {
    static FORMULA_HEURISTIC_REGEX: OnceLock<Regex> = OnceLock::new();
    FORMULA_HEURISTIC_REGEX.get_or_init(|| {
        Regex::new(r"(\$[^$]+\$|\\\([^)]+\\\)|\\\[[^\]]+\\\]|\b\w+\s*=\s*[-+*/^()\w\u{221A}]+)")
            .expect("formula heuristic regex should compile")
    })
}

fn natural_word_regex() -> &'static Regex {
    static NATURAL_WORD_REGEX: OnceLock<Regex> = OnceLock::new();
    NATURAL_WORD_REGEX
        .get_or_init(|| Regex::new(r"\b[a-zA-Z]{4,}\b").expect("natural word regex should compile"))
}

trait PdfGlyphOptionalTextMatrix {
    fn with_optional_text_matrix(self, matrix: Option<TextMatrix>) -> Self;
}

impl PdfGlyphOptionalTextMatrix for PdfGlyph {
    fn with_optional_text_matrix(self, matrix: Option<TextMatrix>) -> Self {
        if let Some(matrix) = matrix {
            self.with_text_matrix(matrix)
        } else {
            self
        }
    }
}
