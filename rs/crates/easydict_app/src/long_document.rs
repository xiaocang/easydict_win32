use crate::compat_client::{CompatClientError, CompatHostFacade};
use crate::compat_protocol::{
    worker_events, BlockTranslatedEventData, ConfigureParams, IpcEvent, ProgressEventData,
    SettingsSnapshot, StatusEventData, TranslateDocumentParams, TranslateDocumentResult,
};
use crate::state::{EasydictUiState, TranslationResultPreview};
use serde_json::Value;
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use win_fluent::prelude::ResultStatus;

const NO_FILE_SELECTED: &str = "No file selected";
const MAX_HISTORY_ITEMS: usize = 50;

pub trait LongDocumentBackend {
    fn configure_longdoc_settings(
        &mut self,
        settings: &SettingsSnapshot,
    ) -> Result<(), LongDocumentBackendError> {
        let _ = settings;
        Ok(())
    }

    fn longdoc_translate(
        &mut self,
        params: &TranslateDocumentParams,
    ) -> Result<TranslateDocumentResult, LongDocumentBackendError>;

    fn take_longdoc_events(&mut self) -> Vec<LongDocumentEvent> {
        Vec::new()
    }
}

impl LongDocumentBackend for CompatHostFacade {
    fn configure_longdoc_settings(
        &mut self,
        settings: &SettingsSnapshot,
    ) -> Result<(), LongDocumentBackendError> {
        let result = self
            .configure(&ConfigureParams {
                settings: settings.clone(),
            })
            .map_err(LongDocumentBackendError::from)?;
        if result.ok {
            Ok(())
        } else {
            Err(LongDocumentBackendError::new(
                "CompatHost rejected long document settings",
            ))
        }
    }

    fn longdoc_translate(
        &mut self,
        params: &TranslateDocumentParams,
    ) -> Result<TranslateDocumentResult, LongDocumentBackendError> {
        CompatHostFacade::longdoc_translate(self, params).map_err(LongDocumentBackendError::from)
    }

    fn take_longdoc_events(&mut self) -> Vec<LongDocumentEvent> {
        long_document_events_from_ipc(self.take_events())
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct LongDocumentServiceRequest {
    pub query_id: u64,
    pub input: LongDocumentInput,
    pub params: TranslateDocumentParams,
    pub settings: SettingsSnapshot,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum LongDocumentInput {
    File(String),
    InlineText(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum LongDocumentStartError {
    MissingInput,
}

impl fmt::Display for LongDocumentStartError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingInput => {
                formatter.write_str("Select a document or enter text to translate.")
            }
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LongDocumentBackendError {
    pub message: String,
}

impl LongDocumentBackendError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl fmt::Display for LongDocumentBackendError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for LongDocumentBackendError {}

impl From<CompatClientError> for LongDocumentBackendError {
    fn from(error: CompatClientError) -> Self {
        Self::new(error.to_string())
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct LongDocumentOutcome {
    pub query_id: u64,
    pub input_label: String,
    pub events: Vec<LongDocumentEvent>,
    pub result: Result<TranslateDocumentResult, LongDocumentBackendError>,
}

#[derive(Clone, Debug, PartialEq)]
pub enum LongDocumentEvent {
    Status(StatusEventData),
    Progress(ProgressEventData),
    BlockTranslated(BlockTranslatedEventData),
}

pub fn begin_long_document_translate(
    state: &mut EasydictUiState,
) -> Result<LongDocumentServiceRequest, LongDocumentStartError> {
    let request = build_long_document_request(state, state.next_query_id)?;
    state.next_query_id += 1;
    mark_long_document_started(state, &request);
    Ok(request)
}

pub fn build_long_document_request(
    state: &EasydictUiState,
    query_id: u64,
) -> Result<LongDocumentServiceRequest, LongDocumentStartError> {
    let long_doc = &state.long_document;
    let selected_file = selected_file_path(&long_doc.selected_file);
    let inline_text = long_doc.source_text.trim();

    let (input, input_mode) = if let Some(path) = selected_file {
        (
            LongDocumentInput::File(path),
            map_input_mode(&long_doc.input_mode).to_string(),
        )
    } else if !inline_text.is_empty() {
        let input_mode = match map_input_mode(&long_doc.input_mode) {
            "Pdf" => "PlainText",
            other => other,
        };
        (
            LongDocumentInput::InlineText(inline_text.to_string()),
            input_mode.to_string(),
        )
    } else {
        return Err(LongDocumentStartError::MissingInput);
    };

    let output_path = build_output_path(long_doc, &input, &input_mode);

    let params = TranslateDocumentParams {
        input_path: match &input {
            LongDocumentInput::File(path) => path.clone(),
            LongDocumentInput::InlineText(_) => String::new(),
        },
        output_path,
        input_mode,
        from: map_document_language(&long_doc.source_language).to_string(),
        to: map_document_language(&long_doc.target_language).to_string(),
        service_id: long_doc.service.clone(),
        output_mode: map_output_mode(&long_doc.output_mode).to_string(),
        pdf_export_mode: Some("ContentStreamReplacement".to_string()),
        layout_detection: Some(state.settings.layout_detection_mode.clone()),
        page_range: non_empty(&long_doc.page_range),
        vision_endpoint: None,
        vision_api_key: None,
        vision_model: None,
        result_json_path: None,
    };

    Ok(LongDocumentServiceRequest {
        query_id,
        input,
        params,
        settings: long_document_settings_snapshot(state),
    })
}

pub fn run_long_document_request_with_current_app_dir(
    request: LongDocumentServiceRequest,
) -> LongDocumentOutcome {
    match current_app_dir() {
        Ok(app_dir) => run_long_document_request_with_packaged_host(request, app_dir),
        Err(message) => long_document_error_outcome(request, message),
    }
}

pub fn run_long_document_request_with_packaged_host(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
) -> LongDocumentOutcome {
    match CompatHostFacade::spawn_packaged(app_dir) {
        Ok(mut backend) => run_long_document_request(&mut backend, request),
        Err(error) => long_document_error_outcome(request, error.to_string()),
    }
}

pub fn run_long_document_request<B: LongDocumentBackend>(
    backend: &mut B,
    request: LongDocumentServiceRequest,
) -> LongDocumentOutcome {
    let input_label = input_label(&request);
    let (result, events) = match materialize_request(&request) {
        Ok(materialized) => {
            let result = backend
                .configure_longdoc_settings(&request.settings)
                .and_then(|()| backend.longdoc_translate(&materialized.params));
            let events = backend.take_longdoc_events();
            if let Some(temp_path) = materialized.temp_path {
                let _ = fs::remove_file(temp_path);
            }
            (result, events)
        }
        Err(error) => (Err(error), Vec::new()),
    };

    LongDocumentOutcome {
        query_id: request.query_id,
        input_label,
        events,
        result,
    }
}

pub fn apply_long_document_outcome(state: &mut EasydictUiState, outcome: LongDocumentOutcome) {
    if state.long_document.active_query_id != Some(outcome.query_id) {
        return;
    }

    state.long_document.active_query_id = None;
    state.long_document.is_translating = false;
    apply_long_document_events(state, &outcome.events);

    match outcome.result {
        Ok(result) => {
            let output_path = display_output_path(&result);
            state.long_document.last_error = None;
            state.long_document.last_output_path = output_path.clone();
            state.long_document.status_text = format!(
                "{} ({}/{})",
                result.state, result.succeeded_chunks, result.total_chunks
            );
            if result.total_chunks > 0 {
                state.long_document.progress_percentage =
                    Some((result.succeeded_chunks as f64 / result.total_chunks as f64) * 100.0);
                state.long_document.progress_detail = Some(format!(
                    "{}/{} chunks translated",
                    result.succeeded_chunks, result.total_chunks
                ));
            }
            if let Some(output_path) = &output_path {
                state.long_document.output_folder = output_folder(output_path);
            }

            push_history(
                state,
                TranslationResultPreview::new(
                    format!("longdoc-{}", outcome.query_id),
                    outcome.input_label,
                    long_document_result_body(&result),
                ),
            );
        }
        Err(error) => {
            state.long_document.status_text = format!("Error: {}", error.message);
            state.long_document.last_error = Some(error.message.clone());
            push_history(
                state,
                TranslationResultPreview::new(
                    format!("longdoc-{}", outcome.query_id),
                    outcome.input_label,
                    error.message,
                )
                .status(ResultStatus::Error),
            );
        }
    }
}

pub fn apply_long_document_start_error(state: &mut EasydictUiState, error: LongDocumentStartError) {
    state.long_document.active_query_id = None;
    state.long_document.is_translating = false;
    state.long_document.status_text = error.to_string();
    state.long_document.last_error = Some(error.to_string());
    state.long_document.progress_percentage = None;
    state.long_document.progress_detail = None;
    state.long_document.last_translated_block = None;
}

fn mark_long_document_started(state: &mut EasydictUiState, request: &LongDocumentServiceRequest) {
    state.long_document.active_query_id = Some(request.query_id);
    state.long_document.is_translating = true;
    state.long_document.last_error = None;
    state.long_document.last_output_path = None;
    state.long_document.progress_percentage = None;
    state.long_document.progress_detail = None;
    state.long_document.last_translated_block = None;
    state.long_document.status_text = "Translating document".to_string();
}

fn apply_long_document_events(state: &mut EasydictUiState, events: &[LongDocumentEvent]) {
    for event in events {
        match event {
            LongDocumentEvent::Status(status) => {
                state.long_document.status_text = status.message.clone();
            }
            LongDocumentEvent::Progress(progress) => {
                state.long_document.progress_percentage = Some(progress.percentage);
                state.long_document.progress_detail = Some(progress_detail(progress));
            }
            LongDocumentEvent::BlockTranslated(block) => {
                state.long_document.last_translated_block = Some(block.translated_text.clone());
                if let Some(last_error) = &block.last_error {
                    state.long_document.progress_detail = Some(format!(
                        "Chunk {} retry {}: {}",
                        block.chunk_index, block.retry_count, last_error
                    ));
                }
            }
        }
    }
}

fn materialize_request(
    request: &LongDocumentServiceRequest,
) -> Result<MaterializedRequest, LongDocumentBackendError> {
    match &request.input {
        LongDocumentInput::File(_) => Ok(MaterializedRequest {
            params: request.params.clone(),
            temp_path: None,
        }),
        LongDocumentInput::InlineText(text) => {
            let temp_path = temp_input_path(&request.params.input_mode);
            fs::write(&temp_path, text).map_err(|error| {
                LongDocumentBackendError::new(format!(
                    "Could not prepare inline document input: {error}"
                ))
            })?;
            let mut params = request.params.clone();
            params.input_path = temp_path.to_string_lossy().to_string();
            Ok(MaterializedRequest {
                params,
                temp_path: Some(temp_path),
            })
        }
    }
}

struct MaterializedRequest {
    params: TranslateDocumentParams,
    temp_path: Option<PathBuf>,
}

fn long_document_error_outcome(
    request: LongDocumentServiceRequest,
    message: impl Into<String>,
) -> LongDocumentOutcome {
    LongDocumentOutcome {
        query_id: request.query_id,
        input_label: input_label(&request),
        events: Vec::new(),
        result: Err(LongDocumentBackendError::new(message)),
    }
}

fn long_document_events_from_ipc(events: Vec<IpcEvent<Value>>) -> Vec<LongDocumentEvent> {
    events
        .into_iter()
        .filter_map(long_document_event_from_ipc)
        .collect()
}

fn long_document_event_from_ipc(event: IpcEvent<Value>) -> Option<LongDocumentEvent> {
    let data = event.data?;

    match event.event.as_str() {
        worker_events::LONGDOC_STATUS => serde_json::from_value::<StatusEventData>(data)
            .ok()
            .map(LongDocumentEvent::Status),
        worker_events::LONGDOC_PROGRESS => serde_json::from_value::<ProgressEventData>(data)
            .ok()
            .map(LongDocumentEvent::Progress),
        worker_events::LONGDOC_BLOCK_TRANSLATED => {
            serde_json::from_value::<BlockTranslatedEventData>(data)
                .ok()
                .map(LongDocumentEvent::BlockTranslated)
        }
        _ => None,
    }
}

fn progress_detail(progress: &ProgressEventData) -> String {
    let mut detail = format!(
        "{}: block {}/{}",
        progress.stage, progress.current_block, progress.total_blocks
    );

    if progress.total_pages > 0 {
        detail.push_str(&format!(
            ", page {}/{}",
            progress.current_page, progress.total_pages
        ));
    }

    if let Some(preview) = progress
        .current_block_preview
        .as_deref()
        .map(str::trim)
        .filter(|preview| !preview.is_empty())
    {
        detail.push_str(" - ");
        detail.push_str(preview);
    }

    detail
}

fn selected_file_path(value: &str) -> Option<String> {
    let value = value.trim();
    (!value.is_empty() && value != NO_FILE_SELECTED).then(|| value.to_string())
}

fn non_empty(value: &str) -> Option<String> {
    let value = value.trim();
    (!value.is_empty()).then(|| value.to_string())
}

fn long_document_settings_snapshot(state: &EasydictUiState) -> SettingsSnapshot {
    let mut settings = crate::state::settings_snapshot(&state.settings);
    settings.long_doc_max_concurrency = parse_concurrency(&state.long_document.concurrency);
    settings.long_doc_enable_document_context_pass = Some(state.long_document.two_pass_context);
    settings
}

fn parse_concurrency(value: &str) -> Option<u32> {
    value
        .trim()
        .parse::<u32>()
        .ok()
        .map(|value| value.clamp(1, 16))
}

fn map_input_mode(value: &str) -> &'static str {
    match value.trim().to_ascii_lowercase().as_str() {
        "markdown" | "md" => "Markdown",
        "plaintext" | "plain" | "text" | "txt" => "PlainText",
        _ => "Pdf",
    }
}

fn map_output_mode(value: &str) -> &'static str {
    match value.trim().to_ascii_lowercase().as_str() {
        "bilingual" | "dual" => "Bilingual",
        "both" => "Both",
        _ => "Monolingual",
    }
}

fn map_document_language(value: &str) -> &'static str {
    match value.trim().to_ascii_lowercase().as_str() {
        "" | "auto" => "Auto",
        "ar" | "ar-sa" => "Arabic",
        "da" | "da-dk" => "Danish",
        "de" | "de-de" => "German",
        "en" | "en-us" => "English",
        "es" | "es-es" => "Spanish",
        "fr" | "fr-fr" => "French",
        "hi" | "hi-in" => "Hindi",
        "id" | "id-id" => "Indonesian",
        "it" | "it-it" => "Italian",
        "ja" | "ja-jp" => "Japanese",
        "ko" | "ko-kr" => "Korean",
        "ms" | "ms-my" => "Malay",
        "th" | "th-th" => "Thai",
        "vi" | "vi-vn" => "Vietnamese",
        "zh" | "zh-cn" | "zh-hans" => "ChineseSimplified",
        "zh-tw" | "zh-hant" => "ChineseTraditional",
        _ => "Auto",
    }
}

fn build_output_path(
    long_doc: &crate::state::LongDocumentState,
    input: &LongDocumentInput,
    input_mode: &str,
) -> Option<String> {
    let folder = requested_output_folder(long_doc, input)?;
    let stem = match input {
        LongDocumentInput::File(path) => {
            file_stem(path).unwrap_or_else(|| "translated".to_string())
        }
        LongDocumentInput::InlineText(_) => "inline-document".to_string(),
    };
    let extension = output_extension(input_mode);
    Some(join_output_path(
        &folder,
        &format!("{stem}_translated{extension}"),
    ))
}

fn requested_output_folder(
    long_doc: &crate::state::LongDocumentState,
    input: &LongDocumentInput,
) -> Option<String> {
    let configured = long_doc.output_folder.trim();
    if !configured.is_empty() && !configured.starts_with('(') {
        return Some(configured.to_string());
    }

    match input {
        LongDocumentInput::File(path) => parent_folder(path),
        LongDocumentInput::InlineText(_) => None,
    }
}

fn output_extension(input_mode: &str) -> &'static str {
    match input_mode {
        "Markdown" => ".md",
        "PlainText" => ".txt",
        _ => ".pdf",
    }
}

fn file_stem(path: &str) -> Option<String> {
    let file_name = file_name(path)?;
    let stem = file_name
        .rsplit_once('.')
        .map_or(file_name, |(stem, _)| stem);
    non_empty(stem)
}

fn file_name(path: &str) -> Option<&str> {
    let path = path.trim_end_matches(['\\', '/']).trim();
    if path.is_empty() {
        return None;
    }

    last_separator_index(path)
        .map(|index| &path[index + 1..])
        .filter(|name| !name.is_empty())
        .or(Some(path))
}

fn parent_folder(path: &str) -> Option<String> {
    let path = path.trim_end_matches(['\\', '/']).trim();
    let index = last_separator_index(path)?;
    let parent = &path[..index];
    non_empty(parent)
}

fn last_separator_index(path: &str) -> Option<usize> {
    match (path.rfind('\\'), path.rfind('/')) {
        (Some(backslash), Some(slash)) => Some(backslash.max(slash)),
        (Some(backslash), None) => Some(backslash),
        (None, Some(slash)) => Some(slash),
        (None, None) => None,
    }
}

fn join_output_path(folder: &str, file_name: &str) -> String {
    let folder = folder.trim_end_matches(['\\', '/']);
    if folder.contains('\\') && !folder.contains('/') {
        format!("{folder}\\{file_name}")
    } else {
        Path::new(folder).join(file_name).display().to_string()
    }
}

fn temp_input_path(input_mode: &str) -> PathBuf {
    let extension = match input_mode {
        "Markdown" => "md",
        _ => "txt",
    };
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!("easydict-longdoc-{stamp}.{extension}"))
}

fn input_label(request: &LongDocumentServiceRequest) -> String {
    match &request.input {
        LongDocumentInput::File(path) => Path::new(path)
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or(path)
            .to_string(),
        LongDocumentInput::InlineText(text) => {
            let preview = text
                .split_whitespace()
                .take(8)
                .collect::<Vec<_>>()
                .join(" ");
            if preview.is_empty() {
                "Inline document".to_string()
            } else {
                format!("Inline: {preview}")
            }
        }
    }
}

fn display_output_path(result: &TranslateDocumentResult) -> Option<String> {
    result
        .bilingual_output_path
        .clone()
        .or_else(|| result.output_path.clone())
        .or_else(|| result.result_json_path.clone())
}

fn output_folder(output_path: &str) -> String {
    parent_folder(output_path).unwrap_or_else(|| "(same as input file folder)".to_string())
}

fn long_document_result_body(result: &TranslateDocumentResult) -> String {
    let mut body = format!(
        "{}: {}/{} chunks translated",
        result.state, result.succeeded_chunks, result.total_chunks
    );
    if let Some(output_path) = display_output_path(result) {
        body.push_str("\nOutput: ");
        body.push_str(&output_path);
    }
    if let Some(failed) = &result.failed_chunk_indexes {
        if !failed.is_empty() {
            body.push_str("\nFailed chunks: ");
            body.push_str(
                &failed
                    .iter()
                    .map(u32::to_string)
                    .collect::<Vec<_>>()
                    .join(", "),
            );
        }
    }
    body
}

fn push_history(state: &mut EasydictUiState, item: TranslationResultPreview) {
    state.long_document.history.insert(0, item);
    state.long_document.history.truncate(MAX_HISTORY_ITEMS);
}

fn current_app_dir() -> Result<PathBuf, String> {
    let exe = std::env::current_exe()
        .map_err(|error| format!("Could not locate current executable: {error}"))?;
    exe.parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| "Could not locate current executable directory".to_string())
}
