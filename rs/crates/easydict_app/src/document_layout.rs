use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PdfRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

impl PdfRect {
    pub const fn new(x: f64, y: f64, width: f64, height: f64) -> Self {
        Self {
            x,
            y,
            width,
            height,
        }
    }

    pub fn right(&self) -> f64 {
        self.x + self.width
    }

    pub fn bottom(&self) -> f64 {
        self.y + self.height
    }
}

pub fn resolve_available_height(
    block_rect: PdfRect,
    render_line_rects: Option<&[PdfRect]>,
    background_line_rects: Option<&[PdfRect]>,
) -> f64 {
    vertical_span(background_line_rects)
        .or_else(|| vertical_span(render_line_rects))
        .unwrap_or_else(|| block_rect.height.max(1.0))
}

fn vertical_span(rects: Option<&[PdfRect]>) -> Option<f64> {
    let rects = rects?;
    if rects.is_empty() {
        return None;
    }

    let min_y = rects
        .iter()
        .map(|rect| rect.y)
        .min_by(f64::total_cmp)
        .expect("rects is not empty");
    let max_bottom = rects
        .iter()
        .map(PdfRect::bottom)
        .max_by(f64::total_cmp)
        .expect("rects is not empty");

    Some((max_bottom - min_y).max(1.0))
}

pub fn expand_line_widths(widths: &[f64], count: usize) -> Vec<f64> {
    if widths.is_empty() {
        return vec![100.0; count.max(1)];
    }

    if widths.len() >= count {
        return widths[..count].to_vec();
    }

    let mut result = Vec::with_capacity(count);
    result.extend_from_slice(widths);
    let last = *widths.last().expect("widths is not empty");
    while result.len() < count {
        result.push(last);
    }
    result
}

pub fn build_final_erase_rects_top_left(
    source_erase_rects_top_left: &[PdfRect],
    final_render_rects_top_left: &[PdfRect],
) -> Vec<PdfRect> {
    let mut combined: Vec<PdfRect> = source_erase_rects_top_left
        .iter()
        .chain(final_render_rects_top_left)
        .copied()
        .filter(|rect| rect.width > 0.1 && rect.height > 0.1)
        .collect();

    if combined.len() <= 1 {
        return combined;
    }

    combined.sort_by(|left, right| left.x.total_cmp(&right.x).then(left.y.total_cmp(&right.y)));

    let mut clusters: Vec<Vec<PdfRect>> = Vec::new();
    for rect in combined {
        let mut matching_clusters = Vec::new();
        for (index, cluster) in clusters.iter().enumerate() {
            if rects_belong_to_same_erase_band(bounds(cluster), rect) {
                matching_clusters.push(index);
            }
        }

        if matching_clusters.is_empty() {
            clusters.push(vec![rect]);
            continue;
        }

        let target_index = matching_clusters[0];
        clusters[target_index].push(rect);
        for cluster_index in matching_clusters.iter().skip(1).rev() {
            let mut merged = clusters.remove(*cluster_index);
            clusters[target_index].append(&mut merged);
        }
    }

    let mut result: Vec<PdfRect> = clusters.iter().map(|cluster| bounds(cluster)).collect();
    result.sort_by(|left, right| left.y.total_cmp(&right.y).then(left.x.total_cmp(&right.x)));
    result
}

fn bounds(rects: &[PdfRect]) -> PdfRect {
    let min_x = rects
        .iter()
        .map(|rect| rect.x)
        .min_by(f64::total_cmp)
        .expect("rects is not empty");
    let min_y = rects
        .iter()
        .map(|rect| rect.y)
        .min_by(f64::total_cmp)
        .expect("rects is not empty");
    let max_right = rects
        .iter()
        .map(PdfRect::right)
        .max_by(f64::total_cmp)
        .expect("rects is not empty");
    let max_bottom = rects
        .iter()
        .map(PdfRect::bottom)
        .max_by(f64::total_cmp)
        .expect("rects is not empty");

    PdfRect::new(min_x, min_y, max_right - min_x, max_bottom - min_y)
}

pub fn rects_belong_to_same_erase_band(left: PdfRect, right: PdfRect) -> bool {
    let horizontal_overlap = left.right().min(right.right()) - left.x.max(right.x);
    if horizontal_overlap > 3.0 {
        return true;
    }

    let horizontal_gap = (left.x.max(right.x) - left.right().min(right.right())).max(0.0);
    let tolerated_gap = (left.width.min(right.width) * 0.2).clamp(4.0, 24.0);
    horizontal_gap <= tolerated_gap
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct BlockLinePosition {
    pub baseline_y: f64,
    pub left: f64,
    pub right: f64,
}

impl BlockLinePosition {
    pub const fn new(baseline_y: f64, left: f64, right: f64) -> Self {
        Self {
            baseline_y,
            left,
            right,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct BlockTextStyle {
    pub font_size: Option<f64>,
    pub line_spacing: Option<f64>,
    pub line_positions: Vec<BlockLinePosition>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct InlineScriptLineSplit {
    pub render_line_rects: Option<Vec<PdfRect>>,
    pub protected_inline_rects: Vec<PdfRect>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct InlineSubscriptAttachment {
    pub base_char: char,
    pub subscript: String,
}

#[derive(Clone, Debug, PartialEq)]
pub struct InlineScriptOverlayResult {
    pub translated_text: String,
    pub render_line_rects: Option<Vec<PdfRect>>,
    pub background_line_rects: Option<Vec<PdfRect>>,
    pub protected_inline_rects: Vec<PdfRect>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FontSegment {
    pub text: String,
    pub needs_math_font: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FormulaFragmentKind {
    Normal,
    Subscript,
    Superscript,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FormulaFragment {
    pub text: String,
    pub kind: FormulaFragmentKind,
}

pub fn try_build_line_rects(
    page_height_points: f64,
    block_rect: PdfRect,
    style: Option<&BlockTextStyle>,
    fallback_line_height: f64,
) -> Option<Vec<PdfRect>> {
    let positions = style?.line_positions.as_slice();
    if positions.is_empty() {
        return None;
    }

    if positions.len() == 1 {
        return build_single_position_line_rects(
            block_rect,
            positions[0],
            style,
            fallback_line_height,
        );
    }

    if looks_like_grid_line_positions(positions) {
        return None;
    }

    let mut sorted_baselines: Vec<f64> = positions
        .iter()
        .map(|position| position.baseline_y)
        .collect();
    sorted_baselines.sort_by(|a, b| b.total_cmp(a));

    let mut line_spacing = positive_value(style.and_then(|style| style.line_spacing));
    if line_spacing <= 0.0 {
        let mut gaps = Vec::new();
        for pair in sorted_baselines.windows(2) {
            let gap = pair[0] - pair[1];
            if gap > 0.1 {
                gaps.push(gap);
            }
        }
        gaps.sort_by(f64::total_cmp);
        if !gaps.is_empty() {
            line_spacing = gaps[gaps.len() / 2];
        }
    }
    if line_spacing <= 0.0 {
        line_spacing = fallback_line_height.max(8.0);
    }

    let mut ordered = positions.to_vec();
    ordered.sort_by(|a, b| b.baseline_y.total_cmp(&a.baseline_y));

    let mut result = Vec::with_capacity(ordered.len());
    for (index, position) in ordered.iter().enumerate() {
        let upper_pdf = if index == 0 {
            position.baseline_y + line_spacing / 2.0
        } else {
            (ordered[index - 1].baseline_y + position.baseline_y) / 2.0
        };
        let lower_pdf = if index == ordered.len() - 1 {
            position.baseline_y - line_spacing / 2.0
        } else {
            (position.baseline_y + ordered[index + 1].baseline_y) / 2.0
        };
        if upper_pdf <= lower_pdf {
            continue;
        }

        let y = page_height_points - upper_pdf;
        let height = upper_pdf - lower_pdf;
        let left = block_rect.x.max(position.left);
        let right = block_rect.right().min(position.right);
        if right - left < 5.0 {
            continue;
        }

        let y_top = block_rect.y.max(y);
        let y_bottom = block_rect.bottom().min(y + height);
        let h = y_bottom - y_top;
        if h < 3.0 {
            continue;
        }

        result.push(PdfRect::new(left, y_top, right - left, h));
    }

    if result.is_empty() {
        None
    } else {
        Some(result)
    }
}

fn build_single_position_line_rects(
    block_rect: PdfRect,
    position: BlockLinePosition,
    style: Option<&BlockTextStyle>,
    fallback_line_height: f64,
) -> Option<Vec<PdfRect>> {
    let left = block_rect.x.max(position.left);
    let right = block_rect.right().min(position.right);
    if right - left < 5.0 || block_rect.height < 3.0 {
        return None;
    }

    let mut single_line_spacing = positive_value(style.and_then(|style| style.line_spacing));
    if single_line_spacing <= 0.0 {
        if let Some(font_size) = style
            .and_then(|style| style.font_size)
            .filter(|value| *value > 0.0)
        {
            single_line_spacing = font_size * 1.3;
        }
    }
    if single_line_spacing <= 0.0 {
        single_line_spacing = fallback_line_height.max(8.0);
    }

    let suggested = (block_rect.height / single_line_spacing.max(1.0)).floor() as usize;
    let line_count = suggested.clamp(1, 3);
    if line_count <= 1 {
        return Some(vec![PdfRect::new(
            left,
            block_rect.y,
            right - left,
            block_rect.height,
        )]);
    }

    let h = block_rect.height / line_count as f64;
    if h < 3.0 {
        return Some(vec![PdfRect::new(
            left,
            block_rect.y,
            right - left,
            block_rect.height,
        )]);
    }

    Some(
        (0..line_count)
            .map(|index| PdfRect::new(left, block_rect.y + index as f64 * h, right - left, h))
            .collect(),
    )
}

pub fn looks_like_grid_line_positions(positions: &[BlockLinePosition]) -> bool {
    if positions.len() < 2 {
        return false;
    }

    let mut baselines: Vec<f64> = positions
        .iter()
        .map(|position| position.baseline_y)
        .collect();
    baselines.sort_by(|a, b| b.total_cmp(a));
    baselines
        .windows(2)
        .any(|pair| (pair[0] - pair[1]).abs() < 0.5)
}

pub fn expand_line_rects_for_cell(
    line_rects: Option<&[PdfRect]>,
    block_rect: PdfRect,
    effective_line_height: f64,
    is_cell_like_region: bool,
) -> Option<Vec<PdfRect>> {
    let Some(line_rects) = line_rects else {
        return None;
    };
    if !is_cell_like_region || line_rects.is_empty() {
        return Some(line_rects.to_vec());
    }

    let max_lines = usize::min(
        6,
        (block_rect.height / effective_line_height.max(1.0)).floor() as usize,
    );
    if max_lines <= line_rects.len() || max_lines <= 1 {
        return Some(line_rects.to_vec());
    }

    let line_height = block_rect.height / max_lines as f64;
    if line_height < 3.0 {
        return Some(line_rects.to_vec());
    }

    Some(
        (0..max_lines)
            .map(|index| {
                PdfRect::new(
                    block_rect.x,
                    block_rect.y + index as f64 * line_height,
                    block_rect.width,
                    line_height,
                )
            })
            .collect(),
    )
}

pub fn split_line_rects_for_inline_script_protection(
    source_text: &str,
    line_rects: Option<&[PdfRect]>,
) -> InlineScriptLineSplit {
    let Some(line_rects) = line_rects else {
        return InlineScriptLineSplit {
            render_line_rects: None,
            protected_inline_rects: Vec::new(),
        };
    };

    if line_rects.is_empty() {
        return InlineScriptLineSplit {
            render_line_rects: Some(Vec::new()),
            protected_inline_rects: Vec::new(),
        };
    }

    let normalized = source_text.replace("\r\n", "\n");
    let source_lines: Vec<&str> = normalized.split('\n').collect();
    if source_lines.len() < 2 {
        return InlineScriptLineSplit {
            render_line_rects: Some(line_rects.to_vec()),
            protected_inline_rects: Vec::new(),
        };
    }

    let paired_count = usize::min(source_lines.len(), line_rects.len());
    if paired_count == 0 {
        return InlineScriptLineSplit {
            render_line_rects: Some(line_rects.to_vec()),
            protected_inline_rects: Vec::new(),
        };
    }

    let mut heights: Vec<f64> = line_rects[..paired_count]
        .iter()
        .map(|rect| rect.height.max(1.0))
        .collect();
    heights.sort_by(f64::total_cmp);
    let median_rect_height = heights[heights.len() / 2];

    let protected_indexes: Vec<usize> = (0..paired_count)
        .filter(|index| {
            looks_like_inline_script_line(source_lines[*index])
                && line_rects[*index].height <= median_rect_height * 0.75
        })
        .collect();

    if protected_indexes.is_empty() {
        return InlineScriptLineSplit {
            render_line_rects: Some(line_rects.to_vec()),
            protected_inline_rects: Vec::new(),
        };
    }

    let mut render_line_rects = Vec::with_capacity(line_rects.len() - protected_indexes.len());
    let mut protected_inline_rects = Vec::with_capacity(protected_indexes.len());
    for (index, rect) in line_rects.iter().copied().enumerate() {
        if protected_indexes.contains(&index) {
            protected_inline_rects.push(rect);
        } else {
            render_line_rects.push(rect);
        }
    }

    InlineScriptLineSplit {
        render_line_rects: Some(render_line_rects),
        protected_inline_rects,
    }
}

pub fn handle_inline_script_lines_for_overlay(
    source_text: &str,
    translated_text: &str,
    line_rects: Option<&[PdfRect]>,
) -> InlineScriptOverlayResult {
    let Some(line_rects) = line_rects else {
        return InlineScriptOverlayResult {
            translated_text: translated_text.to_string(),
            render_line_rects: None,
            background_line_rects: None,
            protected_inline_rects: Vec::new(),
        };
    };

    if line_rects.is_empty() {
        return InlineScriptOverlayResult {
            translated_text: translated_text.to_string(),
            render_line_rects: Some(Vec::new()),
            background_line_rects: Some(Vec::new()),
            protected_inline_rects: Vec::new(),
        };
    }

    let split = split_line_rects_for_inline_script_protection(source_text, Some(line_rects));
    if split.protected_inline_rects.is_empty() {
        return InlineScriptOverlayResult {
            translated_text: translated_text.to_string(),
            render_line_rects: split.render_line_rects.clone(),
            background_line_rects: split.render_line_rects,
            protected_inline_rects: Vec::new(),
        };
    }

    let mut normalized_translation = normalize_translation_for_inline_script_lines(translated_text);
    let mut script_line_indices = Vec::new();
    for (index, rect) in line_rects.iter().enumerate() {
        if split.protected_inline_rects.contains(rect) {
            script_line_indices.push(index);
        }
    }
    if script_line_indices.is_empty() {
        return InlineScriptOverlayResult {
            translated_text: normalized_translation,
            render_line_rects: split.render_line_rects.clone(),
            background_line_rects: split.render_line_rects,
            protected_inline_rects: Vec::new(),
        };
    }

    let source_lines = normalized_lines(source_text);
    let mut protected_indices = script_line_indices.clone();
    for script_line_index in script_line_indices.iter().copied() {
        let Some(script_text) = source_lines.get(script_line_index).map(|line| line.trim()) else {
            continue;
        };
        if script_text.is_empty() {
            continue;
        }

        if is_citation_like_inline_script(script_text) {
            if contains_inline_script_fragment(&normalized_translation, script_text) {
                protected_indices.retain(|index| *index != script_line_index);
            }
            continue;
        }

        if !is_probably_subscript_line(line_rects, script_line_index, &script_line_indices) {
            continue;
        }

        let Some(attachments) = build_inline_subscript_attachments_for_script_line(
            &source_lines,
            &script_line_indices,
            script_line_index,
        ) else {
            continue;
        };

        if let Some(augmented) =
            try_apply_inline_subscript_attachments(&normalized_translation, &attachments)
        {
            normalized_translation = augmented;
            protected_indices.retain(|index| *index != script_line_index);
        }
    }

    let mut background_line_rects = Vec::with_capacity(line_rects.len());
    let mut protected_inline_rects = Vec::new();
    for (index, rect) in line_rects.iter().copied().enumerate() {
        if protected_indices.contains(&index) {
            protected_inline_rects.push(rect);
        } else {
            background_line_rects.push(rect);
        }
    }

    InlineScriptOverlayResult {
        translated_text: normalized_translation,
        render_line_rects: split.render_line_rects,
        background_line_rects: Some(background_line_rects),
        protected_inline_rects,
    }
}

pub fn normalize_translation_for_inline_script_lines(translated_text: &str) -> String {
    if translated_text.is_empty() {
        return String::new();
    }

    let mut normalized = Vec::new();
    for line in normalized_lines(translated_text) {
        if !looks_like_inline_script_line(&line) {
            normalized.push(line);
            continue;
        }

        if is_citation_like_inline_script(&line) {
            let trimmed = line.trim();
            if let Some(previous) = normalized.last_mut() {
                previous.push_str(trimmed);
            } else {
                normalized.push(trimmed.to_string());
            }
        }
    }

    normalized.join("\n")
}

pub fn is_citation_like_inline_script(text: &str) -> bool {
    let trimmed = text.trim();
    let Some(inner) = trimmed
        .strip_prefix('[')
        .and_then(|value| value.strip_suffix(']'))
    else {
        return false;
    };

    let mut saw_number = false;
    for part in inner.split(',') {
        let value = part.trim();
        if value.is_empty() || !value.chars().all(|ch| ch.is_ascii_digit()) {
            return false;
        }
        saw_number = true;
    }

    saw_number
}

pub fn try_convert_to_unicode_subscript(token: &str) -> Option<String> {
    let trimmed = token.trim();
    if trimmed.is_empty() {
        return None;
    }

    let mut subscript = String::new();
    for ch in trimmed.chars() {
        if ch.is_whitespace() {
            continue;
        }

        subscript.push(map_to_unicode_subscript(ch)?);
    }

    (!subscript.is_empty()).then_some(subscript)
}

pub fn try_apply_inline_subscript_attachments(
    translated_text: &str,
    attachments: &[InlineSubscriptAttachment],
) -> Option<String> {
    if translated_text.is_empty() || attachments.is_empty() {
        return None;
    }

    let mut cursor = 0;
    let mut augmented = String::with_capacity(translated_text.len() + attachments.len() * 3);
    for attachment in attachments {
        let match_index =
            find_next_eligible_base_occurrence(translated_text, attachment.base_char, cursor)?;
        augmented.push_str(&translated_text[cursor..match_index]);
        augmented.push(attachment.base_char);
        augmented.push_str(&attachment.subscript);
        cursor = match_index + attachment.base_char.len_utf8();
    }

    augmented.push_str(&translated_text[cursor..]);
    Some(augmented)
}

pub fn looks_like_inline_script_line(text: &str) -> bool {
    let trimmed = text.trim();
    if trimmed.is_empty() || trimmed.chars().count() > 24 {
        return false;
    }

    if trimmed.chars().any(crate::text_layout::is_cjk) {
        return false;
    }

    if has_latin_word_run(trimmed, 3) {
        return false;
    }

    if !trimmed.chars().any(|ch| ch.is_alphanumeric()) {
        return false;
    }

    let has_digits = trimmed
        .chars()
        .any(|ch| ch.is_ascii_digit() || ch.is_numeric());
    let has_symbols = trimmed.chars().any(is_inline_script_symbol);
    has_digits || has_symbols || trimmed.chars().count() <= 2
}

fn normalized_lines(text: &str) -> Vec<String> {
    text.replace("\r\n", "\n")
        .split('\n')
        .map(str::to_string)
        .collect()
}

fn contains_inline_script_fragment(text: &str, fragment: &str) -> bool {
    if text.is_empty() || fragment.is_empty() {
        return false;
    }

    normalize_inline_script_search_text(text)
        .contains(&normalize_inline_script_search_text(fragment))
}

fn normalize_inline_script_search_text(text: &str) -> String {
    text.chars()
        .filter(|ch| !ch.is_whitespace())
        .map(|ch| match ch {
            '［' => '[',
            '］' => ']',
            '，' => ',',
            _ => ch,
        })
        .collect()
}

fn is_probably_subscript_line(
    line_rects: &[PdfRect],
    script_line_index: usize,
    script_line_indices: &[usize],
) -> bool {
    let Some(script_rect) = line_rects.get(script_line_index) else {
        return false;
    };
    let script_center_y = script_rect.y + script_rect.height / 2.0;

    let mut base_index = None;
    for index in (0..script_line_index).rev() {
        if !script_line_indices.contains(&index) {
            base_index = Some(index);
            break;
        }
    }
    if base_index.is_none() {
        for index in script_line_index + 1..line_rects.len() {
            if !script_line_indices.contains(&index) {
                base_index = Some(index);
                break;
            }
        }
    }

    let Some(base_rect) = base_index.and_then(|index| line_rects.get(index)) else {
        return false;
    };
    let base_center_y = base_rect.y + base_rect.height / 2.0;
    script_center_y > base_center_y + 0.5
}

fn build_inline_subscript_attachments_for_script_line(
    source_lines: &[String],
    script_line_indices: &[usize],
    script_line_index: usize,
) -> Option<Vec<InlineSubscriptAttachment>> {
    let script_text = source_lines.get(script_line_index)?;
    let tokens = split_inline_script_tokens(script_text);
    if tokens.is_empty() {
        return None;
    }

    let previous_index =
        find_previous_non_script_line_index(source_lines, script_line_indices, script_line_index)?;
    let base_char = infer_base_char_for_inline_script(&source_lines[previous_index], tokens.len())?;

    let mut attachments = Vec::with_capacity(tokens.len());
    for token in tokens {
        attachments.push(InlineSubscriptAttachment {
            base_char,
            subscript: try_convert_to_unicode_subscript(&token)?,
        });
    }

    (!attachments.is_empty()).then_some(attachments)
}

fn find_previous_non_script_line_index(
    source_lines: &[String],
    script_line_indices: &[usize],
    script_line_index: usize,
) -> Option<usize> {
    for index in (0..script_line_index).rev() {
        if script_line_indices.contains(&index) || source_lines[index].trim().is_empty() {
            continue;
        }
        return Some(index);
    }

    None
}

fn split_inline_script_tokens(script_text: &str) -> Vec<String> {
    let trimmed = script_text.trim();
    if trimmed.is_empty() {
        return Vec::new();
    }

    let mut parts: Vec<&str> = trimmed
        .split([',', '，'])
        .filter(|part| !part.is_empty())
        .collect();
    if parts.len() == 1 {
        parts = trimmed
            .split([' ', '\t'])
            .filter(|part| !part.is_empty())
            .collect();
    }

    parts
        .into_iter()
        .map(|part| part.trim().trim_end_matches([',', ';', '.', ':']))
        .filter(|token| !token.is_empty())
        .map(str::to_string)
        .collect()
}

fn infer_base_char_for_inline_script(previous_line: &str, token_count: usize) -> Option<char> {
    if token_count == 0 {
        return None;
    }

    if token_count == 1 {
        if let Some(symbol) = extract_trailing_isolated_ascii_symbol(previous_line) {
            return Some(symbol);
        }
    }

    let isolated_symbols = extract_isolated_ascii_symbols(previous_line);
    if isolated_symbols.is_empty() {
        return None;
    }

    if token_count > 1 {
        let mut counts = BTreeMap::new();
        for symbol in isolated_symbols {
            *counts.entry(symbol).or_insert(0usize) += 1;
        }
        let candidates: Vec<char> = counts
            .into_iter()
            .filter_map(|(symbol, count)| (count >= token_count).then_some(symbol))
            .collect();
        return (candidates.len() == 1).then_some(candidates[0]);
    }

    let mut unique = isolated_symbols;
    unique.sort_unstable();
    unique.dedup();
    (unique.len() == 1).then_some(unique[0])
}

fn extract_trailing_isolated_ascii_symbol(line: &str) -> Option<char> {
    let trimmed = line.trim_end();
    let chars: Vec<char> = trimmed.chars().collect();
    let mut end = chars.len().checked_sub(1)?;
    while is_trailing_script_punctuation(chars[end]) {
        if end == 0 {
            return None;
        }
        end -= 1;
    }

    let ch = chars[end];
    if !is_ascii_letter_or_digit(ch) {
        return None;
    }
    if end > 0 && is_ascii_letter_or_digit(chars[end - 1]) {
        return None;
    }

    Some(ch)
}

fn is_trailing_script_punctuation(ch: char) -> bool {
    matches!(
        ch,
        ',' | '.'
            | ';'
            | ':'
            | ')'
            | ']'
            | '}'
            | '>'
            | '?'
            | '!'
            | '，'
            | '。'
            | '；'
            | '：'
            | '）'
            | '】'
            | '」'
            | '』'
    )
}

fn extract_isolated_ascii_symbols(line: &str) -> Vec<char> {
    let chars: Vec<char> = line.chars().collect();
    let mut result = Vec::new();
    for (index, ch) in chars.iter().copied().enumerate() {
        if !is_ascii_letter_or_digit(ch) {
            continue;
        }
        if index > 0 && is_ascii_letter_or_digit(chars[index - 1]) {
            continue;
        }
        if index + 1 < chars.len() && is_ascii_letter_or_digit(chars[index + 1]) {
            continue;
        }
        result.push(ch);
    }
    result
}

fn map_to_unicode_subscript(ch: char) -> Option<char> {
    if ch.is_ascii_digit() {
        return char::from_u32('₀' as u32 + (ch as u32 - '0' as u32));
    }

    Some(match ch.to_ascii_lowercase() {
        '+' => '₊',
        '-' | '\u{2212}' => '₋',
        '=' => '₌',
        '(' => '₍',
        ')' => '₎',
        'a' => 'ₐ',
        'e' => 'ₑ',
        'h' => 'ₕ',
        'i' => 'ᵢ',
        'j' => 'ⱼ',
        'k' => 'ₖ',
        'l' => 'ₗ',
        'm' => 'ₘ',
        'n' => 'ₙ',
        'o' => 'ₒ',
        'p' => 'ₚ',
        'r' => 'ᵣ',
        's' => 'ₛ',
        't' => 'ₜ',
        'u' => 'ᵤ',
        'v' => 'ᵥ',
        'x' => 'ₓ',
        _ => return None,
    })
}

fn find_next_eligible_base_occurrence(
    text: &str,
    base_char: char,
    start_index: usize,
) -> Option<usize> {
    let start_index = start_index.min(text.len());
    for (offset, ch) in text[start_index..].char_indices() {
        let index = start_index + offset;
        if ch != base_char {
            continue;
        }

        if previous_char(text, index).is_some_and(is_ascii_letter_or_digit) {
            continue;
        }

        if next_char(text, index + ch.len_utf8()).is_some_and(|next| {
            is_ascii_letter_or_digit(next)
                || next == '_'
                || next == '^'
                || is_unicode_subscript_char(next)
        }) {
            continue;
        }

        return Some(index);
    }

    None
}

fn previous_char(text: &str, byte_index: usize) -> Option<char> {
    text[..byte_index].chars().next_back()
}

fn next_char(text: &str, byte_index: usize) -> Option<char> {
    text[byte_index..].chars().next()
}

fn is_ascii_letter_or_digit(ch: char) -> bool {
    ch.is_ascii_alphanumeric()
}

fn is_unicode_subscript_char(ch: char) -> bool {
    ('\u{2080}'..='\u{209F}').contains(&ch)
        || matches!(
            ch,
            '\u{1D62}' | '\u{1D63}' | '\u{1D64}' | '\u{1D65}' | '\u{2C7C}'
        )
}

pub fn should_apply_formula_hole(formula_hole: PdfRect, text_block_rect: PdfRect) -> bool {
    let inter_left = formula_hole.x.max(text_block_rect.x);
    let inter_top = formula_hole.y.max(text_block_rect.y);
    let inter_right = formula_hole.right().min(text_block_rect.right());
    let inter_bottom = formula_hole.bottom().min(text_block_rect.bottom());

    inter_right <= inter_left || inter_bottom <= inter_top
}

pub fn needs_math_font(ch: char) -> bool {
    matches!(
        ch,
        '\u{2200}'..='\u{22FF}' // Mathematical Operators
            | '\u{0370}'..='\u{03FF}' // Greek and Coptic
            | '\u{2100}'..='\u{214F}' // Letterlike Symbols
            | '\u{2070}'..='\u{209F}' // Superscripts and Subscripts
            | '\u{00D7}' // Multiplication sign
            | '\u{00F7}' // Division sign
            | '\u{2190}'..='\u{21FF}' // Arrows
            | '\u{2300}'..='\u{23FF}' // Miscellaneous Technical
            | '\u{27C0}'..='\u{27EF}' // Miscellaneous Mathematical Symbols-A
            | '\u{2980}'..='\u{29FF}' // Miscellaneous Mathematical Symbols-B
            | '\u{2A00}'..='\u{2AFF}' // Supplemental Mathematical Operators
            | '\u{25A0}'..='\u{25FF}' // Geometric Shapes
            | '\u{2500}'..='\u{257F}' // Box Drawing
            | '\u{2150}'..='\u{218F}' // Number Forms
    )
}

pub fn segment_line_by_font(line: &str) -> Vec<FontSegment> {
    if line.is_empty() {
        return vec![FontSegment {
            text: String::new(),
            needs_math_font: false,
        }];
    }

    let mut chars = line.chars();
    let first = chars.next().expect("line is not empty");
    let mut current = String::new();
    current.push(first);
    let mut current_needs_math = needs_math_font(first);
    let mut segments = Vec::new();

    for ch in chars {
        let char_needs_math = needs_math_font(ch);
        if char_needs_math != current_needs_math {
            segments.push(FontSegment {
                text: std::mem::take(&mut current),
                needs_math_font: current_needs_math,
            });
            current_needs_math = char_needs_math;
        }
        current.push(ch);
    }

    if !current.is_empty() {
        segments.push(FontSegment {
            text: current,
            needs_math_font: current_needs_math,
        });
    }

    segments
}

pub fn parse_formula_fragments(line: &str) -> Vec<FormulaFragment> {
    if line.is_empty() {
        return vec![FormulaFragment {
            text: String::new(),
            kind: FormulaFragmentKind::Normal,
        }];
    }

    let chars: Vec<char> = line.chars().collect();
    let mut fragments = Vec::new();
    let mut normal_buffer = String::new();
    let mut index = 0;

    while index < chars.len() {
        let ch = chars[index];
        if matches!(ch, '_' | '^') && index + 1 < chars.len() {
            if !normal_buffer.is_empty() {
                fragments.push(FormulaFragment {
                    text: std::mem::take(&mut normal_buffer),
                    kind: FormulaFragmentKind::Normal,
                });
            }

            let kind = if ch == '_' {
                FormulaFragmentKind::Subscript
            } else {
                FormulaFragmentKind::Superscript
            };
            index += 1;

            if index < chars.len() && chars[index] == '{' {
                index += 1;
                let mut group_content = String::new();
                let mut nesting = 1usize;
                while index < chars.len() && nesting > 0 {
                    match chars[index] {
                        '{' => nesting += 1,
                        '}' => nesting -= 1,
                        _ => {}
                    }

                    if nesting > 0 {
                        group_content.push(chars[index]);
                    }
                    index += 1;
                }
                fragments.push(FormulaFragment {
                    text: group_content,
                    kind,
                });
            } else if index < chars.len() {
                fragments.push(FormulaFragment {
                    text: chars[index].to_string(),
                    kind,
                });
                index += 1;
            }
        } else {
            normal_buffer.push(ch);
            index += 1;
        }
    }

    if !normal_buffer.is_empty() {
        fragments.push(FormulaFragment {
            text: normal_buffer,
            kind: FormulaFragmentKind::Normal,
        });
    }

    if fragments.is_empty() {
        vec![FormulaFragment {
            text: line.to_string(),
            kind: FormulaFragmentKind::Normal,
        }]
    } else {
        fragments
    }
}

fn has_latin_word_run(text: &str, run_length: usize) -> bool {
    let mut run = 0;
    for ch in text.chars() {
        if ch.is_ascii_alphabetic() {
            run += 1;
            if run >= run_length {
                return true;
            }
        } else {
            run = 0;
        }
    }
    false
}

fn is_inline_script_symbol(ch: char) -> bool {
    matches!(
        ch,
        '[' | ']' | '(' | ')' | '{' | '}' | '=' | '+' | '-' | '_' | '^' | '*' | '/' | '\u{2212}'
    )
}

fn positive_value(value: Option<f64>) -> f64 {
    value.filter(|value| *value > 0.0).unwrap_or(0.0)
}
