use easydict_app::{
    classify_formula_token, detect_formula_matches, extend_formula_trailing_parens,
    formula_requires_exact_soft_preservation,
    formula_text_contains_exact_soft_preservation_candidate, formula_token_type_is_high_confidence,
    protect_formula_spans, protect_formula_spans_two_tier, restore_formula_spans,
    restore_formula_spans_with_diagnostics, FormulaRestoreStatus, FormulaToken, FormulaTokenType,
};

#[test]
fn formula_detector_detects_known_patterns() {
    for (text, expected) in [
        ("$$x^2 + y^2 = r^2$$", "$$x^2 + y^2 = r^2$$"),
        (r"$\alpha + \beta$", r"$\alpha + \beta$"),
        (
            r"\begin{equation}E=mc^2\end{equation}",
            r"\begin{equation}E=mc^2\end{equation}",
        ),
        (r"\alpha", r"\alpha"),
        (r"\infty", r"\infty"),
        ("x_{i+1}", "x_{i+1}"),
        ("x^{2n}", "x^{2n}"),
        ("x_i", "x_i"),
        ("x^2", "x^2"),
        ("W_Q", "W_Q"),
        ("h_{t-1}", "h_{t-1}"),
        ("1_c_i", "1_c_i"),
        ("(x1, ..., xn)", "(x1, ..., xn)"),
        ("z = (z1, ..., zn)", "z = (z1, ..., zn)"),
    ] {
        let matches = detect_formula_matches(text);
        assert!(!matches.is_empty(), "{text} should contain a formula");
        assert_eq!(matches[0].raw, expected);
    }
}

#[test]
fn formula_detector_does_not_match_plain_text_or_natural_language_tuples() {
    assert!(detect_formula_matches("The quick brown fox").is_empty());

    for text in [
        "(apple, banana, cherry)",
        "(version1, version2)",
        "(mp4, mp3)",
    ] {
        let matches = detect_formula_matches(text);
        assert!(
            matches.first().is_none_or(|item| item.raw != text),
            "{text} should not be captured as a full formula tuple"
        );
    }
}

#[test]
fn formula_classifier_and_confidence_match_legacy_categories() {
    for (raw, expected) in [
        ("$$x^2$$", FormulaTokenType::DisplayMath),
        (r"\[x^2\]", FormulaTokenType::DisplayMath),
        ("$x^2$", FormulaTokenType::InlineMath),
        (r"\(x^2\)", FormulaTokenType::InlineMath),
        (
            r"\begin{equation}x\end{equation}",
            FormulaTokenType::LaTeXEnv,
        ),
        (r"\begin{bmatrix}a&b\end{bmatrix}", FormulaTokenType::Matrix),
        (r"\frac{a}{b}", FormulaTokenType::Fraction),
        (r"\sqrt{x}", FormulaTokenType::SquareRoot),
        (r"\sum_{i=1}", FormulaTokenType::SumProduct),
        (r"\prod_{i=1}", FormulaTokenType::SumProduct),
        (r"\int_0^1", FormulaTokenType::Integral),
        (r"\alpha", FormulaTokenType::GreekLetter),
        (r"\infty", FormulaTokenType::MathOperator),
        ("x^2", FormulaTokenType::MathSuperscript),
        ("x_i", FormulaTokenType::MathSubscript),
        ("hidden_state", FormulaTokenType::SequenceToken),
        ("x = 5", FormulaTokenType::InlineEquation),
        ("(x1, ..., xn)", FormulaTokenType::ImplicitTuple),
    ] {
        assert_eq!(classify_formula_token(raw), expected, "{raw}");
    }

    for token_type in [
        FormulaTokenType::InlineMath,
        FormulaTokenType::DisplayMath,
        FormulaTokenType::LaTeXEnv,
        FormulaTokenType::Matrix,
        FormulaTokenType::Fraction,
        FormulaTokenType::SquareRoot,
        FormulaTokenType::SumProduct,
        FormulaTokenType::Integral,
        FormulaTokenType::GreekLetter,
        FormulaTokenType::MathOperator,
        FormulaTokenType::MathFormatting,
        FormulaTokenType::MathSuperscript,
        FormulaTokenType::MathSubscript,
    ] {
        assert!(formula_token_type_is_high_confidence(token_type));
    }

    for token_type in [
        FormulaTokenType::InlineEquation,
        FormulaTokenType::SequenceToken,
        FormulaTokenType::ImplicitTuple,
        FormulaTokenType::UnitFragment,
    ] {
        assert!(!formula_token_type_is_high_confidence(token_type));
    }
}

#[test]
fn formula_exact_soft_preservation_identifies_tuple_forms() {
    assert!(formula_requires_exact_soft_preservation(
        "(x1, ..., xn)",
        FormulaTokenType::ImplicitTuple
    ));
    assert!(formula_requires_exact_soft_preservation(
        "z = (z1, ..., zn)",
        FormulaTokenType::InlineEquation
    ));
    assert!(!formula_requires_exact_soft_preservation(
        "speed = 5",
        FormulaTokenType::InlineEquation
    ));
    assert!(formula_text_contains_exact_soft_preservation_candidate(
        "The tuple (x1, ..., xn) appears."
    ));
}

#[test]
fn formula_protector_replaces_hard_matches_with_sequential_placeholders() {
    let result = protect_formula_spans(r"First $a$ then \alpha and h_{t-1}.");

    assert_eq!(result.hard_tokens.len(), 3);
    assert!(result.protected_text.contains("{v0}"));
    assert!(result.protected_text.contains("{v1}"));
    assert!(result.protected_text.contains("{v2}"));
    assert_eq!(
        result.hard_tokens[1].token_type,
        FormulaTokenType::GreekLetter
    );
    assert_eq!(result.hard_tokens[1].simplified, "α");
}

#[test]
fn formula_protector_groups_trailing_formula_parentheses() {
    let result = protect_formula_spans("The function $f$(x, y) is defined.");

    assert_eq!(result.hard_tokens.len(), 1);
    assert_eq!(result.protected_text, "The function {v0} is defined.");
    assert_eq!(result.hard_tokens[0].raw, "$f$(x, y)");

    let mut raw_tokens = vec!["$E=mc^2$".to_string()];
    let not_grouped =
        extend_formula_trailing_parens("{v0}(which Einstein discovered)", &mut raw_tokens);
    assert!(not_grouped.contains("Einstein"));
    assert_eq!(raw_tokens[0], "$E=mc^2$");
}

#[test]
fn formula_protector_two_tier_soft_wraps_low_confidence_and_demoted_matches() {
    let tuple = protect_formula_spans_two_tier("The tuple (x1, ..., xn) is a sequence.", 0);
    assert!(tuple.hard_tokens.is_empty());
    assert_eq!(tuple.soft_spans.len(), 1);
    assert!(tuple.protected_text.contains("$(x1, ..., xn)$"));
    assert_eq!(tuple.soft_spans[0].raw_text, "(x1, ..., xn)");
    assert!(tuple.soft_spans[0].requires_exact_preservation);

    let equation = protect_formula_spans_two_tier("The speed = 5 is fast.", 0);
    assert!(equation.hard_tokens.is_empty());
    assert!(equation.protected_text.contains("$speed = 5$"));
    assert!(!equation.soft_spans[0].requires_exact_preservation);

    let demoted = protect_formula_spans_two_tier("Look at h_{t-1} here.", 1);
    assert!(demoted.hard_tokens.is_empty());
    assert!(demoted.protected_text.contains("$h_{t-1}$"));

    let greek = protect_formula_spans_two_tier(r"The \alpha is here.", 1);
    assert_eq!(greek.hard_tokens.len(), 1);
    assert!(greek.protected_text.contains("{v0}"));
}

#[test]
fn formula_protector_backward_compatible_mode_treats_low_confidence_as_hard() {
    let result = protect_formula_spans("The speed = 5 is fast.");

    assert_eq!(result.hard_tokens.len(), 1);
    assert!(result.protected_text.contains("{v0}"));
    assert!(!result.protected_text.contains("$speed"));
}

#[test]
fn formula_restorer_replaces_raw_or_simplified_tokens() {
    let tokens = vec![token(r"\alpha", "α")];

    assert_eq!(
        restore_formula_spans("The {v0} letter.", &tokens, "original", false),
        r"The \alpha letter."
    );
    assert_eq!(
        restore_formula_spans("The {v0} letter.", &tokens, "original", true),
        "The α letter."
    );
}

#[test]
fn formula_restorer_handles_partial_and_fallback_thresholds() {
    let tokens = vec![token("x_1", "x-1"), token("x_n", "x-n")];
    assert_eq!(
        restore_formula_spans("符号表示 {v0} 的序列", &tokens, "ORIGINAL", false),
        "符号表示 x_1 的序列"
    );
    assert_eq!(
        restore_formula_spans("完全没有占位符", &tokens, "ORIGINAL", false),
        "ORIGINAL"
    );

    let four = vec![
        token(r"\alpha", "α"),
        token(r"\beta", "β"),
        token(r"\gamma", "γ"),
        token(r"\delta", "δ"),
    ];
    assert_eq!(
        restore_formula_spans("只有 {v0} 剩下", &four, "ORIGINAL", false),
        "ORIGINAL"
    );
}

#[test]
fn formula_restorer_reports_diagnostics_and_rejects_corruption() {
    let tokens = vec![
        token(r"\alpha", "α"),
        token(r"\beta", "β"),
        token(r"\gamma", "γ"),
        token(r"\delta", "δ"),
    ];

    let partial =
        restore_formula_spans_with_diagnostics("{v0} {v1} {v2} here", &tokens, "ORIGINAL", false);
    assert_eq!(partial.status, FormulaRestoreStatus::PartialRestore);
    assert_eq!(partial.dropped_count, 1);
    assert_eq!(partial.missing_indices, vec![3]);

    let fallback =
        restore_formula_spans_with_diagnostics("only {v0} remaining", &tokens, "ORIGINAL", false);
    assert_eq!(fallback.status, FormulaRestoreStatus::FallbackToOriginal);
    assert_eq!(fallback.missing_indices, vec![1, 2, 3]);
    assert_eq!(fallback.text, "ORIGINAL");

    let bad_index = restore_formula_spans("{v0} and {v99}", &tokens[..2], "FALLBACK", false);
    assert_eq!(bad_index, "FALLBACK");

    let unbalanced = restore_formula_spans(
        "{v0}",
        &[token("{unbalanced", "{unbalanced")],
        "FALLBACK",
        false,
    );
    assert_eq!(unbalanced, "FALLBACK");
}

fn token(raw: &str, simplified: &str) -> FormulaToken {
    FormulaToken {
        token_type: FormulaTokenType::InlineMath,
        raw: raw.to_string(),
        placeholder: "{v0}".to_string(),
        simplified: simplified.to_string(),
    }
}
