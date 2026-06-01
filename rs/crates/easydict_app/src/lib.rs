#![forbid(unsafe_code)]

pub mod activation;
pub mod browser_registrar;
pub mod cli_translate;
pub mod compat_client;
pub mod compat_protocol;
pub mod credential_protection;
pub mod custom_streaming;
pub mod grammar_correction;
mod i18n;
pub mod llm_streaming;
pub mod local_dictionary;
pub mod long_document;
pub mod native_bridge;
pub mod ocr;
pub mod openai_compatible;
pub mod quick_translate;
pub mod screen_capture;
pub mod settings_migration;
pub mod settings_storage;
pub mod state;
pub mod theme;
pub mod traditional_http;
pub mod translation_cache;
pub mod translation_language;
pub mod translation_services;
pub mod ui;
pub mod window_options;

pub use custom_streaming::{
    build_custom_streaming_grammar_request_plan, build_custom_streaming_translation_request_plan,
    build_doubao_translation_request_plan, build_gemini_grammar_request_plan,
    build_gemini_translation_request_plan, cleanup_custom_streaming_translation_text,
    cleanup_doubao_translation_text, correct_custom_streaming_grammar,
    custom_streaming_config_for_service, custom_streaming_error_from_response,
    doubao_language_code, doubao_service_config, execute_custom_streaming_request,
    gemini_service_config, parse_custom_streaming_chunks, parse_doubao_stream_chunks,
    parse_gemini_stream_chunks, translate_custom_streaming_service, CustomStreamingFormat,
    CustomStreamingHttpClient, CustomStreamingHttpRequestPlan, CustomStreamingServiceConfig,
    DoubaoConfig, GeminiConfig, ReqwestCustomStreamingHttpClient, DOUBAO_DEFAULT_ENDPOINT,
    DOUBAO_DEFAULT_MODEL, GEMINI_API_BASE_URL, GEMINI_DEFAULT_MODEL,
};
pub use llm_streaming::{
    chat_completions_sse_chunks, extract_chat_completions_delta, extract_responses_delta,
    parse_chat_completions_sse_chunks, parse_openai_sse_chunks, parse_responses_sse_chunks,
    responses_sse_chunks, ChatCompletionsSseChunks, ChatMessage, ChatRole, OpenAiStreamingFormat,
    ResponsesSseChunks,
};
pub use local_dictionary::{
    apply_active_local_dictionary_suggestion, apply_local_dictionary_suggestion,
    apply_local_dictionary_suggestion_update, begin_local_dictionary_suggestions,
    dismiss_local_dictionary_suggestions, exit_local_dictionary_suggestions,
    focus_local_dictionary_suggestions, local_dictionary_query_token,
    move_local_dictionary_suggestion,
    run_delayed_local_dictionary_suggestion_request_with_current_app_dir,
    run_local_dictionary_suggestion_request,
    run_local_dictionary_suggestion_request_with_current_app_dir, LocalDictionarySuggestionBackend,
    LocalDictionarySuggestionError, LocalDictionarySuggestionRequest,
    LocalDictionarySuggestionUpdate, LOCAL_DICTIONARY_SUGGESTION_DELAY_MS,
};
pub use long_document::{
    apply_long_document_outcome, apply_long_document_start_error, begin_long_document_translate,
    build_long_document_request, run_long_document_request, LongDocumentBackend,
    LongDocumentBackendError, LongDocumentEvent, LongDocumentInput, LongDocumentOutcome,
    LongDocumentServiceRequest, LongDocumentStartError,
};
pub use ocr::{
    apply_ocr_outcome, apply_ocr_start_error, begin_ocr_recognize, bgra_to_base64_bmp,
    bgra_to_base64_jpeg_data_url, build_custom_api_ocr_request, build_ollama_ocr_request,
    group_and_sort_ocr_lines, merge_ocr_lines, merge_ocr_words, merged_ocr_text,
    parse_ocr_http_response, run_ocr_recognize, run_ocr_recognize_with_current_app_dir,
    run_ocr_recognize_with_native_provider, run_ocr_recognize_with_packaged_host, NativeOcrBackend,
    OcrBackend, OcrBackendError, OcrCaptureResult, OcrEngineConfig, OcrEngineKind, OcrHttpClient,
    OcrHttpRequestPlan, OcrHttpResponseParser, OcrImageEncodeError, OcrMode, OcrOutcome,
    OcrRecognizeRequest, OcrResultAction, OcrStartError,
};
pub use openai_compatible::{
    build_openai_grammar_messages, build_openai_grammar_request_plan,
    build_openai_http_request_plan, build_openai_request_body, build_openai_translation_messages,
    build_openai_translation_request_plan, built_in_ai_direct_endpoint_for_model,
    built_in_ai_direct_service_config, built_in_ai_proxy_headers, clamp_openai_temperature,
    cleanup_openai_translation_text, correct_grammar_openai_compatible,
    custom_openai_service_config, deepseek_service_config, detect_openai_api_format_from_url,
    execute_openai_stream_request, github_models_service_config, groq_service_config,
    ollama_model_refresh_fallback, ollama_service_config, ollama_tags_url_from_endpoint,
    openai_api_format_from_setting, openai_compatible_config_for_service,
    openai_effective_temperature, openai_error_from_response, openai_responses_reasoning_effort,
    openai_service_config, parse_ollama_model_names, resolve_ollama_model_refresh,
    resolve_openai_api_format, translate_openai_compatible, validate_openai_config,
    zhipu_service_config, OllamaModelRefreshOutcome, OpenAiApiFormat, OpenAiCompatibleConfig,
    OpenAiExecutionError, OpenAiExecutionErrorCode, OpenAiHttpClient, OpenAiHttpRequestPlan,
    OpenAiPlanError, OpenAiTranslationRequest, ReqwestOpenAiHttpClient, BUILT_IN_AI_DEFAULT_MODEL,
    CUSTOM_OPENAI_DEFAULT_MODEL, DEEPSEEK_DEFAULT_ENDPOINT, DEEPSEEK_DEFAULT_MODEL,
    GITHUB_MODELS_DEFAULT_ENDPOINT, GITHUB_MODELS_DEFAULT_MODEL, GROQ_DEFAULT_ENDPOINT,
    GROQ_DEFAULT_MODEL, OLLAMA_DEFAULT_ENDPOINT, OLLAMA_DEFAULT_MODEL, OPENAI_DEFAULT_ENDPOINT,
    OPENAI_DEFAULT_MODEL, OPENAI_DEFAULT_TEMPERATURE, OPENAI_LEGACY_CHAT_COMPLETIONS_ENDPOINT,
    OPENAI_TRANSLATION_SYSTEM_PROMPT, ZHIPU_DEFAULT_ENDPOINT, ZHIPU_DEFAULT_MODEL,
};
pub use quick_translate::{
    apply_quick_translate_outcome, apply_quick_translate_service_update,
    apply_quick_translate_start_error, apply_quick_translate_start_error_for_surface,
    apply_quick_translate_stream_chunk, begin_manual_quick_translate_service,
    begin_manual_quick_translate_service_for_surface, begin_quick_translate,
    begin_quick_translate_for_surface, begin_retry_quick_translate_service_for_surface,
    build_quick_translate_plan, build_quick_translate_plan_for_surface,
    resolve_auto_target_language, resolve_different_target_language, resolve_quick_query_language,
    run_quick_translate, run_quick_translate_service, NativeCustomStreamingQuickTranslateBackend,
    NativeOpenAiQuickTranslateBackend, NativeTraditionalHttpQuickTranslateBackend,
    QuickQueryLanguageResolution, QuickQueryMode, QuickTranslateBackend,
    QuickTranslateBackendError, QuickTranslateExecutionKind, QuickTranslateOutcome,
    QuickTranslatePlan, QuickTranslateService, QuickTranslateServiceOutcome,
    QuickTranslateServiceRequest, QuickTranslateServiceUpdate, QuickTranslateStartError,
    QuickTranslateStreamChunk, QuickTranslateStreamResult, QuickTranslateSurface,
};
pub use screen_capture::{
    CaptureInteraction, CaptureInteractionState, CapturePhase, CapturePoint, CaptureRect,
    DetectedWindow, WindowDetector,
};
pub use settings_migration::{
    migrate_settings_file, migrate_settings_json, migrate_settings_object, resolve_source_path,
    SettingsMigrationError,
};
pub use settings_storage::{
    default_settings_storage_path, load_settings_file, load_settings_json,
    load_settings_json_with_machine_id, save_settings_file, save_settings_json, SettingsLoadResult,
    SettingsStorageError,
};
pub use state::{
    resolve_result_action_intent, AppMode, ConnectionStatus, EasydictUiState, FloatingWindowState,
    GrammarCorrectionPreview, HotkeySetting, ImportedMdxDictionary, LocalDictionarySuggestion,
    LongDocumentState, Message, PreviewScenario, ResultActionIntent, ResultActionKind,
    ServiceProviderField, ServiceProviderSetting, SettingsLink, SettingsSection, SettingsState,
    TranslationResultPreview, TRANSLATION_LANGUAGE_IDS,
};
pub use theme::easydict_theme_tokens;
pub use traditional_http::{
    bing_credentials_expired, bing_host, bing_language_code, build_bing_translate_request_plan,
    build_caiyun_translation_request_plan, build_deepl_api_translation_request_plan,
    build_google_translation_request_plan, build_linguee_translation_request_plan,
    build_niutrans_translation_request_plan, build_traditional_http_translation_request_plan,
    build_volcano_translation_request_plan, caiyun_language_code, compute_volcano_authorization,
    deepl_api_error_from_status, deepl_language_code, from_bing_language_code,
    google_language_code, linguee_language_code, niutrans_error_from_code, niutrans_language_code,
    parse_bing_credentials_from_html, parse_bing_translation_response,
    parse_caiyun_translation_response, parse_deepl_api_translation_response,
    parse_google_translation_response, parse_linguee_translation_response,
    parse_niutrans_translation_response, parse_volcano_translation_response,
    traditional_http_config_for_service, traditional_http_error_from_status,
    translate_traditional_http_service, volcano_language_code,
    volcano_timestamps_from_epoch_seconds, BingCredentials, ReqwestTraditionalHttpClient,
    TraditionalHttpClient, TraditionalHttpRequestPlan, TraditionalHttpServiceConfig,
    TraditionalHttpServiceKind, VolcanoTimestamps, BING_CHINA_HOST, BING_GLOBAL_HOST,
    BING_MAX_TEXT_LENGTH_UTF16, BING_USER_AGENT, CAIYUN_TRANSLATE_ENDPOINT,
    DEEPL_FREE_API_ENDPOINT, DEEPL_PRO_API_ENDPOINT, GOOGLE_TRANSLATE_ENDPOINT,
    LINGUEE_TRANSLATE_ENDPOINT, NIUTRANS_MAX_TEXT_LENGTH_UTF16, NIUTRANS_TRANSLATE_ENDPOINT,
    VOLCANO_MAX_TEXT_LENGTH_UTF16, VOLCANO_QUERY_STRING, VOLCANO_TRANSLATE_ENDPOINT,
    VOLCANO_TRANSLATE_HOST,
};
pub use translation_cache::{
    displayable_phonetics, format_phonetic_text, is_youdao_word_query, merge_phonetics_into_result,
    phonetic_accent_display_label, phonetic_cache_entry_size_kb, phonetic_cache_key,
    plan_phonetic_enrichment, target_phonetics, translation_cache_entry_size_kb,
    translation_cache_key, Definition, Phonetic, PhoneticEnrichmentDecision,
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
    capture_overlay_view, fixed_window_view, fixed_window_view_with_settings, main_window_view,
    mini_window_view, mini_window_view_with_settings, pop_button_view, settings_view,
};
pub use window_options::{
    capture_overlay_window_options, fixed_window_options, main_window_options, mini_window_options,
    pop_button_window_options, settings_window_options,
};

use win_fluent::prelude::*;

pub use activation::{
    parse_startup_activation, resolve_startup_activation_disposition,
    startup_activation_task_for_args, StartupActivation, StartupActivationDisposition,
};
pub use credential_protection::{
    get_or_create_persisted_machine_id, is_protected_credential, protect_credential,
    protect_credential_legacy, protect_credential_with_scope, try_unprotect_credential,
    try_unprotect_credential_legacy, try_unprotect_credential_with_machine_id,
    unprotect_or_return_plaintext, unprotect_or_return_plaintext_with_machine_id,
    CredentialPlaintext, CredentialProtectionError, CredentialProtectionScope,
    MAX_NESTED_PROTECTED_VALUE_DEPTH,
};
pub use grammar_correction::{
    build_grammar_correction_plain_text_prompt, build_grammar_correction_user_prompt,
    grammar_correction_system_prompt, parse_grammar_correction, GrammarCorrectionResult,
    GRAMMAR_CORRECTION_SYSTEM_PROMPT, GRAMMAR_CORRECTION_SYSTEM_PROMPT_WITH_EXPLANATION,
};

pub struct EasydictApp {
    pub state: EasydictUiState,
}

impl Application for EasydictApp {
    type Message = Message;
    type Flags = EasydictUiState;

    fn new(flags: Self::Flags) -> (Self, Task<Self::Message>) {
        (
            Self { state: flags },
            startup_activation_task_for_args(std::env::args().skip(1)),
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
            "main" if self.state.settings_open => settings_view(&self.state.settings),
            "settings" => settings_view(&self.state.settings),
            "mini" => mini_window_view_with_settings(&self.state.mini, &self.state.settings),
            "fixed" => fixed_window_view_with_settings(&self.state.fixed, &self.state.settings),
            "capture-overlay" => capture_overlay_view(),
            "pop-button" => pop_button_view(),
            _ => main_window_view(&self.state),
        }
    }

    fn update(&mut self, message: Self::Message) -> Task<Self::Message> {
        if let Message::HotkeyTriggered(id) = &message {
            return self.hotkey_task(id);
        }

        if let Message::TrayCommand(id) = &message {
            return self.tray_task(id);
        }

        if let Message::ClipboardTextReceived(text) = message {
            return self.translate_clipboard_text(text);
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
                Ok(plan) => Task::batch(
                    plan.service_requests()
                        .into_iter()
                        .map(quick_translate_service_task),
                ),
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

        if let Some(task) = self.capture_overlay_interaction_task(&message) {
            return task;
        }

        if message == Message::BrowseFile
            && self.state.mode == AppMode::LongDocument
            && !self.state.settings_open
            && !self.state.long_document.is_translating
        {
            return Task::open_file_dialog(
                long_document_file_dialog_options(&self.state),
                Message::LongDocumentFileSelected,
            );
        }

        if message == Message::ImportMdxDictionary {
            return Task::open_file_dialog(
                mdx_dictionary_file_dialog_options(),
                Message::MdxDictionarySelected,
            );
        }

        if matches!(message, Message::Translate | Message::RetryLongDocument)
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

        if message == Message::QuickTranslate {
            return match quick_translate::begin_quick_translate(&mut self.state) {
                Ok(plan) => Task::batch(
                    plan.service_requests()
                        .into_iter()
                        .map(quick_translate_service_task),
                ),
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
                Ok(plan) => Task::batch(
                    plan.service_requests()
                        .into_iter()
                        .map(quick_translate_service_task),
                ),
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
                Ok(Some(request)) => return quick_translate_service_task(request),
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
                Ok(Some(request)) => return quick_translate_service_task(request),
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
            return Task::capture_text_insertion_target();
        }

        if let Message::ToggleResultExpanded(service_id) = &message {
            match quick_translate::begin_manual_quick_translate_service(&mut self.state, service_id)
            {
                Ok(Some(request)) => return quick_translate_service_task(request),
                Err(error) => {
                    quick_translate::apply_quick_translate_start_error(&mut self.state, error);
                    return Task::none();
                }
                Ok(None) => {}
            }
        }

        let task = match &message {
            Message::MinimizeWindow => Task::window(WindowCommand::MinimizeCurrent(true)),
            Message::ToggleMaximizeWindow => Task::window(WindowCommand::ToggleMaximizeCurrent),
            Message::CloseWindow => Task::window(WindowCommand::CloseCurrent),
            Message::ToggleShellContextMenu(true) => {
                Task::register_shell_verb(default_shell_verb())
            }
            Message::ToggleShellContextMenu(false) => {
                Task::unregister_shell_verb(default_shell_verb())
            }
            Message::InstallBrowserSupport => browser_registrar_task("install"),
            Message::UninstallBrowserSupport => browser_registrar_task("uninstall"),
            Message::OpenSettingsLink(link) => Task::open_url(link.url()),
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

        if !matches!(
            message,
            Message::ConfirmCapture | Message::CopyResult | Message::CancelCapture
        ) {
            self.state.apply(message);
        }
        task
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
                .chain(default_named_events().into_iter().filter_map(|event| {
                    let message = event.action.press()?;
                    Some(Subscription::named_event(
                        event.name,
                        event.auto_reset,
                        move |_| message.clone(),
                    ))
                }))
                .chain([Subscription::tray(Message::TrayCommand)]),
        )
    }

    fn tray_menu(&self) -> Option<TrayMenu<Self::Message>> {
        Some(default_tray_menu())
    }

    fn named_events(&self) -> Vec<NamedEventRegistration<Self::Message>> {
        default_named_events()
    }

    fn shell_verbs(&self) -> Vec<ShellVerb> {
        if self.state.settings.shell_context_menu {
            default_shell_verbs()
        } else {
            Vec::new()
        }
    }

    fn protocol_registrations(&self) -> Vec<ProtocolRegistration> {
        default_protocol_registrations()
    }
}

impl EasydictApp {
    fn hotkey_task(&mut self, id: &str) -> Task<Message> {
        match id {
            HOTKEY_SHOW_MAIN => Task::window(WindowCommand::Show(WindowId::new("main"))),
            HOTKEY_TRANSLATE_CLIPBOARD => Task::batch([
                Task::capture_text_insertion_target(),
                Task::read_clipboard_text(Message::ClipboardTextReceived),
            ]),
            HOTKEY_OCR_TRANSLATE => {
                self.state.pending_ocr_mode = Some(ocr::OcrMode::Translate);
                self.state.capture_interaction = CaptureInteractionState::new();
                self.state.capture_selection = None;
                self.state.ocr_status_text = "Select a region for OCR Translate".to_string();
                Task::window(WindowCommand::Show(WindowId::new("capture-overlay")))
            }
            HOTKEY_SILENT_OCR => {
                self.state.pending_ocr_mode = Some(ocr::OcrMode::SilentClipboard);
                self.state.capture_interaction = CaptureInteractionState::new();
                self.state.capture_selection = None;
                self.state.ocr_status_text = "Select a region for Silent OCR".to_string();
                Task::window(WindowCommand::Show(WindowId::new("capture-overlay")))
            }
            HOTKEY_SHOW_MINI => Task::batch([
                Task::capture_text_insertion_target(),
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
                    Ok(plan) => Task::batch(
                        plan.service_requests()
                            .into_iter()
                            .map(quick_translate_service_task),
                    ),
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
                Task::clipboard_text(text),
            ]),
        }
    }

    fn capture_overlay_action_task(&mut self, copy_requested: bool) -> Task<Message> {
        let mode =
            ocr::pending_mode_from_surface_action(self.state.pending_ocr_mode, copy_requested);
        self.state.pending_ocr_mode = Some(mode);
        self.state.ocr_status_text = format!("{} capture requested", mode.label());
        let request = screen_capture_request_from_selection(self.state.capture_selection.take());
        Task::capture_screen_region_with_request(request, move |capture| match capture {
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

    fn tray_task(&mut self, id: &str) -> Task<Message> {
        match id {
            TRAY_SHOW_MAIN => self.hotkey_task(HOTKEY_SHOW_MAIN),
            TRAY_TRANSLATE_CLIPBOARD => self.hotkey_task(HOTKEY_TRANSLATE_CLIPBOARD),
            TRAY_OCR_TRANSLATE => self.hotkey_task(HOTKEY_OCR_TRANSLATE),
            TRAY_SHOW_MINI => self.hotkey_task(HOTKEY_SHOW_MINI),
            TRAY_SHOW_FIXED => self.hotkey_task(HOTKEY_SHOW_FIXED),
            TRAY_BROWSER_INSTALL => browser_registrar_task("install"),
            TRAY_BROWSER_UNINSTALL => browser_registrar_task("uninstall"),
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
                .map(|id| Task::window(WindowCommand::Close(WindowId::new(id)))),
            ),
            _ => Task::none(),
        }
    }

    fn translate_clipboard_text(&mut self, text: Option<String>) -> Task<Message> {
        let text = text.unwrap_or_default();
        self.state.source_text = text;

        match quick_translate::begin_quick_translate(&mut self.state) {
            Ok(plan) => Task::batch(
                plan.service_requests()
                    .into_iter()
                    .map(quick_translate_service_task),
            ),
            Err(error) => {
                quick_translate::apply_quick_translate_start_error(&mut self.state, error);
                Task::none()
            }
        }
    }
}

fn screen_capture_request_from_selection(selection: Option<CaptureRect>) -> ScreenCaptureRequest {
    let Some(selection) = selection else {
        return ScreenCaptureRequest::virtual_desktop();
    };
    let selection = selection.normalized();
    if !selection.is_confirmable() {
        return ScreenCaptureRequest::virtual_desktop();
    }

    let Some(width) = selection
        .right
        .checked_sub(selection.left)
        .and_then(|width| u32::try_from(width).ok())
    else {
        return ScreenCaptureRequest::virtual_desktop();
    };
    let Some(height) = selection
        .bottom
        .checked_sub(selection.top)
        .and_then(|height| u32::try_from(height).ok())
    else {
        return ScreenCaptureRequest::virtual_desktop();
    };

    ScreenCaptureRequest::region(ScreenRect::new(
        selection.left,
        selection.top,
        width,
        height,
    ))
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
pub const TRAY_EXIT: &str = "exit";
pub const BROWSER_REGISTRAR_EXE: &str = "easydict_browser_registrar.exe";
pub const OCR_TRANSLATE_EVENT_NAME: &str = r"Local\Easydict-OcrTranslate";
pub const SHELL_OCR_TRANSLATE: &str = "EasydictOCR";
pub const PROTOCOL_EASYDICT: &str = "easydict";

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
    TrayMenu::new("Easydict")
        .item(
            TrayMenuItem::new(TRAY_SHOW_MAIN, "Show Easydict")
                .on_invoke(Message::TrayCommand(TRAY_SHOW_MAIN.to_string())),
        )
        .item(
            TrayMenuItem::new(TRAY_TRANSLATE_CLIPBOARD, "Translate Clipboard")
                .on_invoke(Message::TrayCommand(TRAY_TRANSLATE_CLIPBOARD.to_string())),
        )
        .item(
            TrayMenuItem::new(TRAY_OCR_TRANSLATE, "OCR Translate (Ctrl+Alt+S)")
                .on_invoke(Message::TrayCommand(TRAY_OCR_TRANSLATE.to_string())),
        )
        .item(
            TrayMenuItem::new(TRAY_SHOW_MINI, "Show Mini Window")
                .on_invoke(Message::TrayCommand(TRAY_SHOW_MINI.to_string())),
        )
        .item(
            TrayMenuItem::new(TRAY_SHOW_FIXED, "Show Fixed Window")
                .on_invoke(Message::TrayCommand(TRAY_SHOW_FIXED.to_string())),
        )
        .item(
            TrayMenuItem::new(TRAY_BROWSER_INSTALL, "Install Browser Support")
                .on_invoke(Message::TrayCommand(TRAY_BROWSER_INSTALL.to_string())),
        )
        .item(
            TrayMenuItem::new(TRAY_BROWSER_UNINSTALL, "Uninstall Browser Support")
                .on_invoke(Message::TrayCommand(TRAY_BROWSER_UNINSTALL.to_string())),
        )
        .item(
            TrayMenuItem::new(TRAY_EXIT, "Exit")
                .on_invoke(Message::TrayCommand(TRAY_EXIT.to_string())),
        )
}

pub fn default_named_events() -> Vec<NamedEventRegistration<Message>> {
    vec![NamedEventRegistration::new(OCR_TRANSLATE_EVENT_NAME)
        .on_signal(Message::HotkeyTriggered(HOTKEY_OCR_TRANSLATE.to_string()))]
}

pub fn default_shell_verbs() -> Vec<ShellVerb> {
    vec![default_shell_verb()]
}

pub fn default_shell_verb() -> ShellVerb {
    ShellVerb::new(SHELL_OCR_TRANSLATE, "OCR Translate")
        .directory_background(true)
        .argument("--ocr-translate")
}

pub fn default_protocol_registrations() -> Vec<ProtocolRegistration> {
    vec![ProtocolRegistration::new(PROTOCOL_EASYDICT, "URL:Easydict Protocol").argument("%1")]
}

pub fn browser_registrar_task(command: &'static str) -> Task<Message> {
    Task::run_bundled_executable(BROWSER_REGISTRAR_EXE, [command])
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
    Some(result_action_task(intent))
}

fn result_action_task(intent: ResultActionIntent) -> Task<Message> {
    match intent.kind {
        ResultActionKind::Copy => Task::clipboard_text(intent.text),
        ResultActionKind::Speak => Task::speak_text(intent.text, Some(intent.language)),
        ResultActionKind::Replace => Task::insert_text(intent.text),
    }
}

fn quick_translate_service_task(
    request: quick_translate::QuickTranslateServiceRequest,
) -> Task<Message> {
    if request.execution_kind == quick_translate::QuickTranslateExecutionKind::TranslateStream {
        Task::stream(
            quick_translate::run_quick_translate_streaming_service_with_current_app_dir(request),
        )
    } else {
        Task::perform(
            async move { quick_translate::run_quick_translate_service_with_current_app_dir(request) },
            Message::QuickTranslateServiceFinished,
        )
    }
}

fn long_document_task(request: long_document::LongDocumentServiceRequest) -> Task<Message> {
    Task::perform(
        async move { long_document::run_long_document_request_with_current_app_dir(request) },
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

fn long_document_file_dialog_options(state: &EasydictUiState) -> FileDialogOptions {
    let mut options = FileDialogOptions::new("Open document")
        .filter(FileDialogFilter::new(
            "Supported documents",
            ["*.pdf", "*.md", "*.markdown", "*.txt"],
        ))
        .filter(FileDialogFilter::new("PDF files", ["*.pdf"]))
        .filter(FileDialogFilter::new(
            "Markdown files",
            ["*.md", "*.markdown"],
        ))
        .filter(FileDialogFilter::new("Text files", ["*.txt"]));

    let output_folder = state.long_document.output_folder.trim();
    if !output_folder.is_empty() && !output_folder.starts_with('(') {
        options = options.initial_directory(output_folder);
    }

    options
}

fn mdx_dictionary_file_dialog_options() -> FileDialogOptions {
    FileDialogOptions::new("Import MDX dictionary")
        .filter(FileDialogFilter::new("MDX dictionaries", ["*.mdx"]))
        .filter(FileDialogFilter::new("All files", ["*.*"]))
}
