use std::collections::HashMap;
use std::path::PathBuf;

use easydict_app::font_metrics::{
    glyph_advance_em, is_script_signal, load_font_metrics, parse_cmap_from_bytes,
    parse_font_metrics_from_bytes, FontMetrics, GlyphAdvanceMeasurer, CJK_PRIMARY_ASCII_ADVANCE_EM,
    DEFAULT_NON_CJK_ADVANCE_EM, SPACE_ADVANCE_EM,
};
use easydict_app::TextMeasurer;

fn fixture_font(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../..")
        .join("lib/PdfPig/src/UglyToad.PdfPig.Tests/Fonts/TrueType")
        .join(name)
}

fn synthetic_metrics(
    advance_width: u16,
    units_per_em: u16,
    chars: impl IntoIterator<Item = char>,
) -> FontMetrics {
    let mut glyph_map = HashMap::new();
    let mut advance_widths = HashMap::new();
    for ch in chars {
        let glyph_id = ch as u16;
        glyph_map.insert(ch, glyph_id);
        advance_widths.insert(glyph_id, advance_width);
    }

    FontMetrics::new(glyph_map, advance_widths, units_per_em)
}

fn ascii_metrics(advance_width: u16, units_per_em: u16) -> FontMetrics {
    synthetic_metrics(advance_width, units_per_em, ' '..='~')
}

#[test]
fn native_font_metrics_parse_real_truetype_glyph_map_and_advances() {
    let path = fixture_font("Roboto-Regular.ttf");
    let bytes = std::fs::read(&path).expect("read Roboto fixture");

    let metrics = parse_font_metrics_from_bytes(&bytes).expect("parse Roboto metrics");

    assert!(metrics.units_per_em > 0);
    assert!(metrics.glyph_map.len() > 100);
    assert!(metrics.advance_widths.len() > 100);

    let glyph_a = metrics.glyph_id('A').expect("Roboto maps A");
    let glyph_i = metrics.glyph_id('i').expect("Roboto maps i");
    let glyph_space = metrics.glyph_id(' ').expect("Roboto maps space");

    assert_ne!(glyph_a, 0);
    assert_ne!(glyph_i, 0);
    assert_ne!(glyph_space, 0);
    assert!(metrics.advance_width(glyph_a).unwrap() > metrics.advance_width(glyph_i).unwrap());
    assert!(metrics.advance_em(glyph_space, SPACE_ADVANCE_EM) > 0.0);

    let glyph_map = parse_cmap_from_bytes(&bytes).expect("parse Roboto cmap");
    assert_eq!(glyph_map.get(&'A').copied(), Some(glyph_a));
}

#[test]
fn native_font_metrics_loads_from_path_and_rejects_invalid_bytes() {
    let path = fixture_font("Roboto-Regular.ttf");
    let metrics = load_font_metrics(&path).expect("load Roboto metrics");
    assert!(metrics.glyph_id('R').is_some());

    let error = parse_font_metrics_from_bytes(b"not a font").expect_err("invalid font fails");
    assert!(error.to_string().contains("Failed to parse font metrics"));
}

#[test]
fn native_glyph_advance_measurer_uses_primary_font_metrics_for_latin_and_space() {
    let metrics = ascii_metrics(500, 1000);
    let measurer = GlyphAdvanceMeasurer::new(10.0).with_primary_metrics(metrics);

    assert_eq!(measurer.measure_grapheme("^"), 0.0);
    assert_eq!(measurer.measure_grapheme("_"), 0.0);
    assert!((measurer.measure_grapheme(" ") - 5.0).abs() <= 0.001);
    assert!((measurer.measure_grapheme("A") - 5.0).abs() <= 0.001);
    assert!((measurer.measure_segment("A^B") - 10.0).abs() <= 0.001);
}

#[test]
fn native_glyph_advance_measurer_uses_fallbacks_without_metrics() {
    let measurer = GlyphAdvanceMeasurer::new(10.0);

    assert!((measurer.measure_grapheme(" ") - 10.0 * SPACE_ADVANCE_EM).abs() <= 0.001);
    assert!((measurer.measure_grapheme("A") - 10.0 * DEFAULT_NON_CJK_ADVANCE_EM).abs() <= 0.001);
    assert_eq!(measurer.measure_grapheme("\u{4E2D}"), 10.0);
    assert_eq!(measurer.measure_grapheme(""), 0.0);
}

#[test]
fn native_glyph_advance_measurer_prefers_latin_face_for_ascii_under_cjk_primary_font() {
    let primary = ascii_metrics(1000, 1000);
    let latin = ascii_metrics(400, 1000);
    let measurer = GlyphAdvanceMeasurer::new(10.0)
        .with_primary_metrics(primary)
        .with_latin_metrics(latin)
        .with_primary_font_is_cjk(true);

    assert!((measurer.measure_grapheme("A") - 4.0).abs() <= 0.001);
    assert!((measurer.measure_grapheme(" ") - 4.0).abs() <= 0.001);
    assert!((measurer.measure_segment("A B") - 12.0).abs() <= 0.001);
}

#[test]
fn native_glyph_advance_measurer_uses_fixed_ascii_and_space_under_cjk_primary_without_latin_face() {
    let primary = ascii_metrics(700, 1000);
    let measurer = GlyphAdvanceMeasurer::new(10.0)
        .with_primary_metrics(primary)
        .with_primary_font_is_cjk(true);

    assert!((measurer.measure_grapheme("A") - 10.0 * CJK_PRIMARY_ASCII_ADVANCE_EM).abs() <= 0.001);
    assert!((measurer.measure_grapheme(" ") - 10.0 * SPACE_ADVANCE_EM).abs() <= 0.001);
    assert!(
        (measurer.measure_segment("A B")
            - 10.0
                * (CJK_PRIMARY_ASCII_ADVANCE_EM + SPACE_ADVANCE_EM + CJK_PRIMARY_ASCII_ADVANCE_EM))
            .abs()
            <= 0.001
    );
}

#[test]
fn native_glyph_advance_measurer_uses_noto_fallback_for_non_cjk_missing_primary_glyphs() {
    let primary = synthetic_metrics(500, 1000, ['A', ' ']);
    let noto = synthetic_metrics(700, 1000, ['B']);
    let measurer = GlyphAdvanceMeasurer::new(10.0)
        .with_primary_metrics(primary)
        .with_noto_metrics(noto);

    assert!((measurer.measure_grapheme("B") - 7.0).abs() <= 0.001);
    assert!((measurer.measure_grapheme("C") - 10.0 * DEFAULT_NON_CJK_ADVANCE_EM).abs() <= 0.001);
}

#[test]
fn native_glyph_advance_helpers_match_polyglot_document_export_constants() {
    let mut advances = HashMap::new();
    advances.insert(42, 250);

    assert!(is_script_signal('^'));
    assert!(is_script_signal('_'));
    assert!(!is_script_signal('A'));
    assert_eq!(glyph_advance_em(42, Some(&advances), 1000, 0.6), 0.25);
    assert_eq!(glyph_advance_em(43, Some(&advances), 1000, 0.6), 0.6);
    assert_eq!(glyph_advance_em(42, Some(&advances), 0, 0.6), 0.6);
}
