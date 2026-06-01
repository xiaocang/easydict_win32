use crate::translation_language::TranslationLanguage;

const CORRECTED_OPEN_TAG: &str = "[CORRECTED]";
const CORRECTED_CLOSE_TAG: &str = "[/CORRECTED]";
const EXPLANATION_OPEN_TAG: &str = "[EXPLANATION]";
const EXPLANATION_CLOSE_TAG: &str = "[/EXPLANATION]";

pub const GRAMMAR_CORRECTION_SYSTEM_PROMPT: &str = r#"You are a grammar correction expert. Your task is to correct grammar, spelling, and punctuation errors in the text provided by the user.

Rules:
1. NEVER translate the text. The output must be in the exact same language as the input.
2. Keep the original meaning unchanged.
3. Only fix actual errors; do not rephrase, paraphrase, or "polish" correct text.
4. Output ONLY the corrected text with no additional commentary, labels, or formatting.
5. If the text has no errors, output it unchanged."#;

pub const GRAMMAR_CORRECTION_SYSTEM_PROMPT_WITH_EXPLANATION: &str = r#"You are a grammar correction expert. Your task is to correct grammar, spelling, and punctuation errors in the text provided by the user.

Rules:
1. NEVER translate the text. The output must be in the exact same language as the input.
2. Keep the original meaning unchanged.
3. Only fix actual errors; do not rephrase, paraphrase, or "polish" correct text.
4. First output the fully corrected text, then on a new line output "---", then briefly list the key corrections you made.
5. The "---" separator MUST be on its own line after the corrected text. NEVER put "---" before the corrected text.
6. If the text has no errors, output it unchanged followed by "---" and "No errors found.""#;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GrammarCorrectionResult {
    pub original_text: String,
    pub corrected_text: String,
    pub explanation: Option<String>,
    pub service_name: String,
    pub timing_ms: i64,
}

impl GrammarCorrectionResult {
    pub fn has_corrections(&self) -> bool {
        self.original_text.trim() != self.corrected_text.trim()
    }
}

pub fn parse_grammar_correction(
    raw_output: &str,
    original_text: &str,
    service_name: &str,
    timing_ms: i64,
) -> GrammarCorrectionResult {
    if raw_output.trim().is_empty() {
        return original_result(original_text, service_name, timing_ms);
    }

    let output = strip_misplaced_leading_separator(raw_output);
    if output.trim().is_empty() {
        return original_result(original_text, service_name, timing_ms);
    }

    let mut corrected_text = extract_section(&output, CORRECTED_OPEN_TAG, CORRECTED_CLOSE_TAG);
    let mut explanation = extract_section(&output, EXPLANATION_OPEN_TAG, EXPLANATION_CLOSE_TAG);

    if corrected_text.is_none() {
        if let Some((legacy_corrected, legacy_explanation)) =
            try_parse_legacy_separator_format(&output)
        {
            corrected_text = Some(legacy_corrected);
            explanation = legacy_explanation;
        }
    }

    GrammarCorrectionResult {
        original_text: original_text.to_string(),
        corrected_text: corrected_text.unwrap_or_else(|| output.trim().to_string()),
        explanation,
        service_name: service_name.to_string(),
        timing_ms,
    }
}

pub fn grammar_correction_system_prompt(include_explanations: bool) -> &'static str {
    if include_explanations {
        GRAMMAR_CORRECTION_SYSTEM_PROMPT_WITH_EXPLANATION
    } else {
        GRAMMAR_CORRECTION_SYSTEM_PROMPT
    }
}

pub fn build_grammar_correction_user_prompt(language: TranslationLanguage, text: &str) -> String {
    if language == TranslationLanguage::Auto {
        return format!("Correct the grammar in the following text:\n\n{text}");
    }

    let display_name = language.display_name();
    format!(
        "Correct the grammar in the following {display_name} text. The result MUST remain in {display_name}:\n\n{text}"
    )
}

pub fn build_grammar_correction_plain_text_prompt(
    language: TranslationLanguage,
    text: &str,
    include_explanations: bool,
) -> String {
    format!(
        "{}\n\n{}",
        grammar_correction_system_prompt(include_explanations),
        build_grammar_correction_user_prompt(language, text)
    )
}

fn original_result(
    original_text: &str,
    service_name: &str,
    timing_ms: i64,
) -> GrammarCorrectionResult {
    GrammarCorrectionResult {
        original_text: original_text.to_string(),
        corrected_text: original_text.to_string(),
        explanation: None,
        service_name: service_name.to_string(),
        timing_ms,
    }
}

fn extract_section(text: &str, open_tag: &str, close_tag: &str) -> Option<String> {
    let lowercase_text = text.to_ascii_lowercase();
    let lowercase_open_tag = open_tag.to_ascii_lowercase();
    let lowercase_close_tag = close_tag.to_ascii_lowercase();

    let start_index = lowercase_text.find(&lowercase_open_tag)? + open_tag.len();
    let end_index = lowercase_text[start_index..].find(&lowercase_close_tag)? + start_index;
    let section = text[start_index..end_index].trim();
    (!section.is_empty()).then(|| section.to_string())
}

fn strip_misplaced_leading_separator(text: &str) -> String {
    let trimmed = text.trim_start();
    if !trimmed.starts_with("---") {
        return text.to_string();
    }

    let after_separator = &trimmed[3..];
    if after_separator
        .chars()
        .next()
        .is_some_and(|character| !character.is_whitespace())
    {
        return text.to_string();
    }

    after_separator.trim_start().to_string()
}

fn try_parse_legacy_separator_format(text: &str) -> Option<(String, Option<String>)> {
    let normalized = text.replace("\r\n", "\n").replace('\r', "\n");
    let lines = normalized.split('\n').collect::<Vec<_>>();
    let separator_index = lines.iter().position(|line| line.trim() == "---")?;

    let corrected_text = lines[..separator_index].join("\n").trim().to_string();
    if corrected_text.is_empty() {
        return None;
    }

    let explanation = lines[(separator_index + 1)..].join("\n").trim().to_string();
    Some((
        corrected_text,
        (!explanation.is_empty()).then_some(explanation),
    ))
}
