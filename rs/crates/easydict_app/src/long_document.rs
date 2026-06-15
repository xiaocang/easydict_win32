#[cfg(feature = "retained-dotnet-workers")]
use crate::compat_client::DirectWorkerFacade;
#[cfg(feature = "retained-dotnet-workers")]
use crate::compat_protocol::{worker_events, ConfigureParams, IpcEvent};
use crate::content_preservation::{
    analyze_formula_preservation, protect_formula_block, resolve_formula_fallback,
    restore_formula_block, BlockContext, PreservationMode, ProtectedBlock, ProtectionPlan,
    RestoreOutcome, RestoreStatus, SoftValidationStatus,
};
use crate::doc_layout_yolo::{DocLayoutRegionType, DocLayoutYoloDetection};
use crate::doc_layout_yolo_onnx::DocLayoutYoloOnnxSession;
use crate::formula_protection::SoftProtectionWrapperKind;
use crate::layout_model_download::{
    default_model_cache_dir, ensure_layout_model_available_for_directory,
    ensure_tatr_model_available_for_directory, LayoutModelDownloadConfig, LayoutModelPaths,
    DOC_LAYOUT_MODEL_FILE_NAME, ONNX_RUNTIME_FILE_NAME, TATR_MODEL_FILE_NAME,
};
use crate::long_document_context::{
    apply_preservation_hints, merge_page_partials, try_parse_page_partial, DocumentBlockIr,
    DocumentContext, DocumentIr, PagePartial,
};
use crate::long_document_export::{
    build_bilingual_output_path, compose_bilingual_markdown, compose_bilingual_text,
    compose_monolingual_markdown, compose_monolingual_text, LongDocumentExportBlockType,
    LongDocumentExportCheckpoint, LongDocumentExportChunkMetadata,
};
use crate::ocr::{
    merged_ocr_text, NativeOcrBackend, OcrBackend, OcrRecognizeParams, OcrResultDto,
    ReqwestOcrHttpClient,
};
use crate::openai_compatible::{
    default_foundry_local_runtime_controller, FoundryLocalRuntimeController,
    OpenAiCompatibleConfig, OpenAiExecutionError,
};
#[cfg(test)]
use crate::openai_compatible::{
    FoundryLocalEndpointResolver, FoundryLocalError, FoundryLocalRuntimeState,
    FoundryLocalRuntimeStatus,
};
use crate::pdf_content_stream::extract_pdf_literal_strings;
use crate::pdf_export_blocks::{
    build_pdf_overlay_blocks, PdfExportCheckpoint, PdfExportChunkMetadata, PdfExportSourceBlockType,
};
use crate::pdf_native_export::{
    export_pdf_with_content_stream_replacement, NativePdfContentStreamExportFailureKind,
    NativePdfContentStreamExportSummary,
};
use crate::pdf_source_extraction::{
    block_context_for_pdf_source_block, pdf_export_chunk_metadata_for_source_block,
    pdf_source_block_id, pdf_source_document_from_text_summary,
    pdf_source_document_with_doc_layout_yolo_detections,
    pdf_source_document_with_tatr_table_structures, PdfSourceBlock, PdfSourceDocument,
    PdfSourcePageLayoutDetections, PdfSourcePageTableStructures,
};
use crate::protocol::{
    local_ai_provider_modes, normalize_local_ai_provider_mode, BlockTranslatedEventData,
    ProgressEventData, SettingsSnapshot, StatusEventData, TranslateDocumentParams,
    TranslateDocumentResult, TranslateParams,
};
#[cfg(test)]
use crate::quick_translate::auto_windows_ai_native_probe_status;
use crate::quick_translate::{
    auto_foundry_local_native_probe_request_result, auto_openvino_native_fallback_request,
    local_ai_quick_translate_native_preflight_error, quick_translate_request_can_route_natively,
    run_quick_translate_service_with_native_route, QuickQueryMode, QuickTranslateExecutionKind,
    QuickTranslateService, QuickTranslateServiceRequest,
};
use crate::resource_download::ReqwestResourceDownloadClient;
use crate::runtime_policy::RuntimeRoutePolicy;
use crate::state::{EasydictUiState, SettingsState, TranslationResultPreview};
use crate::table_structure::TableStructure;
use crate::table_structure_onnx::{TatrOnnxError, TatrOnnxSession};
use crate::translation_cache::{
    long_document_source_hash, long_document_translation_cache_path, LongDocumentTranslationCache,
};
use crate::translation_language::TranslationLanguage;
use crate::translation_services::{
    default_translation_service_descriptors, find_translation_service_descriptor,
    TranslationServiceDescriptor, TranslationServiceKind,
};
use crate::vision_layout::{
    execute_vision_layout_detection, ReqwestVisionLayoutHttpClient, VisionLayoutDetection,
    VisionLayoutRegionType,
};
use easydict_pdf_overlay::{
    overlay_pdf_text_blocks, PdfOverlayBlock as NativePdfOverlayBlock, PdfOverlayOptions,
    PdfOverlayRect as NativePdfOverlayRect, PdfOverlaySummary,
};
use easydict_pdf_render::{
    extract_pdf_text_chars, render_pdf_pages_to_bgra_files, PdfTextExtractionOptions,
    PdfToBgraOptions,
};
#[cfg(test)]
use easydict_windows_ai::WindowsAiLanguageModelProbe;
use easydict_windows_ai::{
    default_windows_ai_language_model_client, translate_with_client, windows_ai_status,
    WindowsAiGenerationOptions, WindowsAiLanguage, WindowsAiLanguageModelClient,
    WindowsAiReadyState, WindowsAiResponseStatus, WindowsAiTranslationRequest,
};
use serde::{Deserialize, Serialize};
#[cfg(feature = "retained-dotnet-workers")]
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
const NATIVE_LONG_DOCUMENT_CANCELLED_MESSAGE: &str = "Long document translation was cancelled";
const DEFAULT_LONG_DOCUMENT_REQUEST_TIMEOUT_MS: u32 = 30_000;
const FOUNDRY_LOCAL_LONG_DOCUMENT_REQUEST_TIMEOUT_MS: u32 = 120_000;
const VISION_LAYOUT_GEMINI_OPENAI_ENDPOINT: &str =
    "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
const VISION_LAYOUT_DEFAULT_GEMINI_MODEL: &str = "gemini-2.5-flash";

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

#[cfg(feature = "retained-dotnet-workers")]
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
    MissingRetryCheckpoint,
}

impl fmt::Display for LongDocumentStartError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingInput => {
                formatter.write_str("Select a document or enter text to translate.")
            }
            Self::MissingRetryCheckpoint => formatter.write_str(
                "Retry Failed requires a Rust-native result JSON checkpoint for this document.",
            ),
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

#[derive(Clone)]
pub struct WindowsAiNativeLongDocumentTranslator<C> {
    client: C,
}

impl<C> WindowsAiNativeLongDocumentTranslator<C> {
    pub fn new(client: C) -> Self {
        Self { client }
    }
}

impl<C> NativeLongDocumentTranslator for WindowsAiNativeLongDocumentTranslator<C>
where
    C: WindowsAiLanguageModelClient + Clone + Send,
{
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError> {
        if let Some(prompt) = windows_ai_context_generation_prompt(&request.params) {
            return run_windows_ai_context_generation(&mut self.client, &prompt);
        }

        let translation_request =
            windows_ai_translation_request_from_quick_params(&request.params)?;
        translate_with_client(&mut self.client, &translation_request)
            .map(|outcome| outcome.translated_text)
            .map_err(|error| LongDocumentBackendError::new(error.to_string()))
    }
}

fn windows_ai_context_generation_prompt(params: &TranslateParams) -> Option<String> {
    let custom_prompt = params
        .custom_prompt
        .as_deref()
        .map(str::trim)
        .filter(|prompt| {
            prompt.contains("Do NOT translate the document text")
                || prompt.contains("Merge them into a single 1-3 sentence summary")
        })?;
    let text = params.text.trim();
    if text.is_empty() {
        return None;
    }

    Some(format!("{custom_prompt}\n\n{text}"))
}

fn run_windows_ai_context_generation<C>(
    client: &mut C,
    prompt: &str,
) -> Result<String, LongDocumentBackendError>
where
    C: WindowsAiLanguageModelClient,
{
    let status = windows_ai_status(client);
    if status.ready_state != WindowsAiReadyState::Ready {
        return Err(LongDocumentBackendError::new(status.message));
    }

    let response = client
        .generate(prompt, WindowsAiGenerationOptions::default())
        .map_err(|error| LongDocumentBackendError::new(error.to_string()))?;
    if response.status != WindowsAiResponseStatus::Complete {
        return Err(LongDocumentBackendError::new(
            response
                .error_message
                .unwrap_or_else(|| format!("Phi Silica returned {:?}.", response.status)),
        ));
    }

    Ok(response.text.trim().to_string())
}

pub fn begin_long_document_translate(
    state: &mut EasydictUiState,
) -> Result<LongDocumentServiceRequest, LongDocumentStartError> {
    let request = build_long_document_request(state, state.next_query_id)?;
    state.next_query_id += 1;
    mark_long_document_started(state, &request);
    Ok(request)
}

pub fn begin_long_document_retry_failed(
    state: &mut EasydictUiState,
) -> Result<(LongDocumentServiceRequest, String), LongDocumentStartError> {
    let mut request = build_long_document_request(state, state.next_query_id)?;
    let Some(result_json_path) = retry_result_json_path_for_request(&request) else {
        return Err(LongDocumentStartError::MissingRetryCheckpoint);
    };

    request.params.result_json_path = Some(result_json_path.clone());
    state.next_query_id += 1;
    mark_long_document_started(state, &request);
    Ok((request, result_json_path))
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

    let (vision_endpoint, vision_api_key, vision_model) =
        long_document_vision_layout_params(&state.settings);
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
        vision_endpoint,
        vision_api_key,
        vision_model,
        result_json_path: None,
        request_timeout_ms: Some(long_document_request_timeout_ms(
            &long_doc.service,
            &state.settings,
        )),
    };

    let mut request = LongDocumentServiceRequest {
        query_id,
        input,
        params,
        settings: long_document_settings_snapshot(state),
    };
    ensure_default_result_json_path(&mut request);
    Ok(request)
}

pub fn run_long_document_request_with_current_app_dir(
    request: LongDocumentServiceRequest,
) -> LongDocumentOutcome {
    run_long_document_request_with_current_app_dir_and_worker_policy(
        request,
        RuntimeRoutePolicy::all_disabled(),
    )
}

fn run_long_document_request_with_current_app_dir_and_worker_policy(
    request: LongDocumentServiceRequest,
    worker_policy: RuntimeRoutePolicy,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_route_preflight_error(&request) {
        return long_document_backend_error_outcome(request, error);
    }

    let request = match try_run_native_text_long_document_request(request) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    let mut foundry_resolver = default_foundry_local_runtime_controller();
    let mut windows_ai_client = default_windows_ai_language_model_client();
    let request = match try_run_native_text_long_document_request_with_local_ai_client(
        request,
        &mut windows_ai_client,
        &mut foundry_resolver,
    ) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    if let Some(error) = local_long_document_worker_preflight_error(&request, worker_policy) {
        return long_document_backend_error_outcome(request, error);
    }

    match current_app_dir() {
        Ok(app_dir) => run_long_document_request_with_app_dir_after_native_probe(
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
    let mut foundry_resolver = default_foundry_local_runtime_controller();
    let mut windows_ai_client = default_windows_ai_language_model_client();
    run_long_document_request_with_native_route_and_local_ai_client(
        backend,
        request,
        &mut windows_ai_client,
        &mut foundry_resolver,
    )
}

fn run_long_document_request_with_native_route_and_local_ai_client<
    B: LongDocumentBackend,
    C: WindowsAiLanguageModelClient + Clone + Send,
    R: FoundryLocalRuntimeController,
>(
    backend: &mut B,
    request: LongDocumentServiceRequest,
    windows_ai_client: &mut C,
    foundry_resolver: &mut R,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_route_preflight_error(&request) {
        return long_document_backend_error_outcome(request, error);
    }

    let request = match try_run_native_text_long_document_request(request) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    let request = match try_run_native_text_long_document_request_with_local_ai_client(
        request,
        windows_ai_client,
        foundry_resolver,
    ) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    if let Some(error) =
        local_long_document_worker_preflight_error(&request, RuntimeRoutePolicy::all_disabled())
    {
        return long_document_backend_error_outcome(request, error);
    }

    run_long_document_request(backend, request)
}

#[cfg(test)]
fn run_long_document_request_with_native_route_and_foundry_resolver<
    B: LongDocumentBackend,
    P: WindowsAiLanguageModelProbe,
    R: FoundryLocalRuntimeController,
>(
    backend: &mut B,
    request: LongDocumentServiceRequest,
    windows_ai_probe: &mut P,
    foundry_resolver: &mut R,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_route_preflight_error(&request) {
        return long_document_backend_error_outcome(request, error);
    }

    let request = match try_run_native_text_long_document_request(request) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    let request = match try_run_native_text_long_document_request_with_auto_local_ai_probes(
        request,
        windows_ai_probe,
        foundry_resolver,
    ) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    if let Some(error) =
        local_long_document_worker_preflight_error(&request, RuntimeRoutePolicy::all_disabled())
    {
        return long_document_backend_error_outcome(request, error);
    }

    run_long_document_request(backend, request)
}

pub fn run_long_document_request_with_app_dir(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
) -> LongDocumentOutcome {
    run_long_document_request_with_app_dir_and_worker_policy_internal(
        request,
        app_dir,
        RuntimeRoutePolicy::all_disabled(),
    )
}

#[cfg(feature = "retained-dotnet-workers")]
#[doc(hidden)]
pub fn run_long_document_request_with_packaged_app_dir_and_worker_policy(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
    worker_policy: RuntimeRoutePolicy,
) -> LongDocumentOutcome {
    run_long_document_request_with_app_dir_and_worker_policy_internal(
        request,
        app_dir,
        worker_policy.with_hybrid_runtime_profile_from_environment(),
    )
}

fn run_long_document_request_with_app_dir_and_worker_policy_internal(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
    worker_policy: RuntimeRoutePolicy,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_route_preflight_error(&request) {
        return long_document_backend_error_outcome(request, error);
    }

    let request = match try_run_native_text_long_document_request(request) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    let mut foundry_resolver = default_foundry_local_runtime_controller();
    let mut windows_ai_client = default_windows_ai_language_model_client();
    let request = match try_run_native_text_long_document_request_with_local_ai_client(
        request,
        &mut windows_ai_client,
        &mut foundry_resolver,
    ) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    run_long_document_request_with_app_dir_after_native_probe(request, app_dir, worker_policy)
}

#[doc(hidden)]
pub fn run_long_document_request_with_app_dir_and_native_local_ai_client<
    C: WindowsAiLanguageModelClient + Clone + Send,
    R: FoundryLocalRuntimeController,
>(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
    windows_ai_client: &mut C,
    foundry_resolver: &mut R,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_route_preflight_error(&request) {
        return long_document_backend_error_outcome(request, error);
    }

    let request = match try_run_native_text_long_document_request(request) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    let request = match try_run_native_text_long_document_request_with_local_ai_client(
        request,
        windows_ai_client,
        foundry_resolver,
    ) {
        NativeLongDocumentDispatch::Handled(outcome) => return outcome,
        NativeLongDocumentDispatch::NeedsWorker(request) => request,
    };

    run_long_document_request_with_app_dir_after_native_probe(
        request,
        app_dir,
        RuntimeRoutePolicy::all_disabled(),
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

fn try_run_native_text_long_document_request_with_local_ai_client<C, R>(
    request: LongDocumentServiceRequest,
    windows_ai_client: &mut C,
    foundry_resolver: &mut R,
) -> NativeLongDocumentDispatch
where
    C: WindowsAiLanguageModelClient + Clone + Send,
    R: FoundryLocalRuntimeController,
{
    let Some(probe_request) =
        native_quick_translate_request_for_chunk(&request, "native route probe")
    else {
        return NativeLongDocumentDispatch::NeedsWorker(request);
    };

    if local_ai_long_document_request_uses_explicit_windows_ai(&probe_request) {
        return NativeLongDocumentDispatch::Handled(
            run_native_text_long_document_request_with_windows_ai_client(
                request,
                windows_ai_client,
            ),
        );
    }

    if local_ai_long_document_request_uses_auto_windows_ai(&probe_request) {
        let status = windows_ai_status(windows_ai_client);
        if matches!(status.ready_state, WindowsAiReadyState::Ready) {
            return NativeLongDocumentDispatch::Handled(
                run_native_text_long_document_request_with_windows_ai_client(
                    request,
                    windows_ai_client,
                ),
            );
        }
    }

    try_run_native_text_long_document_request_with_auto_local_ai_fallbacks(
        request,
        &probe_request,
        foundry_resolver,
    )
}

#[cfg(test)]
fn try_run_native_text_long_document_request_with_auto_local_ai_probes<P, R>(
    request: LongDocumentServiceRequest,
    windows_ai_probe: &mut P,
    foundry_resolver: &mut R,
) -> NativeLongDocumentDispatch
where
    P: WindowsAiLanguageModelProbe,
    R: FoundryLocalRuntimeController,
{
    let Some(probe_request) =
        native_quick_translate_request_for_chunk(&request, "native route probe")
    else {
        return NativeLongDocumentDispatch::NeedsWorker(request);
    };

    let _ = auto_windows_ai_native_probe_status(&probe_request, windows_ai_probe);

    try_run_native_text_long_document_request_with_auto_local_ai_fallbacks(
        request,
        &probe_request,
        foundry_resolver,
    )
}

fn try_run_native_text_long_document_request_with_auto_local_ai_fallbacks<R>(
    request: LongDocumentServiceRequest,
    probe_request: &QuickTranslateServiceRequest,
    foundry_resolver: &mut R,
) -> NativeLongDocumentDispatch
where
    R: FoundryLocalRuntimeController,
{
    let native_probe_request =
        match auto_foundry_local_native_probe_request_result(probe_request, foundry_resolver) {
            Ok(Some(native_probe_request)) => Some(native_probe_request),
            Ok(None) => auto_openvino_native_fallback_request(probe_request),
            Err(error) => {
                return NativeLongDocumentDispatch::Handled(long_document_backend_error_outcome(
                    request,
                    LongDocumentBackendError::new(error.to_string()),
                ));
            }
        };

    let Some(native_probe_request) = native_probe_request else {
        return NativeLongDocumentDispatch::NeedsWorker(request);
    };

    let mut native_request = request;
    native_request.settings = native_probe_request.settings;
    try_run_native_text_long_document_request(native_request)
}

fn run_long_document_request_with_app_dir_after_native_probe(
    request: LongDocumentServiceRequest,
    app_dir: impl AsRef<Path>,
    worker_policy: RuntimeRoutePolicy,
) -> LongDocumentOutcome {
    if let Some(error) = local_long_document_worker_preflight_error(&request, worker_policy) {
        return long_document_backend_error_outcome(request, error);
    }

    #[cfg(not(feature = "retained-dotnet-workers"))]
    {
        let _ = app_dir;
        return long_document_backend_error_outcome(
            request,
            LongDocumentBackendError::new(
                RuntimeRoutePolicy::all_disabled()
                    .longdoc_worker_disabled_message()
                    .unwrap_or(
                        "Long Document translation requires a Rust-native route for this request.",
                    ),
            ),
        );
    }

    #[cfg(feature = "retained-dotnet-workers")]
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

fn ensure_default_result_json_path(request: &mut LongDocumentServiceRequest) {
    if request
        .params
        .result_json_path
        .as_deref()
        .and_then(non_empty)
        .is_some()
    {
        return;
    }

    request.params.result_json_path = default_result_json_path_for_request(request);
}

fn retry_result_json_path_for_request(request: &LongDocumentServiceRequest) -> Option<String> {
    request
        .params
        .result_json_path
        .as_deref()
        .and_then(non_empty)
        .or_else(|| default_result_json_path_for_request(request))
}

fn default_result_json_path_for_request(request: &LongDocumentServiceRequest) -> Option<String> {
    if !long_document_request_can_route_natively(request) {
        return None;
    }

    let output_path = request.params.output_path.as_deref().and_then(non_empty)?;
    let mut result_json_path = PathBuf::from(output_path);
    result_json_path.set_extension("result.json");
    Some(result_json_path.display().to_string())
}

pub fn run_native_text_long_document_request(
    request: LongDocumentServiceRequest,
) -> LongDocumentOutcome {
    let mut translator = QuickTranslateNativeLongDocumentTranslator;
    run_native_text_long_document_request_with_translator(&mut translator, request)
}

fn run_native_text_long_document_request_with_windows_ai_client<C>(
    request: LongDocumentServiceRequest,
    windows_ai_client: &mut C,
) -> LongDocumentOutcome
where
    C: WindowsAiLanguageModelClient + Clone + Send,
{
    let mut translator = WindowsAiNativeLongDocumentTranslator::new(windows_ai_client.clone());
    run_native_text_long_document_request_with_translator(&mut translator, request)
}

pub fn run_native_text_long_document_request_with_translator<T: NativeLongDocumentTranslator>(
    translator: &mut T,
    request: LongDocumentServiceRequest,
) -> LongDocumentOutcome {
    run_native_text_long_document_request_with_translator_and_cancellation(
        translator,
        request,
        || false,
    )
}

#[doc(hidden)]
pub fn run_native_text_long_document_request_with_translator_and_cancellation<
    T: NativeLongDocumentTranslator,
    F: Fn() -> bool,
>(
    translator: &mut T,
    request: LongDocumentServiceRequest,
    should_cancel: F,
) -> LongDocumentOutcome {
    let input_label = input_label(&request);
    let result = run_native_text_long_document_request_inner(translator, &request, &should_cancel);

    LongDocumentOutcome {
        query_id: request.query_id,
        input_label,
        events: result.events,
        result: result.result,
    }
}

pub fn retry_failed_native_text_long_document_from_result_json(
    request: LongDocumentServiceRequest,
    result_json_path: impl AsRef<Path>,
) -> LongDocumentOutcome {
    let mut translator = QuickTranslateNativeLongDocumentTranslator;
    retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut translator,
        request,
        result_json_path,
    )
}

#[doc(hidden)]
pub fn retry_failed_native_text_long_document_from_result_json_with_translator<
    T: NativeLongDocumentTranslator,
>(
    translator: &mut T,
    request: LongDocumentServiceRequest,
    result_json_path: impl AsRef<Path>,
) -> LongDocumentOutcome {
    let input_label = input_label(&request);
    let result = retry_failed_native_text_long_document_from_result_json_inner(
        translator,
        &request,
        result_json_path.as_ref(),
    );

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

fn run_native_text_long_document_request_inner<T: NativeLongDocumentTranslator, F: Fn() -> bool>(
    translator: &mut T,
    request: &LongDocumentServiceRequest,
    should_cancel: &F,
) -> NativeLongDocumentRun {
    run_native_text_long_document_request_inner_with_source_reader(
        translator,
        request,
        should_cancel,
        read_native_text_source_chunks,
    )
}

fn run_native_text_long_document_request_inner_with_source_reader<
    T: NativeLongDocumentTranslator,
    F: Fn() -> bool,
    R,
>(
    translator: &mut T,
    request: &LongDocumentServiceRequest,
    should_cancel: &F,
    mut source_reader: R,
) -> NativeLongDocumentRun
where
    R: FnMut(
        &LongDocumentServiceRequest,
        NativeTextInputKind,
    ) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>,
{
    let Some(input_kind) = native_text_input_kind(&request.params.input_mode) else {
        return NativeLongDocumentRun {
            events: Vec::new(),
            result: Err(LongDocumentBackendError::new(
                "Native long document translation only supports PlainText, Markdown, and simple PDF text input",
            )),
        };
    };

    if let Err(error) = preflight_native_text_output_paths(request, input_kind) {
        return NativeLongDocumentRun {
            events: Vec::new(),
            result: Err(error),
        };
    }

    let chunks = match source_reader(request, input_kind) {
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
    if should_cancel() {
        return native_long_document_cancelled_run(events);
    }
    let document_context_plan =
        extract_native_document_context_plan(translator, request, &chunks, &mut events);
    if should_cancel() {
        return native_long_document_cancelled_run(events);
    }
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
        if should_cancel() {
            return native_long_document_cancelled_run(events);
        }
        let mut batch = Vec::new();

        while next_index < chunks.len() && batch.len() < max_concurrency {
            if should_cancel() {
                return native_long_document_cancelled_run(events);
            }
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
            if document_context_plan.should_preserve_chunk(index)
                || matches!(&preparation, NativeTextChunkPreparation::PreserveOriginal)
            {
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
            apply_native_text_document_context_prompt(
                &mut translate_request,
                document_context_plan.prompt_for_page(chunk.page_number),
            );
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

        if should_cancel() {
            return native_long_document_cancelled_run(events);
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

    if should_cancel() {
        return native_long_document_cancelled_run(events);
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
        export_native_text_document(
            request,
            input_kind,
            &chunks,
            &translations,
            &document_context_plan.preserve_chunk_indexes,
        )
        .and_then(|export| {
            let result_json_path = normalized_result_json_path(request);
            let quality_report = native_long_document_quality_report_json(
                &export.checkpoint,
                export.backfill_metrics.clone(),
            )?;
            let result = TranslateDocumentResult {
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
                quality_report: Some(quality_report),
                result_json_path: result_json_path.clone(),
            };
            write_native_result_json_sidecar(
                result_json_path.as_deref(),
                &result,
                input_kind,
                &request.params,
                &export.checkpoint,
                export.pdf_checkpoint.as_ref(),
            )?;
            Ok(result)
        })
    };

    NativeLongDocumentRun { events, result }
}

fn retry_failed_native_text_long_document_from_result_json_inner<
    T: NativeLongDocumentTranslator,
>(
    translator: &mut T,
    request: &LongDocumentServiceRequest,
    result_json_path: &Path,
) -> NativeLongDocumentRun {
    let sidecar = match read_native_result_json_sidecar(result_json_path) {
        Ok(sidecar) => sidecar,
        Err(error) => {
            return NativeLongDocumentRun {
                events: Vec::new(),
                result: Err(error),
            };
        }
    };
    let Some(input_kind) = native_text_input_kind(&sidecar.checkpoint.input_mode) else {
        return NativeLongDocumentRun {
            events: Vec::new(),
            result: Err(LongDocumentBackendError::new(format!(
                "Native long document checkpoint input mode '{}' is not supported",
                sidecar.checkpoint.input_mode
            ))),
        };
    };
    let chunks =
        match native_text_source_chunks_from_retry_checkpoint(input_kind, &sidecar.checkpoint) {
            Ok(chunks) => chunks,
            Err(error) => {
                return NativeLongDocumentRun {
                    events: Vec::new(),
                    result: Err(error),
                };
            }
        };
    let failed_indexes = sidecar
        .checkpoint
        .text
        .failed_chunk_indexes
        .iter()
        .copied()
        .collect::<Vec<_>>();
    if let Some(index) = failed_indexes
        .iter()
        .copied()
        .find(|index| *index >= chunks.len())
    {
        return NativeLongDocumentRun {
            events: Vec::new(),
            result: Err(LongDocumentBackendError::new(format!(
                "Native long document checkpoint failed chunk index {index} is out of range"
            ))),
        };
    }

    let total_chunks = chunks.len() as u32;
    let mut translations = vec![None; chunks.len()];
    for (index, translated) in &sidecar.checkpoint.text.translated_chunks {
        if *index >= translations.len() {
            return NativeLongDocumentRun {
                events: Vec::new(),
                result: Err(LongDocumentBackendError::new(format!(
                    "Native long document checkpoint translated chunk index {index} is out of range"
                ))),
            };
        }
        translations[*index] = Some(translated.clone());
    }
    let failed_index_set = failed_indexes.iter().copied().collect::<BTreeSet<_>>();
    for index in &failed_index_set {
        translations[*index] = None;
    }
    if let Err(error) =
        validate_native_retry_checkpoint_chunk_coverage(&translations, &failed_index_set)
    {
        return NativeLongDocumentRun {
            events: Vec::new(),
            result: Err(error),
        };
    }

    let mut retry_request = request.clone();
    retry_request.params.input_mode = sidecar.checkpoint.input_mode.clone();
    retry_request.params.output_mode = sidecar.checkpoint.output_mode.clone();
    retry_request.params.service_id = sidecar.checkpoint.service_id.clone();
    retry_request.params.from = sidecar.checkpoint.from.clone();
    retry_request.params.to = sidecar.checkpoint.to.clone();
    retry_request.params.result_json_path = Some(result_json_path.display().to_string());
    restore_native_retry_route_from_checkpoint(&mut retry_request, &sidecar.checkpoint);

    if let Err(error) = preflight_native_text_output_paths(&retry_request, input_kind) {
        return NativeLongDocumentRun {
            events: Vec::new(),
            result: Err(error),
        };
    }

    let mut events = vec![LongDocumentEvent::Status(StatusEventData {
        message: "Retrying failed long document chunks natively".to_string(),
    })];
    let mut failed_chunk_indexes = Vec::new();
    let mut first_error = None;
    let mut translation_cache = native_long_document_translation_cache(&retry_request.settings);
    let max_concurrency = retry_request
        .settings
        .long_doc_max_concurrency
        .unwrap_or(1)
        .clamp(1, 16)
        .max(1) as usize;
    let mut next_failed_index = 0;

    while next_failed_index < failed_indexes.len() {
        let mut batch = Vec::new();
        while next_failed_index < failed_indexes.len() && batch.len() < max_concurrency {
            let index = failed_indexes[next_failed_index];
            next_failed_index += 1;
            let chunk = &chunks[index];
            let chunk_text = chunk.text.as_str();

            events.push(LongDocumentEvent::Progress(ProgressEventData {
                stage: "Retrying".to_string(),
                current_block: (next_failed_index) as u32,
                total_blocks: failed_indexes.len() as u32,
                current_page: chunk.page_number,
                total_pages: 1,
                percentage: ((next_failed_index - 1) as f64 / failed_indexes.len() as f64) * 100.0,
                current_block_preview: Some(chunk_preview(chunk_text)),
            }));

            let preparation = prepare_native_text_chunk_for_translation(&retry_request, chunk);
            if matches!(&preparation, NativeTextChunkPreparation::PreserveOriginal) {
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
                native_quick_translate_request_for_chunk(&retry_request, &protected_text)
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
                    &retry_request.params.service_id,
                    &retry_request.params.from,
                    &retry_request.params.to,
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
                &retry_request,
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
        current_block: failed_indexes.len() as u32,
        total_blocks: failed_indexes.len() as u32,
        current_page: 1,
        total_pages: 1,
        percentage: 100.0,
        current_block_preview: None,
    }));

    let succeeded_chunks = translations.iter().filter(|chunk| chunk.is_some()).count() as u32;
    let result = if succeeded_chunks == 0 {
        Err(LongDocumentBackendError::new(first_error.unwrap_or_else(
            || "Retry failed for all long document chunks.".to_string(),
        )))
    } else {
        export_native_text_document(
            &retry_request,
            input_kind,
            &chunks,
            &translations,
            &preserved_chunk_indexes_from_retry_checkpoint(&sidecar.checkpoint),
        )
        .and_then(|export| {
            let result_json_path = normalized_result_json_path(&retry_request);
            let quality_report = native_long_document_quality_report_json(
                &export.checkpoint,
                export.backfill_metrics.clone(),
            )?;
            let result = TranslateDocumentResult {
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
                quality_report: Some(quality_report),
                result_json_path: result_json_path.clone(),
            };
            write_native_result_json_sidecar(
                result_json_path.as_deref(),
                &result,
                input_kind,
                &retry_request.params,
                &export.checkpoint,
                export.pdf_checkpoint.as_ref(),
            )?;
            Ok(result)
        })
    };

    NativeLongDocumentRun { events, result }
}

fn native_long_document_cancelled_run(events: Vec<LongDocumentEvent>) -> NativeLongDocumentRun {
    NativeLongDocumentRun {
        events,
        result: Err(LongDocumentBackendError::new(
            NATIVE_LONG_DOCUMENT_CANCELLED_MESSAGE,
        )),
    }
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
const NATIVE_DOCUMENT_CONTEXT_MAP_PAGE_PROMPT: &str = r#"Do NOT translate the document text. Analyze it and respond with a single JSON object (no prose, no markdown fences) with exactly these three fields:

"summary": a 1-3 sentence overview of this page's content, topic, domain, and terminology style.

"glossary": an object mapping source-language terms to chosen target-language renderings. Include proper nouns, place names, person names, product / model names, and technical terms. Pick ONE consistent rendering per term. Example: {"Transformer": "Transformer", "self-attention": "自注意力"}.

"preservation_hints": an array of verbatim source-text snippets that should NOT be translated in the second pass. Include items like:
  * tabular data: EVERY header cell and EVERY data row of any table on this page (numeric benchmark tables, hyperparameter tables, model comparison tables — list each cell value as its own entry, and also list the column header row verbatim)
  * code fragments, command lines, file paths
  * URLs and email addresses
  * identifiers, variable names, hyperparameter lists
  * proper nouns and product names that should stay verbatim
  * short fragments that look like noise / garbled text
  * any standalone snippet whose translation would degrade quality
Each entry must be a verbatim substring of the source so the second pass can match by Contains/Equals. Do not paraphrase. Do not add quote marks. If there are no items in a category, omit them.

Do NOT include section or subsection headings (short standalone lines that label a structural part of the document, typically beginning with a numeric index like "1", "2.3", or with a common part-name word) — those should always be translated.

Return ONLY the JSON object, nothing else."#;
const NATIVE_DOCUMENT_CONTEXT_REDUCE_SUMMARY_PROMPT: &str = r#"The numbered list below contains partial summaries of consecutive pages of the same document. Merge them into a single 1-3 sentence summary that covers the document as a whole -- its topic, domain, and terminology style. Do not list the pages individually. Respond with the merged summary text only, no JSON, no prose around it."#;

#[derive(Clone, Debug, Default, PartialEq)]
struct NativeDocumentContextPlan {
    context: DocumentContext,
    glossary_by_page: BTreeMap<u32, Vec<(String, String)>>,
    preserve_chunk_indexes: BTreeSet<usize>,
}

impl NativeDocumentContextPlan {
    fn empty() -> Self {
        Self::default()
    }

    fn should_preserve_chunk(&self, index: usize) -> bool {
        self.preserve_chunk_indexes.contains(&index)
    }

    fn prompt_for_page(&self, page_number: u32) -> Option<String> {
        let has_summary = !self.context.summary.trim().is_empty();
        let glossary = self.glossary_by_page.get(&page_number);
        let has_glossary = glossary.is_some_and(|items| !items.is_empty());
        if !has_summary && !has_glossary {
            return None;
        }

        let mut parts = Vec::with_capacity(2);
        if has_summary {
            parts.push(format!("Document summary: {}", self.context.summary.trim()));
        }
        if let Some(glossary) = glossary.filter(|items| !items.is_empty()) {
            let glossary_lines = glossary
                .iter()
                .map(|(source, target)| format!("  {source} -> {target}"))
                .collect::<Vec<_>>()
                .join("\n");
            parts.push(format!(
                "Use these term translations consistently across the document:\n{glossary_lines}"
            ));
        }

        Some(parts.join("\n\n"))
    }
}

fn extract_native_document_context_plan<T: NativeLongDocumentTranslator>(
    translator: &mut T,
    request: &LongDocumentServiceRequest,
    chunks: &[NativeTextSourceChunk],
    events: &mut Vec<LongDocumentEvent>,
) -> NativeDocumentContextPlan {
    if request
        .settings
        .long_doc_enable_document_context_pass
        .unwrap_or(false)
        == false
    {
        return NativeDocumentContextPlan::empty();
    }

    let page_batches = native_document_context_page_batches(chunks);
    if page_batches.is_empty() {
        return NativeDocumentContextPlan::empty();
    }

    events.push(LongDocumentEvent::Status(StatusEventData {
        message: "Analyzing document context natively".to_string(),
    }));

    let max_concurrency = request
        .settings
        .long_doc_max_concurrency
        .unwrap_or(1)
        .clamp(1, 16)
        .max(1) as usize;
    let partials = translate_native_document_context_pages(
        translator,
        request,
        &page_batches,
        max_concurrency,
        events,
    );
    let mut context = merge_page_partials(&partials);
    if context == DocumentContext::empty() {
        return NativeDocumentContextPlan::empty();
    }
    context.summary =
        reduce_native_document_context_summary(translator, request, &partials, &context.summary);

    let preserve_chunk_indexes = native_document_context_preserve_chunk_indexes(chunks, &context);
    let glossary_by_page =
        native_document_context_glossary_by_page(chunks, &context, &preserve_chunk_indexes);

    NativeDocumentContextPlan {
        context,
        glossary_by_page,
        preserve_chunk_indexes,
    }
}

#[derive(Clone, Debug, PartialEq)]
struct NativeDocumentContextPageBatch {
    page_number: u32,
    text: String,
}

fn native_document_context_page_batches(
    chunks: &[NativeTextSourceChunk],
) -> Vec<NativeDocumentContextPageBatch> {
    let mut pages: BTreeMap<u32, Vec<String>> = BTreeMap::new();
    for chunk in chunks {
        let text = chunk.text.trim();
        if text.is_empty() {
            continue;
        }
        pages
            .entry(chunk.page_number)
            .or_default()
            .push(text.to_string());
    }

    pages
        .into_iter()
        .filter_map(|(page_number, texts)| {
            let text = texts.join("\n\n");
            (!text.trim().is_empty())
                .then_some(NativeDocumentContextPageBatch { page_number, text })
        })
        .collect()
}

fn translate_native_document_context_pages<T: NativeLongDocumentTranslator>(
    translator: &T,
    request: &LongDocumentServiceRequest,
    page_batches: &[NativeDocumentContextPageBatch],
    max_concurrency: usize,
    events: &mut Vec<LongDocumentEvent>,
) -> Vec<PagePartial> {
    let mut partials = Vec::with_capacity(page_batches.len());
    let mut next_index = 0;

    while next_index < page_batches.len() {
        let batch_start = next_index;
        let batch_end = page_batches.len().min(batch_start + max_concurrency.max(1));
        let batch = page_batches[batch_start..batch_end].to_vec();
        next_index = batch_end;

        let batch_results = std::thread::scope(|scope| {
            let handles = batch
                .into_iter()
                .map(|page| {
                    let mut worker = translator.clone();
                    scope.spawn(move || {
                        translate_native_document_context_page(&mut worker, request, &page)
                    })
                })
                .collect::<Vec<_>>();

            handles
                .into_iter()
                .map(|handle| handle.join().unwrap_or_else(|_| PagePartial::failed(0)))
                .collect::<Vec<_>>()
        });

        partials.extend(batch_results);
        events.push(LongDocumentEvent::Progress(ProgressEventData {
            stage: "DocumentContext".to_string(),
            current_block: partials.len() as u32,
            total_blocks: page_batches.len() as u32,
            current_page: partials
                .last()
                .map(|partial| partial.page_number.max(0) as u32)
                .unwrap_or(0),
            total_pages: page_batches.len() as u32,
            percentage: (partials.len() as f64 / page_batches.len() as f64) * 100.0,
            current_block_preview: None,
        }));
    }

    partials
}

fn translate_native_document_context_page<T: NativeLongDocumentTranslator>(
    translator: &mut T,
    request: &LongDocumentServiceRequest,
    page: &NativeDocumentContextPageBatch,
) -> PagePartial {
    let Some(mut context_request) = native_quick_translate_request_for_chunk(request, &page.text)
    else {
        return PagePartial::failed(page.page_number as i32);
    };
    context_request.params.custom_prompt =
        Some(NATIVE_DOCUMENT_CONTEXT_MAP_PAGE_PROMPT.to_string());

    match translator.translate_chunk(context_request) {
        Ok(raw) => try_parse_page_partial(&raw, page.page_number as i32)
            .unwrap_or_else(|| PagePartial::failed(page.page_number as i32)),
        Err(_) => PagePartial::failed(page.page_number as i32),
    }
}

fn reduce_native_document_context_summary<T: NativeLongDocumentTranslator>(
    translator: &mut T,
    request: &LongDocumentServiceRequest,
    partials: &[PagePartial],
    fallback_summary: &str,
) -> String {
    let summaries = partials
        .iter()
        .filter(|partial| !partial.failed)
        .map(|partial| (partial.page_number, partial.summary.trim()))
        .filter(|(_, summary)| !summary.is_empty())
        .collect::<Vec<_>>();
    if summaries.len() <= 1 {
        return fallback_summary.trim().to_string();
    }

    let summary_text = summaries
        .iter()
        .map(|(page_number, summary)| format!("Page {page_number}: {summary}"))
        .collect::<Vec<_>>()
        .join("\n");
    let Some(mut summary_request) =
        native_quick_translate_request_for_chunk(request, &summary_text)
    else {
        return fallback_summary.trim().to_string();
    };
    summary_request.params.custom_prompt =
        Some(NATIVE_DOCUMENT_CONTEXT_REDUCE_SUMMARY_PROMPT.to_string());

    translator
        .translate_chunk(summary_request)
        .ok()
        .map(|summary| summary.trim().to_string())
        .filter(|summary| !summary.is_empty())
        .unwrap_or_else(|| fallback_summary.trim().to_string())
}

fn native_document_context_preserve_chunk_indexes(
    chunks: &[NativeTextSourceChunk],
    context: &DocumentContext,
) -> BTreeSet<usize> {
    if context.preservation_hints.is_empty() {
        return BTreeSet::new();
    }

    let ir = DocumentIr::new(
        chunks
            .iter()
            .map(|chunk| DocumentBlockIr::new(chunk.text.clone()))
            .collect(),
    );
    let rewritten = apply_preservation_hints(&ir, &context.preservation_hints);
    rewritten
        .blocks
        .into_iter()
        .enumerate()
        .filter_map(|(index, block)| {
            (block.translation_skipped || block.preserve_original_text_in_pdf_export)
                .then_some(index)
        })
        .collect()
}

fn native_document_context_glossary_by_page(
    chunks: &[NativeTextSourceChunk],
    context: &DocumentContext,
    preserve_chunk_indexes: &BTreeSet<usize>,
) -> BTreeMap<u32, Vec<(String, String)>> {
    if context.glossary.is_empty() {
        return BTreeMap::new();
    }

    let mut page_texts: BTreeMap<u32, String> = BTreeMap::new();
    for (index, chunk) in chunks.iter().enumerate() {
        if preserve_chunk_indexes.contains(&index) {
            continue;
        }
        let page_text = page_texts.entry(chunk.page_number).or_default();
        if !page_text.is_empty() {
            page_text.push('\n');
        }
        page_text.push_str(&chunk.text);
    }

    page_texts
        .into_iter()
        .map(|(page_number, page_text)| {
            let matched = context
                .glossary
                .iter()
                .filter(|(source, _)| !source.is_empty() && page_text.contains(source.as_str()))
                .map(|(source, target)| (source.clone(), target.clone()))
                .collect::<Vec<_>>();
            (page_number, matched)
        })
        .collect()
}

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

fn apply_native_text_document_context_prompt(
    request: &mut QuickTranslateServiceRequest,
    context_prompt: Option<String>,
) {
    let Some(context_prompt) = context_prompt.filter(|prompt| !prompt.trim().is_empty()) else {
        return;
    };

    request.params.custom_prompt = Some(match request.params.custom_prompt.take() {
        Some(existing) if !existing.trim().is_empty() => {
            format!("{context_prompt}\n\n{existing}")
        }
        _ => context_prompt,
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
        settings.cache_dir_str(),
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
    checkpoint: LongDocumentExportCheckpoint,
    pdf_checkpoint: Option<PdfExportCheckpoint>,
    backfill_metrics: Option<serde_json::Value>,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
struct NativeLongDocumentQualityReport {
    stage_timings_ms: BTreeMap<String, u64>,
    backfill_metrics: Option<serde_json::Value>,
    total_blocks: u32,
    translated_blocks: u32,
    skipped_blocks: u32,
    failed_blocks: Vec<NativeLongDocumentFailedBlockInfo>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
struct NativeLongDocumentFailedBlockInfo {
    ir_block_id: String,
    source_block_id: String,
    page_number: i32,
    retry_count: u32,
    error: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct NativeLongDocumentResultSidecar {
    #[serde(flatten)]
    result: TranslateDocumentResult,
    checkpoint: NativeLongDocumentSidecarCheckpoint,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct NativeLongDocumentSidecarCheckpoint {
    #[serde(default, skip_serializing_if = "is_zero_u32")]
    route_metadata_version: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    input_path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    output_path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pdf_export_mode: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    layout_detection: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    page_range: Option<String>,
    input_mode: String,
    output_mode: String,
    service_id: String,
    from: String,
    to: String,
    text: LongDocumentExportCheckpoint,
    #[serde(skip_serializing_if = "Option::is_none")]
    pdf: Option<PdfExportCheckpoint>,
}

fn native_text_input_kind(input_mode: &str) -> Option<NativeTextInputKind> {
    match input_mode {
        "PlainText" => Some(NativeTextInputKind::PlainText),
        "Markdown" => Some(NativeTextInputKind::Markdown),
        "Pdf" => Some(NativeTextInputKind::PdfText),
        _ => None,
    }
}

fn native_text_input_kind_name(input_kind: NativeTextInputKind) -> &'static str {
    match input_kind {
        NativeTextInputKind::PlainText => "PlainText",
        NativeTextInputKind::Markdown => "Markdown",
        NativeTextInputKind::PdfText => "Pdf",
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
                validate_native_pdf_input_file(path)?;
                read_native_pdf_source_chunks_with_fallbacks(
                    request,
                    path,
                    request.params.page_range.as_deref(),
                    try_read_native_pdf_source_chunks,
                    read_native_pdf_text_source_chunks,
                    read_native_pdf_ocr_source_chunks,
                )
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

fn read_native_pdf_source_chunks_with_fallbacks<SR, TR, OR>(
    request: &LongDocumentServiceRequest,
    path: &str,
    page_range: Option<&str>,
    mut source_reader: SR,
    text_reader: TR,
    ocr_reader: OR,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>
where
    SR: FnMut(
        &LongDocumentServiceRequest,
        &str,
        Option<&str>,
    ) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>,
    TR: FnMut(&str, Option<&str>) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>,
    OR: FnMut(
        &LongDocumentServiceRequest,
        &str,
        Option<&str>,
    ) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>,
{
    if let Ok(chunks) = source_reader(request, path, page_range) {
        if !chunks.is_empty() {
            return Ok(chunks);
        }
    }

    read_native_pdf_text_or_ocr_source_chunks(request, path, page_range, text_reader, ocr_reader)
}

fn read_native_pdf_text_or_ocr_source_chunks<TR, OR>(
    request: &LongDocumentServiceRequest,
    path: &str,
    page_range: Option<&str>,
    mut text_reader: TR,
    mut ocr_reader: OR,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>
where
    TR: FnMut(&str, Option<&str>) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>,
    OR: FnMut(
        &LongDocumentServiceRequest,
        &str,
        Option<&str>,
    ) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError>,
{
    match text_reader(path, page_range) {
        Ok(text_chunks) if !text_chunks.is_empty() => Ok(text_chunks),
        Ok(text_chunks) => match ocr_reader(request, path, page_range) {
            Ok(ocr_chunks) if !ocr_chunks.is_empty() => Ok(ocr_chunks),
            Ok(_) => Ok(text_chunks),
            Err(ocr_error) => Err(native_pdf_empty_text_with_ocr_error(ocr_error)),
        },
        Err(text_error) => match ocr_reader(request, path, page_range) {
            Ok(ocr_chunks) if !ocr_chunks.is_empty() => Ok(ocr_chunks),
            Ok(_) => Err(text_error),
            Err(ocr_error) => Err(native_pdf_text_extraction_with_ocr_error(
                text_error, ocr_error,
            )),
        },
    }
}

fn native_pdf_text_extraction_with_ocr_error(
    text_error: LongDocumentBackendError,
    ocr_error: LongDocumentBackendError,
) -> LongDocumentBackendError {
    LongDocumentBackendError::new(format!(
        "{}; OCR fallback failed: {}",
        text_error.message, ocr_error.message
    ))
}

fn native_pdf_empty_text_with_ocr_error(
    ocr_error: LongDocumentBackendError,
) -> LongDocumentBackendError {
    LongDocumentBackendError::new(format!(
        "{}; OCR fallback failed: {}",
        NATIVE_PDF_EMPTY_TEXT_ERROR, ocr_error.message
    ))
}

fn try_read_native_pdf_source_chunks(
    request: &LongDocumentServiceRequest,
    path: &str,
    page_range: Option<&str>,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError> {
    try_read_native_pdf_source_chunks_with_extractor(path, page_range, |options| {
        read_native_pdf_source_chunks_with_options(options, request)
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
    request: &LongDocumentServiceRequest,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError> {
    let input_path = options.input_pdf.display();
    let summary = extract_pdf_text_chars(options).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not extract PDF text '{input_path}': {error}",
        ))
    })?;
    let document = pdf_source_document_from_text_summary(&summary);
    let document =
        try_enrich_native_pdf_source_document_with_doc_layout_yolo(options, request, document)?;
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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum NativePdfLayoutDetectionMode {
    Auto,
    Heuristic,
    OnnxLocal,
    VisionLlm,
}

fn try_enrich_native_pdf_source_document_with_doc_layout_yolo(
    options: &PdfTextExtractionOptions,
    request: &LongDocumentServiceRequest,
    document: PdfSourceDocument,
) -> Result<PdfSourceDocument, LongDocumentBackendError> {
    let mode = native_pdf_layout_detection_mode(request);
    if mode == NativePdfLayoutDetectionMode::VisionLlm {
        return try_enrich_native_pdf_source_document_with_vision_layout(
            options, request, document,
        );
    }

    let Some(paths) = native_pdf_doc_layout_yolo_paths(request) else {
        return Ok(document);
    };
    ensure_native_pdf_doc_layout_yolo_if_explicitly_requested(request)?;
    if !paths.native_lib_path.is_file() || !paths.doc_layout_model_path.is_file() {
        if mode == NativePdfLayoutDetectionMode::OnnxLocal {
            return Err(LongDocumentBackendError::new(format!(
                "DocLayout-YOLO local model is not available: missing '{}' or '{}'",
                paths.native_lib_path.display(),
                paths.doc_layout_model_path.display()
            )));
        }

        return Ok(document);
    }

    let document = match detect_native_pdf_doc_layout_yolo_pages(options, request, &paths) {
        Ok(result) if !result.is_empty() => {
            let document = if result.layouts.is_empty() {
                document
            } else {
                pdf_source_document_with_doc_layout_yolo_detections(&document, &result.layouts)
            };
            if result.table_structures.is_empty() {
                document
            } else {
                pdf_source_document_with_tatr_table_structures(&document, &result.table_structures)
            }
        }
        Ok(_) => document,
        Err(error) if mode == NativePdfLayoutDetectionMode::OnnxLocal => return Err(error),
        Err(_) => document,
    };

    Ok(document)
}

fn ensure_native_pdf_doc_layout_yolo_if_explicitly_requested(
    request: &LongDocumentServiceRequest,
) -> Result<(), LongDocumentBackendError> {
    if !native_pdf_should_ensure_doc_layout_yolo(request) {
        return Ok(());
    }

    let Some(base) = native_pdf_managed_layout_model_base(request) else {
        return Ok(());
    };
    let mut client =
        ReqwestResourceDownloadClient::from_settings(&request.settings).map_err(|error| {
            LongDocumentBackendError::new(format!(
                "Could not prepare DocLayout-YOLO download client: {error}"
            ))
        })?;

    ensure_layout_model_available_for_directory(
        &mut client,
        base,
        &LayoutModelDownloadConfig::default(),
        &mut |_| {},
    )
    .map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not ensure DocLayout-YOLO local model: {error}"
        ))
    })?;

    Ok(())
}

fn try_enrich_native_pdf_source_document_with_vision_layout(
    options: &PdfTextExtractionOptions,
    request: &LongDocumentServiceRequest,
    document: PdfSourceDocument,
) -> Result<PdfSourceDocument, LongDocumentBackendError> {
    let Some(config) = native_pdf_vision_layout_config(request)? else {
        return Ok(document);
    };

    match detect_native_pdf_vision_layout_pages(options, request, &config) {
        Ok(layouts) if !layouts.is_empty() => Ok(
            pdf_source_document_with_doc_layout_yolo_detections(&document, &layouts),
        ),
        Ok(_) => Ok(document),
        Err(error) => Err(error),
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
struct NativePdfDocLayoutDetectionResult {
    layouts: Vec<PdfSourcePageLayoutDetections>,
    table_structures: Vec<PdfSourcePageTableStructures>,
}

impl NativePdfDocLayoutDetectionResult {
    fn is_empty(&self) -> bool {
        self.layouts.is_empty() && self.table_structures.is_empty()
    }
}

fn detect_native_pdf_doc_layout_yolo_pages(
    options: &PdfTextExtractionOptions,
    request: &LongDocumentServiceRequest,
    paths: &LayoutModelPaths,
) -> Result<NativePdfDocLayoutDetectionResult, LongDocumentBackendError> {
    let output_dir = native_pdf_layout_temp_dir(&options.input_pdf);
    let result =
        detect_native_pdf_doc_layout_yolo_pages_in_directory(options, request, paths, &output_dir);
    let _ = fs::remove_dir_all(&output_dir);
    result
}

fn detect_native_pdf_doc_layout_yolo_pages_in_directory(
    options: &PdfTextExtractionOptions,
    request: &LongDocumentServiceRequest,
    paths: &LayoutModelPaths,
    output_dir: &Path,
) -> Result<NativePdfDocLayoutDetectionResult, LongDocumentBackendError> {
    let mut render_options = PdfToBgraOptions::new(&options.input_pdf, output_dir);
    render_options.page_selection = options.page_selection.clone();
    render_options.pdfium_dir = options.pdfium_dir.clone();
    let render_summary = render_pdf_pages_to_bgra_files(&render_options).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not render PDF pages for DocLayout-YOLO '{}': {error}",
            options.input_pdf.display()
        ))
    })?;

    let runtime_dir = paths.native_lib_path.parent().ok_or_else(|| {
        LongDocumentBackendError::new(format!(
            "Could not resolve ONNX Runtime directory from '{}'",
            paths.native_lib_path.display()
        ))
    })?;
    let mut session =
        DocLayoutYoloOnnxSession::from_model_paths(runtime_dir, &paths.doc_layout_model_path)
            .map_err(|error| {
                LongDocumentBackendError::new(format!("Could not load DocLayout-YOLO: {error}"))
            })?;
    let mut tatr_session = None;
    let mut tatr_unavailable = false;

    let mut layouts = Vec::new();
    let mut table_structures = Vec::new();
    for page in render_summary.rendered_pages {
        let pixel_width = usize::try_from(page.pixel_width).map_err(|_| {
            LongDocumentBackendError::new(format!(
                "Rendered page {} width is invalid for DocLayout-YOLO",
                page.page_number
            ))
        })?;
        let pixel_height = usize::try_from(page.pixel_height).map_err(|_| {
            LongDocumentBackendError::new(format!(
                "Rendered page {} height is invalid for DocLayout-YOLO",
                page.page_number
            ))
        })?;
        let pixels = fs::read(&page.pixel_data_path).map_err(|error| {
            LongDocumentBackendError::new(format!(
                "Could not read rendered page {} BGRA data '{}': {error}",
                page.page_number,
                page.pixel_data_path.display()
            ))
        })?;
        let detections = session
            .detect_bgra(&pixels, pixel_width, pixel_height)
            .map_err(|error| {
                LongDocumentBackendError::new(format!(
                    "DocLayout-YOLO detection failed on page {}: {error}",
                    page.page_number
                ))
            })?;
        let has_table_detection = detections
            .iter()
            .any(|detection| detection.region_type == DocLayoutRegionType::Table);
        if has_table_detection && tatr_session.is_none() && !tatr_unavailable {
            match load_or_ensure_native_pdf_tatr_session(request, runtime_dir, paths)? {
                Some(session) => tatr_session = Some(session),
                None => {
                    tatr_unavailable = true;
                }
            }
        }
        if let Some(tatr_session) = tatr_session.as_mut() {
            let mut tables = Vec::new();
            for detection in detections
                .iter()
                .filter(|detection| detection.region_type == DocLayoutRegionType::Table)
            {
                let table = native_pdf_tatr_recognition_result(
                    request,
                    page.page_number,
                    tatr_session.recognize_bgra(
                        &pixels,
                        pixel_width,
                        pixel_height,
                        detection.x,
                        detection.y,
                        detection.width,
                        detection.height,
                    ),
                )?;
                if let Some(table) = table {
                    tables.push(table);
                }
            }
            if !tables.is_empty() {
                table_structures.push(PdfSourcePageTableStructures {
                    page_number: page.page_number,
                    pixel_width,
                    pixel_height,
                    tables,
                });
            }
        }
        layouts.push(PdfSourcePageLayoutDetections {
            page_number: page.page_number,
            pixel_width,
            pixel_height,
            detections,
        });
    }

    Ok(NativePdfDocLayoutDetectionResult {
        layouts,
        table_structures,
    })
}

fn detect_native_pdf_vision_layout_pages(
    options: &PdfTextExtractionOptions,
    request: &LongDocumentServiceRequest,
    config: &OpenAiCompatibleConfig,
) -> Result<Vec<PdfSourcePageLayoutDetections>, LongDocumentBackendError> {
    let output_dir = native_pdf_layout_temp_dir(&options.input_pdf);
    let result =
        detect_native_pdf_vision_layout_pages_in_directory(options, request, config, &output_dir);
    let _ = fs::remove_dir_all(&output_dir);
    result
}

fn detect_native_pdf_vision_layout_pages_in_directory(
    options: &PdfTextExtractionOptions,
    request: &LongDocumentServiceRequest,
    config: &OpenAiCompatibleConfig,
    output_dir: &Path,
) -> Result<Vec<PdfSourcePageLayoutDetections>, LongDocumentBackendError> {
    let mut render_options = PdfToBgraOptions::new(&options.input_pdf, output_dir);
    render_options.page_selection = options.page_selection.clone();
    render_options.pdfium_dir = options.pdfium_dir.clone();
    let render_summary = render_pdf_pages_to_bgra_files(&render_options).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not render PDF pages for Vision layout '{}': {error}",
            options.input_pdf.display()
        ))
    })?;

    let mut client = ReqwestVisionLayoutHttpClient::from_settings(&request.settings)
        .map_err(|error| LongDocumentBackendError::new(error.message))?;
    let mut layouts = Vec::new();
    for page in render_summary.rendered_pages {
        let pixel_width = usize::try_from(page.pixel_width).map_err(|_| {
            LongDocumentBackendError::new(format!(
                "Rendered page {} width is invalid for Vision layout",
                page.page_number
            ))
        })?;
        let pixel_height = usize::try_from(page.pixel_height).map_err(|_| {
            LongDocumentBackendError::new(format!(
                "Rendered page {} height is invalid for Vision layout",
                page.page_number
            ))
        })?;
        let pixels = fs::read(&page.pixel_data_path).map_err(|error| {
            LongDocumentBackendError::new(format!(
                "Could not read rendered page {} BGRA data '{}': {error}",
                page.page_number,
                page.pixel_data_path.display()
            ))
        })?;
        let detections = execute_vision_layout_detection(
            &mut client,
            config,
            &pixels,
            page.pixel_width,
            page.pixel_height,
        )
        .map_err(|error| vision_layout_page_backend_error(page.page_number, error))?;
        let detections = detections
            .iter()
            .filter_map(vision_layout_detection_to_doc_layout_detection)
            .collect::<Vec<_>>();
        if !detections.is_empty() {
            layouts.push(PdfSourcePageLayoutDetections {
                page_number: page.page_number,
                pixel_width,
                pixel_height,
                detections,
            });
        }
    }

    Ok(layouts)
}

fn vision_layout_page_backend_error(
    page_number: usize,
    error: OpenAiExecutionError,
) -> LongDocumentBackendError {
    LongDocumentBackendError::new(format!(
        "Vision layout detection failed on page {page_number}: {}",
        error.message
    ))
}

fn native_pdf_layout_detection_mode(
    request: &LongDocumentServiceRequest,
) -> NativePdfLayoutDetectionMode {
    native_pdf_layout_detection_mode_from_values(
        request.params.layout_detection.as_deref(),
        request.settings.layout_detection_mode.as_deref(),
    )
}

fn native_pdf_layout_detection_mode_from_values(
    param_value: Option<&str>,
    settings_value: Option<&str>,
) -> NativePdfLayoutDetectionMode {
    param_value
        .or(settings_value)
        .map(parse_native_pdf_layout_detection_mode)
        .unwrap_or(NativePdfLayoutDetectionMode::Auto)
}

fn parse_native_pdf_layout_detection_mode(value: &str) -> NativePdfLayoutDetectionMode {
    match value.trim().to_ascii_lowercase().as_str() {
        "heuristic" => NativePdfLayoutDetectionMode::Heuristic,
        "onnx" | "onnxlocal" => NativePdfLayoutDetectionMode::OnnxLocal,
        "vision" | "visionllm" => NativePdfLayoutDetectionMode::VisionLlm,
        _ => NativePdfLayoutDetectionMode::Auto,
    }
}

fn native_pdf_layout_mode_uses_doc_layout_yolo(mode: NativePdfLayoutDetectionMode) -> bool {
    matches!(
        mode,
        NativePdfLayoutDetectionMode::Auto | NativePdfLayoutDetectionMode::OnnxLocal
    )
}

fn native_pdf_tatr_table_structure_enabled(request: &LongDocumentServiceRequest) -> bool {
    request.settings.enable_tatr_table_structure.unwrap_or(true)
}

fn load_or_ensure_native_pdf_tatr_session(
    request: &LongDocumentServiceRequest,
    runtime_dir: &Path,
    paths: &LayoutModelPaths,
) -> Result<Option<TatrOnnxSession>, LongDocumentBackendError> {
    if !native_pdf_tatr_table_structure_enabled(request) {
        return Ok(None);
    }

    let fatal_setup_errors = native_pdf_tatr_errors_are_fatal(request);
    if !paths.tatr_model_path.is_file() && native_pdf_should_lazy_ensure_tatr(request) {
        let Some(base) = native_pdf_managed_layout_model_base(request) else {
            return Ok(None);
        };
        let mut client = match ReqwestResourceDownloadClient::from_settings(&request.settings) {
            Ok(client) => client,
            Err(error) if fatal_setup_errors => {
                return Err(LongDocumentBackendError::new(format!(
                    "Could not prepare TATR download client: {error}"
                )));
            }
            Err(_) => return Ok(None),
        };
        if let Err(error) = ensure_tatr_model_available_for_directory(
            &mut client,
            base,
            &LayoutModelDownloadConfig::default(),
            &mut |_| {},
        ) {
            if fatal_setup_errors {
                return Err(LongDocumentBackendError::new(format!(
                    "Could not ensure TATR local model: {error}"
                )));
            }
            return Ok(None);
        }
    }

    if !paths.tatr_model_path.is_file() {
        if fatal_setup_errors {
            return Err(LongDocumentBackendError::new(format!(
                "TATR local model is not available: missing '{}'",
                paths.tatr_model_path.display()
            )));
        }
        return Ok(None);
    }

    match TatrOnnxSession::from_model_paths(runtime_dir, &paths.tatr_model_path) {
        Ok(session) => Ok(Some(session)),
        Err(error) if fatal_setup_errors => Err(LongDocumentBackendError::new(format!(
            "Could not load TATR table structure model: {error}"
        ))),
        Err(_) => Ok(None),
    }
}

fn native_pdf_tatr_recognition_result(
    request: &LongDocumentServiceRequest,
    page_number: usize,
    result: Result<Option<TableStructure>, TatrOnnxError>,
) -> Result<Option<TableStructure>, LongDocumentBackendError> {
    match result {
        Ok(table) => Ok(table),
        Err(error) if native_pdf_tatr_errors_are_fatal(request) => {
            Err(LongDocumentBackendError::new(format!(
                "TATR table structure recognition failed on page {page_number}: {error}"
            )))
        }
        Err(_) => Ok(None),
    }
}

fn native_pdf_should_ensure_doc_layout_yolo(request: &LongDocumentServiceRequest) -> bool {
    native_pdf_layout_detection_mode(request) == NativePdfLayoutDetectionMode::OnnxLocal
        && native_pdf_managed_layout_model_base(request).is_some()
}

fn native_pdf_should_lazy_ensure_tatr(request: &LongDocumentServiceRequest) -> bool {
    native_pdf_tatr_table_structure_enabled(request)
        && native_pdf_layout_mode_uses_doc_layout_yolo(native_pdf_layout_detection_mode(request))
        && request
            .settings
            .tatr_model_path
            .as_deref()
            .map(str::trim)
            .is_none_or(str::is_empty)
        && native_pdf_managed_layout_model_base(request).is_some()
}

fn native_pdf_tatr_errors_are_fatal(request: &LongDocumentServiceRequest) -> bool {
    native_pdf_layout_detection_mode(request) == NativePdfLayoutDetectionMode::OnnxLocal
}

fn native_pdf_managed_layout_model_base(request: &LongDocumentServiceRequest) -> Option<PathBuf> {
    if request
        .settings
        .doc_layout_yolo_path
        .as_deref()
        .map(str::trim)
        .is_some_and(|value| !value.is_empty())
    {
        return None;
    }

    request
        .settings
        .cache_dir_path()
        .or_else(|| default_model_cache_dir().parent().map(Path::to_path_buf))
}

fn native_pdf_vision_layout_config(
    request: &LongDocumentServiceRequest,
) -> Result<Option<OpenAiCompatibleConfig>, LongDocumentBackendError> {
    if native_pdf_layout_detection_mode(request) != NativePdfLayoutDetectionMode::VisionLlm {
        return Ok(None);
    }

    let endpoint = request
        .params
        .vision_endpoint
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            LongDocumentBackendError::new("Vision layout endpoint is not configured.")
        })?;
    let model = request
        .params
        .vision_model
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| LongDocumentBackendError::new("Vision layout model is not configured."))?;
    let api_key = request
        .params
        .vision_api_key
        .as_deref()
        .map(str::trim)
        .unwrap_or_default();
    let mut config = OpenAiCompatibleConfig::new(endpoint, model).with_api_key(api_key);
    if api_key.is_empty() && endpoint_is_local_http(endpoint) {
        config = config.without_required_api_key();
    } else if config.requires_api_key && api_key.is_empty() {
        return Err(LongDocumentBackendError::new(
            "Vision layout API key is not configured.",
        ));
    }

    Ok(Some(config))
}

fn endpoint_is_local_http(endpoint: &str) -> bool {
    let Ok(url) = reqwest::Url::parse(endpoint) else {
        return false;
    };
    url.host_str()
        .is_some_and(|host| matches!(host, "localhost" | "127.0.0.1" | "::1"))
}

fn vision_layout_detection_to_doc_layout_detection(
    detection: &VisionLayoutDetection,
) -> Option<DocLayoutYoloDetection> {
    let region_type = match detection.region_type {
        VisionLayoutRegionType::Body => DocLayoutRegionType::Body,
        VisionLayoutRegionType::Table | VisionLayoutRegionType::TableLike => {
            DocLayoutRegionType::Table
        }
        VisionLayoutRegionType::Figure => DocLayoutRegionType::Figure,
        VisionLayoutRegionType::Formula => DocLayoutRegionType::Formula,
        VisionLayoutRegionType::Caption => DocLayoutRegionType::Caption,
        VisionLayoutRegionType::Title => DocLayoutRegionType::Title,
        VisionLayoutRegionType::IsolatedFormula => DocLayoutRegionType::IsolatedFormula,
        VisionLayoutRegionType::Unknown
        | VisionLayoutRegionType::Header
        | VisionLayoutRegionType::Footer
        | VisionLayoutRegionType::LeftColumn
        | VisionLayoutRegionType::RightColumn => return None,
    };

    Some(DocLayoutYoloDetection {
        region_type,
        confidence: detection.confidence,
        x: detection.x,
        y: detection.y,
        width: detection.width,
        height: detection.height,
    })
}

fn native_pdf_doc_layout_yolo_paths(
    request: &LongDocumentServiceRequest,
) -> Option<LayoutModelPaths> {
    if !native_pdf_layout_mode_uses_doc_layout_yolo(native_pdf_layout_detection_mode(request)) {
        return None;
    }

    if let Some(model_path) = request
        .settings
        .doc_layout_yolo_path
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
    {
        let models_dir = model_path.parent()?.to_path_buf();
        let tatr_model_path = request
            .settings
            .tatr_model_path
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(PathBuf::from)
            .unwrap_or_else(|| models_dir.join(TATR_MODEL_FILE_NAME));
        return Some(LayoutModelPaths {
            native_lib_path: models_dir.join(ONNX_RUNTIME_FILE_NAME),
            doc_layout_model_path: model_path,
            tatr_model_path,
            models_dir,
        });
    }

    let Some(cache_dir) = request.settings.cache_dir_path() else {
        let models_dir = default_model_cache_dir();
        return Some(LayoutModelPaths {
            native_lib_path: models_dir.join(ONNX_RUNTIME_FILE_NAME),
            doc_layout_model_path: models_dir.join(DOC_LAYOUT_MODEL_FILE_NAME),
            tatr_model_path: models_dir.join(TATR_MODEL_FILE_NAME),
            models_dir,
        });
    };

    Some(LayoutModelPaths::for_base(cache_dir))
}

fn native_pdf_layout_temp_dir(path: &Path) -> PathBuf {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default();
    let stem = path
        .file_stem()
        .and_then(|stem| stem.to_str())
        .map(sanitize_temp_path_component)
        .filter(|stem| !stem.is_empty())
        .unwrap_or_else(|| "document".to_string());

    env::temp_dir()
        .join("easydict-pdf-layout")
        .join(format!("{}-{stamp}-{stem}", process::id()))
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
        let preferred_language_tag =
            document_language_to_ocr_tag(&request.params.from).map(str::to_string);
        for page in pages {
            let params = OcrRecognizeParams {
                pixel_data_path: page.pixel_data_path.display().to_string(),
                pixel_width: page.pixel_width,
                pixel_height: page.pixel_height,
                preferred_language_tag: preferred_language_tag.clone(),
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
    worker_policy: RuntimeRoutePolicy,
) -> Option<LongDocumentBackendError> {
    local_long_document_file_input_error(request)
        .or_else(|| local_long_document_route_preflight_error(request))
        .or_else(|| local_long_document_local_ai_native_preflight_error(request))
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

fn local_long_document_local_ai_native_preflight_error(
    request: &LongDocumentServiceRequest,
) -> Option<LongDocumentBackendError> {
    if !is_local_ai_long_document_service_id(&request.params.service_id) {
        return None;
    }

    let quick_request = native_quick_translate_request_for_chunk(request, "native route probe")?;
    local_ai_quick_translate_native_preflight_error(&quick_request)
        .map(LongDocumentBackendError::new)
}

fn local_long_document_local_ai_worker_bridge_error(
    request: &LongDocumentServiceRequest,
) -> Option<LongDocumentBackendError> {
    if !is_local_ai_long_document_service_id(&request.params.service_id) {
        return None;
    }

    Some(LongDocumentBackendError::new(
        "Windows Local AI Long Document translation requires a Rust-native route for this request.",
    ))
}

fn local_long_document_retained_worker_disabled_error(
    worker_policy: RuntimeRoutePolicy,
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
    validate_native_pdf_input_file(path)?;

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

fn validate_native_pdf_input_file(path: &str) -> Result<(), LongDocumentBackendError> {
    let metadata = fs::metadata(path).map_err(|error| {
        LongDocumentBackendError::new(format!("Could not read PDF document '{}': {error}", path))
    })?;
    if !metadata.is_file() {
        return Err(LongDocumentBackendError::new(format!(
            "Could not read PDF document '{}': path is not a file",
            path
        )));
    }

    Ok(())
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
    fn native_pdf_source_extractor_error_uses_text_fallback_before_ocr() {
        let request = test_pdf_long_document_request(None);
        let calls = Arc::new(Mutex::new(Vec::new()));
        let source_calls = Arc::clone(&calls);
        let text_calls = Arc::clone(&calls);
        let ocr_calls = Arc::clone(&calls);

        let chunks = read_native_pdf_source_chunks_with_fallbacks(
            &request,
            "paper.pdf",
            Some("1"),
            move |_request, path, page_range| {
                source_calls
                    .lock()
                    .unwrap()
                    .push(format!("source:{path}:{}", page_range.unwrap_or_default()));
                Err(LongDocumentBackendError::new("source extractor failed"))
            },
            move |path, page_range| {
                text_calls
                    .lock()
                    .unwrap()
                    .push(format!("text:{path}:{}", page_range.unwrap_or_default()));
                Ok(vec![NativeTextSourceChunk::plain(
                    0,
                    "fallback text".to_string(),
                )])
            },
            move |_request, path, page_range| {
                ocr_calls
                    .lock()
                    .unwrap()
                    .push(format!("ocr:{path}:{}", page_range.unwrap_or_default()));
                Err(LongDocumentBackendError::new("ocr should not run"))
            },
        )
        .expect("text fallback should recover from source extractor errors");

        assert_eq!(chunks.len(), 1);
        assert_eq!(chunks[0].text, "fallback text");
        assert_eq!(
            *calls.lock().unwrap(),
            vec!["source:paper.pdf:1", "text:paper.pdf:1"]
        );
    }

    #[test]
    fn native_pdf_result_json_path_prechecked_before_source_reader_or_ocr() {
        let temp_dir = unique_longdoc_test_dir("pdf-result-json-preflight-before-source");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let input_path = temp_dir.join("scan.pdf");
        let output_path = temp_dir.join("scan-translated.pdf");
        let result_json_path = temp_dir.join("scan-result.json");
        fs::create_dir_all(&result_json_path).expect("conflicting sidecar directory");

        let request = LongDocumentServiceRequest {
            query_id: 98,
            input: LongDocumentInput::File(input_path.display().to_string()),
            params: TranslateDocumentParams {
                input_path: input_path.display().to_string(),
                output_path: Some(output_path.display().to_string()),
                input_mode: "Pdf".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "google".to_string(),
                output_mode: "Both".to_string(),
                pdf_export_mode: Some("ContentStreamReplacement".to_string()),
                layout_detection: None,
                page_range: None,
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: Some(result_json_path.display().to_string()),
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot::default(),
        };
        let calls = Arc::new(Mutex::new(Vec::new()));
        let source_calls = Arc::clone(&calls);
        let mut translator = PrefixNativeLongDocTranslator::default();

        let run = run_native_text_long_document_request_inner_with_source_reader(
            &mut translator,
            &request,
            &|| false,
            move |_request, input_kind| {
                source_calls.lock().unwrap().push(format!("{input_kind:?}"));
                Ok(vec![NativeTextSourceChunk::pdf_ocr(
                    0,
                    "OCR should not run".to_string(),
                    1,
                )])
            },
        );
        let error = run
            .result
            .expect_err("invalid result JSON path should fail before reading PDF source");

        assert!(error.message.contains("Long document output path"));
        assert!(error.message.contains("is a directory"));
        assert!(
            calls.lock().unwrap().is_empty(),
            "PDF source extraction and OCR fallback should not run when output preflight fails"
        );
        assert!(translator.calls().is_empty());
        assert!(!output_path.exists());

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_runner_source_extractor_error_falls_back_to_content_stream_and_writes_result_json(
    ) {
        let temp_dir = unique_longdoc_test_dir("pdf-source-fallback-runner");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let input_path = temp_dir.join("paper.pdf");
        let output_path = temp_dir.join("paper-translated.pdf");
        let result_json_path = temp_dir.join("paper-result.json");
        fs::write(
            &input_path,
            minimal_longdoc_test_pdf_with_pages(&["Fallback PDF"]),
        )
        .expect("input pdf");

        let request = LongDocumentServiceRequest {
            query_id: 96,
            input: LongDocumentInput::File(input_path.display().to_string()),
            params: TranslateDocumentParams {
                input_path: input_path.display().to_string(),
                output_path: Some(output_path.display().to_string()),
                input_mode: "Pdf".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "google".to_string(),
                output_mode: "Both".to_string(),
                pdf_export_mode: Some("ContentStreamReplacement".to_string()),
                layout_detection: None,
                page_range: None,
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: Some(result_json_path.display().to_string()),
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot::default(),
        };
        let calls = Arc::new(Mutex::new(Vec::new()));
        let source_calls = Arc::clone(&calls);
        let text_calls = Arc::clone(&calls);
        let ocr_calls = Arc::clone(&calls);
        let mut translator = PrefixNativeLongDocTranslator::default();

        let run = run_native_text_long_document_request_inner_with_source_reader(
            &mut translator,
            &request,
            &|| false,
            move |request, input_kind| {
                assert_eq!(input_kind, NativeTextInputKind::PdfText);
                read_native_pdf_source_chunks_with_fallbacks(
                    request,
                    &request.params.input_path,
                    request.params.page_range.as_deref(),
                    {
                        let source_calls = Arc::clone(&source_calls);
                        move |_request, _path, _page_range| {
                            source_calls.lock().unwrap().push("source".to_string());
                            Err(LongDocumentBackendError::new(
                                "source extractor unavailable",
                            ))
                        }
                    },
                    {
                        let text_calls = Arc::clone(&text_calls);
                        move |path, page_range| {
                            text_calls.lock().unwrap().push("text".to_string());
                            read_native_pdf_text_source_chunks(path, page_range)
                        }
                    },
                    {
                        let ocr_calls = Arc::clone(&ocr_calls);
                        move |_request, _path, _page_range| {
                            ocr_calls.lock().unwrap().push("ocr".to_string());
                            Err(LongDocumentBackendError::new("OCR should not run"))
                        }
                    },
                )
            },
        );

        let result = run.result.expect("fallback runner should complete");
        assert_eq!(result.state, "Completed");
        assert_eq!(
            result.result_json_path.as_deref(),
            result_json_path.to_str()
        );
        assert_eq!(*calls.lock().unwrap(), vec!["source", "text"]);
        assert_eq!(translator.calls(), vec!["Fallback PDF".to_string()]);

        let output_pdf_text =
            pdf_extract::extract_text(&output_path).expect("translated PDF should extract");
        assert!(output_pdf_text.contains("[zh] Fallback PDF"));
        let bilingual_path = result
            .bilingual_output_path
            .as_deref()
            .expect("bilingual fallback output path");
        let bilingual_text = fs::read_to_string(bilingual_path).expect("bilingual fallback text");
        assert!(bilingual_text.contains("Fallback PDF"));

        let sidecar_json =
            fs::read_to_string(&result_json_path).expect("result JSON sidecar should be written");
        for retained_marker in ["Long Document worker", "CompatHost", ".NET"] {
            assert!(
                !output_pdf_text.contains(retained_marker),
                "native fallback PDF output must not mention retained marker {retained_marker}: {output_pdf_text}"
            );
            assert!(
                !bilingual_text.contains(retained_marker),
                "native fallback bilingual output must not mention retained marker {retained_marker}: {bilingual_text}"
            );
            assert!(
                !sidecar_json.contains(retained_marker),
                "native fallback sidecar must not mention retained marker {retained_marker}: {sidecar_json}"
            );
        }
        let sidecar: serde_json::Value =
            serde_json::from_str(&sidecar_json).expect("sidecar should parse");
        let quality_report_json = sidecar["qualityReport"]
            .as_str()
            .expect("native PDF sidecar should include quality report");
        let quality_report: serde_json::Value =
            serde_json::from_str(quality_report_json).expect("quality report should parse");
        assert_eq!(
            quality_report["backfillMetrics"]["candidateBlocks"],
            serde_json::json!(1)
        );
        assert_eq!(
            quality_report["backfillMetrics"]["objectReplaceBlocks"],
            serde_json::json!(1)
        );
        assert_eq!(
            quality_report["backfillMetrics"]["overlayModeBlocks"],
            serde_json::json!(0)
        );
        assert_eq!(sidecar["checkpoint"]["inputMode"], "Pdf");
        assert!(sidecar["checkpoint"]["pdf"]["sourceChunks"][0]
            .as_str()
            .unwrap_or_default()
            .contains("Fallback PDF"));
        assert!(sidecar["checkpoint"]["pdf"]["translatedChunks"]["0"]
            .as_str()
            .unwrap_or_default()
            .contains("[zh] Fallback PDF"));

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_runner_empty_source_and_text_fallback_uses_ocr_and_writes_result_json() {
        let temp_dir = unique_longdoc_test_dir("pdf-ocr-fallback-runner");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let input_path = temp_dir.join("scan.pdf");
        let output_pdf_path = temp_dir.join("scan-translated.pdf");
        let result_json_path = temp_dir.join("scan-result.json");
        fs::write(
            &input_path,
            minimal_longdoc_test_pdf_with_pages(&["Selectable text intentionally ignored"]),
        )
        .expect("input pdf");

        let request = LongDocumentServiceRequest {
            query_id: 97,
            input: LongDocumentInput::File(input_path.display().to_string()),
            params: TranslateDocumentParams {
                input_path: input_path.display().to_string(),
                output_path: Some(output_pdf_path.display().to_string()),
                input_mode: "Pdf".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "google".to_string(),
                output_mode: "Both".to_string(),
                pdf_export_mode: Some("ContentStreamReplacement".to_string()),
                layout_detection: None,
                page_range: None,
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: Some(result_json_path.display().to_string()),
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot::default(),
        };
        let calls = Arc::new(Mutex::new(Vec::new()));
        let source_calls = Arc::clone(&calls);
        let text_calls = Arc::clone(&calls);
        let ocr_calls = Arc::clone(&calls);
        let mut translator = PrefixNativeLongDocTranslator::default();

        let run = run_native_text_long_document_request_inner_with_source_reader(
            &mut translator,
            &request,
            &|| false,
            move |request, input_kind| {
                assert_eq!(input_kind, NativeTextInputKind::PdfText);
                validate_native_pdf_input_file(&request.params.input_path)?;
                read_native_pdf_source_chunks_with_fallbacks(
                    request,
                    &request.params.input_path,
                    request.params.page_range.as_deref(),
                    {
                        let source_calls = Arc::clone(&source_calls);
                        move |_request, _path, _page_range| {
                            source_calls.lock().unwrap().push("source".to_string());
                            Ok(Vec::new())
                        }
                    },
                    {
                        let text_calls = Arc::clone(&text_calls);
                        move |_path, _page_range| {
                            text_calls.lock().unwrap().push("text".to_string());
                            Ok(Vec::new())
                        }
                    },
                    {
                        let ocr_calls = Arc::clone(&ocr_calls);
                        move |_request, _path, _page_range| {
                            ocr_calls.lock().unwrap().push("ocr".to_string());
                            Ok(vec![NativeTextSourceChunk::pdf_ocr(
                                0,
                                "Scanned OCR source".to_string(),
                                1,
                            )])
                        }
                    },
                )
            },
        );

        let result = run.result.expect("OCR fallback runner should complete");
        assert_eq!(result.state, "Completed");
        assert_eq!(
            result.result_json_path.as_deref(),
            result_json_path.to_str()
        );
        assert_eq!(*calls.lock().unwrap(), vec!["source", "text", "ocr"]);
        assert_eq!(translator.calls(), vec!["Scanned OCR source".to_string()]);
        assert!(
            !output_pdf_path.exists(),
            "OCR fallback should export text instead of writing a PDF"
        );

        let output_text_path = PathBuf::from(
            result
                .output_path
                .as_deref()
                .expect("text fallback output path"),
        );
        assert_eq!(
            output_text_path
                .extension()
                .and_then(|value| value.to_str()),
            Some("txt")
        );
        assert!(fs::read_to_string(&output_text_path)
            .expect("OCR fallback text output")
            .contains("[zh] Scanned OCR source"));
        let bilingual_path = result
            .bilingual_output_path
            .as_deref()
            .expect("OCR fallback bilingual output path");
        let bilingual_text =
            fs::read_to_string(bilingual_path).expect("OCR fallback bilingual text");
        assert!(bilingual_text.contains("Scanned OCR source"));
        assert!(bilingual_text.contains("[zh] Scanned OCR source"));

        let sidecar_json = fs::read_to_string(&result_json_path)
            .expect("OCR fallback result JSON sidecar should be written");
        assert!(
            !sidecar_json.contains("Long Document worker"),
            "native OCR fallback sidecar must not mention retained workers: {sidecar_json}"
        );
        let sidecar: serde_json::Value =
            serde_json::from_str(&sidecar_json).expect("sidecar should parse");
        assert_eq!(sidecar["checkpoint"]["inputMode"], "Pdf");
        assert!(sidecar["checkpoint"]["text"]["sourceChunks"][0]
            .as_str()
            .unwrap_or_default()
            .contains("Scanned OCR source"));
        assert!(sidecar["checkpoint"]["pdf"]["sourceChunks"][0]
            .as_str()
            .unwrap_or_default()
            .contains("Scanned OCR source"));
        assert!(sidecar["checkpoint"]["pdf"]["translatedChunks"]["0"]
            .as_str()
            .unwrap_or_default()
            .contains("[zh] Scanned OCR source"));
        assert!(
            sidecar["checkpoint"]["pdf"]["chunkMetadata"][0]["sourceBlockId"]
                .as_str()
                .unwrap_or_default()
                .contains("-ocr-")
        );
        let typed_sidecar =
            read_native_result_json_sidecar(&result_json_path).expect("typed OCR sidecar");
        let retry_chunks = native_text_source_chunks_from_retry_checkpoint(
            NativeTextInputKind::PdfText,
            &typed_sidecar.checkpoint,
        )
        .expect("OCR retry chunks");
        assert_eq!(retry_chunks[0].source_kind, NativeTextSourceKind::PdfOcr);

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn auto_foundry_local_probe_routes_long_document_to_native_before_worker() {
        let mut resolver =
            TestFoundryEndpointResolver::new(Some("foundry-local-invalid".to_string()));
        let mut windows_ai_probe =
            TestWindowsAiProbe::new(WindowsAiReadyState::NotSupportedOnCurrentSystem);
        let request = test_long_document_request_for_windows_local_ai();

        let dispatch = try_run_native_text_long_document_request_with_auto_local_ai_probes(
            request,
            &mut windows_ai_probe,
            &mut resolver,
        );

        assert_eq!(windows_ai_probe.calls, 1);
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
    fn native_route_helper_uses_auto_foundry_probe_before_worker_backend() {
        let mut resolver =
            TestFoundryEndpointResolver::new(Some("foundry-local-invalid".to_string()));
        let mut windows_ai_probe =
            TestWindowsAiProbe::new(WindowsAiReadyState::NotSupportedOnCurrentSystem);
        let request = test_long_document_request_for_windows_local_ai();
        let mut backend = RecordingLongDocumentBackend::default();

        let outcome = run_long_document_request_with_native_route_and_foundry_resolver(
            &mut backend,
            request,
            &mut windows_ai_probe,
            &mut resolver,
        );

        assert_eq!(windows_ai_probe.calls, 1);
        assert_eq!(resolver.calls, 1);
        assert_eq!(backend.translate_calls, 0);
        let error = outcome
            .result
            .expect_err("invalid Foundry endpoint should fail in native provider route");
        assert!(
            !error.message.contains("Long Document worker"),
            "native route helper should not start retained LongDoc worker: {}",
            error.message
        );
        assert!(
            !error.message.contains("retained .NET workers"),
            "native route helper should not report retained worker requirement: {}",
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
        let mut request = test_pdf_long_document_request(None);
        request.params.from = "Japanese".to_string();

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
                    preferred_language_tag: Some("ja-JP".to_string()),
                },
                OcrRecognizeParams {
                    pixel_data_path: "page-3.bgra".to_string(),
                    pixel_width: 10,
                    pixel_height: 7,
                    preferred_language_tag: Some("ja-JP".to_string()),
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
    fn native_pdf_text_extraction_error_can_fall_back_to_ocr_chunks() {
        let request = test_pdf_long_document_request(None);
        let mut text_calls = Vec::new();
        let mut ocr_calls = Vec::new();

        let chunks = read_native_pdf_text_or_ocr_source_chunks(
            &request,
            "scan.pdf",
            Some("2"),
            |path, page_range| {
                text_calls.push((path.to_string(), page_range.map(str::to_string)));
                Err(LongDocumentBackendError::new(
                    "Could not extract PDF text 'scan.pdf': broken content stream",
                ))
            },
            |request, path, page_range| {
                ocr_calls.push((
                    request.query_id,
                    path.to_string(),
                    page_range.map(str::to_string),
                ));
                Ok(vec![NativeTextSourceChunk::pdf_ocr(
                    0,
                    "Scanned OCR text".to_string(),
                    2,
                )])
            },
        )
        .expect("OCR fallback should provide source chunks");

        assert_eq!(
            text_calls,
            vec![("scan.pdf".to_string(), Some("2".to_string()))]
        );
        assert_eq!(
            ocr_calls,
            vec![(
                request.query_id,
                "scan.pdf".to_string(),
                Some("2".to_string())
            )]
        );
        assert_eq!(chunks.len(), 1);
        assert_eq!(chunks[0].text, "Scanned OCR text");
        assert_eq!(chunks[0].source_kind, NativeTextSourceKind::PdfOcr);
        assert_eq!(chunks[0].page_number, 2);
    }

    #[test]
    fn native_pdf_text_extraction_error_preserves_ocr_failure_diagnostic() {
        let request = test_pdf_long_document_request(None);

        let error = read_native_pdf_text_or_ocr_source_chunks(
            &request,
            "scan.pdf",
            None,
            |_, _| {
                Err(LongDocumentBackendError::new(
                    "Could not extract PDF text 'scan.pdf': invalid xref",
                ))
            },
            |_, _, _| {
                Err(LongDocumentBackendError::new(
                    "Could not render PDF pages for OCR 'scan.pdf': invalid page tree",
                ))
            },
        )
        .expect_err("failed text extraction and failed OCR should stay local");

        assert!(error.message.contains("Could not extract PDF text"));
        assert!(error.message.contains("OCR fallback failed"));
        assert!(error.message.contains("Could not render PDF pages for OCR"));
    }

    #[test]
    fn document_language_to_ocr_tag_maps_known_languages_and_preserves_auto() {
        assert_eq!(document_language_to_ocr_tag("Auto"), None);
        assert_eq!(document_language_to_ocr_tag("Japanese"), Some("ja-JP"));
        assert_eq!(document_language_to_ocr_tag("ja"), Some("ja-JP"));
        assert_eq!(
            document_language_to_ocr_tag("SimplifiedChinese"),
            Some("zh-CN")
        );
        assert_eq!(
            document_language_to_ocr_tag("TraditionalChinese"),
            Some("zh-TW")
        );
        assert_eq!(document_language_to_ocr_tag("unknown"), None);
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
            &BTreeSet::new(),
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
        let cjk_font_path = install_managed_test_cjk_font(&temp_dir);

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
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot {
                cache_dir: Some(temp_dir.display().to_string()),
                cjk_font_path: Some(cjk_font_path.display().to_string()),
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
        let text_checkpoint =
            native_export_checkpoint(NativeTextInputKind::PdfText, &source_chunks, &translations);

        let export = try_export_native_pdf_document(
            &request,
            &source_chunks,
            &translations,
            "",
            &BTreeSet::new(),
            &text_checkpoint,
        )
        .expect("CJK overlay PDF export should not fail")
        .expect("CJK overlay should handle native PDF export");

        assert_eq!(export.output_path, output_path.display().to_string());
        assert!(export.bilingual_output_path.is_none());
        let metrics = export
            .backfill_metrics
            .as_ref()
            .expect("overlay PDF export should report backfill metrics");
        assert_eq!(metrics["candidateBlocks"], serde_json::json!(1));
        assert_eq!(metrics["renderedBlocks"], serde_json::json!(1));
        assert_eq!(metrics["objectReplaceBlocks"], serde_json::json!(0));
        assert_eq!(metrics["overlayModeBlocks"], serde_json::json!(1));
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
    fn native_pdf_overlay_font_fallback_uses_settings_cache_dir() {
        let temp_dir = unique_longdoc_test_dir("pdf-cjk-overlay-cache-dir");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let input_path = temp_dir.join("paper.pdf");
        let output_path = temp_dir.join("paper-translated.pdf");
        fs::write(
            &input_path,
            minimal_longdoc_test_pdf_with_pages(&["Original PDF text"]),
        )
        .expect("input pdf");
        install_managed_test_cjk_font(&temp_dir);

        let request = LongDocumentServiceRequest {
            query_id: 96,
            input: LongDocumentInput::File(input_path.display().to_string()),
            params: TranslateDocumentParams {
                input_path: input_path.display().to_string(),
                output_path: Some(output_path.display().to_string()),
                input_mode: "Pdf".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "google".to_string(),
                output_mode: "Monolingual".to_string(),
                pdf_export_mode: Some("Overlay".to_string()),
                layout_detection: None,
                page_range: None,
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: None,
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot {
                cache_dir: Some(temp_dir.display().to_string()),
                ..SettingsSnapshot::default()
            },
        };
        let source_chunks = vec![NativeTextSourceChunk {
            text: "Text that does not exist in the PDF stream".to_string(),
            fallback_text: None,
            page_number: 1,
            source_block_id: "pdf-p1-body-b1".to_string(),
            source_kind: NativeTextSourceKind::PdfSourceBlock,
            pdf_context: None,
            pdf_export_metadata: Some(PdfExportChunkMetadata {
                chunk_index: 0,
                page_number: 1,
                source_block_id: "pdf-p1-body-b1".to_string(),
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
        let translations = vec![Some("缓存字体".to_string())];
        let text_checkpoint =
            native_export_checkpoint(NativeTextInputKind::PdfText, &source_chunks, &translations);

        let export = try_export_native_pdf_document(
            &request,
            &source_chunks,
            &translations,
            "",
            &BTreeSet::new(),
            &text_checkpoint,
        )
        .expect("cache-dir CJK overlay PDF export should not fail")
        .expect("cached font should handle native PDF overlay export");

        assert_eq!(export.output_path, output_path.display().to_string());
        assert!(export.bilingual_output_path.is_none());
        let extracted =
            pdf_extract::extract_text(&output_path).expect("overlay text should extract");
        assert!(extracted.contains("缓存"));

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_overlay_font_fallback_rejects_unmanaged_cache_fonts() {
        let temp_dir = unique_longdoc_test_dir("pdf-cjk-overlay-unmanaged-fonts");
        let fonts_dir = crate::font_download::font_cache_dir(&temp_dir);
        fs::create_dir_all(&fonts_dir).expect("font cache dir");
        fs::write(fonts_dir.join("SomeOtherCjkFont.ttf"), b"font").expect("unmanaged font marker");
        fs::write(fonts_dir.join("NotoSansSC-Regular.ttf.tmp"), b"font")
            .expect("temporary font marker");

        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.display().to_string());

        let error = native_pdf_overlay_font_path(&request)
            .expect_err("unmanaged cache fonts should not satisfy CJK overlay fallback");
        assert!(
            error.contains("no cached CJK PDF overlay font"),
            "LongDoc overlay should fail through the managed font-cache boundary: {error}"
        );

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_overlay_font_rejects_unmanaged_explicit_cjk_font_path() {
        let temp_dir = unique_longdoc_test_dir("pdf-cjk-overlay-unmanaged-explicit-font");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let unmanaged_font_path = temp_dir.join("NotoSansSC-Regular.ttf");
        fs::copy(test_cjk_font_path(), &unmanaged_font_path).expect("copy unmanaged font fixture");

        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.join("cache").display().to_string());
        request.settings.cjk_font_path = Some(unmanaged_font_path.display().to_string());

        let error = native_pdf_overlay_font_path(&request)
            .expect_err("explicit CJK font outside managed cache should be rejected");
        assert!(
            error.contains("Rust-managed Noto Sans CJK"),
            "LongDoc overlay should reject unmanaged explicit CJK fonts: {error}"
        );

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_overlay_font_accepts_explicit_managed_cache_path() {
        let temp_dir = unique_longdoc_test_dir("pdf-cjk-overlay-managed-explicit-font");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let managed_font_path = install_managed_test_cjk_font(&temp_dir);

        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.display().to_string());
        request.settings.cjk_font_path = Some(managed_font_path.display().to_string());

        let actual =
            native_pdf_overlay_font_path(&request).expect("managed explicit CJK font path");
        assert_eq!(actual, managed_font_path);

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_overlay_font_rejects_invalid_explicit_managed_cjk_font_path() {
        let temp_dir = unique_longdoc_test_dir("pdf-cjk-overlay-invalid-managed-explicit-font");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let managed_font_path =
            crate::font_download::font_cache_dir(&temp_dir).join("NotoSansSC-Regular.ttf");
        fs::create_dir_all(managed_font_path.parent().expect("font cache parent"))
            .expect("font cache dir");
        fs::write(&managed_font_path, b"not a parseable CJK font")
            .expect("invalid managed font marker");

        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.display().to_string());
        request.settings.cjk_font_path = Some(managed_font_path.display().to_string());

        let error = native_pdf_overlay_font_path(&request)
            .expect_err("invalid managed explicit CJK font should be rejected");
        assert!(
            error.contains("Rust-managed Noto Sans CJK"),
            "LongDoc overlay should reject invalid explicit managed CJK font files: {error}"
        );

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_export_overlay_mode_uses_overlay_without_content_stream_match() {
        let temp_dir = unique_longdoc_test_dir("pdf-explicit-overlay-export");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let input_path = temp_dir.join("paper.pdf");
        let output_path = temp_dir.join("paper-overlay.pdf");
        fs::write(
            &input_path,
            minimal_longdoc_test_pdf_with_pages(&["Original PDF text"]),
        )
        .expect("input pdf");
        install_managed_test_cjk_font(&temp_dir);

        let request = LongDocumentServiceRequest {
            query_id: 94,
            input: LongDocumentInput::File(input_path.display().to_string()),
            params: TranslateDocumentParams {
                input_path: input_path.display().to_string(),
                output_path: Some(output_path.display().to_string()),
                input_mode: "Pdf".to_string(),
                from: "English".to_string(),
                to: "SimplifiedChinese".to_string(),
                service_id: "google".to_string(),
                output_mode: "Monolingual".to_string(),
                pdf_export_mode: Some("Overlay".to_string()),
                layout_detection: None,
                page_range: None,
                vision_endpoint: None,
                vision_api_key: None,
                vision_model: None,
                result_json_path: None,
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot {
                cache_dir: Some(temp_dir.display().to_string()),
                ..SettingsSnapshot::default()
            },
        };
        let source_chunks = vec![NativeTextSourceChunk {
            text: "Text that does not exist in the PDF stream".to_string(),
            fallback_text: None,
            page_number: 1,
            source_block_id: "pdf-p1-body-b1".to_string(),
            source_kind: NativeTextSourceKind::PdfSourceBlock,
            pdf_context: None,
            pdf_export_metadata: Some(PdfExportChunkMetadata {
                chunk_index: 0,
                page_number: 1,
                source_block_id: "pdf-p1-body-b1".to_string(),
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
        let translations = vec![Some("显式 Overlay".to_string())];
        let text_checkpoint =
            native_export_checkpoint(NativeTextInputKind::PdfText, &source_chunks, &translations);

        let export = try_export_native_pdf_document(
            &request,
            &source_chunks,
            &translations,
            "",
            &BTreeSet::new(),
            &text_checkpoint,
        )
        .expect("explicit overlay PDF export should not fail")
        .expect("explicit overlay should handle native PDF export");

        assert_eq!(export.output_path, output_path.display().to_string());
        assert!(export.bilingual_output_path.is_none());
        let metrics = export
            .backfill_metrics
            .as_ref()
            .expect("explicit overlay PDF export should report backfill metrics");
        assert_eq!(metrics["candidateBlocks"], serde_json::json!(1));
        assert_eq!(metrics["renderedBlocks"], serde_json::json!(1));
        assert_eq!(metrics["objectReplaceBlocks"], serde_json::json!(0));
        assert_eq!(metrics["overlayModeBlocks"], serde_json::json!(1));
        assert_eq!(
            lopdf::Document::load(&output_path)
                .expect("native overlay PDF output should open")
                .get_pages()
                .len(),
            1
        );
        let extracted =
            pdf_extract::extract_text(&output_path).expect("overlay text should extract");
        assert!(extracted.contains("显式"));

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_content_stream_no_match_uses_overlay_before_text_fallback() {
        let temp_dir = unique_longdoc_test_dir("pdf-no-match-overlay-export");
        fs::create_dir_all(&temp_dir).expect("temp dir");
        let input_path = temp_dir.join("paper.pdf");
        let output_path = temp_dir.join("paper-translated.pdf");
        fs::write(
            &input_path,
            minimal_longdoc_test_pdf_with_pages(&["Original PDF text"]),
        )
        .expect("input pdf");
        install_managed_test_cjk_font(&temp_dir);

        let request = LongDocumentServiceRequest {
            query_id: 95,
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
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot {
                cache_dir: Some(temp_dir.display().to_string()),
                ..SettingsSnapshot::default()
            },
        };
        let source_chunks = vec![NativeTextSourceChunk {
            text: "Text that does not exist in the PDF stream".to_string(),
            fallback_text: None,
            page_number: 1,
            source_block_id: "pdf-p1-body-b1".to_string(),
            source_kind: NativeTextSourceKind::PdfSourceBlock,
            pdf_context: None,
            pdf_export_metadata: Some(PdfExportChunkMetadata {
                chunk_index: 0,
                page_number: 1,
                source_block_id: "pdf-p1-body-b1".to_string(),
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
        let translations = vec![Some("Overlay fallback translation".to_string())];
        let text_checkpoint =
            native_export_checkpoint(NativeTextInputKind::PdfText, &source_chunks, &translations);

        let export = try_export_native_pdf_document(
            &request,
            &source_chunks,
            &translations,
            "",
            &BTreeSet::new(),
            &text_checkpoint,
        )
        .expect("content-stream no-match should try native overlay PDF export")
        .expect("native overlay should produce a PDF before text fallback");

        assert_eq!(export.output_path, output_path.display().to_string());
        assert!(export.bilingual_output_path.is_none());
        let metrics = export
            .backfill_metrics
            .as_ref()
            .expect("overlay fallback PDF export should report backfill metrics");
        assert_eq!(metrics["candidateBlocks"], serde_json::json!(1));
        assert_eq!(metrics["renderedBlocks"], serde_json::json!(1));
        assert_eq!(metrics["objectReplaceBlocks"], serde_json::json!(0));
        assert_eq!(metrics["overlayModeBlocks"], serde_json::json!(1));
        assert_eq!(
            lopdf::Document::load(&output_path)
                .expect("native overlay PDF output should open")
                .get_pages()
                .len(),
            1
        );
        let extracted =
            pdf_extract::extract_text(&output_path).expect("overlay text should extract");
        assert!(extracted.contains("Overlay fallback translation"));

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
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot::default(),
        };
        let source_chunks = vec![NativeTextSourceChunk::simple_pdf_text(
            0,
            "Text that does not exist in the PDF stream".to_string(),
            0,
        )];
        let translations = vec![Some("Translated fallback text".to_string())];
        let text_checkpoint =
            native_export_checkpoint(NativeTextInputKind::PdfText, &source_chunks, &translations);

        let export = try_export_native_pdf_document(
            &request,
            &source_chunks,
            &translations,
            "",
            &BTreeSet::new(),
            &text_checkpoint,
        )
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
                request_timeout_ms: None,
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
                request_timeout_ms: None,
            },
            settings: SettingsSnapshot {
                ocr_engine: Some("WindowsNative".to_string()),
                ..SettingsSnapshot::default()
            },
        }
    }

    #[test]
    fn native_pdf_layout_mode_uses_doc_layout_yolo_for_auto_and_onnx_only() {
        let mut request = test_pdf_long_document_request(None);

        assert_eq!(
            native_pdf_layout_detection_mode(&request),
            NativePdfLayoutDetectionMode::Auto
        );
        assert!(native_pdf_doc_layout_yolo_paths(&request).is_some());

        request.params.layout_detection = Some("Heuristic".to_string());
        assert_eq!(
            native_pdf_layout_detection_mode(&request),
            NativePdfLayoutDetectionMode::Heuristic
        );
        assert!(native_pdf_doc_layout_yolo_paths(&request).is_none());

        request.params.layout_detection = Some("VisionLLM".to_string());
        assert_eq!(
            native_pdf_layout_detection_mode(&request),
            NativePdfLayoutDetectionMode::VisionLlm
        );
        assert!(native_pdf_doc_layout_yolo_paths(&request).is_none());

        request.params.layout_detection = Some("OnnxLocal".to_string());
        assert_eq!(
            native_pdf_layout_detection_mode(&request),
            NativePdfLayoutDetectionMode::OnnxLocal
        );
        assert!(native_pdf_doc_layout_yolo_paths(&request).is_some());
    }

    #[test]
    fn build_long_document_request_populates_gemini_vision_layout_params() {
        let mut state = EasydictUiState::default();
        state.long_document.selected_file = "paper.pdf".to_string();
        state.long_document.target_language = "SimplifiedChinese".to_string();
        state.settings.layout_detection_mode = "VisionLLM".to_string();
        state.settings.vision_layout_service = "gemini".to_string();
        let gemini = state
            .settings
            .service_provider_settings
            .iter_mut()
            .find(|provider| provider.service_id == "gemini")
            .expect("gemini provider");
        gemini.api_key = "gemini-key".to_string();
        gemini.model = "gemini-2.5-pro".to_string();

        let request = build_long_document_request(&state, 9).expect("long document request");

        assert_eq!(
            request.params.vision_endpoint.as_deref(),
            Some(VISION_LAYOUT_GEMINI_OPENAI_ENDPOINT)
        );
        assert_eq!(request.params.vision_api_key.as_deref(), Some("gemini-key"));
        assert_eq!(
            request.params.vision_model.as_deref(),
            Some("gemini-2.5-pro")
        );
    }

    #[test]
    fn native_pdf_vision_layout_config_allows_local_endpoint_without_api_key() {
        let mut request = test_pdf_long_document_request(None);
        request.params.layout_detection = Some("VisionLLM".to_string());
        request.params.vision_endpoint =
            Some("http://localhost:11434/v1/chat/completions".to_string());
        request.params.vision_model = Some("llava".to_string());

        let config = native_pdf_vision_layout_config(&request)
            .expect("vision config result")
            .expect("vision config");

        assert!(config.is_configured());
        assert!(!config.requires_api_key);
    }

    #[test]
    fn explicit_vision_layout_config_surfaces_missing_required_settings() {
        let mut request = test_pdf_long_document_request(None);

        assert_eq!(
            native_pdf_vision_layout_config(&request)
                .expect("auto layout config result")
                .as_ref(),
            None
        );

        request.params.layout_detection = Some("VisionLLM".to_string());
        let error = native_pdf_vision_layout_config(&request)
            .expect_err("explicit VisionLLM should require an endpoint");
        assert_eq!(error.message, "Vision layout endpoint is not configured.");

        request.params.vision_endpoint =
            Some("https://api.openai.com/v1/chat/completions".to_string());
        let error = native_pdf_vision_layout_config(&request)
            .expect_err("explicit VisionLLM should require a model");
        assert_eq!(error.message, "Vision layout model is not configured.");

        request.params.vision_model = Some("gpt-4o-mini".to_string());
        let error = native_pdf_vision_layout_config(&request)
            .expect_err("remote VisionLLM should require an API key");
        assert_eq!(error.message, "Vision layout API key is not configured.");

        request.params.vision_api_key = Some("vision-key".to_string());
        let config = native_pdf_vision_layout_config(&request)
            .expect("configured VisionLLM result")
            .expect("configured VisionLLM config");
        assert!(config.is_configured());
        assert!(config.requires_api_key);
    }

    #[test]
    fn vision_layout_backend_errors_preserve_page_number_and_provider_message() {
        let error = vision_layout_page_backend_error(
            7,
            OpenAiExecutionError::new(
                crate::openai_compatible::OpenAiExecutionErrorCode::InvalidResponse,
                "Vision layout API error (500): upstream failed",
            ),
        );

        assert_eq!(
            error.message,
            "Vision layout detection failed on page 7: Vision layout API error (500): upstream failed"
        );
    }

    #[test]
    fn native_pdf_layout_paths_use_cache_dir_when_no_explicit_model_path() {
        let temp_dir = unique_longdoc_test_dir("layout-cache-paths");
        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());

        let actual = native_pdf_doc_layout_yolo_paths(&request).expect("cache paths");
        let expected = LayoutModelPaths::for_base(&temp_dir);

        assert_eq!(actual.models_dir, expected.models_dir);
        assert_eq!(actual.native_lib_path, expected.native_lib_path);
        assert_eq!(actual.doc_layout_model_path, expected.doc_layout_model_path);
        assert_eq!(actual.tatr_model_path, expected.tatr_model_path);
    }

    #[test]
    fn native_pdf_layout_paths_use_explicit_doc_layout_model_directory() {
        let temp_dir = unique_longdoc_test_dir("layout-explicit-paths");
        let models_dir = temp_dir.join("models");
        let doc_layout_path = models_dir.join("custom-doclayout.onnx");
        let tatr_path = temp_dir.join("table.onnx");
        let mut request = test_pdf_long_document_request(None);
        request.settings.doc_layout_yolo_path = Some(doc_layout_path.to_string_lossy().to_string());
        request.settings.tatr_model_path = Some(tatr_path.to_string_lossy().to_string());

        let actual = native_pdf_doc_layout_yolo_paths(&request).expect("explicit paths");

        assert_eq!(actual.models_dir, models_dir);
        assert_eq!(
            actual.native_lib_path,
            actual.models_dir.join(ONNX_RUNTIME_FILE_NAME)
        );
        assert_eq!(actual.doc_layout_model_path, doc_layout_path);
        assert_eq!(actual.tatr_model_path, tatr_path);
    }

    #[test]
    fn native_pdf_tatr_table_structure_defaults_enabled_and_honors_settings_kill_switch() {
        let mut request = test_pdf_long_document_request(None);

        assert!(native_pdf_tatr_table_structure_enabled(&request));

        request.settings.enable_tatr_table_structure = Some(false);
        assert!(!native_pdf_tatr_table_structure_enabled(&request));

        request.settings.enable_tatr_table_structure = Some(true);
        assert!(native_pdf_tatr_table_structure_enabled(&request));
    }

    #[test]
    fn native_pdf_doc_layout_ensure_runs_only_for_explicit_managed_onnxlocal() {
        let temp_dir = unique_longdoc_test_dir("layout-managed-ensure");
        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());

        assert_eq!(
            native_pdf_layout_detection_mode(&request),
            NativePdfLayoutDetectionMode::Auto
        );
        assert!(!native_pdf_should_ensure_doc_layout_yolo(&request));
        assert_eq!(
            native_pdf_managed_layout_model_base(&request),
            Some(temp_dir.clone())
        );

        request.params.layout_detection = Some("OnnxLocal".to_string());
        assert!(native_pdf_should_ensure_doc_layout_yolo(&request));

        request.settings.doc_layout_yolo_path =
            Some(temp_dir.join("custom.onnx").display().to_string());
        assert!(!native_pdf_should_ensure_doc_layout_yolo(&request));
        assert!(native_pdf_managed_layout_model_base(&request).is_none());

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn explicit_managed_onnx_layout_ensure_surfaces_download_client_errors() {
        let temp_dir = unique_longdoc_test_dir("layout-managed-ensure-error");
        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());
        request.settings.proxy_enabled = Some(true);
        request.settings.proxy_uri = Some("http://[invalid-proxy".to_string());

        assert!(
            ensure_native_pdf_doc_layout_yolo_if_explicitly_requested(&request).is_ok(),
            "Auto layout mode should not attempt a lazy DocLayout-YOLO download"
        );

        request.params.layout_detection = Some("OnnxLocal".to_string());
        let error = ensure_native_pdf_doc_layout_yolo_if_explicitly_requested(&request)
            .expect_err("explicit OnnxLocal layout should surface download setup errors");

        assert!(
            error
                .message
                .contains("Could not prepare DocLayout-YOLO download client"),
            "unexpected error: {}",
            error.message
        );
        assert!(
            error
                .message
                .contains("Invalid resource download proxy URI"),
            "unexpected error: {}",
            error.message
        );

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn auto_tatr_lazy_ensure_keeps_download_client_errors_best_effort() {
        let temp_dir = unique_longdoc_test_dir("layout-tatr-auto-client-error");
        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());
        request.settings.proxy_enabled = Some(true);
        request.settings.proxy_uri = Some("http://[invalid-proxy".to_string());

        let paths = LayoutModelPaths::for_base(&temp_dir);
        let runtime_dir = paths.native_lib_path.parent().expect("runtime dir");
        let session = load_or_ensure_native_pdf_tatr_session(&request, runtime_dir, &paths)
            .expect("Auto TATR setup should stay best-effort");

        assert!(session.is_none());

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn explicit_managed_onnx_tatr_lazy_ensure_surfaces_download_client_errors() {
        let temp_dir = unique_longdoc_test_dir("layout-tatr-managed-ensure-error");
        let mut request = test_pdf_long_document_request(None);
        request.params.layout_detection = Some("OnnxLocal".to_string());
        request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());
        request.settings.proxy_enabled = Some(true);
        request.settings.proxy_uri = Some("http://[invalid-proxy".to_string());

        let paths = LayoutModelPaths::for_base(&temp_dir);
        let runtime_dir = paths.native_lib_path.parent().expect("runtime dir");
        let error = load_or_ensure_native_pdf_tatr_session(&request, runtime_dir, &paths)
            .err()
            .expect("explicit OnnxLocal TATR setup should surface download setup errors");

        assert!(
            error
                .message
                .contains("Could not prepare TATR download client"),
            "unexpected error: {}",
            error.message
        );
        assert!(
            error
                .message
                .contains("Invalid resource download proxy URI"),
            "unexpected error: {}",
            error.message
        );

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn auto_tatr_recognition_errors_remain_best_effort() {
        let request = test_pdf_long_document_request(None);
        let result = native_pdf_tatr_recognition_result(
            &request,
            7,
            Err(TatrOnnxError::Session("synthetic TATR failure".to_string())),
        )
        .expect("Auto TATR recognition errors should stay best-effort");

        assert!(result.is_none());
    }

    #[test]
    fn explicit_onnx_tatr_recognition_errors_surface_page_diagnostics() {
        let mut request = test_pdf_long_document_request(None);
        request.params.layout_detection = Some("OnnxLocal".to_string());

        let error = native_pdf_tatr_recognition_result(
            &request,
            7,
            Err(TatrOnnxError::Session("synthetic TATR failure".to_string())),
        )
        .expect_err("explicit OnnxLocal TATR inference should surface backend errors");

        assert!(
            error
                .message
                .contains("TATR table structure recognition failed on page 7"),
            "unexpected error: {}",
            error.message
        );
        assert!(
            error.message.contains("synthetic TATR failure"),
            "unexpected error: {}",
            error.message
        );
    }

    #[test]
    fn native_pdf_tatr_lazy_ensure_uses_managed_cache_and_honors_overrides() {
        let temp_dir = unique_longdoc_test_dir("layout-tatr-lazy-ensure");
        let mut request = test_pdf_long_document_request(None);
        request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());

        assert!(native_pdf_should_lazy_ensure_tatr(&request));

        request.params.layout_detection = Some("OnnxLocal".to_string());
        assert!(native_pdf_should_lazy_ensure_tatr(&request));

        request.settings.enable_tatr_table_structure = Some(false);
        assert!(!native_pdf_should_lazy_ensure_tatr(&request));

        request.settings.enable_tatr_table_structure = Some(true);
        request.settings.tatr_model_path =
            Some(temp_dir.join("custom-tatr.onnx").display().to_string());
        assert!(!native_pdf_should_lazy_ensure_tatr(&request));

        request.settings.tatr_model_path = None;
        request.settings.doc_layout_yolo_path =
            Some(temp_dir.join("custom-doclayout.onnx").display().to_string());
        assert!(!native_pdf_should_lazy_ensure_tatr(&request));

        request.settings.doc_layout_yolo_path = None;
        request.params.layout_detection = Some("VisionLLM".to_string());
        assert!(!native_pdf_should_lazy_ensure_tatr(&request));

        fs::remove_dir_all(&temp_dir).ok();
    }

    #[test]
    fn native_pdf_managed_layout_paths_match_download_base() {
        let temp_dir = unique_longdoc_test_dir("layout-managed-base");
        let mut request = test_pdf_long_document_request(None);
        request.params.layout_detection = Some("OnnxLocal".to_string());
        request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());

        let base = native_pdf_managed_layout_model_base(&request).expect("managed base");
        let actual = native_pdf_doc_layout_yolo_paths(&request).expect("layout paths");
        let expected = LayoutModelPaths::for_base(&base);

        assert_eq!(base, temp_dir);
        assert_eq!(actual, expected);

        fs::remove_dir_all(&base).ok();
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

    fn install_managed_test_cjk_font(cache_root: &Path) -> PathBuf {
        let managed_font_path =
            crate::font_download::font_cache_dir(cache_root).join("NotoSansSC-Regular.ttf");
        fs::create_dir_all(managed_font_path.parent().expect("font cache parent"))
            .expect("font cache dir");
        fs::copy(test_cjk_font_path(), &managed_font_path).expect("cache CJK font fixture");
        managed_font_path
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
        status_calls: usize,
    }

    struct TestWindowsAiProbe {
        state: WindowsAiReadyState,
        calls: usize,
    }

    impl TestFoundryEndpointResolver {
        fn new(endpoint: Option<String>) -> Self {
            Self {
                endpoint,
                calls: 0,
                status_calls: 0,
            }
        }
    }

    impl TestWindowsAiProbe {
        fn new(state: WindowsAiReadyState) -> Self {
            Self { state, calls: 0 }
        }
    }

    impl FoundryLocalEndpointResolver for TestFoundryEndpointResolver {
        fn resolve_chat_completions_endpoint(
            &mut self,
        ) -> Result<Option<String>, FoundryLocalError> {
            self.calls += 1;
            Ok(self.endpoint.clone())
        }
    }

    impl FoundryLocalRuntimeController for TestFoundryEndpointResolver {
        fn get_status(&mut self) -> Result<FoundryLocalRuntimeStatus, FoundryLocalError> {
            self.status_calls += 1;
            let state = if self.status_calls == 1 {
                FoundryLocalRuntimeState::NotRunning
            } else {
                FoundryLocalRuntimeState::Running
            };
            Ok(FoundryLocalRuntimeStatus::new(state))
        }

        fn start_service(&mut self) -> Result<(), FoundryLocalError> {
            Ok(())
        }

        fn load_model(&mut self, _model: &str) -> Result<(), FoundryLocalError> {
            Ok(())
        }
    }

    impl WindowsAiLanguageModelProbe for TestWindowsAiProbe {
        fn ready_state(&mut self) -> WindowsAiReadyState {
            self.calls += 1;
            self.state
        }
    }

    #[derive(Default)]
    struct RecordingLongDocumentBackend {
        translate_calls: usize,
    }

    impl LongDocumentBackend for RecordingLongDocumentBackend {
        fn longdoc_translate(
            &mut self,
            _params: &TranslateDocumentParams,
        ) -> Result<TranslateDocumentResult, LongDocumentBackendError> {
            self.translate_calls += 1;
            Err(LongDocumentBackendError::new(
                "recording backend should not translate this request",
            ))
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

    #[derive(Clone, Default)]
    struct PrefixNativeLongDocTranslator {
        calls: Arc<Mutex<Vec<String>>>,
    }

    impl PrefixNativeLongDocTranslator {
        fn calls(&self) -> Vec<String> {
            self.calls.lock().expect("calls lock").clone()
        }
    }

    impl NativeLongDocumentTranslator for PrefixNativeLongDocTranslator {
        fn translate_chunk(
            &mut self,
            request: QuickTranslateServiceRequest,
        ) -> Result<String, LongDocumentBackendError> {
            self.calls
                .lock()
                .expect("calls lock")
                .push(request.params.text.clone());
            Ok(format!("[zh] {}", request.params.text.trim()))
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

fn local_ai_long_document_request_uses_explicit_windows_ai(
    request: &QuickTranslateServiceRequest,
) -> bool {
    request.service.id == "windows-local-ai"
        && local_ai_provider_mode_for_long_document(&request.settings)
            == local_ai_provider_modes::WINDOWS_AI
        && matches!(
            request.execution_kind,
            QuickTranslateExecutionKind::Translate | QuickTranslateExecutionKind::TranslateStream
        )
}

fn local_ai_long_document_request_uses_auto_windows_ai(
    request: &QuickTranslateServiceRequest,
) -> bool {
    request.service.id == "windows-local-ai"
        && local_ai_provider_mode_for_long_document(&request.settings)
            == local_ai_provider_modes::AUTO
        && matches!(
            request.execution_kind,
            QuickTranslateExecutionKind::Translate | QuickTranslateExecutionKind::TranslateStream
        )
}

fn local_ai_provider_mode_for_long_document(settings: &SettingsSnapshot) -> &'static str {
    normalize_local_ai_provider_mode(settings.local_ai_provider.as_deref())
}

fn windows_ai_translation_request_from_quick_params(
    params: &TranslateParams,
) -> Result<WindowsAiTranslationRequest, LongDocumentBackendError> {
    Ok(WindowsAiTranslationRequest {
        text: params.text.clone(),
        from_language: windows_ai_language_from_quick_code(
            params.from.as_deref(),
            WindowsAiLanguage::Auto,
        )?,
        to_language: windows_ai_language_from_quick_code(
            params.to.as_deref(),
            WindowsAiLanguage::English,
        )?,
        custom_prompt: params.custom_prompt.clone(),
    })
}

fn windows_ai_language_from_quick_code(
    code: Option<&str>,
    default_language: WindowsAiLanguage,
) -> Result<WindowsAiLanguage, LongDocumentBackendError> {
    let Some(code) = code.map(str::trim).filter(|code| !code.is_empty()) else {
        return Ok(default_language);
    };

    WindowsAiLanguage::from_code(code).ok_or_else(|| {
        LongDocumentBackendError::new("No local AI provider supports this language pair")
    })
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

    let mut settings = request.settings.clone();
    settings.request_timeout_ms = request
        .params
        .request_timeout_ms
        .or(settings.request_timeout_ms);

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
        settings,
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

fn document_language_to_ocr_tag(language: &str) -> Option<&'static str> {
    let code = document_language_to_quick_code(language);
    if code.eq_ignore_ascii_case("auto") {
        return None;
    }

    Some(TranslationLanguage::from_code(code).to_bcp47())
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
    preserved_chunk_indexes: &BTreeSet<usize>,
) -> Result<NativeTextExport, LongDocumentBackendError> {
    let checkpoint = native_export_checkpoint(input_kind, source_chunks, translations);
    let monolingual = compose_native_monolingual_document(input_kind, &checkpoint);
    let bilingual = compose_native_bilingual_document(input_kind, &checkpoint);

    if matches!(input_kind, NativeTextInputKind::PdfText) {
        if let Some(export) = try_export_native_pdf_document(
            request,
            source_chunks,
            translations,
            &bilingual,
            preserved_chunk_indexes,
            &checkpoint,
        )? {
            return Ok(export);
        }
    }

    let pdf_checkpoint = matches!(input_kind, NativeTextInputKind::PdfText).then(|| {
        native_pdf_export_checkpoint(source_chunks, translations, preserved_chunk_indexes)
    });
    let output_path = resolve_native_output_path(&request.params, input_kind);

    match request.params.output_mode.as_str() {
        "Bilingual" => {
            let bilingual_path = build_bilingual_output_path(&output_path);
            ensure_native_output_path_can_be_written(&bilingual_path)?;
            fs::write(&bilingual_path, bilingual).map_err(native_write_error)?;
            Ok(NativeTextExport {
                output_path: bilingual_path.display().to_string(),
                bilingual_output_path: Some(bilingual_path.display().to_string()),
                checkpoint,
                pdf_checkpoint,
                backfill_metrics: None,
            })
        }
        "Both" => {
            let bilingual_path = build_bilingual_output_path(&output_path);
            ensure_native_output_paths_can_be_written([
                output_path.as_path(),
                bilingual_path.as_path(),
            ])?;
            fs::write(&output_path, monolingual).map_err(native_write_error)?;
            fs::write(&bilingual_path, bilingual).map_err(native_write_error)?;
            Ok(NativeTextExport {
                output_path: output_path.display().to_string(),
                bilingual_output_path: Some(bilingual_path.display().to_string()),
                checkpoint,
                pdf_checkpoint,
                backfill_metrics: None,
            })
        }
        _ => {
            ensure_native_output_path_can_be_written(&output_path)?;
            fs::write(&output_path, monolingual).map_err(native_write_error)?;
            Ok(NativeTextExport {
                output_path: output_path.display().to_string(),
                bilingual_output_path: None,
                checkpoint,
                pdf_checkpoint,
                backfill_metrics: None,
            })
        }
    }
}

fn try_export_native_pdf_document(
    request: &LongDocumentServiceRequest,
    source_chunks: &[NativeTextSourceChunk],
    translations: &[Option<String>],
    bilingual_text: &str,
    preserved_chunk_indexes: &BTreeSet<usize>,
    text_checkpoint: &LongDocumentExportCheckpoint,
) -> Result<Option<NativeTextExport>, LongDocumentBackendError> {
    if request.params.output_mode == "Bilingual" {
        return Ok(None);
    }

    let Some(input_path) = native_pdf_input_path(request) else {
        return Ok(None);
    };
    let output_path = resolve_native_pdf_output_path(&request.params);
    if !path_extension_is(&output_path, "pdf") {
        return Ok(None);
    }

    let bilingual_output_path = (request.params.output_mode == "Both")
        .then(|| native_pdf_bilingual_text_output_path(&output_path));
    if let Some(bilingual_path) = bilingual_output_path.as_ref() {
        ensure_native_output_paths_can_be_written([
            output_path.as_path(),
            bilingual_path.as_path(),
        ])?;
    } else {
        ensure_native_output_path_can_be_written(&output_path)?;
    }

    let checkpoint =
        native_pdf_export_checkpoint(source_chunks, translations, preserved_chunk_indexes);
    let selected_page_numbers = native_pdf_selected_page_numbers(request, source_chunks);

    if native_pdf_export_mode_is_overlay(&request.params) {
        let overlay_summary = match export_pdf_with_overlay_text_blocks(
            request,
            input_path,
            &output_path,
            &checkpoint,
            selected_page_numbers.as_deref(),
        ) {
            Ok(summary) => summary,
            Err(_) => return Ok(None),
        };

        return Ok(Some(native_pdf_export_result(
            request,
            &output_path,
            bilingual_text,
            text_checkpoint,
            checkpoint,
            Some(native_pdf_overlay_backfill_metrics(&overlay_summary)),
        )?));
    }

    if !native_pdf_chunks_support_content_stream_export(source_chunks) {
        return Ok(None);
    }

    let backfill_metrics = match export_pdf_with_content_stream_replacement(
        input_path,
        &output_path,
        &checkpoint,
        selected_page_numbers.as_deref(),
    ) {
        Ok(summary) => Some(native_pdf_content_stream_backfill_metrics(&summary)),
        Err(error)
            if matches!(
                error.kind,
                NativePdfContentStreamExportFailureKind::NeedsFontEmbedding
                    | NativePdfContentStreamExportFailureKind::NoReplacements
            ) =>
        {
            match export_pdf_with_overlay_text_blocks(
                request,
                input_path,
                &output_path,
                &checkpoint,
                selected_page_numbers.as_deref(),
            ) {
                Ok(summary) => Some(native_pdf_overlay_backfill_metrics(&summary)),
                Err(_) => return Ok(None),
            }
        }
        Err(_) => {
            return Ok(None);
        }
    };

    Ok(Some(native_pdf_export_result(
        request,
        &output_path,
        bilingual_text,
        text_checkpoint,
        checkpoint,
        backfill_metrics,
    )?))
}

fn native_pdf_export_result(
    request: &LongDocumentServiceRequest,
    output_path: &Path,
    bilingual_text: &str,
    text_checkpoint: &LongDocumentExportCheckpoint,
    pdf_checkpoint: PdfExportCheckpoint,
    backfill_metrics: Option<serde_json::Value>,
) -> Result<NativeTextExport, LongDocumentBackendError> {
    let bilingual_output_path = if request.params.output_mode == "Both" {
        let bilingual_path = native_pdf_bilingual_text_output_path(&output_path);
        ensure_native_output_path_can_be_written(&bilingual_path)?;
        fs::write(&bilingual_path, bilingual_text).map_err(native_write_error)?;
        Some(bilingual_path.display().to_string())
    } else {
        None
    };

    Ok(NativeTextExport {
        output_path: output_path.display().to_string(),
        bilingual_output_path,
        checkpoint: text_checkpoint.clone(),
        pdf_checkpoint: Some(pdf_checkpoint),
        backfill_metrics,
    })
}

fn native_pdf_content_stream_backfill_metrics(
    summary: &NativePdfContentStreamExportSummary,
) -> serde_json::Value {
    serde_json::json!({
        "candidateBlocks": summary.blocks_considered as u64,
        "renderedBlocks": summary.blocks_patched as u64,
        "missingBoundingBoxBlocks": 0u64,
        "shrinkFontBlocks": 0u64,
        "truncatedBlocks": 0u64,
        "objectReplaceBlocks": summary.blocks_patched as u64,
        "overlayModeBlocks": 0u64,
        "structuredFallbackBlocks": summary.blocks_preserved as u64,
        "pageMetrics": serde_json::Value::Null,
        "blockIssues": serde_json::Value::Null,
    })
}

fn native_pdf_overlay_backfill_metrics(summary: &PdfOverlaySummary) -> serde_json::Value {
    serde_json::json!({
        "candidateBlocks": summary.blocks_requested as u64,
        "renderedBlocks": summary.blocks_written as u64,
        "missingBoundingBoxBlocks": 0u64,
        "shrinkFontBlocks": 0u64,
        "truncatedBlocks": 0u64,
        "objectReplaceBlocks": 0u64,
        "overlayModeBlocks": summary.blocks_written as u64,
        "structuredFallbackBlocks": summary.blocks_requested.saturating_sub(summary.blocks_written) as u64,
        "pageMetrics": serde_json::Value::Null,
        "blockIssues": serde_json::Value::Null,
    })
}

fn native_pdf_export_mode_is_overlay(params: &TranslateDocumentParams) -> bool {
    params
        .pdf_export_mode
        .as_deref()
        .is_some_and(|mode| mode.trim().eq_ignore_ascii_case("Overlay"))
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
        if !font_path.is_file() {
            return Err(format!(
                "configured CJK PDF overlay font '{}' is not a readable file",
                font_path.display()
            ));
        }

        if !native_pdf_explicit_overlay_font_path_is_managed(request, &font_path) {
            return Err(format!(
                "configured CJK PDF overlay font '{}' is not a Rust-managed Noto Sans CJK font asset",
                font_path.display()
            ));
        }

        return Ok(font_path);
    }

    let target_language =
        TranslationLanguage::from_code(document_language_to_quick_code(&request.params.to));
    let cached_font_path = request
        .settings
        .cache_dir_path()
        .and_then(|cache_dir| {
            crate::font_download::cached_font_path_for_directory(&cache_dir, target_language)
        })
        .or_else(|| {
            request
                .settings
                .cache_dir_path()
                .is_none()
                .then(|| crate::font_download::cached_font_path(target_language))
                .flatten()
        });

    cached_font_path.ok_or_else(|| {
        format!(
            "no cached CJK PDF overlay font is available for target language '{}'",
            request.params.to
        )
    })
}

fn native_pdf_explicit_overlay_font_path_is_managed(
    request: &LongDocumentServiceRequest,
    font_path: &Path,
) -> bool {
    let Some(file_name) = font_path.file_name().and_then(|name| name.to_str()) else {
        return false;
    };
    if !crate::font_download::font_assets()
        .iter()
        .any(|asset| asset.file_name.eq_ignore_ascii_case(file_name))
    {
        return false;
    }

    let Some(parent) = font_path.parent() else {
        return false;
    };
    let Ok(parent) = fs::canonicalize(parent) else {
        return false;
    };

    let managed_font_dir = request
        .settings
        .cache_dir_path()
        .map(crate::font_download::font_cache_dir)
        .unwrap_or_else(crate::font_download::default_font_cache_dir);
    fs::canonicalize(managed_font_dir)
        .map(|managed_font_dir| managed_font_dir == parent)
        .unwrap_or(false)
        && crate::font_download::is_managed_cjk_font_file(font_path)
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

fn ensure_native_output_path_can_be_written(
    output_path: &Path,
) -> Result<(), LongDocumentBackendError> {
    ensure_native_output_parent(output_path)?;
    if output_path.is_dir() {
        return Err(LongDocumentBackendError::new(format!(
            "Long document output path '{}' is a directory",
            output_path.display()
        )));
    }

    Ok(())
}

fn ensure_native_output_paths_can_be_written<'a>(
    output_paths: impl IntoIterator<Item = &'a Path>,
) -> Result<(), LongDocumentBackendError> {
    for output_path in output_paths {
        ensure_native_output_path_can_be_written(output_path)?;
    }

    Ok(())
}

fn preflight_native_text_output_paths(
    request: &LongDocumentServiceRequest,
    input_kind: NativeTextInputKind,
) -> Result<(), LongDocumentBackendError> {
    let text_output_path = resolve_native_output_path(&request.params, input_kind);
    let mut output_paths = match request.params.output_mode.as_str() {
        "Bilingual" => vec![build_bilingual_output_path(&text_output_path)],
        "Both" => vec![
            text_output_path.clone(),
            build_bilingual_output_path(&text_output_path),
        ],
        _ => vec![text_output_path],
    };

    if matches!(input_kind, NativeTextInputKind::PdfText)
        && request.params.output_mode != "Bilingual"
    {
        let pdf_output_path = resolve_native_pdf_output_path(&request.params);
        if path_extension_is(&pdf_output_path, "pdf") {
            if request.params.output_mode == "Both" {
                output_paths.push(native_pdf_bilingual_text_output_path(&pdf_output_path));
            }
            output_paths.push(pdf_output_path);
        }
    }
    if let Some(result_json_path) = normalized_result_json_path(request) {
        output_paths.push(PathBuf::from(result_json_path));
    }

    ensure_native_output_paths_can_be_written(output_paths.iter().map(PathBuf::as_path))
}

fn normalized_result_json_path(request: &LongDocumentServiceRequest) -> Option<String> {
    request
        .params
        .result_json_path
        .as_deref()
        .map(str::trim)
        .filter(|path| !path.is_empty())
        .map(ToOwned::to_owned)
}

fn read_native_result_json_sidecar(
    result_json_path: &Path,
) -> Result<NativeLongDocumentResultSidecar, LongDocumentBackendError> {
    let json = fs::read_to_string(result_json_path).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not read long document result JSON '{}': {error}",
            result_json_path.display()
        ))
    })?;
    serde_json::from_str(&json).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not parse long document result JSON '{}': {error}",
            result_json_path.display()
        ))
    })
}

fn write_native_result_json_sidecar(
    result_json_path: Option<&str>,
    result: &TranslateDocumentResult,
    input_kind: NativeTextInputKind,
    params: &TranslateDocumentParams,
    checkpoint: &LongDocumentExportCheckpoint,
    pdf_checkpoint: Option<&PdfExportCheckpoint>,
) -> Result<(), LongDocumentBackendError> {
    let Some(path) = result_json_path else {
        return Ok(());
    };
    let sidecar = NativeLongDocumentResultSidecar {
        result: result.clone(),
        checkpoint: NativeLongDocumentSidecarCheckpoint {
            input_mode: native_text_input_kind_name(input_kind).to_string(),
            output_mode: params.output_mode.clone(),
            service_id: params.service_id.clone(),
            from: params.from.clone(),
            to: params.to.clone(),
            route_metadata_version: 1,
            input_path: non_empty(&params.input_path),
            output_path: params.output_path.as_deref().and_then(non_empty),
            pdf_export_mode: params.pdf_export_mode.as_deref().and_then(non_empty),
            layout_detection: params.layout_detection.as_deref().and_then(non_empty),
            page_range: params.page_range.as_deref().and_then(non_empty),
            text: checkpoint.clone(),
            pdf: pdf_checkpoint.cloned(),
        },
    };
    let json = serde_json::to_vec_pretty(&sidecar).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not serialize long document result JSON: {error}"
        ))
    })?;
    fs::write(path, json).map_err(native_write_error)
}

fn restore_native_retry_route_from_checkpoint(
    request: &mut LongDocumentServiceRequest,
    checkpoint: &NativeLongDocumentSidecarCheckpoint,
) {
    if checkpoint.route_metadata_version == 0 {
        return;
    }

    request.params.input_path = checkpoint.input_path.clone().unwrap_or_default();
    request.params.output_path = checkpoint.output_path.clone();
    request.params.pdf_export_mode = checkpoint.pdf_export_mode.clone();
    request.params.layout_detection = checkpoint.layout_detection.clone();
    request.params.page_range = checkpoint.page_range.clone();

    request.input = checkpoint
        .input_path
        .as_deref()
        .and_then(non_empty)
        .map(LongDocumentInput::File)
        .unwrap_or_else(|| LongDocumentInput::InlineText(String::new()));
}

fn native_text_source_chunks_from_retry_checkpoint(
    input_kind: NativeTextInputKind,
    checkpoint: &NativeLongDocumentSidecarCheckpoint,
) -> Result<Vec<NativeTextSourceChunk>, LongDocumentBackendError> {
    let metadata_by_index = checkpoint
        .text
        .chunk_metadata
        .iter()
        .map(|metadata| (metadata.chunk_index, metadata))
        .collect::<BTreeMap<_, _>>();
    let pdf_metadata_by_index = checkpoint.pdf.as_ref().map(|pdf| {
        pdf.chunk_metadata
            .iter()
            .map(|metadata| (metadata.chunk_index, metadata.clone()))
            .collect::<BTreeMap<_, _>>()
    });

    checkpoint
        .text
        .source_chunks
        .iter()
        .enumerate()
        .map(|(index, text)| {
            let page_number = metadata_by_index
                .get(&index)
                .map(|metadata| native_retry_page_number(metadata.page_number))
                .unwrap_or(1);

            if matches!(input_kind, NativeTextInputKind::PdfText) {
                if let Some(pdf_metadata) = pdf_metadata_by_index
                    .as_ref()
                    .and_then(|metadata| metadata.get(&index))
                {
                    let source_block_id = native_retry_source_block_id(
                        &pdf_metadata.source_block_id,
                        index,
                        "pdf-p1-text",
                    );
                    let source_kind = if source_block_id.contains("-ocr-") {
                        NativeTextSourceKind::PdfOcr
                    } else if pdf_metadata.bounding_box.is_some() {
                        NativeTextSourceKind::PdfSourceBlock
                    } else {
                        NativeTextSourceKind::PdfSelectableText
                    };
                    return Ok(NativeTextSourceChunk {
                        text: text.clone(),
                        fallback_text: pdf_metadata.fallback_text.clone(),
                        page_number: native_retry_page_number(pdf_metadata.page_number),
                        source_block_id,
                        source_kind,
                        pdf_context: None,
                        pdf_export_metadata: Some(pdf_metadata.clone()),
                    });
                }
            }

            Ok(NativeTextSourceChunk {
                text: text.clone(),
                fallback_text: None,
                page_number,
                source_block_id: if matches!(input_kind, NativeTextInputKind::PdfText) {
                    format!("pdf-p{}-text-b{}", page_number, index + 1)
                } else {
                    format!("native-text-{}", index + 1)
                },
                source_kind: if matches!(input_kind, NativeTextInputKind::PdfText) {
                    NativeTextSourceKind::PdfSelectableText
                } else {
                    NativeTextSourceKind::PlainText
                },
                pdf_context: None,
                pdf_export_metadata: None,
            })
        })
        .collect()
}

fn native_retry_page_number(page_number: i32) -> u32 {
    u32::try_from(page_number).unwrap_or(1).max(1)
}

fn native_retry_source_block_id(source_block_id: &str, index: usize, prefix: &str) -> String {
    let source_block_id = source_block_id.trim();
    if source_block_id.is_empty() {
        format!("{prefix}-b{}", index + 1)
    } else {
        source_block_id.to_string()
    }
}

fn validate_native_retry_checkpoint_chunk_coverage(
    translations: &[Option<String>],
    failed_indexes: &BTreeSet<usize>,
) -> Result<(), LongDocumentBackendError> {
    for (index, translated) in translations.iter().enumerate() {
        let has_translation = translated
            .as_deref()
            .map(|text| !text.trim().is_empty())
            .unwrap_or(false);
        if !has_translation && !failed_indexes.contains(&index) {
            return Err(LongDocumentBackendError::new(format!(
                "Native long document checkpoint chunk {index} has no translated text and is not marked failed"
            )));
        }
    }

    Ok(())
}

fn preserved_chunk_indexes_from_retry_checkpoint(
    checkpoint: &NativeLongDocumentSidecarCheckpoint,
) -> BTreeSet<usize> {
    checkpoint
        .pdf
        .as_ref()
        .map(|pdf| {
            pdf.chunk_metadata
                .iter()
                .filter_map(|metadata| {
                    (metadata.translation_skipped || metadata.preserve_original_text_in_pdf_export)
                        .then_some(metadata.chunk_index)
                })
                .collect()
        })
        .unwrap_or_default()
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

fn native_long_document_quality_report_json(
    checkpoint: &LongDocumentExportCheckpoint,
    backfill_metrics: Option<serde_json::Value>,
) -> Result<String, LongDocumentBackendError> {
    let report = native_long_document_quality_report(checkpoint, backfill_metrics);
    serde_json::to_string(&report).map_err(|error| {
        LongDocumentBackendError::new(format!(
            "Could not serialize native long document quality report: {error}"
        ))
    })
}

fn native_long_document_quality_report(
    checkpoint: &LongDocumentExportCheckpoint,
    backfill_metrics: Option<serde_json::Value>,
) -> NativeLongDocumentQualityReport {
    let metadata_by_index = checkpoint
        .chunk_metadata
        .iter()
        .map(|metadata| (metadata.chunk_index, metadata))
        .collect::<BTreeMap<_, _>>();
    let failed_blocks = checkpoint
        .failed_chunk_indexes
        .iter()
        .copied()
        .map(|index| {
            let metadata = metadata_by_index.get(&index);
            NativeLongDocumentFailedBlockInfo {
                ir_block_id: format!("checkpoint-{index}"),
                source_block_id: metadata
                    .map(|metadata| native_quality_source_block_id(metadata))
                    .unwrap_or_else(|| format!("chunk-{index}")),
                page_number: metadata.map(|metadata| metadata.page_number).unwrap_or(0),
                retry_count: 0,
                error: "Translation failed or missing translated text.".to_string(),
            }
        })
        .collect();

    NativeLongDocumentQualityReport {
        stage_timings_ms: BTreeMap::new(),
        backfill_metrics,
        total_blocks: checkpoint.source_chunks.len() as u32,
        translated_blocks: checkpoint.translated_chunks.len() as u32,
        skipped_blocks: checkpoint
            .chunk_metadata
            .iter()
            .filter(|metadata| metadata.source_block_type == LongDocumentExportBlockType::Formula)
            .count() as u32,
        failed_blocks,
    }
}

fn native_quality_source_block_id(metadata: &LongDocumentExportChunkMetadata) -> String {
    if metadata.page_number > 0 {
        format!(
            "native-p{}-b{}",
            metadata.page_number,
            metadata.order_in_page.max(0) + 1
        )
    } else {
        format!("chunk-{}", metadata.chunk_index)
    }
}

fn native_pdf_export_checkpoint(
    source_chunks: &[NativeTextSourceChunk],
    translations: &[Option<String>],
    preserved_chunk_indexes: &BTreeSet<usize>,
) -> PdfExportCheckpoint {
    PdfExportCheckpoint {
        source_chunks: source_chunks
            .iter()
            .map(|chunk| chunk.text.clone())
            .collect(),
        chunk_metadata: source_chunks
            .iter()
            .enumerate()
            .map(|(index, chunk)| {
                native_pdf_export_chunk_metadata(index, chunk, preserved_chunk_indexes)
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

fn native_pdf_export_chunk_metadata(
    index: usize,
    chunk: &NativeTextSourceChunk,
    preserved_chunk_indexes: &BTreeSet<usize>,
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
    if preserved_chunk_indexes.contains(&index) {
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

#[cfg(feature = "retained-dotnet-workers")]
fn long_document_events_from_ipc(events: Vec<IpcEvent<Value>>) -> Vec<LongDocumentEvent> {
    events
        .into_iter()
        .filter_map(long_document_event_from_ipc)
        .collect()
}

#[cfg(feature = "retained-dotnet-workers")]
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

fn is_zero_u32(value: &u32) -> bool {
    *value == 0
}

fn long_document_vision_layout_params(
    settings: &SettingsState,
) -> (Option<String>, Option<String>, Option<String>) {
    if native_pdf_layout_detection_mode_from_values(
        None,
        Some(settings.layout_detection_mode.as_str()),
    ) != NativePdfLayoutDetectionMode::VisionLlm
    {
        return (None, None, None);
    }

    match settings
        .vision_layout_service
        .trim()
        .to_ascii_lowercase()
        .as_str()
    {
        "openai" => (
            non_empty(&settings.open_ai_endpoint),
            non_empty(&settings.open_ai_api_key),
            non_empty(&settings.open_ai_model),
        ),
        "custom-openai" | "customopenai" => {
            let provider = settings
                .service_provider_settings
                .iter()
                .find(|provider| provider.service_id == "custom-openai");
            (
                provider.and_then(|provider| non_empty(&provider.endpoint)),
                provider.and_then(|provider| non_empty(&provider.api_key)),
                provider.and_then(|provider| non_empty(&provider.model)),
            )
        }
        _ => {
            let provider = settings
                .service_provider_settings
                .iter()
                .find(|provider| provider.service_id == "gemini");
            (
                Some(VISION_LAYOUT_GEMINI_OPENAI_ENDPOINT.to_string()),
                provider.and_then(|provider| non_empty(&provider.api_key)),
                provider
                    .and_then(|provider| non_empty(&provider.model))
                    .or_else(|| Some(VISION_LAYOUT_DEFAULT_GEMINI_MODEL.to_string())),
            )
        }
    }
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
    settings.request_timeout_ms = Some(if uses_foundry_profile {
        FOUNDRY_LOCAL_LONG_DOCUMENT_REQUEST_TIMEOUT_MS
    } else {
        DEFAULT_LONG_DOCUMENT_REQUEST_TIMEOUT_MS
    });
    settings
}

fn long_document_request_timeout_ms(service_id: &str, settings: &SettingsState) -> u32 {
    let snapshot = crate::state::settings_snapshot(settings);
    if uses_foundry_local_long_document_profile(service_id, &snapshot) {
        FOUNDRY_LOCAL_LONG_DOCUMENT_REQUEST_TIMEOUT_MS
    } else {
        DEFAULT_LONG_DOCUMENT_REQUEST_TIMEOUT_MS
    }
}

fn uses_foundry_local_long_document_profile(service_id: &str, settings: &SettingsSnapshot) -> bool {
    if !map_long_document_service_id(service_id).eq_ignore_ascii_case("windows-local-ai") {
        return false;
    }

    matches!(
        normalize_local_ai_provider_mode(settings.local_ai_provider.as_deref()),
        local_ai_provider_modes::AUTO | local_ai_provider_modes::FOUNDRY_LOCAL
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
