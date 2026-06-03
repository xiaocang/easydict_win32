use serde_json::Value;
use std::collections::{BTreeMap, BTreeSet};

pub const MAX_PRESERVED_BLOCK_LENGTH: usize = 300;

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct DocumentContext {
    pub summary: String,
    pub glossary: BTreeMap<String, String>,
    pub preservation_hints: Vec<String>,
}

impl DocumentContext {
    pub fn empty() -> Self {
        Self::default()
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PagePartial {
    pub page_number: i32,
    pub summary: String,
    pub glossary: BTreeMap<String, String>,
    pub preservation_hints: Vec<String>,
    pub failed: bool,
}

impl PagePartial {
    pub fn new(
        page_number: i32,
        summary: impl Into<String>,
        glossary: BTreeMap<String, String>,
        preservation_hints: Vec<String>,
    ) -> Self {
        Self {
            page_number,
            summary: summary.into(),
            glossary,
            preservation_hints,
            failed: false,
        }
    }

    pub fn failed(page_number: i32) -> Self {
        Self {
            page_number,
            summary: String::new(),
            glossary: BTreeMap::new(),
            preservation_hints: Vec::new(),
            failed: true,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentIr {
    pub blocks: Vec<DocumentBlockIr>,
}

impl DocumentIr {
    pub fn new(blocks: Vec<DocumentBlockIr>) -> Self {
        Self { blocks }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentBlockIr {
    pub original_text: String,
    pub translation_skipped: bool,
    pub preserve_original_text_in_pdf_export: bool,
}

impl DocumentBlockIr {
    pub fn new(original_text: impl Into<String>) -> Self {
        Self {
            original_text: original_text.into(),
            translation_skipped: false,
            preserve_original_text_in_pdf_export: false,
        }
    }
}

pub fn try_parse_page_partial(raw: &str, page_number: i32) -> Option<PagePartial> {
    if raw.trim().is_empty() {
        return None;
    }

    let stripped = strip_code_fence(raw.trim());
    let json = find_json_object(stripped)?;
    let root: Value = serde_json::from_str(json).ok()?;

    let summary = root
        .get("summary")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_string();

    let mut glossary = BTreeMap::new();
    if let Some(entries) = root.get("glossary").and_then(Value::as_object) {
        for (source, target) in entries {
            let Some(target) = target.as_str() else {
                continue;
            };
            let source = source.trim();
            let target = target.trim();
            if source.is_empty() || target.is_empty() {
                continue;
            }
            glossary.insert(source.to_string(), target.to_string());
        }
    }

    let preservation_hints = root
        .get("preservation_hints")
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_str)
                .map(str::trim)
                .filter(|hint| !hint.is_empty() && has_at_least_chars(hint, 3))
                .map(ToString::to_string)
                .collect()
        })
        .unwrap_or_default();

    Some(PagePartial {
        page_number,
        summary,
        glossary,
        preservation_hints,
        failed: false,
    })
}

pub fn merge_page_partials(partials: &[PagePartial]) -> DocumentContext {
    if partials.is_empty() {
        return DocumentContext::empty();
    }

    let ordered = ordered_successful_partials(partials);
    if ordered.is_empty() {
        return DocumentContext::empty();
    }

    DocumentContext {
        summary: fallback_reduce_summaries(&ordered),
        glossary: merge_glossaries_from_ordered_partials(&ordered),
        preservation_hints: merge_preservation_hints_from_ordered_partials(&ordered),
    }
}

pub fn merge_glossaries(partials: &[PagePartial]) -> BTreeMap<String, String> {
    let ordered = ordered_successful_partials(partials);
    merge_glossaries_from_ordered_partials(&ordered)
}

pub fn merge_preservation_hints(partials: &[PagePartial]) -> Vec<String> {
    let ordered = ordered_successful_partials(partials);
    merge_preservation_hints_from_ordered_partials(&ordered)
}

pub fn apply_preservation_hints<S>(ir: &DocumentIr, hints: &[S]) -> DocumentIr
where
    S: AsRef<str>,
{
    let trimmed_hints = normalize_preservation_hints(hints);
    if trimmed_hints.is_empty() {
        return ir.clone();
    }

    let mut any_changed = false;
    let blocks = ir
        .blocks
        .iter()
        .map(|block| {
            if block.translation_skipped || block.preserve_original_text_in_pdf_export {
                return block.clone();
            }

            let block_text = block.original_text.trim();
            if block_text.is_empty()
                || char_count_greater_than(block_text, MAX_PRESERVED_BLOCK_LENGTH)
            {
                return block.clone();
            }

            let matched = trimmed_hints
                .iter()
                .any(|hint| block_text == hint || hint.contains(block_text));

            if !matched {
                return block.clone();
            }

            any_changed = true;
            let mut rewritten = block.clone();
            rewritten.translation_skipped = true;
            rewritten.preserve_original_text_in_pdf_export = true;
            rewritten
        })
        .collect();

    if any_changed {
        DocumentIr { blocks }
    } else {
        ir.clone()
    }
}

pub fn remove_control_characters(text: &str) -> String {
    if text.is_empty() {
        return String::new();
    }

    text.chars()
        .filter(|ch| !ch.is_control() || matches!(ch, '\n' | '\r' | '\t'))
        .collect()
}

pub fn trim_leading_spaces_per_line(text: &str) -> String {
    if text.is_empty() {
        return String::new();
    }

    text.split('\n')
        .map(str::trim_start)
        .collect::<Vec<_>>()
        .join("\n")
}

fn strip_code_fence(raw: &str) -> &str {
    let mut text = raw.trim();
    if let Some(after_ticks) = text.strip_prefix("```") {
        if let Some(newline_index) = after_ticks.find('\n') {
            text = &after_ticks[(newline_index + 1)..];
            text = text.trim();
            if let Some(before_ticks) = text.strip_suffix("```") {
                text = before_ticks;
            }
        }
    }
    text.trim()
}

fn find_json_object(text: &str) -> Option<&str> {
    for (start, _) in text.match_indices('{') {
        let Some(end) = matching_json_object_end(text, start) else {
            continue;
        };
        let candidate = &text[start..end];
        if serde_json::from_str::<Value>(candidate).is_ok() {
            return Some(candidate);
        }
    }
    None
}

fn matching_json_object_end(text: &str, start: usize) -> Option<usize> {
    let mut depth = 0usize;
    let mut in_string = false;
    let mut escaped = false;

    for (offset, ch) in text[start..].char_indices() {
        let index = start + offset;
        if in_string {
            if escaped {
                escaped = false;
            } else if ch == '\\' {
                escaped = true;
            } else if ch == '"' {
                in_string = false;
            }
            continue;
        }

        match ch {
            '"' => in_string = true,
            '{' => depth += 1,
            '}' => {
                depth = depth.saturating_sub(1);
                if depth == 0 {
                    return Some(index + ch.len_utf8());
                }
            }
            _ => {}
        }
    }

    None
}

fn ordered_successful_partials(partials: &[PagePartial]) -> Vec<&PagePartial> {
    let mut ordered = partials
        .iter()
        .filter(|partial| !partial.failed)
        .collect::<Vec<_>>();
    ordered.sort_by_key(|partial| partial.page_number);
    ordered
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct TargetVote {
    count: usize,
    first_page: i32,
}

fn merge_glossaries_from_ordered_partials(partials: &[&PagePartial]) -> BTreeMap<String, String> {
    let mut counted_pages = BTreeSet::new();
    let mut per_term: BTreeMap<String, BTreeMap<String, TargetVote>> = BTreeMap::new();

    for partial in partials {
        for (source, target) in &partial.glossary {
            let source = source.trim();
            let target = target.trim();
            if source.is_empty() || target.is_empty() {
                continue;
            }

            let page_key = (source.to_string(), target.to_string(), partial.page_number);
            if !counted_pages.insert(page_key) {
                continue;
            }

            let votes_for_term = per_term.entry(source.to_string()).or_default();
            votes_for_term
                .entry(target.to_string())
                .and_modify(|vote| {
                    vote.count += 1;
                    vote.first_page = vote.first_page.min(partial.page_number);
                })
                .or_insert(TargetVote {
                    count: 1,
                    first_page: partial.page_number,
                });
        }
    }

    per_term
        .into_iter()
        .filter_map(|(source, targets)| {
            let best_target = targets
                .into_iter()
                .max_by(|(left_target, left_vote), (right_target, right_vote)| {
                    left_vote
                        .count
                        .cmp(&right_vote.count)
                        .then_with(|| right_vote.first_page.cmp(&left_vote.first_page))
                        .then_with(|| right_target.cmp(left_target))
                })
                .map(|(target, _)| target)?;
            Some((source, best_target))
        })
        .collect()
}

fn merge_preservation_hints_from_ordered_partials(partials: &[&PagePartial]) -> Vec<String> {
    let mut seen = BTreeSet::new();
    let mut merged = Vec::new();

    for partial in partials {
        for hint in &partial.preservation_hints {
            let hint = hint.trim();
            if hint.is_empty() || !has_at_least_chars(hint, 3) {
                continue;
            }
            if seen.insert(hint.to_string()) {
                merged.push(hint.to_string());
            }
        }
    }

    merged
}

fn fallback_reduce_summaries(partials: &[&PagePartial]) -> String {
    let summaries = partials
        .iter()
        .map(|partial| partial.summary.trim())
        .filter(|summary| !summary.is_empty())
        .collect::<Vec<_>>();

    match summaries.len() {
        0 => String::new(),
        1 => summaries[0].to_string(),
        _ => summaries
            .into_iter()
            .take(3)
            .map(first_sentence)
            .collect::<Vec<_>>()
            .join(" "),
    }
}

fn first_sentence(summary: &str) -> String {
    for (index, ch) in summary.char_indices() {
        if matches!(ch, '.' | '。' | '！' | '!' | '?' | '？') && index > 0 {
            return summary[..(index + ch.len_utf8())].trim().to_string();
        }
    }
    summary.trim().to_string()
}

fn normalize_preservation_hints<S>(hints: &[S]) -> Vec<String>
where
    S: AsRef<str>,
{
    hints
        .iter()
        .map(AsRef::as_ref)
        .map(str::trim)
        .filter(|hint| !hint.is_empty() && has_at_least_chars(hint, 3))
        .map(ToString::to_string)
        .collect()
}

fn has_at_least_chars(text: &str, count: usize) -> bool {
    text.chars().nth(count.saturating_sub(1)).is_some()
}

fn char_count_greater_than(text: &str, limit: usize) -> bool {
    text.chars().take(limit + 1).count() > limit
}
