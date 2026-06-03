use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use easydict_app::pdf_native_export::NativePdfContentStreamExportFailureKind;
use easydict_app::{
    export_pdf_with_content_stream_replacement, PdfExportCheckpoint, PdfExportChunkMetadata,
    PdfExportSourceBlockType,
};

fn metadata(chunk_index: usize, page_number: i32) -> PdfExportChunkMetadata {
    PdfExportChunkMetadata {
        chunk_index,
        page_number,
        source_block_id: format!("pdf-p{page_number}-body-b{}", chunk_index + 1),
        source_block_type: PdfExportSourceBlockType::Paragraph,
        order_in_page: chunk_index as i32,
        reading_order_score: 1.0,
        bounding_box: None,
        text_style: None,
        translation_skipped: false,
        preserve_original_text_in_pdf_export: false,
        retry_count: 0,
        fallback_text: None,
        detected_font_names: None,
    }
}

fn checkpoint(source: &[&str], translations: &[(usize, &str)]) -> PdfExportCheckpoint {
    PdfExportCheckpoint {
        source_chunks: source.iter().map(|value| value.to_string()).collect(),
        chunk_metadata: source
            .iter()
            .enumerate()
            .map(|(index, _)| metadata(index, index as i32 + 1))
            .collect(),
        translated_chunks: translations
            .iter()
            .map(|(index, text)| (*index, text.to_string()))
            .collect::<BTreeMap<_, _>>(),
        failed_chunk_indexes: BTreeSet::new(),
    }
}

#[test]
fn native_pdf_export_replaces_literal_text_and_writes_openable_pdf() {
    let temp_dir = unique_temp_dir("pdf-native-export-literal");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("input.pdf");
    let output_path = temp_dir.join("output.pdf");
    fs::write(&input_path, minimal_pdf_with_pages(&["Hello PDF"])).expect("input pdf");

    let summary = export_pdf_with_content_stream_replacement(
        &input_path,
        &output_path,
        &checkpoint(&["Hello PDF"], &[(0, "Translated PDF")]),
        None,
    )
    .expect("native PDF export should succeed");

    assert_eq!(summary.blocks_patched, 1);
    assert_eq!(
        lopdf::Document::load(&output_path)
            .unwrap()
            .get_pages()
            .len(),
        1
    );
    let pages = easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
        output_path.to_string_lossy().as_ref(),
    )
    .expect("output text should extract");
    assert!(pages.join("\n").contains("Translated PDF"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_export_retains_selected_pages_after_replacement() {
    let temp_dir = unique_temp_dir("pdf-native-export-page-selection");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("input.pdf");
    let output_path = temp_dir.join("output.pdf");
    fs::write(
        &input_path,
        minimal_pdf_with_pages(&["First page", "Second page"]),
    )
    .expect("input pdf");

    export_pdf_with_content_stream_replacement(
        &input_path,
        &output_path,
        &checkpoint(
            &["First page", "Second page"],
            &[(0, "Translated first"), (1, "Translated second")],
        ),
        Some(&[2]),
    )
    .expect("native PDF export should succeed");

    assert_eq!(
        lopdf::Document::load(&output_path)
            .unwrap()
            .get_pages()
            .len(),
        1
    );
    let pages = easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
        output_path.to_string_lossy().as_ref(),
    )
    .expect("output text should extract");
    let text = pages.join("\n");
    assert!(text.contains("Translated second"));
    assert!(!text.contains("Translated first"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_export_preserves_page_when_some_text_operators_do_not_match() {
    let temp_dir = unique_temp_dir("pdf-native-export-page-preserve");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("input.pdf");
    let output_path = temp_dir.join("output.pdf");
    fs::write(
        &input_path,
        minimal_pdf_with_pages(&["First page", "Second page"]),
    )
    .expect("input pdf");

    let summary = export_pdf_with_content_stream_replacement(
        &input_path,
        &output_path,
        &checkpoint(
            &["First page", "Missing second source"],
            &[(0, "Translated first"), (1, "Translated second")],
        ),
        None,
    )
    .expect("native PDF export should preserve the unmatched page");

    assert_eq!(summary.blocks_patched, 1);
    assert_eq!(summary.pages_preserved_due_to_patch_failure, 1);
    let pages = easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
        output_path.to_string_lossy().as_ref(),
    )
    .expect("output text should extract");
    let text = pages.join("\n");
    assert!(text.contains("Translated first"));
    assert!(text.contains("Second page"));
    assert!(!text.contains("Translated second"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_export_replaces_hex_text_operator_and_writes_openable_pdf() {
    let temp_dir = unique_temp_dir("pdf-native-export-hex");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("input.pdf");
    let output_path = temp_dir.join("output.pdf");
    let stream = format!("BT /F1 24 Tf 100 700 Td <{}> Tj ET", ascii_hex("Hello PDF"));
    fs::write(
        &input_path,
        minimal_pdf_with_page_streams(&[stream.as_str()]),
    )
    .expect("input pdf");

    let summary = export_pdf_with_content_stream_replacement(
        &input_path,
        &output_path,
        &checkpoint(&["Hello PDF"], &[(0, "Translated PDF")]),
        None,
    )
    .expect("native PDF hex export should succeed");

    assert_eq!(summary.blocks_patched, 1);
    assert_eq!(
        lopdf::Document::load(&output_path)
            .unwrap()
            .get_pages()
            .len(),
        1
    );
    let pages = easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
        output_path.to_string_lossy().as_ref(),
    )
    .expect("output text should extract");
    assert!(pages.join("\n").contains("Translated PDF"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_export_patches_binary_content_stream_without_utf8_lossy_conversion() {
    let temp_dir = unique_temp_dir("pdf-native-export-binary-stream");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("input.pdf");
    let output_path = temp_dir.join("output.pdf");
    let stream = b"\xFF q BT /F1 24 Tf 100 700 Td (Hello PDF) Tj ET Q".to_vec();
    fs::write(&input_path, minimal_pdf_with_page_stream_bytes(&[stream])).expect("input pdf");

    let summary = export_pdf_with_content_stream_replacement(
        &input_path,
        &output_path,
        &checkpoint(&["Hello PDF"], &[(0, "Translated PDF")]),
        None,
    )
    .expect("native PDF export should patch byte streams");

    assert_eq!(summary.blocks_patched, 1);
    let output_doc = lopdf::Document::load(&output_path).expect("output PDF should open");
    let page_id = output_doc.get_pages()[&1];
    let content = output_doc
        .get_page_content(page_id)
        .expect("page content should read");
    assert_eq!(content[0], 0xFF);
    assert!(contains_subslice(&content, b"(Translated PDF) Tj"));
    assert!(!contains_subslice(&content, "�".as_bytes()));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_export_fails_when_no_text_operator_can_be_patched() {
    let temp_dir = unique_temp_dir("pdf-native-export-no-match");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("input.pdf");
    let output_path = temp_dir.join("output.pdf");
    fs::write(&input_path, minimal_pdf_with_pages(&["Hello PDF"])).expect("input pdf");

    let error = export_pdf_with_content_stream_replacement(
        &input_path,
        &output_path,
        &checkpoint(&["Missing source"], &[(0, "Translated PDF")]),
        None,
    )
    .expect_err("all-unmatched PDF export should fall back to worker");

    assert!(error
        .message
        .contains("Could not patch any PDF text operators"));
    assert!(!output_path.exists());

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_export_rejects_non_ascii_text_until_font_embedding_exists() {
    let temp_dir = unique_temp_dir("pdf-native-export-non-ascii");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("input.pdf");
    let output_path = temp_dir.join("output.pdf");
    fs::write(&input_path, minimal_pdf_with_pages(&["Hello PDF"])).expect("input pdf");

    let error = export_pdf_with_content_stream_replacement(
        &input_path,
        &output_path,
        &checkpoint(&["Hello PDF"], &[(0, "你好")]),
        None,
    )
    .expect_err("non-ASCII PDF replacement needs font embedding");

    assert_eq!(
        error.kind,
        NativePdfContentStreamExportFailureKind::NeedsFontEmbedding
    );
    assert!(error.message.contains("font embedding"));
    assert!(!output_path.exists());

    fs::remove_dir_all(&temp_dir).ok();
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!("{prefix}-{stamp}"))
}

fn minimal_pdf_with_pages(page_texts: &[&str]) -> Vec<u8> {
    let streams = page_texts
        .iter()
        .map(|text| {
            let escaped = text
                .replace('\\', r"\\")
                .replace('(', r"\(")
                .replace(')', r"\)");
            format!("BT /F1 24 Tf 100 700 Td ({escaped}) Tj ET")
        })
        .collect::<Vec<_>>();
    let stream_refs = streams.iter().map(String::as_str).collect::<Vec<_>>();
    minimal_pdf_with_page_streams(&stream_refs)
}

fn minimal_pdf_with_page_streams(page_streams: &[&str]) -> Vec<u8> {
    let streams = page_streams
        .iter()
        .map(|stream| stream.as_bytes().to_vec())
        .collect::<Vec<_>>();
    minimal_pdf_with_page_stream_bytes(&streams)
}

fn minimal_pdf_with_page_stream_bytes(page_streams: &[Vec<u8>]) -> Vec<u8> {
    let mut objects = Vec::new();
    let page_object_numbers = (0..page_streams.len())
        .map(|index| 4 + index * 2)
        .collect::<Vec<_>>();
    let kids = page_object_numbers
        .iter()
        .map(|object_number| format!("{object_number} 0 R"))
        .collect::<Vec<_>>()
        .join(" ");

    objects.push(b"<< /Type /Catalog /Pages 2 0 R >>".to_vec());
    objects.push(
        format!(
            "<< /Type /Pages /Kids [{kids}] /Count {} >>",
            page_streams.len()
        )
        .into_bytes(),
    );
    objects.push(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".to_vec());

    for (index, stream) in page_streams.iter().enumerate() {
        let page_object_number = 4 + index * 2;
        let content_object_number = page_object_number + 1;

        objects.push(format!(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {content_object_number} 0 R >>"
        ).into_bytes());

        let mut stream_object = format!("<< /Length {} >>\nstream\n", stream.len()).into_bytes();
        stream_object.extend_from_slice(stream);
        stream_object.extend_from_slice(b"\nendstream");
        objects.push(stream_object);
    }

    let mut pdf = b"%PDF-1.4\n".to_vec();
    let mut offsets = Vec::with_capacity(objects.len());
    for (index, object) in objects.iter().enumerate() {
        offsets.push(pdf.len());
        pdf.extend_from_slice(format!("{} 0 obj\n", index + 1).as_bytes());
        pdf.extend_from_slice(object);
        pdf.extend_from_slice(b"\nendobj\n");
    }

    let xref_start = pdf.len();
    pdf.extend_from_slice(format!("xref\n0 {}\n", objects.len() + 1).as_bytes());
    pdf.extend_from_slice(b"0000000000 65535 f \n");
    for offset in offsets {
        pdf.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
    }
    pdf.extend_from_slice(
        format!(
            "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{}\n%%EOF\n",
            objects.len() + 1,
            xref_start
        )
        .as_bytes(),
    );
    pdf
}

fn ascii_hex(value: &str) -> String {
    value
        .as_bytes()
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<String>()
}

fn contains_subslice(haystack: &[u8], needle: &[u8]) -> bool {
    !needle.is_empty()
        && haystack
            .windows(needle.len())
            .any(|window| window == needle)
}
