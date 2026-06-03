use easydict_app::pdf_formula_adapter::{
    block_formula_characters_for_pdf_block, build_formula_aware_pdf_block_text,
    build_pdf_block_formula_evidence, char_info_from_pdf_glyph, char_infos_for_pdf_block,
    glyphs_in_block, letter_geometries_for_pdf_block, PdfBlockBounds, PdfBlockTolerance, PdfGlyph,
    PdfGlyphBounds, PdfTextOrientation,
};

fn bounds(left: f64, bottom: f64, right: f64, top: f64) -> PdfGlyphBounds {
    PdfGlyphBounds::from_lbrt(left, bottom, right, top)
}

fn block() -> PdfBlockBounds {
    PdfBlockBounds::from_lrtb(100.0, 200.0, 720.0, 680.0)
}

fn glyph(value: &str, left: f64, bottom: f64, font_name: &str) -> PdfGlyph {
    PdfGlyph::new(
        value,
        bounds(left, bottom, left + 6.0, bottom + 12.0),
        12.0,
        font_name,
    )
}

#[test]
fn pdf_formula_adapter_filters_glyphs_with_block_tolerance() {
    let glyphs = vec![
        PdfGlyph::new("A", bounds(100.0, 680.0, 106.0, 692.0), 12.0, "Arial"),
        PdfGlyph::new("B", bounds(99.0, 679.0, 201.0, 721.0), 12.0, "Arial"),
        PdfGlyph::new("C", bounds(98.9, 680.0, 105.0, 692.0), 12.0, "Arial"),
        PdfGlyph::new("D", bounds(100.0, 678.6, 106.0, 681.0), 12.0, "Arial"),
    ];

    let character_glyphs = glyphs_in_block(&glyphs, block(), PdfBlockTolerance::character_level());
    assert_eq!(
        character_glyphs
            .iter()
            .map(|item| item.value.as_str())
            .collect::<Vec<_>>(),
        vec!["A", "B"]
    );

    let letters = letter_geometries_for_pdf_block(&glyphs, block());
    assert_eq!(
        letters
            .iter()
            .map(|item| item.value.as_str())
            .collect::<Vec<_>>(),
        vec!["A", "B", "D"]
    );
}

#[test]
fn pdf_formula_adapter_strips_subset_prefix_and_preserves_codes() {
    let source = glyph("α", 110.0, 690.0, "ABCDEF+CMMI10").with_codes(945, 42);

    let ch = char_info_from_pdf_glyph(&source);

    assert_eq!(ch.text, "α");
    assert_eq!(ch.font_name, "CMMI10");
    assert_eq!(ch.character_code, 945);
    assert_eq!(ch.cid, 42);

    let letters = letter_geometries_for_pdf_block(&[source], block());
    assert_eq!(letters[0].font_name, "CMMI10");
}

#[test]
fn pdf_formula_adapter_maps_rotated_glyphs_to_vertical_char_matrix() {
    let source = glyph("x", 110.0, 690.0, "CMMI10").with_orientation(PdfTextOrientation::Rotate90);

    let chars = char_infos_for_pdf_block(&[source.clone()], block());

    assert_eq!(chars.len(), 1);
    assert!(chars[0].text_matrix.is_vertical());
    assert!(letter_geometries_for_pdf_block(&[source], block()).is_empty());
}

#[test]
fn pdf_formula_adapter_builds_character_level_protection_from_math_fonts() {
    let glyphs = vec![
        glyph("w", 110.0, 690.0, "TimesNewRoman"),
        glyph("h", 116.0, 690.0, "TimesNewRoman"),
        glyph("e", 122.0, 690.0, "TimesNewRoman"),
        glyph("r", 128.0, 690.0, "TimesNewRoman"),
        glyph("e", 134.0, 690.0, "TimesNewRoman"),
        glyph(" ", 140.0, 690.0, "TimesNewRoman"),
        glyph("x", 146.0, 690.0, "ABCDEF+CMMI10"),
    ];

    let evidence = build_pdf_block_formula_evidence(&glyphs, block());
    let protection = evidence
        .character_level_protection
        .expect("math font should produce character-level protection");

    assert_eq!(protection.protected_text, "where {v0}");
    assert_eq!(protection.tokens.len(), 1);
    assert_eq!(protection.tokens[0].raw, "x");
    assert_eq!(protection.tokens[0].placeholder, "{v0}");
}

#[test]
fn pdf_formula_adapter_builds_letter_geometry_in_source_order() {
    let glyphs = vec![
        glyph("a", 110.0, 690.0, "TimesNewRoman").with_baseline_y(691.5),
        glyph("b", 116.5, 690.2, "TimesNewRoman").with_baseline_y(691.7),
    ];

    let letters = letter_geometries_for_pdf_block(&glyphs, block());

    assert_eq!(letters.len(), 2);
    assert_eq!(letters[0].value, "a");
    assert_eq!(letters[0].left, 110.0);
    assert_eq!(letters[0].right, 116.0);
    assert_eq!(letters[0].baseline_y, 691.5);
    assert_eq!(letters[1].value, "b");
}

#[test]
fn pdf_formula_adapter_marks_formula_characters_and_scripts() {
    let glyphs = vec![
        PdfGlyph::new("x", bounds(110.0, 690.0, 116.0, 702.0), 12.0, "CMMI10"),
        PdfGlyph::new("1", bounds(116.0, 686.0, 120.0, 694.0), 8.0, "CMMI10"),
        PdfGlyph::new("+", bounds(122.0, 690.0, 128.0, 702.0), 12.0, "CMSY10"),
        PdfGlyph::new("2", bounds(130.0, 696.0, 134.0, 704.0), 8.0, "CMMI10"),
    ];

    let formula_chars = block_formula_characters_for_pdf_block(&glyphs, block())
        .expect("math fonts should create formula character data");

    assert!(formula_chars.has_math_font_characters);
    assert!(formula_chars
        .characters
        .iter()
        .all(|item| item.is_math_font));
    assert!(formula_chars
        .characters
        .iter()
        .any(|item| item.value == "1" && item.is_subscript));
    assert!(formula_chars
        .characters
        .iter()
        .any(|item| item.value == "2" && item.is_superscript));
}

#[test]
fn pdf_formula_adapter_uses_half_word_gap_retry_before_fallback_text() {
    let mut glyphs = Vec::new();
    let mut x = 110.0;
    for word in ["Most", "competitive", "neural", "model"] {
        for (index, ch) in word.chars().enumerate() {
            let font_name = if word == "model" && index == 0 {
                "CMMI10"
            } else {
                "TimesNewRoman"
            };
            glyphs.push(PdfGlyph::new(
                ch.to_string(),
                bounds(x, 690.0, x + 6.0, 702.0),
                12.0,
                font_name,
            ));
            x += 6.4;
        }
        x += 2.5;
    }

    let output = build_formula_aware_pdf_block_text(
        &["Most competitive neural model"],
        &glyphs,
        PdfBlockBounds::from_lrtb(100.0, 320.0, 720.0, 680.0),
    );

    assert_eq!(output.block_text, "Most competitive neural model");
    assert!(output.fallback_text.is_none());
    assert!(output
        .evidence
        .formula_characters
        .as_ref()
        .is_some_and(|chars| chars.has_math_font_characters));
}

#[test]
fn pdf_formula_adapter_preserves_fallback_when_reconstruction_changes_text() {
    let glyphs = vec![
        glyph("x", 110.0, 690.0, "CMMI10"),
        glyph("1", 116.0, 686.0, "CMMI10"),
        glyph(" ", 122.0, 690.0, "TimesNewRoman"),
        glyph("+", 128.0, 690.0, "CMSY10"),
        glyph(" ", 134.0, 690.0, "TimesNewRoman"),
        glyph("y", 140.0, 690.0, "CMMI10"),
    ];

    let output = build_formula_aware_pdf_block_text(&["x1 + y"], &glyphs, block());

    assert_ne!(output.block_text, "x1 + y");
    assert_eq!(output.fallback_text.as_deref(), Some("x1 + y"));
}
