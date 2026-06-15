use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

const LINE_ENDING: &str = "\r\n";

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum LongDocumentExportBlockType {
    Unknown,
    Paragraph,
    Heading,
    Caption,
    TableCell,
    Formula,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LongDocumentExportChunkMetadata {
    pub chunk_index: usize,
    pub page_number: i32,
    pub source_block_type: LongDocumentExportBlockType,
    pub order_in_page: i32,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LongDocumentExportCheckpoint {
    pub source_chunks: Vec<String>,
    pub chunk_metadata: Vec<LongDocumentExportChunkMetadata>,
    pub translated_chunks: BTreeMap<usize, String>,
    pub failed_chunk_indexes: BTreeSet<usize>,
}

pub fn compose_monolingual_text(checkpoint: &LongDocumentExportCheckpoint) -> String {
    let metadata_by_chunk = metadata_by_chunk_index(checkpoint);
    let mut output = String::new();

    for chunk_index in ordered_chunk_indexes(checkpoint, &metadata_by_chunk) {
        if let Some(translated) = translated_non_blank(checkpoint, chunk_index) {
            push_line(&mut output, translated);
            push_line(&mut output, "");
        } else if checkpoint.failed_chunk_indexes.contains(&chunk_index) {
            push_line(
                &mut output,
                &format!("[Chunk {} translation failed.]", chunk_index + 1),
            );
            push_line(&mut output, "");
        }
    }

    trim_end_like_dotnet(output)
}

pub fn compose_bilingual_text(checkpoint: &LongDocumentExportCheckpoint) -> String {
    let metadata_by_chunk = metadata_by_chunk_index(checkpoint);
    let mut output = String::new();

    for chunk_index in ordered_chunk_indexes(checkpoint, &metadata_by_chunk) {
        push_line(&mut output, &checkpoint.source_chunks[chunk_index]);
        push_line(&mut output, "");

        if let Some(translated) = translated_non_blank(checkpoint, chunk_index) {
            push_line(&mut output, translated);
        } else if checkpoint.failed_chunk_indexes.contains(&chunk_index) {
            push_line(
                &mut output,
                &format!("[Chunk {} translation failed.]", chunk_index + 1),
            );
        }

        push_line(&mut output, "");
        push_line(&mut output, "---");
        push_line(&mut output, "");
    }

    trim_end_like_dotnet(output)
}

pub fn compose_monolingual_markdown(checkpoint: &LongDocumentExportCheckpoint) -> String {
    let metadata_by_chunk = metadata_by_chunk_index(checkpoint);
    let is_multi_page = is_multi_page(checkpoint);
    let mut current_page = None;
    let mut output = String::new();

    for chunk_index in ordered_chunk_indexes(checkpoint, &metadata_by_chunk) {
        let metadata = metadata_by_chunk[&chunk_index];
        if is_multi_page && current_page != Some(metadata.page_number) {
            if current_page.is_some() {
                push_line(&mut output, "");
            }
            push_line(&mut output, &format!("## Page {}", metadata.page_number));
            push_line(&mut output, "");
            current_page = Some(metadata.page_number);
        }

        if let Some(translated) = translated_non_blank(checkpoint, chunk_index) {
            push_markdown_translated_line(&mut output, metadata.source_block_type, translated);
            push_line(&mut output, "");
        } else if checkpoint.failed_chunk_indexes.contains(&chunk_index) {
            push_line(
                &mut output,
                &format!("> *[Chunk {} translation failed.]*", chunk_index + 1),
            );
            push_line(&mut output, "");
        }
    }

    trim_end_like_dotnet(output)
}

pub fn compose_bilingual_markdown(checkpoint: &LongDocumentExportCheckpoint) -> String {
    let metadata_by_chunk = metadata_by_chunk_index(checkpoint);
    let is_multi_page = is_multi_page(checkpoint);
    let mut current_page = None;
    let mut output = String::new();

    for chunk_index in ordered_chunk_indexes(checkpoint, &metadata_by_chunk) {
        let metadata = metadata_by_chunk[&chunk_index];
        if is_multi_page && current_page != Some(metadata.page_number) {
            if current_page.is_some() {
                push_line(&mut output, "");
            }
            push_line(&mut output, &format!("## Page {}", metadata.page_number));
            push_line(&mut output, "");
            current_page = Some(metadata.page_number);
        }

        for line in normalized_lines(&checkpoint.source_chunks[chunk_index]) {
            push_line(&mut output, &format!("> {line}"));
        }
        push_line(&mut output, "");

        if let Some(translated) = translated_non_blank(checkpoint, chunk_index) {
            push_markdown_translated_line(&mut output, metadata.source_block_type, translated);
        } else if checkpoint.failed_chunk_indexes.contains(&chunk_index) {
            push_line(
                &mut output,
                &format!("> *[Chunk {} translation failed.]*", chunk_index + 1),
            );
        }

        push_line(&mut output, "");
        push_line(&mut output, "---");
        push_line(&mut output, "");
    }

    trim_end_like_dotnet(output)
}

pub fn build_bilingual_output_path(monolingual_path: impl AsRef<Path>) -> PathBuf {
    let path = monolingual_path.as_ref();
    let dir = path.parent().unwrap_or_else(|| Path::new("."));
    let stem = path
        .file_stem()
        .map(|value| value.to_string_lossy())
        .unwrap_or_default();
    let extension = path
        .extension()
        .map(|value| format!(".{}", value.to_string_lossy()))
        .unwrap_or_default();

    dir.join(format!("{stem}-bilingual{extension}"))
}

fn metadata_by_chunk_index(
    checkpoint: &LongDocumentExportCheckpoint,
) -> BTreeMap<usize, &LongDocumentExportChunkMetadata> {
    checkpoint
        .chunk_metadata
        .iter()
        .map(|metadata| (metadata.chunk_index, metadata))
        .collect()
}

fn ordered_chunk_indexes(
    checkpoint: &LongDocumentExportCheckpoint,
    metadata_by_chunk: &BTreeMap<usize, &LongDocumentExportChunkMetadata>,
) -> Vec<usize> {
    let mut indexes: Vec<usize> = (0..checkpoint.source_chunks.len()).collect();
    indexes.sort_by(|left, right| {
        let left_metadata = metadata_by_chunk
            .get(left)
            .expect("checkpoint chunk metadata is missing");
        let right_metadata = metadata_by_chunk
            .get(right)
            .expect("checkpoint chunk metadata is missing");

        left_metadata
            .page_number
            .cmp(&right_metadata.page_number)
            .then_with(|| {
                left_metadata
                    .order_in_page
                    .cmp(&right_metadata.order_in_page)
            })
            .then_with(|| left.cmp(right))
    });
    indexes
}

fn translated_non_blank(
    checkpoint: &LongDocumentExportCheckpoint,
    chunk_index: usize,
) -> Option<&str> {
    checkpoint
        .translated_chunks
        .get(&chunk_index)
        .map(|value| value.as_str())
        .filter(|value| !value.trim().is_empty())
}

fn is_multi_page(checkpoint: &LongDocumentExportCheckpoint) -> bool {
    checkpoint
        .chunk_metadata
        .iter()
        .map(|metadata| metadata.page_number)
        .collect::<BTreeSet<_>>()
        .len()
        > 1
}

fn push_markdown_translated_line(
    output: &mut String,
    block_type: LongDocumentExportBlockType,
    translated: &str,
) {
    if block_type == LongDocumentExportBlockType::Heading && !translated.starts_with('#') {
        push_line(output, &format!("### {translated}"));
    } else {
        push_line(output, translated);
    }
}

fn push_line(output: &mut String, value: &str) {
    for line in normalized_lines(value) {
        output.push_str(&line);
        output.push_str(LINE_ENDING);
    }
}

fn normalized_lines(value: &str) -> Vec<String> {
    value
        .replace("\r\n", "\n")
        .replace('\r', "\n")
        .split('\n')
        .map(str::to_string)
        .collect()
}

fn trim_end_like_dotnet(mut output: String) -> String {
    while output.chars().last().is_some_and(char::is_whitespace) {
        output.pop();
    }
    output
}
