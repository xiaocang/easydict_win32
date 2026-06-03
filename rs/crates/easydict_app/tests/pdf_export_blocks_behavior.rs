use std::collections::{BTreeMap, BTreeSet};

use easydict_app::pdf_export_blocks::{build_pdf_overlay_blocks, checkpoint_to_overlay_blocks};
use easydict_app::{
    build_translated_block_lookup, should_erase_block_background, should_render_block_text,
    try_get_renderable_text, PdfExportBlockTextStyle, PdfExportCheckpoint, PdfExportChunkMetadata,
    PdfExportSourceBlockType, PdfRect,
};

fn metadata(chunk_index: usize) -> PdfExportChunkMetadata {
    PdfExportChunkMetadata {
        chunk_index,
        page_number: 1,
        source_block_id: format!("p1-body-b{chunk_index}"),
        source_block_type: PdfExportSourceBlockType::Paragraph,
        order_in_page: chunk_index as i32,
        reading_order_score: 1.0 - chunk_index as f64 * 0.1,
        bounding_box: Some(PdfRect::new(60.0, 680.0, 220.0, 40.0)),
        text_style: Some(PdfExportBlockTextStyle {
            font_size: 12.0,
            line_spacing: 14.0,
            rotation_angle: 0.0,
        }),
        translation_skipped: false,
        preserve_original_text_in_pdf_export: false,
        retry_count: 0,
        fallback_text: None,
        detected_font_names: Some(vec!["TimesNewRomanPSMT".to_string()]),
    }
}

fn checkpoint() -> PdfExportCheckpoint {
    PdfExportCheckpoint {
        source_chunks: vec![
            "Source zero.".to_string(),
            "Source one.".to_string(),
            "Source two.".to_string(),
        ],
        chunk_metadata: vec![metadata(0), metadata(1), metadata(2)],
        translated_chunks: BTreeMap::from([(0, "Translated zero.".to_string())]),
        failed_chunk_indexes: BTreeSet::new(),
    }
}

#[test]
fn native_pdf_export_blocks_use_translated_text_before_failed_source_fallback() {
    let mut checkpoint = checkpoint();
    checkpoint.failed_chunk_indexes.insert(0);
    checkpoint.chunk_metadata[0].fallback_text = Some("Fallback zero.".to_string());

    let renderable = try_get_renderable_text(&checkpoint, 0).expect("renderable");

    assert_eq!(renderable.text, "Translated zero.");
    assert!(!renderable.uses_source_fallback);
}

#[test]
fn native_pdf_export_blocks_use_metadata_fallback_text_for_failed_chunks() {
    let mut checkpoint = checkpoint();
    checkpoint.translated_chunks.clear();
    checkpoint.failed_chunk_indexes.insert(1);
    checkpoint.chunk_metadata[1].fallback_text = Some("Fallback one.".to_string());

    let renderable = try_get_renderable_text(&checkpoint, 1).expect("renderable");

    assert_eq!(renderable.text, "Fallback one.");
    assert!(renderable.uses_source_fallback);
}

#[test]
fn native_pdf_export_blocks_use_chunk_index_for_reordered_metadata_fallback() {
    let mut checkpoint = checkpoint();
    checkpoint.translated_chunks.clear();
    checkpoint.failed_chunk_indexes.insert(2);
    checkpoint.chunk_metadata[1].fallback_text = Some("Wrong fallback.".to_string());
    checkpoint.chunk_metadata[2].fallback_text = Some("Correct fallback.".to_string());
    checkpoint.chunk_metadata.swap(1, 2);

    let renderable = try_get_renderable_text(&checkpoint, 2).expect("renderable");

    assert_eq!(renderable.text, "Correct fallback.");
    assert!(renderable.uses_source_fallback);
}

#[test]
fn native_pdf_export_blocks_fall_back_to_source_only_for_failed_chunks() {
    let mut checkpoint = checkpoint();
    checkpoint.translated_chunks.clear();
    checkpoint.failed_chunk_indexes.insert(2);
    checkpoint.chunk_metadata[2].fallback_text = Some("   ".to_string());

    let renderable = try_get_renderable_text(&checkpoint, 2).expect("renderable");

    assert_eq!(renderable.text, "Source two.");
    assert!(renderable.uses_source_fallback);
    assert!(try_get_renderable_text(&checkpoint, 1).is_none());
    assert!(try_get_renderable_text(&checkpoint, 99).is_none());
}

#[test]
fn native_pdf_export_blocks_build_lookup_for_failed_source_fallback() {
    let mut checkpoint = checkpoint();
    checkpoint.translated_chunks.clear();
    checkpoint.failed_chunk_indexes.insert(0);
    checkpoint.chunk_metadata[0].fallback_text = Some("Fallback source block.".to_string());

    let lookup = build_translated_block_lookup(&checkpoint);
    let block = lookup.get(&1).expect("page 1").first().expect("block");

    assert_eq!(block.translated_text, "Fallback source block.");
    assert_eq!(block.source_text, "Source zero.");
    assert!(block.uses_source_fallback);
    assert!(!block.translation_skipped);
    assert!(!block.preserve_original_text_in_pdf_export);
    assert_eq!(block.font_size, 12.0);
    assert_eq!(
        block.detected_font_names.as_deref(),
        Some(&["TimesNewRomanPSMT".to_string()][..])
    );
    assert!(should_render_block_text(block));
    assert!(should_erase_block_background(block));
}

#[test]
fn native_pdf_export_blocks_preserved_formula_does_not_render_or_erase() {
    let mut checkpoint = checkpoint();
    checkpoint.source_chunks = vec!["Attention(Q, K, V) = softmax(QK^T)V".to_string()];
    checkpoint.translated_chunks =
        BTreeMap::from([(0, "Attention(Q, K, V) = softmax(QK^T)V".to_string())]);
    checkpoint.chunk_metadata = vec![PdfExportChunkMetadata {
        source_block_type: PdfExportSourceBlockType::Formula,
        translation_skipped: true,
        preserve_original_text_in_pdf_export: true,
        detected_font_names: Some(vec!["TimesNewRomanPSMT".to_string(), "CMMI10".to_string()]),
        ..metadata(0)
    }];

    let lookup = build_translated_block_lookup(&checkpoint);
    let block = lookup[&1].first().expect("block");

    assert!(block.translation_skipped);
    assert!(block.preserve_original_text_in_pdf_export);
    assert!(!should_render_block_text(block));
    assert!(!should_erase_block_background(block));
}

#[test]
fn native_pdf_export_blocks_vertical_rotation_skips_rendering_and_preserve_flag() {
    let mut checkpoint = checkpoint();
    checkpoint.chunk_metadata[0].preserve_original_text_in_pdf_export = true;
    checkpoint.chunk_metadata[0].text_style = Some(PdfExportBlockTextStyle {
        font_size: 9.0,
        line_spacing: 10.0,
        rotation_angle: -90.0,
    });

    let lookup = build_translated_block_lookup(&checkpoint);
    let block = lookup[&1].first().expect("block");

    assert!(block.translation_skipped);
    assert!(!block.preserve_original_text_in_pdf_export);
    assert_eq!(block.font_size, 9.0);
    assert!(!should_render_block_text(block));
    assert!(!should_erase_block_background(block));
}

#[test]
fn native_pdf_export_blocks_skip_missing_metadata_and_default_font_size() {
    let mut checkpoint = checkpoint();
    checkpoint.chunk_metadata.remove(1);
    checkpoint.chunk_metadata[0].text_style = None;

    let lookup = build_translated_block_lookup(&checkpoint);

    assert_eq!(lookup[&1].len(), 1);
    assert_eq!(lookup[&1][0].chunk_index, 0);
    assert_eq!(lookup[&1][0].font_size, 10.0);
}

#[test]
fn native_pdf_overlay_blocks_include_translated_bbox_and_skip_unsupported_blocks() {
    let mut checkpoint = PdfExportCheckpoint {
        source_chunks: vec![
            "Source translated.".to_string(),
            "Source missing bbox.".to_string(),
            "Source vertical.".to_string(),
            "Source formula.".to_string(),
            "Source skipped.".to_string(),
            "Source fallback.".to_string(),
            "Source unchanged.".to_string(),
        ],
        chunk_metadata: (0..7).map(metadata).collect(),
        translated_chunks: BTreeMap::from([
            (0, "译文覆盖块".to_string()),
            (1, "Missing bbox translation.".to_string()),
            (2, "Vertical translation.".to_string()),
            (3, "x = y + z".to_string()),
            (4, "Skipped translation.".to_string()),
            (6, "Source unchanged.".to_string()),
        ]),
        failed_chunk_indexes: BTreeSet::from([5]),
    };
    checkpoint.chunk_metadata[0].page_number = 2;
    checkpoint.chunk_metadata[0].source_block_id = "pdf-p2-body-b1".to_string();
    checkpoint.chunk_metadata[0].bounding_box = Some(PdfRect::new(40.0, 120.0, 180.0, 36.0));
    checkpoint.chunk_metadata[0].text_style = Some(PdfExportBlockTextStyle {
        font_size: 13.5,
        line_spacing: 16.0,
        rotation_angle: 0.0,
    });
    checkpoint.chunk_metadata[1].bounding_box = None;
    checkpoint.chunk_metadata[2].text_style = Some(PdfExportBlockTextStyle {
        font_size: 12.0,
        line_spacing: 14.0,
        rotation_angle: 90.0,
    });
    checkpoint.chunk_metadata[3] = PdfExportChunkMetadata {
        source_block_type: PdfExportSourceBlockType::Formula,
        translation_skipped: true,
        preserve_original_text_in_pdf_export: true,
        ..metadata(3)
    };
    checkpoint.chunk_metadata[4].translation_skipped = true;
    checkpoint.chunk_metadata[5].fallback_text = Some("Source fallback text.".to_string());

    let overlay_blocks = build_pdf_overlay_blocks(&checkpoint);

    assert_eq!(overlay_blocks.len(), 1);
    let overlay = &overlay_blocks[0];
    assert_eq!(overlay.page_index, 1);
    assert_eq!(overlay.page_number, 2);
    assert_eq!(overlay.source_block_id, "pdf-p2-body-b1");
    assert_eq!(overlay.text, "译文覆盖块");
    assert_eq!(overlay.rect.x, 40.0);
    assert_eq!(overlay.rect.y, 120.0);
    assert_eq!(overlay.rect.width, 180.0);
    assert_eq!(overlay.rect.height, 36.0);
    assert_eq!(overlay.font_size, 13.5);
    assert_eq!(
        overlay.detected_font_names.as_deref(),
        Some(&["TimesNewRomanPSMT".to_string()][..])
    );
    assert_eq!(checkpoint_to_overlay_blocks(&checkpoint), overlay_blocks);
}
