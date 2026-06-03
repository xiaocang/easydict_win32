use crate::latex_formula::{simplify as simplify_latex, simplify_math_content};
use std::collections::HashSet;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FormulaTokenType {
    InlineMath,
    DisplayMath,
    LaTeXEnv,
    MathSubscript,
    MathSuperscript,
    GreekLetter,
    MathOperator,
    Fraction,
    SquareRoot,
    SumProduct,
    Integral,
    MathFormatting,
    Matrix,
    InlineEquation,
    SequenceToken,
    ImplicitTuple,
    UnitFragment,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FormulaToken {
    pub token_type: FormulaTokenType,
    pub raw: String,
    pub placeholder: String,
    pub simplified: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SoftProtectionWrapperKind {
    DollarMath,
    EquationSoftTag,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SoftProtectedSpan {
    pub raw_text: String,
    pub token_type: FormulaTokenType,
    pub wrapped_text: String,
    pub synthetic_delimiters: bool,
    pub requires_exact_preservation: bool,
    pub wrapper_kind: SoftProtectionWrapperKind,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FormulaProtectionResult {
    pub protected_text: String,
    pub hard_tokens: Vec<FormulaToken>,
    pub soft_spans: Vec<SoftProtectedSpan>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FormulaRestoreStatus {
    FullRestore,
    PartialRestore,
    FallbackToOriginal,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FormulaRestoreResult {
    pub text: String,
    pub status: FormulaRestoreStatus,
    pub dropped_count: usize,
    pub missing_indices: Vec<usize>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FormulaMatch {
    pub raw: String,
    pub token_type: FormulaTokenType,
    pub start: usize,
    pub length: usize,
}

const GREEK_LETTERS: &[&str] = &[
    "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa",
    "lambda", "mu", "nu", "xi", "pi", "rho", "sigma", "tau", "upsilon", "phi", "chi", "psi",
    "omega",
];

const KNOWN_LATEX_COMMANDS: &[&str] = &[
    "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa",
    "lambda", "mu", "nu", "xi", "pi", "rho", "sigma", "tau", "upsilon", "phi", "chi", "psi",
    "omega", "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Upsilon", "Phi", "Psi",
    "Omega", "sum", "prod", "int", "infty", "partial", "nabla", "forall", "exists", "subset",
    "supset", "cup", "cap", "times", "cdot", "leq", "geq", "neq", "approx", "equiv", "sim", "pm",
    "mp", "sqrt", "frac", "binom", "log", "ln", "sin", "cos", "tan", "lim", "max", "min",
];

const MATH_FORMAT_COMMANDS: &[&str] = &["mathbf", "mathrm", "mathit", "mathsf", "text"];

pub fn detect_formula_matches(text: &str) -> Vec<FormulaMatch> {
    let mut matches = Vec::new();
    let mut position = 0usize;

    while position < text.len() {
        if !text.is_char_boundary(position) {
            position += 1;
            continue;
        }

        if let Some(end) = try_display_dollar_math(text, position)
            .or_else(|| try_inline_dollar_math(text, position))
            .or_else(|| try_escaped_delimited_math(text, position, r"\(", r"\)"))
            .or_else(|| try_escaped_delimited_math(text, position, r"\[", r"\]"))
            .or_else(|| try_latex_environment(text, position))
            .or_else(|| try_latex_command(text, position))
            .or_else(|| try_script_token(text, position))
            .or_else(|| try_tuple_assignment(text, position))
            .or_else(|| try_implicit_tuple(text, position))
            .or_else(|| try_simple_equation(text, position))
        {
            let raw = text[position..end].to_string();
            let token_type = classify_formula_token(&raw);
            matches.push(FormulaMatch {
                raw,
                token_type,
                start: position,
                length: end - position,
            });
            position = end;
            continue;
        }

        let Some(ch) = text[position..].chars().next() else {
            break;
        };
        position += ch.len_utf8();
    }

    matches
}

pub fn classify_formula_token(raw_formula: &str) -> FormulaTokenType {
    if raw_formula.starts_with("$$") || raw_formula.ends_with("$$") {
        return FormulaTokenType::DisplayMath;
    }
    if raw_formula.starts_with(r"\[") || raw_formula.ends_with(r"\]") {
        return FormulaTokenType::DisplayMath;
    }
    if raw_formula.starts_with('$') || raw_formula.ends_with('$') {
        return FormulaTokenType::InlineMath;
    }
    if raw_formula.starts_with(r"\(") || raw_formula.ends_with(r"\)") {
        return FormulaTokenType::InlineMath;
    }
    if raw_formula.starts_with(r"\begin{") {
        if raw_formula.to_ascii_lowercase().contains("matrix") {
            return FormulaTokenType::Matrix;
        }
        return FormulaTokenType::LaTeXEnv;
    }
    if raw_formula.starts_with(r"\frac") {
        return FormulaTokenType::Fraction;
    }
    if raw_formula.starts_with(r"\sqrt") {
        return FormulaTokenType::SquareRoot;
    }
    if raw_formula.starts_with(r"\sum") || raw_formula.starts_with(r"\prod") {
        return FormulaTokenType::SumProduct;
    }
    if raw_formula.starts_with(r"\int") {
        return FormulaTokenType::Integral;
    }
    if raw_formula.starts_with('\\') {
        let command = raw_formula
            .trim_start_matches('\\')
            .split(|ch: char| ch == ' ' || ch == '{' || ch == '\\')
            .next()
            .unwrap_or_default();
        if is_greek_letter(command) {
            return FormulaTokenType::GreekLetter;
        }
        if MATH_FORMAT_COMMANDS
            .iter()
            .any(|candidate| command.eq_ignore_ascii_case(candidate))
        {
            return FormulaTokenType::MathFormatting;
        }
        if !simplify_math_content(&format!(r"\{command}")).is_empty() {
            return FormulaTokenType::MathOperator;
        }
        return FormulaTokenType::UnitFragment;
    }
    if is_sequence_token(raw_formula) {
        return FormulaTokenType::SequenceToken;
    }
    if raw_formula.contains('^') {
        return FormulaTokenType::MathSuperscript;
    }
    if raw_formula.contains('_') {
        return FormulaTokenType::MathSubscript;
    }
    if raw_formula.contains('=') {
        return FormulaTokenType::InlineEquation;
    }
    if raw_formula.trim_start().starts_with('(') {
        return FormulaTokenType::ImplicitTuple;
    }

    FormulaTokenType::UnitFragment
}

pub fn formula_token_type_is_high_confidence(token_type: FormulaTokenType) -> bool {
    matches!(
        token_type,
        FormulaTokenType::InlineMath
            | FormulaTokenType::DisplayMath
            | FormulaTokenType::LaTeXEnv
            | FormulaTokenType::Matrix
            | FormulaTokenType::Fraction
            | FormulaTokenType::SquareRoot
            | FormulaTokenType::SumProduct
            | FormulaTokenType::Integral
            | FormulaTokenType::GreekLetter
            | FormulaTokenType::MathOperator
            | FormulaTokenType::MathFormatting
            | FormulaTokenType::MathSuperscript
            | FormulaTokenType::MathSubscript
    )
}

pub fn formula_requires_exact_soft_preservation(
    raw_formula: &str,
    token_type: FormulaTokenType,
) -> bool {
    match token_type {
        FormulaTokenType::ImplicitTuple => {
            let trimmed = raw_formula.trim();
            trimmed.starts_with('(')
                && trimmed.ends_with(')')
                && is_tuple_sequence_body(&trimmed[1..trimmed.len() - 1])
        }
        FormulaTokenType::InlineEquation => is_tuple_assignment(raw_formula.trim()),
        _ => false,
    }
}

pub fn formula_text_contains_exact_soft_preservation_candidate(text: &str) -> bool {
    detect_formula_matches(text)
        .iter()
        .any(|item| formula_requires_exact_soft_preservation(&item.raw, item.token_type))
}

pub fn protect_formula_spans(text: &str) -> FormulaProtectionResult {
    protect_formula_spans_internal(text, false, 0)
}

pub fn protect_formula_spans_two_tier(text: &str, demote_level: usize) -> FormulaProtectionResult {
    protect_formula_spans_internal(text, true, demote_level)
}

pub fn restore_formula_spans(
    text: &str,
    tokens: &[FormulaToken],
    original_text: &str,
    use_simplified: bool,
) -> String {
    restore_formula_spans_with_diagnostics(text, tokens, original_text, use_simplified).text
}

pub fn restore_formula_spans_with_diagnostics(
    text: &str,
    tokens: &[FormulaToken],
    original_text: &str,
    use_simplified: bool,
) -> FormulaRestoreResult {
    if tokens.is_empty() || text.trim().is_empty() {
        return FormulaRestoreResult {
            text: text.to_string(),
            status: FormulaRestoreStatus::FullRestore,
            dropped_count: 0,
            missing_indices: Vec::new(),
        };
    }

    let present_indices = numeric_placeholder_indices(text)
        .into_iter()
        .filter(|index| *index < tokens.len())
        .collect::<HashSet<_>>();
    let missing_indices = (0..tokens.len())
        .filter(|index| !present_indices.contains(index))
        .collect::<Vec<_>>();
    let dropped_count = missing_indices.len();

    if present_indices.len() == tokens.len() {
        let full = replace_formula_tokens(text, tokens, use_simplified);
        if contains_numeric_placeholder(&full) || !formula_delimiters_are_balanced(&full) {
            return FormulaRestoreResult {
                text: original_text.to_string(),
                status: FormulaRestoreStatus::FallbackToOriginal,
                dropped_count: tokens.len(),
                missing_indices: (0..tokens.len()).collect(),
            };
        }

        return FormulaRestoreResult {
            text: full,
            status: FormulaRestoreStatus::FullRestore,
            dropped_count: 0,
            missing_indices: Vec::new(),
        };
    }

    if present_indices.is_empty() || present_indices.len() * 2 < tokens.len() {
        return FormulaRestoreResult {
            text: original_text.to_string(),
            status: FormulaRestoreStatus::FallbackToOriginal,
            dropped_count,
            missing_indices,
        };
    }

    let partial = replace_formula_tokens(text, tokens, use_simplified);
    if contains_numeric_placeholder(&partial) {
        return FormulaRestoreResult {
            text: original_text.to_string(),
            status: FormulaRestoreStatus::FallbackToOriginal,
            dropped_count,
            missing_indices,
        };
    }

    FormulaRestoreResult {
        text: partial,
        status: FormulaRestoreStatus::PartialRestore,
        dropped_count,
        missing_indices,
    }
}

pub fn extend_formula_trailing_parens(protected_text: &str, raw_tokens: &mut [String]) -> String {
    let mut output = String::with_capacity(protected_text.len());
    let mut position = 0usize;

    while position < protected_text.len() {
        let Some((index, placeholder_end)) = parse_numeric_placeholder_at(protected_text, position)
        else {
            let Some(ch) = protected_text[position..].chars().next() else {
                break;
            };
            output.push(ch);
            position += ch.len_utf8();
            continue;
        };

        let mut after_spaces = placeholder_end;
        while let Some(ch) = char_at(protected_text, after_spaces) {
            if !ch.is_whitespace() {
                break;
            }
            after_spaces += ch.len_utf8();
        }

        if protected_text[after_spaces..].starts_with('(') {
            if let Some(paren_end) = find_closing_delimiter(protected_text, after_spaces, '(', ')')
            {
                let content = &protected_text[after_spaces + 1..paren_end - 1];
                if content.len() <= 30 && !contains_natural_language_word(content) {
                    if let Some(raw_token) = raw_tokens.get_mut(index) {
                        raw_token.push('(');
                        raw_token.push_str(content);
                        raw_token.push(')');
                    }
                    output.push_str(&format!("{{v{index}}}"));
                    position = paren_end;
                    continue;
                }
            }
        }

        output.push_str(&protected_text[position..placeholder_end]);
        position = placeholder_end;
    }

    output
}

fn protect_formula_spans_internal(
    text: &str,
    split_by_confidence: bool,
    demote_level: usize,
) -> FormulaProtectionResult {
    if text.is_empty() {
        return FormulaProtectionResult {
            protected_text: String::new(),
            hard_tokens: Vec::new(),
            soft_spans: Vec::new(),
        };
    }

    let matches = detect_formula_matches(text);
    if matches.is_empty() {
        return FormulaProtectionResult {
            protected_text: text.to_string(),
            hard_tokens: Vec::new(),
            soft_spans: Vec::new(),
        };
    }

    let mut protected_text = String::with_capacity(text.len());
    let mut hard_tokens = Vec::new();
    let mut soft_spans = Vec::new();
    let mut last_end = 0usize;

    for item in matches {
        protected_text.push_str(&text[last_end..item.start]);

        let is_high = !split_by_confidence
            || (formula_token_type_is_high_confidence(item.token_type)
                && !formula_token_type_is_demoted(item.token_type, demote_level));
        if is_high {
            let placeholder = format!("{{v{}}}", hard_tokens.len());
            let simplified = build_formula_simplified(&item.raw, item.token_type);
            hard_tokens.push(FormulaToken {
                token_type: item.token_type,
                raw: item.raw,
                placeholder: placeholder.clone(),
                simplified,
            });
            protected_text.push_str(&placeholder);
        } else {
            let escaped = item.raw.replace('$', r"\$");
            let wrapped = format!("${escaped}$");
            soft_spans.push(SoftProtectedSpan {
                raw_text: item.raw.clone(),
                token_type: item.token_type,
                wrapped_text: wrapped.clone(),
                synthetic_delimiters: true,
                requires_exact_preservation: formula_requires_exact_soft_preservation(
                    &item.raw,
                    item.token_type,
                ),
                wrapper_kind: SoftProtectionWrapperKind::DollarMath,
            });
            protected_text.push_str(&wrapped);
        }

        last_end = item.start + item.length;
    }
    protected_text.push_str(&text[last_end..]);

    if !hard_tokens.is_empty() {
        let mut raw_tokens = hard_tokens
            .iter()
            .map(|token| token.raw.clone())
            .collect::<Vec<_>>();
        protected_text = extend_formula_trailing_parens(&protected_text, &mut raw_tokens);
        for (index, raw) in raw_tokens.into_iter().enumerate() {
            if raw != hard_tokens[index].raw {
                let token_type = classify_formula_token(&raw);
                hard_tokens[index] = FormulaToken {
                    token_type,
                    raw: raw.clone(),
                    placeholder: format!("{{v{index}}}"),
                    simplified: build_formula_simplified(&raw, token_type),
                };
            }
        }
    }

    FormulaProtectionResult {
        protected_text,
        hard_tokens,
        soft_spans,
    }
}

fn build_formula_simplified(raw: &str, token_type: FormulaTokenType) -> String {
    match token_type {
        FormulaTokenType::SequenceToken => raw.replace('_', "-"),
        FormulaTokenType::DisplayMath
        | FormulaTokenType::InlineMath
        | FormulaTokenType::LaTeXEnv
        | FormulaTokenType::Matrix => simplify_latex(raw),
        _ => simplify_math_content(raw),
    }
}

fn formula_token_type_is_demoted(token_type: FormulaTokenType, demote_level: usize) -> bool {
    demote_level >= 1
        && matches!(
            token_type,
            FormulaTokenType::MathSubscript
                | FormulaTokenType::MathSuperscript
                | FormulaTokenType::Fraction
                | FormulaTokenType::SquareRoot
        )
}

fn try_display_dollar_math(text: &str, position: usize) -> Option<usize> {
    if !text[position..].starts_with("$$") {
        return None;
    }

    text[position + 2..]
        .find("$$")
        .filter(|end| *end > 0)
        .map(|relative_end| position + 2 + relative_end + 2)
}

fn try_inline_dollar_math(text: &str, position: usize) -> Option<usize> {
    if !text[position..].starts_with('$') || text[position..].starts_with("$$") {
        return None;
    }

    let content_start = position + 1;
    let relative_end = text[content_start..].find('$')?;
    if relative_end == 0 || text[content_start..content_start + relative_end].contains('\n') {
        return None;
    }

    Some(content_start + relative_end + 1)
}

fn try_escaped_delimited_math(
    text: &str,
    position: usize,
    open: &str,
    close: &str,
) -> Option<usize> {
    if !text[position..].starts_with(open) {
        return None;
    }

    let content_start = position + open.len();
    let relative_end = text[content_start..].find(close)?;
    (relative_end > 0).then_some(content_start + relative_end + close.len())
}

fn try_latex_environment(text: &str, position: usize) -> Option<usize> {
    if !text[position..].starts_with(r"\begin{") {
        return None;
    }

    let env_start = position + r"\begin{".len();
    let relative_env_end = text[env_start..].find('}')?;
    let search_start = env_start + relative_env_end + 1;
    let relative_end = text[search_start..].find(r"\end{")?;
    let end_start = search_start + relative_end;
    let end_env_start = end_start + r"\end{".len();
    let relative_end_env = text[end_env_start..].find('}')?;
    Some(end_env_start + relative_end_env + 1)
}

fn try_latex_command(text: &str, position: usize) -> Option<usize> {
    if !text[position..].starts_with('\\') {
        return None;
    }
    let command_start = position + 1;
    let (command, command_end) = parse_ascii_command(text, command_start)?;
    KNOWN_LATEX_COMMANDS
        .iter()
        .any(|candidate| *candidate == command)
        .then_some(command_end)
}

fn try_script_token(text: &str, position: usize) -> Option<usize> {
    if !is_word_boundary_start(text, position) {
        return None;
    }

    let mut current = position;
    let mut saw_base = false;
    while let Some(ch) = char_at(text, current) {
        if !ch.is_alphanumeric() {
            break;
        }
        saw_base = true;
        current += ch.len_utf8();
    }
    if !saw_base {
        return None;
    }

    let mut saw_script = false;
    while let Some(marker) = char_at(text, current) {
        if marker != '_' && marker != '^' {
            break;
        }
        let marker_start = current;
        current += marker.len_utf8();
        if text[current..].starts_with('{') {
            let close = find_closing_delimiter(text, current, '{', '}')?;
            current = close;
            saw_script = true;
            continue;
        }

        let Some(script_char) = char_at(text, current) else {
            return None;
        };
        if !script_char.is_alphanumeric() {
            return None;
        }
        current += script_char.len_utf8();
        if char_at(text, current).is_some_and(char::is_alphanumeric) {
            return None;
        }
        saw_script = true;

        if current <= marker_start {
            return None;
        }
    }

    saw_script.then_some(current)
}

fn try_tuple_assignment(text: &str, position: usize) -> Option<usize> {
    if !is_word_boundary_start(text, position) {
        return None;
    }
    let lhs = char_at(text, position)?;
    if !lhs.is_ascii_alphabetic() {
        return None;
    }
    let mut current = position + lhs.len_utf8();
    current = skip_ascii_whitespace(text, current);
    if !text[current..].starts_with('=') {
        return None;
    }
    current += 1;
    current = skip_ascii_whitespace(text, current);
    if !text[current..].starts_with('(') {
        return None;
    }
    let end = find_closing_delimiter(text, current, '(', ')')?;
    is_tuple_sequence_body(&text[current + 1..end - 1]).then_some(end)
}

fn try_implicit_tuple(text: &str, position: usize) -> Option<usize> {
    if !text[position..].starts_with('(') {
        return None;
    }
    let end = find_closing_delimiter(text, position, '(', ')')?;
    is_tuple_sequence_body(&text[position + 1..end - 1]).then_some(end)
}

fn try_simple_equation(text: &str, position: usize) -> Option<usize> {
    if !is_word_boundary_start(text, position) {
        return None;
    }

    let mut current = position;
    let mut saw_lhs = false;
    while let Some(ch) = char_at(text, current) {
        if !ch.is_alphanumeric() {
            break;
        }
        saw_lhs = true;
        current += ch.len_utf8();
    }
    if !saw_lhs {
        return None;
    }

    current = skip_ascii_whitespace(text, current);
    if !text[current..].starts_with('=') {
        return None;
    }
    current += 1;
    current = skip_ascii_whitespace(text, current);
    let rhs_start = current;
    while let Some(ch) = char_at(text, current) {
        if ch.is_whitespace() || matches!(ch, ',' | ';' | '.' | '(') {
            break;
        }
        current += ch.len_utf8();
    }

    (current > rhs_start).then_some(current)
}

fn is_tuple_assignment(value: &str) -> bool {
    try_tuple_assignment(value, 0) == Some(value.len())
}

fn is_tuple_sequence_body(value: &str) -> bool {
    let parts = value.split(',').map(str::trim).collect::<Vec<_>>();
    if parts.len() < 2 {
        return false;
    }
    if !is_tuple_variable(parts[0], true) {
        return false;
    }

    parts[1..]
        .iter()
        .all(|part| is_tuple_ellipsis(part) || is_tuple_variable(part, false))
}

fn is_tuple_variable(value: &str, require_digit: bool) -> bool {
    let value = value.trim();
    let mut chars = value.chars();
    let Some(first) = chars.next() else {
        return false;
    };
    if !first.is_ascii_alphabetic() {
        return false;
    }

    let rest = chars.collect::<String>();
    let rest = rest.strip_prefix('_').unwrap_or(&rest);
    if rest.is_empty() {
        return false;
    }

    if require_digit {
        rest.chars().all(|ch| ch.is_ascii_digit())
    } else {
        rest.chars().all(|ch| ch.is_ascii_alphanumeric())
            && (rest.chars().all(|ch| ch.is_ascii_digit())
                || (rest.chars().count() == 1 && rest.chars().all(|ch| ch.is_ascii_alphabetic())))
    }
}

fn is_tuple_ellipsis(value: &str) -> bool {
    matches!(
        value.trim(),
        "..." | ".." | "…" | r"\ldots" | r"\dots" | r"\cdots"
    )
}

fn is_sequence_token(value: &str) -> bool {
    let Some((base, tail)) = value.split_once('_') else {
        return false;
    };
    base.chars().count() >= 6
        && base.chars().all(char::is_alphabetic)
        && !tail.is_empty()
        && tail.chars().all(char::is_alphanumeric)
}

fn replace_formula_tokens(text: &str, tokens: &[FormulaToken], use_simplified: bool) -> String {
    let mut output = String::with_capacity(text.len());
    let mut position = 0usize;

    while position < text.len() {
        if let Some((index, end)) = parse_numeric_placeholder_at(text, position) {
            if let Some(token) = tokens.get(index) {
                output.push_str(if use_simplified {
                    &token.simplified
                } else {
                    &token.raw
                });
            } else {
                output.push_str(&text[position..end]);
            }
            position = end;
            continue;
        }

        let Some(ch) = text[position..].chars().next() else {
            break;
        };
        output.push(ch);
        position += ch.len_utf8();
    }

    output
}

fn contains_numeric_placeholder(text: &str) -> bool {
    let mut position = 0usize;
    while position < text.len() {
        if parse_numeric_placeholder_at(text, position).is_some() {
            return true;
        }
        let Some(ch) = text[position..].chars().next() else {
            break;
        };
        position += ch.len_utf8();
    }
    false
}

fn numeric_placeholder_indices(text: &str) -> Vec<usize> {
    let mut indices = Vec::new();
    let mut position = 0usize;
    while position < text.len() {
        if let Some((index, end)) = parse_numeric_placeholder_at(text, position) {
            indices.push(index);
            position = end;
            continue;
        }

        let Some(ch) = text[position..].chars().next() else {
            break;
        };
        position += ch.len_utf8();
    }
    indices
}

fn parse_numeric_placeholder_at(text: &str, position: usize) -> Option<(usize, usize)> {
    if !text[position..].starts_with("{v") {
        return None;
    }
    let digit_start = position + 2;
    let mut current = digit_start;
    while let Some(ch) = char_at(text, current) {
        if !ch.is_ascii_digit() {
            break;
        }
        current += ch.len_utf8();
    }
    if current == digit_start || !text[current..].starts_with('}') {
        return None;
    }

    text[digit_start..current]
        .parse::<usize>()
        .ok()
        .map(|index| (index, current + 1))
}

fn formula_delimiters_are_balanced(text: &str) -> bool {
    let mut stack = Vec::new();
    let mut dollar_count = 0usize;

    for ch in text.chars() {
        match ch {
            '$' => dollar_count += 1,
            '(' | '[' | '{' => stack.push(ch),
            ')' => {
                if stack.pop() != Some('(') {
                    return false;
                }
            }
            ']' => {
                if stack.pop() != Some('[') {
                    return false;
                }
            }
            '}' => {
                if stack.pop() != Some('{') {
                    return false;
                }
            }
            _ => {}
        }
    }

    stack.is_empty() && dollar_count % 2 == 0
}

fn contains_natural_language_word(text: &str) -> bool {
    let mut current_len = 0usize;
    for ch in text.chars() {
        if ch.is_ascii_alphabetic() {
            current_len += 1;
            if current_len >= 4 {
                return true;
            }
        } else {
            current_len = 0;
        }
    }
    false
}

fn is_greek_letter(command: &str) -> bool {
    GREEK_LETTERS
        .iter()
        .any(|candidate| command.eq_ignore_ascii_case(candidate))
}

fn parse_ascii_command(text: &str, start: usize) -> Option<(&str, usize)> {
    let mut end = start;
    while end < text.len() {
        let byte = text.as_bytes()[end];
        if !byte.is_ascii_alphabetic() {
            break;
        }
        end += 1;
    }

    (end > start).then_some((&text[start..end], end))
}

fn find_closing_delimiter(
    text: &str,
    open_position: usize,
    open: char,
    close: char,
) -> Option<usize> {
    let mut depth = 0usize;
    for (relative_index, ch) in text[open_position..].char_indices() {
        let index = open_position + relative_index;
        if ch == open {
            depth += 1;
        } else if ch == close {
            depth = depth.checked_sub(1)?;
            if depth == 0 {
                return Some(index + ch.len_utf8());
            }
        }
    }

    None
}

fn skip_ascii_whitespace(text: &str, mut position: usize) -> usize {
    while let Some(ch) = char_at(text, position) {
        if !ch.is_ascii_whitespace() {
            break;
        }
        position += ch.len_utf8();
    }
    position
}

fn is_word_boundary_start(text: &str, position: usize) -> bool {
    if position == 0 {
        return true;
    }
    text[..position]
        .chars()
        .next_back()
        .is_none_or(|ch| !ch.is_alphanumeric())
}

fn char_at(text: &str, position: usize) -> Option<char> {
    text.get(position..)?.chars().next()
}
