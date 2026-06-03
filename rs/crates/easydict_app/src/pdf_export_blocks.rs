use std::collections::{BTreeMap, BTreeSet};

use crate::PdfRect;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PdfExportSourceBlockType {
    Paragraph,
    Heading,
    Caption,
    TableCell,
    Formula,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfExportBlockTextStyle {
    pub font_size: f64,
    pub line_spacing: f64,
    pub rotation_angle: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfExportChunkMetadata {
    pub chunk_index: usize,
    pub page_number: i32,
    pub source_block_id: String,
    pub source_block_type: PdfExportSourceBlockType,
    pub order_in_page: i32,
    pub reading_order_score: f64,
    pub bounding_box: Option<PdfRect>,
    pub text_style: Option<PdfExportBlockTextStyle>,
    pub translation_skipped: bool,
    pub preserve_original_text_in_pdf_export: bool,
    pub retry_count: i32,
    pub fallback_text: Option<String>,
    pub detected_font_names: Option<Vec<String>>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfExportCheckpoint {
    pub source_chunks: Vec<String>,
    pub chunk_metadata: Vec<PdfExportChunkMetadata>,
    pub translated_chunks: BTreeMap<usize, String>,
    pub failed_chunk_indexes: BTreeSet<usize>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PdfRenderableText {
    pub text: String,
    pub uses_source_fallback: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfTranslatedBlock {
    pub chunk_index: usize,
    pub page_number: i32,
    pub source_block_id: String,
    pub order_in_page: i32,
    pub reading_order_score: f64,
    pub source_text: String,
    pub translated_text: String,
    pub bounding_box: Option<PdfRect>,
    pub font_size: f64,
    pub translation_skipped: bool,
    pub render_from_source_text: bool,
    pub skip_erase: bool,
    pub preserve_original_text_in_pdf_export: bool,
    pub text_style: Option<PdfExportBlockTextStyle>,
    pub source_block_type: PdfExportSourceBlockType,
    pub retry_count: i32,
    pub uses_source_fallback: bool,
    pub detected_font_names: Option<Vec<String>>,
    pub render_line_rects: Option<Vec<PdfRect>>,
    pub background_line_rects: Option<Vec<PdfRect>>,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfOverlayRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

impl From<PdfRect> for PdfOverlayRect {
    fn from(rect: PdfRect) -> Self {
        Self {
            x: rect.x,
            y: rect.y,
            width: rect.width,
            height: rect.height,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfOverlayBlock {
    pub page_index: usize,
    pub page_number: i32,
    pub source_block_id: String,
    pub text: String,
    pub rect: PdfOverlayRect,
    pub font_size: f64,
    pub text_style: Option<PdfExportBlockTextStyle>,
    pub detected_font_names: Option<Vec<String>>,
}

pub fn try_get_renderable_text(
    checkpoint: &PdfExportCheckpoint,
    chunk_index: usize,
) -> Option<PdfRenderableText> {
    if chunk_index >= checkpoint.source_chunks.len() {
        return None;
    }

    if let Some(translated) = checkpoint
        .translated_chunks
        .get(&chunk_index)
        .filter(|value| !value.trim().is_empty())
    {
        return Some(PdfRenderableText {
            text: translated.clone(),
            uses_source_fallback: false,
        });
    }

    if !checkpoint.failed_chunk_indexes.contains(&chunk_index) {
        return None;
    }

    let source = checkpoint
        .chunk_metadata
        .iter()
        .find(|metadata| metadata.chunk_index == chunk_index)
        .and_then(|metadata| metadata.fallback_text.as_deref())
        .filter(|text| !text.trim().is_empty())
        .unwrap_or(&checkpoint.source_chunks[chunk_index]);

    if source.trim().is_empty() {
        return None;
    }

    Some(PdfRenderableText {
        text: source.to_string(),
        uses_source_fallback: true,
    })
}

pub fn build_translated_block_lookup(
    checkpoint: &PdfExportCheckpoint,
) -> BTreeMap<i32, Vec<PdfTranslatedBlock>> {
    let metadata_by_chunk_index: BTreeMap<usize, &PdfExportChunkMetadata> = checkpoint
        .chunk_metadata
        .iter()
        .map(|metadata| (metadata.chunk_index, metadata))
        .collect();
    let mut result: BTreeMap<i32, Vec<PdfTranslatedBlock>> = BTreeMap::new();

    for chunk_index in 0..checkpoint.source_chunks.len() {
        let Some(metadata) = metadata_by_chunk_index.get(&chunk_index).copied() else {
            continue;
        };
        let Some(renderable) = try_get_renderable_text(checkpoint, chunk_index) else {
            continue;
        };

        let rotation_angle = metadata
            .text_style
            .map(|style| style.rotation_angle)
            .unwrap_or(0.0);
        let is_vertical = rotation_angle.abs() > 15.0;
        let translation_skipped = metadata.translation_skipped || is_vertical;
        let preserve_original_text_in_pdf_export =
            metadata.preserve_original_text_in_pdf_export && !is_vertical;

        result
            .entry(metadata.page_number)
            .or_default()
            .push(PdfTranslatedBlock {
                chunk_index,
                page_number: metadata.page_number,
                source_block_id: metadata.source_block_id.clone(),
                order_in_page: metadata.order_in_page,
                reading_order_score: metadata.reading_order_score,
                source_text: checkpoint.source_chunks[chunk_index].clone(),
                translated_text: renderable.text,
                bounding_box: metadata.bounding_box,
                font_size: metadata
                    .text_style
                    .map(|style| style.font_size)
                    .unwrap_or(10.0),
                translation_skipped,
                render_from_source_text: false,
                skip_erase: false,
                preserve_original_text_in_pdf_export,
                text_style: metadata.text_style,
                source_block_type: metadata.source_block_type,
                retry_count: metadata.retry_count,
                uses_source_fallback: renderable.uses_source_fallback,
                detected_font_names: metadata.detected_font_names.clone(),
                render_line_rects: None,
                background_line_rects: None,
            });
    }

    result
}

pub fn should_render_block_text(block: &PdfTranslatedBlock) -> bool {
    !block.translated_text.trim().is_empty() && !block.translation_skipped
}

pub fn should_erase_block_background(block: &PdfTranslatedBlock) -> bool {
    should_render_block_text(block) && !block.skip_erase
}

pub fn build_pdf_overlay_blocks(checkpoint: &PdfExportCheckpoint) -> Vec<PdfOverlayBlock> {
    checkpoint_to_overlay_blocks(checkpoint)
}

pub fn checkpoint_to_overlay_blocks(checkpoint: &PdfExportCheckpoint) -> Vec<PdfOverlayBlock> {
    let lookup = build_translated_block_lookup(checkpoint);
    let mut translated_blocks = lookup
        .values()
        .flat_map(|blocks| blocks.iter())
        .collect::<Vec<_>>();
    translated_blocks.sort_by(|left, right| {
        left.page_number
            .cmp(&right.page_number)
            .then_with(|| left.order_in_page.cmp(&right.order_in_page))
            .then_with(|| left.chunk_index.cmp(&right.chunk_index))
    });
    translated_blocks
        .into_iter()
        .filter_map(pdf_overlay_block_from_translated_block)
        .collect()
}

fn pdf_overlay_block_from_translated_block(block: &PdfTranslatedBlock) -> Option<PdfOverlayBlock> {
    if !should_include_overlay_block(block) {
        return None;
    }

    Some(PdfOverlayBlock {
        page_index: page_index_from_page_number(block.page_number),
        page_number: block.page_number,
        source_block_id: block.source_block_id.clone(),
        text: block.translated_text.clone(),
        rect: block.bounding_box?.into(),
        font_size: block.font_size,
        text_style: block.text_style,
        detected_font_names: block.detected_font_names.clone(),
    })
}

fn should_include_overlay_block(block: &PdfTranslatedBlock) -> bool {
    should_render_block_text(block)
        && block.bounding_box.is_some()
        && !block.preserve_original_text_in_pdf_export
        && !block.render_from_source_text
        && !block.uses_source_fallback
        && block.source_text.trim() != block.translated_text.trim()
        && !has_overlay_rotation(block.text_style)
}

fn has_overlay_rotation(text_style: Option<PdfExportBlockTextStyle>) -> bool {
    text_style
        .map(|style| style.rotation_angle.abs() > f64::EPSILON)
        .unwrap_or(false)
}

fn page_index_from_page_number(page_number: i32) -> usize {
    page_number
        .checked_sub(1)
        .and_then(|page_index| usize::try_from(page_index).ok())
        .unwrap_or(0)
}
