use easydict_app::{
    is_reconstruction_quality_acceptable, looks_like_formula_continuation_text,
    previous_line_likely_expects_formula_tail, reconstruct_formula_aware_text,
    should_use_letter_based_block_text, BlockFormulaCharacters, LetterGeometry,
};

fn build_line_letters(
    baseline_y: f64,
    tokens: &[(&str, &[usize], &[usize])],
) -> Vec<LetterGeometry> {
    let mut letters = Vec::new();
    let mut x = 100.0;

    for (token, subscript_indexes, superscript_indexes) in tokens {
        for (index, ch) in token.chars().enumerate() {
            let is_subscript = subscript_indexes.contains(&index);
            let is_superscript = superscript_indexes.contains(&index);
            let point_size = if is_subscript || is_superscript {
                8.0
            } else {
                12.0
            };
            let bottom = if is_subscript {
                baseline_y - 4.0
            } else if is_superscript {
                baseline_y + 4.0
            } else {
                baseline_y
            };
            let top = bottom + point_size;
            let letter_baseline_y = if is_subscript {
                baseline_y - 4.0
            } else if is_superscript {
                baseline_y + 4.0
            } else {
                baseline_y
            };
            let width = if ch.is_alphanumeric() {
                6.0
            } else if ch == '.' {
                2.5
            } else {
                3.5
            };

            letters.push(LetterGeometry::new(
                ch.to_string(),
                x,
                x + width,
                bottom,
                top,
                letter_baseline_y,
                point_size,
                "TimesNewRoman",
            ));

            x += width + 0.4;
        }

        x += 4.5;
    }

    letters
}

#[test]
fn formula_reconstructor_empty_letters_returns_empty() {
    assert_eq!(reconstruct_formula_aware_text(&[], 1.0), "");
}

#[test]
fn formula_reconstructor_preserves_inline_tuple_sequences_without_script_markers() {
    let mut letters = Vec::new();
    letters.extend(build_line_letters(
        700.0,
        &[
            ("Here", &[], &[]),
            (",", &[], &[]),
            ("the", &[], &[]),
            ("encoder", &[], &[]),
            ("maps", &[], &[]),
            ("an", &[], &[]),
            ("input", &[], &[]),
            ("sequence", &[], &[]),
            ("of", &[], &[]),
            ("symbol", &[], &[]),
            ("representations", &[], &[]),
            ("(", &[], &[]),
            ("x1", &[1], &[]),
            (",", &[], &[]),
            ("...", &[], &[]),
            (",", &[], &[]),
            ("xn", &[1], &[]),
            (")", &[], &[]),
            ("to", &[], &[]),
            ("a", &[], &[]),
            ("sequence", &[], &[]),
        ],
    ));
    letters.extend(build_line_letters(
        682.0,
        &[
            ("of", &[], &[]),
            ("continuous", &[], &[]),
            ("representations", &[], &[]),
            ("z", &[], &[]),
            ("=", &[], &[]),
            ("(", &[], &[]),
            ("z1", &[1], &[]),
            (",", &[], &[]),
            ("...", &[], &[]),
            (",", &[], &[]),
            ("zn", &[1], &[]),
            (")", &[], &[]),
            (".", &[], &[]),
        ],
    ));

    let text = reconstruct_formula_aware_text(&letters, 1.0);

    assert!(text.contains("(x1, ..., xn)"), "{text}");
    assert!(text.contains("z = (z1, ..., zn)"), "{text}");
    assert!(!text.contains("sequence_1"), "{text}");
}

#[test]
fn formula_reconstructor_emits_subscript_marker_for_non_simple_math_token() {
    let letters = build_line_letters(700.0, &[("word1", &[4], &[])]);

    let text = reconstruct_formula_aware_text(&letters, 1.0);

    assert_eq!(text, "word_1");
}

#[test]
fn formula_reconstructor_emits_superscript_marker_for_raised_small_token() {
    let letters = build_line_letters(700.0, &[("var2", &[], &[3])]);

    let text = reconstruct_formula_aware_text(&letters, 1.0);

    assert_eq!(text, "var^2");
}

#[test]
fn formula_reconstructor_ignores_non_math_script_run() {
    let token = format!("note{}", '\u{2020}');
    let letters = build_line_letters(700.0, &[(&token, &[], &[4])]);

    let text = reconstruct_formula_aware_text(&letters, 1.0);

    assert_eq!(text, token);
    assert!(!text.contains('^'));
}

#[test]
fn formula_reconstructor_lower_word_gap_scale_produces_more_spaces() {
    let mut letters = Vec::new();
    let mut x = 100.0;
    for word in ["Most", "competitive", "neural"] {
        for ch in word.chars() {
            let width = 6.0;
            letters.push(LetterGeometry::new(
                ch.to_string(),
                x,
                x + width,
                690.0,
                702.0,
                690.0,
                12.0,
                "TimesNewRoman",
            ));
            x += width + 0.4;
        }
        x += 2.5;
    }

    let default_result = reconstruct_formula_aware_text(&letters, 1.0);
    let scaled_result = reconstruct_formula_aware_text(&letters, 0.5);

    assert!(
        scaled_result.matches(' ').count() >= default_result.matches(' ').count(),
        "default={default_result:?} scaled={scaled_result:?}"
    );
}

#[test]
fn formula_reconstructor_merges_previous_unbalanced_paren_tail() {
    let mut letters = build_line_letters(700.0, &[("z=(", &[], &[])]);
    letters.extend(build_line_letters(
        690.0,
        &[("x1", &[1], &[]), (")", &[], &[])],
    ));

    let text = reconstruct_formula_aware_text(&letters, 1.0);

    assert_eq!(text, "z=(x1)");
}

#[test]
fn formula_reconstructor_keeps_distant_continuation_on_new_line() {
    let mut letters = build_line_letters(700.0, &[("z=(", &[], &[])]);
    letters.extend(build_line_letters(
        640.0,
        &[("x1", &[1], &[]), (")", &[], &[])],
    ));

    let text = reconstruct_formula_aware_text(&letters, 1.0);

    assert_eq!(text, "z=(\nx1)");
}

#[test]
fn formula_reconstructor_should_use_letter_text_for_math_font_or_char_protection() {
    let formula_chars = BlockFormulaCharacters {
        characters: Vec::new(),
        has_math_font_characters: true,
    };

    assert!(should_use_letter_based_block_text(
        &["plain text"],
        Some(&formula_chars),
        None,
    ));
    assert!(should_use_letter_based_block_text(
        &["plain text"],
        None,
        Some("x {v0}"),
    ));
    assert!(should_use_letter_based_block_text(
        &["x_1 + y^2"],
        None,
        None,
    ));
}

#[test]
fn formula_reconstructor_should_not_use_letter_text_without_evidence() {
    assert!(!should_use_letter_based_block_text(
        &["Plain prose without any hints."],
        None,
        None,
    ));
}

#[test]
fn formula_reconstructor_continuation_heuristics_match_typical_cases() {
    assert!(looks_like_formula_continuation_text(", ..., xn)"));
    assert!(looks_like_formula_continuation_text(", x_n)"));
    assert!(!looks_like_formula_continuation_text(
        "This is regular prose text."
    ));
    assert!(!looks_like_formula_continuation_text(""));

    assert!(previous_line_likely_expects_formula_tail(
        "Here, the encoder maps an input sequence of symbol representations (x"
    ));
    assert!(previous_line_likely_expects_formula_tail("z = (z"));
    assert!(!previous_line_likely_expects_formula_tail(
        "Regular sentence."
    ));
    assert!(!previous_line_likely_expects_formula_tail(""));
}

#[test]
fn formula_reconstructor_quality_gate_matches_parity_cases() {
    let cases = [
        (
            "Most competitive neural models",
            "Mostcompetitiveneural models",
            false,
        ),
        (
            "Most competitive neural models",
            "Most competitive neural models",
            true,
        ),
        (
            "Most competitive neural models",
            "Most competitive models",
            false,
        ),
        ("a b c d e f g", "a b c d e g", true),
        ("x1, ..., xn", "x1,...,xn", true),
        ("a b c d e f g", "a b c d e f g", true),
        ("", "anything", true),
        ("two words", "twowords", true),
        (
            "Most competitive neural sequence transduction models have an encoder-decoder structure",
            "Mostcompetitiveneural sequencetransductionmodels have anencoder-decoder structure",
            false,
        ),
    ];

    for (fallback, reconstructed, expected) in cases {
        assert_eq!(
            is_reconstruction_quality_acceptable(reconstructed, fallback),
            expected,
            "fallback={fallback:?} reconstructed={reconstructed:?}"
        );
    }
}

#[test]
fn formula_reconstructor_quality_rejects_descender_character_loss() {
    let fallback = "Here, the encoder maps an input sequence of symbol representations (x1, ..., xn) to a sequence of continuous representations z = (z1, ..., zn).";
    let reconstructed = "Here, the encoder ma s an in ut se uence of s mbol re resentations x1... x to a se uence of continuous re resentations z = z1... zn";

    assert!(!is_reconstruction_quality_acceptable(
        reconstructed,
        fallback,
    ));
}

#[test]
fn formula_reconstructor_quality_allows_tuple_anchor_restoration() {
    let fallback = "Here, the encoder ma ps an in put sequence of symbol representations x1 ... xn to a sequence of continuous representations z = z1 ... zn";
    let reconstructed = "Here, the encoder maps an input sequence of symbol representations (x1, ..., xn) to a sequence of continuous representations z = (z1, ..., zn)";

    assert!(is_reconstruction_quality_acceptable(
        reconstructed,
        fallback,
    ));
}
