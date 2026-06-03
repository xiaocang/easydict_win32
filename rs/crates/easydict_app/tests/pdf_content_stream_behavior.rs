use easydict_app::{
    build_content_stream, cid_to_hex, escape_pdf_literal_string, extract_pdf_literal_strings,
    find_text_operator_range, find_text_operator_range_bytes, generate_text_operator,
    hide_text_operator_in_stream, normalize_pdf_text_for_match, parse_pdf_literal_string,
    replace_text_operator_in_stream, replace_text_operator_in_stream_bytes,
    try_patch_pdf_array_text_token, try_patch_pdf_literal_token, TextOperatorRange,
};

#[test]
fn native_pdf_content_stream_encodes_cids_like_content_stream_interpreter() {
    assert_eq!(cid_to_hex(0x41, false), "41");
    assert_eq!(cid_to_hex(0xFF, false), "FF");
    assert_eq!(cid_to_hex(0x00, false), "00");
    assert_eq!(cid_to_hex(0x41, true), "0041");
    assert_eq!(cid_to_hex(0x1234, true), "1234");
    assert_eq!(cid_to_hex(0xFFFF, true), "FFFF");
    assert_eq!(cid_to_hex(0x0000, true), "0000");
}

#[test]
fn native_pdf_content_stream_generates_pdf2zh_text_operator_format() {
    assert_eq!(
        generate_text_operator("noto", 10.0, 50.0, 750.0, "4E2D"),
        "/noto 10.000000 Tf 1 0 0 1 50.000000 750.000000 Tm [<4E2D>] TJ "
    );

    let combined = format!(
        "{}{}",
        generate_text_operator("F1", 12.0, 100.0, 200.0, "48"),
        generate_text_operator("F1", 12.0, 106.0, 200.0, "69")
    );
    assert_eq!(combined.matches("Tf").count(), 2);
    assert_eq!(combined.matches("TJ").count(), 2);
}

#[test]
fn native_pdf_content_stream_builds_wrapped_stream_with_origin_and_erase_ops() {
    let stream = build_content_stream(
        b"1 0 0 1 0 0 cm\n",
        "/F1 12 Tf [<41>] TJ ",
        72.0,
        36.0,
        "1 1 1 rg 0 0 10 10 re f ",
    );
    let content = String::from_utf8(stream).unwrap();

    assert!(content.starts_with("q "));
    assert!(content.contains("1 0 0 1 0 0 cm"));
    assert!(content.contains("Q q 1 1 1 rg 0 0 10 10 re f Q "));
    assert!(content.contains("1 0 0 1 72.000000 36.000000 cm"));
    assert!(content.contains("BT /F1 12 Tf [<41>] TJ ET"));
}

#[test]
fn native_pdf_content_stream_preserves_raw_graphics_bytes() {
    let stream = build_content_stream(&[0xFF, b' ', b'm'], "", 0.0, 0.0, "");

    assert_eq!(&stream[..2], b"q ");
    assert_eq!(stream[2], 0xFF);
    assert!(stream.ends_with(b"BT ET"));
}

#[test]
fn native_pdf_content_stream_parses_escaped_and_nested_literal_strings() {
    let content = r"(a\(b\)\\c(nested)) Tj";
    let literal = parse_pdf_literal_string(content, 0).expect("literal");

    assert_eq!(literal.start, 0);
    assert_eq!(literal.length, "(a\\(b\\)\\\\c(nested))".len());
    assert_eq!(literal.value, "a(b)\\c(nested)");
    assert_eq!(escape_pdf_literal_string(r"a(b)\c"), r"a\(b\)\\c");
}

#[test]
fn native_pdf_content_stream_extracts_literal_strings_from_tj_arrays() {
    let extracted = extract_pdf_literal_strings(r"[(Hello) -80 (World) (escaped\(paren\))]");

    assert_eq!(
        extracted
            .iter()
            .map(|item| item.value.as_str())
            .collect::<Vec<_>>(),
        ["Hello", "World", "escaped(paren)"]
    );
    assert_eq!(normalize_pdf_text_for_match("Hello \n World"), "HelloWorld");
}

#[test]
fn native_pdf_content_stream_finds_literal_tj_operator_ranges() {
    assert_eq!(
        find_text_operator_range("BT /F1 11 Tf (Hello World) Tj ET", "Hello World"),
        Some(TextOperatorRange { start: 13, end: 29 })
    );
    assert_eq!(
        find_text_operator_range("(escaped\\(paren\\)) Tj", "escaped(paren)"),
        Some(TextOperatorRange { start: 0, end: 21 })
    );
    assert_eq!(
        find_text_operator_range("BT /F1 11 Tf (Hello) Tj0 ET", "Hello"),
        None
    );
    assert_eq!(
        find_text_operator_range("BT /F1 11 Tf (Hello World) Tj ET", "Hello\nWorld"),
        Some(TextOperatorRange { start: 13, end: 29 })
    );
}

#[test]
fn native_pdf_content_stream_finds_tj_array_operator_ranges() {
    assert_eq!(
        find_text_operator_range("BT /F1 11 Tf [(Hello) -80 (World)] TJ ET", "Hello World"),
        Some(TextOperatorRange { start: 13, end: 37 })
    );
    assert_eq!(
        find_text_operator_range("BT /F1 11 Tf [(Other)] TJ ET", "Hello"),
        None
    );
    assert_eq!(
        find_text_operator_range(
            "BT /F1 11 Tf [<48656C6C6F> -80 <576F726C64>] TJ ET",
            "Hello World"
        ),
        Some(TextOperatorRange { start: 13, end: 47 })
    );
}

#[test]
fn native_pdf_content_stream_finds_hex_tj_operator_ranges() {
    assert_eq!(
        find_text_operator_range("BT /F1 11 Tf <48656C6C6F> Tj ET", "Hello"),
        Some(TextOperatorRange { start: 13, end: 28 })
    );
    assert_eq!(
        find_text_operator_range("BT /F1 11 Tf <48 65 6C 6C 6F> Tj ET", "Hello"),
        Some(TextOperatorRange { start: 13, end: 32 })
    );
}

#[test]
fn native_pdf_content_stream_patches_literal_tokens_without_truncating() {
    assert_eq!(
        try_patch_pdf_literal_token("BT (short) Tj ET", "short", "go").as_deref(),
        Some("BT (go   ) Tj ET")
    );
    assert_eq!(
        try_patch_pdf_literal_token("BT (a\\(b\\)) Tj ET", "a(b)", "x").as_deref(),
        Some("BT (x   ) Tj ET")
    );
    assert!(
        try_patch_pdf_literal_token("BT (short) Tj ET", "short", "this translation is longer")
            .is_none()
    );
}

#[test]
fn native_pdf_content_stream_patches_multi_segment_tj_arrays_to_literal_tj() {
    let content = "BT /F1 11 Tf [(Hello) -80 (World)] TJ ET";
    let patched = try_patch_pdf_array_text_token(content, "Hello World", "Bonjour World").unwrap();

    assert_eq!(patched, "BT /F1 11 Tf (Bonjour World) Tj ET");
    assert_eq!(
        try_patch_pdf_literal_token(content, "Hello World", "Bonjour World").as_deref(),
        Some("BT /F1 11 Tf (Bonjour World) Tj ET")
    );
}

#[test]
fn native_pdf_content_stream_hides_matching_text_operator_without_erasing_neighbors() {
    let hidden = hide_text_operator_in_stream(
        "q 0 0 m BT /F1 11 Tf [(Hello) -80 (World)] TJ ET Q",
        "Hello World",
    )
    .unwrap();

    assert_eq!(
        hidden,
        "q 0 0 m BT /F1 11 Tf 3 Tr [(Hello) -80 (World)] TJ 0 Tr ET Q"
    );
    assert!(hide_text_operator_in_stream("BT ET", "Hello").is_none());
    assert!(hide_text_operator_in_stream("BT (Hello) Tj ET", " ").is_none());
}

#[test]
fn native_pdf_content_stream_rewrites_text_operator_without_length_limit() {
    assert_eq!(
        replace_text_operator_in_stream("BT /F1 11 Tf (Hi) Tj ET", "Hi", "Longer text").as_deref(),
        Some("BT /F1 11 Tf (Longer text) Tj ET")
    );

    assert_eq!(
        replace_text_operator_in_stream(
            "BT /F1 11 Tf [(Hello) -80 (World)] TJ ET",
            "Hello World",
            "Bonjour World"
        )
        .as_deref(),
        Some("BT /F1 11 Tf (Bonjour World) Tj ET")
    );

    assert_eq!(
        replace_text_operator_in_stream("BT /F1 11 Tf <48656C6C6F> Tj ET", "Hello", "Translated")
            .as_deref(),
        Some("BT /F1 11 Tf (Translated) Tj ET")
    );
}

#[test]
fn native_pdf_content_stream_rewrites_matching_operator_without_touching_raw_bytes() {
    let content = b"\xFF q BT /F1 11 Tf <48656C6C6F> Tj ET Q";
    let patched =
        replace_text_operator_in_stream_bytes(content, "Hello", "Translated").expect("patch");

    assert_eq!(patched[0], 0xFF);
    assert_eq!(
        std::str::from_utf8(&patched[1..]).unwrap(),
        " q BT /F1 11 Tf (Translated) Tj ET Q"
    );
    assert_eq!(
        find_text_operator_range_bytes(content, "Hello"),
        Some(TextOperatorRange { start: 17, end: 32 })
    );
}
