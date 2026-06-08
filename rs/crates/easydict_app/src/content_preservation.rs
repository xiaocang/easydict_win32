use crate::formula_protection::{
    formula_text_contains_exact_soft_preservation_candidate, protect_formula_spans_two_tier,
    restore_formula_spans_with_diagnostics, FormulaProtectionResult, FormulaRestoreStatus,
    FormulaToken, FormulaTokenType, SoftProtectedSpan, SoftProtectionWrapperKind,
};
use regex::RegexBuilder;
use std::collections::BTreeMap;
use std::sync::OnceLock;

pub const EQUATION_SOFT_OPEN_TAG: &str = "[[EQ_SOFT]]";
pub const EQUATION_SOFT_CLOSE_TAG: &str = "[[/EQ_SOFT]]";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SourceBlockType {
    Unknown,
    Paragraph,
    Heading,
    Caption,
    TableCell,
    Formula,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PreservationMode {
    None,
    InlineProtected,
    Opaque,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProtectionPlan {
    pub mode: PreservationMode,
    pub skip_translation: bool,
    pub reason: Option<String>,
}

impl ProtectionPlan {
    pub fn none() -> Self {
        Self {
            mode: PreservationMode::None,
            skip_translation: false,
            reason: None,
        }
    }

    pub fn opaque(reason: impl Into<String>) -> Self {
        Self {
            mode: PreservationMode::Opaque,
            skip_translation: true,
            reason: Some(reason.into()),
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct FormulaCharacterInfo {
    pub value: String,
    pub font_name: String,
    pub is_math_font: bool,
    pub is_subscript: bool,
    pub is_superscript: bool,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct BlockFormulaCharacters {
    pub characters: Vec<FormulaCharacterInfo>,
    pub has_math_font_characters: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct BlockContext {
    pub text: String,
    pub block_type: SourceBlockType,
    pub is_formula_like: bool,
    pub detected_font_names: Option<Vec<String>>,
    pub formula_characters: Option<BlockFormulaCharacters>,
    pub formula_font_pattern: Option<String>,
    pub formula_char_pattern: Option<String>,
    pub character_level_protected_text: Option<String>,
    pub character_level_tokens: Option<Vec<FormulaToken>>,
    pub retry_attempt: usize,
}

impl BlockContext {
    pub fn paragraph(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            block_type: SourceBlockType::Paragraph,
            is_formula_like: false,
            detected_font_names: None,
            formula_characters: None,
            formula_font_pattern: None,
            formula_char_pattern: None,
            character_level_protected_text: None,
            character_level_tokens: None,
            retry_attempt: 0,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProtectedBlock {
    pub original_text: String,
    pub protected_text: String,
    pub tokens: Vec<FormulaToken>,
    pub soft_spans: Vec<SoftProtectedSpan>,
    pub plan: ProtectionPlan,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RestoreStatus {
    FullRestore,
    PartialRestore,
    FallbackToOriginal,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SoftValidationStatus {
    None,
    Passed,
    Normalized,
    Failed,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RestoreOutcome {
    pub text: String,
    pub status: RestoreStatus,
    pub missing_token_count: usize,
    pub soft_validation_status: SoftValidationStatus,
    pub soft_failure_count: usize,
    pub synthetic_delimiter_strip_count: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum FormulaOnlyClassification {
    No,
    AllPlaceholders,
    BothSidesOfEquals,
    ResidueOnly,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct DisplayEquationDiagnostics {
    candidate: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct EquationSoftDiagnostics {
    candidate: bool,
    has_equals: bool,
}

const MATH_FONT_PATTERN: &str = r"CM[^R]|CMSY|CMMI|CMEX|MS\.M|MSAM|MSBM|XY|MT\w*Math|Symbol|Euclid|Mathematica|MathematicalPi|STIX|\bBL\b|\bRM\b|\bEU\b|\bLA\b|\bRS\b|LINE|LCIRCLE|TeX-|rsfs|txsy|wasy|stmary|\w+Sym\w*|\b\w{1,5}Math\w*";

const SUSPICIOUS_EQUATION_CHARS: &[char] = &[
    '(', ')', '[', ']', '{', '}', '/', '*', '^', ',', '+', '-', '\u{221A}',
];

const COMMON_SHORT_ENGLISH_WORDS: &[&str] = &[
    "and", "the", "for", "use", "with", "from", "into", "this", "that", "then", "only", "each",
    "are", "was", "were", "our", "its",
];

const MATH_FUNCTION_NAMES: &[&str] = &[
    "attention",
    "softmax",
    "multihead",
    "concat",
    "layernorm",
    "ffn",
    "relu",
    "gelu",
    "sin",
    "cos",
    "min",
    "max",
    "argmax",
    "argmin",
    "log",
    "exp",
    "tr",
];

pub fn analyze_formula_preservation(context: &BlockContext) -> ProtectionPlan {
    if context.block_type == SourceBlockType::Formula || context.is_formula_like {
        return ProtectionPlan::opaque(format!(
            "BlockType={:?}, IsFormulaLike={}",
            context.block_type, context.is_formula_like
        ));
    }

    if is_font_based_formula(
        context.detected_font_names.as_deref(),
        context.formula_font_pattern.as_deref(),
    ) {
        return ProtectionPlan::opaque("MathFontDensity>30%");
    }

    if is_character_based_formula(&context.text, context.formula_char_pattern.as_deref()) {
        return ProtectionPlan::opaque("MathCharDensity>20%");
    }

    if is_subscript_dense_formula(context.formula_characters.as_ref()) {
        return ProtectionPlan::opaque("SubscriptDensity>25%");
    }

    if is_numeric_data_block(&context.text) {
        return ProtectionPlan::opaque("NumericData");
    }

    let display = get_display_equation_diagnostics(context);
    if display.candidate {
        return ProtectionPlan {
            mode: PreservationMode::Opaque,
            skip_translation: true,
            reason: Some("DisplayEquationHeuristic".to_string()),
        };
    }

    ProtectionPlan::none()
}

pub fn protect_formula_block(context: &BlockContext, plan: &ProtectionPlan) -> ProtectedBlock {
    if plan.skip_translation {
        return ProtectedBlock {
            original_text: context.text.clone(),
            protected_text: context.text.clone(),
            tokens: Vec::new(),
            soft_spans: Vec::new(),
            plan: plan.clone(),
        };
    }

    let has_exact_soft_candidates = context.retry_attempt == 0
        && formula_text_contains_exact_soft_preservation_candidate(&context.text);

    if context.retry_attempt == 0 && !has_exact_soft_candidates {
        if let (Some(protected_text), Some(tokens)) = (
            context.character_level_protected_text.as_ref(),
            context.character_level_tokens.as_ref(),
        ) {
            let formula_only = get_formula_only_classification(protected_text);
            if formula_only != FormulaOnlyClassification::No {
                let mut effective_plan = plan.clone();
                effective_plan.mode = PreservationMode::Opaque;
                effective_plan.skip_translation = true;
                effective_plan.reason = Some("CharLevel:FormulaOnlyText".to_string());
                return ProtectedBlock {
                    original_text: context.text.clone(),
                    protected_text: protected_text.clone(),
                    tokens: tokens.clone(),
                    soft_spans: Vec::new(),
                    plan: effective_plan,
                };
            }

            let mut protected_text = protected_text.clone();
            let mut soft_spans = Vec::new();
            if get_equation_soft_protection_diagnostics(&protected_text, context).candidate {
                protected_text = wrap_equation_soft_protected_text(&protected_text);
                soft_spans.push(create_equation_soft_span(&context.text, &protected_text));
            }

            let mut effective_plan = plan.clone();
            effective_plan.mode = PreservationMode::InlineProtected;
            return ProtectedBlock {
                original_text: context.text.clone(),
                protected_text,
                tokens: tokens.clone(),
                soft_spans,
                plan: effective_plan,
            };
        }
    }

    let FormulaProtectionResult {
        mut protected_text,
        hard_tokens: tokens,
        mut soft_spans,
    } = protect_formula_spans_two_tier(&context.text, context.retry_attempt);

    let formula_only = get_formula_only_classification(&protected_text);
    if formula_only != FormulaOnlyClassification::No {
        let mut effective_plan = plan.clone();
        effective_plan.mode = PreservationMode::Opaque;
        effective_plan.skip_translation = true;
        effective_plan.reason = Some("FormulaOnlyText".to_string());
        return ProtectedBlock {
            original_text: context.text.clone(),
            protected_text,
            tokens,
            soft_spans,
            plan: effective_plan,
        };
    }

    let equation = get_equation_soft_protection_diagnostics(&protected_text, context);
    if equation.candidate
        && !has_equation_soft_wrapper(&protected_text)
        && !is_already_fully_soft_protected(&protected_text, &tokens, &soft_spans)
    {
        protected_text = wrap_equation_soft_protected_text(&protected_text);
        soft_spans.push(create_equation_soft_span(&context.text, &protected_text));
    }

    let mut effective_plan = plan.clone();
    if !tokens.is_empty() || !soft_spans.is_empty() {
        effective_plan.mode = PreservationMode::InlineProtected;
    }

    ProtectedBlock {
        original_text: context.text.clone(),
        protected_text,
        tokens,
        soft_spans,
        plan: effective_plan,
    }
}

pub fn restore_formula_block(
    translated_text: &str,
    protected_block: &ProtectedBlock,
) -> RestoreOutcome {
    let outcome = if protected_block.tokens.is_empty() {
        RestoreOutcome {
            text: translated_text.to_string(),
            status: RestoreStatus::FullRestore,
            missing_token_count: 0,
            soft_validation_status: SoftValidationStatus::None,
            soft_failure_count: 0,
            synthetic_delimiter_strip_count: 0,
        }
    } else {
        let result = restore_formula_spans_with_diagnostics(
            translated_text,
            &protected_block.tokens,
            &protected_block.original_text,
            false,
        );
        RestoreOutcome {
            text: result.text,
            status: match result.status {
                FormulaRestoreStatus::FullRestore => RestoreStatus::FullRestore,
                FormulaRestoreStatus::PartialRestore => RestoreStatus::PartialRestore,
                FormulaRestoreStatus::FallbackToOriginal => RestoreStatus::FallbackToOriginal,
            },
            missing_token_count: result.dropped_count,
            soft_validation_status: SoftValidationStatus::None,
            soft_failure_count: 0,
            synthetic_delimiter_strip_count: 0,
        }
    };

    validate_soft_protected_spans(outcome, protected_block)
}

pub fn resolve_formula_fallback(
    outcome: &RestoreOutcome,
    _protected_block: &ProtectedBlock,
) -> String {
    outcome.text.clone()
}

pub fn is_font_based_formula(font_names: Option<&[String]>, custom_pattern: Option<&str>) -> bool {
    let Some(font_names) = font_names else {
        return false;
    };
    if font_names.is_empty() {
        return false;
    }

    let math_font_count = font_names
        .iter()
        .filter(|font| {
            let stripped = font
                .split_once('+')
                .map(|(_, suffix)| suffix)
                .unwrap_or(font.as_str());
            if let Some(pattern) = custom_pattern.filter(|value| !value.trim().is_empty()) {
                return RegexBuilder::new(pattern)
                    .case_insensitive(true)
                    .build()
                    .is_ok_and(|regex| regex.is_match(stripped));
            }
            math_font_regex().is_match(stripped)
        })
        .count();

    (math_font_count as f64) > (font_names.len() as f64) * 0.3
}

pub fn is_character_based_formula(text: &str, custom_pattern: Option<&str>) -> bool {
    if text.trim().is_empty() {
        return false;
    }

    let char_count = text.chars().count();
    if char_count == 0 {
        return false;
    }

    let math_char_count =
        if let Some(pattern) = custom_pattern.filter(|value| !value.trim().is_empty()) {
            RegexBuilder::new(pattern)
                .build()
                .ok()
                .map(|regex| regex.find_iter(text).count())
                .unwrap_or_else(|| count_default_math_unicode_chars(text))
        } else {
            count_default_math_unicode_chars(text)
        };
    let math_char_count = math_char_count + text.chars().filter(|ch| *ch == '\u{FFFD}').count();

    (math_char_count as f64) / (char_count as f64) > 0.2
}

pub fn is_subscript_dense_formula(formula_chars: Option<&BlockFormulaCharacters>) -> bool {
    let Some(formula_chars) = formula_chars else {
        return false;
    };
    if !formula_chars.has_math_font_characters || formula_chars.characters.is_empty() {
        return false;
    }
    let script_count = formula_chars
        .characters
        .iter()
        .filter(|item| item.is_subscript || item.is_superscript)
        .count();
    formula_chars.characters.len() >= 3
        && (script_count as f64) / (formula_chars.characters.len() as f64) > 0.25
}

pub fn is_numeric_data_block(text: &str) -> bool {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return false;
    }

    let digit_count = trimmed.chars().filter(|ch| ch.is_ascii_digit()).count();
    let letter_count = trimmed.chars().filter(|ch| ch.is_alphabetic()).count();
    if digit_count == 0 || letter_count > 8 {
        return false;
    }

    let alphanum = digit_count + letter_count;
    alphanum > 0 && (digit_count as f64) / (alphanum as f64) >= 0.65
}

pub fn normalize_for_exact_span_comparison(text: &str) -> String {
    if text.is_empty() {
        return String::new();
    }

    let replaced = text
        .replace(r"\ldots", "...")
        .replace(r"\dots", "...")
        .replace(r"\cdots", "...")
        .replace('\u{2026}', "...");

    let chars = replaced.chars().collect::<Vec<_>>();
    let mut normalized = String::with_capacity(replaced.len());
    for (index, ch) in chars.iter().enumerate() {
        if *ch == '_'
            && index > 0
            && chars[index - 1].is_alphabetic()
            && index + 1 < chars.len()
            && chars[index + 1].is_alphanumeric()
            && (index + 2 >= chars.len() || !chars[index + 2].is_alphanumeric())
        {
            continue;
        }
        normalized.push(*ch);
    }
    normalized
}

fn math_font_regex() -> &'static regex::Regex {
    static REGEX: OnceLock<regex::Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        RegexBuilder::new(MATH_FONT_PATTERN)
            .case_insensitive(true)
            .build()
            .expect("built-in math font pattern is valid")
    })
}

fn count_default_math_unicode_chars(text: &str) -> usize {
    text.chars()
        .filter(|ch| {
            matches!(
                *ch as u32,
                0x2200..=0x22FF
                    | 0x2100..=0x214F
                    | 0x0370..=0x03FF
                    | 0x2070..=0x209F
                    | 0x00B2
                    | 0x00B3
                    | 0x00B9
                    | 0x2150..=0x218F
                    | 0x27C0..=0x27EF
                    | 0x2980..=0x29FF
                    | 0x02B0..=0x02FF
                    | 0x0300..=0x036F
                    | 0x200B..=0x200D
            )
        })
        .count()
}

fn get_display_equation_diagnostics(context: &BlockContext) -> DisplayEquationDiagnostics {
    let candidate = context.text.chars().count() <= 200
        && context.text.contains('=')
        && context
            .formula_characters
            .as_ref()
            .is_some_and(|chars| chars.has_math_font_characters)
        && count_non_math_function_words(&context.text) <= 1;
    DisplayEquationDiagnostics { candidate }
}

fn get_equation_soft_protection_diagnostics(
    protected_text: &str,
    context: &BlockContext,
) -> EquationSoftDiagnostics {
    if protected_text.trim().is_empty() || protected_text.chars().count() > 220 {
        return EquationSoftDiagnostics {
            candidate: false,
            has_equals: false,
        };
    }

    let has_equals = protected_text.contains('=');
    if !has_equals {
        return EquationSoftDiagnostics {
            candidate: false,
            has_equals: false,
        };
    }

    let has_math_font_chars = context
        .formula_characters
        .as_ref()
        .is_some_and(|chars| chars.has_math_font_characters);
    let has_placeholder_evidence = contains_numeric_placeholder(protected_text);
    let non_math_word_count = count_non_math_function_words(protected_text);

    let mut left_suspicious = false;
    let mut right_suspicious = false;
    for (equals_index, _) in protected_text.match_indices('=') {
        let left = protected_text[..equals_index].trim();
        let right = protected_text[equals_index + 1..].trim();
        if left.is_empty() || right.is_empty() {
            continue;
        }
        left_suspicious |= is_suspicious_equation_side(left);
        right_suspicious |= is_suspicious_equation_side(right);
    }

    EquationSoftDiagnostics {
        candidate: (has_math_font_chars || has_placeholder_evidence)
            && non_math_word_count <= 1
            && left_suspicious
            && right_suspicious,
        has_equals: true,
    }
}

fn get_formula_only_classification(protected_text: &str) -> FormulaOnlyClassification {
    if protected_text.trim().is_empty() {
        return FormulaOnlyClassification::No;
    }

    let has_placeholders = contains_numeric_placeholder(protected_text);
    let cleaned = remove_numeric_placeholders(protected_text)
        .trim()
        .to_string();
    if cleaned.is_empty() {
        return FormulaOnlyClassification::AllPlaceholders;
    }
    if !has_placeholders {
        return FormulaOnlyClassification::No;
    }
    if has_formula_placeholders_on_both_sides_of_equals(protected_text) {
        return FormulaOnlyClassification::BothSidesOfEquals;
    }
    if is_formula_residue_only(&cleaned) {
        return FormulaOnlyClassification::ResidueOnly;
    }
    FormulaOnlyClassification::No
}

fn has_formula_placeholders_on_both_sides_of_equals(protected_text: &str) -> bool {
    protected_text.match_indices('=').any(|(index, _)| {
        contains_numeric_placeholder(&protected_text[..index])
            && contains_numeric_placeholder(&protected_text[index + 1..])
    })
}

fn is_formula_residue_only(cleaned: &str) -> bool {
    if cleaned.trim().is_empty() {
        return false;
    }

    let mut has_math_function = false;
    let mut short_alpha_token_count = 0usize;
    for token in split_formula_residue_tokens(cleaned) {
        if is_math_function_name(token) {
            has_math_function = true;
            continue;
        }

        if token.chars().all(|ch| ch.is_ascii_digit()) {
            continue;
        }

        if token.chars().count() <= 3 {
            if token.chars().all(char::is_alphabetic) {
                if COMMON_SHORT_ENGLISH_WORDS
                    .iter()
                    .any(|word| token.eq_ignore_ascii_case(word))
                {
                    return false;
                }
                short_alpha_token_count += 1;
            }
            continue;
        }

        return false;
    }

    has_math_function || short_alpha_token_count <= 1
}

fn split_formula_residue_tokens(text: &str) -> Vec<&str> {
    text.split(|ch: char| {
        ch.is_whitespace()
            || matches!(
                ch,
                '=' | '('
                    | ')'
                    | ','
                    | '+'
                    | '-'
                    | '*'
                    | '/'
                    | '^'
                    | '['
                    | ']'
                    | '{'
                    | '}'
                    | '<'
                    | '>'
                    | '|'
            )
    })
    .filter(|token| !token.trim().is_empty())
    .collect()
}

fn is_suspicious_equation_side(side: &str) -> bool {
    if side.trim().is_empty() {
        return false;
    }

    let has_placeholder = contains_numeric_placeholder(side);
    let has_equation_syntax = side
        .chars()
        .any(|ch| SUSPICIOUS_EQUATION_CHARS.contains(&ch));
    let tokens = side_tokens(side);

    let has_function_name = tokens.iter().any(|token| is_math_function_name(token));
    let has_short_token = tokens
        .iter()
        .any(|token| token.chars().all(|ch| ch.is_ascii_digit()) || token.chars().count() <= 3);
    let has_letter_digit_mix_any = tokens.iter().any(|token| has_letter_digit_mix(token));
    let has_rejected_natural_word = tokens.iter().any(|token| {
        token.chars().count() > 3 && !is_math_function_name(token) && !has_letter_digit_mix(token)
    });

    if !has_placeholder && has_rejected_natural_word && !has_equation_syntax && !has_function_name {
        return false;
    }

    has_placeholder
        || has_equation_syntax
        || has_function_name
        || has_short_token
        || has_letter_digit_mix_any
}

fn side_tokens(side: &str) -> Vec<String> {
    let mut tokens = Vec::new();
    let mut position = 0usize;
    while position < side.len() {
        if let Some((_, end)) = parse_numeric_placeholder_at(side, position) {
            position = end;
            continue;
        }
        let Some(ch) = side[position..].chars().next() else {
            break;
        };
        if ch.is_ascii_alphanumeric() {
            let start = position;
            position += ch.len_utf8();
            while let Some(next) = side[position..].chars().next() {
                if !next.is_ascii_alphanumeric() {
                    break;
                }
                position += next.len_utf8();
            }
            tokens.push(side[start..position].to_string());
        } else {
            position += ch.len_utf8();
        }
    }
    tokens
}

fn has_letter_digit_mix(token: &str) -> bool {
    let has_letter = token.chars().any(char::is_alphabetic);
    let has_digit = token.chars().any(|ch| ch.is_ascii_digit());
    has_letter && has_digit
}

fn count_non_math_function_words(text: &str) -> usize {
    let mut count = 0usize;
    let mut current = String::new();
    for ch in text.chars() {
        if ch.is_ascii_alphabetic() {
            current.push(ch);
            continue;
        }

        if current.chars().count() >= 4 && !is_math_function_name(&current) {
            count += 1;
        }
        current.clear();
    }

    if current.chars().count() >= 4 && !is_math_function_name(&current) {
        count += 1;
    }
    count
}

fn is_math_function_name(token: &str) -> bool {
    MATH_FUNCTION_NAMES
        .iter()
        .any(|candidate| token.eq_ignore_ascii_case(candidate))
}

fn wrap_equation_soft_protected_text(protected_text: &str) -> String {
    format!("{EQUATION_SOFT_OPEN_TAG}{protected_text}{EQUATION_SOFT_CLOSE_TAG}")
}

fn create_equation_soft_span(original_text: &str, wrapped_text: &str) -> SoftProtectedSpan {
    SoftProtectedSpan {
        raw_text: original_text.to_string(),
        token_type: FormulaTokenType::InlineEquation,
        wrapped_text: wrapped_text.to_string(),
        synthetic_delimiters: true,
        requires_exact_preservation: true,
        wrapper_kind: SoftProtectionWrapperKind::EquationSoftTag,
    }
}

fn has_equation_soft_wrapper(protected_text: &str) -> bool {
    protected_text.starts_with(EQUATION_SOFT_OPEN_TAG)
        && protected_text.ends_with(EQUATION_SOFT_CLOSE_TAG)
}

fn is_already_fully_soft_protected(
    protected_text: &str,
    tokens: &[FormulaToken],
    soft_spans: &[SoftProtectedSpan],
) -> bool {
    tokens.is_empty()
        && soft_spans.len() == 1
        && soft_spans[0].wrapped_text.as_str() == protected_text
}

fn validate_soft_protected_spans(
    outcome: RestoreOutcome,
    protected_block: &ProtectedBlock,
) -> RestoreOutcome {
    if protected_block.soft_spans.is_empty() || outcome.status == RestoreStatus::FallbackToOriginal
    {
        return outcome;
    }

    let exact_spans = protected_block
        .soft_spans
        .iter()
        .filter(|span| span.requires_exact_preservation)
        .collect::<Vec<_>>();
    if exact_spans.is_empty() {
        return outcome;
    }

    let mut expected_by_raw = BTreeMap::<&str, usize>::new();
    for span in &exact_spans {
        *expected_by_raw.entry(span.raw_text.as_str()).or_default() += 1;
    }

    let mut normalized_text = outcome.text.clone();
    let mut strip_count = 0usize;
    for span in &exact_spans {
        if !span.synthetic_delimiters {
            continue;
        }

        let wrapped_raw = wrapped_raw_text(span);
        let hits = count_occurrences(&normalized_text, &wrapped_raw);
        if hits == 0 {
            continue;
        }
        normalized_text = normalized_text.replace(&wrapped_raw, &span.raw_text);
        strip_count += hits;
    }

    let comparison_text = normalize_for_exact_span_comparison(&normalized_text);
    let mut soft_failure_count = 0usize;
    for (raw, expected) in expected_by_raw {
        let comparison_raw = normalize_for_exact_span_comparison(raw);
        let actual = count_occurrences(&comparison_text, &comparison_raw);
        if actual < expected {
            soft_failure_count += expected - actual;
        }
    }

    if soft_failure_count > 0 {
        return RestoreOutcome {
            text: protected_block.original_text.clone(),
            status: RestoreStatus::FallbackToOriginal,
            missing_token_count: outcome.missing_token_count,
            soft_validation_status: SoftValidationStatus::Failed,
            soft_failure_count,
            synthetic_delimiter_strip_count: strip_count,
        };
    }

    RestoreOutcome {
        text: normalized_text,
        status: outcome.status,
        missing_token_count: outcome.missing_token_count,
        soft_validation_status: if strip_count > 0 {
            SoftValidationStatus::Normalized
        } else {
            SoftValidationStatus::Passed
        },
        soft_failure_count: 0,
        synthetic_delimiter_strip_count: strip_count,
    }
}

fn wrapped_raw_text(span: &SoftProtectedSpan) -> String {
    match span.wrapper_kind {
        SoftProtectionWrapperKind::EquationSoftTag => {
            wrap_equation_soft_protected_text(&span.raw_text)
        }
        SoftProtectionWrapperKind::DollarMath => span.wrapped_text.clone(),
    }
}

fn contains_numeric_placeholder(text: &str) -> bool {
    numeric_placeholder_ranges(text).next().is_some()
}

fn remove_numeric_placeholders(text: &str) -> String {
    let mut cleaned = String::with_capacity(text.len());
    let mut position = 0usize;
    for (start, end) in numeric_placeholder_ranges(text) {
        cleaned.push_str(&text[position..start]);
        position = end;
    }
    cleaned.push_str(&text[position..]);
    cleaned
}

fn numeric_placeholder_ranges(text: &str) -> impl Iterator<Item = (usize, usize)> + '_ {
    let mut position = 0usize;
    std::iter::from_fn(move || {
        while position < text.len() {
            if let Some((_, end)) = parse_numeric_placeholder_at(text, position) {
                let start = position;
                position = end;
                return Some((start, end));
            }
            let ch = text[position..].chars().next()?;
            position += ch.len_utf8();
        }
        None
    })
}

fn parse_numeric_placeholder_at(text: &str, position: usize) -> Option<(usize, usize)> {
    if !text[position..].starts_with("{v") {
        return None;
    }
    let mut current = position + 2;
    let digit_start = current;
    while let Some(ch) = text[current..].chars().next() {
        if !ch.is_ascii_digit() {
            break;
        }
        current += ch.len_utf8();
    }
    if current == digit_start || !text[current..].starts_with('}') {
        return None;
    }
    let index = text[digit_start..current].parse::<usize>().ok()?;
    Some((index, current + 1))
}

fn count_occurrences(text: &str, value: &str) -> usize {
    if text.is_empty() || value.is_empty() {
        return 0;
    }

    let mut count = 0usize;
    let mut search_from = 0usize;
    while let Some(relative) = text[search_from..].find(value) {
        count += 1;
        search_from += relative + value.len();
    }
    count
}
