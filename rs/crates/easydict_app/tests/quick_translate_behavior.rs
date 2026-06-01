use easydict_app::compat_protocol::{
    GrammarCorrectParams, GrammarCorrectResultDto, MdxLookupEntry, MdxLookupParams,
    MdxLookupResult, SettingsSnapshot, TranslateParams, TranslationResultDto,
};
use easydict_app::{
    apply_local_dictionary_suggestion, apply_local_dictionary_suggestion_update,
    apply_quick_translate_outcome, apply_quick_translate_service_update,
    begin_local_dictionary_suggestions, begin_manual_quick_translate_service,
    begin_quick_translate, begin_quick_translate_for_surface,
    begin_retry_quick_translate_service_for_surface, build_quick_translate_plan,
    build_quick_translate_plan_for_surface, default_hotkeys, default_named_events,
    default_protocol_registrations, default_shell_verbs, default_tray_menu,
    local_dictionary_query_token, parse_startup_activation, resolve_quick_query_language,
    resolve_startup_activation_disposition, run_local_dictionary_suggestion_request,
    run_quick_translate, run_quick_translate_service, startup_activation_task_for_args,
    CustomStreamingHttpClient, CustomStreamingHttpRequestPlan, EasydictApp, EasydictUiState,
    LocalDictionarySuggestion, LocalDictionarySuggestionBackend, LocalDictionarySuggestionError,
    LocalDictionarySuggestionUpdate, Message, NativeCustomStreamingQuickTranslateBackend,
    NativeOpenAiQuickTranslateBackend, NativeTraditionalHttpQuickTranslateBackend,
    OpenAiExecutionError, OpenAiExecutionErrorCode, OpenAiHttpClient, OpenAiHttpRequestPlan,
    QuickQueryMode, QuickTranslateBackend, QuickTranslateBackendError, QuickTranslateExecutionKind,
    QuickTranslateOutcome, QuickTranslatePlan, QuickTranslateService, QuickTranslateServiceOutcome,
    QuickTranslateServiceRequest, QuickTranslateServiceUpdate, QuickTranslateStartError,
    QuickTranslateStreamChunk, QuickTranslateStreamResult, QuickTranslateSurface, ResultActionKind,
    SettingsLink, StartupActivation, StartupActivationDisposition, TraditionalHttpClient,
    TraditionalHttpRequestPlan, TraditionalHttpServiceKind, BROWSER_REGISTRAR_EXE,
    HOTKEY_OCR_TRANSLATE, HOTKEY_SHOW_FIXED, HOTKEY_SHOW_MAIN, HOTKEY_SHOW_MINI, HOTKEY_SILENT_OCR,
    HOTKEY_TOGGLE_FIXED, HOTKEY_TOGGLE_MINI, HOTKEY_TRANSLATE_CLIPBOARD,
    LOCAL_DICTIONARY_SUGGESTION_DELAY_MS, OCR_TRANSLATE_EVENT_NAME, PROTOCOL_EASYDICT,
    SHELL_OCR_TRANSLATE, TRAY_BROWSER_INSTALL, TRAY_BROWSER_UNINSTALL, TRAY_EXIT,
    TRAY_OCR_TRANSLATE, TRAY_SHOW_FIXED, TRAY_SHOW_MAIN, TRAY_SHOW_MINI, TRAY_TRANSLATE_CLIPBOARD,
};
use std::collections::VecDeque;
use std::fs;
use std::path::{Path, PathBuf};
use win_fluent::prelude::{
    Application, Hotkey, HotkeyKey, HotkeyModifier, PlatformCommand, PlatformEvent, ResultStatus,
    RuntimePlan, Subscription, SubscriptionKind, Task, WindowCommand, WindowId,
};

#[test]
fn plan_uses_trimmed_text_language_state_and_enabled_services() {
    let mut state = EasydictUiState::default();
    state.source_text = "  Hello from Rust  ".to_string();
    state.source_language = "auto".to_string();
    state.target_language = "zh-Hans".to_string();
    state.results[1].enabled_query = false;
    state.results[2].demoted = true;

    let plan = build_quick_translate_plan(&state, 42).expect("plan should be created");

    assert_eq!(plan.query_id, 42);
    assert_eq!(plan.text, "Hello from Rust");
    assert_eq!(plan.from, None);
    assert_eq!(plan.to.as_deref(), Some("zh-Hans"));
    assert_eq!(
        plan.language_resolution.effective_mode,
        QuickQueryMode::Translation
    );
    assert_eq!(
        plan.services
            .iter()
            .map(|service| service.id.as_str())
            .collect::<Vec<_>>(),
        ["google"]
    );

    let requests = plan.service_requests();
    assert_eq!(requests.len(), 1);
    assert_eq!(requests[0].query_id, 42);
    assert_eq!(requests[0].query_mode, QuickQueryMode::Translation);
    assert_eq!(
        requests[0].execution_kind,
        QuickTranslateExecutionKind::Translate
    );
    assert!(requests[0].grammar_params.is_none());
    assert_eq!(requests[0].params.text, "Hello from Rust");
    assert_eq!(
        requests[0].params.services.as_deref(),
        Some(&["google".to_string()][..])
    );
}

#[test]
fn floating_surface_plan_uses_own_text_languages_and_enabled_services() {
    let mut state = EasydictUiState::default();
    state.source_text = "Main text should not be used".to_string();
    state.results = vec![QuickTranslateResult::new("google", "Google Translate", false).into()];
    state.mini.text = "  Mini text  ".to_string();
    state.mini.source_language = "en".to_string();
    state.mini.target_language = "fr".to_string();
    state.mini.target_language_manually_selected = true;
    state.mini.results = vec![QuickTranslateResult::new("openai", "OpenAI", true)
        .streaming()
        .into()];

    let plan = build_quick_translate_plan_for_surface(&state, 77, QuickTranslateSurface::Mini)
        .expect("mini plan should be created");

    assert_eq!(plan.query_id, 77);
    assert_eq!(plan.text, "Mini text");
    assert_eq!(plan.from.as_deref(), Some("en"));
    assert_eq!(plan.to.as_deref(), Some("fr"));
    assert_eq!(
        plan.services
            .iter()
            .map(|service| service.id.as_str())
            .collect::<Vec<_>>(),
        ["openai"]
    );
    assert_eq!(
        plan.service_requests()[0].execution_kind,
        QuickTranslateExecutionKind::TranslateStream
    );
}

#[test]
fn auto_target_language_uses_first_second_language_rule() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.source_language = "en".to_string();
    state.target_language = "auto".to_string();
    state.settings.first_language = "zh".to_string();
    state.settings.second_language = "en".to_string();

    let plan = build_quick_translate_plan(&state, 1).expect("plan should be created");

    assert_eq!(plan.language_resolution.is_target_auto, true);
    assert_eq!(plan.language_resolution.effective_source_language, "en");
    assert_eq!(
        plan.language_resolution.effective_target_language,
        "zh-Hans"
    );
    assert_eq!(plan.from.as_deref(), Some("en"));
    assert_eq!(plan.to.as_deref(), Some("zh-Hans"));

    state.source_language = "zh-Hans".to_string();
    let plan = build_quick_translate_plan(&state, 2).expect("plan should be created");

    assert_eq!(plan.language_resolution.effective_target_language, "en");
    assert_eq!(plan.to.as_deref(), Some("en"));
}

#[test]
fn manual_target_selection_pauses_auto_target_routing() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.source_language = "en".to_string();

    state.apply(Message::TargetLanguageChanged("ja".to_string()));

    let plan = build_quick_translate_plan(&state, 1).expect("plan should be created");

    assert!(state.target_language_manually_selected);
    assert_eq!(plan.language_resolution.is_target_auto, false);
    assert_eq!(plan.language_resolution.effective_target_language, "ja");
    assert_eq!(plan.to.as_deref(), Some("ja"));
}

#[test]
fn language_preference_messages_update_auto_target_routing() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.source_language = "en".to_string();
    state.target_language = "fr".to_string();
    state.target_language_manually_selected = false;

    state.apply(Message::FirstLanguageChanged("ja".to_string()));
    state.apply(Message::SecondLanguageChanged("en".to_string()));
    state.apply(Message::UiLanguageChanged("fr-FR".to_string()));

    assert!(state.settings.unsaved_changes);
    assert_eq!(state.settings.first_language, "ja");
    assert_eq!(state.settings.second_language, "en");
    assert_eq!(state.settings.ui_language, "fr-FR");

    let plan = build_quick_translate_plan(&state, 1).expect("plan should use auto target");

    assert_eq!(plan.language_resolution.is_target_auto, true);
    assert_eq!(plan.to.as_deref(), Some("ja"));

    state.apply(Message::ToggleAutoSelectTargetLanguage(false));
    let plan = build_quick_translate_plan(&state, 2).expect("plan should use selected target");

    assert_eq!(plan.language_resolution.is_target_auto, false);
    assert_eq!(plan.to.as_deref(), Some("fr"));
}

#[test]
fn common_bcp47_language_codes_are_normalized_for_translation_params() {
    for (source, target, expected_from, expected_to) in [
        ("ar-SA", "da-DK", "ar", "da"),
        ("de-DE", "fr-FR", "de", "fr"),
        ("hi-IN", "id-ID", "hi", "id"),
        ("it-IT", "ms-MY", "it", "ms"),
        ("th-TH", "vi-VN", "th", "vi"),
        ("zh-CN", "zh-TW", "zh-Hans", "zh-Hant"),
    ] {
        let mut state = EasydictUiState::default();
        state.source_text = "Hello".to_string();
        state.source_language = source.to_string();
        state.target_language = target.to_string();
        state.target_language_manually_selected = true;

        let plan = build_quick_translate_plan(&state, 42).expect("plan should be created");

        assert_eq!(plan.from.as_deref(), Some(expected_from), "source={source}");
        assert_eq!(plan.to.as_deref(), Some(expected_to), "target={target}");
    }
}

#[test]
fn detected_language_labels_cover_common_picker_languages() {
    for (detected, expected_source) in [
        ("Detected: Arabic", "ar"),
        ("Detected: Danish", "da"),
        ("Detected: Hindi", "hi"),
        ("Detected: Indonesian", "id"),
        ("Detected: Italian", "it"),
        ("Detected: Malay", "ms"),
        ("Detected: Thai", "th"),
        ("Detected: Vietnamese", "vi"),
    ] {
        let mut state = EasydictUiState::default();
        state.source_text = "Hello".to_string();
        state.source_language = "auto".to_string();
        state.target_language = "en".to_string();
        state.target_language_manually_selected = true;
        state.detected_language = Some(detected.to_string());

        let plan = build_quick_translate_plan(&state, 43).expect("plan should be created");

        assert_eq!(
            plan.language_resolution.effective_source_language, expected_source,
            "detected={detected}"
        );
        assert_eq!(plan.from.as_deref(), Some(expected_source));
    }
}

#[test]
fn selected_languages_filter_translate_surface_language_pickers() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    let main = WindowId::new("main");
    let mini = WindowId::new("mini");
    let fixed = WindowId::new("fixed");

    let before = win_fluent_testkit::view_snapshot(&app.view(&main));
    assert!(before.contains("fr:\"French\""));

    app.state
        .apply(Message::ToggleSelectedLanguage("fr".to_string(), false));

    assert!(app.state.settings.unsaved_changes);
    assert!(!app
        .state
        .settings
        .selected_languages
        .iter()
        .any(|language| language == "fr"));

    for (name, snapshot) in [
        ("main", win_fluent_testkit::view_snapshot(&app.view(&main))),
        ("mini", win_fluent_testkit::view_snapshot(&app.view(&mini))),
        (
            "fixed",
            win_fluent_testkit::view_snapshot(&app.view(&fixed)),
        ),
    ] {
        assert!(
            !snapshot.contains("fr:\"French\""),
            "{name} picker should hide disabled French language"
        );
    }

    app.state.mode = easydict_app::AppMode::LongDocument;
    let long_document = win_fluent_testkit::view_snapshot(&app.view(&main));
    assert!(!long_document.contains("fr:\"French\""));

    app.state
        .apply(Message::ToggleSelectedLanguage("fr".to_string(), true));
    let restored = win_fluent_testkit::view_snapshot(&app.view(&main));
    assert!(restored.contains("fr:\"French\""));
}

#[test]
fn selected_language_changes_keep_first_second_visible_and_at_least_two_selected() {
    let mut state = EasydictUiState::default();
    state.settings.first_language = "zh-Hans".to_string();
    state.settings.second_language = "en".to_string();

    state.apply(Message::ToggleSelectedLanguage("zh-CN".to_string(), false));

    assert!(state.settings.unsaved_changes);
    assert!(!state
        .settings
        .selected_languages
        .iter()
        .any(|language| language == "zh-Hans"));
    assert!(state
        .settings
        .selected_languages
        .iter()
        .any(|language| language == &state.settings.first_language));
    assert_ne!(state.settings.first_language, "zh-Hans");
    assert_eq!(state.settings.second_language, "en");
    assert_ne!(
        state.settings.first_language,
        state.settings.second_language
    );

    state.settings.selected_languages = vec!["en".to_string(), "ja".to_string()];
    state.settings.first_language = "en".to_string();
    state.settings.second_language = "ja".to_string();
    state.settings.unsaved_changes = false;

    state.apply(Message::ToggleSelectedLanguage("ja".to_string(), false));

    assert_eq!(state.settings.selected_languages, ["en", "ja"]);
    assert_eq!(state.settings.first_language, "en");
    assert_eq!(state.settings.second_language, "ja");
    assert!(!state.settings.unsaved_changes);
}

#[test]
fn streaming_capable_service_uses_translate_stream_execution() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.results = vec![
        QuickTranslateResult::new("google", "Google Translate", false),
        QuickTranslateResult::new("openai", "OpenAI", true).streaming(),
    ]
    .into_iter()
    .map(Into::into)
    .collect();

    let plan = build_quick_translate_plan(&state, 12).expect("plan should be created");
    let requests = plan.service_requests();

    assert_eq!(
        requests[0].execution_kind,
        QuickTranslateExecutionKind::Translate
    );
    assert_eq!(
        requests[1].execution_kind,
        QuickTranslateExecutionKind::TranslateStream
    );

    let mut backend = RecordingBackend::with_translation_stream_and_grammar_responses(
        [Ok(dto(
            "google",
            "Google Translate",
            "translated hello",
            Some("en"),
            Some(121),
        ))],
        [Ok(QuickTranslateStreamResult {
            result: dto("openai", "OpenAI", "streamed hello", Some("en"), Some(80)),
            chunks: vec!["streamed ".to_string(), "hello".to_string()],
        })],
        [],
    );

    let outcome = run_quick_translate(&mut backend, &plan);

    assert_eq!(backend.calls.len(), 1);
    assert_eq!(backend.stream_calls.len(), 1);
    assert_eq!(
        backend.stream_calls[0].services.as_deref(),
        Some(&["openai".to_string()][..])
    );
    assert_eq!(
        outcome.results[1].streamed_chunks,
        vec!["streamed ".to_string(), "hello".to_string()]
    );
    assert_eq!(
        outcome.results[1].result.as_ref().unwrap().translated_text,
        "streamed hello"
    );
}

#[test]
fn same_language_routes_to_grammar_when_a_capable_service_is_enabled() {
    let mut state = EasydictUiState::default();
    state.source_text = "I has a apple".to_string();
    state.source_language = "en".to_string();
    state.target_language = "en".to_string();
    state.target_language_manually_selected = true;
    state.results = vec![
        QuickTranslateResult::new("google", "Google Translate", false),
        QuickTranslateResult::new("openai", "OpenAI", true),
    ]
    .into_iter()
    .map(Into::into)
    .collect();

    let plan = build_quick_translate_plan(&state, 1).expect("plan should be created");

    assert_eq!(
        plan.language_resolution.effective_mode,
        QuickQueryMode::GrammarCorrection
    );
    assert!(plan.language_resolution.grammar_correction_requested);
    assert!(!plan.language_resolution.grammar_correction_fallback);
    assert_eq!(plan.from.as_deref(), Some("en"));
    assert_eq!(plan.to.as_deref(), Some("en"));

    let requests = plan.service_requests();
    assert_eq!(requests[0].query_mode, QuickQueryMode::Translation);
    assert_eq!(
        requests[0].execution_kind,
        QuickTranslateExecutionKind::Translate
    );
    assert!(requests[0].grammar_params.is_none());
    assert_eq!(requests[1].query_mode, QuickQueryMode::GrammarCorrection);
    assert_eq!(
        requests[1].execution_kind,
        QuickTranslateExecutionKind::GrammarCorrection
    );
    let grammar_params = requests[1]
        .grammar_params
        .as_ref()
        .expect("grammar-capable service should have grammar params");
    assert_eq!(grammar_params.text, "I has a apple");
    assert_eq!(grammar_params.language.as_deref(), Some("en"));
    assert_eq!(
        grammar_params.services.as_deref(),
        Some(&["openai".to_string()][..])
    );
}

#[test]
fn same_language_without_grammar_falls_back_to_different_translation_target() {
    let resolution = resolve_quick_query_language("en", "en", "en", false, "zh", "en");

    assert_eq!(resolution.effective_mode, QuickQueryMode::Translation);
    assert_eq!(resolution.effective_target_language, "zh-Hans");
    assert!(resolution.grammar_correction_requested);
    assert!(resolution.grammar_correction_fallback);
}

#[test]
fn plan_rejects_empty_text_and_all_disabled_services() {
    let mut state = EasydictUiState::default();
    state.source_text = "   ".to_string();

    let error = build_quick_translate_plan(&state, 1).unwrap_err();
    assert_eq!(error, QuickTranslateStartError::EmptyText);

    state.source_text = "Hello".to_string();
    for result in &mut state.results {
        result.enabled_query = false;
    }

    let error = build_quick_translate_plan(&state, 1).unwrap_err();
    assert_eq!(error, QuickTranslateStartError::NoEnabledServices);
}

#[test]
fn begin_translate_marks_enabled_services_loading_and_tracks_active_query() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();

    let plan = begin_quick_translate(&mut state).expect("translate should begin");

    assert_eq!(plan.query_id, 1);
    assert_eq!(state.next_query_id, 2);
    assert_eq!(state.active_query_id, Some(1));
    assert_eq!(state.active_query_service_count, 3);
    assert_eq!(state.active_query_success_count, 0);
    assert!(state.is_translating);
    assert_eq!(state.status_text, "Translating");
    assert_eq!(state.services_completed, 0);
    assert!(state.detected_language.is_none());
    assert!(state
        .results
        .iter()
        .all(|result| result.status == ResultStatus::Loading));
    assert!(state.results.iter().all(|result| result.body.is_empty()));
}

#[test]
fn run_translate_calls_backend_once_per_service_with_single_service_params() {
    let plan = QuickTranslatePlan {
        query_id: 7,
        text: "Hello".to_string(),
        from: None,
        to: Some("zh-Hans".to_string()),
        settings: SettingsSnapshot::default(),
        services: vec![
            QuickTranslateService {
                id: "google".to_string(),
                name: "Google Translate".to_string(),
                enabled_query: true,
                grammar_capable: false,
                streaming_capable: false,
            },
            QuickTranslateService {
                id: "bing".to_string(),
                name: "Bing Translate".to_string(),
                enabled_query: true,
                grammar_capable: false,
                streaming_capable: false,
            },
        ],
        language_resolution: translation_resolution("auto", "zh-Hans"),
    };
    let mut backend = RecordingBackend::with_responses([
        Ok(dto(
            "google",
            "Google Translate",
            "translated hello",
            Some("en"),
            Some(121),
        )),
        Err(QuickTranslateBackendError::new("network unavailable")),
    ]);

    let outcome = run_quick_translate(&mut backend, &plan);

    assert_eq!(backend.calls.len(), 2);
    assert_eq!(
        backend.calls[0].services.as_deref(),
        Some(&["google".to_string()][..])
    );
    assert_eq!(
        backend.calls[1].services.as_deref(),
        Some(&["bing".to_string()][..])
    );
    assert_eq!(backend.calls[0].text, "Hello");
    assert_eq!(backend.calls[0].from, None);
    assert_eq!(backend.calls[0].to.as_deref(), Some("zh-Hans"));
    assert_eq!(outcome.query_id, 7);
    assert!(outcome.results[0].result.is_ok());
    assert_eq!(
        outcome.results[1].result.as_ref().unwrap_err().message,
        "network unavailable"
    );
}

#[test]
fn grammar_mode_calls_grammar_for_capable_services_and_translation_for_others() {
    let plan = QuickTranslatePlan {
        query_id: 8,
        text: "I has a apple".to_string(),
        from: Some("en".to_string()),
        to: Some("en".to_string()),
        settings: SettingsSnapshot::default(),
        services: vec![
            QuickTranslateService {
                id: "google".to_string(),
                name: "Google Translate".to_string(),
                enabled_query: true,
                grammar_capable: false,
                streaming_capable: false,
            },
            QuickTranslateService {
                id: "openai".to_string(),
                name: "OpenAI".to_string(),
                enabled_query: true,
                grammar_capable: true,
                streaming_capable: true,
            },
        ],
        language_resolution: grammar_resolution("en"),
    };
    let mut backend = RecordingBackend::with_translation_and_grammar_responses(
        [Ok(dto(
            "google",
            "Google Translate",
            "translated fallback",
            Some("en"),
            Some(121),
        ))],
        [Ok(grammar_dto(
            "openai",
            "OpenAI",
            "I has a apple",
            "I have an apple",
            Some("en"),
            Some(80),
        ))],
    );

    let outcome = run_quick_translate(&mut backend, &plan);

    assert_eq!(backend.calls.len(), 1);
    assert_eq!(
        backend.calls[0].services.as_deref(),
        Some(&["google".to_string()][..])
    );
    assert_eq!(backend.grammar_calls.len(), 1);
    assert_eq!(backend.grammar_calls[0].text, "I has a apple");
    assert_eq!(backend.grammar_calls[0].language.as_deref(), Some("en"));
    assert_eq!(
        backend.grammar_calls[0].services.as_deref(),
        Some(&["openai".to_string()][..])
    );
    assert_eq!(outcome.query_id, 8);
    assert_eq!(
        outcome.results[0].result.as_ref().unwrap().translated_text,
        "translated fallback"
    );
    assert_eq!(
        outcome.results[1].result.as_ref().unwrap().translated_text,
        "I have an apple"
    );
    let grammar_result = outcome.results[1]
        .grammar_result
        .as_ref()
        .expect("grammar-capable result should retain structured correction");
    assert_eq!(grammar_result.original_text, "I has a apple");
    assert_eq!(grammar_result.corrected_text, "I have an apple");
    assert_eq!(
        grammar_result.explanation.as_deref(),
        Some("grammar explanation")
    );
    assert!(grammar_result.has_corrections);
}

#[test]
fn native_openai_quick_translate_stream_uses_settings_and_parses_chunks() {
    let request = QuickTranslateServiceRequest {
        query_id: 21,
        service: QuickTranslateService {
            id: "openai".to_string(),
            name: "OpenAI".to_string(),
            enabled_query: true,
            grammar_capable: true,
            streaming_capable: true,
        },
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("fr".to_string()),
            services: Some(vec!["openai".to_string()]),
        },
        grammar_params: None,
        settings: openai_settings(),
    };
    let mut backend =
        NativeOpenAiQuickTranslateBackend::new(RecordingOpenAiHttpClient::with_responses([Ok(
            chat_completion_sse(&["Bonjour ", "le monde"]),
        )]));

    let update = run_quick_translate_service(&mut backend, &request);

    assert_eq!(update.query_id, 21);
    assert_eq!(
        update.outcome.streamed_chunks,
        vec!["Bonjour ".to_string(), "le monde".to_string()]
    );
    let result = update.outcome.result.expect("native stream should succeed");
    assert_eq!(result.translated_text, "Bonjour le monde");
    assert_eq!(result.service_id.as_deref(), Some("openai"));
    assert_eq!(result.service_name.as_deref(), Some("OpenAI"));
    assert_eq!(result.detected_language.as_deref(), Some("en"));

    let requests = &backend.http_client().requests;
    assert_eq!(requests.len(), 1);
    assert_eq!(
        requests[0].endpoint,
        "https://api.openai.com/v1/chat/completions"
    );
    assert_eq!(
        requests[0].headers,
        vec![("Authorization".to_string(), "Bearer sk-native".to_string())]
    );
    assert_eq!(requests[0].body["model"], "gpt-4o-mini");
    assert_eq!(requests[0].body["stream"], true);
}

#[test]
fn native_openai_quick_translate_grammar_keeps_structured_result() {
    let request = QuickTranslateServiceRequest {
        query_id: 22,
        service: QuickTranslateService {
            id: "openai".to_string(),
            name: "OpenAI".to_string(),
            enabled_query: true,
            grammar_capable: true,
            streaming_capable: true,
        },
        query_mode: QuickQueryMode::GrammarCorrection,
        execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
        params: TranslateParams {
            text: "He go home.".to_string(),
            from: Some("en".to_string()),
            to: Some("en".to_string()),
            services: Some(vec!["openai".to_string()]),
        },
        grammar_params: Some(GrammarCorrectParams {
            text: "He go home.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["openai".to_string()]),
            include_explanations: true,
        }),
        settings: openai_settings(),
    };
    let mut backend =
        NativeOpenAiQuickTranslateBackend::new(RecordingOpenAiHttpClient::with_responses([Ok(
            chat_completion_sse(&["[CORRECTED]He goes home.[/CORRECTED]\n\
                 [EXPLANATION]Subject-verb agreement.[/EXPLANATION]"]),
        )]));

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("native grammar should succeed");
    assert_eq!(result.translated_text, "He goes home.");
    assert_eq!(result.service_id.as_deref(), Some("openai"));
    let grammar_result = update
        .outcome
        .grammar_result
        .expect("grammar preview should be retained");
    assert_eq!(grammar_result.original_text, "He go home.");
    assert_eq!(grammar_result.corrected_text, "He goes home.");
    assert_eq!(
        grammar_result.explanation.as_deref(),
        Some("Subject-verb agreement.")
    );
    assert!(grammar_result.has_corrections);
    assert_eq!(backend.http_client().requests.len(), 1);
}

#[test]
fn native_openai_quick_translate_supports_builtin_direct_user_key() {
    let request = QuickTranslateServiceRequest {
        query_id: 24,
        service: QuickTranslateService {
            id: "builtin".to_string(),
            name: "Built-in AI".to_string(),
            enabled_query: true,
            grammar_capable: true,
            streaming_capable: true,
        },
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["builtin".to_string()]),
        },
        grammar_params: None,
        settings: builtin_direct_settings(),
    };
    let mut backend = NativeOpenAiQuickTranslateBackend::new(
        RecordingOpenAiHttpClient::with_responses([Ok(chat_completion_sse(&["你好"]))]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("native Built-in AI direct mode should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("builtin"));
    assert_eq!(result.service_name.as_deref(), Some("Built-in AI"));

    let requests = &backend.http_client().requests;
    assert_eq!(requests.len(), 1);
    assert_eq!(
        requests[0].endpoint,
        "https://api.groq.com/openai/v1/chat/completions"
    );
    assert_eq!(
        requests[0].headers,
        vec![(
            "Authorization".to_string(),
            "Bearer builtin-user-key".to_string()
        )]
    );
    assert_eq!(requests[0].body["model"], "llama-3.1-8b-instant");
}

#[test]
fn native_openai_quick_translate_rejects_unsupported_service_without_http_request() {
    let request = QuickTranslateServiceRequest {
        query_id: 23,
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
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("fr".to_string()),
            services: Some(vec!["google".to_string()]),
        },
        grammar_params: None,
        settings: openai_settings(),
    };
    let mut backend = NativeOpenAiQuickTranslateBackend::new(RecordingOpenAiHttpClient::default());

    let update = run_quick_translate_service(&mut backend, &request);

    let error = update
        .outcome
        .result
        .expect_err("unsupported service should fail locally");
    assert!(error
        .message
        .contains("not handled by the native OpenAI-compatible backend"));
    assert!(backend.http_client().requests.is_empty());
}

#[test]
fn native_custom_streaming_quick_translate_supports_gemini_stream_and_grammar() {
    let stream_request = QuickTranslateServiceRequest {
        query_id: 25,
        service: quick_service("gemini", "Gemini", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("fr".to_string()),
            services: Some(vec!["gemini".to_string()]),
        },
        grammar_params: None,
        settings: gemini_settings(),
    };
    let grammar_request = QuickTranslateServiceRequest {
        query_id: 26,
        service: quick_service("gemini", "Gemini", true, true),
        query_mode: QuickQueryMode::GrammarCorrection,
        execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
        params: TranslateParams {
            text: "He go home.".to_string(),
            from: Some("en".to_string()),
            to: Some("en".to_string()),
            services: Some(vec!["gemini".to_string()]),
        },
        grammar_params: Some(GrammarCorrectParams {
            text: "He go home.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["gemini".to_string()]),
            include_explanations: true,
        }),
        settings: gemini_settings(),
    };
    let mut backend = NativeCustomStreamingQuickTranslateBackend::new(
        RecordingCustomStreamingHttpClient::with_responses([
            Ok(gemini_stream_sse(&["Bonjour ", "le monde"])),
            Ok(gemini_stream_sse(&[
                "[CORRECTED]He goes home.[/CORRECTED]\n[EXPLANATION]Subject-verb agreement.[/EXPLANATION]",
            ])),
        ]),
    );

    let stream_update = run_quick_translate_service(&mut backend, &stream_request);
    let grammar_update = run_quick_translate_service(&mut backend, &grammar_request);

    assert_eq!(
        stream_update.outcome.streamed_chunks,
        vec!["Bonjour ".to_string(), "le monde".to_string()]
    );
    assert_eq!(
        stream_update
            .outcome
            .result
            .as_ref()
            .unwrap()
            .translated_text,
        "Bonjour le monde"
    );
    let grammar = grammar_update
        .outcome
        .grammar_result
        .expect("Gemini grammar preview should be retained");
    assert_eq!(grammar.corrected_text, "He goes home.");
    assert_eq!(
        grammar.explanation.as_deref(),
        Some("Subject-verb agreement.")
    );
    assert_eq!(backend.http_client().requests.len(), 2);
    assert!(backend.http_client().requests[0]
        .endpoint
        .contains("models/gemini-2.5-flash:streamGenerateContent"));
}

#[test]
fn native_custom_streaming_quick_translate_supports_doubao_stream() {
    let request = QuickTranslateServiceRequest {
        query_id: 27,
        service: quick_service("doubao", "Doubao", false, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["doubao".to_string()]),
        },
        grammar_params: None,
        settings: doubao_settings(),
    };
    let mut backend = NativeCustomStreamingQuickTranslateBackend::new(
        RecordingCustomStreamingHttpClient::with_responses([Ok(doubao_stream_sse(&[
            "'你", "好'",
        ]))]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update.outcome.result.expect("native Doubao should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("doubao"));
    assert_eq!(
        update.outcome.streamed_chunks,
        vec!["'你".to_string(), "好'".to_string()]
    );
    let plan = &backend.http_client().requests[0];
    assert_eq!(
        plan.headers,
        vec![("Authorization".to_string(), "Bearer doubao-key".to_string())]
    );
    assert_eq!(
        plan.body["input"][0]["content"][0]["translation_options"]["target_language"],
        "zh"
    );
}

#[test]
fn native_traditional_http_quick_translate_supports_google() {
    let request = QuickTranslateServiceRequest {
        query_id: 28,
        service: quick_service("google", "Google Translate", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: None,
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["google".to_string()]),
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };
    let mut backend = NativeTraditionalHttpQuickTranslateBackend::new(
        RecordingTraditionalHttpClient::with_responses([Ok(
            r#"{"sentences":[{"trans":"你好"}],"src":"en"}"#.to_string(),
        )]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("native Google Translate should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("google"));
    assert_eq!(result.service_name.as_deref(), Some("Google Translate"));
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    let plan = &backend.http_client().requests[0];
    assert_eq!(plan.method, "GET");
    assert!(plan.endpoint.contains("client=gtx"));
    assert!(plan.endpoint.contains("sl=auto"));
    assert!(plan.endpoint.contains("tl=zh-CN"));
}

#[test]
fn native_traditional_http_quick_translate_supports_caiyun_deepl_api_and_niutrans() {
    let mut backend = NativeTraditionalHttpQuickTranslateBackend::new(
        RecordingTraditionalHttpClient::with_responses([
            Ok(r#"{"target":["你好"]}"#.to_string()),
            Ok(
                r#"{"translations":[{"detected_source_language":"EN","text":"Salut"}]}"#
                    .to_string(),
            ),
            Ok(r#"{"tgt_text":"Bonjour"}"#.to_string()),
            Ok(
                r#"{"TranslationList":[{"Translation":"你好","DetectedSourceLanguage":"en"}],"ResponseMetadata":{}}"#
                    .to_string(),
            ),
        ]),
    );

    let caiyun_request = QuickTranslateServiceRequest {
        query_id: 29,
        service: quick_service("caiyun", "Caiyun", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["caiyun".to_string()]),
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            caiyun_token: Some("caiyun-token".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    let caiyun_update = run_quick_translate_service(&mut backend, &caiyun_request);
    let caiyun = caiyun_update
        .outcome
        .result
        .expect("native Caiyun should succeed");
    assert_eq!(caiyun.translated_text, "你好");
    assert_eq!(caiyun.service_id.as_deref(), Some("caiyun"));
    let caiyun_plan = &backend.http_client().requests[0];
    assert_eq!(caiyun_plan.method, "POST");
    assert_eq!(
        caiyun_plan.headers[1],
        (
            "X-Authorization".to_string(),
            "token caiyun-token".to_string()
        )
    );

    let deepl_request = QuickTranslateServiceRequest {
        query_id: 30,
        service: quick_service("deepl", "DeepL", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("fr".to_string()),
            services: Some(vec!["deepl".to_string()]),
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            deep_l_api_key: Some("deepl-key".to_string()),
            deep_l_use_free_api: Some(false),
            deep_l_use_quality_optimized: Some(false),
            ..SettingsSnapshot::default()
        },
    };

    let deepl_update = run_quick_translate_service(&mut backend, &deepl_request);
    let deepl = deepl_update
        .outcome
        .result
        .expect("native DeepL API should succeed");
    assert_eq!(deepl.translated_text, "Salut");
    assert_eq!(deepl.service_id.as_deref(), Some("deepl"));
    assert_eq!(deepl.detected_language.as_deref(), Some("en"));
    let deepl_plan = &backend.http_client().requests[1];
    assert_eq!(deepl_plan.method, "POST");
    assert!(deepl_plan.endpoint.contains("api.deepl.com/v2/translate"));
    assert!(deepl_plan
        .body
        .as_deref()
        .unwrap()
        .contains("target_lang=FR"));

    let niutrans_request = QuickTranslateServiceRequest {
        query_id: 31,
        service: quick_service("niutrans", "NiuTrans", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("fr".to_string()),
            services: Some(vec!["niutrans".to_string()]),
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            niu_trans_api_key: Some("niu-key".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    let niutrans_update = run_quick_translate_service(&mut backend, &niutrans_request);
    let niutrans = niutrans_update
        .outcome
        .result
        .expect("native NiuTrans should succeed");
    assert_eq!(niutrans.translated_text, "Bonjour");
    assert_eq!(niutrans.service_id.as_deref(), Some("niutrans"));
    assert_eq!(
        niutrans_update.outcome.streamed_chunks,
        vec!["Bonjour".to_string()]
    );
    let niutrans_body: serde_json::Value =
        serde_json::from_str(backend.http_client().requests[2].body.as_deref().unwrap()).unwrap();
    assert_eq!(niutrans_body["apikey"], "niu-key");
    assert_eq!(niutrans_body["from"], "en");
    assert_eq!(niutrans_body["to"], "fr");

    let volcano_request = QuickTranslateServiceRequest {
        query_id: 32,
        service: quick_service("volcano", "Volcano", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["volcano".to_string()]),
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            volcano_access_key_id: Some("volcano-akid".to_string()),
            volcano_secret_access_key: Some("volcano-secret".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    let volcano_update = run_quick_translate_service(&mut backend, &volcano_request);
    let volcano = volcano_update
        .outcome
        .result
        .expect("native Volcano should succeed");
    assert_eq!(volcano.translated_text, "你好");
    assert_eq!(volcano.service_id.as_deref(), Some("volcano"));
    assert_eq!(volcano.detected_language.as_deref(), Some("en"));
    let volcano_plan = &backend.http_client().requests[3];
    assert_eq!(volcano_plan.method, "POST");
    assert_eq!(
        volcano_plan.service_kind,
        TraditionalHttpServiceKind::Volcano
    );
    assert!(volcano_plan
        .headers
        .iter()
        .any(|(key, value)| key == "Authorization"
            && value.starts_with("HMAC-SHA256 Credential=volcano-akid/")));
}

#[test]
fn apply_grammar_result_hydrates_structured_preview_body_and_metadata() {
    let mut state = EasydictUiState::default();
    state.source_text = "I has a apple".to_string();
    state.source_language = "en".to_string();
    state.target_language = "en".to_string();
    state.target_language_manually_selected = true;
    state.results = vec![QuickTranslateResult::new("openai", "OpenAI", true)]
        .into_iter()
        .map(Into::into)
        .collect();
    let plan = begin_quick_translate(&mut state).expect("grammar query should begin");

    assert!(apply_quick_translate_service_update(
        &mut state,
        QuickTranslateServiceUpdate {
            query_id: plan.query_id,
            outcome: QuickTranslateServiceOutcome {
                service: plan.services[0].clone(),
                grammar_result: Some(easydict_app::GrammarCorrectionPreview::new(
                    "I has a apple",
                    "I have an apple",
                    Some("Use have with I and an before apple.".to_string()),
                    true,
                )),
                streamed_chunks: Vec::new(),
                result: Ok(dto(
                    "openai",
                    "OpenAI",
                    "I have an apple",
                    Some("en"),
                    Some(80),
                )),
            },
        }
    ));

    let result = &state.results[0];
    assert_eq!(result.query_mode, QuickQueryMode::GrammarCorrection);
    assert_eq!(result.body, "I have an apple");
    assert_eq!(
        result
            .grammar_result
            .as_ref()
            .and_then(|grammar| grammar.explanation.as_deref()),
        Some("Use have with I and an before apple.")
    );

    let item = result.to_result_item();
    assert!(item.body.contains("Corrected\nI have an apple"));
    assert!(item
        .body
        .contains("Explanation\nUse have with I and an before apple."));
    assert_eq!(item.metadata.as_deref(), Some("Grammar - 80ms"));
}

#[test]
fn stream_chunks_update_service_body_without_completing_query() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.results = vec![QuickTranslateResult::new("openai", "OpenAI", true)
        .streaming()
        .into()];
    let plan = begin_quick_translate(&mut state).expect("translate should begin");

    assert!(easydict_app::apply_quick_translate_stream_chunk(
        &mut state,
        QuickTranslateStreamChunk {
            query_id: plan.query_id,
            service: plan.services[0].clone(),
            text: "streamed ".to_string(),
        }
    ));
    assert!(easydict_app::apply_quick_translate_stream_chunk(
        &mut state,
        QuickTranslateStreamChunk {
            query_id: plan.query_id,
            service: plan.services[0].clone(),
            text: "hello".to_string(),
        }
    ));

    assert!(state.is_translating);
    assert_eq!(state.active_query_id, Some(plan.query_id));
    assert_eq!(state.services_completed, 0);
    assert_eq!(state.results[0].status, ResultStatus::Streaming);
    assert_eq!(state.results[0].body, "streamed hello");
    assert_eq!(
        state.results[0].streamed_chunks,
        vec!["streamed ".to_string(), "hello".to_string()]
    );
}

#[test]
fn manual_pending_service_query_builds_single_request_without_enabling_future_auto_queries() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.results = vec![
        QuickTranslateResult::new("google", "Google Translate", false).into(),
        easydict_app::TranslationResultPreview::new("openai", "OpenAI", "")
            .grammar_capable(true)
            .streaming_capable(true)
            .manual_query(),
    ];

    let request = begin_manual_quick_translate_service(&mut state, "openai")
        .expect("manual query should be valid")
        .expect("pending service should start a request");

    assert_eq!(request.query_id, 1);
    assert_eq!(request.service.id, "openai");
    assert!(!request.service.enabled_query);
    assert_eq!(
        request.execution_kind,
        QuickTranslateExecutionKind::TranslateStream
    );
    assert_eq!(
        request.params.services.as_deref(),
        Some(&["openai".to_string()][..])
    );
    assert_eq!(state.active_query_id, Some(1));
    assert_eq!(state.next_query_id, 2);
    assert_eq!(state.active_query_service_count, 1);
    assert_eq!(state.services_completed, 0);
    assert_eq!(state.results[0].status, ResultStatus::Ready);
    assert_eq!(state.results[1].status, ResultStatus::Loading);
    assert!(!state.results[1].enabled_query);
    assert!(state.results[1].has_queried);
    assert!(state.results[1].expanded);

    assert!(apply_quick_translate_service_update(
        &mut state,
        QuickTranslateServiceUpdate {
            query_id: request.query_id,
            outcome: QuickTranslateServiceOutcome {
                service: request.service,
                grammar_result: None,
                streamed_chunks: vec!["manual ".to_string(), "result".to_string()],
                result: Ok(dto("openai", "OpenAI", "", Some("en"), Some(91),)),
            },
        }
    ));

    assert_eq!(state.active_query_id, None);
    assert!(!state.results[1].enabled_query);
    assert!(state.results[1].has_queried);
    assert_eq!(state.results[1].status, ResultStatus::Ready);
    assert_eq!(state.results[1].body, "manual result");
    assert_eq!(state.results[1].latency_ms, Some(91));
}

#[test]
fn retry_service_query_builds_single_request_for_existing_result() {
    let mut state = EasydictUiState::default();
    state.source_text = "Retry me".to_string();
    state.results = vec![
        QuickTranslateResult::new("google", "Google Translate", false).into(),
        QuickTranslateResult::new("openai", "OpenAI", true)
            .streaming()
            .into(),
    ];
    state.results[1].status = ResultStatus::Error;
    state.results[1].body = "request timed out".to_string();

    let request = begin_retry_quick_translate_service_for_surface(
        &mut state,
        QuickTranslateSurface::Main,
        "openai",
    )
    .expect("retry should be valid")
    .expect("existing service should start a request");

    assert_eq!(request.query_id, 1);
    assert_eq!(request.service.id, "openai");
    assert!(request.service.enabled_query);
    assert_eq!(
        request.execution_kind,
        QuickTranslateExecutionKind::TranslateStream
    );
    assert_eq!(
        request.params.services.as_deref(),
        Some(&["openai".to_string()][..])
    );
    assert_eq!(state.active_query_service_count, 1);
    assert_eq!(state.results[0].status, ResultStatus::Ready);
    assert_eq!(state.results[1].status, ResultStatus::Loading);
    assert!(state.results[1].body.is_empty());
}

#[test]
fn floating_service_update_hydrates_matching_window_without_touching_main_results() {
    let mut state = EasydictUiState::default();
    state.results = vec![QuickTranslateResult::new("google", "Google Translate", false).into()];
    state.results[0].body = "main result stays put".to_string();
    state.mini.text = "Hello from mini".to_string();
    state.mini.results = vec![QuickTranslateResult::new("openai", "OpenAI", true).into()];

    let plan = begin_quick_translate_for_surface(&mut state, QuickTranslateSurface::Mini)
        .expect("mini query should begin");

    assert_eq!(state.active_query_id, None);
    assert!(!state.is_translating);
    assert_eq!(state.mini.active_query_id, Some(plan.query_id));
    assert!(state.mini.is_translating);
    assert_eq!(state.mini.results[0].status, ResultStatus::Loading);

    assert!(apply_quick_translate_service_update(
        &mut state,
        QuickTranslateServiceUpdate {
            query_id: plan.query_id,
            outcome: QuickTranslateServiceOutcome {
                service: plan.services[0].clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Ok(dto(
                    "openai",
                    "OpenAI",
                    "bonjour mini",
                    Some("en"),
                    Some(72),
                )),
            },
        }
    ));

    assert_eq!(state.results[0].body, "main result stays put");
    assert_eq!(state.mini.active_query_id, None);
    assert!(!state.mini.is_translating);
    assert_eq!(state.mini.status_text, "Connected");
    assert_eq!(state.mini.services_completed, 1);
    assert_eq!(
        state.mini.detected_language.as_deref(),
        Some("Detected: English")
    );
    assert_eq!(state.mini.results[0].body, "bonjour mini");
    assert_eq!(state.mini.results[0].latency_ms, Some(72));
}

#[test]
fn no_result_responses_are_demoted_and_partitioned_when_hide_empty_is_enabled() {
    let mut state = EasydictUiState::default();
    state.settings.hide_empty_service_results = true;
    state.source_text = "missing-word".to_string();
    state.results = vec![
        QuickTranslateResult::new("mdx::demo", "Demo Dictionary", false).into(),
        QuickTranslateResult::new("google", "Google Translate", false).into(),
    ];
    let plan = begin_quick_translate(&mut state).expect("query should begin");

    assert!(apply_quick_translate_service_update(
        &mut state,
        QuickTranslateServiceUpdate {
            query_id: plan.query_id,
            outcome: QuickTranslateServiceOutcome {
                service: plan.services[0].clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Ok(no_result_dto(
                    "mdx::demo",
                    "Demo Dictionary",
                    "No result found in dictionary: missing-word",
                    Some(33),
                )),
            },
        }
    ));

    assert_eq!(state.results[0].id, "google");
    assert_eq!(state.results[0].status, ResultStatus::Loading);
    assert_eq!(state.results[1].id, "mdx::demo");
    assert!(state.results[1].no_result);
    assert!(state.results[1].demoted);
    assert!(!state.results[1].expanded);
    assert_eq!(
        state.results[1].body,
        "No result found in dictionary: missing-word"
    );
    assert_eq!(
        state.results[1].to_result_item().metadata.as_deref(),
        Some("No result - 33ms")
    );
}

#[test]
fn no_result_responses_stay_expanded_when_hide_empty_is_disabled() {
    let mut state = EasydictUiState::default();
    state.settings.hide_empty_service_results = false;
    state.source_text = "missing-word".to_string();
    state.results = vec![QuickTranslateResult::new("mdx::demo", "Demo Dictionary", false).into()];
    let plan = begin_quick_translate(&mut state).expect("query should begin");

    assert!(apply_quick_translate_service_update(
        &mut state,
        QuickTranslateServiceUpdate {
            query_id: plan.query_id,
            outcome: QuickTranslateServiceOutcome {
                service: plan.services[0].clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Ok(no_result_dto(
                    "mdx::demo",
                    "Demo Dictionary",
                    "No result found in dictionary: missing-word",
                    Some(33),
                )),
            },
        }
    ));

    assert!(state.results[0].no_result);
    assert!(!state.results[0].demoted);
    assert!(state.results[0].expanded);
    assert_eq!(
        state.results[0].to_result_item().metadata.as_deref(),
        Some("No result - 33ms")
    );
}

#[test]
fn run_single_service_returns_query_scoped_update() {
    let request = QuickTranslateServiceRequest {
        query_id: 9,
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
            text: "Hello".to_string(),
            from: None,
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["google".to_string()]),
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };
    let mut backend = RecordingBackend::with_responses([Ok(dto(
        "google",
        "Google Translate",
        "translated hello",
        Some("en"),
        Some(121),
    ))]);

    let update = run_quick_translate_service(&mut backend, &request);

    assert_eq!(update.query_id, 9);
    assert_eq!(backend.calls, [request.params]);
    assert!(update.outcome.result.is_ok());
}

#[test]
fn apply_outcome_hydrates_results_detected_language_and_completion_state() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.results.truncate(2);
    let plan = begin_quick_translate(&mut state).expect("translate should begin");

    let outcome = QuickTranslateOutcome {
        query_id: plan.query_id,
        results: vec![
            QuickTranslateServiceOutcome {
                service: plan.services[0].clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Ok(dto(
                    "google",
                    "Google Translate",
                    "translated hello",
                    Some("en"),
                    Some(121),
                )),
            },
            QuickTranslateServiceOutcome {
                service: plan.services[1].clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Err(QuickTranslateBackendError::new("request timed out")),
            },
        ],
    };

    assert!(apply_quick_translate_outcome(&mut state, outcome));

    assert!(!state.is_translating);
    assert_eq!(state.active_query_id, None);
    assert_eq!(state.services_completed, 2);
    assert_eq!(state.status_text, "Connected");
    assert_eq!(
        state.detected_language.as_deref(),
        Some("Detected: English")
    );
    assert_eq!(state.results[0].body, "translated hello");
    assert_eq!(state.results[0].status, ResultStatus::Ready);
    assert_eq!(state.results[0].latency_ms, Some(121));
    assert_eq!(state.results[1].body, "request timed out");
    assert_eq!(state.results[1].status, ResultStatus::Error);
}

#[test]
fn service_updates_complete_incrementally_and_keep_query_running_until_all_services_finish() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.results.truncate(2);
    let plan = begin_quick_translate(&mut state).expect("translate should begin");

    assert!(apply_quick_translate_service_update(
        &mut state,
        QuickTranslateServiceUpdate {
            query_id: plan.query_id,
            outcome: QuickTranslateServiceOutcome {
                service: plan.services[0].clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Err(QuickTranslateBackendError::new("request timed out")),
            },
        }
    ));

    assert!(state.is_translating);
    assert_eq!(state.active_query_id, Some(plan.query_id));
    assert_eq!(state.services_completed, 1);
    assert_eq!(state.status_text, "Translating");
    assert_eq!(state.results[0].status, ResultStatus::Error);

    assert!(apply_quick_translate_service_update(
        &mut state,
        QuickTranslateServiceUpdate {
            query_id: plan.query_id,
            outcome: QuickTranslateServiceOutcome {
                service: plan.services[1].clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Ok(dto(
                    "bing",
                    "Bing Translate",
                    "translated hello",
                    Some("en"),
                    Some(80),
                )),
            },
        }
    ));

    assert!(!state.is_translating);
    assert_eq!(state.active_query_id, None);
    assert_eq!(state.active_query_service_count, 0);
    assert_eq!(state.active_query_success_count, 0);
    assert_eq!(state.services_completed, 2);
    assert_eq!(state.status_text, "Connected");
    assert_eq!(state.results[1].status, ResultStatus::Ready);
}

#[test]
fn stale_outcomes_do_not_replace_newer_queries() {
    let mut state = EasydictUiState::default();
    state.results.truncate(1);

    state.source_text = "First".to_string();
    let first = begin_quick_translate(&mut state).expect("first query should begin");

    state.source_text = "Second".to_string();
    let second = begin_quick_translate(&mut state).expect("second query should begin");

    let stale = success_outcome(&first, "stale result");
    assert!(!apply_quick_translate_outcome(&mut state, stale));
    assert_eq!(state.active_query_id, Some(second.query_id));
    assert!(state.is_translating);
    assert_eq!(state.results[0].status, ResultStatus::Loading);
    assert!(state.results[0].body.is_empty());

    assert!(apply_quick_translate_outcome(
        &mut state,
        success_outcome(&second, "fresh result")
    ));
    assert_eq!(state.results[0].body, "fresh result");
}

#[test]
fn stale_service_updates_do_not_replace_newer_queries_or_increment_progress() {
    let mut state = EasydictUiState::default();
    state.results.truncate(1);

    state.source_text = "First".to_string();
    let first = begin_quick_translate(&mut state).expect("first query should begin");

    state.source_text = "Second".to_string();
    let second = begin_quick_translate(&mut state).expect("second query should begin");

    assert!(!apply_quick_translate_service_update(
        &mut state,
        QuickTranslateServiceUpdate {
            query_id: first.query_id,
            outcome: QuickTranslateServiceOutcome {
                service: first.services[0].clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Ok(dto(
                    "google",
                    "Google Translate",
                    "stale result",
                    Some("en"),
                    Some(50),
                )),
            },
        }
    ));

    assert_eq!(state.active_query_id, Some(second.query_id));
    assert_eq!(state.services_completed, 0);
    assert_eq!(state.results[0].status, ResultStatus::Loading);
    assert!(state.results[0].body.is_empty());
}

#[test]
fn app_update_quick_translate_starts_runtime_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.source_text = "Hello".to_string();

    let task = app.update(Message::QuickTranslate);

    match task {
        Task::Batch(tasks) => {
            assert_eq!(tasks.len(), 3);
            let task_kinds = tasks.iter().map(task_kind).collect::<Vec<_>>();
            assert_eq!(
                task_kinds.iter().filter(|kind| **kind == "stream").count(),
                1
            );
            assert_eq!(
                task_kinds.iter().filter(|kind| **kind == "future").count(),
                2
            );
        }
        other => panic!(
            "expected per-service task batch, got {:?}",
            task_kind(&other)
        ),
    }
    assert!(app.state.is_translating);
    assert_eq!(app.state.active_query_id, Some(1));
}

#[test]
fn source_text_enter_submits_translate_or_commits_active_dictionary_suggestion() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.source_text = "Hello".to_string();

    let task = app.update(Message::SourceTextSubmitted);
    assert_eq!(task_kind(&task), "batch");
    assert!(app.state.is_translating);
    assert_eq!(app.state.active_query_id, Some(1));

    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.source_text = "please app".to_string();
    app.state.local_dictionary_suggestions = vec![LocalDictionarySuggestion {
        key: "apple".to_string(),
        dictionary_name: "Demo Dictionary".to_string(),
    }];
    app.state.local_dictionary_suggestion_active_index = Some(0);

    let task = app.update(Message::SourceTextSubmitted);

    assert_eq!(task_kind(&task), "none");
    assert_eq!(app.state.source_text, "please apple");
    assert_eq!(app.state.active_query_id, None);
    assert!(app.state.local_dictionary_suggestions.is_empty());
    assert!(app.state.source_text_focused);
}

#[test]
fn app_update_pending_result_toggle_starts_manual_service_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.source_text = "Hello".to_string();
    app.state.results = vec![
        easydict_app::TranslationResultPreview::new("openai", "OpenAI", "")
            .streaming_capable(true)
            .manual_query(),
    ];

    let task = app.update(Message::ToggleResultExpanded("openai".to_string()));

    assert_eq!(task_kind(&task), "stream");
    assert!(app.state.is_translating);
    assert_eq!(app.state.active_query_id, Some(1));
    assert_eq!(app.state.active_query_service_count, 1);
    assert_eq!(app.state.results[0].status, ResultStatus::Loading);
    assert!(!app.state.results[0].enabled_query);
    assert!(app.state.results[0].has_queried);
}

#[test]
fn app_update_retry_result_starts_single_service_task_with_item_id() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.source_text = "Hello".to_string();
    app.state.results = vec![QuickTranslateResult::new("google", "Google Translate", false).into()];
    app.state.results[0].status = ResultStatus::Error;
    app.state.results[0].body = "request timed out".to_string();

    let task = app.update(Message::RetryResultIn(
        QuickTranslateSurface::Main,
        "google".to_string(),
    ));

    assert_eq!(task_kind(&task), "future");
    assert!(app.state.is_translating);
    assert_eq!(app.state.active_query_id, Some(1));
    assert_eq!(app.state.active_query_service_count, 1);
    assert_eq!(app.state.results[0].status, ResultStatus::Loading);
    assert!(app.state.results[0].body.is_empty());
}

#[test]
fn app_update_floating_translate_starts_surface_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.fixed.text = "Hello from fixed".to_string();
    app.state.fixed.results =
        vec![QuickTranslateResult::new("google", "Google Translate", false).into()];

    let task = app.update(Message::QuickTranslateIn(QuickTranslateSurface::Fixed));

    assert_eq!(task_kind(&task), "future");
    assert!(!app.state.is_translating);
    assert_eq!(app.state.active_query_id, None);
    assert!(app.state.fixed.is_translating);
    assert_eq!(app.state.fixed.active_query_id, Some(1));
    assert_eq!(app.state.fixed.active_query_service_count, 1);
    assert_eq!(app.state.fixed.results[0].status, ResultStatus::Loading);
}

#[test]
fn floating_surface_messages_update_only_the_target_window() {
    let mut state = EasydictUiState::default();
    let fixed_text = state.fixed.text.clone();
    let main_source = state.source_language.clone();

    state.apply(Message::FloatingSurfaceTextChanged(
        QuickTranslateSurface::Mini,
        "mini only".to_string(),
    ));
    state.apply(Message::FloatingSourceLanguageChanged(
        QuickTranslateSurface::Mini,
        "en".to_string(),
    ));
    state.apply(Message::FloatingTargetLanguageChanged(
        QuickTranslateSurface::Mini,
        "ja".to_string(),
    ));
    state.apply(Message::SwapFloatingLanguages(QuickTranslateSurface::Mini));

    assert_eq!(state.mini.text, "mini only");
    assert_eq!(state.mini.source_language, "ja");
    assert_eq!(state.mini.target_language, "en");
    assert!(state.mini.target_language_manually_selected);
    assert_eq!(state.fixed.text, fixed_text);
    assert_eq!(state.source_language, main_source);
}

#[test]
fn result_action_messages_capture_surface_service_text_and_language() {
    let mut state = EasydictUiState::default();
    state.target_language = "fr".to_string();
    state.results = vec![QuickTranslateResult::new("google", "Google Translate", false).into()];
    state.results[0].body = "bonjour".to_string();

    state.apply(Message::CopyResultIn(
        QuickTranslateSurface::Main,
        "google".to_string(),
    ));

    let action = state
        .last_result_action
        .as_ref()
        .expect("copy should capture the selected result");
    assert_eq!(action.kind, ResultActionKind::Copy);
    assert_eq!(action.surface, QuickTranslateSurface::Main);
    assert_eq!(action.service_id, "google");
    assert_eq!(action.text, "bonjour");
    assert_eq!(action.language, "fr");
}

#[test]
fn result_action_messages_use_rendered_grammar_body_and_ignore_empty_results() {
    let mut state = EasydictUiState::default();
    state.mini.target_language = "en".to_string();
    state.mini.results = vec![
        QuickTranslateResult::new("openai", "OpenAI", true).into(),
        QuickTranslateResult::new("empty", "Empty", false).into(),
    ];
    state.mini.results[0].grammar_result = Some(easydict_app::GrammarCorrectionPreview::new(
        "I has a apple",
        "I have an apple",
        Some("Use have with I.".to_string()),
        true,
    ));
    state.mini.results[1].body.clear();

    state.apply(Message::SpeakResultIn(
        QuickTranslateSurface::Mini,
        "openai".to_string(),
    ));
    assert_eq!(
        state.last_result_action.as_ref().map(|action| (
            &action.kind,
            action.text.as_str(),
            action.language.as_str()
        )),
        Some((
            &ResultActionKind::Speak,
            "Corrected\nI have an apple\n\nExplanation\nUse have with I.",
            "en",
        ))
    );

    let previous = state.last_result_action.clone();
    state.apply(Message::ReplaceResultIn(
        QuickTranslateSurface::Mini,
        "empty".to_string(),
    ));
    assert_eq!(state.last_result_action, previous);
}

#[test]
fn app_update_result_actions_emit_platform_side_effect_tasks() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.target_language = "fr".to_string();
    app.state.results = vec![QuickTranslateResult::new("google", "Google", false).into()];
    app.state.results[0].body = "bonjour".to_string();

    let copy = app.update(Message::CopyResultIn(
        QuickTranslateSurface::Main,
        "google".to_string(),
    ));
    assert_eq!(
        platform_command(&copy),
        Some(PlatformCommand::WriteClipboardText("bonjour".to_string()))
    );

    let speak = app.update(Message::SpeakResultIn(
        QuickTranslateSurface::Main,
        "google".to_string(),
    ));
    assert_eq!(
        platform_command(&speak),
        Some(PlatformCommand::SpeakText {
            text: "bonjour".to_string(),
            language: Some("fr".to_string()),
        })
    );

    let replace = app.update(Message::ReplaceResultIn(
        QuickTranslateSurface::Main,
        "google".to_string(),
    ));
    assert_eq!(
        platform_command(&replace),
        Some(PlatformCommand::InsertText("bonjour".to_string()))
    );
}

#[test]
fn app_update_translate_selection_captures_text_insertion_target() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::TranslateSelection);

    assert_eq!(
        platform_command(&task),
        Some(PlatformCommand::CaptureTextInsertionTarget)
    );
}

#[test]
fn shell_context_menu_toggle_emits_registration_commands_and_updates_setting() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    let expected = default_shell_verbs().remove(0);

    let register = app.update(Message::ToggleShellContextMenu(true));
    assert_eq!(app.state.settings.shell_context_menu, true);
    assert_eq!(
        platform_command(&register),
        Some(PlatformCommand::RegisterShellVerb(expected.clone()))
    );

    let unregister = app.update(Message::ToggleShellContextMenu(false));
    assert_eq!(app.state.settings.shell_context_menu, false);
    assert_eq!(
        platform_command(&unregister),
        Some(PlatformCommand::UnregisterShellVerb(expected))
    );
}

#[test]
fn browser_support_messages_run_bundled_registrar_commands() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let install = app.update(Message::InstallBrowserSupport);
    assert_eq!(
        platform_command(&install),
        Some(PlatformCommand::RunBundledExecutable {
            executable_name: BROWSER_REGISTRAR_EXE.to_string(),
            arguments: vec!["install".to_string()],
        })
    );

    let uninstall = app.update(Message::UninstallBrowserSupport);
    assert_eq!(
        platform_command(&uninstall),
        Some(PlatformCommand::RunBundledExecutable {
            executable_name: BROWSER_REGISTRAR_EXE.to_string(),
            arguments: vec!["uninstall".to_string()],
        })
    );
}

#[test]
fn import_mdx_dictionary_opens_dedicated_mdx_file_dialog() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::ImportMdxDictionary);

    match task {
        Task::OpenFileDialog { options, .. } => {
            assert_eq!(options.title, "Import MDX dictionary");
            assert_eq!(options.filters[0].name, "MDX dictionaries");
            assert_eq!(options.filters[0].patterns, ["*.mdx"]);
        }
        _ => panic!("expected MDX file dialog task"),
    }
}

#[test]
fn selected_mdx_dictionary_adds_imported_dictionary_and_service_rows() {
    let mut state = EasydictUiState::default();

    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Oxford English.mdx".to_string(),
    )));
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Oxford English.mdx".to_string(),
    )));

    assert_eq!(state.settings.imported_mdx_dictionaries.len(), 1);
    let dictionary = &state.settings.imported_mdx_dictionaries[0];
    assert_eq!(dictionary.service_id, "mdx::oxford-english");
    assert_eq!(dictionary.display_name, "Oxford English");
    assert_eq!(dictionary.file_path, r"C:\Dicts\Oxford English.mdx");

    assert!(state.results.iter().any(|result| {
        result.id == "mdx::oxford-english" && result.service_name == "Oxford English"
    }));
    assert!(state
        .mini
        .results
        .iter()
        .any(|result| result.id == "mdx::oxford-english"));
    assert!(state
        .fixed
        .results
        .iter()
        .any(|result| result.id == "mdx::oxford-english"));

    let plan = build_quick_translate_plan(&state, 42).expect("plan should include settings");
    let imported = plan
        .settings
        .imported_mdx_dictionaries
        .as_ref()
        .expect("imported MDX dictionaries should be in settings snapshot");
    assert_eq!(imported[0].service_id, "mdx::oxford-english");
    assert_eq!(imported[0].file_path, r"C:\Dicts\Oxford English.mdx");
}

#[test]
fn selected_mdx_dictionary_auto_discovers_companion_mdd_files() {
    let temp_dir = unique_temp_dir("easydict-mdd-discovery");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Oxford.mdx");
    let mdd_path = temp_dir.join("Oxford.mdd");
    let first_numbered = temp_dir.join("Oxford.1.mdd");
    let second_numbered = temp_dir.join("Oxford.2.mdd");
    let skipped_after_gap = temp_dir.join("Oxford.4.mdd");
    fs::write(&mdx_path, b"mdx").expect("MDX file should be created");
    fs::write(&mdd_path, b"mdd").expect("MDD file should be created");
    fs::write(&first_numbered, b"mdd1").expect("numbered MDD file should be created");
    fs::write(&second_numbered, b"mdd2").expect("numbered MDD file should be created");
    fs::write(&skipped_after_gap, b"mdd4").expect("gap MDD file should be created");

    let mut state = EasydictUiState::default();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));

    let dictionary = &state.settings.imported_mdx_dictionaries[0];
    assert_eq!(
        dictionary.mdd_file_paths,
        vec![
            path_string(&mdd_path),
            path_string(&first_numbered),
            path_string(&second_numbered),
        ]
    );
    let imported = build_quick_translate_plan(&state, 42)
        .expect("plan should include settings")
        .settings
        .imported_mdx_dictionaries
        .expect("imported MDX dictionaries should be in settings snapshot");
    assert_eq!(imported[0].mdd_file_paths, dictionary.mdd_file_paths);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn mdx_service_requests_configure_settings_and_use_mdx_lookup() {
    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut backend = RecordingBackend::with_mdx_responses([Ok(MdxLookupResult {
        entries: vec![MdxLookupEntry {
            key: "apple".to_string(),
            html: "<div>A fruit</div>".to_string(),
            dictionary_name: Some("Demo Dictionary".to_string()),
        }],
    })]);

    let outcome = run_quick_translate(&mut backend, &plan);

    assert_eq!(backend.configure_calls.len(), 1);
    assert_eq!(
        backend.configure_calls[0]
            .imported_mdx_dictionaries
            .as_ref()
            .and_then(|dictionaries| dictionaries.first())
            .map(|dictionary| dictionary.service_id.as_str()),
        Some("mdx::demo-dictionary")
    );
    assert_eq!(backend.mdx_calls.len(), 1);
    assert_eq!(backend.mdx_calls[0].dictionary_id, "mdx::demo-dictionary");
    assert_eq!(backend.mdx_calls[0].query, "apple");
    assert!(!backend.mdx_calls[0].fuzzy);
    assert!(backend.calls.is_empty());

    let result = outcome.results[0].result.as_ref().expect("MDX result");
    assert_eq!(result.service_name.as_deref(), Some("Demo Dictionary"));
    assert!(result.translated_text.contains("apple"));
    assert!(result.translated_text.contains("<div>A fruit</div>"));
}

#[test]
fn mdx_service_lookup_miss_returns_no_result_dto() {
    let mut state = EasydictUiState::default();
    state.source_text = "missing-word".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo.mdx".to_string(),
    )));
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut backend = RecordingBackend::with_mdx_responses([Ok(MdxLookupResult {
        entries: Vec::new(),
    })]);

    let outcome = run_quick_translate(&mut backend, &plan);
    let result = outcome.results[0].result.as_ref().expect("MDX miss result");

    assert_eq!(result.result_kind.as_deref(), Some("NoResult"));
    assert_eq!(
        result.info_message.as_deref(),
        Some("No result found in dictionary: missing-word")
    );
}

#[test]
fn local_dictionary_query_token_uses_current_word_and_ignores_paths() {
    assert_eq!(
        local_dictionary_query_token("please complete app").as_deref(),
        Some("app")
    );
    assert_eq!(
        local_dictionary_query_token("tea*").as_deref(),
        Some("tea*")
    );
    assert_eq!(local_dictionary_query_token("C:\\Dicts\\Demo.mdx"), None);
    assert_eq!(local_dictionary_query_token("@command"), None);
    assert_eq!(local_dictionary_query_token("   "), None);
}

#[test]
fn source_text_changes_start_local_dictionary_suggestion_task_when_mdx_is_imported() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let no_dictionary_task = app.update(Message::SourceTextChanged("app".to_string()));
    assert_eq!(task_kind(&no_dictionary_task), "none");
    assert_eq!(app.state.local_dictionary_suggestion_query, None);

    app.state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));

    let task = app.update(Message::SourceTextChanged("please app".to_string()));

    assert_eq!(task_kind(&task), "future");
    assert_eq!(app.state.active_suggestion_query_id, Some(1));
    assert_eq!(
        app.state.local_dictionary_suggestion_query.as_deref(),
        Some("app")
    );
}

#[test]
fn local_dictionary_suggestions_use_delayed_query_contract() {
    assert_eq!(LOCAL_DICTIONARY_SUGGESTION_DELAY_MS, 150);
}

#[test]
fn local_dictionary_suggestion_runner_configures_settings_and_deduplicates_fuzzy_hits() {
    let mut state = EasydictUiState::default();
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Second.mdx".to_string(),
    )));
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");
    let mut backend = RecordingSuggestionBackend::with_mdx_responses([
        Ok(MdxLookupResult {
            entries: vec![
                MdxLookupEntry {
                    key: "apple".to_string(),
                    html: "<div>apple</div>".to_string(),
                    dictionary_name: Some("Demo Dictionary".to_string()),
                },
                MdxLookupEntry {
                    key: "apple".to_string(),
                    html: "<div>duplicate</div>".to_string(),
                    dictionary_name: Some("Demo Dictionary".to_string()),
                },
            ],
        }),
        Ok(MdxLookupResult {
            entries: vec![MdxLookupEntry {
                key: "application".to_string(),
                html: "<div>application</div>".to_string(),
                dictionary_name: None,
            }],
        }),
    ]);

    let update = run_local_dictionary_suggestion_request(&mut backend, request);

    assert_eq!(backend.configure_calls.len(), 1);
    assert_eq!(
        backend.configure_calls[0]
            .imported_mdx_dictionaries
            .as_ref()
            .map(Vec::len),
        Some(2)
    );
    assert_eq!(backend.mdx_calls.len(), 2);
    assert!(backend.mdx_calls.iter().all(|call| call.fuzzy));
    assert_eq!(backend.mdx_calls[0].query, "app");
    assert_eq!(backend.mdx_calls[0].dictionary_id, "mdx::demo-dictionary");
    assert_eq!(backend.mdx_calls[1].dictionary_id, "mdx::second");
    assert_eq!(
        update.suggestions,
        vec![
            LocalDictionarySuggestion {
                key: "apple".to_string(),
                dictionary_name: "Demo Dictionary".to_string(),
            },
            LocalDictionarySuggestion {
                key: "application".to_string(),
                dictionary_name: "Second".to_string(),
            },
        ]
    );
    assert_eq!(update.error, None);
}

#[test]
fn local_dictionary_suggestion_updates_ignore_stale_queries_and_apply_matching_results() {
    let mut state = EasydictUiState::default();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    let stale = LocalDictionarySuggestionUpdate {
        query_id: request.query_id + 1,
        query: "apricot".to_string(),
        suggestions: vec![LocalDictionarySuggestion {
            key: "apricot".to_string(),
            dictionary_name: "Demo Dictionary".to_string(),
        }],
        error: None,
    };
    assert!(!apply_local_dictionary_suggestion_update(&mut state, stale));
    assert!(state.local_dictionary_suggestions.is_empty());
    assert_eq!(state.active_suggestion_query_id, Some(request.query_id));

    let matching = LocalDictionarySuggestionUpdate {
        query_id: request.query_id,
        query: request.query,
        suggestions: vec![LocalDictionarySuggestion {
            key: "apple".to_string(),
            dictionary_name: "Demo Dictionary".to_string(),
        }],
        error: None,
    };
    assert!(apply_local_dictionary_suggestion_update(
        &mut state, matching
    ));
    assert_eq!(state.active_suggestion_query_id, None);
    assert_eq!(state.local_dictionary_suggestions[0].key, "apple");
}

#[test]
fn applying_local_dictionary_suggestion_replaces_current_token_and_clears_state() {
    let mut state = EasydictUiState::default();
    state.source_text = "please app".to_string();
    state.active_suggestion_query_id = Some(10);
    state.local_dictionary_suggestion_query = Some("app".to_string());
    state.local_dictionary_suggestions = vec![LocalDictionarySuggestion {
        key: "apple".to_string(),
        dictionary_name: "Demo Dictionary".to_string(),
    }];

    assert!(apply_local_dictionary_suggestion(&mut state, "apple"));

    assert_eq!(state.source_text, "please apple");
    assert_eq!(state.active_suggestion_query_id, None);
    assert!(state.local_dictionary_suggestions.is_empty());
}

#[test]
fn local_dictionary_suggestion_navigation_moves_applies_and_restores_input_focus() {
    let mut state = EasydictUiState::default();
    state.source_text = "please app".to_string();
    state.local_dictionary_suggestions = vec![
        LocalDictionarySuggestion {
            key: "apple".to_string(),
            dictionary_name: "Demo Dictionary".to_string(),
        },
        LocalDictionarySuggestion {
            key: "application".to_string(),
            dictionary_name: "Demo Dictionary".to_string(),
        },
    ];
    state.source_text_focused = true;

    state.apply(Message::FocusLocalDictionarySuggestions);
    assert_eq!(state.local_dictionary_suggestion_active_index, Some(0));
    assert!(!state.source_text_focused);

    state.apply(Message::MoveLocalDictionarySuggestion(1));
    assert_eq!(state.local_dictionary_suggestion_active_index, Some(1));

    state.apply(Message::MoveLocalDictionarySuggestion(1));
    assert_eq!(state.local_dictionary_suggestion_active_index, Some(0));

    state.apply(Message::MoveLocalDictionarySuggestion(-1));
    assert_eq!(state.local_dictionary_suggestion_active_index, Some(1));

    state.apply(Message::CommitLocalDictionarySuggestion);
    assert_eq!(state.source_text, "please application");
    assert!(state.local_dictionary_suggestions.is_empty());
    assert!(state.source_text_focused);
}

#[test]
fn local_dictionary_suggestion_exit_and_dismiss_match_keyboard_contract() {
    let mut state = EasydictUiState::default();
    state.local_dictionary_suggestions = vec![LocalDictionarySuggestion {
        key: "apple".to_string(),
        dictionary_name: "Demo Dictionary".to_string(),
    }];

    state.apply(Message::MoveLocalDictionarySuggestion(1));
    assert_eq!(state.local_dictionary_suggestion_active_index, Some(0));
    assert!(!state.source_text_focused);

    state.apply(Message::ExitLocalDictionarySuggestions);
    assert_eq!(state.local_dictionary_suggestion_active_index, None);
    assert_eq!(state.local_dictionary_suggestions.len(), 1);
    assert!(state.source_text_focused);

    state.apply(Message::FocusLocalDictionarySuggestions);
    state.apply(Message::DismissLocalDictionarySuggestions);
    assert_eq!(state.local_dictionary_suggestion_active_index, None);
    assert!(state.local_dictionary_suggestions.is_empty());
    assert!(state.source_text_focused);
}

#[test]
fn invalid_local_dictionary_query_clears_existing_suggestions() {
    let mut state = EasydictUiState::default();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    state.source_text = "app".to_string();
    assert!(begin_local_dictionary_suggestions(&mut state).is_some());
    state.local_dictionary_suggestions = vec![LocalDictionarySuggestion {
        key: "apple".to_string(),
        dictionary_name: "Demo Dictionary".to_string(),
    }];

    state.source_text = "   ".to_string();

    assert!(begin_local_dictionary_suggestions(&mut state).is_none());
    assert_eq!(state.active_suggestion_query_id, None);
    assert!(state.local_dictionary_suggestions.is_empty());
}

#[test]
fn default_hotkey_subscriptions_cover_migration_contract() {
    let app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let mut ids = Vec::new();
    collect_hotkey_subscription_ids(&app.subscription(), &mut ids);
    ids.sort();

    assert_eq!(
        ids,
        vec![
            HOTKEY_OCR_TRANSLATE,
            HOTKEY_SHOW_FIXED,
            HOTKEY_SHOW_MAIN,
            HOTKEY_SHOW_MINI,
            HOTKEY_SILENT_OCR,
            HOTKEY_TOGGLE_FIXED,
            HOTKEY_TOGGLE_MINI,
            HOTKEY_TRANSLATE_CLIPBOARD,
        ]
    );

    let silent = default_hotkeys()
        .into_iter()
        .find(|hotkey| hotkey.id == HOTKEY_SILENT_OCR)
        .expect("silent OCR hotkey");
    assert!(silent.modifiers.contains(&HotkeyModifier::Shift));
    assert!(contains_tray_subscription(&app.subscription()));
    assert!(contains_named_event_subscription(&app.subscription()));
}

#[test]
fn hotkey_settings_disable_invalid_and_derive_toggle_subscriptions() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    app.state.apply(Message::ToggleHotkey(
        HOTKEY_TRANSLATE_CLIPBOARD.to_string(),
        false,
    ));
    app.state.apply(Message::HotkeyShortcutChanged(
        HOTKEY_SHOW_MINI.to_string(),
        "Ctrl+Alt+Y".to_string(),
    ));
    app.state.apply(Message::HotkeyShortcutChanged(
        HOTKEY_OCR_TRANSLATE.to_string(),
        "Ctrl+Alt+".to_string(),
    ));

    let hotkeys = hotkey_subscriptions(&app.subscription());
    let ids = hotkeys
        .iter()
        .map(|hotkey| hotkey.id.as_str())
        .collect::<Vec<_>>();

    assert!(!ids.contains(&HOTKEY_TRANSLATE_CLIPBOARD));
    assert!(!ids.contains(&HOTKEY_OCR_TRANSLATE));

    let show_mini = hotkeys
        .iter()
        .find(|hotkey| hotkey.id == HOTKEY_SHOW_MINI)
        .expect("show mini hotkey");
    assert_eq!(show_mini.key, HotkeyKey::Character('y'));
    assert!(show_mini.modifiers.contains(&HotkeyModifier::Control));
    assert!(show_mini.modifiers.contains(&HotkeyModifier::Alt));
    assert!(!show_mini.modifiers.contains(&HotkeyModifier::Shift));

    let toggle_mini = hotkeys
        .iter()
        .find(|hotkey| hotkey.id == HOTKEY_TOGGLE_MINI)
        .expect("toggle mini hotkey");
    assert_eq!(toggle_mini.key, HotkeyKey::Character('y'));
    assert!(toggle_mini.modifiers.contains(&HotkeyModifier::Control));
    assert!(toggle_mini.modifiers.contains(&HotkeyModifier::Alt));
    assert!(toggle_mini.modifiers.contains(&HotkeyModifier::Shift));
    assert!(app.state.settings.unsaved_changes);
}

#[test]
fn default_tray_menu_covers_migration_contract() {
    let menu = default_tray_menu();
    let ids = menu
        .items
        .iter()
        .map(|item| item.id.as_str())
        .collect::<Vec<_>>();
    let labels = menu
        .items
        .iter()
        .map(|item| item.label.as_str())
        .collect::<Vec<_>>();

    assert_eq!(menu.tooltip, "Easydict");
    assert_eq!(
        ids,
        vec![
            TRAY_SHOW_MAIN,
            TRAY_TRANSLATE_CLIPBOARD,
            TRAY_OCR_TRANSLATE,
            TRAY_SHOW_MINI,
            TRAY_SHOW_FIXED,
            TRAY_BROWSER_INSTALL,
            TRAY_BROWSER_UNINSTALL,
            TRAY_EXIT,
        ]
    );
    assert!(labels.contains(&"OCR Translate (Ctrl+Alt+S)"));
    assert!(menu.items[5].enabled);
    assert!(menu.items[6].enabled);
    assert_eq!(
        menu.items[1].action.press(),
        Some(Message::TrayCommand(TRAY_TRANSLATE_CLIPBOARD.to_string()))
    );
    assert_eq!(
        menu.items[5].action.press(),
        Some(Message::TrayCommand(TRAY_BROWSER_INSTALL.to_string()))
    );
    assert_eq!(
        menu.items[6].action.press(),
        Some(Message::TrayCommand(TRAY_BROWSER_UNINSTALL.to_string()))
    );

    let app = EasydictApp {
        state: EasydictUiState::default(),
    };
    assert_eq!(
        app.tray_menu().expect("tray menu").items.len(),
        menu.items.len()
    );
}

#[test]
fn tray_commands_route_to_existing_desktop_actions() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let show = app.update(Message::TrayCommand(TRAY_SHOW_MAIN.to_string()));
    assert!(contains_window_command(&show, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "main"
    )));

    let clipboard = app.update(Message::TrayCommand(TRAY_TRANSLATE_CLIPBOARD.to_string()));
    assert!(contains_platform_command(
        &clipboard,
        &PlatformCommand::CaptureTextInsertionTarget
    ));
    assert!(contains_read_clipboard_task(&clipboard));

    let ocr = app.update(Message::TrayCommand(TRAY_OCR_TRANSLATE.to_string()));
    assert!(contains_window_command(&ocr, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "capture-overlay"
    )));

    let mini = app.update(Message::TrayCommand(TRAY_SHOW_MINI.to_string()));
    assert!(contains_window_command(&mini, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "mini"
    )));

    let fixed = app.update(Message::TrayCommand(TRAY_SHOW_FIXED.to_string()));
    assert!(contains_window_command(&fixed, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "fixed"
    )));

    let browser_install = app.update(Message::TrayCommand(TRAY_BROWSER_INSTALL.to_string()));
    assert_eq!(
        platform_command(&browser_install),
        Some(PlatformCommand::RunBundledExecutable {
            executable_name: BROWSER_REGISTRAR_EXE.to_string(),
            arguments: vec!["install".to_string()],
        })
    );

    let browser_uninstall = app.update(Message::TrayCommand(TRAY_BROWSER_UNINSTALL.to_string()));
    assert_eq!(
        platform_command(&browser_uninstall),
        Some(PlatformCommand::RunBundledExecutable {
            executable_name: BROWSER_REGISTRAR_EXE.to_string(),
            arguments: vec!["uninstall".to_string()],
        })
    );

    let exit = app.update(Message::TrayCommand(TRAY_EXIT.to_string()));
    assert!(contains_window_command(&exit, |command| matches!(
        command,
        WindowCommand::Close(id) if id.as_str() == "main"
    )));
}

#[test]
fn settings_about_links_open_external_urls() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::OpenSettingsLink(SettingsLink::IssueFeedback));

    assert_eq!(
        app.state.last_opened_settings_link,
        Some(SettingsLink::IssueFeedback)
    );
    assert_eq!(
        platform_command(&task),
        Some(PlatformCommand::OpenUrl(
            "https://github.com/xiaocang/easydict_win32/issues".to_string(),
        ))
    );
}

#[test]
fn shell_and_protocol_entries_cover_ocr_activation_contract() {
    let verbs = default_shell_verbs();
    assert_eq!(verbs.len(), 1);
    assert_eq!(verbs[0].id, SHELL_OCR_TRANSLATE);
    assert_eq!(verbs[0].label, "OCR Translate");
    assert!(verbs[0].accepts_files);
    assert!(verbs[0].accepts_directory_background);
    assert_eq!(verbs[0].arguments, vec!["--ocr-translate"]);

    let protocols = default_protocol_registrations();
    assert_eq!(protocols.len(), 1);
    assert_eq!(protocols[0].scheme, PROTOCOL_EASYDICT);
    assert_eq!(protocols[0].description, "URL:Easydict Protocol");
    assert_eq!(protocols[0].arguments, vec!["%1"]);

    let named_events = default_named_events();
    assert_eq!(named_events.len(), 1);
    assert_eq!(named_events[0].name, OCR_TRANSLATE_EVENT_NAME);
    assert!(named_events[0].auto_reset);
    assert_eq!(
        named_events[0].action.press(),
        Some(Message::HotkeyTriggered(HOTKEY_OCR_TRANSLATE.to_string()))
    );

    let app = EasydictApp {
        state: EasydictUiState::default(),
    };
    assert_eq!(app.named_events()[0].name, named_events[0].name);
    assert!(app.shell_verbs().is_empty());
    assert_eq!(app.protocol_registrations(), protocols);

    let mut state = EasydictUiState::default();
    state.settings.shell_context_menu = true;
    let app = EasydictApp { state };
    assert_eq!(app.shell_verbs(), verbs);
}

#[test]
fn startup_activation_parses_shell_and_protocol_ocr_triggers() {
    assert_eq!(
        parse_startup_activation(["--ocr-translate"]),
        Some(StartupActivation::OcrTranslate)
    );
    assert_eq!(
        parse_startup_activation(["easydict://ocr-translate"]),
        Some(StartupActivation::OcrTranslate)
    );
    assert_eq!(
        parse_startup_activation(["EASYDICT://OCR-TRANSLATE?source=browser"]),
        Some(StartupActivation::OcrTranslate)
    );
    assert_eq!(
        parse_startup_activation(["easydict:ocr-translate#native-message"]),
        Some(StartupActivation::OcrTranslate)
    );
    assert_eq!(parse_startup_activation(["easydict://settings"]), None);
    assert_eq!(parse_startup_activation(["--unknown"]), None);
}

#[test]
fn startup_activation_disposition_signals_existing_instance_or_cold_starts() {
    let mut signaled = Vec::new();
    let disposition = resolve_startup_activation_disposition(["--ocr-translate"], |activation| {
        signaled.push(activation);
        Ok::<_, ()>(true)
    })
    .expect("signal succeeds");

    assert_eq!(
        disposition,
        StartupActivationDisposition::SignalRunningInstanceAndExit(StartupActivation::OcrTranslate)
    );
    assert_eq!(signaled, [StartupActivation::OcrTranslate]);

    let disposition =
        resolve_startup_activation_disposition(["easydict://ocr-translate"], |activation| {
            signaled.push(activation);
            Ok::<_, ()>(false)
        })
        .expect("signal reports no running instance");
    assert_eq!(
        disposition,
        StartupActivationDisposition::ColdLaunchWithPendingActivation(
            StartupActivation::OcrTranslate
        )
    );
    assert_eq!(signaled.len(), 2);

    let disposition = resolve_startup_activation_disposition(
        ["--unknown"],
        |_: StartupActivation| -> Result<bool, ()> {
            panic!("non-activation launches must not signal")
        },
    )
    .expect("normal launch");
    assert_eq!(disposition, StartupActivationDisposition::NormalLaunch);
}

#[test]
fn startup_activation_reuses_hotkey_ocr_overlay_path() {
    let task = startup_activation_task_for_args(["easydict://ocr-translate"]);
    let message = match task {
        Task::Message(message) => message,
        other => panic!(
            "expected startup activation message, got {:?}",
            task_kind(&other)
        ),
    };
    assert_eq!(
        message,
        Message::HotkeyTriggered(HOTKEY_OCR_TRANSLATE.to_string())
    );

    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    let follow_up = app.update(message);

    assert!(contains_window_command(&follow_up, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "capture-overlay"
    )));
}

#[test]
fn runtime_plan_captures_desktop_integration_entries() {
    let plan = RuntimePlan::<EasydictApp>::new(EasydictUiState::default());

    assert!(plan.desktop_integration.has_entries());
    assert_eq!(plan.desktop_integration.entry_count(), 3);
    assert_eq!(
        plan.desktop_integration
            .tray_menu
            .as_ref()
            .expect("tray menu")
            .tooltip,
        "Easydict"
    );
    assert!(plan.desktop_integration.shell_verbs.is_empty());
    assert_eq!(
        plan.desktop_integration.named_events[0].name,
        OCR_TRANSLATE_EVENT_NAME
    );
    assert_eq!(
        plan.desktop_integration.protocol_registrations[0].scheme,
        PROTOCOL_EASYDICT
    );

    let mut enabled = EasydictUiState::default();
    enabled.settings.shell_context_menu = true;
    let plan = RuntimePlan::<EasydictApp>::new(enabled);
    assert_eq!(plan.desktop_integration.entry_count(), 4);
    assert_eq!(
        plan.desktop_integration.shell_verbs[0].id,
        SHELL_OCR_TRANSLATE
    );
}

#[test]
fn translate_clipboard_hotkey_captures_text_insertion_target() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::HotkeyTriggered(
        HOTKEY_TRANSLATE_CLIPBOARD.to_string(),
    ));

    assert!(contains_platform_command(
        &task,
        &PlatformCommand::CaptureTextInsertionTarget
    ));
    assert!(contains_read_clipboard_task(&task));
}

#[test]
fn floating_window_toggle_hotkeys_emit_toggle_visibility_commands() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let mini_task = app.update(Message::HotkeyTriggered(HOTKEY_TOGGLE_MINI.to_string()));
    let fixed_task = app.update(Message::HotkeyTriggered(HOTKEY_TOGGLE_FIXED.to_string()));

    assert!(contains_window_command(&mini_task, |command| matches!(
        command,
        WindowCommand::ToggleVisibility(id) if id.as_str() == "mini"
    )));
    assert!(contains_window_command(&fixed_task, |command| matches!(
        command,
        WindowCommand::ToggleVisibility(id) if id.as_str() == "fixed"
    )));
}

#[test]
fn floating_window_show_hotkeys_keep_explicit_show_semantics() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let mini_task = app.update(Message::HotkeyTriggered(HOTKEY_SHOW_MINI.to_string()));
    let fixed_task = app.update(Message::HotkeyTriggered(HOTKEY_SHOW_FIXED.to_string()));

    assert!(contains_platform_command(
        &mini_task,
        &PlatformCommand::CaptureTextInsertionTarget
    ));
    assert!(contains_window_command(&mini_task, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "mini"
    )));
    assert!(contains_window_command(&fixed_task, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "fixed"
    )));
}

#[test]
fn settings_button_routes_main_window_to_settings_and_back_restores_content() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    let main_window = WindowId::new("main");

    let task = app.update(Message::OpenSettings);

    assert_eq!(task_kind(&task), "none");
    assert!(app.state.settings_open);
    assert_eq!(app.title(&main_window), "Easydict Settings");
    let settings_snapshot = win_fluent_testkit::view_snapshot(&app.view(&main_window));
    assert!(settings_snapshot.contains("Page title=\"Settings\""));
    assert!(settings_snapshot.contains("id=\"BackButton\""));
    assert!(!settings_snapshot.contains("id=\"QuickInputCard\""));

    let task = app.update(Message::Back);

    assert_eq!(task_kind(&task), "none");
    assert!(!app.state.settings_open);
    assert_eq!(app.title(&main_window), "Easydict");
    let main_snapshot = win_fluent_testkit::view_snapshot(&app.view(&main_window));
    assert!(main_snapshot.contains("id=\"QuickInputCard\""));
    assert!(main_snapshot.contains("id=\"SettingsButton\""));
}

#[test]
fn settings_changes_prompt_on_back_and_save_discard_cancel_are_stateful() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    let main_window = WindowId::new("main");

    app.update(Message::OpenSettings);
    app.update(Message::ToggleMonitorClipboard(true));
    app.update(Message::ToggleLaunchAtStartup(true));
    app.update(Message::TtsSpeedChanged("1.5".to_string()));
    app.update(Message::ToggleAutoPlayTranslation(true));
    assert!(app.state.settings.unsaved_changes);
    assert!(app.state.settings.monitor_clipboard);
    assert!(app.state.settings.launch_at_startup);
    assert_eq!(app.state.settings.tts_speed, "1.5");
    assert!(app.state.settings.auto_play_translation);

    let back_task = app.update(Message::Back);
    assert_eq!(task_kind(&back_task), "none");
    assert!(app.state.settings_open);
    assert!(app.state.settings.show_unsaved_changes_dialog);
    let snapshot = win_fluent_testkit::view_snapshot(&app.view(&main_window));
    assert!(snapshot.contains("id=\"settings.unsaved_dialog\""));

    app.update(Message::CancelSettingsChangesDialog);
    assert!(app.state.settings_open);
    assert!(!app.state.settings.show_unsaved_changes_dialog);
    assert!(app.state.settings.unsaved_changes);

    app.update(Message::Back);
    app.update(Message::SaveSettingsChanges);
    assert!(!app.state.settings_open);
    assert!(!app.state.settings.unsaved_changes);
    assert!(app.state.settings.monitor_clipboard);
    assert!(app.state.saved_settings.monitor_clipboard);
    assert!(app.state.saved_settings.launch_at_startup);
    assert_eq!(app.state.saved_settings.tts_speed, "1.5");
    assert!(app.state.saved_settings.auto_play_translation);

    app.update(Message::OpenSettings);
    app.update(Message::ToggleMonitorClipboard(false));
    app.update(Message::Back);
    app.update(Message::DiscardSettingsChanges);
    assert!(!app.state.settings_open);
    assert!(!app.state.settings.unsaved_changes);
    assert!(app.state.settings.monitor_clipboard);
    assert!(app.state.saved_settings.monitor_clipboard);
}

#[test]
fn views_settings_save_updates_surface_service_order_and_enabled_query() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    app.update(Message::OpenSettings);
    app.update(Message::ToggleWindowServiceQuery(
        QuickTranslateSurface::Main,
        "google".to_string(),
        false,
    ));
    app.update(Message::ToggleWindowService(
        QuickTranslateSurface::Main,
        "bing".to_string(),
        false,
    ));
    let openai_index = app
        .state
        .settings
        .main_window_services
        .iter()
        .position(|service| service.service_id == "openai")
        .expect("OpenAI should be registered for Main Window results");
    app.update(Message::MoveWindowService(
        QuickTranslateSurface::Main,
        "openai".to_string(),
        -(openai_index as isize),
    ));
    app.update(Message::ToggleWindowService(
        QuickTranslateSurface::Mini,
        "google".to_string(),
        false,
    ));
    app.update(Message::SaveSettingsChanges);

    assert!(!app.state.settings_open);
    assert_eq!(
        app.state
            .results
            .iter()
            .map(|result| result.id.as_str())
            .collect::<Vec<_>>(),
        vec!["openai", "google"]
    );
    assert!(app.state.results[0].enabled_query);
    assert!(!app.state.results[1].enabled_query);
    assert!(!app.state.results[1].has_queried);

    let plan = build_quick_translate_plan(&app.state, 99).expect("quick translate plan");
    assert_eq!(
        plan.services
            .iter()
            .map(|service| service.id.as_str())
            .collect::<Vec<_>>(),
        vec!["openai"]
    );
    assert_eq!(app.state.mini.results.len(), 1);
    assert_eq!(app.state.mini.results[0].id, "google");
    assert!(app.state.settings.mini_window_services[0].enabled);
}

#[test]
fn clipboard_text_received_starts_quick_translate_from_clipboard_text() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::ClipboardTextReceived(Some(
        "Hello from clipboard".to_string(),
    )));

    assert_eq!(app.state.source_text, "Hello from clipboard");
    assert!(app.state.is_translating);
    assert_eq!(app.state.active_query_id, Some(1));
    assert_eq!(app.state.active_query_service_count, 3);
    assert_eq!(task_kind(&task), "batch");
}

#[test]
fn app_update_queried_result_toggle_only_expands_without_starting_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    let was_expanded = app.state.results[0].expanded;

    let task = app.update(Message::ToggleResultExpanded("google".to_string()));

    assert_eq!(task_kind(&task), "none");
    assert_eq!(app.state.results[0].expanded, !was_expanded);
    assert!(!app.state.is_translating);
}

#[derive(Default)]
struct RecordingOpenAiHttpClient {
    requests: Vec<OpenAiHttpRequestPlan>,
    responses: VecDeque<Result<String, OpenAiExecutionError>>,
}

impl RecordingOpenAiHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl OpenAiHttpClient for RecordingOpenAiHttpClient {
    fn post_sse(
        &mut self,
        request: &OpenAiHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        self.requests.push(request.clone());
        self.responses.pop_front().unwrap_or_else(|| {
            Err(OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::Unknown,
                "test OpenAI response was not queued",
            ))
        })
    }
}

#[derive(Default)]
struct RecordingCustomStreamingHttpClient {
    requests: Vec<CustomStreamingHttpRequestPlan>,
    responses: VecDeque<Result<String, OpenAiExecutionError>>,
}

impl RecordingCustomStreamingHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl CustomStreamingHttpClient for RecordingCustomStreamingHttpClient {
    fn post_sse(
        &mut self,
        request: &CustomStreamingHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        self.requests.push(request.clone());
        self.responses.pop_front().unwrap_or_else(|| {
            Err(OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::Unknown,
                "test custom streaming response was not queued",
            ))
        })
    }
}

#[derive(Default)]
struct RecordingTraditionalHttpClient {
    requests: Vec<TraditionalHttpRequestPlan>,
    responses: VecDeque<Result<String, OpenAiExecutionError>>,
}

impl RecordingTraditionalHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl TraditionalHttpClient for RecordingTraditionalHttpClient {
    fn execute(
        &mut self,
        request: &TraditionalHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        self.requests.push(request.clone());
        self.responses.pop_front().unwrap_or_else(|| {
            Err(OpenAiExecutionError::new(
                OpenAiExecutionErrorCode::Unknown,
                "test traditional HTTP response was not queued",
            ))
        })
    }
}

struct RecordingBackend {
    configure_calls: Vec<SettingsSnapshot>,
    calls: Vec<TranslateParams>,
    stream_calls: Vec<TranslateParams>,
    grammar_calls: Vec<GrammarCorrectParams>,
    mdx_calls: Vec<MdxLookupParams>,
    responses: VecDeque<Result<TranslationResultDto, QuickTranslateBackendError>>,
    stream_responses: VecDeque<Result<QuickTranslateStreamResult, QuickTranslateBackendError>>,
    grammar_responses: VecDeque<Result<GrammarCorrectResultDto, QuickTranslateBackendError>>,
    mdx_responses: VecDeque<Result<MdxLookupResult, QuickTranslateBackendError>>,
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let mut path = std::env::temp_dir();
    path.push(format!(
        "{}-{}-{}",
        prefix,
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("system time should be after Unix epoch")
            .as_nanos()
    ));
    path
}

fn path_string(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

impl RecordingBackend {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<TranslationResultDto, QuickTranslateBackendError>>,
    ) -> Self {
        Self {
            configure_calls: Vec::new(),
            calls: Vec::new(),
            stream_calls: Vec::new(),
            grammar_calls: Vec::new(),
            mdx_calls: Vec::new(),
            responses: responses.into_iter().collect(),
            stream_responses: VecDeque::new(),
            grammar_responses: VecDeque::new(),
            mdx_responses: VecDeque::new(),
        }
    }

    fn with_translation_and_grammar_responses(
        responses: impl IntoIterator<Item = Result<TranslationResultDto, QuickTranslateBackendError>>,
        grammar_responses: impl IntoIterator<
            Item = Result<GrammarCorrectResultDto, QuickTranslateBackendError>,
        >,
    ) -> Self {
        Self::with_translation_stream_and_grammar_responses(responses, [], grammar_responses)
    }

    fn with_translation_stream_and_grammar_responses(
        responses: impl IntoIterator<Item = Result<TranslationResultDto, QuickTranslateBackendError>>,
        stream_responses: impl IntoIterator<
            Item = Result<QuickTranslateStreamResult, QuickTranslateBackendError>,
        >,
        grammar_responses: impl IntoIterator<
            Item = Result<GrammarCorrectResultDto, QuickTranslateBackendError>,
        >,
    ) -> Self {
        Self {
            configure_calls: Vec::new(),
            calls: Vec::new(),
            stream_calls: Vec::new(),
            grammar_calls: Vec::new(),
            mdx_calls: Vec::new(),
            responses: responses.into_iter().collect(),
            stream_responses: stream_responses.into_iter().collect(),
            grammar_responses: grammar_responses.into_iter().collect(),
            mdx_responses: VecDeque::new(),
        }
    }

    fn with_mdx_responses(
        responses: impl IntoIterator<Item = Result<MdxLookupResult, QuickTranslateBackendError>>,
    ) -> Self {
        Self {
            configure_calls: Vec::new(),
            calls: Vec::new(),
            stream_calls: Vec::new(),
            grammar_calls: Vec::new(),
            mdx_calls: Vec::new(),
            responses: VecDeque::new(),
            stream_responses: VecDeque::new(),
            grammar_responses: VecDeque::new(),
            mdx_responses: responses.into_iter().collect(),
        }
    }
}

impl QuickTranslateBackend for RecordingBackend {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        self.configure_calls.push(settings.clone());
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        self.calls.push(params.clone());
        self.responses
            .pop_front()
            .expect("test backend response should be queued")
    }

    fn correct_grammar(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError> {
        self.grammar_calls.push(params.clone());
        self.grammar_responses
            .pop_front()
            .expect("test backend grammar response should be queued")
    }

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError> {
        self.stream_calls.push(params.clone());
        self.stream_responses
            .pop_front()
            .expect("test backend stream response should be queued")
    }

    fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, QuickTranslateBackendError> {
        self.mdx_calls.push(params.clone());
        self.mdx_responses
            .pop_front()
            .expect("test backend MDX response should be queued")
    }
}

struct RecordingSuggestionBackend {
    configure_calls: Vec<SettingsSnapshot>,
    mdx_calls: Vec<MdxLookupParams>,
    mdx_responses: VecDeque<Result<MdxLookupResult, LocalDictionarySuggestionError>>,
}

impl RecordingSuggestionBackend {
    fn with_mdx_responses(
        responses: impl IntoIterator<Item = Result<MdxLookupResult, LocalDictionarySuggestionError>>,
    ) -> Self {
        Self {
            configure_calls: Vec::new(),
            mdx_calls: Vec::new(),
            mdx_responses: responses.into_iter().collect(),
        }
    }
}

impl LocalDictionarySuggestionBackend for RecordingSuggestionBackend {
    fn configure(
        &mut self,
        settings: &SettingsSnapshot,
    ) -> Result<(), LocalDictionarySuggestionError> {
        self.configure_calls.push(settings.clone());
        Ok(())
    }

    fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, LocalDictionarySuggestionError> {
        self.mdx_calls.push(params.clone());
        self.mdx_responses
            .pop_front()
            .expect("test backend MDX suggestion response should be queued")
    }
}

fn success_outcome(plan: &QuickTranslatePlan, translated_text: &str) -> QuickTranslateOutcome {
    QuickTranslateOutcome {
        query_id: plan.query_id,
        results: vec![QuickTranslateServiceOutcome {
            service: plan.services[0].clone(),
            grammar_result: None,
            streamed_chunks: Vec::new(),
            result: Ok(dto(
                &plan.services[0].id,
                &plan.services[0].name,
                translated_text,
                Some("en"),
                Some(50),
            )),
        }],
    }
}

struct QuickTranslateResult {
    id: &'static str,
    name: &'static str,
    grammar_capable: bool,
    streaming_capable: bool,
}

impl QuickTranslateResult {
    fn new(id: &'static str, name: &'static str, grammar_capable: bool) -> Self {
        Self {
            id,
            name,
            grammar_capable,
            streaming_capable: false,
        }
    }

    fn streaming(mut self) -> Self {
        self.streaming_capable = true;
        self
    }
}

impl From<QuickTranslateResult> for easydict_app::TranslationResultPreview {
    fn from(value: QuickTranslateResult) -> Self {
        easydict_app::TranslationResultPreview::new(value.id, value.name, "")
            .grammar_capable(value.grammar_capable)
            .streaming_capable(value.streaming_capable)
    }
}

fn translation_resolution(from: &str, to: &str) -> easydict_app::QuickQueryLanguageResolution {
    easydict_app::QuickQueryLanguageResolution {
        selected_source_language: from.to_string(),
        selected_target_language: to.to_string(),
        effective_source_language: from.to_string(),
        effective_target_language: to.to_string(),
        effective_mode: QuickQueryMode::Translation,
        is_target_auto: to == "auto",
        grammar_correction_requested: false,
        grammar_correction_fallback: false,
    }
}

fn grammar_resolution(language: &str) -> easydict_app::QuickQueryLanguageResolution {
    easydict_app::QuickQueryLanguageResolution {
        selected_source_language: language.to_string(),
        selected_target_language: language.to_string(),
        effective_source_language: language.to_string(),
        effective_target_language: language.to_string(),
        effective_mode: QuickQueryMode::GrammarCorrection,
        is_target_auto: false,
        grammar_correction_requested: true,
        grammar_correction_fallback: false,
    }
}

fn dto(
    service_id: &str,
    service_name: &str,
    translated_text: &str,
    detected_language: Option<&str>,
    timing_ms: Option<i64>,
) -> TranslationResultDto {
    TranslationResultDto {
        translated_text: translated_text.to_string(),
        service_id: Some(service_id.to_string()),
        service_name: Some(service_name.to_string()),
        detected_language: detected_language.map(str::to_string),
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms,
    }
}

fn no_result_dto(
    service_id: &str,
    service_name: &str,
    info_message: &str,
    timing_ms: Option<i64>,
) -> TranslationResultDto {
    TranslationResultDto {
        translated_text: String::new(),
        service_id: Some(service_id.to_string()),
        service_name: Some(service_name.to_string()),
        detected_language: None,
        result_kind: Some("NoResult".to_string()),
        info_message: Some(info_message.to_string()),
        timing_ms,
    }
}

fn grammar_dto(
    service_id: &str,
    service_name: &str,
    original_text: &str,
    corrected_text: &str,
    language: Option<&str>,
    timing_ms: Option<i64>,
) -> GrammarCorrectResultDto {
    GrammarCorrectResultDto {
        original_text: original_text.to_string(),
        corrected_text: corrected_text.to_string(),
        explanation: Some("grammar explanation".to_string()),
        raw_text: Some(corrected_text.to_string()),
        service_id: Some(service_id.to_string()),
        service_name: Some(service_name.to_string()),
        language: language.map(str::to_string),
        timing_ms,
        has_corrections: original_text != corrected_text,
    }
}

fn openai_settings() -> SettingsSnapshot {
    SettingsSnapshot {
        open_ai_api_key: Some("sk-native".to_string()),
        open_ai_endpoint: Some("https://api.openai.com/v1/chat/completions".to_string()),
        open_ai_model: Some("gpt-4o-mini".to_string()),
        open_ai_temperature: Some(0.2),
        ..SettingsSnapshot::default()
    }
}

fn builtin_direct_settings() -> SettingsSnapshot {
    SettingsSnapshot {
        built_in_ai_api_key: Some("builtin-user-key".to_string()),
        built_in_ai_model: Some("llama-3.1-8b-instant".to_string()),
        ..SettingsSnapshot::default()
    }
}

fn gemini_settings() -> SettingsSnapshot {
    SettingsSnapshot {
        gemini_api_key: Some("gemini-key".to_string()),
        gemini_model: Some("gemini-2.5-flash".to_string()),
        ..SettingsSnapshot::default()
    }
}

fn doubao_settings() -> SettingsSnapshot {
    SettingsSnapshot {
        doubao_api_key: Some("doubao-key".to_string()),
        doubao_endpoint: Some("https://ark.example.test/api/v3/responses".to_string()),
        doubao_model: Some("doubao-seed-translation-250915".to_string()),
        ..SettingsSnapshot::default()
    }
}

fn quick_service(
    id: &str,
    name: &str,
    grammar_capable: bool,
    streaming_capable: bool,
) -> QuickTranslateService {
    QuickTranslateService {
        id: id.to_string(),
        name: name.to_string(),
        enabled_query: true,
        grammar_capable,
        streaming_capable,
    }
}

fn chat_completion_sse(chunks: &[&str]) -> String {
    let mut sse = String::new();
    for chunk in chunks {
        sse.push_str("data: {\"choices\":[{\"delta\":{\"content\":");
        sse.push_str(&serde_json::to_string(chunk).expect("test chunk should serialize"));
        sse.push_str("}}]}\n\n");
    }
    sse.push_str("data: [DONE]\n\n");
    sse
}

fn gemini_stream_sse(chunks: &[&str]) -> String {
    let mut sse = String::new();
    for chunk in chunks {
        sse.push_str("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":");
        sse.push_str(&serde_json::to_string(chunk).expect("test chunk should serialize"));
        sse.push_str("}]}}]}\n\n");
    }
    sse.push_str("data: [DONE]\n\n");
    sse
}

fn doubao_stream_sse(chunks: &[&str]) -> String {
    let mut sse = String::new();
    for chunk in chunks {
        sse.push_str("event: response.output_text.delta\n");
        sse.push_str("data: {\"delta\":");
        sse.push_str(&serde_json::to_string(chunk).expect("test chunk should serialize"));
        sse.push_str("}\n\n");
    }
    sse.push_str("data: [DONE]\n\n");
    sse
}

fn task_kind(task: &Task<Message>) -> &'static str {
    match task {
        Task::None => "none",
        Task::Message(_) => "message",
        Task::Batch(_) => "batch",
        Task::Future(_) => "future",
        Task::Stream(_) => "stream",
        Task::Window(_) => "window",
        Task::Platform(_) => "platform",
        Task::ReadClipboardText(_) => "read_clipboard",
        Task::CaptureScreenRegion { .. } => "capture_screen",
        Task::OpenFileDialog { .. } => "file_dialog",
    }
}

fn platform_command(task: &Task<Message>) -> Option<PlatformCommand> {
    match task {
        Task::Platform(command) => Some(command.clone()),
        _ => None,
    }
}

fn contains_platform_command(task: &Task<Message>, expected: &PlatformCommand) -> bool {
    match task {
        Task::Platform(command) => command == expected,
        Task::Batch(tasks) => tasks
            .iter()
            .any(|task| contains_platform_command(task, expected)),
        _ => false,
    }
}

fn contains_read_clipboard_task(task: &Task<Message>) -> bool {
    match task {
        Task::ReadClipboardText(_) => true,
        Task::Batch(tasks) => tasks.iter().any(contains_read_clipboard_task),
        _ => false,
    }
}

fn contains_window_command(
    task: &Task<Message>,
    predicate: impl Fn(&WindowCommand<Message>) -> bool + Copy,
) -> bool {
    match task {
        Task::Window(command) => predicate(command),
        Task::Batch(tasks) => tasks
            .iter()
            .any(|task| contains_window_command(task, predicate)),
        _ => false,
    }
}

fn collect_hotkey_subscription_ids(
    subscription: &Subscription<Message>,
    ids: &mut Vec<&'static str>,
) {
    match subscription {
        Subscription::None => {}
        Subscription::Batch(values) => {
            for value in values {
                collect_hotkey_subscription_ids(value, ids);
            }
        }
        Subscription::Event { kind, .. } => {
            if let SubscriptionKind::Hotkey(hotkey) = kind {
                ids.push(match hotkey.id.as_str() {
                    HOTKEY_SHOW_MAIN => HOTKEY_SHOW_MAIN,
                    HOTKEY_TRANSLATE_CLIPBOARD => HOTKEY_TRANSLATE_CLIPBOARD,
                    HOTKEY_OCR_TRANSLATE => HOTKEY_OCR_TRANSLATE,
                    HOTKEY_SILENT_OCR => HOTKEY_SILENT_OCR,
                    HOTKEY_SHOW_MINI => HOTKEY_SHOW_MINI,
                    HOTKEY_TOGGLE_MINI => HOTKEY_TOGGLE_MINI,
                    HOTKEY_SHOW_FIXED => HOTKEY_SHOW_FIXED,
                    HOTKEY_TOGGLE_FIXED => HOTKEY_TOGGLE_FIXED,
                    _ => "unknown",
                });
            }
        }
    }
}

fn hotkey_subscriptions(subscription: &Subscription<Message>) -> Vec<Hotkey> {
    let mut hotkeys = Vec::new();
    collect_hotkey_subscriptions(subscription, &mut hotkeys);
    hotkeys
}

fn collect_hotkey_subscriptions(subscription: &Subscription<Message>, hotkeys: &mut Vec<Hotkey>) {
    match subscription {
        Subscription::None => {}
        Subscription::Batch(values) => {
            for value in values {
                collect_hotkey_subscriptions(value, hotkeys);
            }
        }
        Subscription::Event { kind, .. } => {
            if let SubscriptionKind::Hotkey(hotkey) = kind {
                hotkeys.push(hotkey.clone());
            }
        }
    }
}

fn contains_tray_subscription(subscription: &Subscription<Message>) -> bool {
    match subscription {
        Subscription::None => false,
        Subscription::Batch(values) => values.iter().any(contains_tray_subscription),
        Subscription::Event { kind, .. } => matches!(kind, SubscriptionKind::Tray),
    }
}

fn contains_named_event_subscription(subscription: &Subscription<Message>) -> bool {
    match subscription {
        Subscription::None => false,
        Subscription::Batch(values) => values.iter().any(contains_named_event_subscription),
        Subscription::Event { kind, map } => {
            let SubscriptionKind::NamedEvent { name, auto_reset } = kind else {
                return false;
            };

            name == OCR_TRANSLATE_EVENT_NAME
                && *auto_reset
                && map(PlatformEvent::NamedEventSignaled(name.clone()))
                    == Some(Message::HotkeyTriggered(HOTKEY_OCR_TRANSLATE.to_string()))
        }
    }
}
