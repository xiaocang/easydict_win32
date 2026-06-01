use easydict_app::{
    build_grammar_correction_plain_text_prompt, build_grammar_correction_user_prompt,
    grammar_correction_system_prompt, parse_grammar_correction, TranslationLanguage,
};

const SERVICE_NAME: &str = "TestService";

#[test]
fn grammar_parser_with_valid_markers_extracts_corrected_text_and_explanation() {
    let raw_output = r#"
[CORRECTED]
He went to the store yesterday.
[/CORRECTED]

[EXPLANATION]
Changed "go" to "went" (past tense required for past action).
[/EXPLANATION]
"#;

    let result = parse_grammar_correction(
        raw_output,
        "He go to the store yesterday.",
        SERVICE_NAME,
        100,
    );

    assert_eq!(result.corrected_text, "He went to the store yesterday.");
    assert!(result
        .explanation
        .as_deref()
        .unwrap()
        .contains("Changed \"go\" to \"went\""));
    assert_eq!(result.original_text, "He go to the store yesterday.");
    assert_eq!(result.service_name, SERVICE_NAME);
    assert_eq!(result.timing_ms, 100);
    assert!(result.has_corrections());
}

#[test]
fn grammar_parser_with_no_corrections_reports_no_corrections() {
    let original = "The quick brown fox jumps over the lazy dog.";
    let raw_output = format!(
        r#"
[CORRECTED]
{original}
[/CORRECTED]

[EXPLANATION]
No grammar issues found.
[/EXPLANATION]
"#
    );

    let result = parse_grammar_correction(&raw_output, original, SERVICE_NAME, 50);

    assert_eq!(result.corrected_text, original);
    assert!(!result.has_corrections());
    assert!(result
        .explanation
        .as_deref()
        .unwrap()
        .contains("No grammar issues found"));
}

#[test]
fn grammar_parser_without_markers_falls_back_to_entire_output() {
    let result = parse_grammar_correction(
        "He went to the store yesterday.",
        "He go to the store yesterday.",
        SERVICE_NAME,
        75,
    );

    assert_eq!(result.corrected_text, "He went to the store yesterday.");
    assert_eq!(result.explanation, None);
    assert!(result.has_corrections());
}

#[test]
fn grammar_parser_with_legacy_separator_extracts_corrected_text_and_explanation() {
    let raw_output = r#"
He went to the store yesterday.
---
Changed "go" to "went" to match the past-tense time marker.
"#;

    let result = parse_grammar_correction(
        raw_output,
        "He go to the store yesterday.",
        SERVICE_NAME,
        75,
    );

    assert_eq!(result.corrected_text, "He went to the store yesterday.");
    assert!(result
        .explanation
        .as_deref()
        .unwrap()
        .contains("Changed \"go\" to \"went\""));
    assert!(result.has_corrections());
}

#[test]
fn grammar_parser_with_legacy_separator_no_errors_reports_no_corrections() {
    let original = "The quick brown fox jumps over the lazy dog.";
    let raw_output = format!(
        r#"
{original}
---
No errors found.
"#
    );

    let result = parse_grammar_correction(&raw_output, original, SERVICE_NAME, 50);

    assert_eq!(result.corrected_text, original);
    assert_eq!(result.explanation.as_deref(), Some("No errors found."));
    assert!(!result.has_corrections());
}

#[test]
fn grammar_parser_with_misplaced_leading_separator_same_line_strips_separator() {
    let result = parse_grammar_correction(
        "--- I went swimming today at the gym's swimming pool.",
        "I went to swimming today to gym's swimming pool",
        SERVICE_NAME,
        75,
    );

    assert_eq!(
        result.corrected_text,
        "I went swimming today at the gym's swimming pool."
    );
    assert_eq!(result.explanation, None);
    assert!(result.has_corrections());
}

#[test]
fn grammar_parser_with_misplaced_leading_separator_own_line_strips_separator() {
    let raw_output = r#"
---
I went swimming today at the gym's swimming pool.
"#;

    let result = parse_grammar_correction(
        raw_output,
        "I went to swimming today to gym's swimming pool",
        SERVICE_NAME,
        75,
    );

    assert_eq!(
        result.corrected_text,
        "I went swimming today at the gym's swimming pool."
    );
    assert_eq!(result.explanation, None);
    assert!(result.has_corrections());
}

#[test]
fn grammar_parser_with_empty_output_returns_original_text() {
    let result = parse_grammar_correction("", "Some text.", SERVICE_NAME, 10);

    assert_eq!(result.corrected_text, "Some text.");
    assert!(!result.has_corrections());
    assert_eq!(result.explanation, None);
}

#[test]
fn grammar_parser_with_whitespace_output_returns_original_text() {
    let result = parse_grammar_correction("   \n  ", "Some text.", SERVICE_NAME, 10);

    assert_eq!(result.corrected_text, "Some text.");
    assert!(!result.has_corrections());
}

#[test]
fn grammar_parser_with_only_corrected_marker_extracts_corrected_text() {
    let raw_output = r#"
[CORRECTED]
She has been working here since 2020.
[/CORRECTED]
"#;

    let result = parse_grammar_correction(
        raw_output,
        "She have been working here since 2020.",
        SERVICE_NAME,
        60,
    );

    assert_eq!(
        result.corrected_text,
        "She has been working here since 2020."
    );
    assert_eq!(result.explanation, None);
    assert!(result.has_corrections());
}

#[test]
fn grammar_parser_case_insensitive_tags_work() {
    let raw_output = r#"
[corrected]
Fixed text.
[/corrected]

[explanation]
Some fix.
[/explanation]
"#;

    let result = parse_grammar_correction(raw_output, "Broken text.", SERVICE_NAME, 30);

    assert_eq!(result.corrected_text, "Fixed text.");
    assert_eq!(result.explanation.as_deref(), Some("Some fix."));
}

#[test]
fn grammar_parser_with_multiline_correction_preserves_newlines() {
    let raw_output = r#"
[CORRECTED]
First line corrected.
Second line corrected.
Third line corrected.
[/CORRECTED]

[EXPLANATION]
Line 1: Fixed subject-verb agreement.
Line 2: Fixed spelling.
[/EXPLANATION]
"#;

    let result = parse_grammar_correction(
        raw_output,
        "First line broken.\nSecond line broken.",
        SERVICE_NAME,
        120,
    );

    assert!(result.corrected_text.contains("First line corrected."));
    assert!(result.corrected_text.contains("Third line corrected."));
    assert!(result.explanation.as_deref().unwrap().contains("Line 1:"));
    assert!(result.explanation.as_deref().unwrap().contains("Line 2:"));
}

#[test]
fn grammar_parser_with_malformed_open_tag_only_falls_back_to_entire_output() {
    let raw_output = "[CORRECTED]\nSome text but no closing tag";

    let result = parse_grammar_correction(raw_output, "Original.", SERVICE_NAME, 20);

    assert_eq!(result.corrected_text, raw_output.trim());
}

#[test]
fn grammar_prompt_without_explanations_matches_shared_rules() {
    let prompt = grammar_correction_system_prompt(false);

    assert!(prompt.contains("You are a grammar correction expert."));
    assert!(prompt.contains("Output ONLY the corrected text"));
    assert!(prompt.contains("If the text has no errors, output it unchanged."));
    assert!(!prompt.contains("No errors found."));
}

#[test]
fn grammar_prompt_with_explanations_matches_shared_separator_rules() {
    let prompt = grammar_correction_system_prompt(true);

    assert!(prompt.contains("First output the fully corrected text"));
    assert!(prompt.contains("The \"---\" separator MUST be on its own line"));
    assert!(prompt.contains("NEVER put \"---\" before the corrected text"));
    assert!(prompt.contains("No errors found."));
}

#[test]
fn grammar_user_prompt_auto_language_omits_language_name() {
    let prompt = build_grammar_correction_user_prompt(TranslationLanguage::Auto, "He go home.");

    assert_eq!(
        prompt,
        "Correct the grammar in the following text:\n\nHe go home."
    );
}

#[test]
fn grammar_user_prompt_specific_language_uses_display_name_twice() {
    let prompt =
        build_grammar_correction_user_prompt(TranslationLanguage::SimplifiedChinese, "我去学校");

    assert!(prompt.contains("following Chinese (Simplified) text"));
    assert!(prompt.contains("MUST remain in Chinese (Simplified)"));
    assert!(prompt.ends_with("\n\n我去学校"));
}

#[test]
fn grammar_plain_text_prompt_combines_system_and_user_prompt() {
    let prompt = build_grammar_correction_plain_text_prompt(
        TranslationLanguage::English,
        "He go home.",
        true,
    );

    assert!(prompt.starts_with("You are a grammar correction expert."));
    assert!(prompt.contains("No errors found."));
    assert!(prompt.contains(
        "Correct the grammar in the following English text. The result MUST remain in English:"
    ));
    assert!(prompt.ends_with("\n\nHe go home."));
}
