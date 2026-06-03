use std::fmt;
use std::path::Path;

use crate::pdf_content_stream::replace_text_operator_in_stream_bytes;
use crate::pdf_export_blocks::{
    build_translated_block_lookup, should_render_block_text, PdfExportCheckpoint,
    PdfTranslatedBlock,
};

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NativePdfContentStreamExportSummary {
    pub pages_visited: usize,
    pub pages_preserved_due_to_patch_failure: usize,
    pub blocks_considered: usize,
    pub blocks_patched: usize,
    pub blocks_preserved: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NativePdfContentStreamExportFailureKind {
    OpenInput,
    ReadPageContent,
    UpdatePageContent,
    WriteOutput,
    NoReplacements,
    NeedsFontEmbedding,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NativePdfContentStreamExportError {
    pub kind: NativePdfContentStreamExportFailureKind,
    pub message: String,
}

impl NativePdfContentStreamExportError {
    fn new(kind: NativePdfContentStreamExportFailureKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
        }
    }
}

impl fmt::Display for NativePdfContentStreamExportError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for NativePdfContentStreamExportError {}

pub fn export_pdf_with_content_stream_replacement(
    input_path: impl AsRef<Path>,
    output_path: impl AsRef<Path>,
    checkpoint: &PdfExportCheckpoint,
    selected_page_numbers: Option<&[u32]>,
) -> Result<NativePdfContentStreamExportSummary, NativePdfContentStreamExportError> {
    let mut document = lopdf::Document::load(input_path.as_ref()).map_err(|error| {
        NativePdfContentStreamExportError::new(
            NativePdfContentStreamExportFailureKind::OpenInput,
            format!("Could not open PDF input: {error}"),
        )
    })?;
    let block_lookup = build_translated_block_lookup(checkpoint);
    if block_lookup.is_empty() {
        return Err(NativePdfContentStreamExportError::new(
            NativePdfContentStreamExportFailureKind::NoReplacements,
            "No PDF blocks are available for native content-stream replacement",
        ));
    }

    let expected_rewrites = block_lookup
        .values()
        .flat_map(|blocks| blocks.iter())
        .filter(|block| block_requires_operator_rewrite(block))
        .count();

    let mut summary = NativePdfContentStreamExportSummary {
        pages_visited: 0,
        pages_preserved_due_to_patch_failure: 0,
        blocks_considered: 0,
        blocks_patched: 0,
        blocks_preserved: 0,
    };

    for (page_number, page_id) in document.get_pages() {
        let Some(page_blocks) = block_lookup.get(&(page_number as i32)) else {
            continue;
        };

        summary.pages_visited += 1;
        let mut content = document.get_page_content(page_id).map_err(|error| {
            NativePdfContentStreamExportError::new(
                NativePdfContentStreamExportFailureKind::ReadPageContent,
                format!("Could not read PDF page {page_number} content stream: {error}"),
            )
        })?;
        let mut page_blocks_considered = 0usize;
        let mut page_blocks_patched = 0usize;
        let mut page_blocks_preserved = 0usize;
        let mut page_blocks_expected_rewrite = 0usize;
        let mut page_blocks = page_blocks.clone();
        page_blocks.sort_by(|left, right| {
            left.order_in_page
                .cmp(&right.order_in_page)
                .then_with(|| left.chunk_index.cmp(&right.chunk_index))
        });

        for block in page_blocks {
            page_blocks_considered += 1;
            if block_should_preserve_original(&block) {
                page_blocks_preserved += 1;
                continue;
            }

            if !is_safe_pdf_literal_replacement_text(&block.translated_text) {
                return Err(NativePdfContentStreamExportError::new(
                    NativePdfContentStreamExportFailureKind::NeedsFontEmbedding,
                    format!(
                        "PDF block '{}' translation needs font embedding",
                        block.source_block_id,
                    ),
                ));
            }

            page_blocks_expected_rewrite += 1;
            if let Some(patched) = replace_text_operator_in_stream_bytes(
                &content,
                &block.source_text,
                &block.translated_text,
            ) {
                content = patched;
                page_blocks_patched += 1;
            }
        }

        summary.blocks_considered += page_blocks_considered;
        if page_blocks_expected_rewrite == page_blocks_patched {
            summary.blocks_preserved += page_blocks_preserved;
        } else {
            summary.pages_preserved_due_to_patch_failure += 1;
            summary.blocks_preserved += page_blocks_preserved + page_blocks_expected_rewrite;
            continue;
        }

        if page_blocks_patched > 0 {
            document
                .change_page_content(page_id, content)
                .map_err(|error| {
                    NativePdfContentStreamExportError::new(
                        NativePdfContentStreamExportFailureKind::UpdatePageContent,
                        format!("Could not update PDF page {page_number} content stream: {error}",),
                    )
                })?;
            summary.blocks_patched += page_blocks_patched;
        }
    }

    if expected_rewrites > 0 && summary.blocks_patched == 0 {
        return Err(NativePdfContentStreamExportError::new(
            NativePdfContentStreamExportFailureKind::NoReplacements,
            format!("Could not patch any PDF text operators (expected {expected_rewrites})"),
        ));
    }

    if summary.blocks_patched == 0 && summary.blocks_preserved == 0 {
        return Err(NativePdfContentStreamExportError::new(
            NativePdfContentStreamExportFailureKind::NoReplacements,
            "No PDF text operators were eligible for native replacement",
        ));
    }

    if let Some(selected_page_numbers) = selected_page_numbers {
        retain_pdf_pages(&mut document, selected_page_numbers);
    }

    document.save(output_path.as_ref()).map_err(|error| {
        NativePdfContentStreamExportError::new(
            NativePdfContentStreamExportFailureKind::WriteOutput,
            format!("Could not write PDF output: {error}"),
        )
    })?;

    Ok(summary)
}

fn retain_pdf_pages(document: &mut lopdf::Document, selected_page_numbers: &[u32]) {
    let selected = selected_page_numbers
        .iter()
        .copied()
        .collect::<std::collections::BTreeSet<_>>();
    if selected.is_empty() {
        return;
    }

    let pages_to_delete = document
        .get_pages()
        .keys()
        .copied()
        .filter(|page_number| !selected.contains(page_number))
        .collect::<Vec<_>>();
    if pages_to_delete.is_empty() {
        return;
    }

    document.delete_pages(&pages_to_delete);
    document.prune_objects();
    document.renumber_objects();
}

fn block_requires_operator_rewrite(block: &PdfTranslatedBlock) -> bool {
    !block_should_preserve_original(block)
}

fn block_should_preserve_original(block: &PdfTranslatedBlock) -> bool {
    block.preserve_original_text_in_pdf_export
        || block.translation_skipped
        || block.uses_source_fallback
        || !should_render_block_text(block)
        || block.source_text.trim() == block.translated_text.trim()
}

fn is_safe_pdf_literal_replacement_text(text: &str) -> bool {
    text.chars()
        .all(|ch| ch.is_ascii() && (!ch.is_control() || matches!(ch, '\n' | '\r' | '\t')))
}
