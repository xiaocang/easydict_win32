use easydict_app::content_preservation::{analyze_formula_preservation, SourceBlockType};
use easydict_app::pdf_export_blocks::PdfExportSourceBlockType;
use easydict_app::pdf_formula_adapter::{char_info_from_pdf_glyph, PdfTextOrientation};
use easydict_app::pdf_source_extraction::{
    block_context_for_pdf_source_block, build_pdf_source_layout_profile,
    calculate_pdf_source_reading_order_score, guess_pdf_source_block_type,
    infer_pdf_source_region_type, infer_region_info_from_source_block_id,
    pdf_export_chunk_metadata_for_source_block, pdf_glyph_from_extracted_text_char,
    pdf_source_page_from_extracted_page, PdfSourceExtractionOptions, PdfSourceLayoutRegion,
    PdfSourceLayoutRegionSource,
};
use easydict_pdf_render::{
    ExtractedPdfTextChar, ExtractedPdfTextPage, PdfTextBounds, PdfTextMatrix,
};

fn text_char(
    page_number: usize,
    char_index: usize,
    value: &str,
    left: f64,
    bottom: f64,
    font_name: &str,
) -> ExtractedPdfTextChar {
    ExtractedPdfTextChar {
        page_number,
        char_index,
        value: value.to_string(),
        unicode_value: value.chars().next().map(|ch| ch as u32).unwrap_or(0),
        font_name: font_name.to_string(),
        scaled_font_size: 12.0,
        unscaled_font_size: 12.0,
        bounds: PdfTextBounds {
            left,
            bottom,
            right: left + 6.0,
            top: bottom + 12.0,
        },
        origin_x: Some(left),
        origin_y: Some(bottom),
        matrix: Some(PdfTextMatrix {
            a: 1.0,
            b: 0.0,
            c: 0.0,
            d: 1.0,
            e: left,
            f: bottom,
        }),
        angle_degrees: Some(0.0),
    }
}

fn page(chars: Vec<ExtractedPdfTextChar>) -> ExtractedPdfTextPage {
    ExtractedPdfTextPage {
        page_number: 1,
        width: 1000.0,
        height: 1400.0,
        chars,
    }
}

fn line_chars(text: &str, y: f64, start_x: f64, font_name: &str) -> Vec<ExtractedPdfTextChar> {
    let mut x = start_x;
    text.chars()
        .enumerate()
        .map(|(index, ch)| {
            let width = if ch == ' ' { 4.0 } else { 6.0 };
            let extracted = text_char(1, index, &ch.to_string(), x, y, font_name);
            x += width;
            extracted
        })
        .collect()
}

#[test]
fn pdf_source_extraction_converts_pdfium_char_to_pdf_glyph() {
    let mut source = text_char(2, 7, "x", 110.0, 690.0, "ABCDEF+CMMI10");
    source.unscaled_font_size = 0.0;
    source.scaled_font_size = 11.5;
    source.angle_degrees = Some(90.0);
    source.matrix = Some(PdfTextMatrix {
        a: 0.0,
        b: 1.0,
        c: -1.0,
        d: 0.0,
        e: 110.0,
        f: 690.0,
    });

    let glyph = pdf_glyph_from_extracted_text_char(&source);
    let ch = char_info_from_pdf_glyph(&glyph);

    assert_eq!(glyph.value, "x");
    assert_eq!(glyph.point_size, 11.5);
    assert_eq!(glyph.orientation, PdfTextOrientation::Rotate90);
    assert_eq!(ch.font_name, "CMMI10");
    assert!(ch.text_matrix.is_vertical());
}

#[test]
fn pdf_source_extraction_groups_lines_into_conservative_blocks() {
    let mut chars = Vec::new();
    chars.extend(line_chars("Hello", 700.0, 100.0, "TimesNewRoman"));
    chars.extend(line_chars("world", 682.0, 100.0, "TimesNewRoman"));
    chars.extend(line_chars("Tail", 640.0, 100.0, "TimesNewRoman"));

    let source_page =
        pdf_source_page_from_extracted_page(&page(chars), PdfSourceExtractionOptions::default());

    assert_eq!(source_page.lines.len(), 3);
    assert_eq!(source_page.lines[0].text, "Hello");
    assert_eq!(source_page.lines[1].text, "world");
    assert_eq!(source_page.lines[2].text, "Tail");
    assert_eq!(source_page.blocks.len(), 2);
    assert_eq!(source_page.blocks[0].line_texts, vec!["Hello", "world"]);
    assert_eq!(source_page.blocks[0].text, "Hello\nworld");
    assert_eq!(source_page.blocks[1].text, "Tail");
}

#[test]
fn pdf_source_extraction_assigns_layout_region_ids_and_table_blocks() {
    let mut chars = Vec::new();
    chars.extend(line_chars("Header text", 1300.0, 100.0, "TimesNewRoman"));
    chars.extend(line_chars("1.0  2.0  3.0", 700.0, 100.0, "TimesNewRoman"));
    chars.extend(line_chars("Footer text", 90.0, 100.0, "TimesNewRoman"));

    let source_page =
        pdf_source_page_from_extracted_page(&page(chars), PdfSourceExtractionOptions::default());

    assert_eq!(source_page.width, 1000.0);
    assert_eq!(source_page.height, 1400.0);
    assert_eq!(source_page.blocks.len(), 3);
    assert_eq!(
        source_page.blocks[0].region_type,
        PdfSourceLayoutRegion::Header
    );
    assert_eq!(source_page.blocks[0].source_block_id, "p1-header-b1");
    assert_eq!(
        source_page.blocks[1].region_type,
        PdfSourceLayoutRegion::TableLike
    );
    assert_eq!(
        source_page.blocks[1].source_block_type,
        SourceBlockType::TableCell
    );
    assert_eq!(source_page.blocks[1].source_block_id, "p1-table-b2");
    assert_eq!(
        source_page.blocks[2].region_type,
        PdfSourceLayoutRegion::Footer
    );
    assert_eq!(source_page.blocks[2].source_block_id, "p1-footer-b3");
}

#[test]
fn pdf_source_extraction_splits_wide_same_baseline_columns() {
    let mut chars = Vec::new();
    chars.extend(line_chars("Left", 700.0, 100.0, "TimesNewRoman"));
    chars.extend(line_chars("Right", 700.0, 620.0, "TimesNewRoman"));

    let source_page =
        pdf_source_page_from_extracted_page(&page(chars), PdfSourceExtractionOptions::default());

    assert_eq!(source_page.lines.len(), 2);
    assert_eq!(source_page.blocks.len(), 2);
    assert_eq!(source_page.blocks[0].text, "Left");
    assert_eq!(source_page.blocks[0].source_block_id, "p1-left-b1");
    assert_eq!(source_page.blocks[1].text, "Right");
    assert_eq!(source_page.blocks[1].source_block_id, "p1-right-b2");
}

#[test]
fn pdf_source_extraction_orders_two_column_blocks_by_column() {
    let mut chars = Vec::new();
    for (index, y) in [1000.0, 984.0, 968.0, 952.0].into_iter().enumerate() {
        chars.extend(line_chars(
            &format!("L{}", index + 1),
            y,
            100.0,
            "TimesNewRoman",
        ));
    }
    for (index, y) in [994.0, 978.0, 962.0, 946.0].into_iter().enumerate() {
        chars.extend(line_chars(
            &format!("R{}", index + 1),
            y,
            620.0,
            "TimesNewRoman",
        ));
    }

    let source_page =
        pdf_source_page_from_extracted_page(&page(chars), PdfSourceExtractionOptions::default());

    assert_eq!(source_page.lines.len(), 8);
    assert_eq!(source_page.blocks.len(), 2);
    assert_eq!(
        source_page.blocks[0].line_texts,
        vec!["L1", "L2", "L3", "L4"]
    );
    assert_eq!(source_page.blocks[0].source_block_id, "p1-left-b1");
    assert_eq!(
        source_page.blocks[1].line_texts,
        vec!["R1", "R2", "R3", "R4"]
    );
    assert_eq!(source_page.blocks[1].source_block_id, "p1-right-b2");
}

#[test]
fn pdf_source_extraction_respects_two_column_boundaries() {
    let lines = vec![
        source_line(0, 50.0, 700.0, 400.0, 650.0, "Left column text"),
        source_line(1, 600.0, 700.0, 950.0, 650.0, "Right column text"),
        source_line(2, 440.0, 620.0, 560.0, 590.0, "Center text"),
        source_line(3, 60.0, 560.0, 410.0, 530.0, "More left"),
        source_line(4, 620.0, 560.0, 960.0, 530.0, "More right"),
    ];
    let profile = build_pdf_source_layout_profile(&lines, 1000.0, 1400.0);

    assert!(profile.is_two_column);
    assert_eq!(
        infer_pdf_source_region_type(profile, lines[0].bounds, &lines[0].text),
        PdfSourceLayoutRegion::LeftColumn
    );
    assert_eq!(
        infer_pdf_source_region_type(profile, lines[1].bounds, &lines[1].text),
        PdfSourceLayoutRegion::RightColumn
    );
    assert_eq!(
        infer_pdf_source_region_type(profile, lines[2].bounds, &lines[2].text),
        PdfSourceLayoutRegion::Body
    );
}

#[test]
fn pdf_source_extraction_infers_region_info_from_block_id() {
    let table = infer_region_info_from_source_block_id("p2-table-b3");
    assert_eq!(table.region_type, PdfSourceLayoutRegion::TableLike);
    assert!(table.confidence > 0.8);
    assert_eq!(table.source, PdfSourceLayoutRegionSource::Heuristic);

    let body = infer_region_info_from_source_block_id("p1-body-b1");
    assert_eq!(body.region_type, PdfSourceLayoutRegion::Body);
    assert_eq!(body.source, PdfSourceLayoutRegionSource::BlockIdFallback);

    let unknown = infer_region_info_from_source_block_id("p9-raw-b1");
    assert_eq!(unknown.region_type, PdfSourceLayoutRegion::Unknown);
    assert_eq!(unknown.source, PdfSourceLayoutRegionSource::Unknown);
}

#[test]
fn pdf_source_region_info_matches_dotnet_tag_semantics() {
    let cases = [
        (
            "P2-HEADER-B1",
            PdfSourceLayoutRegion::Header,
            PdfSourceLayoutRegionSource::Heuristic,
            0.92,
        ),
        (
            "p2-footer-b1",
            PdfSourceLayoutRegion::Footer,
            PdfSourceLayoutRegionSource::Heuristic,
            0.92,
        ),
        (
            "checkpoint-p2-left-b1",
            PdfSourceLayoutRegion::LeftColumn,
            PdfSourceLayoutRegionSource::Heuristic,
            0.80,
        ),
        (
            "p2-right-b1",
            PdfSourceLayoutRegion::RightColumn,
            PdfSourceLayoutRegionSource::Heuristic,
            0.80,
        ),
        (
            "p2-table-b1",
            PdfSourceLayoutRegion::TableLike,
            PdfSourceLayoutRegionSource::Heuristic,
            0.88,
        ),
        (
            "p2-figure-b1",
            PdfSourceLayoutRegion::Figure,
            PdfSourceLayoutRegionSource::OnnxModel,
            0.90,
        ),
        (
            "p2-formula-b1",
            PdfSourceLayoutRegion::Formula,
            PdfSourceLayoutRegionSource::OnnxModel,
            0.90,
        ),
        (
            "p2-caption-b1",
            PdfSourceLayoutRegion::Caption,
            PdfSourceLayoutRegionSource::OnnxModel,
            0.85,
        ),
        (
            "p2-title-b1",
            PdfSourceLayoutRegion::Title,
            PdfSourceLayoutRegionSource::OnnxModel,
            0.88,
        ),
        (
            "p2-body-b1",
            PdfSourceLayoutRegion::Body,
            PdfSourceLayoutRegionSource::BlockIdFallback,
            0.72,
        ),
    ];

    for (source_block_id, expected_region, expected_source, expected_confidence) in cases {
        let actual = infer_region_info_from_source_block_id(source_block_id);
        assert_eq!(actual.region_type, expected_region);
        assert_eq!(actual.source, expected_source);
        assert_eq!(actual.confidence, expected_confidence);
    }

    for source_block_id in ["p2-sidebar-b1", "p2-b1", "pdf-p2-block-1"] {
        let actual = infer_region_info_from_source_block_id(source_block_id);
        assert_eq!(actual.region_type, PdfSourceLayoutRegion::Unknown);
        assert_eq!(actual.source, PdfSourceLayoutRegionSource::Unknown);
        assert_eq!(actual.confidence, 0.35);
    }
}

#[test]
fn pdf_source_region_type_respects_csharp_geometry_priority() {
    let profile = build_pdf_source_layout_profile(&[], 1000.0, 1400.0);

    assert_eq!(
        infer_pdf_source_region_type(
            profile,
            source_line(0, 100.0, 1300.0, 400.0, 1260.0, "header").bounds,
            "header"
        ),
        PdfSourceLayoutRegion::Header
    );
    assert_eq!(
        infer_pdf_source_region_type(
            profile,
            source_line(1, 100.0, 120.0, 400.0, 80.0, "footer").bounds,
            "footer"
        ),
        PdfSourceLayoutRegion::Footer
    );
    assert_eq!(
        infer_pdf_source_region_type(
            profile,
            source_line(2, 100.0, 900.0, 910.0, 860.0, "wide table").bounds,
            "wide table"
        ),
        PdfSourceLayoutRegion::TableLike
    );
    assert_eq!(
        infer_pdf_source_region_type(
            profile,
            source_line(3, 100.0, 800.0, 810.0, 760.0, "1.0 2.0").bounds,
            "1.0 2.0"
        ),
        PdfSourceLayoutRegion::TableLike
    );
    assert_eq!(
        infer_pdf_source_region_type(
            profile,
            source_line(4, 300.0, 700.0, 700.0, 660.0, "body").bounds,
            "body"
        ),
        PdfSourceLayoutRegion::Body
    );
    assert_eq!(
        infer_pdf_source_region_type(
            profile,
            source_line(5, 100.0, 700.0, 450.0, 660.0, "left").bounds,
            "left"
        ),
        PdfSourceLayoutRegion::LeftColumn
    );
    assert_eq!(
        infer_pdf_source_region_type(
            profile,
            source_line(6, 550.0, 700.0, 900.0, 660.0, "right").bounds,
            "right"
        ),
        PdfSourceLayoutRegion::RightColumn
    );
}

#[test]
fn pdf_source_reading_order_score_matches_dotnet_clamping() {
    assert_eq!(calculate_pdf_source_reading_order_score(0, 0), 1.0);
    assert_eq!(calculate_pdf_source_reading_order_score(0, 1), 1.0);
    assert_eq!(calculate_pdf_source_reading_order_score(0, 200), 1.0);
    assert_eq!(calculate_pdf_source_reading_order_score(199, 200), 0.0);
    assert_eq!(calculate_pdf_source_reading_order_score(250, 200), 0.0);
    assert_eq!(calculate_pdf_source_reading_order_score(1, 3), 0.5);
}

#[test]
fn pdf_source_extraction_guesses_formula_and_prose_dominant_blocks() {
    assert_eq!(
        guess_pdf_source_block_type("x = 5"),
        SourceBlockType::Formula
    );
    assert_eq!(
        guess_pdf_source_block_type(r"\(x_i + y_i\)"),
        SourceBlockType::Formula
    );
    assert_eq!(
        guess_pdf_source_block_type("APPENDIX A"),
        SourceBlockType::Heading
    );

    let prose = concat!(
        "This paragraph explains the simple relation x = y while keeping enough ",
        "ordinary natural words around it that the inline equation is not the block."
    );
    assert_eq!(
        guess_pdf_source_block_type(prose),
        SourceBlockType::Paragraph
    );
}

#[test]
fn pdf_source_extraction_builds_block_context_with_formula_evidence() {
    let mut chars = line_chars("where ", 700.0, 100.0, "TimesNewRoman");
    chars.push(text_char(1, 6, "x", 136.0, 700.0, "ABCDEF+CMMI10"));

    let source_page =
        pdf_source_page_from_extracted_page(&page(chars), PdfSourceExtractionOptions::default());
    let block = &source_page.blocks[0];
    let context = block_context_for_pdf_source_block(block, 0);

    assert_eq!(
        block.detected_font_names,
        vec!["CMMI10".to_string(), "TimesNewRoman".to_string()]
    );
    assert_eq!(
        block.character_level_protected_text.as_deref(),
        Some("where {v0}")
    );
    assert_eq!(block.character_level_tokens.len(), 1);
    assert!(context
        .formula_characters
        .as_ref()
        .is_some_and(|chars| chars.has_math_font_characters));
    assert_eq!(
        context.character_level_protected_text.as_deref(),
        Some("where {v0}")
    );
    assert_eq!(
        context.character_level_tokens.as_ref().map(Vec::len),
        Some(1)
    );
}

#[test]
fn pdf_source_extraction_preserves_formula_context_and_exports_pdf_metadata() {
    let mut chars = Vec::new();
    chars.extend(line_chars(
        "INTRODUCTION",
        700.0,
        100.0,
        "TimesNewRoman-Bold",
    ));
    chars.extend(line_chars("x = 5", 640.0, 100.0, "ABCDEF+CMMI10"));

    let source_page =
        pdf_source_page_from_extracted_page(&page(chars), PdfSourceExtractionOptions::default());
    let block = &source_page.blocks[1];
    let context = block_context_for_pdf_source_block(block, 1);
    let plan = analyze_formula_preservation(&context);
    let metadata =
        pdf_export_chunk_metadata_for_source_block(block, 7, source_page.blocks.len(), 2, false);

    assert_eq!(block.source_block_type, SourceBlockType::Formula);
    assert_eq!(context.block_type, SourceBlockType::Formula);
    assert!(context.is_formula_like);
    assert_eq!(context.retry_attempt, 1);
    assert!(plan.skip_translation);
    assert_eq!(metadata.chunk_index, 7);
    assert_eq!(metadata.source_block_id, "p1-left-b2");
    assert_eq!(
        metadata.source_block_type,
        PdfExportSourceBlockType::Formula
    );
    assert_eq!(metadata.order_in_page, 1);
    assert_eq!(metadata.reading_order_score, 0.0);
    assert!(metadata.preserve_original_text_in_pdf_export);
    assert_eq!(metadata.retry_count, 2);
    assert_eq!(
        metadata.detected_font_names.as_deref(),
        Some(&["CMMI10".to_string()][..])
    );
    let style = metadata.text_style.expect("text style");
    assert_eq!(style.font_size, 12.0);
    assert_eq!(style.rotation_angle, 0.0);
    let bounds = metadata.bounding_box.expect("bounds");
    assert_eq!(bounds.x, 100.0);
    assert_eq!(bounds.y, 640.0);
}

#[test]
fn pdf_source_extraction_filters_empty_chars_without_losing_page() {
    let source_page = pdf_source_page_from_extracted_page(
        &page(vec![text_char(1, 0, "", 100.0, 700.0, "Arial")]),
        PdfSourceExtractionOptions::default(),
    );

    assert_eq!(source_page.page_number, 1);
    assert!(source_page.lines.is_empty());
    assert!(source_page.blocks.is_empty());
}

fn source_line(
    line_index: usize,
    left: f64,
    top: f64,
    right: f64,
    bottom: f64,
    text: &str,
) -> easydict_app::pdf_source_extraction::PdfSourceLine {
    easydict_app::pdf_source_extraction::PdfSourceLine {
        page_number: 1,
        line_index,
        text: text.to_string(),
        bounds: easydict_app::pdf_formula_adapter::PdfBlockBounds {
            left,
            right,
            top,
            bottom,
        },
        glyphs: Vec::new(),
    }
}
