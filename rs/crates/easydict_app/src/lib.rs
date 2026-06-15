#![forbid(unsafe_code)]

pub mod activation;
pub mod app_data;
pub mod browser_registrar;
pub mod character_paragraph;
pub mod cli_translate;
pub mod clipboard;
#[cfg(feature = "retained-dotnet-workers")]
pub mod compat_client;
#[cfg(feature = "retained-dotnet-workers")]
pub mod compat_protocol;
pub mod content_preservation;
pub mod credential_protection;
pub mod custom_streaming;
pub mod desktop_integration;
pub mod desktop_shell;
pub mod doc_layout_yolo;
pub mod doc_layout_yolo_onnx;
pub mod document_layout;
pub mod file_dialog;
pub mod font_download;
pub mod font_metrics;
pub mod formula_protection;
pub mod formula_text_reconstruction;
pub mod grammar_correction;
mod i18n;
pub mod latex_formula;
pub mod layout_model_download;
pub mod lex_index;
pub mod llm_streaming;
pub mod local_dictionary;
pub mod local_dictionary_index;
pub mod long_document;
pub mod long_document_cli;
pub mod long_document_context;
pub mod long_document_export;
pub mod mdx_native;
pub mod mouse_selection;
pub mod named_event;
pub mod native_bridge;
pub mod ocr;
pub mod openai_compatible;
pub mod openvino_download;
pub mod pdf_content_stream;
pub mod pdf_export_blocks;
pub mod pdf_formula_adapter;
pub mod pdf_native_export;
pub mod pdf_source_extraction;
pub mod protocol;
mod protocol_core;
pub mod quick_translate;
pub mod resource_download;
mod runtime_policy;
pub mod screen_capture;
pub mod screen_capture_native;
pub mod settings_migration;
pub mod settings_status;
pub mod settings_storage;
pub mod state;
pub mod table_structure;
pub mod table_structure_onnx;
pub mod text_insertion;
pub mod text_layout;
pub mod text_selection;
pub mod theme;
pub mod traditional_http;
pub mod translation_cache;
pub mod translation_language;
pub mod translation_services;
pub mod tts;
pub mod ui;
pub mod vision_layout;
pub mod window_options;

pub use character_paragraph::{
    build_char_paragraphs, build_char_paragraphs_with_classifier, build_character_level_protection,
    get_bracket_delta, get_formula_confidence, is_formula_character, reconstruct_latex_from_chars,
    strip_subset_prefix, CharInfo, CharParagraph, CharParagraphResult, CharTextInfo,
    CharacterLevelProtection, FormulaConfidence, FormulaVariableGroup, TextMatrix,
};
pub use content_preservation::{
    analyze_formula_preservation, is_character_based_formula, is_font_based_formula,
    is_numeric_data_block, is_subscript_dense_formula, normalize_for_exact_span_comparison,
    protect_formula_block, resolve_formula_fallback, restore_formula_block, BlockContext,
    BlockFormulaCharacters, FormulaCharacterInfo, PreservationMode, ProtectedBlock, ProtectionPlan,
    RestoreOutcome, RestoreStatus, SoftValidationStatus, SourceBlockType, EQUATION_SOFT_CLOSE_TAG,
    EQUATION_SOFT_OPEN_TAG,
};
pub use custom_streaming::{
    build_custom_streaming_grammar_request_plan, build_custom_streaming_translation_request_plan,
    build_doubao_translation_request_plan, build_gemini_grammar_request_plan,
    build_gemini_translation_request_plan, cleanup_custom_streaming_translation_text,
    cleanup_doubao_translation_text, correct_custom_streaming_grammar,
    custom_streaming_config_for_service, custom_streaming_error_from_response,
    doubao_language_code, doubao_service_config, execute_custom_streaming_request,
    execute_custom_streaming_request_observing_chunks, gemini_service_config,
    parse_custom_streaming_chunks, parse_doubao_stream_chunks, parse_gemini_stream_chunks,
    translate_custom_streaming_service, CustomStreamingFormat, CustomStreamingHttpClient,
    CustomStreamingHttpRequestPlan, CustomStreamingServiceConfig,
    CustomStreamingSseLineChunkParser, DoubaoConfig, GeminiConfig,
    ReqwestCustomStreamingHttpClient, DOUBAO_DEFAULT_ENDPOINT, DOUBAO_DEFAULT_MODEL,
    GEMINI_API_BASE_URL, GEMINI_DEFAULT_MODEL,
};
pub use doc_layout_yolo::{
    apply_doc_layout_yolo_nms, compute_doc_layout_yolo_iou, doc_layout_yolo_class_to_region,
    parse_doc_layout_yolo_output, preprocess_doc_layout_yolo_bgra, DocLayoutRegionType,
    DocLayoutYoloDetection, DocLayoutYoloPreprocessResult, DOC_LAYOUT_YOLO_CLASS_NAMES,
    DOC_LAYOUT_YOLO_DEFAULT_CONFIDENCE_THRESHOLD, DOC_LAYOUT_YOLO_INPUT_SIZE,
    DOC_LAYOUT_YOLO_NMS_IOU_THRESHOLD, DOC_LAYOUT_YOLO_NUM_CLASSES, DOC_LAYOUT_YOLO_PADDING_VALUE,
};
pub use doc_layout_yolo_onnx::{
    normalize_doc_layout_yolo_output_shape, parse_doc_layout_yolo_onnx_output,
    resolve_doc_layout_yolo_input_name, resolve_doc_layout_yolo_output_name,
    DocLayoutYoloOnnxError, DocLayoutYoloOnnxSession,
};
pub use document_layout::{
    build_final_erase_rects_top_left, expand_line_rects_for_cell, expand_line_widths,
    handle_inline_script_lines_for_overlay, is_citation_like_inline_script,
    looks_like_grid_line_positions, looks_like_inline_script_line, needs_math_font,
    normalize_translation_for_inline_script_lines, parse_formula_fragments,
    rects_belong_to_same_erase_band, resolve_available_height, segment_line_by_font,
    should_apply_formula_hole, split_line_rects_for_inline_script_protection,
    try_apply_inline_subscript_attachments, try_build_line_rects, try_convert_to_unicode_subscript,
    BlockLinePosition, BlockTextStyle, FontSegment, FormulaFragment, FormulaFragmentKind,
    InlineScriptLineSplit, InlineScriptOverlayResult, InlineSubscriptAttachment, PdfRect,
};
pub use font_download::{
    cached_font_path, cached_font_path_for_directory, default_font_cache_dir,
    delete_all_fonts_for_directory, ensure_font, ensure_font_for_directory,
    ensure_font_with_settings, font_asset_for_language, font_assets, font_cache_dir,
    has_any_cjk_font_for_directory, is_font_downloaded_for_directory, requires_cjk_font,
    total_font_size_bytes_for_directory, FontAsset, FontDownloadError, FONTS_SUBDIR,
};
pub use font_metrics::{
    glyph_advance_em, is_script_signal, load_font_metrics, parse_cmap_from_bytes,
    parse_font_metrics_from_bytes, FontMetrics, FontMetricsError, GlyphAdvanceMeasurer,
    CJK_PRIMARY_ASCII_ADVANCE_EM, DEFAULT_NON_CJK_ADVANCE_EM, DEFAULT_UNITS_PER_EM,
    SPACE_ADVANCE_EM,
};
pub use formula_protection::{
    classify_formula_token, detect_formula_matches, extend_formula_trailing_parens,
    formula_requires_exact_soft_preservation,
    formula_text_contains_exact_soft_preservation_candidate, formula_token_type_is_high_confidence,
    protect_formula_spans, protect_formula_spans_two_tier, restore_formula_spans,
    restore_formula_spans_with_diagnostics, FormulaMatch, FormulaProtectionResult,
    FormulaRestoreResult, FormulaRestoreStatus, FormulaToken, FormulaTokenType, SoftProtectedSpan,
    SoftProtectionWrapperKind,
};
pub use formula_text_reconstruction::{
    is_reconstruction_quality_acceptable, looks_like_formula_continuation_text,
    previous_line_likely_expects_formula_tail, reconstruct_formula_aware_text,
    should_use_letter_based_block_text, LetterGeometry,
};
pub use latex_formula::{
    is_script_signal as is_latex_script_signal, prepare_renderable_text_for_pdf,
    simplify as simplify_latex_formula, simplify_math_content, simplify_math_content_with_options,
    simplify_with_options as simplify_latex_with_options,
};
pub use layout_model_download::{
    cleanup_invalid_layout_model_files_for_directory, default_model_cache_dir,
    delete_all_layout_model_files_for_directory, ensure_full_layout_model_available,
    ensure_full_layout_model_available_for_directory, ensure_layout_model_available,
    ensure_layout_model_available_for_directory, ensure_tatr_model_available,
    ensure_tatr_model_available_for_directory, is_full_layout_model_ready_for_directory,
    is_layout_model_ready_for_directory, layout_model_status_for_directory, model_cache_dir,
    LayoutModelDownloadConfig, LayoutModelDownloadError, LayoutModelPaths, LayoutModelStatus,
    DOC_LAYOUT_MODEL_FILE_NAME, DOC_LAYOUT_MODEL_URLS, MIN_DOC_LAYOUT_MODEL_FILE_SIZE,
    MIN_RUNTIME_FILE_SIZE, MIN_TATR_MODEL_FILE_SIZE, MODELS_SUBDIR, ONNX_RUNTIME_FILE_NAME,
    ONNX_RUNTIME_TEMP_ZIP_FILE_NAME, ONNX_RUNTIME_URLS, ONNX_RUNTIME_ZIP_ENTRY_PATH,
    TATR_MODEL_FILE_NAME, TATR_MODEL_URLS,
};
pub use llm_streaming::{
    chat_completions_sse_chunks, extract_chat_completions_delta, extract_responses_delta,
    parse_chat_completions_sse_chunks, parse_openai_sse_chunks, parse_responses_sse_chunks,
    responses_sse_chunks, ChatCompletionsSseChunks, ChatMessage, ChatRole,
    OpenAiSseLineChunkParser, OpenAiStreamingFormat, ResponsesSseChunks,
};
pub use local_dictionary::{
    apply_active_local_dictionary_suggestion, apply_local_dictionary_suggestion,
    apply_local_dictionary_suggestion_update, begin_local_dictionary_suggestions,
    dismiss_local_dictionary_suggestions, exit_local_dictionary_suggestions,
    focus_local_dictionary_suggestions, local_dictionary_query_token,
    local_dictionary_suggestion_request_can_route_natively, move_local_dictionary_suggestion,
    run_delayed_local_dictionary_suggestion_request_with_current_app_dir,
    run_local_dictionary_suggestion_request, run_local_dictionary_suggestion_request_with_app_dir,
    run_local_dictionary_suggestion_request_with_current_app_dir,
    run_local_dictionary_suggestion_request_with_native_index,
    run_local_dictionary_suggestion_request_with_native_index_root,
    run_local_dictionary_suggestion_request_with_native_route, LocalDictionarySuggestionBackend,
    LocalDictionarySuggestionError, LocalDictionarySuggestionRequest,
    LocalDictionarySuggestionUpdate, NativeMdxLocalDictionarySuggestionBackend,
    LOCAL_DICTIONARY_SUGGESTION_DELAY_MS,
};
#[cfg(feature = "retained-dotnet-workers")]
pub use local_dictionary::{
    run_local_dictionary_suggestion_request_with_lazy_bridge,
    run_local_dictionary_suggestion_request_with_routed_backends,
};
pub use local_dictionary_index::{
    default_local_dictionary_index_root, escape_data_string,
    local_dictionary_index_root_for_settings, LocalDictionaryIndexDescriptor,
    LocalDictionaryIndexError, LocalDictionaryIndexManifest, LocalDictionaryIndexService,
    LocalDictionaryIndexSuggestionItem, CURRENT_INDEX_FORMAT_VERSION, DEFAULT_NORMALIZATION_ID,
    INDEX_FILE_NAME, MANIFEST_FILE_NAME,
};
#[cfg(feature = "retained-dotnet-workers")]
pub use long_document::run_long_document_request_with_packaged_app_dir_and_worker_policy;
pub use long_document::{
    apply_long_document_outcome, apply_long_document_start_error, begin_long_document_retry_failed,
    begin_long_document_translate, build_long_document_request,
    long_document_request_can_route_natively, long_document_service_kind_is_supported,
    long_document_supported_service_descriptors,
    retry_failed_native_text_long_document_from_result_json,
    retry_failed_native_text_long_document_from_result_json_with_translator,
    run_long_document_request, run_long_document_request_with_app_dir,
    run_long_document_request_with_app_dir_and_native_local_ai_client,
    run_long_document_request_with_native_route, run_native_text_long_document_request,
    run_native_text_long_document_request_with_translator,
    run_native_text_long_document_request_with_translator_and_cancellation, LongDocumentBackend,
    LongDocumentBackendError, LongDocumentEvent, LongDocumentInput, LongDocumentOutcome,
    LongDocumentServiceRequest, LongDocumentStartError, NativeLongDocumentTranslator,
    QuickTranslateNativeLongDocumentTranslator, WindowsAiNativeLongDocumentTranslator,
};
pub use long_document_context::{
    apply_preservation_hints, merge_glossaries, merge_page_partials, merge_preservation_hints,
    remove_control_characters, trim_leading_spaces_per_line, try_parse_page_partial,
    DocumentBlockIr, DocumentContext, DocumentIr, PagePartial, MAX_PRESERVED_BLOCK_LENGTH,
};
pub use long_document_export::{
    build_bilingual_output_path, compose_bilingual_markdown, compose_bilingual_text,
    compose_monolingual_markdown, compose_monolingual_text, LongDocumentExportBlockType,
    LongDocumentExportCheckpoint, LongDocumentExportChunkMetadata,
};
pub use mdx_native::{
    detect_mdx_file_encryption_mode, discover_mdd_file_paths, inline_mdd_resources_in_html,
    inline_mdd_resources_in_html_with_factory, mdx_decode_base64_regcode, mdx_decrypt_block,
    mdx_decrypt_regcode_by_device_id, mdx_decrypt_regcode_by_email, mdx_fast_decrypt,
    mdx_ripemd128, mdx_salsa20_8, mime_type_for_mdd_resource_key,
    native_mdx_dictionary_can_route_natively, native_mdx_dictionary_needs_credentials,
    native_mdx_lookup_can_route, native_mdx_lookup_local_input_error,
    native_mdx_lookup_needs_credentials, native_mdx_service_can_route_natively,
    normalize_mdd_resource_key, run_native_mdd_resource_lookup,
    run_native_mdd_resource_lookup_with_factory, run_native_mdx_lookup,
    run_native_mdx_lookup_with_factories, run_native_mdx_lookup_with_factories_and_mdd_policy,
    run_native_mdx_lookup_with_factory, run_native_mdx_lookup_with_factory_and_mdd_policy,
    MdxEncryptionMode, NativeMddResource, NativeMddResourceError, NativeMddResourceReader,
    NativeMddResourceReaderFactory, NativeMdxDictionaryReader, NativeMdxDictionaryReaderFactory,
    NativeMdxLookupError, RsMdictMddReader, RsMdictMddReaderFactory, RsMdictReader,
    RsMdictReaderFactory,
};
pub use mouse_selection::{
    keyboard_message_dismiss_reason, DragDetector, DragSelectionResult,
    MouseSelectionCaptureRequest, MouseSelectionDismissReason, MouseSelectionHookOutcome,
    MouseSelectionHookState, MouseSelectionPoint, MouseSelectionProducer,
    MouseSelectionProducerAction, MouseSelectionProducerContext, MouseSelectionTextReady,
    MouseSelectionTrigger, MouseSelectionTriggerKind, MultiClickDetector, MultiClickResult,
    PendingMultiClickSelection, EASYDICT_SYNTHETIC_KEY, MAX_CLICK_DISTANCE, MIN_DRAG_DISTANCE,
    MULTI_CLICK_DELAY_GRACE_MS, WM_KEYDOWN, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE,
    WM_MOUSEWHEEL, WM_RBUTTONDOWN, WM_SYSKEYDOWN,
};
pub use ocr::{
    apply_ocr_outcome, apply_ocr_start_error, begin_ocr_recognize, bgra_to_base64_bmp,
    bgra_to_base64_jpeg_data_url, build_custom_api_ocr_request, build_ollama_ocr_request,
    group_and_sort_ocr_lines, merge_ocr_lines, merge_ocr_words, merged_ocr_text,
    parse_ocr_http_response, run_ocr_recognize, run_ocr_recognize_with_app_dir,
    run_ocr_recognize_with_current_app_dir, run_ocr_recognize_with_native_provider,
    windows_native_ocr_availability, windows_native_ocr_availability_with_recognizer,
    NativeOcrBackend, OcrAvailabilityDto, OcrBackend, OcrBackendError, OcrCaptureResult,
    OcrEngineConfig, OcrEngineKind, OcrHttpClient, OcrHttpRequestPlan, OcrHttpResponseParser,
    OcrImageEncodeError, OcrLanguageDto, OcrLineDto, OcrMode, OcrOutcome, OcrRecognizeParams,
    OcrRecognizeRequest, OcrRectDto, OcrResultAction, OcrResultDto, OcrStartError,
    WindowsNativeOcrRecognizer,
};
pub use openai_compatible::{
    build_built_in_ai_device_registration_request_plan, build_foundry_local_models_request_plan,
    build_openai_grammar_messages, build_openai_grammar_request_plan,
    build_openai_http_request_plan, build_openai_request_body, build_openai_translation_messages,
    build_openai_translation_request_plan, built_in_ai_device_registration_endpoint,
    built_in_ai_direct_endpoint_for_model, built_in_ai_direct_service_config,
    built_in_ai_embedded_device_registration_request_plan,
    built_in_ai_embedded_proxy_service_config, built_in_ai_embedded_secret,
    built_in_ai_proxy_headers, built_in_ai_proxy_model_or_default,
    built_in_ai_proxy_service_config, check_foundry_local_runtime_status, clamp_openai_temperature,
    cleanup_openai_translation_text, correct_grammar_openai_compatible,
    custom_openai_service_config, decrypt_built_in_ai_secret, deepseek_service_config,
    default_foundry_local_runtime_controller, detect_openai_api_format_from_url,
    execute_openai_stream_request, execute_openai_stream_request_observing_chunks,
    extract_foundry_local_chat_completions_endpoint,
    extract_foundry_local_chat_completions_endpoint_from_logs,
    foundry_local_models_endpoint_from_chat_completions_endpoint, foundry_local_service_config,
    github_models_service_config, groq_service_config,
    normalize_foundry_local_chat_completions_endpoint, ollama_model_refresh_fallback,
    ollama_service_config, ollama_tags_url_from_endpoint, openai_api_format_from_setting,
    openai_compatible_config_for_service, openai_compatible_service_can_route_natively,
    openai_effective_temperature, openai_error_from_response, openai_responses_reasoning_effort,
    openai_service_config, parse_built_in_ai_device_registration_response,
    parse_foundry_local_runtime_status, parse_ollama_model_names, prepare_foundry_local_service,
    register_built_in_ai_device, resolve_foundry_local_model_id_for_config,
    resolve_ollama_model_refresh, resolve_openai_api_format,
    resolve_openai_compatible_config_for_service, translate_openai_compatible,
    try_resolve_foundry_local_model_id, validate_openai_config, zhipu_service_config,
    BuiltInAiDeviceRegistrationHttpClient, BuiltInAiDeviceRegistrationHttpResponse,
    BuiltInAiDeviceRegistrationRequestPlan, BuiltInAiSecretError,
    CommandFoundryLocalEndpointResolver, DefaultFoundryLocalRuntimeController,
    FoundryLocalEndpointResolver, FoundryLocalError, FoundryLocalErrorCode, FoundryLocalModelState,
    FoundryLocalPrepareOutcome, FoundryLocalRuntimeController, FoundryLocalRuntimeState,
    FoundryLocalRuntimeStatus, FoundryLocalStatusCheck, OllamaModelRefreshOutcome, OpenAiApiFormat,
    OpenAiCompatibleConfig, OpenAiExecutionError, OpenAiExecutionErrorCode, OpenAiHttpClient,
    OpenAiHttpGetRequestPlan, OpenAiHttpRequestPlan, OpenAiHttpTextResponse, OpenAiPlanError,
    OpenAiTranslationRequest, ReqwestOpenAiHttpClient, BUILT_IN_AI_ALLOWED_PROXY_MODELS,
    BUILT_IN_AI_DEFAULT_MODEL, CUSTOM_OPENAI_DEFAULT_MODEL, DEEPSEEK_DEFAULT_ENDPOINT,
    DEEPSEEK_DEFAULT_MODEL, FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE, FOUNDRY_LOCAL_DEFAULT_MODEL,
    GITHUB_MODELS_DEFAULT_ENDPOINT, GITHUB_MODELS_DEFAULT_MODEL, GROQ_DEFAULT_ENDPOINT,
    GROQ_DEFAULT_MODEL, OLLAMA_DEFAULT_ENDPOINT, OLLAMA_DEFAULT_MODEL, OPENAI_DEFAULT_ENDPOINT,
    OPENAI_DEFAULT_MODEL, OPENAI_DEFAULT_TEMPERATURE, OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT,
    OPENAI_TRANSLATION_SYSTEM_PROMPT, ZHIPU_DEFAULT_ENDPOINT, ZHIPU_DEFAULT_MODEL,
};
pub use openvino_download::{
    default_nllb_model_approximate_bytes, default_openvino_data_directory,
    ensure_openvino_assets_available, ensure_openvino_assets_available_for_directory,
    ensure_openvino_model_available_for_directory, ensure_openvino_runtime_available_for_directory,
    ensure_openvino_runtime_directory_on_path, is_openvino_runtime_supported_current_architecture,
    openvino_download_status_for_directory, openvino_ep_path_injection_enabled,
    openvino_runtime_path_with_directory, OpenVinoDownloadConfig, OpenVinoDownloadError,
    OpenVinoDownloadStatus, OpenVinoModelDownloadFile,
};
pub use pdf_content_stream::{
    build_content_stream, cid_to_hex, escape_pdf_literal_string, extract_pdf_literal_strings,
    find_text_operator_range, find_text_operator_range_bytes, generate_text_operator,
    hide_text_operator_in_stream, normalize_pdf_text_for_match, parse_pdf_literal_string,
    replace_text_operator_in_stream, replace_text_operator_in_stream_bytes,
    try_patch_pdf_array_text_token, try_patch_pdf_literal_token, PdfLiteralString,
    TextOperatorRange,
};
pub use pdf_export_blocks::{
    build_pdf_overlay_blocks, build_translated_block_lookup, checkpoint_to_overlay_blocks,
    should_erase_block_background, should_render_block_text, try_get_renderable_text,
    PdfExportBlockTextStyle, PdfExportCheckpoint, PdfExportChunkMetadata, PdfExportSourceBlockType,
    PdfOverlayBlock, PdfOverlayRect, PdfRenderableText, PdfTranslatedBlock,
};
pub use pdf_native_export::{
    export_pdf_with_content_stream_replacement, NativePdfContentStreamExportError,
    NativePdfContentStreamExportFailureKind, NativePdfContentStreamExportSummary,
};
#[cfg(feature = "retained-dotnet-workers")]
pub use quick_translate::LocalAiWorkerQuickTranslateBackend;
pub use quick_translate::{
    apply_quick_translate_outcome, apply_quick_translate_service_update,
    apply_quick_translate_start_error, apply_quick_translate_start_error_for_surface,
    apply_quick_translate_stream_chunk, auto_foundry_local_native_probe_request,
    auto_openvino_native_fallback_request, auto_windows_ai_native_probe_status,
    begin_manual_quick_translate_service, begin_manual_quick_translate_service_for_surface,
    begin_quick_translate, begin_quick_translate_for_surface,
    begin_retry_quick_translate_service_for_surface, build_quick_translate_plan,
    build_quick_translate_plan_for_surface, enrich_quick_translate_update_with_youdao_phonetics,
    local_ai_quick_translate_local_error, local_ai_quick_translate_native_preflight_error,
    local_ai_route_decision, quick_translate_request_can_route_natively,
    quick_translate_service_update_from_cache, resolve_auto_target_language,
    resolve_different_target_language, resolve_quick_query_language, run_quick_translate,
    run_quick_translate_service, run_quick_translate_service_with_app_dir,
    run_quick_translate_service_with_app_dir_and_native_local_ai_client,
    run_quick_translate_service_with_app_dir_and_native_local_ai_probes,
    run_quick_translate_service_with_current_app_dir,
    run_quick_translate_service_with_native_route,
    run_quick_translate_streaming_service_with_app_dir_and_foundry_resolver,
    run_quick_translate_streaming_service_with_app_dir_and_native_local_ai_client,
    run_quick_translate_streaming_service_with_app_dir_and_native_local_ai_client_observing_chunks,
    run_quick_translate_streaming_service_with_app_dir_observing_chunks,
    run_quick_translate_streaming_service_with_current_app_dir_observing_chunks,
    run_quick_translate_streaming_service_with_native_route_observing_chunks,
    run_quick_translate_with_app_dir, store_quick_translate_cache_result,
    translation_cache_request_for_quick_translate, LocalAiRouteDecision,
    NativeBingQuickTranslateBackend, NativeCustomStreamingQuickTranslateBackend,
    NativeMdxQuickTranslateBackend, NativeOpenAiQuickTranslateBackend,
    NativeOpenVinoQuickTranslateBackend, NativeTraditionalHttpQuickTranslateBackend,
    QuickQueryLanguageResolution, QuickQueryMode, QuickTranslateBackend,
    QuickTranslateBackendError, QuickTranslateExecutionKind, QuickTranslateOutcome,
    QuickTranslatePlan, QuickTranslateService, QuickTranslateServiceOutcome,
    QuickTranslateServiceRequest, QuickTranslateServiceUpdate, QuickTranslateStartError,
    QuickTranslateStreamChunk, QuickTranslateStreamResult, QuickTranslateSurface,
};
#[cfg(feature = "retained-dotnet-workers")]
pub use quick_translate::{
    run_quick_translate_service_with_packaged_app_dir_and_worker_policy,
    run_quick_translate_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver,
    run_quick_translate_streaming_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver,
};
pub use resource_download::{
    download_with_retry, download_with_retry_and_policy, is_file_valid, ordered_urls_by_probe,
    try_delete_file, ReqwestResourceDownloadClient, ResourceDownloadClient, ResourceDownloadError,
    ResourceDownloadProgress, ResourceDownloadRetryPolicy, ResourceProbeResult,
};
#[cfg(feature = "retained-dotnet-workers")]
pub use runtime_policy::{
    RetainedWorkerPolicy, DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE,
    DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE, GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
    LOCAL_AI_WORKER_DISABLED_MESSAGE, LONGDOC_WORKER_DISABLED_MESSAGE,
    RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
};
pub use screen_capture::{
    detected_windows_from_screen_windows, CaptureInteraction, CaptureInteractionState,
    CapturePhase, CapturePoint, CaptureRect, DetectedWindow, ScreenWindowRect,
    ScreenWindowSnapshot, WindowDetector,
};
pub use settings_migration::{
    migrate_settings_file, migrate_settings_json, migrate_settings_object, resolve_source_path,
    SettingsMigrateParams, SettingsMigrateResult, SettingsMigrationError,
};
pub use settings_storage::{
    default_settings_storage_path, load_settings_file, load_settings_json,
    load_settings_json_with_machine_id, save_settings_file, save_settings_json, SettingsLoadResult,
    SettingsStorageError,
};
pub use state::{
    preview_control_state_from_id, resolve_result_action_intent, settings_snapshot, AppMode,
    BrowserSupportState, ConnectionStatus, EasydictUiState, FloatingWindowState,
    GrammarCorrectionPreview, HotkeySetting, ImportedMdxDictionary, LocalDictionarySuggestion,
    LongDocumentState, Message, PopButtonAnchor, PopButtonState, PreviewScenario,
    ResultActionIntent, ResultActionKind, ServiceProviderField, ServiceProviderSetting,
    SettingsLink, SettingsSection, SettingsState, TranslationResultPreview,
    TRANSLATION_LANGUAGE_IDS,
};
pub use table_structure::{
    build_table_cell_grid, build_table_structure_from_detections, calculate_tatr_crop_resize,
    compute_rect_iou, compute_table_iou, deduplicate_table_detections_by_iou,
    parse_tatr_detr_output, preprocess_table_crop, table_element_class_from_index,
    tatr_detections_to_page_space, TableCellBounds, TableCropResize, TableElementClass,
    TablePreprocessResult, TableStructure, TableSubDetection, TATR_DEFAULT_CONFIDENCE_THRESHOLD,
    TATR_DUPLICATE_IOU_THRESHOLD, TATR_IMAGE_MEAN, TATR_IMAGE_STD, TATR_LONGEST_EDGE,
    TATR_MAX_CELLS_PER_TABLE, TATR_MIN_CELL_SIDE_PX, TATR_NO_OBJECT_CLASS_INDEX, TATR_NUM_CLASSES,
    TATR_NUM_QUERIES, TATR_SHORTEST_EDGE,
};
pub use table_structure_onnx::{
    clamp_tatr_table_crop, normalize_tatr_logits_shape, normalize_tatr_pred_boxes_shape,
    parse_tatr_onnx_outputs, resolve_tatr_input_name, resolve_tatr_output_names, TatrOnnxError,
    TatrOnnxSession, TatrOutputNames, TatrTableCrop,
};
pub use text_insertion::{
    capture_text_insertion_target, capture_text_insertion_target_with_backend,
    captured_text_insertion_target, insert_text_into_captured_target,
    insert_text_into_captured_target_with_backend, store_captured_text_insertion_target,
    NativeTextInsertionBackend, TextInsertionBackend, TextInsertionError, TextInsertionOutcome,
    TextInsertionTarget,
};
pub use text_layout::{
    classify_char, enumerate_graphemes, is_cjk, is_close_punctuation, is_left_sticky,
    is_open_punctuation, is_prohibited_line_end, is_prohibited_line_start, layout_next_line,
    layout_paragraph, layout_paragraph_with_lines, layout_paragraph_with_lines_and_widths,
    layout_paragraph_with_widths, normalize_whitespace, prepare_paragraph,
    prepare_paragraph_with_options, segment_text, segment_text_with_options, solve_font_fit,
    walk_line_ranges, CharCategory, FontFitRequest, FontFitResult, KinsokuTable, LayoutCursor,
    LayoutLine, LayoutLineRange, LayoutLinesResult, LayoutResult, PreparedParagraph, SegmentKind,
    SegmentedText, TextMeasurer, TextPrepareOptions,
};
pub use theme::easydict_theme_tokens;
pub use traditional_http::{
    apply_deepl_dynamic_spacing, bing_credentials_expired, bing_host, bing_language_code,
    build_bing_translate_request_plan, build_caiyun_translation_request_plan,
    build_deepl_api_translation_request_plan, build_deepl_web_translation_request_plan,
    build_deepl_web_translation_request_plan_with_values, build_google_translation_request_plan,
    build_google_web_translation_request_plan, build_linguee_translation_request_plan,
    build_niutrans_translation_request_plan, build_traditional_http_translation_request_plan,
    build_volcano_translation_request_plan, build_youdao_openapi_translation_request_plan,
    build_youdao_openapi_translation_request_plan_with_nonce,
    build_youdao_web_dict_translation_request_plan, build_youdao_web_translate_key_request_plan,
    build_youdao_web_translate_key_request_plan_with_time, build_youdao_web_translate_request_plan,
    build_youdao_web_translate_request_plan_with_time, caiyun_language_code,
    compute_volcano_authorization, compute_youdao_openapi_sign, compute_youdao_web_dict_sign,
    compute_youdao_web_translate_sign, decrypt_youdao_web_translate_response,
    deepl_aligned_timestamp, deepl_api_error_from_status, deepl_i_count, deepl_language_code,
    deepl_web_error_from_status, from_bing_language_code, google_language_code,
    linguee_language_code, niutrans_error_from_code, niutrans_language_code,
    parse_bing_credentials_from_html, parse_bing_translation_response,
    parse_caiyun_translation_response, parse_deepl_api_translation_response,
    parse_deepl_web_translation_response, parse_google_translation_response,
    parse_google_web_translation_response, parse_linguee_translation_response,
    parse_niutrans_translation_response, parse_volcano_translation_response,
    parse_youdao_openapi_response, parse_youdao_web_dict_response,
    parse_youdao_web_translate_key_response, parse_youdao_web_translate_response,
    traditional_http_config_for_request, traditional_http_config_for_service,
    traditional_http_error_from_status, traditional_http_supports_language_pair_for_kind,
    translate_bing_service, translate_deepl_web_service, translate_traditional_http_service,
    translate_youdao_web_dict_service, translate_youdao_web_translate_service,
    volcano_language_code, volcano_timestamps_from_epoch_seconds, youdao_language_code,
    youdao_openapi_error_from_code, youdao_openapi_signature_input, youdao_web_dict_language_code,
    youdao_web_dict_time, youdao_web_translate_error_from_code, BingCredentials, BingHttpClient,
    BingHttpResponse, BingTranslatorPage, ReqwestBingHttpClient, ReqwestTraditionalHttpClient,
    TraditionalHttpClient, TraditionalHttpRequestPlan, TraditionalHttpServiceConfig,
    TraditionalHttpServiceKind, VolcanoTimestamps, BING_CHINA_HOST, BING_GLOBAL_HOST,
    BING_MAX_TEXT_LENGTH_UTF16, BING_USER_AGENT, CAIYUN_TRANSLATE_ENDPOINT,
    DEEPL_FREE_API_ENDPOINT, DEEPL_PRO_API_ENDPOINT, DEEPL_WEB_ENDPOINT, DEEPL_WEB_USER_AGENT,
    GOOGLE_TRANSLATE_ENDPOINT, LINGUEE_TRANSLATE_ENDPOINT, NIUTRANS_MAX_TEXT_LENGTH_UTF16,
    NIUTRANS_TRANSLATE_ENDPOINT, VOLCANO_MAX_TEXT_LENGTH_UTF16, VOLCANO_QUERY_STRING,
    VOLCANO_TRANSLATE_ENDPOINT, VOLCANO_TRANSLATE_HOST, YOUDAO_DICT_VOICE_ENDPOINT,
    YOUDAO_OPENAPI_ENDPOINT, YOUDAO_WEB_AES_IV_SOURCE, YOUDAO_WEB_AES_KEY_SOURCE,
    YOUDAO_WEB_DICT_ENDPOINT, YOUDAO_WEB_INITIAL_SIGN_KEY, YOUDAO_WEB_TRANSLATE_ENDPOINT,
    YOUDAO_WEB_TRANSLATE_KEY_ENDPOINT, YOUDAO_WEB_USER_AGENT,
};
pub use translation_cache::{
    displayable_phonetics, format_phonetic_text, is_youdao_word_query, long_document_source_hash,
    long_document_translation_cache_path, merge_phonetics_into_result,
    phonetic_accent_display_label, phonetic_cache_entry_size_kb, phonetic_cache_key,
    plan_phonetic_enrichment, target_phonetics, translation_cache_entry_size_kb,
    translation_cache_key, Definition, LongDocumentTranslationCache,
    PersistentTranslationCacheError, Phonetic, PhoneticEnrichmentDecision,
    PhoneticEnrichmentSkipReason, PhoneticFlightRegistration, PhoneticFlightTracker,
    PhoneticMemoryCache, Synonym, TranslationCacheRequest, TranslationMemoryCache,
    TranslationResult, TranslationResultKind, WordForm, WordResult, PHONETIC_CACHE_LIMIT_KB,
    TRANSLATION_CACHE_LIMIT_KB,
};
pub use translation_language::{TranslationLanguage, ALL_TRANSLATION_LANGUAGES};
pub use translation_services::{
    app_visible_translation_service_ids, default_translation_service_descriptors,
    find_translation_service_descriptor, imported_mdx_service_descriptor,
    openai_compatible_service_ids, translation_service_capabilities, TranslationServiceDescriptor,
    TranslationServiceKind, DEFAULT_FLOATING_WINDOW_SERVICE_IDS, DEFAULT_MAIN_WINDOW_SERVICE_IDS,
    DEFAULT_SERVICE_ID,
};
pub use ui::{
    capture_overlay_view, capture_overlay_view_with_state, fixed_window_view,
    fixed_window_view_with_settings, main_window_view, mini_window_view,
    mini_window_view_with_settings, pop_button_view, pop_button_view_with_state, settings_view,
    settings_view_for_main_window,
};
pub use vision_layout::{
    build_vision_layout_request_plan, build_vision_layout_request_plan_from_bgra,
    execute_vision_layout_detection, parse_vision_layout_detection_array,
    parse_vision_layout_response, vision_layout_region_type_from_str,
    ReqwestVisionLayoutHttpClient, VisionLayoutDetection, VisionLayoutHttpClient,
    VisionLayoutHttpRequestPlan, VisionLayoutHttpResponse, VisionLayoutRegionType,
    VISION_LAYOUT_DETECTION_PROMPT,
};
pub use window_options::{
    capture_overlay_window_options, fixed_window_options, main_window_options,
    main_window_options_for_settings, mini_window_options, pop_button_window_options,
    settings_window_options, MAIN_WINDOW_DEFAULT_HEIGHT_DIPS, MAIN_WINDOW_DEFAULT_WIDTH_DIPS,
    MAIN_WINDOW_MIN_HEIGHT_DIPS, MAIN_WINDOW_MIN_WIDTH_DIPS, SETTINGS_WINDOW_DEFAULT_HEIGHT_DIPS,
    SETTINGS_WINDOW_DEFAULT_WIDTH_DIPS,
};

pub fn clear_persistent_translation_cache_for_settings(settings: &protocol::SettingsSnapshot) {
    if let Ok(mut cache) = LongDocumentTranslationCache::open(long_document_translation_cache_path(
        settings.cache_dir_str(),
    )) {
        let _ = cache.clear();
    }
}

use win_fluent::prelude::*;

pub use activation::{
    parse_startup_activation, resolve_startup_activation_disposition,
    startup_activation_message_for_args, StartupActivation, StartupActivationDisposition,
};
pub use credential_protection::{
    get_or_create_persisted_machine_id, get_or_create_persisted_machine_id_with_legacy_fallback,
    is_protected_credential, protect_credential, protect_credential_legacy,
    protect_credential_with_scope, try_unprotect_credential, try_unprotect_credential_legacy,
    try_unprotect_credential_with_machine_id, unprotect_or_return_plaintext,
    unprotect_or_return_plaintext_with_machine_id, CredentialPlaintext, CredentialProtectionError,
    CredentialProtectionScope, MAX_NESTED_PROTECTED_VALUE_DEPTH,
};
pub use grammar_correction::{
    build_grammar_correction_plain_text_prompt, build_grammar_correction_user_prompt,
    grammar_correction_system_prompt, parse_grammar_correction, GrammarCorrectionResult,
    GRAMMAR_CORRECTION_SYSTEM_PROMPT, GRAMMAR_CORRECTION_SYSTEM_PROMPT_WITH_EXPLANATION,
};

pub struct EasydictApp {
    pub state: EasydictUiState,
}

const APP_WINDOW_SUBSCRIPTION_IDS: [&str; 6] = [
    "main",
    "settings",
    "mini",
    "fixed",
    "capture-overlay",
    "pop-button",
];

impl Application for EasydictApp {
    type Message = Message;
    type Flags = EasydictUiState;

    fn new(mut flags: Self::Flags) -> (Self, Task<Self::Message>) {
        flags.browser_support = match load_browser_support_status() {
            Ok(status) => BrowserSupportState::from_status(&status),
            Err(error) => BrowserSupportState::failed(error),
        };
        let built_in_ai_registration_task = built_in_ai_device_registration_task(&flags.settings);
        let clipboard_monitor_task = clipboard_monitor_task_for_settings(&flags.settings);
        let mouse_selection_hook_task = mouse_selection_hook_task_for_settings(&flags.settings);
        let named_event_task = named_event_listener_task();
        let protocol_registration_task = protocol_registration_task();
        (
            Self { state: flags },
            Task::batch([
                startup_activation_task_for_args(std::env::args().skip(1)),
                built_in_ai_registration_task,
                clipboard_monitor_task,
                mouse_selection_hook_task,
                named_event_task,
                protocol_registration_task,
            ]),
        )
    }

    fn title(&self, window: &WindowId) -> String {
        match window.as_str() {
            "main" if self.state.settings_open => "Easydict Settings".to_string(),
            "main" => "Easydict".to_string(),
            "settings" => "Easydict Settings".to_string(),
            "mini" => "Easydict Mini".to_string(),
            "fixed" => "Easydict Fixed".to_string(),
            "capture-overlay" => "Easydict Capture".to_string(),
            "pop-button" => "Easydict Selection".to_string(),
            _ => "Easydict".to_string(),
        }
    }

    fn view(&self, window: &WindowId) -> View<Self::Message> {
        match window.as_str() {
            "main" if self.state.settings_open => {
                settings_view_for_main_window(&self.state.settings)
            }
            "settings" => settings_view(&self.state.settings),
            "mini" => mini_window_view_with_settings(&self.state.mini, &self.state.settings),
            "fixed" => fixed_window_view_with_settings(&self.state.fixed, &self.state.settings),
            "capture-overlay" => capture_overlay_view_with_state(
                &self.state.capture_interaction,
                self.state.capture_selection,
                self.state.capture_background.as_ref(),
            ),
            "pop-button" => pop_button_view(),
            _ => main_window_view(&self.state),
        }
    }

    fn window_options(&self, window: &WindowId) -> Option<WindowOptions> {
        match window.as_str() {
            "main" => Some(main_window_options_for_settings(&self.state.settings)),
            "settings" => Some(settings_window_options()),
            "mini" => Some(mini_window_options()),
            "fixed" => Some(fixed_window_options()),
            "capture-overlay" => Some(capture_overlay_window_options()),
            "pop-button" => Some(pop_button_window_options()),
            _ => None,
        }
    }

    fn update(&mut self, message: Self::Message) -> Task<Self::Message> {
        if let Message::HotkeyTriggered(id) = &message {
            return self.hotkey_task(id);
        }

        if let Message::TrayCommand(id) = &message {
            return self.tray_task(id);
        }

        if let Message::TrayClipboardTextReceived(text) = message {
            return self.translate_tray_clipboard_text(text);
        }

        if let Message::ClipboardTextReceived(text) = message {
            return self.translate_clipboard_text(text);
        }

        if let Message::MouseSelectionInputHookEvent(event) = message {
            return self.mouse_selection_input_hook_event_task(event);
        }

        if let Message::MouseSelectionPendingMultiClickElapsed(generation) = message {
            return self.mouse_selection_pending_multi_click_elapsed_task(generation);
        }

        if let Message::SourceTextChanged(text) = message {
            self.state.apply(Message::SourceTextChanged(text));
            return match local_dictionary::begin_local_dictionary_suggestions(&mut self.state) {
                Some(request) => local_dictionary_suggestion_task(request),
                None => Task::none(),
            };
        }

        if message == Message::SourceTextSubmitted {
            if local_dictionary::apply_active_local_dictionary_suggestion(&mut self.state) {
                return Task::none();
            }

            return match quick_translate::begin_quick_translate(&mut self.state) {
                Ok(plan) => self.quick_translate_service_batch_task(plan.service_requests()),
                Err(error) => {
                    quick_translate::apply_quick_translate_start_error(&mut self.state, error);
                    Task::none()
                }
            };
        }

        if let Message::LocalDictionarySuggestionsFinished(update) = message {
            local_dictionary::apply_local_dictionary_suggestion_update(&mut self.state, update);
            return Task::none();
        }

        if let Message::ApplyLocalDictionarySuggestion(suggestion) = message {
            local_dictionary::apply_local_dictionary_suggestion(&mut self.state, &suggestion);
            return Task::none();
        }

        if let Message::LongDocumentFinished(outcome) = message {
            long_document::apply_long_document_outcome(&mut self.state, outcome);
            return Task::none();
        }

        if let Message::OcrCaptureFinished(capture) = message {
            return self.start_ocr_recognize(ocr::OcrMode::Translate, capture);
        }

        if let Message::SilentOcrCaptureFinished(capture) = message {
            return self.start_ocr_recognize(ocr::OcrMode::SilentClipboard, capture);
        }

        if let Message::OcrCaptureCancelled(mode) = message {
            ocr::reset_pending_ocr(&mut self.state);
            self.state.capture_interaction = CaptureInteractionState::new();
            self.state.capture_selection = None;
            self.state.ocr_status_text = format!("{} capture cancelled", mode.label());
            return Task::window(WindowCommand::Hide(WindowId::new("capture-overlay")));
        }

        if let Message::OcrRecognizeFinished(outcome) = message {
            return self.finish_ocr_recognize(outcome);
        }

        if let Message::BuiltInAiDeviceRegistrationFinished(result) = &message {
            let should_persist = result
                .as_ref()
                .ok()
                .and_then(|token| token.as_deref())
                .is_some_and(|token| !token.trim().is_empty());
            self.state.apply(message);
            return if should_persist {
                settings_save_task(self.state.saved_settings.clone())
            } else {
                Task::none()
            };
        }

        if let Message::FoundryLocalPrepareFinished(result) = message {
            let should_persist_endpoint =
                self.state.settings.foundry_local_endpoint.trim().is_empty()
                    && result
                        .as_ref()
                        .ok()
                        .and_then(|outcome| outcome.endpoint.as_deref())
                        .is_some_and(|endpoint| !endpoint.trim().is_empty());
            let should_persist_model = self.state.settings.foundry_local_model.trim().is_empty()
                && result
                    .as_ref()
                    .ok()
                    .is_some_and(|outcome| !outcome.model.trim().is_empty());

            self.state
                .apply(Message::FoundryLocalPrepareFinished(result));

            let mut should_persist = false;
            if should_persist_endpoint
                && self.state.saved_settings.foundry_local_endpoint
                    != self.state.settings.foundry_local_endpoint
            {
                self.state.saved_settings.foundry_local_endpoint =
                    self.state.settings.foundry_local_endpoint.clone();
                should_persist = true;
            }

            if should_persist_model
                && self.state.saved_settings.foundry_local_model
                    != self.state.settings.foundry_local_model
            {
                self.state.saved_settings.foundry_local_model =
                    self.state.settings.foundry_local_model.clone();
                should_persist = true;
            }

            return if should_persist {
                settings_save_task(self.state.saved_settings.clone())
            } else {
                Task::none()
            };
        }

        if let Message::WindowsAiPrepareFinished(result) = message {
            self.state.apply(Message::WindowsAiPrepareFinished(result));
            return Task::none();
        }

        let should_start_foundry_local_prepare = message == Message::StartFoundryLocal
            || (message == Message::PrepareLocalAiModel
                && self.state.settings.local_ai_provider
                    == crate::protocol::local_ai_provider_modes::FOUNDRY_LOCAL);
        if should_start_foundry_local_prepare {
            self.state.apply(message);
            return foundry_local_prepare_task(crate::state::settings_snapshot(
                &self.state.settings,
            ));
        }

        let should_start_windows_ai_prepare = message == Message::PrepareLocalAiModel
            && matches!(
                self.state.settings.local_ai_provider.as_str(),
                crate::protocol::local_ai_provider_modes::AUTO
                    | crate::protocol::local_ai_provider_modes::WINDOWS_AI
            );
        if should_start_windows_ai_prepare {
            self.state.apply(message);
            return windows_ai_prepare_task();
        }

        if let Message::SelectionTextReady {
            text,
            anchor_x,
            anchor_y,
            generation,
        } = message
        {
            return self.pop_button_selection_text_ready_task(text, anchor_x, anchor_y, generation);
        }

        if let Message::PopButtonAutoDismiss(generation) = message {
            return self.pop_button_auto_dismiss_task(generation);
        }

        if message == Message::DismissPopButton {
            return self.dismiss_pop_button_task();
        }

        if message == Message::PopButtonClicked {
            return self.pop_button_clicked_task();
        }

        if let Some(task) = self.capture_overlay_interaction_task(&message) {
            return task;
        }

        if message == Message::BrowseFile
            && self.state.mode == AppMode::LongDocument
            && !self.state.settings_open
            && !self.state.long_document.is_translating
        {
            return open_file_dialog_task(
                long_document_file_dialog_options(&self.state),
                Message::LongDocumentFileSelected,
            );
        }

        if message == Message::BrowseOutputFolder
            && self.state.mode == AppMode::LongDocument
            && !self.state.settings_open
            && !self.state.long_document.is_translating
        {
            return open_folder_dialog_task(
                long_document_output_folder_dialog_options(&self.state),
                Message::LongDocumentOutputFolderSelected,
            );
        }

        if message == Message::ImportMdxDictionary {
            return open_file_dialog_task(
                mdx_dictionary_file_dialog_options(),
                Message::MdxDictionarySelected,
            );
        }

        if message == Message::Translate
            && self.state.mode == AppMode::LongDocument
            && !self.state.settings_open
        {
            return match long_document::begin_long_document_translate(&mut self.state) {
                Ok(request) => long_document_task(request),
                Err(error) => {
                    long_document::apply_long_document_start_error(&mut self.state, error);
                    Task::none()
                }
            };
        }

        if message == Message::RetryLongDocument
            && self.state.mode == AppMode::LongDocument
            && !self.state.settings_open
        {
            return match long_document::begin_long_document_retry_failed(&mut self.state) {
                Ok((request, result_json_path)) => {
                    long_document_retry_failed_task(request, result_json_path)
                }
                Err(error) => {
                    long_document::apply_long_document_start_error(&mut self.state, error);
                    Task::none()
                }
            };
        }

        if message == Message::QuickTranslate {
            return match quick_translate::begin_quick_translate(&mut self.state) {
                Ok(plan) => self.quick_translate_service_batch_task(plan.service_requests()),
                Err(error) => {
                    quick_translate::apply_quick_translate_start_error(&mut self.state, error);
                    Task::none()
                }
            };
        }

        if let Message::QuickTranslateIn(surface) = &message {
            return match quick_translate::begin_quick_translate_for_surface(
                &mut self.state,
                *surface,
            ) {
                Ok(plan) => self.quick_translate_service_batch_task(plan.service_requests()),
                Err(error) => {
                    quick_translate::apply_quick_translate_start_error_for_surface(
                        &mut self.state,
                        *surface,
                        error,
                    );
                    Task::none()
                }
            };
        }

        if let Message::ToggleResultExpandedIn(surface, service_id) = &message {
            match quick_translate::begin_manual_quick_translate_service_for_surface(
                &mut self.state,
                *surface,
                service_id,
            ) {
                Ok(Some(request)) => return self.quick_translate_service_task(request),
                Err(error) => {
                    quick_translate::apply_quick_translate_start_error_for_surface(
                        &mut self.state,
                        *surface,
                        error,
                    );
                    return Task::none();
                }
                Ok(None) => {}
            }
        }

        if let Message::RetryResultIn(surface, service_id) = &message {
            match quick_translate::begin_retry_quick_translate_service_for_surface(
                &mut self.state,
                *surface,
                service_id,
            ) {
                Ok(Some(request)) => return self.quick_translate_retry_service_task(request),
                Err(error) => {
                    quick_translate::apply_quick_translate_start_error_for_surface(
                        &mut self.state,
                        *surface,
                        error,
                    );
                    return Task::none();
                }
                Ok(None) => {}
            }
        }

        if let Some(task) = result_action_task_for_message(&mut self.state, &message) {
            return task;
        }

        if message == Message::TranslateSelection {
            self.state.apply(message);
            return Task::batch([
                capture_text_insertion_target_task(),
                selected_text_capture_task(),
            ]);
        }

        if let Message::ToggleResultExpanded(service_id) = &message {
            match quick_translate::begin_manual_quick_translate_service(&mut self.state, service_id)
            {
                Ok(Some(request)) => return self.quick_translate_service_task(request),
                Err(error) => {
                    quick_translate::apply_quick_translate_start_error(&mut self.state, error);
                    return Task::none();
                }
                Ok(None) => {}
            }
        }

        if let Message::QuickTranslateServiceFinished(update) = &message {
            self.store_quick_translate_cache_result(update);
            let auto_play_task = auto_play_translation_task(&self.state, update);
            self.state.apply(message);
            return auto_play_task;
        }

        if message == Message::ClearTranslationCache {
            self.clear_persistent_translation_cache();
        }

        let task = match &message {
            // Opening settings kicks off a real async check of the on-disk
            // layout-model / CJK-font availability; the entry loading overlay is
            // shown until it resolves.
            Message::OpenSettings => {
                settings_runtime_status_task(crate::state::settings_snapshot(&self.state.settings))
            }
            // Switching settings tabs resets the shared scroll view to the top,
            // matching WinUI `MainScrollViewer.ChangeView(null, 0, null)`.
            Message::SettingsSectionChanged(_) => Task::scroll_to_top("MainScrollViewer"),
            Message::WindowEvent(WindowEvent::CloseRequested(id)) if id.as_str() == "main" => {
                self.main_window_close_task()
            }
            Message::MinimizeWindow => Task::window(WindowCommand::MinimizeCurrent(true)),
            Message::ToggleMaximizeWindow => Task::window(WindowCommand::ToggleMaximizeCurrent),
            Message::CloseMainWindow => self.main_window_close_task(),
            Message::CloseWindow => Task::window(WindowCommand::CloseCurrent),
            Message::ToggleShellContextMenu(true) => {
                register_shell_verb_task(default_desktop_shell_verb())
            }
            Message::ToggleShellContextMenu(false) => {
                unregister_shell_verb_task(default_desktop_shell_verb())
            }
            Message::InstallBrowserSupport => browser_registrar_action_task("install", None),
            Message::UninstallBrowserSupport => browser_registrar_action_task("uninstall", None),
            Message::OpenSettingsLink(link) => desktop_shell::open_url_task(link.url()),
            Message::DownloadOpenVinoModel => {
                openvino_download_task(crate::state::settings_snapshot(&self.state.settings))
            }
            Message::ConfirmCapture => self.capture_overlay_action_task(false),
            Message::CopyResult => self.capture_overlay_action_task(true),
            Message::CancelCapture => {
                ocr::reset_pending_ocr(&mut self.state);
                self.state.capture_interaction = CaptureInteractionState::new();
                self.state.capture_selection = None;
                Task::window(WindowCommand::Hide(WindowId::new("capture-overlay")))
            }
            _ => Task::none(),
        };

        let should_sync_background_hooks = matches!(
            message,
            Message::SaveSettingsChanges | Message::DiscardSettingsChanges
        );
        let should_sync_startup_registration = message == Message::SaveSettingsChanges;

        if !matches!(
            message,
            Message::ConfirmCapture | Message::CopyResult | Message::CancelCapture
        ) {
            self.state.apply(message);
        }

        if should_sync_background_hooks {
            let startup_registration = if should_sync_startup_registration {
                startup_registration_task(self.state.settings.launch_at_startup)
            } else {
                Task::none()
            };
            Task::batch([
                task,
                clipboard_monitor_task_for_settings(&self.state.settings),
                mouse_selection_hook_task_for_settings(&self.state.settings),
                startup_registration,
            ])
        } else {
            task
        }
    }

    fn theme(&self) -> ThemeMode {
        self.state.settings.theme
    }

    fn theme_tokens(&self) -> ThemeTokens {
        easydict_theme_tokens(self.state.settings.theme)
    }

    fn subscription(&self) -> Subscription<Self::Message> {
        Subscription::batch(
            hotkeys_for_settings(&self.state.settings)
                .into_iter()
                .map(|hotkey| Subscription::hotkey(hotkey, Message::HotkeyTriggered))
                .chain(std::iter::once(Subscription::tray(Message::TrayCommand)))
                .chain(
                    APP_WINDOW_SUBSCRIPTION_IDS
                        .into_iter()
                        .map(|id| Subscription::window(WindowId::new(id), Message::WindowEvent)),
                ),
        )
    }

    fn tray_menu(&self) -> Option<TrayMenu<Self::Message>> {
        Some(tray_menu_for_browser_support_locale(
            &self.state.browser_support,
            &self.state.settings.ui_language,
        ))
    }

    fn named_events(&self) -> Vec<NamedEventRegistration<Self::Message>> {
        Vec::new()
    }

    fn shell_verbs(&self) -> Vec<ShellVerb> {
        if self.state.settings.shell_context_menu {
            default_desktop_shell_verbs()
                .iter()
                .map(platform_shell_verb_from_desktop)
                .collect()
        } else {
            Vec::new()
        }
    }

    fn protocol_registrations(&self) -> Vec<ProtocolRegistration> {
        default_desktop_protocol_registrations()
            .iter()
            .map(platform_protocol_registration_from_desktop)
            .collect()
    }
}

impl EasydictApp {
    fn mouse_selection_input_hook_event_task(
        &mut self,
        event: easydict_windows_text_selection::LowLevelInputHookEvent,
    ) -> Task<Message> {
        if !self.state.settings.mouse_selection_translate {
            return Task::none();
        }

        let context = self.mouse_selection_producer_context(&event);
        let actions = self
            .state
            .mouse_selection_producer
            .process_low_level_input_event(event, context);
        mouse_selection_producer_actions_task(actions)
    }

    fn mouse_selection_pending_multi_click_elapsed_task(
        &mut self,
        generation: u64,
    ) -> Task<Message> {
        self.state
            .mouse_selection_producer
            .complete_pending_multi_click(generation)
            .map(mouse_selection_producer_action_task)
            .unwrap_or_else(Task::none)
    }

    fn mouse_selection_producer_context(
        &self,
        event: &easydict_windows_text_selection::LowLevelInputHookEvent,
    ) -> MouseSelectionProducerContext {
        let context = MouseSelectionProducerContext::new(
            easydict_windows_text_selection::system_double_click_time_ms(),
        )
        .current_app_excluded(mouse_selection::foreground_process_matches_excluded_apps(
            &self.state.settings.mouse_selection_excluded_apps,
        ));

        let easydict_windows_text_selection::LowLevelInputHookEvent::Mouse(event) = event else {
            return context;
        };

        let Some(anchor) = self.state.pop_button.anchor else {
            return context;
        };
        if !self.state.pop_button.visible {
            return context;
        }

        let anchor = MouseSelectionPoint::new(anchor.x, anchor.y);
        let point = MouseSelectionPoint::new(event.x, event.y);
        context.left_click_is_pop_button(mouse_selection::point_hits_pop_button(anchor, point))
    }

    fn pop_button_selection_text_ready_task(
        &mut self,
        text: String,
        anchor_x: i32,
        anchor_y: i32,
        generation: u64,
    ) -> Task<Message> {
        if generation < self.state.pop_button.generation {
            return Task::none();
        }

        self.state.pop_button.generation = generation;
        let text = text.trim().to_string();
        if text.is_empty() {
            self.state.pop_button.clear();
            return Task::window(WindowCommand::Hide(WindowId::new("pop-button")));
        }

        let anchor = PopButtonAnchor::new(anchor_x, anchor_y);
        let (window_x, window_y) = anchor.window_position_dips();
        self.state.pop_button.pending_text = Some(text);
        self.state.pop_button.anchor = Some(anchor);
        self.state.pop_button.visible = true;

        Task::batch([
            Task::window(WindowCommand::ShowAt {
                id: WindowId::new("pop-button"),
                x: window_x,
                y: window_y,
            }),
            pop_button_auto_dismiss_task(generation),
        ])
    }

    fn pop_button_auto_dismiss_task(&mut self, generation: u64) -> Task<Message> {
        if generation != self.state.pop_button.generation || !self.state.pop_button.visible {
            return Task::none();
        }

        self.state.pop_button.clear();
        Task::window(WindowCommand::Hide(WindowId::new("pop-button")))
    }

    fn dismiss_pop_button_task(&mut self) -> Task<Message> {
        self.state.pop_button.clear();
        Task::window(WindowCommand::Hide(WindowId::new("pop-button")))
    }

    fn pop_button_clicked_task(&mut self) -> Task<Message> {
        let Some(text) = self.state.pop_button.pending_text.take() else {
            self.state.pop_button.clear();
            return Task::window(WindowCommand::Hide(WindowId::new("pop-button")));
        };

        self.state.pop_button.clear();
        self.state.mini.text = text;
        let translate_task = match quick_translate::begin_quick_translate_for_surface(
            &mut self.state,
            QuickTranslateSurface::Mini,
        ) {
            Ok(plan) => self.quick_translate_service_batch_task(plan.service_requests()),
            Err(error) => {
                quick_translate::apply_quick_translate_start_error_for_surface(
                    &mut self.state,
                    QuickTranslateSurface::Mini,
                    error,
                );
                Task::none()
            }
        };

        Task::batch([
            Task::window(WindowCommand::Hide(WindowId::new("pop-button"))),
            capture_text_insertion_target_task(),
            Task::window(WindowCommand::Show(WindowId::new("mini"))),
            translate_task,
        ])
    }

    fn hotkey_task(&mut self, id: &str) -> Task<Message> {
        match id {
            HOTKEY_SHOW_MAIN => show_and_focus_main_window_task(),
            HOTKEY_TRANSLATE_CLIPBOARD => Task::batch([
                capture_text_insertion_target_task(),
                selected_text_capture_task(),
            ]),
            HOTKEY_OCR_TRANSLATE => {
                self.state.pending_ocr_mode = Some(ocr::OcrMode::Translate);
                self.state.capture_interaction = CaptureInteractionState::new();
                self.state.capture_selection = None;
                // Freeze the desktop before the overlay opens, like the WinUI
                // ScreenCaptureWindow's BitBlt-on-open.
                self.state.capture_background = crate::state::capture_screen_background();
                self.state.ocr_status_text = "Select a region for OCR Translate".to_string();
                Task::batch([
                    capture_screen_window_snapshot_task(),
                    Task::window(WindowCommand::Show(WindowId::new("capture-overlay"))),
                ])
            }
            HOTKEY_SILENT_OCR => {
                self.state.pending_ocr_mode = Some(ocr::OcrMode::SilentClipboard);
                self.state.capture_interaction = CaptureInteractionState::new();
                self.state.capture_selection = None;
                self.state.capture_background = crate::state::capture_screen_background();
                self.state.ocr_status_text = "Select a region for Silent OCR".to_string();
                Task::batch([
                    capture_screen_window_snapshot_task(),
                    Task::window(WindowCommand::Show(WindowId::new("capture-overlay"))),
                ])
            }
            HOTKEY_SHOW_MINI => Task::batch([
                capture_text_insertion_target_task(),
                Task::window(WindowCommand::Show(WindowId::new("mini"))),
            ]),
            HOTKEY_TOGGLE_MINI => {
                Task::window(WindowCommand::ToggleVisibility(WindowId::new("mini")))
            }
            HOTKEY_SHOW_FIXED => Task::window(WindowCommand::Show(WindowId::new("fixed"))),
            HOTKEY_TOGGLE_FIXED => {
                Task::window(WindowCommand::ToggleVisibility(WindowId::new("fixed")))
            }
            _ => Task::none(),
        }
    }

    fn start_ocr_recognize(
        &mut self,
        mode: ocr::OcrMode,
        capture: ocr::OcrCaptureResult,
    ) -> Task<Message> {
        match ocr::begin_ocr_recognize(&mut self.state, mode, capture) {
            Ok(request) => Task::batch([
                Task::window(WindowCommand::Hide(WindowId::new("capture-overlay"))),
                ocr_recognize_task(request),
            ]),
            Err(error) => {
                ocr::apply_ocr_start_error(&mut self.state, error);
                Task::none()
            }
        }
    }

    fn finish_ocr_recognize(&mut self, outcome: ocr::OcrOutcome) -> Task<Message> {
        let Some(action) = ocr::apply_ocr_outcome(&mut self.state, outcome) else {
            return Task::window(WindowCommand::Hide(WindowId::new("capture-overlay")));
        };

        match action {
            ocr::OcrResultAction::TranslateInMini => {
                let translate_task = match quick_translate::begin_quick_translate_for_surface(
                    &mut self.state,
                    ocr::ocr_surface(),
                ) {
                    Ok(plan) => self.quick_translate_service_batch_task(plan.service_requests()),
                    Err(error) => {
                        quick_translate::apply_quick_translate_start_error_for_surface(
                            &mut self.state,
                            ocr::ocr_surface(),
                            error,
                        );
                        Task::none()
                    }
                };

                Task::batch([
                    Task::window(WindowCommand::Hide(WindowId::new("capture-overlay"))),
                    Task::window(WindowCommand::Show(WindowId::new("mini"))),
                    translate_task,
                ])
            }
            ocr::OcrResultAction::CopyText(text) => Task::batch([
                Task::window(WindowCommand::Hide(WindowId::new("capture-overlay"))),
                clipboard_write_task(text),
            ]),
        }
    }

    fn capture_overlay_action_task(&mut self, copy_requested: bool) -> Task<Message> {
        let selection = self
            .state
            .capture_selection
            .or(self.state.capture_interaction.selection)
            .map(CaptureRect::normalized);
        let Some(request) = screen_capture_request_from_selection(selection) else {
            self.state.capture_selection = selection.filter(|selection| selection.is_confirmable());
            self.state.ocr_status_text = "Select a region before OCR".to_string();
            return Task::none();
        };

        let mode =
            ocr::pending_mode_from_surface_action(self.state.pending_ocr_mode, copy_requested);
        self.state.pending_ocr_mode = Some(mode);
        self.state.ocr_status_text = format!("{} capture requested", mode.label());
        self.state.capture_selection = None;
        screen_capture_native::capture_screen_region_task(request, move |capture| match capture {
            Some(capture) => {
                let capture = ocr::OcrCaptureResult::from(capture);
                match mode {
                    ocr::OcrMode::Translate => Message::OcrCaptureFinished(capture),
                    ocr::OcrMode::SilentClipboard => Message::SilentOcrCaptureFinished(capture),
                }
            }
            None => Message::OcrCaptureCancelled(mode),
        })
    }

    fn capture_overlay_interaction_task(&mut self, message: &Message) -> Option<Task<Message>> {
        let detector = self.state.capture_window_detector.clone();
        let interaction = match message {
            Message::CaptureMouseMoved(point) => self
                .state
                .capture_interaction
                .on_mouse_move(*point, &detector),
            Message::CaptureLeftButtonDown(point) => {
                self.state.capture_interaction.on_left_button_down(*point)
            }
            Message::CaptureLeftButtonUp(point) => {
                self.state.capture_interaction.on_left_button_up(*point)
            }
            Message::CaptureDoubleClick(point) => {
                self.state.capture_interaction.on_double_click(*point)
            }
            Message::CaptureRightButtonDown => {
                self.state.capture_interaction.on_right_button_down()
            }
            Message::CaptureMouseWheel { delta, point } => self
                .state
                .capture_interaction
                .on_mouse_wheel(*delta, *point, &detector),
            Message::CaptureNudgeSelection { delta_x, delta_y } => self
                .state
                .capture_interaction
                .nudge_selection(*delta_x, *delta_y),
            Message::CaptureEscape => self.state.capture_interaction.on_escape(),
            _ => return None,
        };

        Some(self.apply_capture_interaction(interaction))
    }

    fn apply_capture_interaction(&mut self, interaction: CaptureInteraction) -> Task<Message> {
        match interaction {
            CaptureInteraction::None => Task::none(),
            CaptureInteraction::Redraw => {
                self.state.capture_selection = self.state.capture_interaction.selection;
                Task::none()
            }
            CaptureInteraction::Confirm(selection) => {
                self.state.capture_selection = Some(selection.normalized());
                self.capture_overlay_action_task(false)
            }
            CaptureInteraction::Cancel => {
                ocr::reset_pending_ocr(&mut self.state);
                self.state.capture_interaction = CaptureInteractionState::new();
                self.state.capture_selection = None;
                Task::window(WindowCommand::Hide(WindowId::new("capture-overlay")))
            }
        }
    }

    fn quick_translate_service_batch_task(
        &mut self,
        requests: Vec<quick_translate::QuickTranslateServiceRequest>,
    ) -> Task<Message> {
        Task::batch(
            requests
                .into_iter()
                .map(|request| self.quick_translate_service_task(request)),
        )
    }

    fn quick_translate_service_task(
        &mut self,
        request: quick_translate::QuickTranslateServiceRequest,
    ) -> Task<Message> {
        self.quick_translate_service_task_with_cache_policy(request, false)
    }

    fn quick_translate_retry_service_task(
        &mut self,
        request: quick_translate::QuickTranslateServiceRequest,
    ) -> Task<Message> {
        self.quick_translate_service_task_with_cache_policy(request, true)
    }

    fn quick_translate_service_task_with_cache_policy(
        &mut self,
        request: quick_translate::QuickTranslateServiceRequest,
        bypass_cache_read: bool,
    ) -> Task<Message> {
        if self.state.settings.translation_cache_enabled {
            if let Some(cache_request) =
                quick_translate::translation_cache_request_for_quick_translate(&request)
            {
                if !bypass_cache_read {
                    if let Some(result) = self.state.translation_cache.get(&cache_request) {
                        let update = quick_translate::quick_translate_service_update_from_cache(
                            &request, result,
                        );
                        if quick_translate::quick_translate_update_needs_youdao_phonetic_enrichment(
                            &request, &update,
                        ) {
                            return quick_translate_phonetic_enrichment_task(request, update);
                        }

                        return Task::message(Message::QuickTranslateServiceFinished(update));
                    }
                }

                self.state.pending_quick_translate_cache_requests.insert(
                    (request.query_id, request.service.id.clone()),
                    cache_request,
                );
            }
        }

        quick_translate_backend_service_task(request)
    }

    fn store_quick_translate_cache_result(
        &mut self,
        update: &quick_translate::QuickTranslateServiceUpdate,
    ) {
        let key = (update.query_id, update.outcome.service.id.clone());
        let Some(cache_request) = self
            .state
            .pending_quick_translate_cache_requests
            .remove(&key)
        else {
            return;
        };

        if self.state.settings.translation_cache_enabled
            && quick_translate::store_quick_translate_cache_result(
                &mut self.state.translation_cache,
                &cache_request,
                update,
            )
        {
            let entries = self.state.translation_cache.len();
            self.state.settings.translation_cache_status = if entries == 1 {
                "1 cached result".to_string()
            } else {
                format!("{entries} cached results")
            };
        }
    }

    fn clear_persistent_translation_cache(&self) {
        clear_persistent_translation_cache_for_settings(&state::settings_snapshot(
            &self.state.settings,
        ));
    }

    fn main_window_close_task(&self) -> Task<Message> {
        if self.state.settings.minimize_to_tray {
            Task::window(WindowCommand::Hide(WindowId::new("main")))
        } else {
            Task::batch([
                Task::window(WindowCommand::Close(WindowId::new("main"))),
                Task::exit(),
            ])
        }
    }

    fn tray_task(&mut self, id: &str) -> Task<Message> {
        match id {
            TRAY_SHOW_MAIN => self.hotkey_task(HOTKEY_SHOW_MAIN),
            TRAY_TRANSLATE_CLIPBOARD => tray_clipboard_read_task(),
            TRAY_OCR_TRANSLATE => self.hotkey_task(HOTKEY_OCR_TRANSLATE),
            TRAY_SHOW_MINI => self.hotkey_task(HOTKEY_SHOW_MINI),
            TRAY_SHOW_FIXED => self.hotkey_task(HOTKEY_SHOW_FIXED),
            TRAY_BROWSER_INSTALL => browser_registrar_action_task("install", None),
            TRAY_BROWSER_UNINSTALL => browser_registrar_action_task("uninstall", None),
            TRAY_BROWSER_INSTALL_CHROME => browser_registrar_action_task("install", Some("chrome")),
            TRAY_BROWSER_UNINSTALL_CHROME => {
                browser_registrar_action_task("uninstall", Some("chrome"))
            }
            TRAY_BROWSER_INSTALL_FIREFOX => {
                browser_registrar_action_task("install", Some("firefox"))
            }
            TRAY_BROWSER_UNINSTALL_FIREFOX => {
                browser_registrar_action_task("uninstall", Some("firefox"))
            }
            TRAY_BROWSER_GET_CHROME_EXTENSION => desktop_shell::open_url_task(CHROME_EXTENSION_URL),
            TRAY_BROWSER_GET_FIREFOX_EXTENSION => Task::none(),
            TRAY_OPEN_SETTINGS => {
                self.state.apply(Message::OpenSettings);
                Task::batch([
                    show_and_focus_main_window_task(),
                    settings_runtime_status_task(crate::state::settings_snapshot(
                        &self.state.settings,
                    )),
                ])
            }
            TRAY_EXIT => Task::batch(
                [
                    "pop-button",
                    "capture-overlay",
                    "mini",
                    "fixed",
                    "settings",
                    "main",
                ]
                .into_iter()
                .map(|id| Task::window(WindowCommand::Close(WindowId::new(id))))
                .chain([Task::exit()]),
            ),
            _ => Task::none(),
        }
    }

    fn translate_clipboard_text(&mut self, text: Option<String>) -> Task<Message> {
        let text = text.unwrap_or_default();
        self.state.source_text = text;

        match quick_translate::begin_quick_translate(&mut self.state) {
            Ok(plan) => self.quick_translate_service_batch_task(plan.service_requests()),
            Err(error) => {
                quick_translate::apply_quick_translate_start_error(&mut self.state, error);
                Task::none()
            }
        }
    }

    fn translate_tray_clipboard_text(&mut self, text: Option<String>) -> Task<Message> {
        let Some(text) = text.filter(|value| !value.trim().is_empty()) else {
            return Task::none();
        };
        self.state.source_text = text;

        let show_main = show_and_focus_main_window_task();
        match quick_translate::begin_quick_translate(&mut self.state) {
            Ok(plan) => Task::batch([
                show_main,
                self.quick_translate_service_batch_task(plan.service_requests()),
            ]),
            Err(error) => {
                quick_translate::apply_quick_translate_start_error(&mut self.state, error);
                show_main
            }
        }
    }
}

fn show_and_focus_main_window_task() -> Task<Message> {
    Task::batch([
        Task::window(WindowCommand::Show(WindowId::new("main"))),
        Task::window(WindowCommand::Focus(WindowId::new("main"))),
    ])
}

pub fn screen_capture_request_from_selection(
    selection: Option<CaptureRect>,
) -> Option<easydict_windows_screen_capture::ScreenCaptureRequest> {
    let selection = selection?;
    let selection = selection.normalized();
    if !selection.is_confirmable() {
        return None;
    }

    let Some(width) = selection
        .right
        .checked_sub(selection.left)
        .and_then(|width| u32::try_from(width).ok())
    else {
        return None;
    };
    let Some(height) = selection
        .bottom
        .checked_sub(selection.top)
        .and_then(|height| u32::try_from(height).ok())
    else {
        return None;
    };

    Some(
        easydict_windows_screen_capture::ScreenCaptureRequest::region(
            easydict_windows_screen_capture::ScreenRect::new(
                selection.left,
                selection.top,
                width,
                height,
            ),
        ),
    )
}

fn capture_screen_window_snapshot_task() -> Task<Message> {
    screen_capture_native::capture_screen_windows_task(
        easydict_windows_screen_capture::ScreenWindowSnapshotRequest::new()
            .exclude_title("Easydict Capture"),
        |windows| Message::CaptureWindowsChanged(detected_windows_from_screen_windows(windows)),
    )
}

pub const HOTKEY_SHOW_MAIN: &str = "show-main";
pub const HOTKEY_TRANSLATE_CLIPBOARD: &str = "translate-clipboard";
pub const HOTKEY_OCR_TRANSLATE: &str = "ocr-translate";
pub const HOTKEY_SILENT_OCR: &str = "silent-ocr";
pub const HOTKEY_SHOW_MINI: &str = "show-mini";
pub const HOTKEY_TOGGLE_MINI: &str = "toggle-mini";
pub const HOTKEY_SHOW_FIXED: &str = "show-fixed";
pub const HOTKEY_TOGGLE_FIXED: &str = "toggle-fixed";

pub const TRAY_SHOW_MAIN: &str = "show-main";
pub const TRAY_TRANSLATE_CLIPBOARD: &str = "translate-clipboard";
pub const TRAY_OCR_TRANSLATE: &str = "ocr-translate";
pub const TRAY_SHOW_MINI: &str = "show-mini";
pub const TRAY_SHOW_FIXED: &str = "show-fixed";
pub const TRAY_BROWSER_INSTALL: &str = "browser-install";
pub const TRAY_BROWSER_UNINSTALL: &str = "browser-uninstall";
pub const TRAY_BROWSER_INSTALL_CHROME: &str = "browser-install-chrome";
pub const TRAY_BROWSER_UNINSTALL_CHROME: &str = "browser-uninstall-chrome";
pub const TRAY_BROWSER_GET_CHROME_EXTENSION: &str = "browser-get-chrome-extension";
pub const TRAY_BROWSER_INSTALL_FIREFOX: &str = "browser-install-firefox";
pub const TRAY_BROWSER_UNINSTALL_FIREFOX: &str = "browser-uninstall-firefox";
pub const TRAY_BROWSER_GET_FIREFOX_EXTENSION: &str = "browser-get-firefox-extension";
pub const TRAY_OPEN_SETTINGS: &str = "open-settings";
pub const TRAY_EXIT: &str = "exit";
pub const BROWSER_REGISTRAR_EXE: &str = "easydict_browser_registrar.exe";
pub const CHROME_EXTENSION_URL: &str =
    "https://chromewebstore.google.com/detail/dmokdfinnomehfpmhoeekomncpobgagf";
pub const OCR_TRANSLATE_EVENT_NAME: &str = r"Local\EasydictRs-OcrTranslate";
pub const SHELL_OCR_TRANSLATE: &str = "EasydictRsOCR";
pub const PROTOCOL_EASYDICT: &str = "easydict-rs";
pub const LEGACY_PROTOCOL_EASYDICT: &str = "easydict";

pub fn startup_activation_task_for_args<I, S>(args: I) -> Task<Message>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    startup_activation_message_for_args(args)
        .map(Task::message)
        .unwrap_or_else(Task::none)
}

pub fn default_hotkeys() -> Vec<Hotkey> {
    hotkeys_for_settings(&SettingsState::default())
}

pub fn hotkeys_for_settings(settings: &SettingsState) -> Vec<Hotkey> {
    let mut hotkeys = Vec::new();

    push_configured_hotkey(&mut hotkeys, HOTKEY_SHOW_MAIN, &settings.show_main_hotkey);
    push_configured_hotkey(
        &mut hotkeys,
        HOTKEY_TRANSLATE_CLIPBOARD,
        &settings.translate_clipboard_hotkey,
    );
    push_configured_hotkey(
        &mut hotkeys,
        HOTKEY_OCR_TRANSLATE,
        &settings.ocr_translate_hotkey,
    );
    push_configured_hotkey(&mut hotkeys, HOTKEY_SILENT_OCR, &settings.silent_ocr_hotkey);

    if let Some(show_mini) = configured_hotkey(HOTKEY_SHOW_MINI, &settings.show_mini_hotkey) {
        hotkeys.push(show_mini.clone());
        hotkeys.push(shift_derived_hotkey(HOTKEY_TOGGLE_MINI, show_mini));
    }

    if let Some(show_fixed) = configured_hotkey(HOTKEY_SHOW_FIXED, &settings.show_fixed_hotkey) {
        hotkeys.push(show_fixed.clone());
        hotkeys.push(shift_derived_hotkey(HOTKEY_TOGGLE_FIXED, show_fixed));
    }

    hotkeys
}

pub fn parse_hotkey(id: &str, shortcut: &str) -> Option<Hotkey> {
    let mut modifiers = Vec::new();
    let mut key = None;

    for part in shortcut.split('+') {
        let part = part.trim();
        if part.is_empty() {
            return None;
        }

        match part.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => push_unique_modifier(&mut modifiers, HotkeyModifier::Control),
            "alt" | "option" => push_unique_modifier(&mut modifiers, HotkeyModifier::Alt),
            "shift" => push_unique_modifier(&mut modifiers, HotkeyModifier::Shift),
            "win" | "windows" | "logo" | "meta" | "cmd" | "command" => {
                push_unique_modifier(&mut modifiers, HotkeyModifier::Logo)
            }
            _ => {
                if key.is_some() {
                    return None;
                }
                key = parse_hotkey_key(part);
            }
        }
    }

    if modifiers.is_empty() {
        return None;
    }

    let mut hotkey = Hotkey::new(id, key?);
    for modifier in modifiers {
        hotkey = hotkey.modifier(modifier);
    }

    Some(hotkey)
}

pub fn default_tray_menu() -> TrayMenu<Message> {
    tray_menu_for_browser_support(&BrowserSupportState::default())
}

pub fn tray_menu_for_browser_support(browser: &BrowserSupportState) -> TrayMenu<Message> {
    tray_menu_for_browser_support_locale(browser, "en-US")
}

pub fn tray_menu_for_browser_support_locale(
    browser: &BrowserSupportState,
    locale: &str,
) -> TrayMenu<Message> {
    let any_not_installed = !browser.chrome_installed || !browser.firefox_installed;
    let any_installed = browser.chrome_installed || browser.firefox_installed;
    let tray = TrayMenu::new("Easydict - Dictionary & Translation").default_item(TRAY_SHOW_MAIN);
    let tray = if let Some(icon_path) = default_tray_icon_path() {
        tray.icon_path(icon_path)
    } else {
        tray
    };

    tray.item(
        TrayMenuItem::new(
            TRAY_SHOW_MAIN,
            crate::i18n::tr_locale(locale, "tray.show", "Show Easydict"),
        )
        .on_invoke(Message::TrayCommand(TRAY_SHOW_MAIN.to_string())),
    )
    .item(
        TrayMenuItem::new(
            TRAY_TRANSLATE_CLIPBOARD,
            crate::i18n::tr_locale(locale, "tray.translate_clipboard", "Translate Clipboard"),
        )
        .on_invoke(Message::TrayCommand(TRAY_TRANSLATE_CLIPBOARD.to_string())),
    )
    .item(
        TrayMenuItem::new(
            TRAY_OCR_TRANSLATE,
            format!(
                "{} (Ctrl+Alt+S)",
                crate::i18n::tr_locale(locale, "tray.ocr_translate", "OCR Translate")
            ),
        )
        .on_invoke(Message::TrayCommand(TRAY_OCR_TRANSLATE.to_string())),
    )
    .item(
        TrayMenuItem::new(
            TRAY_SHOW_MINI,
            format!(
                "{} (Ctrl+Alt+M)",
                crate::i18n::tr_locale(locale, "tray.show_mini", "Mini Window")
            ),
        )
        .on_invoke(Message::TrayCommand(TRAY_SHOW_MINI.to_string())),
    )
    .item(
        TrayMenuItem::new(
            TRAY_SHOW_FIXED,
            format!(
                "{} (Ctrl+Alt+F)",
                crate::i18n::tr_locale(locale, "tray.show_fixed", "Fixed Window")
            ),
        )
        .on_invoke(Message::TrayCommand(TRAY_SHOW_FIXED.to_string())),
    )
    .separator()
    .item(
        TrayMenuItem::submenu(
            "browser-support",
            crate::i18n::tr_locale(locale, "tray.browser_support", "Browser Support"),
        )
        .item(
            TrayMenuItem::submenu(
                "browser-chrome",
                crate::i18n::tr_locale(locale, "tray.browser.chrome", "Chrome"),
            )
            .item(
                TrayMenuItem::new(
                    TRAY_BROWSER_INSTALL_CHROME,
                    crate::i18n::tr_locale(
                        locale,
                        "tray.browser.install_chrome",
                        "① Install Chrome Support",
                    ),
                )
                .enabled(!browser.chrome_installed)
                .on_invoke(Message::TrayCommand(
                    TRAY_BROWSER_INSTALL_CHROME.to_string(),
                )),
            )
            .item(
                TrayMenuItem::new(
                    TRAY_BROWSER_UNINSTALL_CHROME,
                    crate::i18n::tr_locale(
                        locale,
                        "tray.browser.uninstall_chrome",
                        "Uninstall Chrome Support",
                    ),
                )
                .enabled(browser.chrome_installed)
                .on_invoke(Message::TrayCommand(
                    TRAY_BROWSER_UNINSTALL_CHROME.to_string(),
                )),
            )
            .item(
                TrayMenuItem::new(
                    TRAY_BROWSER_GET_CHROME_EXTENSION,
                    crate::i18n::tr_locale(
                        locale,
                        "tray.browser.get_chrome_extension",
                        "② Get Extension",
                    ),
                )
                .on_invoke(Message::TrayCommand(
                    TRAY_BROWSER_GET_CHROME_EXTENSION.to_string(),
                )),
            ),
        )
        .item(
            TrayMenuItem::submenu(
                "browser-firefox",
                crate::i18n::tr_locale(locale, "tray.browser.firefox", "Firefox"),
            )
            .item(
                TrayMenuItem::new(
                    TRAY_BROWSER_INSTALL_FIREFOX,
                    crate::i18n::tr_locale(
                        locale,
                        "tray.browser.install_firefox",
                        "① Install Firefox Support",
                    ),
                )
                .enabled(!browser.firefox_installed)
                .on_invoke(Message::TrayCommand(
                    TRAY_BROWSER_INSTALL_FIREFOX.to_string(),
                )),
            )
            .item(
                TrayMenuItem::new(
                    TRAY_BROWSER_UNINSTALL_FIREFOX,
                    crate::i18n::tr_locale(
                        locale,
                        "tray.browser.uninstall_firefox",
                        "Uninstall Firefox Support",
                    ),
                )
                .enabled(browser.firefox_installed)
                .on_invoke(Message::TrayCommand(
                    TRAY_BROWSER_UNINSTALL_FIREFOX.to_string(),
                )),
            )
            .item(
                TrayMenuItem::new(
                    TRAY_BROWSER_GET_FIREFOX_EXTENSION,
                    crate::i18n::tr_locale(
                        locale,
                        "tray.browser.get_firefox_extension",
                        "② Get Extension",
                    ),
                )
                .on_invoke(Message::TrayCommand(
                    TRAY_BROWSER_GET_FIREFOX_EXTENSION.to_string(),
                )),
            ),
        )
        .item(TrayMenuItem::separator())
        .item(
            TrayMenuItem::new(
                TRAY_BROWSER_INSTALL,
                crate::i18n::tr_locale(locale, "tray.browser.install_all", "Install All"),
            )
            .enabled(any_not_installed)
            .on_invoke(Message::TrayCommand(TRAY_BROWSER_INSTALL.to_string())),
        )
        .item(
            TrayMenuItem::new(
                TRAY_BROWSER_UNINSTALL,
                crate::i18n::tr_locale(locale, "tray.browser.uninstall_all", "Uninstall All"),
            )
            .enabled(any_installed)
            .on_invoke(Message::TrayCommand(TRAY_BROWSER_UNINSTALL.to_string())),
        ),
    )
    .item(
        TrayMenuItem::new(
            TRAY_OPEN_SETTINGS,
            crate::i18n::tr_locale(locale, "tray.settings", "Settings"),
        )
        .on_invoke(Message::TrayCommand(TRAY_OPEN_SETTINGS.to_string())),
    )
    .separator()
    .item(
        TrayMenuItem::new(
            TRAY_EXIT,
            crate::i18n::tr_locale(locale, "tray.exit", "Exit"),
        )
        .on_invoke(Message::TrayCommand(TRAY_EXIT.to_string())),
    )
}

pub fn default_tray_icon_path() -> Option<String> {
    for candidate in default_tray_icon_path_candidates() {
        if candidate.is_file() {
            let path = std::fs::canonicalize(&candidate).unwrap_or(candidate);
            return Some(path.to_string_lossy().into_owned());
        }
    }

    None
}

fn default_tray_icon_path_candidates() -> Vec<std::path::PathBuf> {
    let mut candidates = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(directory) = exe.parent() {
            push_tray_icon_candidates_from_directory(&mut candidates, directory);
        }
    }

    if let Ok(directory) = std::env::current_dir() {
        push_tray_icon_candidates_from_directory(&mut candidates, &directory);
    }

    candidates
}

fn push_tray_icon_candidates_from_directory(
    candidates: &mut Vec<std::path::PathBuf>,
    directory: &std::path::Path,
) {
    candidates.push(directory.join("AppIcon.ico"));
    for ancestor in directory.ancestors() {
        candidates.push(
            ancestor
                .join("crates")
                .join("easydict_app")
                .join("resources")
                .join("AppIcon.ico"),
        );
        candidates.push(
            ancestor
                .join("rs")
                .join("crates")
                .join("easydict_app")
                .join("resources")
                .join("AppIcon.ico"),
        );
    }
}

pub fn default_named_events() -> Vec<NamedEventRegistration<Message>> {
    vec![NamedEventRegistration::new(OCR_TRANSLATE_EVENT_NAME)
        .on_signal(Message::HotkeyTriggered(HOTKEY_OCR_TRANSLATE.to_string()))]
}

pub fn default_desktop_shell_verbs() -> Vec<desktop_integration::DesktopShellVerb> {
    vec![default_desktop_shell_verb()]
}

pub fn default_desktop_shell_verb() -> desktop_integration::DesktopShellVerb {
    desktop_integration::DesktopShellVerb::new(SHELL_OCR_TRANSLATE, "OCR Translate")
        .directory_background(true)
        .argument("--ocr-translate")
}

pub fn default_desktop_protocol_registrations(
) -> Vec<desktop_integration::DesktopProtocolRegistration> {
    vec![desktop_integration::DesktopProtocolRegistration::new(
        PROTOCOL_EASYDICT,
        "URL:Easydict Rust Protocol",
    )
    .argument("%1")]
}

fn platform_shell_verb_from_desktop(verb: &desktop_integration::DesktopShellVerb) -> ShellVerb {
    ShellVerb {
        id: verb.id.clone(),
        label: verb.label.clone(),
        accepts_files: verb.accepts_files,
        accepts_directory_background: verb.accepts_directory_background,
        arguments: verb.arguments.clone(),
    }
}

fn platform_protocol_registration_from_desktop(
    protocol: &desktop_integration::DesktopProtocolRegistration,
) -> ProtocolRegistration {
    ProtocolRegistration {
        scheme: protocol.scheme.clone(),
        description: protocol.description.clone(),
        arguments: protocol.arguments.clone(),
    }
}

pub fn browser_registrar_task(command: &'static str) -> Task<Message> {
    browser_registrar_task_for(command, None)
}

pub fn browser_registrar_task_for(
    command: &'static str,
    browser: Option<&'static str>,
) -> Task<Message> {
    desktop_shell::run_browser_registrar_task(browser_registrar_arguments(command, browser))
}

pub fn browser_registrar_arguments(command: &str, browser: Option<&str>) -> Vec<String> {
    let mut arguments = vec![
        command.to_string(),
        "--bridge-root-name".to_string(),
        browser_registrar::RUST_BRIDGE_ROOT_NAME.to_string(),
    ];
    match browser {
        Some("chrome") => arguments.push("--chrome".to_string()),
        Some("firefox") => arguments.push("--firefox".to_string()),
        _ => {}
    }
    arguments
}

fn browser_registrar_action_task(
    command: &'static str,
    browser: Option<&'static str>,
) -> Task<Message> {
    Task::batch([
        browser_registrar_task_for(command, browser),
        browser_support_status_task(std::time::Duration::from_millis(600)),
    ])
}

fn register_shell_verb_task(verb: desktop_integration::DesktopShellVerb) -> Task<Message> {
    Task::perform(
        async move { desktop_integration::register_shell_verb(verb) },
        Message::DesktopIntegrationActionFinished,
    )
}

fn unregister_shell_verb_task(verb: desktop_integration::DesktopShellVerb) -> Task<Message> {
    Task::perform(
        async move { desktop_integration::unregister_shell_verb(verb) },
        Message::DesktopIntegrationActionFinished,
    )
}

fn named_event_listener_task() -> Task<Message> {
    Task::stream(named_event::named_event_stream(
        OCR_TRANSLATE_EVENT_NAME,
        true,
        Message::HotkeyTriggered(HOTKEY_OCR_TRANSLATE.to_string()),
    ))
}

fn protocol_registration_task() -> Task<Message> {
    Task::perform(
        async move {
            for protocol in default_desktop_protocol_registrations() {
                desktop_integration::register_protocol(protocol)?;
            }
            Ok(())
        },
        Message::DesktopIntegrationActionFinished,
    )
}

fn startup_registration_task(enabled: bool) -> Task<Message> {
    Task::perform(
        async move { desktop_integration::set_startup_enabled(enabled) },
        Message::DesktopIntegrationActionFinished,
    )
}

fn browser_support_status_task(delay: std::time::Duration) -> Task<Message> {
    Task::perform(
        async move {
            if !delay.is_zero() {
                std::thread::sleep(delay);
            }
            load_browser_support_status()
        },
        Message::BrowserSupportStatusLoaded,
    )
}

pub fn load_browser_support_status() -> Result<browser_registrar::StatusOutput, String> {
    let local_app_data = std::env::var_os("LOCALAPPDATA")
        .filter(|value| !value.is_empty())
        .map(std::path::PathBuf::from)
        .ok_or_else(|| {
            "LOCALAPPDATA is not set; cannot resolve browser bridge directory".to_string()
        })?;
    let bridge_directory = browser_registrar::bridge_directory_for_root(
        local_app_data,
        browser_registrar::RUST_BRIDGE_ROOT_NAME,
    );
    let core =
        browser_registrar::BrowserRegistrarCore::new(bridge_directory, CurrentUserBrowserRegistry);
    Ok(core.status())
}

struct CurrentUserBrowserRegistry;

impl browser_registrar::BrowserRegistry for CurrentUserBrowserRegistry {
    fn write_default_value(&mut self, key_path: &str, value: &str) -> std::io::Result<()> {
        easydict_windows_registry::write_current_user_default_string(key_path, value)
            .map_err(browser_registry_error)
    }

    fn delete_key(&mut self, key_path: &str) -> std::io::Result<()> {
        easydict_windows_registry::delete_current_user_key(key_path).map_err(browser_registry_error)
    }

    fn read_default_value(&self, key_path: &str) -> std::io::Result<Option<String>> {
        easydict_windows_registry::read_current_user_default_string(key_path)
            .map_err(browser_registry_error)
    }
}

fn browser_registry_error(
    error: easydict_windows_registry::WindowsRegistryError,
) -> std::io::Error {
    std::io::Error::other(error.to_string())
}

/// Async task that checks on-disk availability of downloadable settings assets
/// under the conventional Easydict data directory, replacing static
/// placeholder statuses with real values once it resolves.
fn settings_runtime_status_task(settings: protocol::SettingsSnapshot) -> Task<Message> {
    Task::perform(
        async move { settings_status::load_runtime_status_for_settings(settings) },
        Message::SettingsRuntimeStatusLoaded,
    )
}

fn settings_save_task(settings: SettingsState) -> Task<Message> {
    Task::perform(
        async move {
            let path = settings_storage::default_settings_storage_path();
            let _ = settings_storage::save_settings_file(path, &settings);
        },
        |_| Message::Noop,
    )
}

fn pop_button_auto_dismiss_task(generation: u64) -> Task<Message> {
    Task::perform(
        async move {
            std::thread::sleep(std::time::Duration::from_secs(5));
            generation
        },
        Message::PopButtonAutoDismiss,
    )
}

fn selected_text_capture_task() -> Task<Message> {
    Task::perform(
        async move { text_selection::capture_native_selected_text_after_hotkey_delay() },
        |text| match text {
            Some(text) if !text.trim().is_empty() => Message::ClipboardTextReceived(Some(text)),
            _ => Message::Noop,
        },
    )
}

fn capture_text_insertion_target_task() -> Task<Message> {
    Task::perform(
        async move {
            let _ = text_insertion::capture_text_insertion_target();
        },
        |_| Message::Noop,
    )
}

fn insert_text_task(text: String) -> Task<Message> {
    if text.is_empty() {
        return Task::none();
    }

    Task::perform(
        async move {
            let _ = text_insertion::insert_text_into_captured_target(text);
        },
        |_| Message::Noop,
    )
}

fn tray_clipboard_read_task() -> Task<Message> {
    Task::perform(
        async move { clipboard::read_clipboard_text().ok().flatten() },
        Message::TrayClipboardTextReceived,
    )
}

fn clipboard_monitor_task_for_settings(settings: &SettingsState) -> Task<Message> {
    if !settings.monitor_clipboard {
        clipboard::stop_clipboard_monitor();
        return Task::none();
    }

    clipboard::clipboard_monitor_stream(|text| Message::ClipboardTextReceived(Some(text)))
        .map(Task::stream)
        .unwrap_or_else(Task::none)
}

fn mouse_selection_hook_task_for_settings(settings: &SettingsState) -> Task<Message> {
    if !settings.mouse_selection_translate {
        mouse_selection::stop_mouse_selection_hook();
        return Task::none();
    }

    mouse_selection::mouse_selection_hook_stream(Message::MouseSelectionInputHookEvent)
        .map(Task::stream)
        .unwrap_or_else(Task::none)
}

fn clipboard_write_task(text: String) -> Task<Message> {
    Task::perform(
        async move {
            let _ = clipboard::write_clipboard_text(text);
        },
        |_| Message::Noop,
    )
}

fn speak_text_task(text: String, language: Option<String>, tts_speed: String) -> Task<Message> {
    if text.trim().is_empty() {
        return Task::none();
    }

    Task::perform(
        async move {
            let speaking_rate = tts::parse_speaking_rate(&tts_speed);
            let _ = tts::speak_text(text, language, speaking_rate);
        },
        |_| Message::Noop,
    )
}

pub fn mouse_selection_capture_result_message(
    request: MouseSelectionCaptureRequest,
    text: Option<String>,
) -> Message {
    let Some(text) = text.filter(|text| !text.trim().is_empty()) else {
        return Message::Noop;
    };

    let ready = request.selection_text_ready(text);
    Message::SelectionTextReady {
        text: ready.text,
        anchor_x: ready.anchor_x,
        anchor_y: ready.anchor_y,
        generation: ready.generation,
    }
}

pub fn mouse_selection_capture_task(request: MouseSelectionCaptureRequest) -> Task<Message> {
    Task::perform(
        async move { text_selection::capture_native_selected_text_after_hotkey_delay() },
        move |text| mouse_selection_capture_result_message(request, text),
    )
}

pub fn mouse_selection_producer_action_task(action: MouseSelectionProducerAction) -> Task<Message> {
    match action {
        MouseSelectionProducerAction::DismissPopButton { .. } => {
            Task::message(Message::DismissPopButton)
        }
        MouseSelectionProducerAction::CaptureSelectionText(request) => {
            mouse_selection_capture_task(request)
        }
        MouseSelectionProducerAction::SchedulePendingMultiClick {
            pending,
            generation,
        } => mouse_selection_pending_multi_click_task(generation, pending.delay_ms),
        MouseSelectionProducerAction::CancelPendingMultiClick { .. } => Task::none(),
    }
}

pub fn mouse_selection_producer_actions_task(
    actions: Vec<MouseSelectionProducerAction>,
) -> Task<Message> {
    Task::batch(
        actions
            .into_iter()
            .map(mouse_selection_producer_action_task),
    )
}

pub fn mouse_selection_pending_timer(action: &MouseSelectionProducerAction) -> Option<(u64, u64)> {
    match action {
        MouseSelectionProducerAction::SchedulePendingMultiClick {
            pending,
            generation,
        } => Some((*generation, pending.delay_ms)),
        _ => None,
    }
}

fn mouse_selection_pending_multi_click_task(generation: u64, delay_ms: u64) -> Task<Message> {
    Task::perform(
        async move {
            std::thread::sleep(std::time::Duration::from_millis(delay_ms));
            generation
        },
        Message::MouseSelectionPendingMultiClickElapsed,
    )
}

fn built_in_ai_device_registration_task(settings: &SettingsState) -> Task<Message> {
    let snapshot = crate::state::settings_snapshot(settings);
    if snapshot
        .built_in_ai_api_key
        .as_deref()
        .is_some_and(|api_key| !api_key.trim().is_empty())
        || snapshot
            .device_token
            .as_deref()
            .is_some_and(|token| !token.trim().is_empty())
    {
        return Task::none();
    }

    let Some(device_id) = snapshot
        .device_id
        .as_deref()
        .map(str::trim)
        .filter(|device_id| !device_id.is_empty())
        .map(str::to_string)
    else {
        return Task::none();
    };

    Task::perform(
        async move {
            let Some(request) =
                openai_compatible::built_in_ai_embedded_device_registration_request_plan(
                    &device_id,
                )
            else {
                return Ok(None);
            };

            let mut client = openai_compatible::ReqwestOpenAiHttpClient::from_settings(&snapshot)
                .map_err(|error| error.to_string())?;
            openai_compatible::register_built_in_ai_device(&mut client, &request)
                .map_err(|error| error.to_string())
        },
        Message::BuiltInAiDeviceRegistrationFinished,
    )
}

fn foundry_local_prepare_task(settings: protocol::SettingsSnapshot) -> Task<Message> {
    Task::perform(
        async move {
            let mut controller = openai_compatible::default_foundry_local_runtime_controller();
            openai_compatible::prepare_foundry_local_service(&mut controller, &settings)
                .map_err(|error| error.to_string())
        },
        Message::FoundryLocalPrepareFinished,
    )
}

fn windows_ai_prepare_task() -> Task<Message> {
    Task::perform(
        async move {
            let mut client = easydict_windows_ai::default_windows_ai_language_model_client();
            easydict_windows_ai::prepare_windows_ai_client(&mut client)
                .map_err(|error| error.to_string())
        },
        Message::WindowsAiPrepareFinished,
    )
}

fn openvino_download_task(settings: protocol::SettingsSnapshot) -> Task<Message> {
    Task::perform(
        async move {
            let mut progress = |_progress: resource_download::ResourceDownloadProgress| {};
            openvino_download::ensure_openvino_assets_available(&settings, &mut progress)
                .map_err(|error| error.to_string())
        },
        Message::OpenVinoDownloadFinished,
    )
}

fn push_configured_hotkey(hotkeys: &mut Vec<Hotkey>, id: &str, setting: &HotkeySetting) {
    if let Some(hotkey) = configured_hotkey(id, setting) {
        hotkeys.push(hotkey);
    }
}

fn configured_hotkey(id: &str, setting: &HotkeySetting) -> Option<Hotkey> {
    setting
        .enabled
        .then(|| parse_hotkey(id, &setting.shortcut))
        .flatten()
}

fn shift_derived_hotkey(id: &str, mut hotkey: Hotkey) -> Hotkey {
    hotkey.id = id.to_string();
    if !hotkey.modifiers.contains(&HotkeyModifier::Shift) {
        hotkey.modifiers.push(HotkeyModifier::Shift);
    }
    hotkey
}

fn push_unique_modifier(modifiers: &mut Vec<HotkeyModifier>, modifier: HotkeyModifier) {
    if !modifiers.contains(&modifier) {
        modifiers.push(modifier);
    }
}

fn parse_hotkey_key(part: &str) -> Option<HotkeyKey> {
    let lower = part.to_ascii_lowercase();
    if let Some(number) = lower.strip_prefix('f') {
        if let Ok(value) = number.parse::<u8>() {
            if (1..=24).contains(&value) {
                return Some(HotkeyKey::Function(value));
            }
        }
    }

    if part.chars().count() == 1 {
        let character = part.chars().next()?;
        if character.is_ascii_alphanumeric() {
            return Some(HotkeyKey::Character(character.to_ascii_lowercase()));
        }
    }

    match lower.as_str() {
        "backspace" => Some(HotkeyKey::Named("backspace".to_string())),
        "delete" | "del" => Some(HotkeyKey::Named("delete".to_string())),
        "down" | "arrowdown" => Some(HotkeyKey::Named("down".to_string())),
        "end" => Some(HotkeyKey::Named("end".to_string())),
        "enter" | "return" => Some(HotkeyKey::Named("enter".to_string())),
        "escape" | "esc" => Some(HotkeyKey::Named("escape".to_string())),
        "home" => Some(HotkeyKey::Named("home".to_string())),
        "left" | "arrowleft" => Some(HotkeyKey::Named("left".to_string())),
        "right" | "arrowright" => Some(HotkeyKey::Named("right".to_string())),
        "space" => Some(HotkeyKey::Named("space".to_string())),
        "tab" => Some(HotkeyKey::Named("tab".to_string())),
        "up" | "arrowup" => Some(HotkeyKey::Named("up".to_string())),
        _ => None,
    }
}

fn result_action_task_for_message(
    state: &mut EasydictUiState,
    message: &Message,
) -> Option<Task<Message>> {
    let (kind, surface, service_id) = match message {
        Message::CopyResultIn(surface, service_id) => {
            (ResultActionKind::Copy, *surface, service_id)
        }
        Message::SpeakResultIn(surface, service_id) => {
            (ResultActionKind::Speak, *surface, service_id)
        }
        Message::ReplaceResultIn(surface, service_id) => {
            (ResultActionKind::Replace, *surface, service_id)
        }
        _ => return None,
    };

    let Some(intent) = state::resolve_result_action_intent(state, kind, surface, service_id) else {
        return Some(Task::none());
    };

    state.last_result_action = Some(intent.clone());
    let tts_speed = state.settings.tts_speed.clone();
    Some(result_action_task(intent, tts_speed))
}

fn result_action_task(intent: ResultActionIntent, tts_speed: String) -> Task<Message> {
    match intent.kind {
        ResultActionKind::Copy => clipboard_write_task(intent.text),
        ResultActionKind::Speak => speak_text_task(intent.text, Some(intent.language), tts_speed),
        ResultActionKind::Replace => insert_text_task(intent.text),
    }
}

fn auto_play_translation_task(
    state: &EasydictUiState,
    update: &quick_translate::QuickTranslateServiceUpdate,
) -> Task<Message> {
    if !state.settings.auto_play_translation {
        return Task::none();
    }

    let Ok(result) = &update.outcome.result else {
        return Task::none();
    };

    let text = result.translated_text.trim();
    if text.is_empty() {
        return Task::none();
    }

    let Some((success_count, target_language)) = auto_play_query_context(state, update.query_id)
    else {
        return Task::none();
    };

    if success_count > 0 {
        return Task::none();
    }

    speak_text_task(
        text.to_string(),
        Some(target_language),
        state.settings.tts_speed.clone(),
    )
}

fn auto_play_query_context(state: &EasydictUiState, query_id: u64) -> Option<(usize, String)> {
    if state.active_query_id == Some(query_id) {
        Some((
            state.active_query_success_count,
            state.target_language.clone(),
        ))
    } else if state.mini.active_query_id == Some(query_id) {
        Some((
            state.mini.active_query_success_count,
            state.mini.target_language.clone(),
        ))
    } else if state.fixed.active_query_id == Some(query_id) {
        Some((
            state.fixed.active_query_success_count,
            state.fixed.target_language.clone(),
        ))
    } else {
        None
    }
}

fn quick_translate_backend_service_task(
    request: quick_translate::QuickTranslateServiceRequest,
) -> Task<Message> {
    if request.execution_kind == quick_translate::QuickTranslateExecutionKind::TranslateStream {
        Task::stream(
            quick_translate::run_quick_translate_streaming_service_with_current_app_dir(request),
        )
    } else {
        Task::perform(
            async move {
                let update = quick_translate::run_quick_translate_service_with_current_app_dir(
                    request.clone(),
                );
                quick_translate::enrich_quick_translate_update_with_global_youdao_phonetics(
                    &request, update,
                )
            },
            Message::QuickTranslateServiceFinished,
        )
    }
}

fn quick_translate_phonetic_enrichment_task(
    request: quick_translate::QuickTranslateServiceRequest,
    update: quick_translate::QuickTranslateServiceUpdate,
) -> Task<Message> {
    Task::perform(
        async move {
            quick_translate::enrich_quick_translate_update_with_global_youdao_phonetics(
                &request, update,
            )
        },
        Message::QuickTranslateServiceFinished,
    )
}

fn long_document_task(request: long_document::LongDocumentServiceRequest) -> Task<Message> {
    Task::perform(
        async move { long_document::run_long_document_request_with_current_app_dir(request) },
        Message::LongDocumentFinished,
    )
}

fn long_document_retry_failed_task(
    request: long_document::LongDocumentServiceRequest,
    result_json_path: String,
) -> Task<Message> {
    Task::perform(
        async move {
            long_document::retry_failed_native_text_long_document_from_result_json(
                request,
                result_json_path,
            )
        },
        Message::LongDocumentFinished,
    )
}

fn ocr_recognize_task(request: ocr::OcrRecognizeRequest) -> Task<Message> {
    Task::perform(
        async move { ocr::run_ocr_recognize_with_current_app_dir(request) },
        Message::OcrRecognizeFinished,
    )
}

fn local_dictionary_suggestion_task(
    request: local_dictionary::LocalDictionarySuggestionRequest,
) -> Task<Message> {
    Task::perform(
        async move {
            local_dictionary::run_delayed_local_dictionary_suggestion_request_with_current_app_dir(
                request,
            )
        },
        Message::LocalDictionarySuggestionsFinished,
    )
}

fn open_file_dialog_task(
    options: file_dialog::AppOpenFileDialogOptions,
    map: fn(Option<String>) -> Message,
) -> Task<Message> {
    Task::perform(async move { file_dialog::open_file_dialog(options) }, map)
}

fn open_folder_dialog_task(
    options: file_dialog::AppOpenFolderDialogOptions,
    map: fn(Option<String>) -> Message,
) -> Task<Message> {
    Task::perform(async move { file_dialog::open_folder_dialog(options) }, map)
}

fn long_document_file_dialog_options(
    state: &EasydictUiState,
) -> file_dialog::AppOpenFileDialogOptions {
    let mut options = file_dialog::AppOpenFileDialogOptions::new("Open document")
        .filter(file_dialog::file_filter(
            "Supported documents",
            ["*.pdf", "*.md", "*.markdown", "*.txt"],
        ))
        .filter(file_dialog::file_filter("PDF files", ["*.pdf"]))
        .filter(file_dialog::file_filter(
            "Markdown files",
            ["*.md", "*.markdown"],
        ))
        .filter(file_dialog::file_filter("Text files", ["*.txt"]));

    let output_folder = state.long_document.output_folder.trim();
    if !output_folder.is_empty() && !output_folder.starts_with('(') {
        options = options.initial_directory(output_folder);
    }

    options
}

fn long_document_output_folder_dialog_options(
    state: &EasydictUiState,
) -> file_dialog::AppOpenFolderDialogOptions {
    let mut options = file_dialog::AppOpenFolderDialogOptions::new("Select output folder");
    let output_folder = state.long_document.output_folder.trim();
    if !output_folder.is_empty() && !output_folder.starts_with('(') {
        options = options.initial_directory(output_folder);
    }

    options
}

fn mdx_dictionary_file_dialog_options() -> file_dialog::AppOpenFileDialogOptions {
    file_dialog::AppOpenFileDialogOptions::new("Import MDX dictionary")
        .filter(file_dialog::file_filter("MDX dictionaries", ["*.mdx"]))
        .filter(file_dialog::file_filter("All files", ["*.*"]))
}
