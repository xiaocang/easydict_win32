use easydict_app::{
    build_bilingual_output_path, compose_bilingual_markdown, compose_bilingual_text,
    compose_monolingual_markdown, compose_monolingual_text, LongDocumentExportBlockType,
    LongDocumentExportCheckpoint, LongDocumentExportChunkMetadata,
};
use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;

#[test]
fn native_long_document_export_composes_monolingual_text_in_page_order() {
    let checkpoint = sample_checkpoint();

    let content = compose_monolingual_text(&checkpoint);

    assert_eq!(
        content,
        "[Chunk 1 translation failed.]\r\n\r\n这是第一页第二块。\r\n\r\n这是第二页第一块。"
    );
    assert!(!content.contains("Hello page one first."));
}

#[test]
fn native_long_document_export_composes_bilingual_text_with_separators() {
    let checkpoint = sample_checkpoint();

    let content = compose_bilingual_text(&checkpoint);

    assert_eq!(
        content,
        concat!(
            "Hello page one first.\r\n\r\n[Chunk 1 translation failed.]\r\n\r\n---\r\n\r\n",
            "Hello page one second.\r\n\r\n这是第一页第二块。\r\n\r\n---\r\n\r\n",
            "Second page heading\r\n\r\n这是第二页第一块。\r\n\r\n---"
        )
    );
}

#[test]
fn native_long_document_export_failed_fallback_chunks_show_marker_not_source() {
    let checkpoint = failed_checkpoint();

    assert_eq!(
        compose_monolingual_text(&checkpoint),
        "[Chunk 1 translation failed.]"
    );
    assert!(!compose_monolingual_text(&checkpoint).contains("Fallback source block."));
    assert_eq!(
        compose_monolingual_markdown(&checkpoint),
        "> *[Chunk 1 translation failed.]*"
    );
    assert!(!compose_monolingual_markdown(&checkpoint).contains("Fallback source block."));
}

#[test]
fn native_long_document_export_markdown_adds_page_headers_and_heading_prefix() {
    let checkpoint = sample_checkpoint();

    let content = compose_monolingual_markdown(&checkpoint);

    assert_eq!(
        content,
        concat!(
            "## Page 1\r\n\r\n",
            "> *[Chunk 1 translation failed.]*\r\n\r\n",
            "这是第一页第二块。\r\n\r\n\r\n",
            "## Page 2\r\n\r\n",
            "### 这是第二页第一块。"
        )
    );
}

#[test]
fn native_long_document_export_bilingual_markdown_uses_blockquotes_and_preserves_heading_hash() {
    let mut checkpoint = sample_checkpoint();
    checkpoint
        .translated_chunks
        .insert(2, "## 已有标题".to_string());

    let content = compose_bilingual_markdown(&checkpoint);

    assert!(content.contains("> Hello page one first."));
    assert!(content.contains("> *[Chunk 1 translation failed.]*"));
    assert!(content.contains("这是第一页第二块。"));
    assert!(content.contains("## 已有标题"));
    assert!(!content.contains("### ## 已有标题"));
    assert!(content.contains("\r\n---\r\n"));
}

#[test]
fn native_long_document_export_bilingual_markdown_blockquotes_each_source_line() {
    let checkpoint = LongDocumentExportCheckpoint {
        source_chunks: vec!["line one\nline two".to_string()],
        chunk_metadata: vec![metadata(0, 1, 0, LongDocumentExportBlockType::Paragraph)],
        translated_chunks: BTreeMap::from([(0, "译文".to_string())]),
        failed_chunk_indexes: BTreeSet::new(),
    };

    let content = compose_bilingual_markdown(&checkpoint);

    assert!(content.starts_with("> line one\r\n> line two\r\n\r\n译文"));
}

#[test]
fn native_long_document_export_normalizes_embedded_line_endings_to_crlf() {
    let checkpoint = LongDocumentExportCheckpoint {
        source_chunks: vec!["source one\r\nsource two".to_string()],
        chunk_metadata: vec![metadata(0, 1, 0, LongDocumentExportBlockType::Paragraph)],
        translated_chunks: BTreeMap::from([(
            0,
            "translated one\ntranslated two\rthird".to_string(),
        )]),
        failed_chunk_indexes: BTreeSet::new(),
    };

    let monolingual = compose_monolingual_text(&checkpoint);
    let bilingual = compose_bilingual_markdown(&checkpoint);

    assert_eq!(monolingual, "translated one\r\ntranslated two\r\nthird");
    assert!(bilingual.starts_with(
        "> source one\r\n> source two\r\n\r\ntranslated one\r\ntranslated two\r\nthird"
    ));
    let monolingual_without_crlf = monolingual.replace("\r\n", "");
    let bilingual_without_crlf = bilingual.replace("\r\n", "");
    assert!(!monolingual_without_crlf.contains('\r'));
    assert!(!monolingual_without_crlf.contains('\n'));
    assert!(!bilingual_without_crlf.contains('\r'));
    assert!(!bilingual_without_crlf.contains('\n'));
}

#[test]
fn native_long_document_export_builds_bilingual_output_path() {
    assert!(build_bilingual_output_path(Path::new("/tmp/doc.txt"))
        .to_string_lossy()
        .ends_with("doc-bilingual.txt"));
    assert!(build_bilingual_output_path(Path::new("/tmp/my file.md"))
        .to_string_lossy()
        .ends_with("my file-bilingual.md"));
}

fn sample_checkpoint() -> LongDocumentExportCheckpoint {
    LongDocumentExportCheckpoint {
        source_chunks: vec![
            "Hello page one first.".to_string(),
            "Hello page one second.".to_string(),
            "Second page heading".to_string(),
        ],
        chunk_metadata: vec![
            metadata(0, 1, 0, LongDocumentExportBlockType::Paragraph),
            metadata(1, 1, 1, LongDocumentExportBlockType::Paragraph),
            metadata(2, 2, 0, LongDocumentExportBlockType::Heading),
        ],
        translated_chunks: BTreeMap::from([
            (0, "  ".to_string()),
            (1, "这是第一页第二块。".to_string()),
            (2, "这是第二页第一块。".to_string()),
        ]),
        failed_chunk_indexes: BTreeSet::from([0]),
    }
}

fn failed_checkpoint() -> LongDocumentExportCheckpoint {
    LongDocumentExportCheckpoint {
        source_chunks: vec!["Fa llback source block.".to_string()],
        chunk_metadata: vec![metadata(0, 1, 0, LongDocumentExportBlockType::Paragraph)],
        translated_chunks: BTreeMap::new(),
        failed_chunk_indexes: BTreeSet::from([0]),
    }
}

fn metadata(
    chunk_index: usize,
    page_number: i32,
    order_in_page: i32,
    source_block_type: LongDocumentExportBlockType,
) -> LongDocumentExportChunkMetadata {
    LongDocumentExportChunkMetadata {
        chunk_index,
        page_number,
        source_block_type,
        order_in_page,
    }
}
