use crate::content_preservation::BlockFormulaCharacters;
use regex::Regex;
use std::sync::OnceLock;

#[derive(Clone, Debug, PartialEq)]
pub struct LetterGeometry {
    pub value: String,
    pub left: f64,
    pub right: f64,
    pub bottom: f64,
    pub top: f64,
    pub baseline_y: f64,
    pub point_size: f64,
    pub font_name: String,
}

impl LetterGeometry {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        value: impl Into<String>,
        left: f64,
        right: f64,
        bottom: f64,
        top: f64,
        baseline_y: f64,
        point_size: f64,
        font_name: impl Into<String>,
    ) -> Self {
        Self {
            value: value.into(),
            left,
            right,
            bottom,
            top,
            baseline_y,
            point_size,
            font_name: font_name.into(),
        }
    }

    pub fn width(&self) -> f64 {
        (self.right - self.left).max(0.1)
    }

    pub fn height(&self) -> f64 {
        (self.top - self.bottom).max(0.1)
    }
}

pub fn should_use_letter_based_block_text<S: AsRef<str>>(
    line_texts: &[S],
    formula_chars: Option<&BlockFormulaCharacters>,
    character_level_protected_text: Option<&str>,
) -> bool {
    if formula_chars.is_some_and(|chars| chars.has_math_font_characters) {
        return true;
    }

    if character_level_protected_text.is_some_and(|text| !text.trim().is_empty()) {
        return true;
    }

    line_texts.iter().any(|line| {
        let line = line.as_ref();
        line_contains_script_hint(line)
            || looks_like_formula_continuation_text(line)
            || previous_line_likely_expects_formula_tail(line)
    })
}

pub fn reconstruct_formula_aware_text(letters: &[LetterGeometry], word_gap_scale: f64) -> String {
    if letters.is_empty() {
        return String::new();
    }

    let grouped_lines = group_letters_into_reading_lines(letters);
    if grouped_lines.is_empty() {
        return String::new();
    }

    let line_texts = grouped_lines
        .iter()
        .map(|line| build_text_from_reading_line(line, word_gap_scale))
        .collect::<Vec<_>>();

    merge_continuation_line_texts(&grouped_lines, &line_texts)
        .join("\n")
        .trim()
        .to_string()
}

pub fn is_reconstruction_quality_acceptable(reconstructed_text: &str, fallback_text: &str) -> bool {
    if fallback_text.trim().is_empty() {
        return true;
    }

    let fallback_alpha_count = fallback_text
        .chars()
        .filter(|ch| ch.is_alphanumeric())
        .count();
    if fallback_alpha_count > 10 {
        let reconstructed_alpha_count = reconstructed_text
            .chars()
            .filter(|ch| ch.is_alphanumeric())
            .count();
        if (reconstructed_alpha_count as f64) < (fallback_alpha_count as f64) * 0.95 {
            return false;
        }
    }

    let fallback_compact = collapse_whitespace_regex()
        .replace_all(fallback_text, "")
        .to_string();
    let reconstructed_compact = collapse_whitespace_regex()
        .replace_all(reconstructed_text, "")
        .to_string();
    let fallback_anchors = count_tuple_or_equation_anchors(&fallback_compact);
    let reconstructed_anchors = count_tuple_or_equation_anchors(&reconstructed_compact);
    if fallback_anchors > reconstructed_anchors {
        return false;
    }

    let fallback_spaces = fallback_text.chars().filter(|ch| *ch == ' ').count();
    if fallback_spaces <= 2 {
        return true;
    }

    let restores_tuple_or_equation_anchors = reconstructed_anchors > fallback_anchors;
    let reconstructed_spaces = reconstructed_text.chars().filter(|ch| *ch == ' ').count();
    if (reconstructed_spaces as f64) < (fallback_spaces as f64) * 0.8
        && !restores_tuple_or_equation_anchors
    {
        return false;
    }

    let fallback_max_word_len = fallback_text
        .split(' ')
        .filter(|word| !word.is_empty() && word.chars().all(char::is_alphabetic))
        .map(|word| word.chars().count())
        .max()
        .unwrap_or(0);

    let merge_threshold = 16.max(fallback_max_word_len + 2);
    let mut reconstructed_max_word_len = 0usize;
    for word in reconstructed_text
        .split(' ')
        .filter(|word| !word.is_empty() && word.chars().all(char::is_alphabetic))
    {
        let word_len = word.chars().count();
        if word_len > merge_threshold {
            return false;
        }
        reconstructed_max_word_len = reconstructed_max_word_len.max(word_len);
    }

    if fallback_max_word_len > 0
        && (reconstructed_max_word_len as f64) > (fallback_max_word_len as f64) * 1.3 + 2.0
    {
        return false;
    }

    true
}

pub fn looks_like_formula_continuation_text(text: &str) -> bool {
    if text.trim().is_empty() {
        return false;
    }

    let trimmed = text.trim();
    if trimmed.is_empty() || trimmed.chars().count() > 24 {
        return false;
    }

    if !formula_continuation_text_regex().is_match(trimmed) {
        return false;
    }

    if long_alphabetic_run_regex().is_match(trimmed) {
        return false;
    }

    trimmed.contains("...")
        || trimmed.contains('_')
        || trimmed.contains('^')
        || trimmed
            .chars()
            .next()
            .is_some_and(|ch| ",.;:)]}".contains(ch))
}

pub fn previous_line_likely_expects_formula_tail(text: &str) -> bool {
    if text.trim().is_empty() {
        return false;
    }

    let trimmed = text.trim_end();
    if trimmed.is_empty() {
        return false;
    }

    let open_parens = trimmed.chars().filter(|ch| *ch == '(').count();
    let close_parens = trimmed.chars().filter(|ch| *ch == ')').count();
    open_parens > close_parens
        || trimmed.ends_with("...")
        || trimmed.ends_with(',')
        || trimmed.ends_with('_')
        || trimmed.ends_with('^')
}

fn line_contains_script_hint(text: &str) -> bool {
    !text.trim().is_empty() && (text.contains('_') || text.contains('^'))
}

fn count_tuple_or_equation_anchors(compact_text: &str) -> usize {
    tuple_sequence_anchor_regex()
        .find_iter(compact_text)
        .count()
        + equation_tuple_anchor_regex()
            .find_iter(compact_text)
            .count()
}

fn group_letters_into_reading_lines(letters: &[LetterGeometry]) -> Vec<ReconstructedLetterLine> {
    let mut ordered = letters.to_vec();
    ordered.sort_by(|left, right| {
        right
            .top
            .total_cmp(&left.top)
            .then_with(|| left.left.total_cmp(&right.left))
    });

    let median_scale = median_positive(
        ordered
            .iter()
            .map(|letter| positive_point_size_or_height(letter)),
        10.0,
    );
    let baseline_tolerance = 1.2_f64.max(median_scale * 0.22);
    let script_tolerance = 2.4_f64.max(median_scale * 0.75);

    let mut lines = Vec::<ReconstructedLetterLine>::new();
    for letter in ordered {
        let mut best_line = None;
        let mut best_score = f64::MAX;

        for (candidate_index, candidate) in lines.iter().enumerate() {
            let baseline_distance = (candidate.baseline_y() - letter.baseline_y).abs();
            let same_baseline = baseline_distance <= baseline_tolerance;
            let looks_like_script = letter.point_size > 0.0
                && letter.point_size < candidate.median_point_size() * 0.92
                && baseline_distance <= script_tolerance;

            if !same_baseline && !looks_like_script {
                continue;
            }

            let vertical_overlap =
                (candidate.top.min(letter.top) - candidate.bottom.max(letter.bottom)).max(0.0);
            let score = baseline_distance - vertical_overlap * 0.05;
            if score < best_score {
                best_score = score;
                best_line = Some(candidate_index);
            }
        }

        if let Some(index) = best_line {
            lines[index].add(letter);
        } else {
            lines.push(ReconstructedLetterLine::new(letter));
        }
    }

    lines.sort_by(|left, right| {
        right
            .top
            .total_cmp(&left.top)
            .then_with(|| left.left.total_cmp(&right.left))
    });
    lines
}

fn build_text_from_reading_line(line: &ReconstructedLetterLine, word_gap_scale: f64) -> String {
    let mut sorted = line.letters.clone();
    sorted.sort_by(|left, right| {
        left.left
            .total_cmp(&right.left)
            .then_with(|| right.top.total_cmp(&left.top))
    });
    if sorted.is_empty() {
        return String::new();
    }

    let median_width = median_positive(sorted.iter().map(LetterGeometry::width), 5.0);
    let median_scale = median_positive(
        sorted
            .iter()
            .map(|letter| positive_point_size_or_height(letter)),
        10.0,
    );
    let word_gap_threshold = 1.0_f64
        .max((median_width * 0.75 * word_gap_scale).min(median_scale * 0.45 * word_gap_scale));

    let mut tokens = Vec::<Vec<LetterGeometry>>::new();
    let mut current_token = vec![sorted[0].clone()];
    for index in 1..sorted.len() {
        let previous = &sorted[index - 1];
        let current = &sorted[index];
        let gap = current.left - previous.right;
        if gap > word_gap_threshold {
            tokens.push(current_token);
            current_token = Vec::new();
        }
        current_token.push(current.clone());
    }
    tokens.push(current_token);

    let token_texts = tokens
        .iter()
        .map(|token| build_script_aware_token_text(token))
        .filter(|text| !text.trim().is_empty())
        .collect::<Vec<_>>();

    normalize_reconstructed_spacing(&token_texts.join(" "))
}

fn build_script_aware_token_text(letters: &[LetterGeometry]) -> String {
    if letters.is_empty() {
        return String::new();
    }

    if letters.len() == 1 {
        return letters[0].value.clone();
    }

    let mut sorted = letters.to_vec();
    sorted.sort_by(|left, right| {
        left.left
            .total_cmp(&right.left)
            .then_with(|| right.top.total_cmp(&left.top))
    });

    let mut point_sizes = sorted
        .iter()
        .map(|letter| positive_point_size_or_height(letter))
        .filter(|size| size.is_finite() && *size > 0.0)
        .collect::<Vec<_>>();
    point_sizes.sort_by(f64::total_cmp);
    if point_sizes.len() < 2 || point_sizes[point_sizes.len() - 1] / point_sizes[0] < 1.10 {
        return concat_letter_values(&sorted);
    }

    let median_size = point_sizes[point_sizes.len() / 2];
    let median_baseline = median_all(sorted.iter().map(|letter| letter.baseline_y), 0.0);
    let position_threshold = 0.5_f64.max(median_size * 0.15);

    let mut subs = vec![false; sorted.len()];
    let mut sups = vec![false; sorted.len()];
    for (index, letter) in sorted.iter().enumerate() {
        let point_size = positive_point_size_or_height(letter);
        if point_size >= median_size * 0.87 {
            continue;
        }

        subs[index] = letter.baseline_y < median_baseline - position_threshold;
        sups[index] = letter.baseline_y > median_baseline + position_threshold;
    }

    if !subs.iter().any(|value| *value) && !sups.iter().any(|value| *value) {
        return concat_letter_values(&sorted);
    }

    if let Some(indexed_token) = try_build_simple_indexed_token_text(&sorted, &subs, &sups) {
        return indexed_token;
    }

    let mut text = String::new();
    let mut token_index = 0usize;
    while token_index < sorted.len() {
        if token_index == 0 || (!subs[token_index] && !sups[token_index]) {
            text.push_str(&sorted[token_index].value);
            token_index += 1;
            continue;
        }

        let is_sub = subs[token_index];
        let mut run_end = token_index;
        while run_end + 1 < sorted.len()
            && ((is_sub && subs[run_end + 1]) || (!is_sub && sups[run_end + 1]))
        {
            run_end += 1;
        }

        let run_text = concat_letter_values(&sorted[token_index..=run_end]);
        if !is_math_token(&run_text) {
            text.push_str(&run_text);
        } else {
            let signal = if is_sub { '_' } else { '^' };
            if run_text.chars().count() == 1 {
                text.push(signal);
                text.push_str(&run_text);
            } else {
                text.push(signal);
                text.push('{');
                text.push_str(&run_text);
                text.push('}');
            }
        }

        token_index = run_end + 1;
    }

    text
}

fn try_build_simple_indexed_token_text(
    letters: &[LetterGeometry],
    subs: &[bool],
    sups: &[bool],
) -> Option<String> {
    if letters.len() < 2 || sups.iter().any(|value| *value) {
        return None;
    }

    let first = single_char(&letters[0].value)?;
    if !first.is_alphabetic() {
        return None;
    }

    for index in 1..letters.len() {
        let ch = single_char(&letters[index].value)?;
        if !subs[index] || !ch.is_alphanumeric() {
            return None;
        }
    }

    Some(concat_letter_values(letters))
}

fn normalize_reconstructed_spacing(text: &str) -> String {
    if text.trim().is_empty() {
        return String::new();
    }

    let normalized = space_before_trailing_punctuation_regex()
        .replace_all(text, "$1")
        .to_string();
    let normalized = space_after_leading_bracket_regex()
        .replace_all(&normalized, "$1")
        .to_string();
    let normalized = add_missing_space_after_comma(&normalized);
    let normalized = space_before_closing_quote_regex()
        .replace_all(&normalized, "$1")
        .to_string();
    let normalized = space_after_opening_quote_regex()
        .replace_all(&normalized, "$1")
        .to_string();
    collapse_whitespace_regex()
        .replace_all(&normalized, " ")
        .trim()
        .to_string()
}

fn add_missing_space_after_comma(text: &str) -> String {
    let mut output = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();
    while let Some(ch) = chars.next() {
        output.push(ch);
        if ch == ','
            && chars
                .peek()
                .is_some_and(|next| !next.is_whitespace() && !matches!(next, ')' | ']' | '}'))
        {
            output.push(' ');
        }
    }
    output
}

fn merge_continuation_line_texts(
    lines: &[ReconstructedLetterLine],
    line_texts: &[String],
) -> Vec<String> {
    let mut merged = Vec::<String>::new();
    for (index, text) in line_texts.iter().enumerate() {
        let text = text.trim();
        if text.is_empty() {
            continue;
        }

        if !merged.is_empty()
            && index > 0
            && should_append_to_previous_line(
                &merged[merged.len() - 1],
                text,
                &lines[index - 1],
                &lines[index],
            )
        {
            let last = merged.len() - 1;
            merged[last] = append_continuation_text(&merged[last], text);
            continue;
        }

        merged.push(text.to_string());
    }

    merged
}

fn should_append_to_previous_line(
    previous_text: &str,
    current_text: &str,
    previous_line: &ReconstructedLetterLine,
    current_line: &ReconstructedLetterLine,
) -> bool {
    let vertical_gap = (previous_line.bottom - current_line.top).abs();
    let max_gap = 6.0_f64.max(previous_line.median_point_size() * 0.85);
    if vertical_gap > max_gap {
        return false;
    }

    looks_like_formula_continuation_text(current_text)
        || previous_line_likely_expects_formula_tail(previous_text)
}

fn append_continuation_text(previous_text: &str, continuation_text: &str) -> String {
    let trimmed_previous = previous_text.trim_end();
    let trimmed_continuation = continuation_text.trim();
    if trimmed_previous.is_empty() {
        return trimmed_continuation.to_string();
    }

    if trimmed_continuation.is_empty() {
        return trimmed_previous.to_string();
    }

    let continuation_first = trimmed_continuation.chars().next();
    let previous_last = trimmed_previous.chars().last();

    if continuation_first.is_some_and(|ch| ",.;:)]}".contains(ch))
        || trimmed_continuation.starts_with('_')
        || trimmed_continuation.starts_with('^')
    {
        return format!("{trimmed_previous}{trimmed_continuation}");
    }

    if continuation_first.is_some_and(|ch| ch.is_alphanumeric() || ch == '(')
        && previous_last.is_some_and(|ch| ch.is_alphanumeric() || matches!(ch, '(' | '_' | '^'))
    {
        return format!("{trimmed_previous}{trimmed_continuation}");
    }

    format!("{trimmed_previous} {trimmed_continuation}")
}

fn concat_letter_values(letters: &[LetterGeometry]) -> String {
    let mut output = String::new();
    for letter in letters {
        output.push_str(&letter.value);
    }
    output
}

fn single_char(value: &str) -> Option<char> {
    let mut chars = value.chars();
    let ch = chars.next()?;
    chars.next().is_none().then_some(ch)
}

fn is_math_token(token: &str) -> bool {
    !token.is_empty()
        && token.chars().all(|ch| {
            ch.is_alphanumeric()
                || matches!(ch, '+' | '-' | '=' | '.' | ',' | '(' | ')' | '/' | '*')
        })
}

fn positive_point_size_or_height(letter: &LetterGeometry) -> f64 {
    if letter.point_size > 0.0 {
        letter.point_size
    } else {
        letter.height()
    }
}

fn median_positive(values: impl IntoIterator<Item = f64>, fallback: f64) -> f64 {
    let mut values = values
        .into_iter()
        .filter(|value| value.is_finite() && *value > 0.0)
        .collect::<Vec<_>>();
    values.sort_by(f64::total_cmp);
    values.get(values.len() / 2).copied().unwrap_or(fallback)
}

fn median_all(values: impl IntoIterator<Item = f64>, fallback: f64) -> f64 {
    let mut values = values
        .into_iter()
        .filter(|value| value.is_finite())
        .collect::<Vec<_>>();
    values.sort_by(f64::total_cmp);
    values.get(values.len() / 2).copied().unwrap_or(fallback)
}

#[derive(Clone, Debug)]
struct ReconstructedLetterLine {
    letters: Vec<LetterGeometry>,
    top: f64,
    bottom: f64,
    left: f64,
    right: f64,
}

impl ReconstructedLetterLine {
    fn new(letter: LetterGeometry) -> Self {
        Self {
            top: letter.top,
            bottom: letter.bottom,
            left: letter.left,
            right: letter.right,
            letters: vec![letter],
        }
    }

    fn add(&mut self, letter: LetterGeometry) {
        self.top = self.top.max(letter.top);
        self.bottom = self.bottom.min(letter.bottom);
        self.left = self.left.min(letter.left);
        self.right = self.right.max(letter.right);
        self.letters.push(letter);
    }

    fn baseline_y(&self) -> f64 {
        median_all(self.letters.iter().map(|letter| letter.baseline_y), 0.0)
    }

    fn median_point_size(&self) -> f64 {
        median_positive(
            self.letters
                .iter()
                .map(|letter| positive_point_size_or_height(letter)),
            10.0,
        )
    }
}

fn formula_continuation_text_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(r"^[,.;:)\]\}\s_^\{\}A-Za-z0-9+\-=/\\*]+$")
            .expect("formula continuation regex is valid")
    })
}

fn long_alphabetic_run_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| Regex::new(r"[A-Za-z]{4,}").expect("alphabetic run regex is valid"))
}

fn space_before_trailing_punctuation_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(r"\s+([,.;:!?%\)\]\}])").expect("trailing punctuation regex is valid")
    })
}

fn space_after_leading_bracket_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| Regex::new(r"([(\[\{])\s+").expect("leading bracket regex is valid"))
}

fn space_before_closing_quote_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(r"\s+([\u{2019}\u{201D}])").expect("closing quote regex is valid")
    })
}

fn space_after_opening_quote_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(r"([\u{201C}\u{2018}])\s+").expect("opening quote regex is valid")
    })
}

fn collapse_whitespace_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| Regex::new(r"\s{2,}").expect("collapse whitespace regex is valid"))
}

fn tuple_sequence_anchor_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| Regex::new(r"\([A-Za-z]1").expect("tuple anchor regex is valid"))
}

fn equation_tuple_anchor_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(r"[A-Za-z]=\([A-Za-z]1").expect("equation anchor regex is valid")
    })
}
