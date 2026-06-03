use base64::{engine::general_purpose, Engine as _};
use easydict_app::lex_index::{normalize_key, LexIndex};

const DOTNET_BUILT_LEX_INDEX_BASE64: &str = concat!(
    "TFhEWAEAAAABAAAAHQAAABwAAAAGAAAABgAAAAYAAAAGAAAAKwAAAAAAAAACAAAA/////wIA",
    "AAABAAAA/////wMAAAABAAAA/////wQAAAABAAAA/////wUAAAABAAAA/////wYAAAABAAAA",
    "/////wcAAAACAAAA/////wkAAAADAAAA/////wwAAAABAAAA/////w0AAAACAAAA/////w8A",
    "AAAAAAAAAAAAAA8AAAABAAAA/////xAAAAAAAAAAAgAAABAAAAABAAAA/////xEAAAABAAAA",
    "/////xIAAAABAAAA/////xMAAAABAAAA/////xQAAAABAAAA/////xUAAAABAAAA/////xYA",
    "AAABAAAA/////xcAAAABAAAA/////xgAAAABAAAA/////xkAAAAAAAAABAAAABkAAAAAAAAA",
    "BQAAABkAAAABAAAA/////xoAAAAAAAAAAwAAABoAAAABAAAA/////xsAAAABAAAA/////xwA",
    "AAAAAAAAAQAAAGEAAAABAAAAdAAAAAIAAABwAAAAAwAAAGUAAAAEAAAAcAAAAAUAAABhAAAA",
    "BgAAAGwAAAAHAAAAbAAAAAgAAAB0AAAACQAAAGUAAAAKAAAAaQAAAAsAAAB5AAAADAAAAGkA",
    "AAANAAAAaQAAAA4AAAByAAAADwAAAGMAAAAQAAAAZwAAABEAAABtAAAAEgAAAGEAAAATAAAA",
    "YQAAABQAAABoAAAAFQAAAGUAAAAWAAAAeQAAABcAAAB0AAAAGAAAAHQAAAAZAAAAaQAAABoA",
    "AABvAAAAGwAAAG4AAAAcAAAAAAAAAAEAAAABAAAAAQAAAAIAAAABAAAAAwAAAAEAAAAEAAAA",
    "AQAAAAUAAAABAAAAAAAAAAEAAAACAAAAAwAAAAQAAAAFAAAAAAAAAAUAAAAQAAAAFQAAAB0A",
    "AAAkAAAAKwAAAGFwcGxlYXBwbGljYXRpb25hcHBseXRlYWxpZ2h0dGVhdGltZXRlYXRyYXk=",
);

#[test]
fn native_lex_index_round_trips_prefix_and_wildcard_queries() {
    let bytes = LexIndex::build_bytes([
        "apple",
        "application",
        "apply",
        "tealight",
        "teatime",
        "teatray",
    ]);
    let index = LexIndex::open_bytes(&bytes).expect("LexIndex bytes should open");

    assert_eq!(index.complete("app", 10), ["apple", "application", "apply"]);
    assert_eq!(index.match_pattern("tea*t", 10), ["tealight"]);
}

#[test]
fn native_lex_index_opens_dotnet_built_lxdx_fixture() {
    let bytes = general_purpose::STANDARD
        .decode(DOTNET_BUILT_LEX_INDEX_BASE64)
        .expect("fixture should decode");
    let index = LexIndex::open_bytes(&bytes).expect(".NET-built LexIndex bytes should open");

    assert_eq!(index.metadata().state_count, 29);
    assert_eq!(index.metadata().edge_count, 28);
    assert_eq!(index.metadata().entry_count, 6);
    assert_eq!(index.complete("app", 10), ["apple", "application", "apply"]);
    assert_eq!(index.match_pattern("tea*t", 10), ["tealight"]);
}

#[test]
fn native_lex_index_preserves_original_variants_for_same_normalized_key() {
    let index = LexIndex::from_keys(["Apple", "apple", "Ａｐｐｌｅ"]);

    assert_eq!(
        index.complete("apple", 10),
        ["Apple", "apple", "Ａｐｐｌｅ"]
    );
    assert_eq!(index.metadata().entry_count, 1);
    assert_eq!(index.metadata().value_ref_count, 3);
}

#[test]
fn native_lex_index_uses_nfkc_lowercase_normalization() {
    let index = LexIndex::from_keys(["café", "CAFÉ", "𝓐pple", "Alpha beta"]);

    assert_eq!(normalize_key("  CAFÉ  "), "café");
    assert_eq!(normalize_key("𝓐pple"), "apple");
    assert_eq!(index.complete("café", 10), ["CAFÉ", "café"]);
    assert_eq!(index.complete("apple", 10), ["𝓐pple"]);
    assert_eq!(index.match_pattern("alpha?beta", 10), ["Alpha beta"]);
}

#[test]
fn native_lex_index_empty_and_whitespace_keys_are_ignored() {
    let index = LexIndex::from_keys(["", " ", "\t", "apple"]);

    assert_eq!(index.metadata().entry_count, 1);
    assert_eq!(index.complete("a", 10), ["apple"]);
}

#[test]
fn native_lex_index_empty_input_builds_readable_empty_index() {
    let bytes = LexIndex::build_bytes(["", " ", "\t"]);
    let index = LexIndex::open_bytes(&bytes).expect("empty LexIndex should open");

    assert_eq!(index.metadata().entry_count, 0);
    assert!(index.complete("a", 10).is_empty());
    assert!(index.match_pattern("*", 10).is_empty());
}

#[test]
fn native_lex_index_question_and_star_wildcards_respect_limit() {
    let index = LexIndex::from_keys(["cat", "cot", "coat", "cut"]);

    assert_eq!(index.match_pattern("c?t", 2), ["cat", "cot"]);
    assert_eq!(
        index.match_pattern("c*t", 10),
        ["cat", "coat", "cot", "cut"]
    );
}

#[test]
fn native_lex_index_invalid_header_fails_locally() {
    let error = LexIndex::open_bytes(&[1, 2, 3, 4]).expect_err("invalid header should fail");

    assert!(error.to_string().contains("header"));
}

#[test]
fn native_lex_index_unsupported_version_fails_locally() {
    let mut bytes = LexIndex::build_bytes(["apple"]);
    bytes[4..8].copy_from_slice(&99i32.to_le_bytes());

    let error = LexIndex::open_bytes(&bytes).expect_err("unsupported version should fail");

    assert!(error.to_string().contains("version"));
}

#[test]
fn native_lex_index_invalid_edge_table_fails_locally() {
    let mut bytes = LexIndex::build_bytes(["apple"]);
    const HEADER_SIZE: usize = 4 + (4 * 9);
    bytes[HEADER_SIZE..HEADER_SIZE + 4].copy_from_slice(&1024i32.to_le_bytes());

    let error = LexIndex::open_bytes(&bytes).expect_err("invalid edge table should fail");

    assert!(error.to_string().contains("edge bounds"));
}
