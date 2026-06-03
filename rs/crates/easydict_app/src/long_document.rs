use crate::compat_client::DirectWorkerFacade;
use crate::compat_protocol::{
    local_ai_provider_modes, worker_events, BlockTranslatedEventData, ConfigureParams, IpcEvent,
    ProgressEventData, SettingsSnapshot, StatusEventData, TranslateDocumentParams,
    TranslateDocumentResult, TranslateParams,
};
use crate::content_preservation::{
    analyze_formula_preservation, protect_formula_block, resolve_formula_fallback,
    restore_formula_block, BlockContext, PreservationMode, ProtectedBlock, ProtectionPlan,
    RestoreOutcome, RestoreStatus, SoftValidationStatus,
};
use crate::formula_protection::SoftProtectionWrapperKind;
use crate::long_document_export::{
    build_bilingual_output_path, compose_bilingual_markdown, compose_bilingual_text,
    compose_monolingual_markdown, compose_monolingual_text, LongDocumentExportBlockType,
    LongDocumentExportCheckpoint, LongDocumentExportChunkMetadata,
};
use crate::ocr::{
    merged_ocr_text, NativeOcrBackend, OcrBackend, OcrRecognizeParams, OcrResultDto,
    ReqwestOcrHttpClient,
};
use crate::openai_compatible::{CommandFoundryLocalEndpointResolver, FoundryLocalEndpointResolver};
use crate::pdf_content_stream::extract_pdf_literal_strings;
use crate::pdf_export_blocks::{
    build_pdf_overlay_blocks, PdfExportCheckpoint, PdfExportChunkMetadata, PdfExportSourceBlockType,
};
use crate::pdf_native_export::{
    export_pdf_with_content_stream_replacement, NativePdfContentStreamExportFailureKind,
};
use crate::pdf_source_extraction::{
    block_context_for_pdf_source_block, pdf_export_chunk_metadata_for_source_block,
    pdf_source_block_id, pdf_source_document_from_text_summary, PdfSourceBlock,
};
use crate::quick_translate::{
    auto_foundry_local_native_probe_request, quick_translate_request_can_route_natively,
    run_quick_translate_service_with_native_route, QuickQueryMode, QuickTranslateExecutionKind,
    QuickTranslateService, QuickTranslateServiceRequest,
};
use crate::retained_workers::RetainedWorkerPolicy;
use crate::state::{EasydictUiState, TranslationResultPreview};
use crate::translation_cache::{
    long_document_source_hash, long_document_translation_cache_path, LongDocumentTranslationCache,
};
use crate::translation_language::TranslationLanguage;
use crate::translation_services::{
    default_translation_service_descriptors, find_translation_service_descriptor,
    TranslationServiceDescriptor, TranslationServiceKind,
};
use easydict_pdf_overlay::{
    overlay_pdf_text_blocks, PdfOverlayBlock as NativePdfOverlayBlock, PdfOverlayOptions,
    PdfOverlayRect as NativePdfOverlayRect, PdfOverlaySummary,
};
use easydict_pdf_render::{
    extract_pdf_text_chars, render_pdf_pages_to_bgra_files, PdfTextExtractionOptions,
    PdfToBgraOptions,
};
use serde_json::Value;
use std::collections::{BTreeMap, BTreeSet};
use std::env;
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::process;
use std::time::{SystemTime, UNIX_EPOCH};
use text_splitter::{MarkdownSplitter, TextSplitter};
use win_fluent::prelude::ResultStatus;

const NO_FILE_SELECTED: &str = "No file selected";
const MAX_HISTORY_ITEMS: usize = 50;
const NATIVE_TEXT_CHUNK_CHAR_LIMIT: usize = 2_500;
const NATIVE_PDF_EMPTY_TEXT_ERROR: &str = "Native PDF text extraction found no selectable text";

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

impl LongDocumentBackend for DirectWorkerFacade {
    fn configure_longdoc_settings(
        &mut self,
        settings: &SettingsSnapshot,
    ) -> Result<(), LongDocumentBackendError> {
        let result = self
            .configure(&ConfigureParams {
                settings: settings.clone(),
            })
            .map_err(|error| {
                LongDocumentBackendError::new(error.process_message("Long Document worker"))
            })?;
        if result.ok {
            Ok(())
        } else {
            Err(LongDocumentBackendError::new(
                "Long Document worker rejected settings",
            ))
        }
    }

    fn longdoc_translate(
        &mut self,
        params: &TranslateDocumentParams,
    ) -> Result<TranslateDocumentResult, LongDocumentBackendError> {
        DirectWorkerFacade::longdoc_translate(self, params).map_err(|error| {
            LongDocumentBackendError::new(error.process_message("Long Document worker"))
        })
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

pub trait NativeLongDocumentTranslator: Clone + Send {
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError>;
}

#[derive(Clone)]
pub struct QuickTranslateNativeLongDocumentTranslator;

impl NativeLongDocumentTranslator for QuickTranslateNativeLongDocumentTranslator {
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError> {
        let update = run_quick_translate_service_with_native_route(request).ok_or_else(|| {
            LongDocumentBackendError::new("Long document service is not available natively")
        })?;

        update
            .outcome
            .result
            .map(|result| result.translated_text)
            .map_err(|error| LongDocumentBackendError::new(error.message))
    }
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
            LongDocumentInput::File(path.clone()),
            resolve_file_input_mode(&path, &long_doc.input_mode).to_string(),
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

    let page_range = (input_mode == "Pdf")
        .then(|| non_empty(&long_doc.page_range))
        .flatten();

    let params = TranslateDocumentParams {
        input_path: match &input {
            LongDocumentInput::File(path) => path.clone(),
            LongDocumentInput::InlineText(_) => String::new(),
        },
        output_path,
        input_mode,
        from: map_document_language(&long_doc.source_language).to_string(),
        to: map_document_language(&long_doc.target_language).to_string(),
        service_id: map_long_document_service_id(&long_doc.service).to_string(),
        output_mode: map_output_mode(&long_doc.output_mode).to_string(),
        pdf_export_mode: Some("ContentStreamReplacement".to_string()),
        layout_detection: Some(state.settings.layout_detection_mode.clone()),
        page_range,
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
    if let Some(error) = local_long_document_route_preflight_error(&request) {
        return long_document_backend_error_outcome(request, error);
    }

    let request = match try_run_native_text_long_document_request(request) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    let worker_policy = RetainedWorkerPolicy::from_environment();
    if let Some(error) = local_long_document_worker_preflight_error(&request, worker_policy) {
        return long_document_backend_error_outcome(request, error);
    }

    match current_app_dir() {
        Ok(app_dir) => run_long_document_request_with_packaged_app_dir_after_native_probe(
            request,
            app_dir,
            worker_policy,
        ),
        Err(message) => long_document_error_outcome(request, message),
    }
}

pub fn run_long_document_request_with_native_route<B: LongDocumentBackend>(
    backend: &mut B,
    request: LongDocumentServiceRequest,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_route_preflight_error(&request) {
        return long_document_backend_error_outcome(request, error);
    }

    match try_run_native_text_long_document_request(request) {
        NativeLongDocumentDispatch::Handled(outcome) => outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => {
            if let Some(error) = local_long_document_worker_preflight_error(
                &request,
                RetainedWorkerPolicy::from_environment(),
            ) {
                return long_document_backend_error_outcome(request, error);
            }

            run_long_document_request(backend, request)
        }
    }
}

pub fn run_long_document_request_with_packaged_app_dir(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
) -> LongDocumentOutcome {
    run_long_document_request_with_packaged_app_dir_and_worker_policy(
        request,
        app_dir,
        RetainedWorkerPolicy::from_environment(),
    )
}

pub fn run_long_document_request_with_packaged_app_dir_and_worker_policy(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
    worker_policy: RetainedWorkerPolicy,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_route_preflight_error(&request) {
        return long_document_backend_error_outcome(request, error);
    }

    let request = match try_run_native_text_long_document_request(request) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    let mut foundry_resolver = CommandFoundryLocalEndpointResolver::default();
    let request = match try_run_native_text_long_document_request_with_auto_foundry_probe(
        request,
        &mut foundry_resolver,
    ) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    run_long_document_request_with_packaged_app_dir_after_native_probe(
        request,
        app_dir,
        worker_policy,
    )
}

enum NativeLongDocumentDispatch {
    Handled(LongDocumentOutcome),
    NeedsWorker(LongDocumentServiceRequest),
}

fn try_run_native_text_long_document_request(
    request: LongDocumentServiceRequest,
) -> NativeLongDocumentDispatch {
    if !long_document_request_can_route_natively(&request) {
        return NativeLongDocumentDispatch::NeedsWorker(request);
    }

    NativeLongDocumentDispatch::Handled(run_native_text_long_document_request(request))
}

fn try_run_native_text_long_document_request_with_auto_foundry_probe<R>(
    request: LongDocumentServiceRequest,
    foundry_resolver: &mut R,
) -> NativeLongDocumentDispatch
where
    R: FoundryLocalEndpointResolver,
{
    let Some(probe_request) =
        native_quick_translate_request_for_chunk(&request, "native route probe")
    else {
        return NativeLongDocumentDispatch::NeedsWorker(request);
    };

    let Some(native_probe_request) =
        auto_foundry_local_native_probe_request(&probe_request, foundry_resolver)
    else {
        return NativeLongDocumentDispatch::NeedsWorker(request);
    };

    let mut native_request = request;
    native_request.settings = native_probe_request.settings;
    try_run_native_text_long_document_request(native_request)
}

fn run_long_document_request_with_packaged_app_dir_after_native_probe(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
    worker_policy: RetainedWorkerPolicy,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_worker_preflight_error(&request, worker_policy) {
        return long_document_backend_error_outcome(request, error);
    }

    match DirectWorkerFacade::spawn_packaged_longdoc(app_dir) {
        Ok(mut backend) => run_long_document_request(&mut backend, request),
        Err(error) => {
            long_document_error_outcome(request, error.process_message("Long Document worker"))
        }
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

pub fn long_document_request_can_route_natively(request: &LongDocumentServiceRequest) -> bool {
    if native_text_input_kind(&request.params.input_mode).is_none() {
        return false;
    }

    native_quick_translate_request_for_chunk(request, "native route probe")
        .as_ref()
        .is_some_and(quick_translate_request_can_route_natively)
}

pub fn run_native_text_long_document_request(
    request: LongDocumentServiceRequest,
) -> LongDocumentOutcome {
    let mut translator = QuickTranslateNativeLongDocumentTranslator;
    run_native_text_long_document_request_with_translator(&mut translator, request)
}

pub fn run_native_text_long_document_request_with_translator<T: NativeLongDocumentTranslator>(
    translator: &mut T,
    request: LongDocumentServiceRequest,
) -> LongDocumentOutcome {
    let input_label = input_label(&request);
    let result = run_native_text_long_document_request_inner(translator, &request);

    LongDocumentOutcome {
        query_id: request.query_id,
        input_label,
        events: result.events,
        result: result.result,
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

struct NativeLongDocumentRun {
    events: Vec<LongDocumentEvent>,
    result: Result<TranslateDocumentResult, LongDocumentBackendError>,
}

fn run_native_text_long_document_request_inner<T: NativeLongDocumentTranslator>(
    translator: &mut T,
    request: &LongDocumentServiceRequest,
) -> NativeLongDocumentRun {
    let Some(input_kind) = native_text_input_kind(&request.params.input_mode) else {
        return NativeLongDocumentRun {
            events: Vec::new(),
            result: Err(LongDocumentBackendError::new(
                "Native long document translation only supports PlainText, Markdown, and simple PDF text input",
            )),
        };
    };

    let chunks = match read_native_text_source_chunks(request, input_kind) {
        Ok(chunks) => chunks,
        Err(error) => {
            return NativeLongDocumentRun {
                events: Vec::new(),
                result: Err(error),
            };
        }
    };
    if chunks.is_empty() {
        let message = if matches!(input_kind, NativeTextInputKind::PdfText) {
            NATIVE_PDF_EMPTY_TEXT_ERROR
        } else {
            "Document text is empty"
        };
        return NativeLongDocumentRun {
            events: Vec::new(),
            result: Err(LongDocumentBackendError::new(message)),
        };
    }

    let total_chunks = chunks.len() as u32;
    let mut events = vec![LongDocumentEvent::Status(StatusEventData {
        message: "Translating text document natively".to_string(),
    })];
    let mut translations = Vec::with_capacity(chunks.len());
    translations.resize_with(chunks.len(), || None);
    let mut failed_chunk_indexes = Vec::new();
    let mut first_error = None;
    let mut translation_cache = native_long_document_translation_cache(&request.settings);
    let max_concurrency = request
        .settings
        .long_doc_max_concurrency
        .unwrap_or(1)
        .clamp(1, 16)
        .max(1) as usize;
    let mut next_index = 0;

    while next_index < chunks.len() {
        let mut batch = Vec::new();

        while next_index < chunks.len() && batch.len() < max_concurrency {
            let index = next_index;
            next_index += 1;
            let chunk = &chunks[index];
            let chunk_text = chunk.text.as_str();

            events.push(LongDocumentEvent::Progress(ProgressEventData {
                stage: "Translating".to_string(),
                current_block: (index + 1) as u32,
                total_blocks: total_chunks,
                current_page: chunk.page_number,
                total_pages: 1,
                percentage: (index as f64 / total_chunks as f64) * 100.0,
                current_block_preview: Some(chunk_preview(chunk_text)),
            }));

            let preparation = prepare_native_text_chunk_for_translation(request, chunk);
            if let NativeTextChunkPreparation::PreserveOriginal = preparation {
                events.push(LongDocumentEvent::BlockTranslated(
                    BlockTranslatedEventData {
                        chunk_index: index as u32,
                        page_number: Some(chunk.page_number),
                        source_block_id: Some(chunk.source_block_id.clone()),
                        translated_text: chunk.text.clone(),
                        retry_count: 0,
                        last_error: None,
                    },
                ));
                translations[index] = Some(chunk.text.clone());
                continue;
            }

            let NativeTextChunkPreparation::Translate {
                protected_text,
                protection,
            } = preparation
            else {
                unreachable!("preserve-original preparation was handled above");
            };

            let Some(mut translate_request) =
                native_quick_translate_request_for_chunk(request, &protected_text)
            else {
                first_error.get_or_insert_with(|| {
                    "Long document service is not available natively".to_string()
                });
                failed_chunk_indexes.push(index as u32);
                continue;
            };
            apply_native_text_formula_prompt(&mut translate_request, protection.as_ref(), 0, false);

            let chunk_hash =
                (!chunk_text.trim().is_empty()).then(|| long_document_source_hash(chunk_text));
            if let (Some(cache), Some(hash)) = (translation_cache.as_mut(), chunk_hash.as_deref()) {
                if let Ok(Some(cached)) = cache.try_get(
                    &request.params.service_id,
                    &request.params.from,
                    &request.params.to,
                    hash,
                ) {
                    if !cached.trim().is_empty() {
                        events.push(LongDocumentEvent::BlockTranslated(
                            BlockTranslatedEventData {
                                chunk_index: index as u32,
                                page_number: Some(chunk.page_number),
                                source_block_id: Some(chunk.source_block_id.clone()),
                                translated_text: cached.clone(),
                                retry_count: 0,
                                last_error: None,
                            },
                        ));
                        translations[index] = Some(cached);
                        continue;
                    }
                }
            }

            batch.push(NativeTextChunkWork {
                index,
                chunk: chunk.text.clone(),
                fallback_text: chunk.fallback_text.clone(),
                page_number: chunk.page_number,
                source_block_id: chunk.source_block_id.clone(),
                source_hash: chunk_hash,
                request: translate_request,
                protection,
            });
        }

        let batch_results = translate_native_text_chunk_batch(translator, batch);
        for result in batch_results {
            apply_native_text_chunk_result(
                request,
                &mut translation_cache,
                &mut translations,
                &mut failed_chunk_indexes,
                &mut first_error,
                &mut events,
                result,
            );
        }
    }

    events.push(LongDocumentEvent::Progress(ProgressEventData {
        stage: "Exporting".to_string(),
        current_block: total_chunks,
        total_blocks: total_chunks,
        current_page: 1,
        total_pages: 1,
        percentage: 100.0,
        current_block_preview: None,
    }));

    let succeeded_chunks = translations.iter().filter(|chunk| chunk.is_some()).count() as u32;
    let result = if succeeded_chunks == 0 {
        Err(LongDocumentBackendError::new(first_error.unwrap_or_else(
            || "Translation failed for all chunks.".to_string(),
        )))
    } else {
        export_native_text_document(request, input_kind, &chunks, &translations).map(|export| {
            TranslateDocumentResult {
                state: if failed_chunk_indexes.is_empty() {
                    "Completed".to_string()
                } else {
                    "PartiallyCompleted".to_string()
                },
                output_path: Some(export.output_path),
                bilingual_output_path: export.bilingual_output_path,
                total_chunks,
                succeeded_chunks,
                failed_chunk_indexes: (!failed_chunk_indexes.is_empty())
                    .then(|| failed_chunk_indexes.to_vec()),
                quality_report: None,
                result_json_path: None,
            }
        })
    };

    NativeLongDocumentRun { events, result }
}

struct NativeTextChunkWork {
    index: usize,
    chunk: String,
    fallback_text: Option<String>,
    page_number: u32,
    source_block_id: String,
    source_hash: Option<String>,
    request: QuickTranslateServiceRequest,
    protection: Option<NativeTextChunkProtection>,
}

#[derive(Clone, Debug, PartialEq)]
struct NativeTextSourceChunk {
    text: String,
    fallback_text: Option<String>,
    page_number: u32,
    source_block_id: String,
    source_kind: NativeTextSourceKind,
    pdf_context: Option<BlockContext>,
    pdf_export_metadata: Option<PdfExportChunkMetadata>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum NativeTextSourceKind {
    PlainText,
    PdfSelectableText,
    PdfSourceBlock,
    PdfOcr,
}

impl NativeTextSourceChunk {
    fn plain(index: usize, text: String) -> Self {
        Self {
            text,
            fallback_text: None,
            page_number: 1,
            source_block_id: format!("native-text-{}", index + 1),
            source_kind: NativeTextSourceKind::PlainText,
            pdf_context: None,
            pdf_export_metadata: None,
        }
    }

    fn simple_pdf_text(index: usize, text: String, page_index: usize) -> Self {
        Self {
            text,
            fallback_text: None,
            page_number: page_index.saturating_add(1) as u32,
            source_block_id: format!("pdf-p{}-text-b{}", page_index + 1, index + 1),
            source_kind: NativeTextSourceKind::PdfSelectableText,
            pdf_context: None,
            pdf_export_metadata: None,
        }
    }

    fn pdf_ocr(index: usize, text: String, page_number: usize) -> Self {
        let page_number = page_number.max(1);
        Self {
            text,
            fallback_text: None,
            page_number: page_number as u32,
            source_block_id: format!("pdf-p{page_number}-ocr-b{}", index + 1),
            source_kind: NativeTextSourceKind::PdfOcr,
            pdf_context: None,
            pdf_export_metadata: None,
        }
    }

    fn from_pdf_block(block: &PdfSourceBlock, chunk_index: usize, page_block_count: usize) -> Self {
        Self {
            text: block.text.clone(),
            fallback_text: normalized_native_fallback_text(
                block.fallback_text.as_deref(),
                &block.text,
            ),
            page_number: block.page_number as u32,
            source_block_id: pdf_source_block_id(block).to_string(),
            source_kind: NativeTextSourceKind::PdfSourceBlock,
            pdf_context: Some(block_context_for_pdf_source_block(block, 0)),
            pdf_export_metadata: Some(pdf_export_chunk_metadata_for_source_block(
                block,
                chunk_index,
                page_block_count,
                0,
                false,
            )),
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
struct NativeTextChunkProtection {
    context: BlockContext,
    protected_block: ProtectedBlock,
}

#[derive(Clone, Debug, PartialEq)]
enum NativeTextChunkPreparation {
    PreserveOriginal,
    Translate {
        protected_text: String,
        protection: Option<NativeTextChunkProtection>,
    },
}

struct NativeTextChunkResult {
    index: usize,
    chunk: String,
    page_number: u32,
    source_block_id: String,
    source_hash: Option<String>,
    retry_count: u32,
    result: Result<String, LongDocumentBackendError>,
}

const NATIVE_TEXT_MAX_RETRIES_PER_CHUNK: u32 = 1;

fn prepare_native_text_chunk_for_translation(
    request: &LongDocumentServiceRequest,
    chunk: &NativeTextSourceChunk,
) -> NativeTextChunkPreparation {
    let context = native_text_block_context(request, chunk, 0);
    let plan = analyze_formula_preservation(&context);
    let protected_block = protect_formula_block(&context, &plan);

    if protected_block.plan.skip_translation {
        return NativeTextChunkPreparation::PreserveOriginal;
    }

    let protection = native_text_chunk_needs_formula_protection(&protected_block).then_some(
        NativeTextChunkProtection {
            context,
            protected_block,
        },
    );
    let protected_text = protection
        .as_ref()
        .map(|protection| protection.protected_block.protected_text.clone())
        .unwrap_or_else(|| chunk.text.clone());

    NativeTextChunkPreparation::Translate {
        protected_text,
        protection,
    }
}

fn native_text_block_context(
    request: &LongDocumentServiceRequest,
    chunk: &NativeTextSourceChunk,
    retry_attempt: usize,
) -> BlockContext {
    let mut context = chunk
        .pdf_context
        .clone()
        .unwrap_or_else(|| BlockContext::paragraph(chunk.text.clone()));
    context.formula_font_pattern = request.settings.formula_font_pattern.clone();
    context.formula_char_pattern = request.settings.formula_char_pattern.clone();
    context.retry_attempt = retry_attempt;
    context
}

fn normalized_native_fallback_text(
    fallback_text: Option<&str>,
    source_text: &str,
) -> Option<String> {
    let fallback = fallback_text?.trim();
    (!fallback.is_empty() && fallback != source_text.trim()).then(|| fallback.to_string())
}

fn native_text_chunk_needs_formula_protection(protected_block: &ProtectedBlock) -> bool {
    protected_block.plan.mode == PreservationMode::InlineProtected
        && (!protected_block.tokens.is_empty() || !protected_block.soft_spans.is_empty())
}

fn apply_native_text_formula_prompt(
    request: &mut QuickTranslateServiceRequest,
    protection: Option<&NativeTextChunkProtection>,
    retry_attempt: usize,
    last_soft_validation_failed: bool,
) {
    let Some(prompt) = protection.and_then(|protection| {
        native_text_formula_prompt(
            &protection.protected_block,
            retry_attempt,
            last_soft_validation_failed,
        )
    }) else {
        return;
    };

    request.params.custom_prompt = Some(match request.params.custom_prompt.take() {
        Some(existing) if !existing.trim().is_empty() => format!("{existing}\n{prompt}"),
        _ => prompt,
    });
}

fn native_text_formula_prompt(
    protected_block: &ProtectedBlock,
    retry_attempt: usize,
    last_soft_validation_failed: bool,
) -> Option<String> {
    let has_hard_tokens = !protected_block.tokens.is_empty();
    let has_dollar_soft_math = protected_block
        .soft_spans
        .iter()
        .any(|span| span.wrapper_kind == SoftProtectionWrapperKind::DollarMath);
    let has_equation_soft_tags = protected_block
        .soft_spans
        .iter()
        .any(|span| span.wrapper_kind == SoftProtectionWrapperKind::EquationSoftTag);
    let has_exact_soft_spans = protected_block
        .soft_spans
        .iter()
        .any(|span| span.requires_exact_preservation);

    if !has_hard_tokens && !has_dollar_soft_math && !has_equation_soft_tags {
        return None;
    }

    let mut parts = Vec::new();
    if has_hard_tokens {
        parts.push(
            "This text has formula placeholders ({v0}, {v1}, ...). \
             Keep all {vN} placeholders exactly as-is. Do not translate, remove, or modify them.",
        );
    }
    if has_dollar_soft_math {
        parts.push(
            "Content in $...$ is likely a mathematical formula or technical identifier. \
             If it is math, keep it unchanged. If it is ordinary text, translate it and remove the $ delimiters.",
        );
    }
    if has_equation_soft_tags {
        parts.push(
            "Content inside [[EQ_SOFT]]...[[/EQ_SOFT]] is an equation-like technical span. \
             Copy the inner content verbatim and remove only the wrapper markers in the final output.",
        );
    }

    let mut prompt = parts.join(" ");
    if retry_attempt >= 1 {
        let retry_instruction = if last_soft_validation_failed && has_exact_soft_spans {
            "The previous translation attempt changed a protected technical span. \
             Copy every technical symbol sequence inside synthetic $...$ verbatim, do not keep the synthetic $ delimiters in the final output, \
             Copy everything inside [[EQ_SOFT]]...[[/EQ_SOFT]] verbatim and remove only the wrapper markers in the final output.\n"
        } else {
            "The previous translation attempt lost some protected content. \
             Translate carefully and preserve EVERY {vN} placeholder, every $...$ span, and every [[EQ_SOFT]]...[[/EQ_SOFT]] span exactly as written.\n"
        };
        prompt = format!("{retry_instruction}{prompt}");
    }

    Some(prompt)
}

fn translate_native_text_chunk_batch<T: NativeLongDocumentTranslator>(
    translator: &T,
    batch: Vec<NativeTextChunkWork>,
) -> Vec<NativeTextChunkResult> {
    if batch.is_empty() {
        return Vec::new();
    }

    std::thread::scope(|scope| {
        let handles = batch
            .into_iter()
            .map(|work| {
                let index = work.index;
                let chunk_for_panic = work.chunk.clone();
                let mut worker = translator.clone();
                (
                    index,
                    chunk_for_panic,
                    scope.spawn(move || {
                        let (result, retry_count) =
                            translate_native_text_chunk_with_retry(&mut worker, &work);
                        NativeTextChunkResult {
                            index: work.index,
                            chunk: work.chunk,
                            page_number: work.page_number,
                            source_block_id: work.source_block_id,
                            source_hash: work.source_hash,
                            retry_count,
                            result,
                        }
                    }),
                )
            })
            .collect::<Vec<_>>();

        handles
            .into_iter()
            .map(|(index, chunk, handle)| {
                handle.join().unwrap_or_else(|_| NativeTextChunkResult {
                    index,
                    chunk,
                    page_number: 1,
                    source_block_id: format!("native-text-{}", index + 1),
                    source_hash: None,
                    retry_count: 0,
                    result: Err(LongDocumentBackendError::new(
                        "Native long document translation worker panicked",
                    )),
                })
            })
            .collect()
    })
}

fn translate_native_text_chunk_with_retry<T: NativeLongDocumentTranslator>(
    translator: &mut T,
    work: &NativeTextChunkWork,
) -> (Result<String, LongDocumentBackendError>, u32) {
    let mut last_error = None;
    let mut request = work.request.clone();
    let mut protection = work.protection.clone();

    for retry_count in 0..=NATIVE_TEXT_MAX_RETRIES_PER_CHUNK {
        match translator.translate_chunk(request.clone()) {
            Ok(translated) if !translated.trim().is_empty() => {
                if let Some(current_protection) = protection.as_ref() {
                    let outcome =
                        restore_formula_block(&translated, &current_protection.protected_block);
                    let restored =
                        resolve_formula_fallback(&outcome, &current_protection.protected_block);
                    let has_quality_issue = outcome.status != RestoreStatus::FullRestore
                        || outcome.soft_validation_status == SoftValidationStatus::Failed;

                    if !has_quality_issue {
                        return (Ok(restored), retry_count);
                    }

                    last_error = Some(native_text_formula_quality_error(&outcome));
                    if retry_count < NATIVE_TEXT_MAX_RETRIES_PER_CHUNK {
                        let retry_attempt = retry_count as usize + 1;
                        if !should_preserve_equation_soft_protection_on_retry(
                            &outcome,
                            current_protection,
                        ) {
                            protection = Some(reprotect_native_text_chunk(
                                current_protection,
                                retry_attempt,
                            ));
                        }
                        request = native_text_retry_request_with_protection(
                            &request,
                            protection.as_ref(),
                            retry_attempt,
                            outcome.soft_validation_status == SoftValidationStatus::Failed,
                        );
                        continue;
                    }

                    if let Some((fallback_request, fallback_protection)) =
                        native_text_fallback_retry_request(
                            &request,
                            work.fallback_text.as_deref(),
                            protection.as_ref(),
                        )
                    {
                        return (
                            translate_native_text_chunk_once(
                                translator,
                                fallback_request,
                                fallback_protection,
                            ),
                            retry_count,
                        );
                    }

                    return (
                        Err(last_error.unwrap_or_else(|| {
                            LongDocumentBackendError::new(
                                "Protected content was not preserved during translation",
                            )
                        })),
                        retry_count,
                    );
                }

                return (Ok(translated), retry_count);
            }
            Ok(_) => {
                last_error = Some(LongDocumentBackendError::new(
                    "Translation returned empty text",
                ));
            }
            Err(error) => {
                last_error = Some(error);
            }
        }
    }

    if let Some((fallback_request, fallback_protection)) = native_text_fallback_retry_request(
        &request,
        work.fallback_text.as_deref(),
        protection.as_ref(),
    ) {
        return (
            translate_native_text_chunk_once(translator, fallback_request, fallback_protection),
            NATIVE_TEXT_MAX_RETRIES_PER_CHUNK,
        );
    }

    (
        Err(last_error.unwrap_or_else(|| {
            LongDocumentBackendError::new("Native long document translation failed")
        })),
        NATIVE_TEXT_MAX_RETRIES_PER_CHUNK,
    )
}

fn translate_native_text_chunk_once<T: NativeLongDocumentTranslator>(
    translator: &mut T,
    request: QuickTranslateServiceRequest,
    protection: Option<NativeTextChunkProtection>,
) -> Result<String, LongDocumentBackendError> {
    match translator.translate_chunk(request) {
        Ok(translated) if !translated.trim().is_empty() => {
            if let Some(protection) = protection.as_ref() {
                let outcome = restore_formula_block(&translated, &protection.protected_block);
                let restored = resolve_formula_fallback(&outcome, &protection.protected_block);
                let has_quality_issue = outcome.status != RestoreStatus::FullRestore
                    || outcome.soft_validation_status == SoftValidationStatus::Failed;

                if has_quality_issue {
                    return Err(native_text_formula_quality_error(&outcome));
                }

                return Ok(restored);
            }

            Ok(translated)
        }
        Ok(_) => Err(LongDocumentBackendError::new(
            "Translation returned empty text",
        )),
        Err(error) => Err(error),
    }
}

fn native_text_fallback_retry_request(
    previous_request: &QuickTranslateServiceRequest,
    fallback_text: Option<&str>,
    previous_protection: Option<&NativeTextChunkProtection>,
) -> Option<(
    QuickTranslateServiceRequest,
    Option<NativeTextChunkProtection>,
)> {
    let fallback_text = fallback_text?.trim();
    if fallback_text.is_empty() {
        return None;
    }

    let mut context = previous_protection
        .map(|protection| protection.context.clone())
        .unwrap_or_else(|| BlockContext::paragraph(fallback_text.to_string()));
    context.text = fallback_text.to_string();
    context.character_level_protected_text = None;
    context.character_level_tokens = None;
    context.retry_attempt = 0;

    let plan = analyze_formula_preservation(&context);
    if plan.skip_translation {
        return None;
    }

    let protected_block = protect_formula_block(&context, &plan);
    let protection = native_text_chunk_needs_formula_protection(&protected_block).then_some(
        NativeTextChunkProtection {
            context,
            protected_block,
        },
    );
    let protected_text = protection
        .as_ref()
        .map(|protection| protection.protected_block.protected_text.clone())
        .unwrap_or_else(|| fallback_text.to_string());

    let mut next = previous_request.clone();
    next.params.text = protected_text;
    next.params.custom_prompt = next.settings.long_doc_custom_prompt.clone();
    apply_native_text_formula_prompt(&mut next, protection.as_ref(), 0, false);
    Some((next, protection))
}

fn native_text_retry_request_with_protection(
    previous_request: &QuickTranslateServiceRequest,
    protection: Option<&NativeTextChunkProtection>,
    retry_attempt: usize,
    last_soft_validation_failed: bool,
) -> QuickTranslateServiceRequest {
    let mut next = previous_request.clone();
    if let Some(protection) = protection {
        next.params.text = protection.protected_block.protected_text.clone();
    }
    next.params.custom_prompt = next.settings.long_doc_custom_prompt.clone();
    apply_native_text_formula_prompt(
        &mut next,
        protection,
        retry_attempt,
        last_soft_validation_failed,
    );
    next
}

fn reprotect_native_text_chunk(
    current: &NativeTextChunkProtection,
    retry_attempt: usize,
) -> NativeTextChunkProtection {
    let mut context = current.context.clone();
    context.retry_attempt = retry_attempt;
    let protected_block = protect_formula_block(&context, &ProtectionPlan::none());
    NativeTextChunkProtection {
        context,
        protected_block,
    }
}

fn should_preserve_equation_soft_protection_on_retry(
    outcome: &RestoreOutcome,
    current: &NativeTextChunkProtection,
) -> bool {
    outcome.soft_validation_status == SoftValidationStatus::Failed
        && current
            .protected_block
            .soft_spans
            .iter()
            .any(|span| span.wrapper_kind == SoftProtectionWrapperKind::EquationSoftTag)
}

fn native_text_formula_quality_error(outcome: &RestoreOutcome) -> LongDocumentBackendError {
    LongDocumentBackendError::new(format!(
        "Protected content was not preserved during translation (status={:?}, missing={}, softStatus={:?}, softFailures={})",
        outcome.status,
        outcome.missing_token_count,
        outcome.soft_validation_status,
        outcome.soft_failure_count
    ))
}

fn apply_native_text_chunk_result(
    request: &LongDocumentServiceRequest,
    translation_cache: &mut Option<LongDocumentTranslationCache>,
    translations: &mut [Option<String>],
    failed_chunk_indexes: &mut Vec<u32>,
    first_error: &mut Option<String>,
    events: &mut Vec<LongDocumentEvent>,
    result: NativeTextChunkResult,
) {
    if result.index >= translations.len() {
        first_error.get_or_insert_with(|| "Native long document translation failed".to_string());
        return;
    }

    match result.result {
        Ok(translated) if !translated.trim().is_empty() => {
            if let (Some(cache), Some(hash)) =
                (translation_cache.as_mut(), result.source_hash.as_deref())
            {
                let _ = cache.set(
                    &request.params.service_id,
                    &request.params.from,
                    &request.params.to,
                    hash,
                    &result.chunk,
                    &translated,
                );
            }

            events.push(LongDocumentEvent::BlockTranslated(
                BlockTranslatedEventData {
                    chunk_index: result.index as u32,
                    page_number: Some(result.page_number),
                    source_block_id: Some(result.source_block_id.clone()),
                    translated_text: translated.clone(),
                    retry_count: result.retry_count,
                    last_error: None,
                },
            ));
            translations[result.index] = Some(translated);
        }
        Ok(_) => {
            first_error.get_or_insert_with(|| "Translation returned empty text".to_string());
            events.push(LongDocumentEvent::BlockTranslated(
                BlockTranslatedEventData {
                    chunk_index: result.index as u32,
                    page_number: Some(result.page_number),
                    source_block_id: Some(result.source_block_id.clone()),
                    translated_text: result.chunk.clone(),
                    retry_count: result.retry_count,
                    last_error: Some("Translation returned empty text".to_string()),
                },
            ));
            failed_chunk_indexes.push(result.index as u32);
        }
        Err(error) => {
            first_error.get_or_insert_with(|| error.message.clone());
            events.push(LongDocumentEvent::BlockTranslated(
                BlockTranslatedEventData {
                    chunk_index: result.index as u32,
                    page_number: Some(result.page_number),
                    source_block_id: Some(result.source_block_id),
                    translated_text: result.chunk,
                    retry_count: result.retry_count,
                    last_error: Some(error.message),
                },
            ));
            failed_chunk_indexes.push(result.index as u32);
        }
    }
}

fn native_long_document_translation_cache(
    settings: &SettingsSnapshot,
) -> Option<LongDocumentTranslationCache> {
    if !settings.enable_translation_cache.unwrap_or(false) {
        return None;
    }

    LongDocumentTranslationCache::open(long_document_translation_cache_path(
        settings.cache_dir.as_deref(),
    ))
    .ok()
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum NativeTextInputKind {
    PlainText,
    Markdown,
    PdfText,
}

struct NativeTextExport {
    output_path: String,
    bilingual_output_path: Option<String>,
}

fn native_text_input_kind(input_mode: &str) -> Option<NativeTextInputKind> {
    match input_mode {
        "PlainText" => Some(NativeTextInputKind::PlainText),
        "Markdown" => Some(NativeTextInputKind::Markdown),
        "Pdf" => Some(NativeTextInputKind::PdfText),
        _ => None,
    }
}

fn read_native_text_source_chunks(
    request: &LongDocumentServiceRequest,
    input_kind: NativeTextInputKind,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError> {
    match &request.input {
        LongDocumentInput::InlineText(text) => Ok(split_native_text_document(text, input_kind)
            .into_iter()
            .enumerate()
            .map(|(index, text)| NativeTextSourceChunk::plain(index, text))
            .collect()),
        LongDocumentInput::File(path) => match input_kind {
            NativeTextInputKind::PdfText => {
                if let Ok(chunks) =
                    try_read_native_pdf_source_chunks(path, request.params.page_range.as_deref())
                {
                    if !chunks.is_empty() {
                        return Ok(chunks);
                    }
                }

                let text_chunks =
                    read_native_pdf_text_source_chunks(path, request.params.page_range.as_deref())?;
                if !text_chunks.is_empty() {
                    return Ok(text_chunks);
                }

                match read_native_pdf_ocr_source_chunks(
                    request,
                    path,
                    request.params.page_range.as_deref(),
                ) {
                    Ok(chunks) if !chunks.is_empty() => Ok(chunks),
                    _ => Ok(text_chunks),
                }
            }
            NativeTextInputKind::PlainText | NativeTextInputKind::Markdown => {
                fs::read_to_string(path)
                    .map(|text| {
                        split_native_text_document(&text, input_kind)
                            .into_iter()
                            .enumerate()
                            .map(|(index, text)| NativeTextSourceChunk::plain(index, text))
                            .collect()
                    })
                    .map_err(|error| {
                        LongDocumentBackendError::new(format!(
                            "Could not read text document '{}': {error}",
                            path
                        ))
                    })
            }
        },
    }
}

fn try_read_native_pdf_source_chunks(
    path: &str,
    page_range: Option<&str>,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError> {
    try_read_native_pdf_source_chunks_with_extractor(path, page_range, |options| {
        read_native_pdf_source_chunks_with_options(options)
    })
}

fn try_read_native_pdf_source_chunks_with_extractor<F>(
    path: &str,
    page_range: Option<&str>,
    mut extractor: F,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>
where
    F: FnMut(
        &PdfTextExtractionOptions,
    ) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>,
{
    let mut options = PdfTextExtractionOptions::new(path);
    let page_range = page_range.map(str::trim).unwrap_or("");
    if !page_range.is_empty() && !page_range.eq_ignore_ascii_case("all") {
        options.page_selection = Some(page_range.to_string());
    }

    match extractor(&options) {
        Ok(chunks) if !chunks.is_empty() => Ok(chunks),
        first_attempt => {
            options.prefer_loose_bounds = true;
            match (first_attempt, extractor(&options)) {
                (Err(first_error), Err(_)) => Err(first_error),
                (_, second_attempt) => second_attempt,
            }
        }
    }
}

fn read_native_pdf_source_chunks_with_options(
    options: &PdfTextExtractionOptions,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError> {
    let input_path = options.input_pdf.display();
    let summary = extract_pdf_text_chars(options).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not extract PDF text '{input_path}': {error}",
        ))
    })?;
    let document = pdf_source_document_from_text_summary(&summary);
    Ok(document
        .pages
        .iter()
        .flat_map(|page| {
            page.blocks
                .iter()
                .map(move |block| (block, page.blocks.len()))
        })
        .filter(|(block, _)| !block.text.trim().is_empty())
        .enumerate()
        .map(|(chunk_index, (block, page_block_count))| {
            NativeTextSourceChunk::from_pdf_block(block, chunk_index, page_block_count)
        })
        .collect())
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct NativePdfOcrPage {
    page_number: usize,
    pixel_width: u32,
    pixel_height: u32,
    pixel_data_path: PathBuf,
}

trait NativePdfOcrPageRenderer {
    fn render_pages_to_bgra(
        &mut self,
        path: &str,
        page_range: Option<&str>,
        output_dir: &Path,
    ) -> Result<Vec<NativePdfOcrPage>, LongDocumentBackendError>;
}

#[derive(Default)]
struct PdfiumNativePdfOcrPageRenderer;

impl NativePdfOcrPageRenderer for PdfiumNativePdfOcrPageRenderer {
    fn render_pages_to_bgra(
        &mut self,
        path: &str,
        page_range: Option<&str>,
        output_dir: &Path,
    ) -> Result<Vec<NativePdfOcrPage>, LongDocumentBackendError> {
        let mut options = PdfToBgraOptions::new(path, output_dir);
        let page_range = page_range.map(str::trim).unwrap_or("");
        if !page_range.is_empty() && !page_range.eq_ignore_ascii_case("all") {
            options.page_selection = Some(page_range.to_string());
        }

        let summary = render_pdf_pages_to_bgra_files(&options).map_err(|error| {
            LongDocumentBackendError::new(format!(
                "Could not render PDF pages for OCR '{}': {error}",
                path
            ))
        })?;

        Ok(summary
            .rendered_pages
            .into_iter()
            .map(|page| NativePdfOcrPage {
                page_number: page.page_number,
                pixel_width: page.pixel_width,
                pixel_height: page.pixel_height,
                pixel_data_path: page.pixel_data_path,
            })
            .collect())
    }
}

fn read_native_pdf_ocr_source_chunks(
    request: &LongDocumentServiceRequest,
    path: &str,
    page_range: Option<&str>,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError> {
    let mut renderer = PdfiumNativePdfOcrPageRenderer;
    let http_client = ReqwestOcrHttpClient::from_settings(&request.settings).map_err(|error| {
        LongDocumentBackendError::new(format!("Could not create PDF OCR backend: {error}"))
    })?;
    let mut ocr_backend = NativeOcrBackend::new(http_client);
    read_native_pdf_ocr_source_chunks_with_services(
        request,
        path,
        page_range,
        &mut renderer,
        &mut ocr_backend,
    )
}

fn read_native_pdf_ocr_source_chunks_with_services<R, B>(
    request: &LongDocumentServiceRequest,
    path: &str,
    page_range: Option<&str>,
    renderer: &mut R,
    ocr_backend: &mut B,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>
where
    R: NativePdfOcrPageRenderer,
    B: OcrBackend,
{
    let output_dir = native_pdf_ocr_temp_dir(path);
    let result = (|| {
        let pages = renderer.render_pages_to_bgra(path, page_range, &output_dir)?;
        if pages.is_empty() {
            return Ok(Vec::new());
        }

        ocr_backend.configure(&request.settings).map_err(|error| {
            LongDocumentBackendError::new(format!("Could not configure PDF OCR: {error}"))
        })?;

        let mut chunks = Vec::new();
        for page in pages {
            let params = OcrRecognizeParams {
                pixel_data_path: page.pixel_data_path.display().to_string(),
                pixel_width: page.pixel_width,
                pixel_height: page.pixel_height,
                preferred_language_tag: None,
            };
            let result = ocr_backend.recognize(&params).map_err(|error| {
                LongDocumentBackendError::new(format!(
                    "Could not OCR PDF page {}: {error}",
                    page.page_number
                ))
            })?;
            append_native_pdf_ocr_chunks(&mut chunks, page.page_number, &result);
        }

        Ok(chunks)
    })();

    let _ = fs::remove_dir_all(&output_dir);
    result
}

fn append_native_pdf_ocr_chunks(
    chunks: &mut Vec<NativeTextSourceChunk>,
    page_number: usize,
    result: &OcrResultDto,
) {
    let text = merged_ocr_text(result);
    for text in split_native_text_document(&text, NativeTextInputKind::PdfText) {
        let chunk_index = chunks.len();
        chunks.push(NativeTextSourceChunk::pdf_ocr(
            chunk_index,
            text,
            page_number,
        ));
    }
}

fn native_pdf_ocr_temp_dir(path: &str) -> PathBuf {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default();
    let stem = Path::new(path)
        .file_stem()
        .and_then(|stem| stem.to_str())
        .map(sanitize_temp_path_component)
        .filter(|stem| !stem.is_empty())
        .unwrap_or_else(|| "document".to_string());

    env::temp_dir()
        .join("easydict-pdf-ocr")
        .join(format!("{}-{stamp}-{stem}", process::id()))
}

fn sanitize_temp_path_component(value: &str) -> String {
    value
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() || matches!(character, '-' | '_') {
                character
            } else {
                '_'
            }
        })
        .collect()
}

fn local_long_document_file_input_error(
    request: &LongDocumentServiceRequest,
) -> Option<LongDocumentBackendError> {
    let LongDocumentInput::File(path) = &request.input else {
        return None;
    };

    let path = path.trim();
    if path.is_empty() {
        return Some(LongDocumentBackendError::new(
            "Long document input file path cannot be empty",
        ));
    }

    match fs::metadata(path) {
        Ok(metadata) if metadata.is_file() => None,
        Ok(_) => Some(LongDocumentBackendError::new(format!(
            "Could not read long document input '{}': path is not a file",
            path
        ))),
        Err(error) => Some(LongDocumentBackendError::new(format!(
            "Could not read long document input '{}': {error}",
            path
        ))),
    }
}

fn local_long_document_worker_preflight_error(
    request: &LongDocumentServiceRequest,
    worker_policy: RetainedWorkerPolicy,
) -> Option<LongDocumentBackendError> {
    local_long_document_file_input_error(request)
        .or_else(|| local_long_document_route_preflight_error(request))
        .or_else(|| local_long_document_local_ai_worker_bridge_error(request))
        .or_else(|| local_long_document_service_error(request))
        .or_else(|| local_long_document_retained_worker_disabled_error(worker_policy))
}

fn local_long_document_route_preflight_error(
    request: &LongDocumentServiceRequest,
) -> Option<LongDocumentBackendError> {
    local_long_document_output_error(request)
        .or_else(|| local_long_document_target_language_error(request))
}

fn local_long_document_target_language_error(
    request: &LongDocumentServiceRequest,
) -> Option<LongDocumentBackendError> {
    if !request.params.to.trim().eq_ignore_ascii_case("auto") {
        return None;
    }

    Some(LongDocumentBackendError::new(
        "Long Document target language cannot be Auto",
    ))
}

fn local_long_document_service_error(
    request: &LongDocumentServiceRequest,
) -> Option<LongDocumentBackendError> {
    let service_id = request.params.service_id.trim();
    if service_id.is_empty() {
        return Some(LongDocumentBackendError::new(
            "Long document translation service cannot be empty",
        ));
    }

    if service_id.starts_with("mdx::") {
        return Some(LongDocumentBackendError::new(
            "Dictionary services are not available for Long Document translation",
        ));
    }

    let Some(descriptor) = find_translation_service_descriptor(service_id) else {
        return Some(LongDocumentBackendError::new(format!(
            "Long document translation service '{service_id}' is not registered",
        )));
    };

    if !long_document_service_kind_is_supported(descriptor.kind) {
        return Some(LongDocumentBackendError::new(
            "Dictionary services are not available for Long Document translation",
        ));
    }

    None
}

fn local_long_document_local_ai_worker_bridge_error(
    request: &LongDocumentServiceRequest,
) -> Option<LongDocumentBackendError> {
    if !is_local_ai_long_document_service_id(&request.params.service_id) {
        return None;
    }

    Some(LongDocumentBackendError::new(
        "Windows Local AI Long Document translation requires a Rust-native route; the selected input or provider would require retained .NET workers.",
    ))
}

fn local_long_document_retained_worker_disabled_error(
    worker_policy: RetainedWorkerPolicy,
) -> Option<LongDocumentBackendError> {
    worker_policy
        .longdoc_worker_disabled_message()
        .map(LongDocumentBackendError::new)
}

fn local_long_document_output_error(
    request: &LongDocumentServiceRequest,
) -> Option<LongDocumentBackendError> {
    let output_path = request
        .params
        .output_path
        .as_deref()
        .map(str::trim)
        .filter(|path| !path.is_empty())?;
    let output_path = Path::new(output_path);

    match fs::metadata(output_path) {
        Ok(metadata) if metadata.is_dir() => {
            return Some(LongDocumentBackendError::new(format!(
                "Long document output path '{}' is a directory",
                output_path.display()
            )));
        }
        Ok(_) => return None,
        Err(error) if error.kind() != std::io::ErrorKind::NotFound => {
            return Some(LongDocumentBackendError::new(format!(
                "Could not inspect long document output '{}': {error}",
                output_path.display()
            )));
        }
        Err(_) => {}
    }

    let Some(parent) = output_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    else {
        return None;
    };

    match fs::metadata(parent) {
        Ok(metadata) if metadata.is_dir() => None,
        Ok(_) => Some(LongDocumentBackendError::new(format!(
            "Could not create long document output folder '{}': path is not a directory",
            parent.display()
        ))),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => None,
        Err(error) => Some(LongDocumentBackendError::new(format!(
            "Could not inspect long document output folder '{}': {error}",
            parent.display()
        ))),
    }
}

fn read_native_pdf_text_source_chunks(
    path: &str,
    page_range: Option<&str>,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError> {
    let metadata = fs::metadata(path).map_err(|error| {
        LongDocumentBackendError::new(format!("Could not read PDF document '{}': {error}", path))
    })?;
    if !metadata.is_file() {
        return Err(LongDocumentBackendError::new(format!(
            "Could not read PDF document '{}': path is not a file",
            path
        )));
    }

    let pages = extract_native_pdf_text_by_pages(path)?;
    let selected_indexes = selected_pdf_page_indexes(page_range, pages.len())
        .unwrap_or_else(|| (0..pages.len()).collect());

    let mut chunks = Vec::new();
    for page_index in selected_indexes {
        let Some(page_text) = pages.get(page_index) else {
            continue;
        };
        for text in split_native_text_document(page_text, NativeTextInputKind::PdfText) {
            let chunk_index = chunks.len();
            chunks.push(NativeTextSourceChunk::simple_pdf_text(
                chunk_index,
                text,
                page_index,
            ));
        }
    }

    Ok(chunks)
}

fn extract_native_pdf_text_by_pages(path: &str) -> Result<Vec<String>, LongDocumentBackendError> {
    match pdf_extract::extract_text_by_pages(path) {
        Ok(pages) if pdf_pages_contain_text(&pages) => Ok(pages),
        Ok(pages) => match extract_native_pdf_text_from_content_stream_pages(path) {
            Ok(fallback_pages) if pdf_pages_contain_text(&fallback_pages) => Ok(fallback_pages),
            _ => Ok(pages),
        },
        Err(primary_error) => match extract_native_pdf_text_from_content_stream_pages(path) {
            Ok(pages) => Ok(pages),
            Err(_) => Err(LongDocumentBackendError::new(format!(
                "Could not extract PDF text '{}': {primary_error}",
                path
            ))),
        },
    }
}

pub fn extract_native_pdf_text_from_content_stream_pages(
    path: &str,
) -> Result<Vec<String>, LongDocumentBackendError> {
    let document = lopdf::Document::load(path).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not parse PDF content streams '{}': {error}",
            path
        ))
    })?;

    document
        .get_pages()
        .into_iter()
        .map(|(page_number, page_id)| {
            let decoded = document
                .extract_text_chunks(&[page_number])
                .into_iter()
                .filter_map(Result::ok)
                .collect::<Vec<_>>()
                .join("");
            if !decoded.trim().is_empty() {
                return Ok(decoded);
            }

            extract_native_pdf_literal_text_from_page_content(&document, page_id, path)
        })
        .collect()
}

fn extract_native_pdf_literal_text_from_page_content(
    document: &lopdf::Document,
    page_id: lopdf::ObjectId,
    path: &str,
) -> Result<String, LongDocumentBackendError> {
    let content = document.get_page_content(page_id).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not read PDF page content stream '{}': {error}",
            path
        ))
    })?;
    let content = String::from_utf8_lossy(&content);
    Ok(extract_pdf_literal_strings(&content)
        .into_iter()
        .map(|literal| literal.value)
        .collect::<Vec<_>>()
        .join(""))
}

fn pdf_pages_contain_text(pages: &[String]) -> bool {
    pages.iter().any(|page| !page.trim().is_empty())
}

fn selected_pdf_page_indexes(page_range: Option<&str>, total_pages: usize) -> Option<Vec<usize>> {
    let page_range = page_range?.trim();
    if page_range.is_empty() || page_range.eq_ignore_ascii_case("all") {
        return None;
    }

    let mut pages = std::collections::BTreeSet::new();
    for part in page_range
        .split(',')
        .map(str::trim)
        .filter(|part| !part.is_empty())
    {
        if let Some((start, end)) = part.split_once('-') {
            let Ok(start) = start.trim().parse::<isize>() else {
                continue;
            };
            let Ok(end) = end.trim().parse::<isize>() else {
                continue;
            };
            let start = start.max(1) as usize;
            let end = end.min(total_pages as isize);
            if end < 1 {
                continue;
            }
            let end = end as usize;
            for page in start..=end {
                pages.insert(page);
            }
        } else if let Ok(page) = part.parse::<usize>() {
            if (1..=total_pages).contains(&page) {
                pages.insert(page);
            }
        }
    }

    (!pages.is_empty()).then(|| pages.into_iter().map(|page| page - 1).collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::{Arc, Mutex};

    #[test]
    fn selected_pdf_page_indexes_matches_compat_page_range_parser() {
        assert_eq!(selected_pdf_page_indexes(None, 10), None);
        assert_eq!(selected_pdf_page_indexes(Some(""), 10), None);
        assert_eq!(selected_pdf_page_indexes(Some("   "), 10), None);
        assert_eq!(selected_pdf_page_indexes(Some("all"), 10), None);
        assert_eq!(selected_pdf_page_indexes(Some("ALL"), 10), None);
        assert_eq!(selected_pdf_page_indexes(Some("3"), 10), Some(vec![2]));
        assert_eq!(
            selected_pdf_page_indexes(Some("1-5"), 10),
            Some(vec![0, 1, 2, 3, 4])
        );
        assert_eq!(
            selected_pdf_page_indexes(Some("1-3,5,7-10"), 10),
            Some(vec![0, 1, 2, 4, 6, 7, 8, 9])
        );
        assert_eq!(
            selected_pdf_page_indexes(Some("1-20"), 5),
            Some(vec![0, 1, 2, 3, 4])
        );
        assert_eq!(selected_pdf_page_indexes(Some("100"), 5), None);
        assert_eq!(
            selected_pdf_page_indexes(Some("0,1,2"), 5),
            Some(vec![0, 1])
        );
        assert_eq!(
            selected_pdf_page_indexes(Some(" 1 - 3 , 5 "), 10),
            Some(vec![0, 1, 2, 4])
        );
        assert_eq!(selected_pdf_page_indexes(Some("abc"), 10), None);
        assert_eq!(
            selected_pdf_page_indexes(Some("1,abc,3"), 10),
            Some(vec![0, 2])
        );
        assert_eq!(selected_pdf_page_indexes(Some("5-5"), 10), Some(vec![4]));
    }

    #[test]
    fn native_text_chunk_retries_with_fallback_text_after_original_retries_fail() {
        let request = test_quick_translate_request("Mostcompetitiveneural sequencetransduction");
        let work = NativeTextChunkWork {
            index: 0,
            chunk: "Mostcompetitiveneural sequencetransduction".to_string(),
            fallback_text: Some("Most competitive neural sequence transduction".to_string()),
            page_number: 1,
            source_block_id: "pdf-p1-block-1".to_string(),
            source_hash: None,
            request,
            protection: None,
        };
        let mut translator = FallbackRetryTranslator::default();

        let (result, retry_count) = translate_native_text_chunk_with_retry(&mut translator, &work);

        assert_eq!(retry_count, 1);
        assert_eq!(
            result.expect("fallback retry should succeed"),
            "translated:Most competitive neural sequence transduction"
        );
        assert_eq!(
            translator.calls(),
            vec![
                "Mostcompetitiveneural sequencetransduction".to_string(),
                "Mostcompetitiveneural sequencetransduction".to_string(),
                "Most competitive neural sequence transduction".to_string(),
            ]
        );
    }

    #[test]
    fn native_pdf_source_chunk_extraction_does_not_retry_when_tight_bounds_succeed() {
        let calls = Arc::new(Mutex::new(Vec::new()));
        let observed_calls = Arc::clone(&calls);

        let chunks =
            try_read_native_pdf_source_chunks_with_extractor("paper.pdf", Some("2-3"), |options| {
                observed_calls
                    .lock()
                    .unwrap()
                    .push((options.prefer_loose_bounds, options.page_selection.clone()));
                Ok(vec![NativeTextSourceChunk::plain(
                    0,
                    "tight text".to_string(),
                )])
            })
            .expect("tight bounds should succeed");

        assert_eq!(chunks.len(), 1);
        assert_eq!(chunks[0].text, "tight text");
        assert_eq!(
            *calls.lock().unwrap(),
            vec![(false, Some("2-3".to_string()))]
        );
    }

    #[test]
    fn native_pdf_source_chunk_extraction_retries_with_loose_bounds_after_empty_tight() {
        let calls = Arc::new(Mutex::new(Vec::new()));
        let observed_calls = Arc::clone(&calls);

        let chunks =
            try_read_native_pdf_source_chunks_with_extractor("paper.pdf", Some("all"), |options| {
                observed_calls
                    .lock()
                    .unwrap()
                    .push((options.prefer_loose_bounds, options.page_selection.clone()));
                if options.prefer_loose_bounds {
                    Ok(vec![NativeTextSourceChunk::plain(
                        0,
                        "loose text".to_string(),
                    )])
                } else {
                    Ok(Vec::new())
                }
            })
            .expect("loose bounds should recover source chunks");

        assert_eq!(chunks.len(), 1);
        assert_eq!(chunks[0].text, "loose text");
        assert_eq!(*calls.lock().unwrap(), vec![(false, None), (true, None)]);
    }

    #[test]
    fn native_pdf_source_chunk_extraction_keeps_tight_error_when_loose_also_fails() {
        let calls = Arc::new(Mutex::new(Vec::new()));
        let observed_calls = Arc::clone(&calls);

        let error =
            try_read_native_pdf_source_chunks_with_extractor("paper.pdf", Some("4"), |options| {
                observed_calls
                    .lock()
                    .unwrap()
                    .push((options.prefer_loose_bounds, options.page_selection.clone()));
                if options.prefer_loose_bounds {
                    Err(LongDocumentBackendError::new("loose error"))
                } else {
                    Err(LongDocumentBackendError::new("tight error"))
                }
            })
            .expect_err("both extraction attempts should fail");

        assert_eq!(error.message, "tight error");
        assert_eq!(
            *calls.lock().unwrap(),
            vec![
                (false, Some("4".to_string())),
                (true, Some("4".to_string()))
            ]
        );
    }

    #[test]
    fn auto_foundry_local_probe_routes_long_document_to_native_before_worker() {
        let mut resolver =
            TestFoundryEndpointResolver::new(Some("foundry-local-invalid".to_string()));
        let request = test_long_document_request_for_windows_local_ai();

        let dispatch = try_run_native_text_long_document_request_with_auto_foundry_probe(
            request,
            &mut resolver,
        );

        assert_eq!(resolver.calls, 1);
        let NativeLongDocumentDispatch::Handled(outcome) = dispatch else {
            panic!("Auto Foundry endpoint discovery should use the native LongDoc route");
        };
        let error = outcome
            .result
            .expect_err("invalid Foundry endpoint should fail in native provider route");
        assert!(
            !error.message.contains("Long Document worker"),
            "native Foundry probe should not start retained LongDoc worker: {}",
            error.message
        );
        assert!(
            !error.message.contains("retained .NET workers"),
            "native Foundry probe should not report retained worker requirement: {}",
            error.message
        );
    }

    #[test]
    fn native_source_block_order_suffix_accepts_region_block_ids() {
        assert_eq!(parse_native_source_block_order_suffix("b12"), Some(12));
        assert_eq!(parse_native_source_block_order_suffix("B3"), Some(3));
        assert_eq!(parse_native_source_block_order_suffix("7"), Some(7));
        assert_eq!(parse_native_source_block_order_suffix("raw"), None);
    }

    #[test]
    fn native_pdf_ocr_source_chunks_render_pages_and_merge_ocr_text() {
        let mut renderer = RecordingPdfOcrRenderer::new([
            NativePdfOcrPage {
                page_number: 2,
                pixel_width: 8,
                pixel_height: 6,
                pixel_data_path: PathBuf::from("page-2.bgra"),
            },
            NativePdfOcrPage {
                page_number: 3,
                pixel_width: 10,
                pixel_height: 7,
                pixel_data_path: PathBuf::from("page-3.bgra"),
            },
        ]);
        let mut ocr_backend = RecordingPdfOcrBackend::with_responses([
            Ok(OcrResultDto {
                text: " First OCR page ".to_string(),
                ..Default::default()
            }),
            Ok(OcrResultDto {
                text: "Second OCR page".to_string(),
                ..Default::default()
            }),
        ]);
        let request = test_pdf_long_document_request(None);

        let chunks = read_native_pdf_ocr_source_chunks_with_services(
            &request,
            "paper.pdf",
            Some("2-3"),
            &mut renderer,
            &mut ocr_backend,
        )
        .expect("OCR source chunks");

        assert_eq!(renderer.calls.len(), 1);
        assert_eq!(renderer.calls[0].path, "paper.pdf");
        assert_eq!(renderer.calls[0].page_range.as_deref(), Some("2-3"));
        assert!(renderer.calls[0]
            .output_dir
            .to_string_lossy()
            .contains("easydict-pdf-ocr"));
        assert_eq!(ocr_backend.configure_calls, vec![request.settings.clone()]);
        assert_eq!(
            ocr_backend.recognize_calls,
            vec![
                OcrRecognizeParams {
                    pixel_data_path: "page-2.bgra".to_string(),
                    pixel_width: 8,
                    pixel_height: 6,
                    preferred_language_tag: None,
                },
                OcrRecognizeParams {
                    pixel_data_path: "page-3.bgra".to_string(),
                    pixel_width: 10,
                    pixel_height: 7,
                    preferred_language_tag: None,
                },
            ]
        );
        assert_eq!(chunks.len(), 2);
        assert_eq!(chunks[0].text, "First OCR page");
        assert_eq!(chunks[0].page_number, 2);
        assert_eq!(chunks[0].source_kind, NativeTextSourceKind::PdfOcr);
        assert_eq!(chunks[0].source_block_id, "pdf-p2-ocr-b1");
        assert_eq!(chunks[1].text, "Second OCR page");
        assert_eq!(chunks[1].page_number, 3);
        assert_eq!(chunks[1].source_block_id, "pdf-p3-ocr-b2");
    }

    #[test]
    fn native_pdf_ocr_chunks_skip_content_stream_export() {
        let selectable =
            NativeTextSourceChunk::simple_pdf_text(0, "Selectable text".to_string(), 0);
        let ocr = NativeTextSourceChunk::pdf_ocr(1, "Scanned text".to_string(), 1);

        assert!(native_pdf_chunks_support_content_stream_export(&[
            selectable.clone()
        ]));
        assert!(!native_pdf_chunks_support_content_stream_export(&[
            selectable, ocr
        ]));
    }

    #[test]
    fn native_pdf_ocr_chunks_export_as_text_output() {
        let temp_dir = unique_longdoc_test_dir("pdf-ocr-text-export");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let output_path = temp_dir.join("scan.pdf");
        let request = test_pdf_long_document_request(Some(output_path.display().to_string()));
        let chunks = vec![NativeTextSourceChunk::pdf_ocr(
            0,
            "Scanned source".to_string(),
            1,
        )];
        let translations = vec![Some("Translated scanned source".to_string())];

        let export = export_native_text_document(
            &request,
            NativeTextInputKind::PdfText,
            &chunks,
            &translations,
        )
        .expect("text export");

        let exported_path = PathBuf::from(&export.output_path);
        assert_eq!(
            exported_path.extension().and_then(|value| value.to_str()),
            Some("txt")
        );
        assert!(!output_path.exists());
        assert_eq!(
            fs::read_to_string(&exported_path).expect("exported text"),
            "Translated scanned source"
        );

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_export_uses_overlay_font_embedding_for_cjk_translation() {
        let temp_dir = unique_longdoc_test_dir("pdf-cjk-overlay-export");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let input_path = temp_dir.join("paper.pdf");
        let output_path = temp_dir.join("paper-translated.pdf");
        fs::write(
            &input_path,
            minimal_longdoc_test_pdf_with_pages(&["First page", "Hello PDF"]),
        )
        .expect("input pdf");

        let request = LongDocumentServiceRequest {
            query_id: 91,
            input: LongDocumentInput::File(input_path.display().to_string()),
            params: TranslateDocumentParams {
                input_path: input_path.display().to_string(),
                output_path: Some(output_path.display().to_string()),
                input_mode: "Pdf".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "google".to_string(),
                output_mode: "Monolingual".to_string(),
                pdf_export_mode: Some("ContentStreamReplacement".to_string()),
                layout_detection: None,
                page_range: Some("2".to_string()),
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: None,
            },
            settings: SettingsSnapshot {
                cjk_font_path: Some(test_cjk_font_path().display().to_string()),
                ..SettingsSnapshot::default()
            },
        };
        let source_chunks = vec![NativeTextSourceChunk {
            text: "Hello PDF".to_string(),
            fallback_text: None,
            page_number: 2,
            source_block_id: "pdf-p2-body-b1".to_string(),
            source_kind: NativeTextSourceKind::PdfSourceBlock,
            pdf_context: None,
            pdf_export_metadata: Some(PdfExportChunkMetadata {
                chunk_index: 0,
                page_number: 2,
                source_block_id: "pdf-p2-body-b1".to_string(),
                source_block_type: PdfExportSourceBlockType::Paragraph,
                order_in_page: 0,
                reading_order_score: 1.0,
                bounding_box: Some(crate::PdfRect::new(96.0, 684.0, 260.0, 48.0)),
                text_style: Some(crate::pdf_export_blocks::PdfExportBlockTextStyle {
                    font_size: 14.0,
                    line_spacing: 16.0,
                    rotation_angle: 0.0,
                }),
                translation_skipped: false,
                preserve_original_text_in_pdf_export: false,
                retry_count: 0,
                fallback_text: None,
                detected_font_names: Some(vec!["Helvetica".to_string()]),
            }),
        }];
        let translations = vec![Some("你好，PDF".to_string())];

        let export = try_export_native_pdf_document(&request, &source_chunks, &translations, "")
            .expect("CJK overlay PDF export should not fail")
            .expect("CJK overlay should handle native PDF export");

        assert_eq!(export.output_path, output_path.display().to_string());
        assert!(export.bilingual_output_path.is_none());
        assert_eq!(
            lopdf::Document::load(&output_path)
                .expect("native PDF output should open")
                .get_pages()
                .len(),
            1
        );
        let extracted =
            pdf_extract::extract_text(&output_path).expect("overlay text should extract");
        assert!(extracted.contains("你好"));

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_export_failure_allows_text_export_fallback() {
        let temp_dir = unique_longdoc_test_dir("pdf-export-fallback-to-text");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let input_path = temp_dir.join("paper.pdf");
        let output_path = temp_dir.join("paper-translated.pdf");
        fs::write(
            &input_path,
            minimal_longdoc_test_pdf_with_pages(&["Original PDF text"]),
        )
        .expect("input pdf");

        let request = LongDocumentServiceRequest {
            query_id: 92,
            input: LongDocumentInput::File(input_path.display().to_string()),
            params: TranslateDocumentParams {
                input_path: input_path.display().to_string(),
                output_path: Some(output_path.display().to_string()),
                input_mode: "Pdf".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "google".to_string(),
                output_mode: "Monolingual".to_string(),
                pdf_export_mode: Some("ContentStreamReplacement".to_string()),
                layout_detection: None,
                page_range: None,
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: None,
            },
            settings: SettingsSnapshot::default(),
        };
        let source_chunks = vec![NativeTextSourceChunk::simple_pdf_text(
            0,
            "Text that does not exist in the PDF stream".to_string(),
            0,
        )];
        let translations = vec![Some("Translated fallback text".to_string())];

        let export = try_export_native_pdf_document(&request, &source_chunks, &translations, "")
            .expect("native PDF export failure should fall back to text export");

        assert!(
            export.is_none(),
            "failed PDF content-stream export should allow the caller to write TXT output"
        );
        assert!(
            !output_path.exists(),
            "failed PDF export should not leave a partial PDF"
        );

        fs::remove_dir_all(&temp_dir).ok();
    }

    fn test_quick_translate_request(text: &str) -> QuickTranslateServiceRequest {
        QuickTranslateServiceRequest {
            query_id: 1,
            service: QuickTranslateService {
                id: "google".to_string(),
                name: "Google Translate".to_string(),
                enabled_query: true,
                grammar_capable: false,
                streaming_capable: false,
            },
            query_mode: QuickQueryMode::Translation,
            execution_kind: QuickTranslateExecutionKind::Translate,
            params: TranslateParams {
                text: text.to_string(),
                from: Some("en".to_string()),
                to: Some("zh".to_string()),
                services: Some(vec!["google".to_string()]),
                custom_prompt: None,
            },
            grammar_params: None,
            settings: SettingsSnapshot::default(),
        }
    }

    fn test_long_document_request_for_windows_local_ai() -> LongDocumentServiceRequest {
        LongDocumentServiceRequest {
            query_id: 42,
            input: LongDocumentInput::InlineText("Hello Foundry document".to_string()),
            params: TranslateDocumentParams {
                input_path: String::new(),
                output_path: None,
                input_mode: "PlainText".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "windows-local-ai".to_string(),
                output_mode: "Monolingual".to_string(),
                pdf_export_mode: None,
                layout_detection: None,
                page_range: None,
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: None,
            },
            settings: SettingsSnapshot {
                local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
                foundry_local_model: Some("qwen2.5-0.5b".to_string()),
                ..SettingsSnapshot::default()
            },
        }
    }

    fn test_pdf_long_document_request(output_path: Option<String>) -> LongDocumentServiceRequest {
        LongDocumentServiceRequest {
            query_id: 77,
            input: LongDocumentInput::File("scan.pdf".to_string()),
            params: TranslateDocumentParams {
                input_path: "scan.pdf".to_string(),
                output_path,
                input_mode: "Pdf".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "google".to_string(),
                output_mode: "Monolingual".to_string(),
                pdf_export_mode: Some("ContentStreamReplacement".to_string()),
                layout_detection: None,
                page_range: None,
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: None,
            },
            settings: SettingsSnapshot {
                ocr_engine: Some("WindowsNative".to_string()),
                ..SettingsSnapshot::default()
            },
        }
    }

    fn unique_longdoc_test_dir(label: &str) -> PathBuf {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or_default();
        std::env::temp_dir().join(format!(
            "easydict-longdoc-{label}-{}-{stamp}",
            std::process::id()
        ))
    }

    fn test_cjk_font_path() -> PathBuf {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join(r"..\..\..\lib\PdfPig\src\UglyToad.PdfPig.Tests\Fonts\TrueType\PMingLiU.ttf");
        assert!(
            path.is_file(),
            "test CJK font fixture should exist at {}",
            path.display()
        );
        path
    }

    fn minimal_longdoc_test_pdf_with_pages(page_texts: &[&str]) -> Vec<u8> {
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
        let mut objects = Vec::new();
        let page_object_numbers = (0..streams.len())
            .map(|index| 4 + index * 2)
            .collect::<Vec<_>>();
        let kids = page_object_numbers
            .iter()
            .map(|object_number| format!("{object_number} 0 R"))
            .collect::<Vec<_>>()
            .join(" ");

        objects.push("<< /Type /Catalog /Pages 2 0 R >>".to_string());
        objects.push(format!(
            "<< /Type /Pages /Kids [{kids}] /Count {} >>",
            streams.len()
        ));
        objects.push("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".to_string());

        for (index, stream) in streams.iter().enumerate() {
            let page_object_number = 4 + index * 2;
            let content_object_number = page_object_number + 1;
            objects.push(format!(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {content_object_number} 0 R >>"
            ));
            objects.push(format!(
                "<< /Length {} >>\nstream\n{}\nendstream",
                stream.len(),
                stream
            ));
        }

        let mut pdf = b"%PDF-1.4\n".to_vec();
        let mut offsets = Vec::with_capacity(objects.len());
        for (index, object) in objects.iter().enumerate() {
            offsets.push(pdf.len());
            pdf.extend_from_slice(format!("{} 0 obj\n{}\nendobj\n", index + 1, object).as_bytes());
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

    struct TestFoundryEndpointResolver {
        endpoint: Option<String>,
        calls: usize,
    }

    impl TestFoundryEndpointResolver {
        fn new(endpoint: Option<String>) -> Self {
            Self { endpoint, calls: 0 }
        }
    }

    impl FoundryLocalEndpointResolver for TestFoundryEndpointResolver {
        fn resolve_chat_completions_endpoint(
            &mut self,
        ) -> Result<Option<String>, crate::openai_compatible::OpenAiExecutionError> {
            self.calls += 1;
            Ok(self.endpoint.clone())
        }
    }

    #[derive(Clone, Default)]
    struct FallbackRetryTranslator {
        calls: Arc<Mutex<Vec<String>>>,
    }

    impl FallbackRetryTranslator {
        fn calls(&self) -> Vec<String> {
            self.calls.lock().expect("calls lock").clone()
        }
    }

    impl NativeLongDocumentTranslator for FallbackRetryTranslator {
        fn translate_chunk(
            &mut self,
            request: QuickTranslateServiceRequest,
        ) -> Result<String, LongDocumentBackendError> {
            let mut calls = self.calls.lock().expect("calls lock");
            calls.push(request.params.text.clone());
            if calls.len() < 3 {
                return Err(LongDocumentBackendError::new("network error"));
            }

            Ok(format!("translated:{}", request.params.text))
        }
    }

    struct RecordingPdfOcrRenderer {
        calls: Vec<RecordingPdfOcrRenderCall>,
        pages: Vec<NativePdfOcrPage>,
    }

    struct RecordingPdfOcrRenderCall {
        path: String,
        page_range: Option<String>,
        output_dir: PathBuf,
    }

    impl RecordingPdfOcrRenderer {
        fn new(pages: impl IntoIterator<Item = NativePdfOcrPage>) -> Self {
            Self {
                calls: Vec::new(),
                pages: pages.into_iter().collect(),
            }
        }
    }

    impl NativePdfOcrPageRenderer for RecordingPdfOcrRenderer {
        fn render_pages_to_bgra(
            &mut self,
            path: &str,
            page_range: Option<&str>,
            output_dir: &Path,
        ) -> Result<Vec<NativePdfOcrPage>, LongDocumentBackendError> {
            self.calls.push(RecordingPdfOcrRenderCall {
                path: path.to_string(),
                page_range: page_range.map(str::to_string),
                output_dir: output_dir.to_path_buf(),
            });
            Ok(self.pages.clone())
        }
    }

    struct RecordingPdfOcrBackend {
        configure_calls: Vec<SettingsSnapshot>,
        recognize_calls: Vec<OcrRecognizeParams>,
        responses: VecDeque<Result<OcrResultDto, crate::ocr::OcrBackendError>>,
    }

    impl RecordingPdfOcrBackend {
        fn with_responses(
            responses: impl IntoIterator<Item = Result<OcrResultDto, crate::ocr::OcrBackendError>>,
        ) -> Self {
            Self {
                configure_calls: Vec::new(),
                recognize_calls: Vec::new(),
                responses: responses.into_iter().collect(),
            }
        }
    }

    impl OcrBackend for RecordingPdfOcrBackend {
        fn configure(
            &mut self,
            settings: &SettingsSnapshot,
        ) -> Result<(), crate::ocr::OcrBackendError> {
            self.configure_calls.push(settings.clone());
            Ok(())
        }

        fn recognize(
            &mut self,
            params: &OcrRecognizeParams,
        ) -> Result<OcrResultDto, crate::ocr::OcrBackendError> {
            self.recognize_calls.push(params.clone());
            self.responses
                .pop_front()
                .unwrap_or_else(|| Err(crate::ocr::OcrBackendError::new("missing OCR response")))
        }
    }
}

fn split_native_text_document(text: &str, input_kind: NativeTextInputKind) -> Vec<String> {
    let chunks = match input_kind {
        NativeTextInputKind::PlainText | NativeTextInputKind::PdfText => {
            TextSplitter::new(NATIVE_TEXT_CHUNK_CHAR_LIMIT)
                .chunks(text)
                .collect::<Vec<_>>()
        }
        NativeTextInputKind::Markdown => MarkdownSplitter::new(NATIVE_TEXT_CHUNK_CHAR_LIMIT)
            .chunks(text)
            .collect::<Vec<_>>(),
    };

    chunks
        .into_iter()
        .map(str::trim)
        .filter(|chunk| !chunk.is_empty())
        .map(ToOwned::to_owned)
        .collect()
}

fn native_quick_translate_request_for_chunk(
    request: &LongDocumentServiceRequest,
    text: &str,
) -> Option<QuickTranslateServiceRequest> {
    let service_id = request.params.service_id.trim();
    if service_id.is_empty() || service_id.starts_with("mdx::") {
        return None;
    }

    let descriptor = find_translation_service_descriptor(service_id)?;
    if !long_document_service_kind_is_supported(descriptor.kind) {
        return None;
    }

    let service = QuickTranslateService {
        id: service_id.to_string(),
        name: descriptor.display_name.to_string(),
        enabled_query: true,
        grammar_capable: descriptor.grammar_capable,
        streaming_capable: descriptor.streaming_capable,
    };

    Some(QuickTranslateServiceRequest {
        query_id: request.query_id,
        query_mode: QuickQueryMode::Translation,
        execution_kind: if service.streaming_capable {
            QuickTranslateExecutionKind::TranslateStream
        } else {
            QuickTranslateExecutionKind::Translate
        },
        params: TranslateParams {
            text: text.to_string(),
            from: Some(document_language_to_quick_code(&request.params.from).to_string()),
            to: Some(document_language_to_quick_code(&request.params.to).to_string()),
            services: Some(vec![service.id.clone()]),
            custom_prompt: request.settings.long_doc_custom_prompt.clone(),
        },
        grammar_params: None,
        settings: request.settings.clone(),
        service,
    })
}

pub fn long_document_supported_service_descriptors() -> Vec<TranslationServiceDescriptor> {
    default_translation_service_descriptors()
        .into_iter()
        .filter(|descriptor| long_document_service_kind_is_supported(descriptor.kind))
        .collect()
}

pub fn long_document_service_kind_is_supported(kind: TranslationServiceKind) -> bool {
    !matches!(
        kind,
        TranslationServiceKind::Dictionary | TranslationServiceKind::ImportedMdx
    )
}

fn document_language_to_quick_code(language: &str) -> &'static str {
    match language.trim().to_ascii_lowercase().as_str() {
        "" | "auto" => "auto",
        "simplifiedchinese" | "chinesesimplified" => "zh",
        "traditionalchinese" | "chinesetraditional" => "zh-tw",
        "classicalchinese" | "chineseclassical" => "zh-classical",
        "arabic" => "ar",
        "bengali" => "bn",
        "bulgarian" => "bg",
        "czech" => "cs",
        "danish" => "da",
        "dutch" => "nl",
        "estonian" => "et",
        "finnish" => "fi",
        "german" => "de",
        "greek" => "el",
        "english" => "en",
        "spanish" => "es",
        "persian" => "fa",
        "french" => "fr",
        "hebrew" => "he",
        "hindi" => "hi",
        "hungarian" => "hu",
        "indonesian" => "id",
        "italian" => "it",
        "japanese" => "ja",
        "korean" => "ko",
        "latvian" => "lv",
        "lithuanian" => "lt",
        "malay" => "ms",
        "norwegian" => "no",
        "polish" => "pl",
        "portuguese" => "pt",
        "romanian" => "ro",
        "russian" => "ru",
        "slovak" => "sk",
        "slovenian" => "sl",
        "swedish" => "sv",
        "tamil" => "ta",
        "telugu" => "te",
        "thai" => "th",
        "filipino" => "tl",
        "turkish" => "tr",
        "ukrainian" => "uk",
        "urdu" => "ur",
        "vietnamese" => "vi",
        "ar" => "ar",
        "bn" => "bn",
        "bg" => "bg",
        "cs" => "cs",
        "da" => "da",
        "nl" => "nl",
        "et" => "et",
        "fi" => "fi",
        "de" => "de",
        "el" => "el",
        "en" => "en",
        "es" => "es",
        "fa" => "fa",
        "fr" => "fr",
        "he" | "iw" => "he",
        "hi" => "hi",
        "hu" => "hu",
        "id" => "id",
        "it" => "it",
        "ja" => "ja",
        "ko" => "ko",
        "lv" => "lv",
        "lt" => "lt",
        "ms" => "ms",
        "no" | "nb" => "no",
        "pl" => "pl",
        "pt" => "pt",
        "ro" => "ro",
        "ru" => "ru",
        "sk" => "sk",
        "sl" => "sl",
        "sv" => "sv",
        "ta" => "ta",
        "te" => "te",
        "th" => "th",
        "tl" | "fil" => "tl",
        "tr" => "tr",
        "uk" => "uk",
        "ur" => "ur",
        "vi" => "vi",
        "zh" => "zh",
        "zh-tw" => "zh-tw",
        "zh-classical" => "zh-classical",
        _ => "auto",
    }
}

fn export_native_text_document(
    request: &LongDocumentServiceRequest,
    input_kind: NativeTextInputKind,
    source_chunks: &[NativeTextSourceChunk],
    translations: &[Option<String>],
) -> Result<NativeTextExport, LongDocumentBackendError> {
    let checkpoint = native_export_checkpoint(input_kind, source_chunks, translations);
    let monolingual = compose_native_monolingual_document(input_kind, &checkpoint);
    let bilingual = compose_native_bilingual_document(input_kind, &checkpoint);

    if matches!(input_kind, NativeTextInputKind::PdfText) {
        if let Some(export) =
            try_export_native_pdf_document(request, source_chunks, translations, &bilingual)?
        {
            return Ok(export);
        }
    }

    let output_path = resolve_native_output_path(&request.params, input_kind);
    ensure_native_output_parent(&output_path)?;

    match request.params.output_mode.as_str() {
        "Bilingual" => {
            let bilingual_path = build_bilingual_output_path(&output_path);
            fs::write(&bilingual_path, bilingual).map_err(native_write_error)?;
            Ok(NativeTextExport {
                output_path: bilingual_path.display().to_string(),
                bilingual_output_path: Some(bilingual_path.display().to_string()),
            })
        }
        "Both" => {
            fs::write(&output_path, monolingual).map_err(native_write_error)?;
            let bilingual_path = build_bilingual_output_path(&output_path);
            fs::write(&bilingual_path, bilingual).map_err(native_write_error)?;
            Ok(NativeTextExport {
                output_path: output_path.display().to_string(),
                bilingual_output_path: Some(bilingual_path.display().to_string()),
            })
        }
        _ => {
            fs::write(&output_path, monolingual).map_err(native_write_error)?;
            Ok(NativeTextExport {
                output_path: output_path.display().to_string(),
                bilingual_output_path: None,
            })
        }
    }
}

fn try_export_native_pdf_document(
    request: &LongDocumentServiceRequest,
    source_chunks: &[NativeTextSourceChunk],
    translations: &[Option<String>],
    bilingual_text: &str,
) -> Result<Option<NativeTextExport>, LongDocumentBackendError> {
    if request.params.output_mode == "Bilingual" {
        return Ok(None);
    }
    if !native_pdf_chunks_support_content_stream_export(source_chunks) {
        return Ok(None);
    }

    let Some(input_path) = native_pdf_input_path(request) else {
        return Ok(None);
    };
    let output_path = resolve_native_pdf_output_path(&request.params);
    if !path_extension_is(&output_path, "pdf") {
        return Ok(None);
    }

    ensure_native_output_parent(&output_path)?;
    let checkpoint = native_pdf_export_checkpoint(source_chunks, translations);
    let selected_page_numbers = native_pdf_selected_page_numbers(request, source_chunks);
    match export_pdf_with_content_stream_replacement(
        input_path,
        &output_path,
        &checkpoint,
        selected_page_numbers.as_deref(),
    ) {
        Ok(_) => {}
        Err(error) if error.kind == NativePdfContentStreamExportFailureKind::NeedsFontEmbedding => {
            if export_pdf_with_overlay_text_blocks(
                request,
                input_path,
                &output_path,
                &checkpoint,
                selected_page_numbers.as_deref(),
            )
            .is_err()
            {
                return Ok(None);
            }
        }
        Err(_) => {
            return Ok(None);
        }
    }

    let bilingual_output_path = if request.params.output_mode == "Both" {
        let bilingual_path = native_pdf_bilingual_text_output_path(&output_path);
        ensure_native_output_parent(&bilingual_path)?;
        fs::write(&bilingual_path, bilingual_text).map_err(native_write_error)?;
        Some(bilingual_path.display().to_string())
    } else {
        None
    };

    Ok(Some(NativeTextExport {
        output_path: output_path.display().to_string(),
        bilingual_output_path,
    }))
}

fn export_pdf_with_overlay_text_blocks(
    request: &LongDocumentServiceRequest,
    input_path: &str,
    output_path: &Path,
    checkpoint: &PdfExportCheckpoint,
    selected_page_numbers: Option<&[u32]>,
) -> Result<PdfOverlaySummary, String> {
    let font_path = native_pdf_overlay_font_path(request)?;
    let blocks = native_pdf_overlay_blocks(checkpoint)?;
    overlay_pdf_text_blocks(&PdfOverlayOptions {
        source_pdf: PathBuf::from(input_path),
        output_pdf: output_path.to_path_buf(),
        font_path,
        blocks,
        selected_page_numbers: selected_page_numbers.map(|pages| pages.to_vec()),
    })
    .map_err(|error| format!("Could not write PDF overlay text blocks: {error}"))
}

fn native_pdf_overlay_font_path(request: &LongDocumentServiceRequest) -> Result<PathBuf, String> {
    if let Some(font_path) = request
        .settings
        .cjk_font_path
        .as_deref()
        .map(str::trim)
        .filter(|path| !path.is_empty())
    {
        let font_path = PathBuf::from(font_path);
        if font_path.is_file() {
            return Ok(font_path);
        }

        return Err(format!(
            "configured CJK PDF overlay font '{}' is not a readable file",
            font_path.display()
        ));
    }

    let target_language =
        TranslationLanguage::from_code(document_language_to_quick_code(&request.params.to));
    crate::font_download::cached_font_path(target_language).ok_or_else(|| {
        format!(
            "no cached CJK PDF overlay font is available for target language '{}'",
            request.params.to
        )
    })
}

fn native_pdf_overlay_blocks(
    checkpoint: &PdfExportCheckpoint,
) -> Result<Vec<NativePdfOverlayBlock>, String> {
    let blocks = build_pdf_overlay_blocks(checkpoint);
    if blocks.is_empty() {
        return Err("no PDF overlay blocks are available for font embedding export".to_string());
    }

    blocks
        .into_iter()
        .map(|block| {
            let page_number = u32::try_from(block.page_number).map_err(|_| {
                format!(
                    "PDF overlay block '{}' has invalid page number {}",
                    block.source_block_id, block.page_number
                )
            })?;
            let mut native_block = NativePdfOverlayBlock::new(
                page_number,
                NativePdfOverlayRect::new(
                    native_pdf_overlay_f32(block.rect.x, &block.source_block_id, "x")?,
                    native_pdf_overlay_f32(block.rect.y, &block.source_block_id, "y")?,
                    native_pdf_overlay_f32(block.rect.width, &block.source_block_id, "width")?,
                    native_pdf_overlay_f32(block.rect.height, &block.source_block_id, "height")?,
                ),
                block.text,
                native_pdf_overlay_f32(block.font_size, &block.source_block_id, "font size")?,
            );
            if let Some(line_spacing) = block
                .text_style
                .and_then(|style| (style.line_spacing > 0.0).then_some(style.line_spacing))
            {
                native_block.line_height =
                    native_pdf_overlay_f32(line_spacing, &block.source_block_id, "line height")?;
            }
            Ok(native_block)
        })
        .collect()
}

fn native_pdf_overlay_f32(value: f64, source_block_id: &str, label: &str) -> Result<f32, String> {
    let value = value as f32;
    if value.is_finite() {
        Ok(value)
    } else {
        Err(format!(
            "PDF overlay block '{source_block_id}' has non-finite {label}"
        ))
    }
}

fn native_pdf_chunks_support_content_stream_export(
    source_chunks: &[NativeTextSourceChunk],
) -> bool {
    source_chunks
        .iter()
        .all(|chunk| chunk.source_kind != NativeTextSourceKind::PdfOcr)
}

fn ensure_native_output_parent(output_path: &Path) -> Result<(), LongDocumentBackendError> {
    if let Some(parent) = output_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    {
        fs::create_dir_all(parent).map_err(|error| {
            LongDocumentBackendError::new(format!(
                "Could not create long document output folder '{}': {error}",
                parent.display()
            ))
        })?;
    }

    Ok(())
}

fn native_export_checkpoint(
    input_kind: NativeTextInputKind,
    source_chunks: &[NativeTextSourceChunk],
    translations: &[Option<String>],
) -> LongDocumentExportCheckpoint {
    LongDocumentExportCheckpoint {
        source_chunks: source_chunks
            .iter()
            .map(|chunk| chunk.text.clone())
            .collect(),
        chunk_metadata: source_chunks
            .iter()
            .enumerate()
            .map(|(index, chunk)| LongDocumentExportChunkMetadata {
                chunk_index: index,
                page_number: chunk.page_number as i32,
                source_block_type: native_export_block_type(input_kind, &chunk.text),
                order_in_page: native_order_in_page(index, chunk, input_kind),
            })
            .collect(),
        translated_chunks: translations
            .iter()
            .enumerate()
            .filter_map(|(index, translated)| translated.as_ref().map(|text| (index, text.clone())))
            .collect::<BTreeMap<_, _>>(),
        failed_chunk_indexes: translations
            .iter()
            .enumerate()
            .filter_map(|(index, translated)| translated.is_none().then_some(index))
            .collect::<BTreeSet<_>>(),
    }
}

fn native_pdf_export_checkpoint(
    source_chunks: &[NativeTextSourceChunk],
    translations: &[Option<String>],
) -> PdfExportCheckpoint {
    PdfExportCheckpoint {
        source_chunks: source_chunks
            .iter()
            .map(|chunk| chunk.text.clone())
            .collect(),
        chunk_metadata: source_chunks
            .iter()
            .enumerate()
            .map(|(index, chunk)| native_pdf_export_chunk_metadata(index, chunk))
            .collect(),
        translated_chunks: translations
            .iter()
            .enumerate()
            .filter_map(|(index, translated)| translated.as_ref().map(|text| (index, text.clone())))
            .collect::<BTreeMap<_, _>>(),
        failed_chunk_indexes: translations
            .iter()
            .enumerate()
            .filter_map(|(index, translated)| translated.is_none().then_some(index))
            .collect::<BTreeSet<_>>(),
    }
}

fn native_pdf_export_chunk_metadata(
    index: usize,
    chunk: &NativeTextSourceChunk,
) -> PdfExportChunkMetadata {
    let mut metadata = chunk
        .pdf_export_metadata
        .clone()
        .unwrap_or_else(|| fallback_pdf_export_chunk_metadata(index, chunk));
    metadata.chunk_index = index;

    if metadata.source_block_type == PdfExportSourceBlockType::Formula {
        metadata.translation_skipped = true;
        metadata.preserve_original_text_in_pdf_export = true;
    }

    metadata
}

fn fallback_pdf_export_chunk_metadata(
    index: usize,
    chunk: &NativeTextSourceChunk,
) -> PdfExportChunkMetadata {
    PdfExportChunkMetadata {
        chunk_index: index,
        page_number: chunk.page_number as i32,
        source_block_id: chunk.source_block_id.clone(),
        source_block_type: PdfExportSourceBlockType::Paragraph,
        order_in_page: native_order_in_page(index, chunk, NativeTextInputKind::PdfText),
        reading_order_score: 1.0,
        bounding_box: None,
        text_style: None,
        translation_skipped: false,
        preserve_original_text_in_pdf_export: false,
        retry_count: 0,
        fallback_text: chunk.fallback_text.clone(),
        detected_font_names: chunk
            .pdf_context
            .as_ref()
            .and_then(|context| context.detected_font_names.clone()),
    }
}

fn native_order_in_page(
    fallback_index: usize,
    chunk: &NativeTextSourceChunk,
    input_kind: NativeTextInputKind,
) -> i32 {
    if matches!(input_kind, NativeTextInputKind::PdfText) {
        if let Some(index) = chunk
            .source_block_id
            .rsplit('-')
            .next()
            .and_then(parse_native_source_block_order_suffix)
        {
            return index.saturating_sub(1);
        }
    }

    fallback_index as i32
}

fn parse_native_source_block_order_suffix(value: &str) -> Option<i32> {
    value
        .trim()
        .trim_start_matches('b')
        .trim_start_matches('B')
        .parse::<i32>()
        .ok()
}

fn native_export_block_type(
    input_kind: NativeTextInputKind,
    source: &str,
) -> LongDocumentExportBlockType {
    if !matches!(input_kind, NativeTextInputKind::Markdown) {
        return LongDocumentExportBlockType::Paragraph;
    }

    if source.trim_start().starts_with('#') {
        LongDocumentExportBlockType::Heading
    } else {
        LongDocumentExportBlockType::Paragraph
    }
}

fn compose_native_monolingual_document(
    input_kind: NativeTextInputKind,
    checkpoint: &LongDocumentExportCheckpoint,
) -> String {
    match input_kind {
        NativeTextInputKind::Markdown => compose_monolingual_markdown(checkpoint),
        NativeTextInputKind::PlainText | NativeTextInputKind::PdfText => {
            compose_monolingual_text(checkpoint)
        }
    }
}

fn compose_native_bilingual_document(
    input_kind: NativeTextInputKind,
    checkpoint: &LongDocumentExportCheckpoint,
) -> String {
    match input_kind {
        NativeTextInputKind::Markdown => compose_bilingual_markdown(checkpoint),
        NativeTextInputKind::PlainText | NativeTextInputKind::PdfText => {
            compose_bilingual_text(checkpoint)
        }
    }
}

fn resolve_native_output_path(
    params: &TranslateDocumentParams,
    input_kind: NativeTextInputKind,
) -> PathBuf {
    if let Some(output_path) = params
        .output_path
        .as_deref()
        .map(str::trim)
        .filter(|path| !path.is_empty())
    {
        return coerce_native_output_path(PathBuf::from(output_path), input_kind);
    }

    let input_path = params.input_path.trim();
    if !input_path.is_empty() {
        let extension = native_text_output_extension(input_kind);
        let parent = Path::new(input_path)
            .parent()
            .unwrap_or_else(|| Path::new("."));
        let stem = Path::new(input_path)
            .file_stem()
            .and_then(|stem| stem.to_str())
            .filter(|stem| !stem.trim().is_empty())
            .unwrap_or("translated");
        return parent.join(format!("{stem}.translated{extension}"));
    }

    temp_output_path(&params.input_mode)
}

fn resolve_native_pdf_output_path(params: &TranslateDocumentParams) -> PathBuf {
    if let Some(output_path) = params
        .output_path
        .as_deref()
        .map(str::trim)
        .filter(|path| !path.is_empty())
    {
        return PathBuf::from(output_path);
    }

    let input_path = params.input_path.trim();
    if !input_path.is_empty() {
        let parent = Path::new(input_path)
            .parent()
            .unwrap_or_else(|| Path::new("."));
        let stem = Path::new(input_path)
            .file_stem()
            .and_then(|stem| stem.to_str())
            .filter(|stem| !stem.trim().is_empty())
            .unwrap_or("translated");
        return parent.join(format!("{stem}.translated.pdf"));
    }

    let mut output_path = temp_output_path(&params.input_mode);
    output_path.set_extension("pdf");
    output_path
}

fn coerce_native_output_path(mut output_path: PathBuf, input_kind: NativeTextInputKind) -> PathBuf {
    if matches!(input_kind, NativeTextInputKind::PdfText)
        && output_path
            .extension()
            .and_then(|extension| extension.to_str())
            .is_some_and(|extension| extension.eq_ignore_ascii_case("pdf"))
    {
        output_path.set_extension("txt");
    }

    output_path
}

fn native_pdf_input_path(request: &LongDocumentServiceRequest) -> Option<&str> {
    match &request.input {
        LongDocumentInput::File(path) => Some(path.as_str()),
        LongDocumentInput::InlineText(_) => Some(request.params.input_path.as_str()),
    }
    .map(str::trim)
    .filter(|path| !path.is_empty())
}

fn native_pdf_selected_page_numbers(
    request: &LongDocumentServiceRequest,
    source_chunks: &[NativeTextSourceChunk],
) -> Option<Vec<u32>> {
    let page_range = request.params.page_range.as_deref()?.trim();
    if page_range.is_empty() || page_range.eq_ignore_ascii_case("all") {
        return None;
    }

    let pages = source_chunks
        .iter()
        .map(|chunk| chunk.page_number)
        .filter(|page| *page > 0)
        .collect::<BTreeSet<_>>();
    (!pages.is_empty()).then(|| pages.into_iter().collect())
}

fn native_pdf_bilingual_text_output_path(monolingual_pdf_path: &Path) -> PathBuf {
    let mut output_path = build_bilingual_output_path(monolingual_pdf_path);
    output_path.set_extension("txt");
    output_path
}

fn path_extension_is(path: &Path, expected_extension: &str) -> bool {
    path.extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case(expected_extension))
}

fn native_text_output_extension(input_kind: NativeTextInputKind) -> &'static str {
    match input_kind {
        NativeTextInputKind::Markdown => ".md",
        NativeTextInputKind::PlainText | NativeTextInputKind::PdfText => ".txt",
    }
}

fn native_write_error(error: std::io::Error) -> LongDocumentBackendError {
    LongDocumentBackendError::new(format!("Could not write long document output: {error}"))
}

fn chunk_preview(chunk: &str) -> String {
    chunk
        .split_whitespace()
        .take(12)
        .collect::<Vec<_>>()
        .join(" ")
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

fn long_document_backend_error_outcome(
    request: LongDocumentServiceRequest,
    error: LongDocumentBackendError,
) -> LongDocumentOutcome {
    LongDocumentOutcome {
        query_id: request.query_id,
        input_label: input_label(&request),
        events: Vec::new(),
        result: Err(error),
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
    let uses_foundry_profile =
        uses_foundry_local_long_document_profile(&state.long_document.service, &settings);
    settings.long_doc_max_concurrency = if uses_foundry_profile {
        Some(1)
    } else {
        parse_concurrency(&state.long_document.concurrency)
    };
    settings.long_doc_enable_document_context_pass =
        Some(state.long_document.two_pass_context && !uses_foundry_profile);
    settings
}

fn uses_foundry_local_long_document_profile(service_id: &str, settings: &SettingsSnapshot) -> bool {
    if !map_long_document_service_id(service_id).eq_ignore_ascii_case("windows-local-ai") {
        return false;
    }

    matches!(
        settings
            .local_ai_provider
            .as_deref()
            .unwrap_or(local_ai_provider_modes::AUTO)
            .trim()
            .to_ascii_lowercase()
            .as_str(),
        "" | "auto" | "foundrylocal" | "foundry-local"
    )
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

fn resolve_file_input_mode(path: &str, configured: &str) -> &'static str {
    let configured = map_input_mode(configured);
    if configured != "Pdf" {
        return configured;
    }

    file_input_mode_for_path(path).unwrap_or(configured)
}

fn file_input_mode_for_path(path: &str) -> Option<&'static str> {
    let extension = Path::new(path)
        .extension()
        .and_then(|extension| extension.to_str())
        .map(str::to_ascii_lowercase)
        .or_else(|| {
            path.rsplit_once('.')
                .map(|(_, extension)| extension.to_ascii_lowercase())
        })?;

    match extension.as_str() {
        "md" | "markdown" => Some("Markdown"),
        "txt" | "text" => Some("PlainText"),
        "pdf" => Some("Pdf"),
        _ => None,
    }
}

fn map_output_mode(value: &str) -> &'static str {
    match value.trim().to_ascii_lowercase().as_str() {
        "bilingual" | "dual" => "Bilingual",
        "both" => "Both",
        _ => "Monolingual",
    }
}

fn map_long_document_service_id(value: &str) -> &str {
    match value.trim().to_ascii_lowercase().as_str() {
        "local-ai" | "foundry-local" | "foundrylocal" => "windows-local-ai",
        _ => value.trim(),
    }
}

fn is_local_ai_long_document_service_id(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "windows-local-ai" | "local-ai" | "foundry-local" | "foundrylocal"
    )
}

fn map_document_language(value: &str) -> &'static str {
    match value.trim().to_ascii_lowercase().as_str() {
        "" | "auto" => "Auto",
        "zh" | "zh-cn" | "zh-hans" | "simplifiedchinese" | "chinesesimplified" => {
            "SimplifiedChinese"
        }
        "zh-tw" | "zh-hant" | "traditionalchinese" | "chinesetraditional" => "TraditionalChinese",
        "zh-classical" | "classicalchinese" | "chineseclassical" => "ClassicalChinese",
        "ar" | "ar-sa" => "Arabic",
        "bn" | "bn-in" => "Bengali",
        "bg" | "bg-bg" => "Bulgarian",
        "cs" | "cs-cz" => "Czech",
        "da" | "da-dk" => "Danish",
        "nl" | "nl-nl" => "Dutch",
        "et" | "et-ee" => "Estonian",
        "fi" | "fi-fi" => "Finnish",
        "de" | "de-de" => "German",
        "el" | "el-gr" => "Greek",
        "en" | "en-us" => "English",
        "es" | "es-es" => "Spanish",
        "fa" | "fa-ir" => "Persian",
        "fr" | "fr-fr" => "French",
        "he" | "iw" | "he-il" => "Hebrew",
        "hi" | "hi-in" => "Hindi",
        "hu" | "hu-hu" => "Hungarian",
        "id" | "id-id" => "Indonesian",
        "it" | "it-it" => "Italian",
        "ja" | "ja-jp" => "Japanese",
        "ko" | "ko-kr" => "Korean",
        "lv" | "lv-lv" => "Latvian",
        "lt" | "lt-lt" => "Lithuanian",
        "ms" | "ms-my" => "Malay",
        "no" | "nb" | "nb-no" | "no-no" => "Norwegian",
        "pl" | "pl-pl" => "Polish",
        "pt" | "pt-br" | "pt-pt" => "Portuguese",
        "ro" | "ro-ro" => "Romanian",
        "ru" | "ru-ru" => "Russian",
        "sk" | "sk-sk" => "Slovak",
        "sl" | "sl-si" => "Slovenian",
        "sv" | "sv-se" => "Swedish",
        "ta" | "ta-in" => "Tamil",
        "te" | "te-in" => "Telugu",
        "th" | "th-th" => "Thai",
        "tl" | "fil" | "fil-ph" => "Filipino",
        "tr" | "tr-tr" => "Turkish",
        "uk" | "uk-ua" => "Ukrainian",
        "ur" | "ur-pk" => "Urdu",
        "vi" | "vi-vn" => "Vietnamese",
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

fn temp_output_path(input_mode: &str) -> PathBuf {
    let extension = match input_mode {
        "Markdown" => "md",
        _ => "txt",
    };
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!("easydict-longdoc-output-{stamp}.{extension}"))
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
