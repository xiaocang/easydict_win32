use easydict_app::{
    analyze_formula_preservation, is_character_based_formula, is_font_based_formula,
    normalize_for_exact_span_comparison, protect_formula_block, resolve_formula_fallback,
    restore_formula_block, BlockContext, BlockFormulaCharacters, FormulaCharacterInfo,
    FormulaToken, FormulaTokenType, PreservationMode, ProtectedBlock, ProtectionPlan,
    RestoreStatus, SoftProtectedSpan, SoftProtectionWrapperKind, SoftValidationStatus,
    SourceBlockType, EQUATION_SOFT_CLOSE_TAG, EQUATION_SOFT_OPEN_TAG,
};

#[test]
fn formula_preservation_analyze_matches_legacy_block_level_heuristics() {
    let mut formula = BlockContext::paragraph("x = 5");
    formula.block_type = SourceBlockType::Formula;
    let plan = analyze_formula_preservation(&formula);
    assert!(plan.skip_translation);
    assert_eq!(plan.mode, PreservationMode::Opaque);

    let mut font_context = BlockContext::paragraph("formula content");
    font_context.detected_font_names = Some(vec![
        "ABCDEF+CMMI12".to_string(),
        "CMSY10".to_string(),
        "Arial".to_string(),
    ]);
    assert!(analyze_formula_preservation(&font_context).skip_translation);

    assert!(is_font_based_formula(
        Some(&["FancyEquation".to_string(), "Arial".to_string()]),
        Some("Equation")
    ));

    let mut unicode_context = BlockContext::paragraph("αβγ formula");
    assert!(is_character_based_formula(&unicode_context.text, None));
    unicode_context.text = "plain words".to_string();
    assert!(!analyze_formula_preservation(&unicode_context).skip_translation);

    let mut subscript_context = BlockContext::paragraph("a i j k");
    subscript_context.formula_characters = Some(BlockFormulaCharacters {
        has_math_font_characters: true,
        characters: vec![
            formula_char("a", true, false),
            formula_char("i", true, true),
            formula_char("j", true, false),
            formula_char("k", true, false),
        ],
    });
    assert!(analyze_formula_preservation(&subscript_context).skip_translation);

    assert!(
        analyze_formula_preservation(&BlockContext::paragraph(
            "base 6 512 2048 8 64 64 0.1 0.1 100K 4.92 25.8 65"
        ))
        .skip_translation
    );
    assert!(
        !analyze_formula_preservation(&BlockContext::paragraph(
            "Section 3.2 introduces the Transformer."
        ))
        .skip_translation
    );

    let mut display = BlockContext::paragraph("Attention(Q, K, V) = softmax(QK^T)V");
    display.formula_characters = Some(BlockFormulaCharacters {
        has_math_font_characters: true,
        characters: vec![
            formula_char("Q", false, false),
            formula_char("K", false, false),
        ],
    });
    let plan = analyze_formula_preservation(&display);
    assert!(plan.skip_translation);
    assert_eq!(plan.reason.as_deref(), Some("DisplayEquationHeuristic"));

    let normal = analyze_formula_preservation(&BlockContext::paragraph("This is normal text."));
    assert!(!normal.skip_translation);
    assert_eq!(normal.mode, PreservationMode::None);
}

#[test]
fn formula_preservation_protect_prefers_character_level_until_retry_or_exact_soft_candidates() {
    let mut context = BlockContext::paragraph("The value x equals 5");
    context.character_level_protected_text = Some("The value {v0} equals 5".to_string());
    context.character_level_tokens = Some(vec![token(FormulaTokenType::InlineMath, "x", "{v0}")]);

    let protected = protect_formula_block(&context, &ProtectionPlan::none());
    assert_eq!(protected.protected_text, "The value {v0} equals 5");
    assert_eq!(protected.tokens.len(), 1);
    assert!(protected.soft_spans.is_empty());
    assert_eq!(protected.plan.mode, PreservationMode::InlineProtected);

    let mut formula_only = BlockContext::paragraph("Attention(Q, K, V) = softmax(QK^T)V");
    formula_only.character_level_protected_text =
        Some("Attention({v0}) = softmax({v1}){v2}".to_string());
    formula_only.character_level_tokens = Some(vec![
        token(FormulaTokenType::InlineMath, "Q, K, V", "{v0}"),
        token(FormulaTokenType::InlineMath, "QK^T", "{v1}"),
        token(FormulaTokenType::InlineMath, "V", "{v2}"),
    ]);
    let protected = protect_formula_block(&formula_only, &ProtectionPlan::none());
    assert!(protected.plan.skip_translation);
    assert_eq!(protected.plan.mode, PreservationMode::Opaque);

    let mut equation_soft = BlockContext::paragraph("Attention score = softmax(QK^T)");
    equation_soft.character_level_protected_text =
        Some("Attention score = softmax({v0})".to_string());
    equation_soft.character_level_tokens =
        Some(vec![token(FormulaTokenType::InlineMath, "QK^T", "{v0}")]);
    let protected = protect_formula_block(&equation_soft, &ProtectionPlan::none());
    assert_eq!(
        protected.protected_text,
        format!(
            "{EQUATION_SOFT_OPEN_TAG}Attention score = softmax({{v0}}){EQUATION_SOFT_CLOSE_TAG}"
        )
    );
    assert_eq!(protected.soft_spans.len(), 1);
    assert_eq!(
        protected.soft_spans[0].wrapper_kind,
        SoftProtectionWrapperKind::EquationSoftTag
    );
    assert!(protected.soft_spans[0].requires_exact_preservation);

    let mut retry = BlockContext::paragraph("The h_{t-1} value.");
    retry.character_level_protected_text = Some("The {v0} value.".to_string());
    retry.character_level_tokens = Some(vec![token(
        FormulaTokenType::MathSubscript,
        "h_{t-1}",
        "{v0}",
    )]);
    retry.retry_attempt = 1;
    let protected = protect_formula_block(&retry, &ProtectionPlan::none());
    assert!(protected.tokens.is_empty());
    assert!(protected.protected_text.contains("$h_{t-1}$"));

    let mut tuple_context = BlockContext::paragraph(
        "The encoder maps (x1, ..., xn) to continuous representations z = (z1, ..., zn).",
    );
    tuple_context.character_level_protected_text =
        Some("The encoder maps {v0} to continuous representations {v1}.".to_string());
    tuple_context.character_level_tokens = Some(vec![
        token(FormulaTokenType::InlineMath, "(x1, ..., xn)", "{v0}"),
        token(
            FormulaTokenType::InlineEquation,
            "z = (z1, ..., zn)",
            "{v1}",
        ),
    ]);
    let protected = protect_formula_block(&tuple_context, &ProtectionPlan::none());
    assert!(protected.tokens.is_empty());
    assert_eq!(protected.soft_spans.len(), 2);
    assert!(protected
        .soft_spans
        .iter()
        .all(|span| span.requires_exact_preservation));
    assert!(protected.protected_text.contains("$(x1, ..., xn)$"));
    assert!(protected.protected_text.contains("$z = (z1, ..., zn)$"));
    assert!(!protected.protected_text.contains("{v0}"));
}

#[test]
fn formula_preservation_restore_validates_soft_spans_and_equation_tags() {
    let hard = ProtectedBlock {
        original_text: r"The \alpha letter".to_string(),
        protected_text: "The {v0} letter".to_string(),
        tokens: vec![token(FormulaTokenType::GreekLetter, r"\alpha", "{v0}")],
        soft_spans: Vec::new(),
        plan: ProtectionPlan {
            mode: PreservationMode::InlineProtected,
            skip_translation: false,
            reason: None,
        },
    };
    let outcome = restore_formula_block("The {v0} letter in translation", &hard);
    assert_eq!(
        resolve_formula_fallback(&outcome, &hard),
        r"The \alpha letter in translation"
    );
    assert_eq!(outcome.status, RestoreStatus::FullRestore);
    assert_eq!(outcome.missing_token_count, 0);

    let tuple = protected_soft_tuple();
    let outcome = restore_formula_block("The tuple $(x1, ..., xn)$ is a sequence.", &tuple);
    assert_eq!(outcome.text, tuple.original_text);
    assert_eq!(outcome.status, RestoreStatus::FullRestore);
    assert_eq!(
        outcome.soft_validation_status,
        SoftValidationStatus::Normalized
    );
    assert_eq!(outcome.synthetic_delimiter_strip_count, 1);

    let mutated = restore_formula_block("The tuple sequence1 is a sequence.", &tuple);
    assert_eq!(mutated.text, tuple.original_text);
    assert_eq!(mutated.status, RestoreStatus::FallbackToOriginal);
    assert_eq!(mutated.soft_validation_status, SoftValidationStatus::Failed);
    assert_eq!(mutated.soft_failure_count, 1);

    let equation = ProtectedBlock {
        original_text: "a = softmax(QK^T)".to_string(),
        protected_text: format!(
            "{EQUATION_SOFT_OPEN_TAG}a = softmax(QK^T){EQUATION_SOFT_CLOSE_TAG}"
        ),
        tokens: Vec::new(),
        soft_spans: vec![SoftProtectedSpan {
            raw_text: "a = softmax(QK^T)".to_string(),
            token_type: FormulaTokenType::InlineEquation,
            wrapped_text: format!(
                "{EQUATION_SOFT_OPEN_TAG}a = softmax(QK^T){EQUATION_SOFT_CLOSE_TAG}"
            ),
            synthetic_delimiters: true,
            requires_exact_preservation: true,
            wrapper_kind: SoftProtectionWrapperKind::EquationSoftTag,
        }],
        plan: ProtectionPlan {
            mode: PreservationMode::InlineProtected,
            skip_translation: false,
            reason: None,
        },
    };
    let outcome = restore_formula_block(
        &format!("{EQUATION_SOFT_OPEN_TAG}a = softmax(QK^T){EQUATION_SOFT_CLOSE_TAG}"),
        &equation,
    );
    assert_eq!(outcome.text, equation.original_text);
    assert_eq!(
        outcome.soft_validation_status,
        SoftValidationStatus::Normalized
    );

    let mutated = restore_formula_block(
        &format!("{EQUATION_SOFT_OPEN_TAG}a = 注意力KV{EQUATION_SOFT_CLOSE_TAG}"),
        &equation,
    );
    assert_eq!(mutated.text, equation.original_text);
    assert_eq!(mutated.status, RestoreStatus::FallbackToOriginal);
    assert_eq!(mutated.soft_validation_status, SoftValidationStatus::Failed);
}

#[test]
fn formula_preservation_exact_soft_comparison_accepts_latex_equivalent_tuples() {
    let tuple = ProtectedBlock {
        original_text: "The sequence (y1, ..., ym) of symbols.".to_string(),
        protected_text: "The sequence $(y1, ..., ym)$ of symbols.".to_string(),
        tokens: Vec::new(),
        soft_spans: vec![SoftProtectedSpan {
            raw_text: "(y1, ..., ym)".to_string(),
            token_type: FormulaTokenType::ImplicitTuple,
            wrapped_text: "$(y1, ..., ym)$".to_string(),
            synthetic_delimiters: true,
            requires_exact_preservation: true,
            wrapper_kind: SoftProtectionWrapperKind::DollarMath,
        }],
        plan: ProtectionPlan::none(),
    };

    for translated in [
        r"The sequence (y_1, \ldots, y_m) of symbols.",
        r"The sequence (y_1, \dots, y_m) of symbols.",
        r"The sequence (y_1, \cdots, y_m) of symbols.",
        "The sequence (y_1, …, y_m) of symbols.",
    ] {
        let outcome = restore_formula_block(translated, &tuple);
        assert_ne!(
            outcome.status,
            RestoreStatus::FallbackToOriginal,
            "{translated}"
        );
        assert_ne!(
            outcome.soft_validation_status,
            SoftValidationStatus::Failed,
            "{translated}"
        );
    }

    for (input, expected) in [
        ("", ""),
        ("(y1, ..., ym)", "(y1, ..., ym)"),
        (r"(y_1, \ldots, y_m)", "(y1, ..., ym)"),
        (r"(y_1, \dots, y_m)", "(y1, ..., ym)"),
        (r"(y_1, \cdots, y_m)", "(y1, ..., ym)"),
        ("(y_1, …, y_m)", "(y1, ..., ym)"),
        ("my_var = 5", "my_var = 5"),
        ("z_1, z_2, z_n", "z1, z2, zn"),
    ] {
        assert_eq!(normalize_for_exact_span_comparison(input), expected);
    }
}

fn protected_soft_tuple() -> ProtectedBlock {
    let original_text = "The tuple (x1, ..., xn) is a sequence.";
    ProtectedBlock {
        original_text: original_text.to_string(),
        protected_text: "The tuple $(x1, ..., xn)$ is a sequence.".to_string(),
        tokens: Vec::new(),
        soft_spans: vec![SoftProtectedSpan {
            raw_text: "(x1, ..., xn)".to_string(),
            token_type: FormulaTokenType::ImplicitTuple,
            wrapped_text: "$(x1, ..., xn)$".to_string(),
            synthetic_delimiters: true,
            requires_exact_preservation: true,
            wrapper_kind: SoftProtectionWrapperKind::DollarMath,
        }],
        plan: ProtectionPlan {
            mode: PreservationMode::InlineProtected,
            skip_translation: false,
            reason: None,
        },
    }
}

fn token(token_type: FormulaTokenType, raw: &str, placeholder: &str) -> FormulaToken {
    FormulaToken {
        token_type,
        raw: raw.to_string(),
        placeholder: placeholder.to_string(),
        simplified: raw.to_string(),
    }
}

fn formula_char(value: &str, is_subscript: bool, is_superscript: bool) -> FormulaCharacterInfo {
    FormulaCharacterInfo {
        value: value.to_string(),
        font_name: "CMMI10".to_string(),
        is_math_font: true,
        is_subscript,
        is_superscript,
    }
}
