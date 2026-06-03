use crate::formula_protection::{FormulaToken, FormulaTokenType};
use regex::{Regex, RegexBuilder};
use std::collections::BTreeMap;
use std::sync::OnceLock;

const SUBSCRIPT_SIZE_RATIO: f64 = 0.79;
const SCRIPT_SIZE_RATIO: f64 = 0.85;
const BASELINE_THRESHOLD: f64 = 0.5;

const MATH_FONT_PATTERN: &str = r"CM[^R]|CMSY|CMMI|CMEX|MS\.M|MSAM|MSBM|XY|MT\w*Math|Symbol|Euclid|Mathematica|MathematicalPi|STIX|\bBL\b|\bRM\b|\bEU\b|\bLA\b|\bRS\b|LINE|LCIRCLE|TeX-|rsfs|txsy|wasy|stmary|\w+Sym\w*|\b\w{1,5}Math\w*";

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TextMatrix {
    pub a: f64,
    pub b: f64,
    pub c: f64,
    pub d: f64,
    pub e: f64,
    pub f: f64,
}

impl TextMatrix {
    pub const IDENTITY: Self = Self {
        a: 1.0,
        b: 0.0,
        c: 0.0,
        d: 1.0,
        e: 0.0,
        f: 0.0,
    };

    pub fn from_values(a: f64, b: f64, c: f64, d: f64, e: f64, f: f64) -> Self {
        Self { a, b, c, d, e, f }
    }

    pub fn is_vertical(&self) -> bool {
        self.a == 0.0 && self.d == 0.0
    }
}

impl Default for TextMatrix {
    fn default() -> Self {
        Self::IDENTITY
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct CharInfo {
    pub text: String,
    pub character_code: u32,
    pub cid: u32,
    pub font_name: String,
    pub font_size: f64,
    pub point_size: f64,
    pub text_matrix: TextMatrix,
    pub current_transformation_matrix: TextMatrix,
    pub x0: f64,
    pub y0: f64,
    pub x1: f64,
    pub y1: f64,
}

impl CharInfo {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        text: impl Into<String>,
        x0: f64,
        y0: f64,
        x1: f64,
        y1: f64,
        point_size: f64,
        font_name: impl Into<String>,
    ) -> Self {
        Self {
            text: text.into(),
            character_code: 0,
            cid: 0,
            font_name: font_name.into(),
            font_size: point_size,
            point_size,
            text_matrix: TextMatrix::IDENTITY,
            current_transformation_matrix: TextMatrix::IDENTITY,
            x0,
            y0,
            x1,
            y1,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct CharParagraph {
    pub text: String,
    pub protected_text: String,
    pub layout_class: i32,
    pub characters: Vec<CharInfo>,
    pub formula_variables: BTreeMap<usize, FormulaVariableGroup>,
    pub x0: f64,
    pub y0: f64,
    pub x1: f64,
    pub y1: f64,
    pub parent_font_size: f64,
}

impl Default for CharParagraph {
    fn default() -> Self {
        Self {
            text: String::new(),
            protected_text: String::new(),
            layout_class: 0,
            characters: Vec::new(),
            formula_variables: BTreeMap::new(),
            x0: f64::MAX,
            y0: f64::MAX,
            x1: f64::MIN,
            y1: f64::MIN,
            parent_font_size: 0.0,
        }
    }
}

impl CharParagraph {
    fn update_bounds(&mut self, ch: &CharInfo) {
        self.x0 = self.x0.min(ch.x0);
        self.y0 = self.y0.min(ch.y0);
        self.x1 = self.x1.max(ch.x1);
        self.y1 = self.y1.max(ch.y1);
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct FormulaVariableGroup {
    pub index: usize,
    pub characters: Vec<CharInfo>,
}

impl FormulaVariableGroup {
    pub fn x0(&self) -> f64 {
        self.characters
            .iter()
            .map(|ch| ch.x0)
            .reduce(f64::min)
            .unwrap_or(0.0)
    }

    pub fn y0(&self) -> f64 {
        self.characters
            .iter()
            .map(|ch| ch.y0)
            .reduce(f64::min)
            .unwrap_or(0.0)
    }

    pub fn x1(&self) -> f64 {
        self.characters
            .iter()
            .map(|ch| ch.x1)
            .reduce(f64::max)
            .unwrap_or(0.0)
    }

    pub fn y1(&self) -> f64 {
        self.characters
            .iter()
            .map(|ch| ch.y1)
            .reduce(f64::max)
            .unwrap_or(0.0)
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct CharParagraphResult {
    pub paragraphs: Vec<CharParagraph>,
    pub all_formula_groups: Vec<FormulaVariableGroup>,
    pub total_characters: usize,
    pub formula_characters: usize,
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum FormulaConfidence {
    None,
    Low,
    High,
}

#[derive(Clone, Debug, PartialEq)]
pub struct CharTextInfo {
    pub text: String,
    pub point_size: f64,
    pub baseline_y: f64,
    pub is_math_font: bool,
}

impl CharTextInfo {
    pub fn new(
        text: impl Into<String>,
        point_size: f64,
        baseline_y: f64,
        is_math_font: bool,
    ) -> Self {
        Self {
            text: text.into(),
            point_size,
            baseline_y,
            is_math_font,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct CharacterLevelProtection {
    pub protected_text: String,
    pub tokens: Vec<FormulaToken>,
}

pub fn build_char_paragraphs(characters: &[CharInfo]) -> CharParagraphResult {
    build_char_paragraphs_with_classifier(characters, |_, _| 1)
}

pub fn build_char_paragraphs_with_classifier(
    characters: &[CharInfo],
    classify_character: impl Fn(f64, f64) -> i32,
) -> CharParagraphResult {
    if characters.is_empty() {
        return CharParagraphResult::default();
    }

    let mut paragraphs = Vec::<CharParagraph>::new();
    let mut all_formula_groups = Vec::<FormulaVariableGroup>::new();
    let mut current_paragraph = CharParagraph::default();
    let mut current_formula_group = None::<FormulaVariableGroup>;
    let mut formula_group_index = 0usize;
    let mut total_formula_chars = 0usize;
    let mut previous_layout_class = -1;
    let mut bracket_depth = 0i32;
    let mut in_formula_mode = false;

    for ch in characters {
        let cls = classify_character((ch.x0 + ch.x1) / 2.0, (ch.y0 + ch.y1) / 2.0);
        let mut is_formula = is_formula_character(ch, current_paragraph.parent_font_size, cls);

        if in_formula_mode || is_formula {
            bracket_depth = (bracket_depth + get_bracket_delta(&ch.text)).max(0);
            if in_formula_mode && bracket_depth > 0 && !is_formula {
                is_formula = true;
            }
        }

        let is_new_paragraph = previous_layout_class >= 0
            && cls != previous_layout_class
            && cls > 0
            && previous_layout_class > 0;
        if is_new_paragraph {
            finalize_formula_group(
                &mut current_formula_group,
                &mut current_paragraph,
                &mut all_formula_groups,
            );
            bracket_depth = 0;

            if !current_paragraph.characters.is_empty()
                || !current_paragraph.formula_variables.is_empty()
            {
                current_paragraph.protected_text = current_paragraph.text.clone();
                paragraphs.push(current_paragraph);
            }
            current_paragraph = CharParagraph::default();
            formula_group_index = 0;
        }

        current_paragraph.layout_class = cls;

        if cls == 0 {
            if current_formula_group.is_none() {
                current_formula_group = Some(FormulaVariableGroup {
                    index: formula_group_index,
                    characters: Vec::new(),
                });
                formula_group_index += 1;
            }
            current_formula_group
                .as_mut()
                .expect("formula group exists")
                .characters
                .push(ch.clone());
            total_formula_chars += 1;
            in_formula_mode = true;
            previous_layout_class = cls;
            continue;
        }

        if is_formula {
            if current_formula_group.is_none() {
                current_formula_group = Some(FormulaVariableGroup {
                    index: formula_group_index,
                    characters: Vec::new(),
                });
                formula_group_index += 1;
            }
            current_formula_group
                .as_mut()
                .expect("formula group exists")
                .characters
                .push(ch.clone());
            total_formula_chars += 1;
            in_formula_mode = true;
        } else {
            finalize_formula_group(
                &mut current_formula_group,
                &mut current_paragraph,
                &mut all_formula_groups,
            );
            in_formula_mode = false;
            bracket_depth = 0;

            current_paragraph.characters.push(ch.clone());
            current_paragraph.text.push_str(&ch.text);
            current_paragraph.update_bounds(ch);
            current_paragraph.parent_font_size = ch.point_size;
        }

        previous_layout_class = cls;
    }

    finalize_formula_group(
        &mut current_formula_group,
        &mut current_paragraph,
        &mut all_formula_groups,
    );

    if !current_paragraph.characters.is_empty() || !current_paragraph.formula_variables.is_empty() {
        current_paragraph.protected_text = current_paragraph.text.clone();
        paragraphs.push(current_paragraph);
    }

    CharParagraphResult {
        paragraphs,
        all_formula_groups,
        total_characters: characters.len(),
        formula_characters: total_formula_chars,
    }
}

pub fn is_formula_character(ch: &CharInfo, parent_font_size: f64, layout_class: i32) -> bool {
    if layout_class == 0 {
        return true;
    }

    if parent_font_size > 0.0 && ch.point_size < parent_font_size * SUBSCRIPT_SIZE_RATIO {
        return true;
    }

    let font_name = strip_subset_prefix(&ch.font_name);
    let is_math_font = math_font_regex().is_match(font_name);
    if is_math_font {
        return true;
    }

    let is_math_unicode = ch.text.chars().any(is_math_unicode_char);
    if is_math_unicode {
        return true;
    }

    if ch.text_matrix.is_vertical() && (is_math_font || is_math_unicode) {
        return true;
    }

    ch.text.contains('\u{FFFD}')
}

pub fn get_formula_confidence(
    ch: &CharInfo,
    parent_font_size: f64,
    layout_class: i32,
) -> FormulaConfidence {
    if layout_class == 0 {
        return FormulaConfidence::High;
    }

    let font_name = strip_subset_prefix(&ch.font_name);
    let is_math_font = math_font_regex().is_match(font_name);
    let is_math_unicode = ch.text.chars().any(is_math_unicode_char);

    if is_math_font || is_math_unicode {
        return FormulaConfidence::High;
    }

    if ch.text_matrix.is_vertical() && (is_math_font || is_math_unicode) {
        return FormulaConfidence::High;
    }

    if parent_font_size > 0.0 && ch.point_size < parent_font_size * SUBSCRIPT_SIZE_RATIO {
        return FormulaConfidence::Low;
    }

    if ch.text.contains('\u{FFFD}') {
        return FormulaConfidence::Low;
    }

    FormulaConfidence::None
}

pub fn get_bracket_delta(text: &str) -> i32 {
    text.chars().fold(0, |delta, ch| {
        delta
            + match ch {
                '(' | '[' | '{' => 1,
                ')' | ']' | '}' => -1,
                _ => 0,
            }
    })
}

pub fn build_character_level_protection(
    characters: &[CharInfo],
) -> Option<CharacterLevelProtection> {
    let char_result = build_char_paragraphs(characters);
    if char_result.all_formula_groups.is_empty() {
        return None;
    }

    let mut hard_tokens = Vec::<FormulaToken>::new();
    let mut hard_counter = 0usize;
    let mut group_replacements = BTreeMap::<usize, String>::new();

    for group in &char_result.all_formula_groups {
        let group_confidence = group
            .characters
            .iter()
            .map(|ch| get_formula_confidence(ch, 0.0, 1))
            .min()
            .unwrap_or(FormulaConfidence::High);

        if group_confidence == FormulaConfidence::High {
            let raw = group_text(&group.characters);
            let placeholder = format!("{{v{hard_counter}}}");
            hard_tokens.push(FormulaToken {
                token_type: FormulaTokenType::InlineMath,
                raw: raw.clone(),
                placeholder: placeholder.clone(),
                simplified: raw,
            });
            group_replacements.insert(group.index, placeholder);
            hard_counter += 1;
        } else {
            let char_text_infos = group
                .characters
                .iter()
                .map(|ch| {
                    CharTextInfo::new(
                        &ch.text,
                        ch.point_size,
                        ch.y0,
                        math_font_regex().is_match(strip_subset_prefix(&ch.font_name)),
                    )
                })
                .collect::<Vec<_>>();
            group_replacements.insert(
                group.index,
                format!("${}$", reconstruct_latex_from_chars(&char_text_infos)),
            );
        }
    }

    let protected_text = char_result
        .paragraphs
        .iter()
        .map(|paragraph| {
            let mut text = paragraph.protected_text.clone();
            for (original_index, replacement) in &group_replacements {
                text = text.replace(&format!("{{v{original_index}}}"), replacement);
            }
            text
        })
        .collect::<Vec<_>>()
        .join("\n");

    Some(CharacterLevelProtection {
        protected_text,
        tokens: hard_tokens,
    })
}

pub fn reconstruct_latex_from_chars(chars: &[CharTextInfo]) -> String {
    if chars.is_empty() {
        return String::new();
    }

    let median_size = median_positive(chars.iter().map(|ch| ch.point_size), 0.0);
    let script_size_threshold = median_size * SCRIPT_SIZE_RATIO;
    let normal_baselines = chars
        .iter()
        .filter(|ch| ch.point_size > 0.0 && ch.point_size >= script_size_threshold)
        .map(|ch| ch.baseline_y);
    let median_baseline = median_all(normal_baselines, 0.0);

    let mut output = String::new();
    let mut in_subscript = false;
    let mut in_superscript = false;

    for ch in chars {
        let is_small =
            median_size > 0.0 && ch.point_size > 0.0 && ch.point_size < script_size_threshold;
        let is_below = ch.baseline_y < median_baseline - BASELINE_THRESHOLD;
        let is_above = ch.baseline_y > median_baseline + BASELINE_THRESHOLD;

        if is_small && is_below && !in_subscript {
            if in_superscript {
                output.push('}');
                in_superscript = false;
            }
            output.push_str("_{");
            in_subscript = true;
        } else if is_small && is_above && !in_superscript {
            if in_subscript {
                output.push('}');
                in_subscript = false;
            }
            output.push_str("^{");
            in_superscript = true;
        } else if !is_small && (in_subscript || in_superscript) {
            output.push('}');
            in_subscript = false;
            in_superscript = false;
        }

        append_latex_char(&mut output, &ch.text);
    }

    if in_subscript || in_superscript {
        output.push('}');
    }

    output.trim_end().to_string()
}

pub fn strip_subset_prefix(font_name: &str) -> &str {
    font_name
        .split_once('+')
        .and_then(|(_, suffix)| (!suffix.is_empty()).then_some(suffix))
        .unwrap_or(font_name)
}

fn finalize_formula_group(
    current_formula_group: &mut Option<FormulaVariableGroup>,
    current_paragraph: &mut CharParagraph,
    all_formula_groups: &mut Vec<FormulaVariableGroup>,
) {
    let Some(group) = current_formula_group
        .take()
        .filter(|group| !group.characters.is_empty())
    else {
        return;
    };

    current_paragraph
        .text
        .push_str(&format!("{{v{}}}", group.index));
    current_paragraph
        .formula_variables
        .insert(group.index, group.clone());
    all_formula_groups.push(group);
}

fn group_text(characters: &[CharInfo]) -> String {
    characters
        .iter()
        .map(|ch| ch.text.as_str())
        .collect::<String>()
}

fn append_latex_char(output: &mut String, text: &str) {
    if let Some(latex) = unicode_to_latex(text) {
        output.push_str(latex);
        output.push(' ');
    } else if text == "_" {
        output.push_str(r"\_");
    } else {
        output.push_str(text);
    }
}

fn unicode_to_latex(text: &str) -> Option<&'static str> {
    Some(match text {
        "α" => r"\alpha",
        "β" => r"\beta",
        "γ" => r"\gamma",
        "δ" => r"\delta",
        "ε" => r"\epsilon",
        "ζ" => r"\zeta",
        "η" => r"\eta",
        "θ" => r"\theta",
        "ι" => r"\iota",
        "κ" => r"\kappa",
        "λ" => r"\lambda",
        "μ" => r"\mu",
        "ν" => r"\nu",
        "ξ" => r"\xi",
        "π" => r"\pi",
        "ρ" => r"\rho",
        "σ" => r"\sigma",
        "τ" => r"\tau",
        "υ" => r"\upsilon",
        "φ" => r"\phi",
        "χ" => r"\chi",
        "ψ" => r"\psi",
        "ω" => r"\omega",
        "Γ" => r"\Gamma",
        "Δ" => r"\Delta",
        "Θ" => r"\Theta",
        "Λ" => r"\Lambda",
        "Ξ" => r"\Xi",
        "Π" => r"\Pi",
        "Σ" => r"\Sigma",
        "Υ" => r"\Upsilon",
        "Φ" => r"\Phi",
        "Ψ" => r"\Psi",
        "Ω" => r"\Omega",
        "∞" => r"\infty",
        "±" => r"\pm",
        "∓" => r"\mp",
        "×" => r"\times",
        "÷" => r"\div",
        "·" => r"\cdot",
        "≤" => r"\leq",
        "≥" => r"\geq",
        "≠" => r"\neq",
        "≈" => r"\approx",
        "≡" => r"\equiv",
        "∼" => r"\sim",
        "⊂" => r"\subset",
        "⊃" => r"\supset",
        "∪" => r"\cup",
        "∩" => r"\cap",
        "∈" => r"\in",
        "∉" => r"\notin",
        "∀" => r"\forall",
        "∃" => r"\exists",
        "∇" => r"\nabla",
        "∂" => r"\partial",
        "∫" => r"\int",
        "√" => r"\sqrt",
        "…" => r"\ldots",
        "⋯" => r"\cdots",
        "→" => r"\to",
        "←" => r"\leftarrow",
        "⇐" => r"\Leftarrow",
        "⇒" => r"\Rightarrow",
        "↔" => r"\leftrightarrow",
        "⊕" => r"\oplus",
        "⊗" => r"\otimes",
        "∘" => r"\circ",
        _ => return None,
    })
}

fn is_math_unicode_char(ch: char) -> bool {
    matches!(
        ch as u32,
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
}

fn math_font_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        RegexBuilder::new(MATH_FONT_PATTERN)
            .case_insensitive(true)
            .build()
            .expect("built-in math font pattern is valid")
    })
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
