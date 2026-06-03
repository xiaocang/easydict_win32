use easydict_app::{
    build_char_paragraphs, build_char_paragraphs_with_classifier, build_character_level_protection,
    get_bracket_delta, get_formula_confidence, is_formula_character, reconstruct_latex_from_chars,
    CharInfo, CharTextInfo, FormulaConfidence, TextMatrix,
};

fn make_char(text: &str, x: f64, y: f64) -> CharInfo {
    make_char_with_font_size(text, x, y, 12.0, "TimesNewRoman")
}

fn make_char_with_font_size(
    text: &str,
    x: f64,
    y: f64,
    point_size: f64,
    font_name: &str,
) -> CharInfo {
    let mut ch = CharInfo::new(text, x, y, x + 6.0, y + point_size, point_size, font_name);
    ch.character_code = text.chars().next().map(|ch| ch as u32).unwrap_or(0);
    ch.cid = ch.character_code;
    ch
}

fn make_char_with_matrix(
    text: &str,
    x: f64,
    y: f64,
    font_name: &str,
    matrix: TextMatrix,
) -> CharInfo {
    let mut ch = make_char_with_font_size(text, x, y, 12.0, font_name);
    ch.text_matrix = matrix;
    ch
}

#[test]
fn character_paragraph_empty_input_returns_empty_result() {
    let result = build_char_paragraphs(&[]);

    assert!(result.paragraphs.is_empty());
    assert!(result.all_formula_groups.is_empty());
    assert_eq!(result.total_characters, 0);
    assert_eq!(result.formula_characters, 0);
}

#[test]
fn character_paragraph_plain_word_merges_into_single_paragraph() {
    let chars = ["H", "e", "l", "l", "o"]
        .into_iter()
        .enumerate()
        .map(|(index, text)| make_char(text, 100.0 + index as f64 * 6.0, 700.0))
        .collect::<Vec<_>>();

    let result = build_char_paragraphs(&chars);

    assert_eq!(result.paragraphs.len(), 1);
    assert_eq!(result.paragraphs[0].text, "Hello");
    assert_eq!(result.paragraphs[0].characters.len(), 5);
    assert_eq!(result.total_characters, 5);
    assert_eq!(result.formula_characters, 0);
}

#[test]
fn character_paragraph_math_font_subscript_and_unicode_create_formula_groups() {
    let math_font = [
        make_char("f", 100.0, 700.0),
        make_char("(", 106.0, 700.0),
        make_char_with_font_size("x", 112.0, 700.0, 12.0, "CMMI10"),
        make_char(")", 118.0, 700.0),
    ];
    let result = build_char_paragraphs(&math_font);
    assert!(result.formula_characters >= 1);
    assert!(result.paragraphs[0].text.contains("{v"));

    let subscript = [
        make_char_with_font_size("x", 100.0, 700.0, 12.0, "TimesNewRoman"),
        make_char_with_font_size("2", 106.0, 696.0, 8.0, "TimesNewRoman"),
    ];
    let result = build_char_paragraphs(&subscript);
    assert_eq!(result.formula_characters, 1);
    assert!(result.paragraphs[0].text.contains("{v"));

    let unicode = [make_char("x", 100.0, 700.0), make_char("≠", 106.0, 700.0)];
    let result = build_char_paragraphs(&unicode);
    assert_eq!(result.formula_characters, 1);

    let greek = [make_char("α", 100.0, 700.0)];
    let result = build_char_paragraphs(&greek);
    assert_eq!(result.formula_characters, 1);
    assert_eq!(result.all_formula_groups.len(), 1);
}

#[test]
fn character_paragraph_excluded_region_and_layout_change_match_legacy_behavior() {
    let excluded = [
        make_char("x", 100.0, 700.0),
        make_char("=", 106.0, 700.0),
        make_char("1", 112.0, 700.0),
    ];
    let result = build_char_paragraphs_with_classifier(&excluded, |_, _| 0);
    assert_eq!(result.formula_characters, 3);

    let split = [
        make_char("A", 100.0, 700.0),
        make_char("B", 106.0, 700.0),
        make_char("C", 100.0, 500.0),
        make_char("D", 106.0, 500.0),
    ];
    let result =
        build_char_paragraphs_with_classifier(&split, |_, y| if y > 600.0 { 1 } else { 2 });
    assert_eq!(result.paragraphs.len(), 2);
    assert_eq!(result.paragraphs[0].text, "AB");
    assert_eq!(result.paragraphs[1].text, "CD");
}

#[test]
fn character_paragraph_formula_group_bounds_and_protected_text_are_recorded() {
    let chars = [
        make_char_with_font_size("x", 100.0, 700.0, 12.0, "CMMI10"),
        make_char_with_font_size("+", 108.0, 700.0, 12.0, "CMSY10"),
        make_char_with_font_size("y", 116.0, 700.0, 12.0, "CMMI10"),
    ];

    let result = build_char_paragraphs(&chars);

    assert_eq!(result.all_formula_groups.len(), 1);
    let group = &result.all_formula_groups[0];
    assert_eq!(group.characters.len(), 3);
    assert_eq!(group.x0(), 100.0);
    assert_eq!(group.x1(), 122.0);
    assert_eq!(
        result.paragraphs[0].protected_text,
        result.paragraphs[0].text
    );
}

#[test]
fn character_paragraph_formula_classification_false_positive_guards() {
    for font_name in [
        "Lato-Regular",
        "Helvetica-Regular",
        "NotoSans-Regular",
        "TimesNewRoman",
        "ArialMT",
    ] {
        let ch = make_char_with_font_size("A", 100.0, 700.0, 12.0, font_name);
        assert!(!is_formula_character(&ch, 12.0, 1), "font={font_name}");
    }

    let vertical = TextMatrix::from_values(0.0, 1.0, -1.0, 0.0, 100.0, 700.0);
    let normal_vertical = make_char_with_matrix("A", 100.0, 700.0, "Arial", vertical);
    assert!(!is_formula_character(&normal_vertical, 12.0, 1));

    let math_vertical = make_char_with_matrix("x", 100.0, 700.0, "CMMI10", vertical);
    assert!(is_formula_character(&math_vertical, 12.0, 1));

    assert!(!is_formula_character(
        &make_char("\u{2002}", 100.0, 700.0),
        12.0,
        1
    ));
    assert!(is_formula_character(
        &make_char("\u{200B}", 100.0, 700.0),
        12.0,
        1
    ));
}

#[test]
fn character_paragraph_confidence_and_bracket_delta_match_legacy_rules() {
    assert_eq!(
        get_formula_confidence(
            &make_char_with_font_size("x", 100.0, 700.0, 12.0, "CMMI10"),
            12.0,
            1,
        ),
        FormulaConfidence::High
    );
    assert_eq!(
        get_formula_confidence(
            &make_char_with_font_size("x", 100.0, 700.0, 12.0, "ABCDEF+CMSY10"),
            12.0,
            1
        ),
        FormulaConfidence::High
    );
    assert_eq!(
        get_formula_confidence(
            &make_char_with_font_size("2", 100.0, 700.0, 7.0, "Arial"),
            12.0,
            1
        ),
        FormulaConfidence::Low
    );
    assert!(!is_formula_character(
        &make_char_with_font_size("2", 100.0, 700.0, 7.0, "Arial"),
        0.0,
        1
    ));
    assert_eq!(
        get_formula_confidence(&make_char("\u{FFFD}", 100.0, 700.0), 0.0, 1),
        FormulaConfidence::Low
    );
    assert_eq!(
        get_formula_confidence(&make_char("A", 100.0, 700.0), 12.0, 1),
        FormulaConfidence::None
    );

    for (input, expected) in [
        ("(", 1),
        ("[", 1),
        ("{", 1),
        (")", -1),
        ("]", -1),
        ("}", -1),
        ("A", 0),
        ("", 0),
        ("()", 0),
        ("((", 2),
        ("))", -2),
    ] {
        assert_eq!(get_bracket_delta(input), expected, "input={input:?}");
    }
}

#[test]
fn character_paragraph_formula_mode_does_not_backfill_plain_open_bracket() {
    let chars = [
        make_char("f", 100.0, 700.0),
        make_char("(", 106.0, 700.0),
        make_char_with_font_size("x", 112.0, 700.0, 12.0, "CMMI10"),
        make_char(")", 118.0, 700.0),
    ];

    let result = build_char_paragraphs(&chars);

    assert_eq!(result.formula_characters, 1);
    assert_eq!(result.paragraphs[0].text, "f({v0})");
}

#[test]
fn character_paragraph_math_open_bracket_keeps_inner_plain_chars_until_depth_zero() {
    let chars = [
        make_char_with_font_size("(", 100.0, 700.0, 12.0, "CMSY10"),
        make_char("x", 106.0, 700.0),
        make_char("+", 112.0, 700.0),
        make_char(")", 118.0, 700.0),
    ];

    let result = build_char_paragraphs(&chars);

    assert_eq!(result.formula_characters, 3);
    assert_eq!(result.all_formula_groups.len(), 1);
    assert_eq!(result.paragraphs[0].text, "{v0})");
}

#[test]
fn character_level_protection_builds_hard_placeholders_for_high_confidence_groups() {
    let chars = [
        make_char("w", 100.0, 700.0),
        make_char("h", 106.0, 700.0),
        make_char("e", 112.0, 700.0),
        make_char("r", 118.0, 700.0),
        make_char("e", 124.0, 700.0),
        make_char(" ", 130.0, 700.0),
        make_char_with_font_size("x", 136.0, 700.0, 12.0, "CMMI10"),
        make_char(" ", 142.0, 700.0),
        make_char("i", 148.0, 700.0),
        make_char("s", 154.0, 700.0),
    ];

    let protection = build_character_level_protection(&chars).expect("formula group");

    assert_eq!(protection.protected_text, "where {v0} is");
    assert_eq!(protection.tokens.len(), 1);
    assert_eq!(protection.tokens[0].raw, "x");
    assert_eq!(protection.tokens[0].placeholder, "{v0}");
}

#[test]
fn character_level_protection_soft_wraps_low_confidence_groups() {
    let chars = [
        make_char("a", 100.0, 700.0),
        make_char("\u{FFFD}", 106.0, 700.0),
        make_char("b", 112.0, 700.0),
    ];

    let protection = build_character_level_protection(&chars).expect("formula group");

    assert_eq!(protection.protected_text, "a$\u{FFFD}$b");
    assert!(protection.tokens.is_empty());
}

#[test]
fn character_level_protection_soft_wraps_size_only_subscript_group() {
    let chars = [
        make_char_with_font_size("x", 100.0, 700.0, 12.0, "TimesNewRoman"),
        make_char_with_font_size("2", 106.0, 696.0, 8.0, "TimesNewRoman"),
    ];

    let protection = build_character_level_protection(&chars).expect("subscript group");

    assert_eq!(protection.protected_text, "x$2$");
    assert!(protection.tokens.is_empty());
}

#[test]
fn character_level_protection_mixed_high_and_low_group_demotes_entire_group() {
    let chars = [
        make_char_with_font_size("x", 100.0, 700.0, 12.0, "CMMI10"),
        make_char("\u{FFFD}", 106.0, 700.0),
    ];

    let protection = build_character_level_protection(&chars).expect("mixed group");

    assert_eq!(protection.protected_text, "$x\u{FFFD}$");
    assert!(protection.tokens.is_empty());
}

#[test]
fn formula_latex_reconstructor_matches_legacy_cases() {
    assert_eq!(
        reconstruct_latex_from_chars(&[
            CharTextInfo::new("x", 12.0, 700.0, false),
            CharTextInfo::new("+", 12.0, 700.0, false),
            CharTextInfo::new("y", 12.0, 700.0, false),
        ]),
        "x+y"
    );
    assert!(reconstruct_latex_from_chars(&[
        CharTextInfo::new("x", 12.0, 700.0, false),
        CharTextInfo::new("2", 8.0, 696.0, false),
    ])
    .contains("_{2}"));
    assert!(reconstruct_latex_from_chars(&[
        CharTextInfo::new("x", 12.0, 700.0, false),
        CharTextInfo::new("2", 8.0, 706.0, false),
    ])
    .contains("^{2}"));
    assert!(
        reconstruct_latex_from_chars(&[CharTextInfo::new("α", 12.0, 700.0, true)])
            .contains(r"\alpha")
    );
    assert!(reconstruct_latex_from_chars(&[
        CharTextInfo::new("x", 12.0, 700.0, false),
        CharTextInfo::new("∈", 12.0, 700.0, false),
        CharTextInfo::new("ℝ", 12.0, 700.0, false),
    ])
    .contains(r"\in"));
    assert_eq!(reconstruct_latex_from_chars(&[]), "");
    assert!(reconstruct_latex_from_chars(&[
        CharTextInfo::new("h", 12.0, 700.0, false),
        CharTextInfo::new("_", 12.0, 700.0, false),
        CharTextInfo::new("t", 12.0, 700.0, false),
    ])
    .contains(r"\_"));
    assert!(reconstruct_latex_from_chars(&[
        CharTextInfo::new("x", 12.0, 700.0, false),
        CharTextInfo::new("2", 8.0, 696.0, false),
        CharTextInfo::new("+", 12.0, 700.0, false),
        CharTextInfo::new("y", 12.0, 700.0, false),
    ])
    .contains("}+"));
}
