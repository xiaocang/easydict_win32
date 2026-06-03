use easydict_app::{
    apply_preservation_hints, merge_glossaries, merge_page_partials, merge_preservation_hints,
    remove_control_characters, trim_leading_spaces_per_line, try_parse_page_partial,
    DocumentBlockIr, DocumentIr, PagePartial, MAX_PRESERVED_BLOCK_LENGTH,
};
use std::collections::BTreeMap;

#[test]
fn parses_fenced_json_partial() {
    let raw = "```json\n{\"summary\":\"hi\",\"glossary\":{},\"preservation_hints\":[]}\n```";

    let partial = try_parse_page_partial(raw, 1).expect("partial");

    assert_eq!(partial.page_number, 1);
    assert_eq!(partial.summary, "hi");
    assert!(partial.glossary.is_empty());
    assert!(partial.preservation_hints.is_empty());
}

#[test]
fn parses_prose_wrapped_json_partial_and_filters_short_hints() {
    let raw = concat!(
        "Sure, here is the analysis:\n\n",
        "{\"summary\":\"Use {x} carefully.\",",
        "\"glossary\":{\" Transformer \":\" Transformer \",\"empty\":\"   \",\"n\":42},",
        "\"preservation_hints\":[\"a\",\"ab\",\"abc\",\" abcd \",42]}",
        "\n\nThanks!"
    );

    let partial = try_parse_page_partial(raw, 7).expect("partial");

    assert_eq!(partial.summary, "Use {x} carefully.");
    assert_eq!(
        partial.glossary.get("Transformer"),
        Some(&"Transformer".to_string())
    );
    assert!(!partial.glossary.contains_key("empty"));
    assert!(!partial.glossary.contains_key("n"));
    assert_eq!(partial.preservation_hints, vec!["abc", "abcd"]);
}

#[test]
fn invalid_partial_returns_none() {
    assert!(try_parse_page_partial("this is not json at all", 1).is_none());
    assert!(try_parse_page_partial("{\"summary\":", 1).is_none());
}

#[test]
fn merges_glossary_by_page_majority_and_ties_by_earliest_page() {
    let partials = [
        partial(
            2,
            "Page 2 summary.",
            &[("Transformer", "变压器"), ("Tie", "late")],
            &["hint-2"],
        ),
        partial(
            3,
            "Page 3 summary.",
            &[("Transformer", "Transformer")],
            &["hint-3"],
        ),
        partial(
            1,
            "Page 1 summary.",
            &[("Transformer", "Transformer"), ("Tie", "early")],
            &["hint-1", "hi"],
        ),
        PagePartial::failed(4),
    ];
    let context = merge_page_partials(&partials);

    assert_eq!(
        context.glossary.get("Transformer"),
        Some(&"Transformer".to_string())
    );
    assert_eq!(context.glossary.get("Tie"), Some(&"early".to_string()));
    assert_eq!(
        context.preservation_hints,
        vec!["hint-1", "hint-2", "hint-3"]
    );
    assert_eq!(
        context.summary,
        "Page 1 summary. Page 2 summary. Page 3 summary."
    );
    assert_eq!(merge_glossaries(&partials), context.glossary);
    assert_eq!(
        merge_preservation_hints(&partials),
        context.preservation_hints
    );
}

#[test]
fn preservation_hints_match_exact_blocks() {
    let ir = DocumentIr::new(vec![block("BLEU 28.4"), block("regular paragraph")]);

    let rewritten = apply_preservation_hints(&ir, &["BLEU 28.4"]);

    assert!(rewritten.blocks[0].translation_skipped);
    assert!(rewritten.blocks[0].preserve_original_text_in_pdf_export);
    assert!(!rewritten.blocks[1].translation_skipped);
}

#[test]
fn preservation_hints_match_when_hint_contains_block_text() {
    let ir = DocumentIr::new(vec![
        block("Transformer (base model)"),
        block("regular paragraph"),
    ]);

    let rewritten = apply_preservation_hints(&ir, &["Transformer (base model) 65M 27.3 38.1 5e18"]);

    assert!(rewritten.blocks[0].translation_skipped);
    assert!(!rewritten.blocks[1].translation_skipped);
}

#[test]
fn preservation_hints_do_not_preserve_long_prose_containing_hint_substring() {
    let prose = concat!(
        "The encoder is composed of a stack of N = 6 identical layers. ",
        "Each layer applies LayerNorm(x + Sublayer(x)) where Sublayer(x) is the ",
        "function implemented by the sub-layer itself."
    );
    let ir = DocumentIr::new(vec![block(prose), block("LayerNorm(x + Sublayer(x))")]);

    let rewritten = apply_preservation_hints(&ir, &["LayerNorm(x + Sublayer(x))"]);

    assert!(!rewritten.blocks[0].translation_skipped);
    assert!(rewritten.blocks[1].translation_skipped);
}

#[test]
fn preservation_hints_ignore_short_hints_and_long_blocks() {
    let long_block = "x".repeat(MAX_PRESERVED_BLOCK_LENGTH + 1);
    let ir = DocumentIr::new(vec![block("ab"), block(&long_block)]);

    let rewritten = apply_preservation_hints(&ir, &["ab", long_block.as_str()]);

    assert!(!rewritten.blocks[0].translation_skipped);
    assert!(!rewritten.blocks[1].translation_skipped);
}

#[test]
fn remove_control_characters_preserves_newline_carriage_return_and_tab() {
    let input = "Hello\u{0000}World\u{0001}!\u{001f}End\nLine2\r\nLine3\tTabbed";

    let cleaned = remove_control_characters(input);

    assert_eq!(cleaned, "HelloWorld!End\nLine2\r\nLine3\tTabbed");
}

#[test]
fn trim_leading_spaces_per_line_trims_each_line_and_preserves_empty_lines() {
    let input = "  hello\n\tworld\n\n    indented";

    let trimmed = trim_leading_spaces_per_line(input);

    assert_eq!(trimmed, "hello\nworld\n\nindented");
}

fn partial(
    page_number: i32,
    summary: &str,
    glossary_entries: &[(&str, &str)],
    hints: &[&str],
) -> PagePartial {
    PagePartial::new(
        page_number,
        summary,
        glossary(glossary_entries),
        hints.iter().map(|hint| hint.to_string()).collect(),
    )
}

fn glossary(entries: &[(&str, &str)]) -> BTreeMap<String, String> {
    entries
        .iter()
        .map(|(source, target)| (source.to_string(), target.to_string()))
        .collect()
}

fn block(text: impl Into<String>) -> DocumentBlockIr {
    DocumentBlockIr::new(text)
}
