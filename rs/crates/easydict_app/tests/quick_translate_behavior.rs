use easydict_app::browser_registrar::RUST_BRIDGE_ROOT_NAME;
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::compat_client::{DirectWorkerFacade, WorkerCommand};
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::compat_protocol::worker_kinds;
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::mdx_native::{native_mdx_lookup_can_route, native_mdx_lookup_local_input_error};
use easydict_app::protocol::{
    local_ai_provider_modes, DefinitionDto, GrammarCorrectParams, GrammarCorrectResultDto,
    ImportedMdxDictionarySnapshot, MdxLookupEntry, MdxLookupParams, MdxLookupResult, PhoneticDto,
    SettingsSnapshot, SynonymDto, TranslateParams, TranslationResultDto, WordFormDto,
    WordResultDto,
};
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::quick_translate::{
    local_ai_route_decision_with_worker_policy,
    run_quick_translate_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver,
};
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::LocalAiWorkerQuickTranslateBackend;
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::RetainedWorkerPolicy;
use easydict_app::{
    apply_local_dictionary_suggestion, apply_local_dictionary_suggestion_update,
    apply_quick_translate_outcome, apply_quick_translate_service_update,
    auto_foundry_local_native_probe_request, begin_local_dictionary_suggestions,
    begin_manual_quick_translate_service, begin_quick_translate, begin_quick_translate_for_surface,
    begin_retry_quick_translate_service_for_surface, build_quick_translate_plan,
    build_quick_translate_plan_for_surface, clear_persistent_translation_cache_for_settings,
    default_hotkeys, default_named_events, default_protocol_registrations, default_shell_verbs,
    default_tray_menu, enrich_quick_translate_update_with_youdao_phonetics,
    local_ai_route_decision, local_dictionary_query_token,
    local_dictionary_suggestion_request_can_route_natively, long_document_translation_cache_path,
    parse_startup_activation, quick_translate_request_can_route_natively,
    resolve_quick_query_language, resolve_startup_activation_disposition,
    run_local_dictionary_suggestion_request, run_local_dictionary_suggestion_request_with_app_dir,
    run_local_dictionary_suggestion_request_with_native_index_root, run_quick_translate,
    run_quick_translate_service, run_quick_translate_service_with_app_dir,
    run_quick_translate_service_with_app_dir_and_native_local_ai_client,
    run_quick_translate_service_with_app_dir_and_native_local_ai_probes,
    run_quick_translate_streaming_service_with_app_dir_and_foundry_resolver,
    run_quick_translate_streaming_service_with_app_dir_and_native_local_ai_client,
    startup_activation_task_for_args, translation_cache_request_for_quick_translate,
    BingHttpClient, BingHttpResponse, BingTranslatorPage, CustomStreamingHttpClient,
    CustomStreamingHttpRequestPlan, EasydictApp, EasydictUiState, FoundryLocalEndpointResolver,
    FoundryLocalError, FoundryLocalErrorCode, FoundryLocalRuntimeController,
    FoundryLocalRuntimeState, FoundryLocalRuntimeStatus, LocalAiRouteDecision,
    LocalDictionarySuggestion, LocalDictionarySuggestionBackend, LocalDictionarySuggestionError,
    LocalDictionarySuggestionUpdate, LongDocumentTranslationCache, Message,
    NativeBingQuickTranslateBackend, NativeCustomStreamingQuickTranslateBackend,
    NativeMdxDictionaryReader, NativeMdxDictionaryReaderFactory, NativeMdxLookupError,
    NativeOpenAiQuickTranslateBackend, NativeOpenVinoQuickTranslateBackend,
    NativeTraditionalHttpQuickTranslateBackend, OpenAiApiFormat, OpenAiExecutionError,
    OpenAiExecutionErrorCode, OpenAiHttpClient, OpenAiHttpGetRequestPlan, OpenAiHttpRequestPlan,
    OpenAiHttpTextResponse, Phonetic, PhoneticFlightTracker, PhoneticMemoryCache, PopButtonAnchor,
    QuickQueryMode, QuickTranslateBackend, QuickTranslateBackendError, QuickTranslateExecutionKind,
    QuickTranslateOutcome, QuickTranslatePlan, QuickTranslateService, QuickTranslateServiceOutcome,
    QuickTranslateServiceRequest, QuickTranslateServiceUpdate, QuickTranslateStartError,
    QuickTranslateStreamChunk, QuickTranslateStreamResult, QuickTranslateSurface, ResultActionKind,
    SettingsLink, StartupActivation, StartupActivationDisposition, TraditionalHttpClient,
    TraditionalHttpRequestPlan, TraditionalHttpServiceKind, TranslationCacheRequest,
    TranslationLanguage, TranslationResult, BROWSER_REGISTRAR_EXE,
    FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE, HOTKEY_OCR_TRANSLATE, HOTKEY_SHOW_FIXED,
    HOTKEY_SHOW_MAIN, HOTKEY_SHOW_MINI, HOTKEY_SILENT_OCR, HOTKEY_TOGGLE_FIXED, HOTKEY_TOGGLE_MINI,
    HOTKEY_TRANSLATE_CLIPBOARD, LOCAL_DICTIONARY_SUGGESTION_DELAY_MS, OCR_TRANSLATE_EVENT_NAME,
    PROTOCOL_EASYDICT, SHELL_OCR_TRANSLATE, TRAY_BROWSER_INSTALL, TRAY_BROWSER_UNINSTALL,
    TRAY_EXIT, TRAY_OCR_TRANSLATE, TRAY_SHOW_FIXED, TRAY_SHOW_MAIN, TRAY_SHOW_MINI,
    TRAY_TRANSLATE_CLIPBOARD,
};
use easydict_app::{find_translation_service_descriptor, TranslationServiceKind};
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::{
    run_local_dictionary_suggestion_request_with_lazy_bridge,
    run_local_dictionary_suggestion_request_with_routed_backends,
};
use easydict_nllb::{
    NllbError, NllbInferenceEngine, NllbModelPaths, NllbTokenizer, NllbTranslator,
    MODEL_COMPLETION_SENTINEL, NLLB_MODEL_FILES, OPENVINO_RUNTIME_FILES,
};
use easydict_windows_ai::{
    WindowsAiError, WindowsAiGenerationOptions, WindowsAiLanguageModelClient,
    WindowsAiLanguageModelProbe, WindowsAiReadyState, WindowsAiResponse,
};
use futures_channel::mpsc::{unbounded, TryRecvError};
#[cfg(feature = "retained-dotnet-workers")]
use std::cell::Cell;
use std::cell::RefCell;
use std::collections::VecDeque;
use std::fs;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::Mutex;
use win_fluent::prelude::{
    Application, Hotkey, HotkeyKey, HotkeyModifier, PlatformCommand, PlatformEvent, ResultStatus,
    RuntimePlan, Subscription, SubscriptionKind, Task, WindowCommand, WindowId,
};

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

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
fn quick_translate_cache_request_uses_effective_service_language_and_trimmed_text() {
    let mut state = EasydictUiState::default();
    state.source_text = "  Hello cache  ".to_string();
    state.source_language = "auto".to_string();
    state.target_language = "zh-Hans".to_string();
    state.results = vec![
        QuickTranslateResult::new("google", "Google Translate", false).into(),
        QuickTranslateResult::new("bing", "Bing Translate", false).into(),
    ];

    let requests = build_quick_translate_plan(&state, 7)
        .expect("plan should be created")
        .service_requests();

    let google_cache = translation_cache_request_for_quick_translate(&requests[0])
        .expect("google request should be cacheable");
    let bing_cache = translation_cache_request_for_quick_translate(&requests[1])
        .expect("bing request should be cacheable");

    assert_eq!(google_cache.service_id, "google");
    assert_eq!(google_cache.from_language, TranslationLanguage::Auto);
    assert_eq!(
        google_cache.to_language,
        TranslationLanguage::SimplifiedChinese
    );
    assert_eq!(google_cache.text, "Hello cache");
    assert_ne!(google_cache.cache_key(), bing_cache.cache_key());

    let mut streaming_request = requests[0].clone();
    streaming_request.execution_kind = QuickTranslateExecutionKind::TranslateStream;
    assert!(translation_cache_request_for_quick_translate(&streaming_request).is_none());

    let mut custom_prompt_request = requests[0].clone();
    custom_prompt_request.params.custom_prompt = Some("Use a playful style".to_string());
    assert!(translation_cache_request_for_quick_translate(&custom_prompt_request).is_none());
}

#[test]
fn quick_translate_cache_hit_emits_service_finished_message_without_backend_task() {
    let mut app = quick_translate_cache_app(["google"]);
    let cache_request = google_cache_request("Hello cache");
    app.state.translation_cache.insert(
        &cache_request,
        TranslationResult::success(
            "你好，缓存",
            "Hello cache",
            TranslationLanguage::SimplifiedChinese,
            "Google Translate",
        ),
    );

    let task = app.update(Message::QuickTranslate);
    let updates = quick_translate_service_finished_updates(&task);

    assert!(!contains_future_task(&task));
    assert!(!contains_stream_task(&task));
    assert_eq!(updates.len(), 1);
    assert_eq!(updates[0].outcome.service.id, "google");
    assert_eq!(
        updates[0]
            .outcome
            .result
            .as_ref()
            .expect("cached result should be successful")
            .translated_text,
        "你好，缓存"
    );
    assert!(app.state.pending_quick_translate_cache_requests.is_empty());

    app.update(Message::QuickTranslateServiceFinished(updates[0].clone()));

    assert_eq!(app.state.results[0].body, "你好，缓存");
    assert_eq!(app.state.results[0].status, ResultStatus::Ready);
    assert_eq!(app.state.active_query_id, None);
    assert_eq!(
        app.state.connection_status,
        easydict_app::ConnectionStatus::Connected
    );
}

#[test]
fn mixed_cache_hit_and_miss_keeps_query_running_until_all_services_finish() {
    let mut app = quick_translate_cache_app(["google", "bing"]);
    let google_cache = google_cache_request("Hello cache");
    app.state.translation_cache.insert(
        &google_cache,
        TranslationResult::success(
            "谷歌缓存",
            "Hello cache",
            TranslationLanguage::SimplifiedChinese,
            "Google Translate",
        ),
    );

    let task = app.update(Message::QuickTranslate);
    let cached_updates = quick_translate_service_finished_updates(&task);

    assert!(contains_future_task(&task));
    assert_eq!(cached_updates.len(), 1);
    assert_eq!(cached_updates[0].outcome.service.id, "google");
    assert_eq!(app.state.pending_quick_translate_cache_requests.len(), 1);

    app.update(Message::QuickTranslateServiceFinished(
        cached_updates[0].clone(),
    ));

    assert_eq!(app.state.services_completed, 1);
    assert!(app.state.is_translating);
    assert_eq!(app.state.results[0].body, "谷歌缓存");
    assert_eq!(app.state.results[1].status, ResultStatus::Loading);

    let query_id = app
        .state
        .active_query_id
        .expect("query should stay active until miss finishes");
    app.update(Message::QuickTranslateServiceFinished(
        quick_translate_update(query_id, "bing", "Bing Translate", "必应刷新"),
    ));

    assert_eq!(app.state.active_query_id, None);
    assert_eq!(app.state.is_translating, false);
    assert_eq!(app.state.results[1].body, "必应刷新");
    assert!(app.state.pending_quick_translate_cache_requests.is_empty());
    assert!(app
        .state
        .translation_cache
        .get(&cache_request("bing", "Hello cache"))
        .is_some());
}

#[test]
fn quick_translate_cache_miss_stores_success_and_second_request_hits_cache() {
    let mut app = quick_translate_cache_app(["google"]);

    let first_task = app.update(Message::QuickTranslate);
    assert!(contains_future_task(&first_task));
    assert_eq!(app.state.pending_quick_translate_cache_requests.len(), 1);

    let query_id = app
        .state
        .active_query_id
        .expect("query should be active after cache miss");
    app.update(Message::QuickTranslateServiceFinished(
        quick_translate_update(query_id, "google", "Google Translate", "首次结果"),
    ));

    let mut cache = app.state.translation_cache.clone();
    assert_eq!(
        cache
            .get(&google_cache_request("Hello cache"))
            .expect("provider result should be cached")
            .translated_text,
        "首次结果"
    );
    assert!(app
        .state
        .settings
        .translation_cache_status
        .contains("cached result"));

    let second_task = app.update(Message::QuickTranslate);
    let updates = quick_translate_service_finished_updates(&second_task);

    assert!(!contains_future_task(&second_task));
    assert_eq!(updates.len(), 1);
    assert_eq!(
        updates[0]
            .outcome
            .result
            .as_ref()
            .expect("cached result should be successful")
            .translated_text,
        "首次结果"
    );
}

#[test]
fn translation_cache_disabled_and_clear_affect_quick_translate_cache() {
    let mut app = quick_translate_cache_app(["google"]);
    app.state.translation_cache.insert(
        &google_cache_request("Hello cache"),
        TranslationResult::success(
            "禁用时不应命中",
            "Hello cache",
            TranslationLanguage::SimplifiedChinese,
            "Google Translate",
        ),
    );
    app.state.settings.translation_cache_enabled = false;

    let task = app.update(Message::QuickTranslate);

    assert!(contains_future_task(&task));
    assert!(quick_translate_service_finished_updates(&task).is_empty());

    let clear_task = app.update(Message::ClearTranslationCache);

    assert_eq!(task_kind(&clear_task), "none");
    assert!(app.state.translation_cache.is_empty());
    assert_eq!(app.state.settings.translation_cache_status, "Cleared");
}

#[test]
fn clear_persistent_translation_cache_uses_settings_cache_dir() {
    let temp_dir = unique_temp_dir("easydict-persistent-cache-clear-settings-root");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let settings = SettingsSnapshot {
        cache_dir: Some(path_string(&temp_dir)),
        ..SettingsSnapshot::default()
    };
    let db_path = long_document_translation_cache_path(settings.cache_dir_str());
    let mut cache = LongDocumentTranslationCache::open(&db_path).expect("cache should open");
    cache
        .set(
            "google",
            "English",
            "SimplifiedChinese",
            "SOURCE-HASH",
            "Hello",
            "你好",
        )
        .expect("cache set should work");
    assert_eq!(cache.entry_count().expect("count should work"), 1);
    drop(cache);

    clear_persistent_translation_cache_for_settings(&settings);

    let cache = LongDocumentTranslationCache::open(&db_path).expect("cache should reopen");
    assert_eq!(cache.entry_count().expect("count should work"), 0);
    drop(cache);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
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
            custom_prompt: Some("Preserve glossary terms.".to_string()),
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
    assert!(requests[0].body["messages"][0]["content"]
        .as_str()
        .unwrap()
        .contains("Additional instructions: Preserve glossary terms."));
}

#[test]
fn native_openai_quick_translate_backend_observes_stream_chunks_before_result() {
    struct StepwiseOpenAiHttpClient {
        events: Rc<RefCell<Vec<String>>>,
    }

    impl OpenAiHttpClient for StepwiseOpenAiHttpClient {
        fn post_sse(
            &mut self,
            _request: &OpenAiHttpRequestPlan,
        ) -> Result<String, OpenAiExecutionError> {
            panic!("native backend streaming should use post_sse_lines")
        }

        fn post_sse_lines(
            &mut self,
            request: &OpenAiHttpRequestPlan,
            on_line: &mut dyn FnMut(&str) -> Result<(), OpenAiExecutionError>,
        ) -> Result<(), OpenAiExecutionError> {
            self.events
                .borrow_mut()
                .push(format!("request:{}", request.endpoint));
            on_line("data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}")?;
            self.events
                .borrow_mut()
                .push("after-first-line".to_string());
            on_line("data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}")?;
            self.events
                .borrow_mut()
                .push("after-second-line".to_string());
            on_line("data: [DONE]")?;
            Ok(())
        }
    }

    let events = Rc::new(RefCell::new(Vec::new()));
    let mut backend = NativeOpenAiQuickTranslateBackend::new(StepwiseOpenAiHttpClient {
        events: Rc::clone(&events),
    });
    backend.configure(&openai_settings()).unwrap();
    let mut observed = Vec::new();
    let callback_events = Rc::clone(&events);

    let streamed = backend
        .translate_stream_observing_chunks(
            &TranslateParams {
                text: "Hello".to_string(),
                from: Some("en".to_string()),
                to: Some("zh-Hans".to_string()),
                services: Some(vec!["openai".to_string()]),
                custom_prompt: None,
            },
            &mut |chunk| {
                callback_events.borrow_mut().push(format!("chunk:{chunk}"));
                observed.push(chunk.to_string());
            },
        )
        .unwrap();

    assert_eq!(streamed.chunks, vec!["你".to_string(), "好".to_string()]);
    assert_eq!(streamed.result.translated_text, "你好");
    assert_eq!(observed, streamed.chunks);
    assert_eq!(
        events.borrow().as_slice(),
        [
            "request:https://api.openai.com/v1/chat/completions",
            "chunk:你",
            "after-first-line",
            "chunk:好",
            "after-second-line",
        ]
    );
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
            custom_prompt: None,
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
            custom_prompt: None,
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
fn native_openai_quick_translate_supports_builtin_proxy_mode() {
    let request = QuickTranslateServiceRequest {
        query_id: 25,
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
            custom_prompt: None,
        },
        grammar_params: None,
        settings: builtin_proxy_settings(),
    };
    let mut backend = NativeOpenAiQuickTranslateBackend::new(
        RecordingOpenAiHttpClient::with_responses([Ok(chat_completion_sse(&["你好"]))]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("native Built-in AI proxy mode should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("builtin"));
    assert_eq!(result.service_name.as_deref(), Some("Built-in AI"));
    assert_eq!(update.outcome.streamed_chunks, vec!["你好".to_string()]);

    let requests = &backend.http_client().requests;
    assert_eq!(requests.len(), 1);
    assert!(requests[0].endpoint.starts_with("https://"));
    assert!(requests[0]
        .headers
        .iter()
        .any(|(name, value)| name == "Authorization" && value.starts_with("Bearer ")));
    assert!(requests[0]
        .headers
        .contains(&("X-Device-Id".to_string(), "device-id".to_string())));
    assert!(requests[0]
        .headers
        .contains(&("X-Device-Token".to_string(), "device-token".to_string())));
    assert_eq!(requests[0].body["model"], "glm-4-flash");
}

#[test]
fn builtin_device_registration_result_updates_token_without_dirtying_settings() {
    let mut state = EasydictUiState::default();
    state.settings.device_id = "device-id".to_string();
    state.saved_settings = state.settings.clone();

    state.apply(Message::BuiltInAiDeviceRegistrationFinished(Ok(Some(
        "registered-token".to_string(),
    ))));

    assert_eq!(state.settings.device_token, "registered-token");
    assert_eq!(state.saved_settings.device_token, "registered-token");
    assert!(!state.settings.unsaved_changes);

    state.apply(Message::BuiltInAiDeviceRegistrationFinished(Err(
        "network error".to_string(),
    )));
    assert_eq!(state.settings.device_token, "registered-token");

    state.apply(Message::BuiltInAiDeviceRegistrationFinished(Ok(Some(
        "   ".to_string(),
    ))));
    assert_eq!(state.settings.device_token, "registered-token");
}

#[test]
fn app_persists_builtin_device_token_after_successful_registration() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.device_id = "device-id".to_string();
    app.state.settings.unsaved_changes = true;
    app.state.saved_settings = EasydictUiState::default().settings;
    app.state.saved_settings.device_id = "device-id".to_string();

    let task = app.update(Message::BuiltInAiDeviceRegistrationFinished(Ok(Some(
        "registered-token".to_string(),
    ))));

    assert!(contains_future_task(&task));
    assert_eq!(app.state.settings.device_token, "registered-token");
    assert_eq!(app.state.saved_settings.device_token, "registered-token");
    assert!(app.state.settings.unsaved_changes);

    let task = app.update(Message::BuiltInAiDeviceRegistrationFinished(Ok(None)));
    assert!(!contains_future_task(&task));
}

#[test]
fn app_startup_registers_builtin_device_when_token_is_missing() {
    let mut state = EasydictUiState::default();
    state.settings.device_id = "device-id".to_string();
    state.saved_settings = state.settings.clone();

    let (_app, task) = <EasydictApp as Application>::new(state);

    assert!(contains_future_task(&task));

    let mut direct_or_registered = EasydictUiState::default();
    direct_or_registered.settings.device_id = "device-id".to_string();
    direct_or_registered.settings.device_token = "already-registered".to_string();
    let (_app, task) = <EasydictApp as Application>::new(direct_or_registered);
    assert!(!contains_future_task(&task));

    let mut direct_user_key = EasydictUiState::default();
    if let Some(builtin) = direct_user_key
        .settings
        .service_provider_settings
        .iter_mut()
        .find(|setting| setting.service_id == "builtin")
    {
        builtin.api_key = "user-key".to_string();
    }
    direct_user_key.settings.device_id = "device-id".to_string();
    let (_app, task) = <EasydictApp as Application>::new(direct_user_key);
    assert!(!contains_future_task(&task));
}

#[test]
fn app_start_foundry_local_runs_native_prepare_task_and_applies_result() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.local_ai_provider = local_ai_provider_modes::FOUNDRY_LOCAL.to_string();

    let task = app.update(Message::StartFoundryLocal);

    assert!(contains_future_task(&task));
    assert_eq!(
        app.state.settings.foundry_local_status,
        "Starting Foundry Local service..."
    );

    app.update(Message::FoundryLocalPrepareFinished(Ok(
        easydict_app::FoundryLocalPrepareOutcome {
            ready: true,
            status_message: "Foundry Local is ready at http://localhost:5273/v1/chat/completions."
                .to_string(),
            endpoint: Some("http://localhost:5273/v1/chat/completions".to_string()),
            model: "qwen2.5-0.5b".to_string(),
        },
    )));
    assert!(app
        .state
        .settings
        .foundry_local_status
        .contains("Foundry Local is ready"));
    assert_eq!(
        app.state.settings.foundry_local_endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(app.state.settings.foundry_local_model, "qwen2.5-0.5b");
    assert!(app.state.settings.unsaved_changes);

    app.update(Message::FoundryLocalPrepareFinished(Err(
        "Foundry Local CLI is not installed or is not available on PATH.".to_string(),
    )));
    assert_eq!(
        app.state.settings.foundry_local_status,
        "Foundry Local CLI is not installed or is not available on PATH."
    );
}

#[test]
fn app_start_foundry_local_in_auto_provider_enables_native_auto_foundry_route() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.local_ai_provider = local_ai_provider_modes::AUTO.to_string();

    let task = app.update(Message::StartFoundryLocal);

    assert!(contains_future_task(&task));
    assert_eq!(
        app.state.settings.foundry_local_status,
        "Starting Foundry Local service..."
    );

    app.update(Message::FoundryLocalPrepareFinished(Ok(
        easydict_app::FoundryLocalPrepareOutcome {
            ready: true,
            status_message: "Foundry Local is ready at http://localhost:5273/v1/chat/completions."
                .to_string(),
            endpoint: Some("http://localhost:5273/v1/chat/completions".to_string()),
            model: "qwen2.5-0.5b".to_string(),
        },
    )));

    assert_eq!(
        app.state.settings.foundry_local_endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(
        app.state.settings.local_ai_provider,
        local_ai_provider_modes::AUTO
    );
    assert!(app.state.settings.unsaved_changes);

    let request = QuickTranslateServiceRequest {
        query_id: 94,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: easydict_app::state::settings_snapshot(&app.state.settings),
    };

    assert!(quick_translate_request_can_route_natively(&request));
}

#[test]
fn foundry_local_prepare_result_persists_only_discovered_empty_fields() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.saved_settings = app.state.settings.clone();
    app.state.settings.foundry_local_endpoint.clear();
    app.state.settings.foundry_local_model.clear();
    app.state.saved_settings.foundry_local_endpoint.clear();
    app.state.saved_settings.foundry_local_model.clear();
    app.state.settings.monitor_clipboard = true;
    app.state.saved_settings.monitor_clipboard = false;
    app.state.settings.unsaved_changes = true;

    let task = app.update(Message::FoundryLocalPrepareFinished(Ok(
        easydict_app::FoundryLocalPrepareOutcome {
            ready: true,
            status_message: "Foundry Local is ready at http://localhost:5273/v1/chat/completions."
                .to_string(),
            endpoint: Some("http://localhost:5273/v1/chat/completions".to_string()),
            model: "qwen2.5-0.5b".to_string(),
        },
    )));

    assert!(contains_future_task(&task));
    assert_eq!(
        app.state.settings.foundry_local_endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(app.state.settings.foundry_local_model, "qwen2.5-0.5b");
    assert_eq!(
        app.state.saved_settings.foundry_local_endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(app.state.saved_settings.foundry_local_model, "qwen2.5-0.5b");
    assert!(app.state.settings.monitor_clipboard);
    assert!(!app.state.saved_settings.monitor_clipboard);
}

#[test]
fn app_prepare_local_ai_model_routes_foundry_provider_to_native_prepare_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.local_ai_provider = local_ai_provider_modes::FOUNDRY_LOCAL.to_string();

    let task = app.update(Message::PrepareLocalAiModel);

    assert!(contains_future_task(&task));
    assert_eq!(
        app.state.settings.local_ai_prepare_progress,
        "Starting Foundry Local service..."
    );
    assert_eq!(
        app.state.settings.foundry_local_status,
        "Starting Foundry Local service..."
    );

    app.update(Message::FoundryLocalPrepareFinished(Ok(
        easydict_app::FoundryLocalPrepareOutcome {
            ready: true,
            status_message: "Foundry Local is ready at http://localhost:5273/v1/chat/completions."
                .to_string(),
            endpoint: Some("http://localhost:5273/v1/chat/completions".to_string()),
            model: "qwen2.5-0.5b".to_string(),
        },
    )));

    assert_eq!(
        app.state.settings.foundry_local_endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert!(app.state.settings.unsaved_changes);
}

#[test]
fn app_prepare_local_ai_model_routes_auto_provider_to_windows_ai_prepare_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.local_ai_provider = local_ai_provider_modes::AUTO.to_string();

    let task = app.update(Message::PrepareLocalAiModel);

    assert!(contains_future_task(&task));
    assert_eq!(
        app.state.settings.local_ai_status,
        "Preparing Phi Silica model"
    );
    assert_eq!(
        app.state.settings.local_ai_prepare_progress,
        "Requesting model download and preparation from Windows"
    );
    assert_eq!(
        app.state.settings.foundry_local_status,
        "Endpoint auto-detected at runtime"
    );
}

#[test]
fn app_prepare_local_ai_model_keeps_openvino_on_download_path() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.local_ai_provider = local_ai_provider_modes::OPENVINO.to_string();

    let task = app.update(Message::PrepareLocalAiModel);

    assert!(!contains_future_task(&task));
    assert_eq!(app.state.settings.local_ai_status, "OpenVINO selected");
    assert_eq!(
        app.state.settings.local_ai_prepare_progress,
        "Use Download model to prepare OpenVINO assets"
    );
}

#[test]
fn app_windows_ai_prepare_finished_updates_local_ai_status() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.local_ai_provider = local_ai_provider_modes::WINDOWS_AI.to_string();

    let task = app.update(Message::WindowsAiPrepareFinished(Ok(
        easydict_windows_ai::status_for_ready_state(
            easydict_windows_ai::WindowsAiReadyState::Ready,
        ),
    )));

    assert_eq!(task_kind(&task), "none");
    assert_eq!(app.state.settings.local_ai_status, "Phi Silica is ready.");
    assert_eq!(app.state.settings.local_ai_prepare_progress, "Ready");
}

#[test]
fn app_windows_ai_prepare_finished_reports_incompatible_status() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.local_ai_provider = local_ai_provider_modes::WINDOWS_AI.to_string();

    app.update(Message::WindowsAiPrepareFinished(Ok(
        easydict_windows_ai::status_for_ready_state(
            easydict_windows_ai::WindowsAiReadyState::CapabilityMissing,
        ),
    )));

    assert!(app
        .state
        .settings
        .local_ai_status
        .contains("systemAIModels"));
    assert_eq!(
        app.state.settings.local_ai_prepare_progress,
        "Not compatible"
    );
}

#[test]
fn foundry_local_prepare_result_does_not_overwrite_user_endpoint() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.state.settings.foundry_local_endpoint = "https://foundry.example.test/v1".to_string();
    app.state.settings.foundry_local_model = "custom-model".to_string();
    app.state.saved_settings = app.state.settings.clone();
    app.state.settings.unsaved_changes = false;

    let task = app.update(Message::FoundryLocalPrepareFinished(Ok(
        easydict_app::FoundryLocalPrepareOutcome {
            ready: true,
            status_message: "Foundry Local is ready at http://localhost:5273/v1/chat/completions."
                .to_string(),
            endpoint: Some("http://localhost:5273/v1/chat/completions".to_string()),
            model: "qwen2.5-0.5b".to_string(),
        },
    )));

    assert_eq!(
        app.state.settings.foundry_local_endpoint,
        "https://foundry.example.test/v1"
    );
    assert_eq!(app.state.settings.foundry_local_model, "custom-model");
    assert_eq!(
        app.state.saved_settings.foundry_local_endpoint,
        "https://foundry.example.test/v1"
    );
    assert_eq!(app.state.saved_settings.foundry_local_model, "custom-model");
    assert!(!app.state.settings.unsaved_changes);
    assert_eq!(task_kind(&task), "none");
}

#[test]
fn native_openai_quick_translate_supports_foundry_local_endpoint() {
    let request = QuickTranslateServiceRequest {
        query_id: 26,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::FOUNDRY_LOCAL.to_string()),
            foundry_local_endpoint: Some("http://127.0.0.1:5273/v1".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };
    let mut backend = NativeOpenAiQuickTranslateBackend::new(
        RecordingOpenAiHttpClient::with_responses([Ok(chat_completion_sse(&["你好"]))]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("native Foundry Local endpoint should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    assert_eq!(result.service_name.as_deref(), Some("Windows Local AI"));
    assert_eq!(update.outcome.streamed_chunks, vec!["你好".to_string()]);

    let requests = &backend.http_client().requests;
    assert_eq!(requests.len(), 1);
    assert_eq!(
        requests[0].endpoint,
        "http://127.0.0.1:5273/v1/chat/completions"
    );
    assert!(requests[0].headers.is_empty());
    assert_eq!(requests[0].api_format, OpenAiApiFormat::ChatCompletions);
    assert_eq!(requests[0].body["model"], "qwen2.5-0.5b");
}

#[test]
fn native_openai_quick_translate_resolves_foundry_local_model_alias() {
    let request = QuickTranslateServiceRequest {
        query_id: 129,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::FOUNDRY_LOCAL.to_string()),
            foundry_local_endpoint: Some("http://127.0.0.1:5273/v1".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };
    let mut backend = NativeOpenAiQuickTranslateBackend::new(
        RecordingOpenAiHttpClient::with_responses_and_get_responses(
            [Ok(chat_completion_sse(&["你好"]))],
            [Ok(Some(OpenAiHttpTextResponse {
                status_code: 200,
                reason_phrase: "OK".to_string(),
                body: r#"{
                    "data": [
                        { "id": "qwen2.5-0.5b-instruct-openvino-cpu" },
                        { "id": "qwen2.5-0.5b-instruct-openvino-npu" }
                    ]
                }"#
                .to_string(),
            }))],
        ),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("native Foundry Local endpoint should succeed with resolved model");
    assert_eq!(result.translated_text, "你好");
    let client = backend.http_client();
    assert_eq!(client.get_requests.len(), 1);
    assert_eq!(
        client.get_requests[0].endpoint,
        "http://127.0.0.1:5273/v1/models"
    );
    assert_eq!(
        client.requests[0].body["model"],
        "qwen2.5-0.5b-instruct-openvino-npu"
    );
}

#[test]
fn native_openai_quick_translate_supports_auto_foundry_local_endpoint() {
    let request = QuickTranslateServiceRequest {
        query_id: 126,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_endpoint: Some("http://127.0.0.1:5273/v1".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };
    assert!(quick_translate_request_can_route_natively(&request));
    let mut backend = NativeOpenAiQuickTranslateBackend::new(
        RecordingOpenAiHttpClient::with_responses([Ok(chat_completion_sse(&["你好"]))]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("Auto provider with configured Foundry endpoint should use native route");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    assert_eq!(update.outcome.streamed_chunks, vec!["你好".to_string()]);
    let requests = &backend.http_client().requests;
    assert_eq!(requests.len(), 1);
    assert_eq!(
        requests[0].endpoint,
        "http://127.0.0.1:5273/v1/chat/completions"
    );
    assert_eq!(requests[0].api_format, OpenAiApiFormat::ChatCompletions);
}

#[test]
fn native_openai_quick_translate_supports_auto_foundry_local_grammar() {
    let request = QuickTranslateServiceRequest {
        query_id: 127,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::GrammarCorrection,
        execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
        params: TranslateParams {
            text: "He go home.".to_string(),
            from: Some("en".to_string()),
            to: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: Some(GrammarCorrectParams {
            text: "He go home.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            include_explanations: true,
        }),
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_endpoint: Some("http://127.0.0.1:5273/v1".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };
    assert!(quick_translate_request_can_route_natively(&request));
    let mut backend =
        NativeOpenAiQuickTranslateBackend::new(RecordingOpenAiHttpClient::with_responses([Ok(
            chat_completion_sse(&["[CORRECTED]He goes home.[/CORRECTED]\n\
                 [EXPLANATION]Subject-verb agreement.[/EXPLANATION]"]),
        )]));

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("Auto provider with configured Foundry endpoint should correct grammar natively");
    assert_eq!(result.translated_text, "He goes home.");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    let grammar_result = update
        .outcome
        .grammar_result
        .expect("grammar preview should be retained");
    assert_eq!(grammar_result.corrected_text, "He goes home.");
    assert_eq!(
        grammar_result.explanation.as_deref(),
        Some("Subject-verb agreement.")
    );
    assert!(grammar_result.has_corrections);
    let requests = &backend.http_client().requests;
    assert_eq!(requests.len(), 1);
    assert_eq!(
        requests[0].endpoint,
        "http://127.0.0.1:5273/v1/chat/completions"
    );
    assert_eq!(requests[0].api_format, OpenAiApiFormat::ChatCompletions);
    assert_eq!(requests[0].body["model"], "qwen2.5-0.5b");
}

#[test]
fn auto_foundry_local_probe_request_uses_discovered_endpoint_before_worker_route() {
    let request = QuickTranslateServiceRequest {
        query_id: 129,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };
    assert!(!quick_translate_request_can_route_natively(&request));
    let mut resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://localhost:5273/openai/status".to_string(),
    ))]);

    let native_request = auto_foundry_local_native_probe_request(&request, &mut resolver)
        .expect("running Foundry Local endpoint should make Auto probe native");

    assert_eq!(resolver.calls, 1);
    assert_eq!(
        native_request.settings.foundry_local_endpoint.as_deref(),
        Some("http://localhost:5273/v1/chat/completions")
    );
    assert_eq!(
        native_request.settings.foundry_local_model.as_deref(),
        Some("qwen2.5-0.5b")
    );
    assert_eq!(resolver.status_calls, 2);
    assert_eq!(resolver.start_calls, 1);
    assert_eq!(resolver.load_model_calls, vec!["qwen2.5-0.5b".to_string()]);
    assert!(quick_translate_request_can_route_natively(&native_request));
    assert!(request.settings.foundry_local_endpoint.is_none());
}

#[test]
fn local_ai_route_decision_marks_auto_request_for_windows_ai_probe_before_foundry() {
    let request = QuickTranslateServiceRequest {
        query_id: 131,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert_eq!(
        local_ai_route_decision(&request),
        LocalAiRouteDecision::ProbeWindowsAi
    );
}

#[test]
fn local_ai_route_decision_routes_configured_auto_endpoint_natively() {
    let request = QuickTranslateServiceRequest {
        query_id: 132,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_endpoint: Some("http://127.0.0.1:5273/v1/chat/completions".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert_eq!(
        local_ai_route_decision(&request),
        LocalAiRouteDecision::NativeFoundry
    );
}

#[test]
fn local_ai_route_decision_marks_explicit_windows_ai_for_native_probe() {
    let request = QuickTranslateServiceRequest {
        query_id: 133,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert_eq!(
        local_ai_route_decision(&request),
        LocalAiRouteDecision::ProbeWindowsAi
    );
}

#[test]
fn local_ai_route_decision_matrix_prefers_rust_native_paths_before_worker_bridge() {
    #[cfg(feature = "retained-dotnet-workers")]
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    #[cfg(feature = "retained-dotnet-workers")]
    let _runtime_profile = EnvironmentVariableGuard::set("EASYDICT_RUNTIME_PROFILE", "hybrid");
    #[cfg(feature = "retained-dotnet-workers")]
    let _generic_runtime_profile = EnvironmentVariableGuard::remove("RUNTIME_PROFILE");

    let openvino_missing_cache = unique_temp_dir("easydict-local-ai-route-matrix-openvino-missing");
    fs::create_dir_all(&openvino_missing_cache).expect("OpenVINO missing cache dir should exist");
    let openvino_missing_cache_string = path_string(&openvino_missing_cache);
    let openvino_cache = unique_temp_dir("easydict-local-ai-route-matrix-openvino");
    install_open_vino_cache(&openvino_cache);
    let openvino_cache_string = path_string(&openvino_cache);

    struct RouteCase<'a> {
        name: &'static str,
        service_id: &'static str,
        provider_mode: Option<&'static str>,
        execution_kind: QuickTranslateExecutionKind,
        from: &'static str,
        to: &'static str,
        foundry_endpoint: Option<&'static str>,
        foundry_model: Option<&'static str>,
        cache_dir: Option<&'a str>,
        expected: LocalAiRouteDecision,
    }

    let cases = [
        RouteCase {
            name: "non LocalAI service is ignored",
            service_id: "google",
            provider_mode: Some(local_ai_provider_modes::AUTO),
            execution_kind: QuickTranslateExecutionKind::Translate,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: None,
            expected: LocalAiRouteDecision::NotLocalAi,
        },
        RouteCase {
            name: "Auto translate probes WindowsAI first",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::AUTO),
            execution_kind: QuickTranslateExecutionKind::Translate,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: None,
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::ProbeWindowsAi,
        },
        RouteCase {
            name: "Auto streaming probes WindowsAI first",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::AUTO),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: None,
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::ProbeWindowsAi,
        },
        RouteCase {
            name: "explicit WindowsAI streaming uses native probe",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::WINDOWS_AI),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: None,
            expected: LocalAiRouteDecision::ProbeWindowsAi,
        },
        RouteCase {
            name: "Auto grammar correction probes WindowsAI first",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::AUTO),
            execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
            from: "en",
            to: "en",
            foundry_endpoint: None,
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::ProbeWindowsAi,
        },
        RouteCase {
            name: "Auto configured Foundry endpoint routes natively",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::AUTO),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: Some("http://127.0.0.1:5273/v1/chat/completions"),
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::NativeFoundry,
        },
        RouteCase {
            name: "explicit Foundry endpoint routes natively",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::FOUNDRY_LOCAL),
            execution_kind: QuickTranslateExecutionKind::Translate,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: Some("http://127.0.0.1:5273/v1/chat/completions"),
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::NativeFoundry,
        },
        RouteCase {
            name: "explicit Foundry without endpoint stays native for resolver",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::FOUNDRY_LOCAL),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: None,
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::NativeFoundry,
        },
        RouteCase {
            name: "explicit Foundry grammar correction stays native",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::FOUNDRY_LOCAL),
            execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
            from: "en",
            to: "en",
            foundry_endpoint: Some("http://127.0.0.1:5273/v1/chat/completions"),
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::NativeFoundry,
        },
        RouteCase {
            name: "explicit WindowsAI grammar correction uses native probe",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::WINDOWS_AI),
            execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
            from: "en",
            to: "en",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: None,
            expected: LocalAiRouteDecision::ProbeWindowsAi,
        },
        RouteCase {
            name: "OpenVINO cache miss fails locally",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::OPENVINO),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: Some(openvino_missing_cache_string.as_str()),
            expected: LocalAiRouteDecision::LocalError(
                "OpenVINO runtime or NLLB-200 model is not downloaded. Open Settings -> Services and click \"Download model\".",
            ),
        },
        RouteCase {
            name: "OpenVINO with ready cache routes to native runtime",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::OPENVINO),
            execution_kind: QuickTranslateExecutionKind::Translate,
            from: "en",
            to: "zh-Hans",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: Some(openvino_cache_string.as_str()),
            expected: LocalAiRouteDecision::NativeOpenVino,
        },
        RouteCase {
            name: "OpenVINO grammar fails locally",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::OPENVINO),
            execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
            from: "en",
            to: "en",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: Some(openvino_cache_string.as_str()),
            expected: LocalAiRouteDecision::LocalError(
                "No local AI provider supports grammar correction for this language",
            ),
        },
        RouteCase {
            name: "Auto target Auto fails locally before worker fallback",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::AUTO),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "auto",
            foundry_endpoint: None,
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::LocalError(
                "No local AI provider supports this language pair",
            ),
        },
        RouteCase {
            name: "WindowsAI target Auto fails locally before worker fallback",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::WINDOWS_AI),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "auto",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: None,
            expected: LocalAiRouteDecision::LocalError(
                "No local AI provider supports this language pair",
            ),
        },
        RouteCase {
            name: "Foundry target Auto fails locally before worker fallback",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::FOUNDRY_LOCAL),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "auto",
            foundry_endpoint: Some("http://127.0.0.1:5273/v1/chat/completions"),
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::LocalError(
                "No local AI provider supports this language pair",
            ),
        },
        RouteCase {
            name: "OpenVINO target Auto fails locally before worker fallback",
            service_id: "windows-local-ai",
            provider_mode: Some(local_ai_provider_modes::OPENVINO),
            execution_kind: QuickTranslateExecutionKind::TranslateStream,
            from: "en",
            to: "auto",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: Some(openvino_cache_string.as_str()),
            expected: LocalAiRouteDecision::LocalError(
                "No local AI provider supports this language pair",
            ),
        },
    ];

    for (index, case) in cases.iter().enumerate() {
        let request = local_ai_route_matrix_request(
            140 + index as u64,
            case.service_id,
            case.provider_mode,
            case.execution_kind,
            case.from,
            case.to,
            case.foundry_endpoint,
            case.foundry_model,
            case.cache_dir,
        );

        assert_eq!(
            local_ai_route_decision(&request),
            case.expected,
            "{}",
            case.name
        );

        #[cfg(feature = "retained-dotnet-workers")]
        {
            let retained_decision = local_ai_route_decision_with_worker_policy(
                &request,
                RetainedWorkerPolicy::all_enabled(),
            );
            assert_eq!(retained_decision, case.expected, "{}", case.name);
            assert!(
                !matches!(
                    retained_decision,
                    LocalAiRouteDecision::RetainedWorkerCompat
                ),
                "{} unexpectedly fell back to retained LocalAI worker",
                case.name
            );
        }
    }

    fs::remove_dir_all(&openvino_missing_cache)
        .expect("OpenVINO missing cache fixture should be removed");
    fs::remove_dir_all(&openvino_cache).expect("OpenVINO cache fixture should be removed");
}

#[test]
fn local_ai_provider_aliases_route_to_native_boundaries_without_worker_bridge() {
    let openvino_missing_cache = unique_temp_dir("easydict-local-ai-provider-alias-openvino");
    fs::create_dir_all(&openvino_missing_cache).expect("OpenVINO missing cache dir should exist");
    let openvino_missing_cache_string = path_string(&openvino_missing_cache);

    struct AliasCase<'a> {
        provider_mode: &'static str,
        foundry_endpoint: Option<&'static str>,
        foundry_model: Option<&'static str>,
        cache_dir: Option<&'a str>,
        expected: LocalAiRouteDecision,
    }

    let cases = [
        AliasCase {
            provider_mode: "windows_ai",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: None,
            expected: LocalAiRouteDecision::ProbeWindowsAi,
        },
        AliasCase {
            provider_mode: "windows-ai",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: None,
            expected: LocalAiRouteDecision::ProbeWindowsAi,
        },
        AliasCase {
            provider_mode: "phi_silica",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: None,
            expected: LocalAiRouteDecision::ProbeWindowsAi,
        },
        AliasCase {
            provider_mode: "foundry_local",
            foundry_endpoint: None,
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::NativeFoundry,
        },
        AliasCase {
            provider_mode: "foundry-local",
            foundry_endpoint: Some("http://127.0.0.1:5273/v1/chat/completions"),
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::NativeFoundry,
        },
        AliasCase {
            provider_mode: "local-ai",
            foundry_endpoint: None,
            foundry_model: Some("qwen2.5-0.5b"),
            cache_dir: None,
            expected: LocalAiRouteDecision::NativeFoundry,
        },
        AliasCase {
            provider_mode: "open_vino",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: Some(openvino_missing_cache_string.as_str()),
            expected: LocalAiRouteDecision::LocalError(
                "OpenVINO runtime or NLLB-200 model is not downloaded. Open Settings -> Services and click \"Download model\".",
            ),
        },
        AliasCase {
            provider_mode: "open-vino",
            foundry_endpoint: None,
            foundry_model: None,
            cache_dir: Some(openvino_missing_cache_string.as_str()),
            expected: LocalAiRouteDecision::LocalError(
                "OpenVINO runtime or NLLB-200 model is not downloaded. Open Settings -> Services and click \"Download model\".",
            ),
        },
    ];

    for (index, case) in cases.iter().enumerate() {
        let request = local_ai_route_matrix_request(
            180 + index as u64,
            "windows-local-ai",
            Some(case.provider_mode),
            QuickTranslateExecutionKind::TranslateStream,
            "en",
            "zh-Hans",
            case.foundry_endpoint,
            case.foundry_model,
            case.cache_dir,
        );

        assert_eq!(
            local_ai_route_decision(&request),
            case.expected,
            "provider alias {}",
            case.provider_mode
        );

        #[cfg(feature = "retained-dotnet-workers")]
        {
            let retained_decision = local_ai_route_decision_with_worker_policy(
                &request,
                RetainedWorkerPolicy::all_enabled(),
            );
            assert_eq!(
                retained_decision, case.expected,
                "provider alias {}",
                case.provider_mode
            );
            assert!(
                !matches!(
                    retained_decision,
                    LocalAiRouteDecision::RetainedWorkerCompat
                ),
                "provider alias {} unexpectedly fell back to retained LocalAI worker",
                case.provider_mode
            );
        }
    }

    fs::remove_dir_all(&openvino_missing_cache)
        .expect("OpenVINO missing cache fixture should be removed");
}

#[cfg(not(feature = "retained-dotnet-workers"))]
#[test]
fn local_ai_route_decision_keeps_worker_compat_unreachable_without_retained_feature() {
    let request = QuickTranslateServiceRequest {
        query_id: 134,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert_eq!(
        local_ai_route_decision(&request),
        LocalAiRouteDecision::ProbeWindowsAi
    );
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn local_ai_route_decision_keeps_windows_ai_native_probe_with_retained_feature() {
    let request = QuickTranslateServiceRequest {
        query_id: 135,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert_eq!(
        local_ai_route_decision_with_worker_policy(&request, RetainedWorkerPolicy::all_enabled()),
        LocalAiRouteDecision::ProbeWindowsAi
    );
}

#[test]
fn auto_foundry_local_probe_request_preserves_worker_route_when_endpoint_missing() {
    let request = QuickTranslateServiceRequest {
        query_id: 130,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };
    let mut resolver = RecordingFoundryLocalEndpointResolver::new([Ok(None)]);

    let native_request = auto_foundry_local_native_probe_request(&request, &mut resolver);

    assert!(native_request.is_none());
    assert_eq!(resolver.calls, 1);
    assert!(!quick_translate_request_can_route_natively(&request));
}

#[test]
fn auto_local_ai_probes_windows_ai_before_foundry_endpoint_fallback() {
    let temp_dir = unique_temp_dir("easydict-auto-local-ai-windows-ai-probe-order");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 136,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };
    let mut windows_ai_probe =
        RecordingWindowsAiProbe::new([WindowsAiReadyState::NotSupportedOnCurrentSystem]);
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(None)]);

    let update = run_quick_translate_service_with_app_dir_and_native_local_ai_probes(
        request,
        &temp_dir,
        &mut windows_ai_probe,
        &mut foundry_resolver,
    );
    let error = update
        .outcome
        .result
        .expect_err("Auto LocalAI should continue after unsupported WindowsAI and fail locally");

    assert_eq!(windows_ai_probe.ready_state_calls, 1);
    assert_eq!(foundry_resolver.calls, 1);
    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains(".NET"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn explicit_windows_ai_probe_reports_phi_status_without_worker_lookup() {
    let temp_dir = unique_temp_dir("easydict-explicit-windows-ai-native-probe");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 137,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };
    let mut windows_ai_probe =
        RecordingWindowsAiProbe::new([WindowsAiReadyState::CapabilityMissing]);
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://127.0.0.1:5273/v1/chat/completions".to_string(),
    ))]);

    let update = run_quick_translate_service_with_app_dir_and_native_local_ai_probes(
        request,
        &temp_dir,
        &mut windows_ai_probe,
        &mut foundry_resolver,
    );
    let error = update
        .outcome
        .result
        .expect_err("explicit WindowsAI should fail after Rust-native Phi probe");

    assert_eq!(windows_ai_probe.ready_state_calls, 1);
    assert_eq!(
        foundry_resolver.calls, 0,
        "explicit WindowsAI must not fall back to Foundry Local"
    );
    assert!(error.message.contains("systemAIModels"));
    assert!(error
        .message
        .contains("requires a Rust-native Phi Silica generation route"));
    assert!(!error.message.contains(".NET"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn explicit_windows_ai_client_routes_translation_natively_without_foundry_fallback() {
    let temp_dir = unique_temp_dir("easydict-explicit-windows-ai-native-client");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 138,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: Some("Use concise wording.".to_string()),
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };
    let mut windows_ai_client = RecordingWindowsAiClient::with_stream_responses(
        [WindowsAiReadyState::Ready],
        [Ok(vec!["你".to_string(), "好".to_string()])],
    );
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://127.0.0.1:5273/v1/chat/completions".to_string(),
    ))]);

    let update = run_quick_translate_service_with_app_dir_and_native_local_ai_client(
        request,
        &temp_dir,
        &mut windows_ai_client,
        &mut foundry_resolver,
    );

    let result = update
        .outcome
        .result
        .expect("explicit WindowsAI should use the injected Rust-native Phi client");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    assert_eq!(result.service_name.as_deref(), Some("Phi Silica"));
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    assert_eq!(
        update.outcome.streamed_chunks,
        vec!["你".to_string(), "好".to_string()]
    );
    assert_eq!(windows_ai_client.ready_state_calls, 1);
    assert_eq!(windows_ai_client.stream_prompts.len(), 1);
    assert!(windows_ai_client.stream_prompts[0].contains("Hello"));
    assert!(windows_ai_client.stream_prompts[0].contains("Use concise wording."));
    assert_eq!(
        foundry_resolver.calls, 0,
        "explicit WindowsAI must not fall back to Foundry Local"
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn auto_local_ai_ready_windows_ai_client_runs_before_foundry_fallback() {
    let temp_dir = unique_temp_dir("easydict-auto-windows-ai-ready-client");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 139,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };
    let mut windows_ai_client = RecordingWindowsAiClient::with_stream_responses(
        [WindowsAiReadyState::Ready, WindowsAiReadyState::Ready],
        [Ok(vec!["你好".to_string()])],
    );
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://127.0.0.1:5273/v1/chat/completions".to_string(),
    ))]);

    let update = run_quick_translate_service_with_app_dir_and_native_local_ai_client(
        request,
        &temp_dir,
        &mut windows_ai_client,
        &mut foundry_resolver,
    );

    let result = update
        .outcome
        .result
        .expect("Auto LocalAI should use ready WindowsAI before Foundry");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_name.as_deref(), Some("Phi Silica"));
    assert_eq!(windows_ai_client.ready_state_calls, 2);
    assert_eq!(windows_ai_client.stream_prompts.len(), 1);
    assert_eq!(
        foundry_resolver.calls, 0,
        "ready WindowsAI should win before Foundry Local discovery"
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn auto_local_ai_not_ready_windows_ai_client_continues_to_foundry_and_openvino_fallbacks() {
    let temp_dir = unique_temp_dir("easydict-auto-windows-ai-not-ready-fallback");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 140,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };
    let mut windows_ai_client =
        RecordingWindowsAiClient::with_stream_responses([WindowsAiReadyState::NotReady], []);
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(None)]);

    let update = run_quick_translate_service_with_app_dir_and_native_local_ai_client(
        request,
        &temp_dir,
        &mut windows_ai_client,
        &mut foundry_resolver,
    );
    let error = update
        .outcome
        .result
        .expect_err("Auto should continue past a not-ready WindowsAI client");

    assert_eq!(windows_ai_client.ready_state_calls, 1);
    assert!(windows_ai_client.stream_prompts.is_empty());
    assert_eq!(foundry_resolver.calls, 1);
    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn streaming_explicit_windows_ai_client_emits_phi_chunks() {
    let temp_dir = unique_temp_dir("easydict-streaming-explicit-windows-ai-client");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 141,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };
    let mut windows_ai_client = RecordingWindowsAiClient::with_stream_responses(
        [WindowsAiReadyState::Ready],
        [Ok(vec!["你".to_string(), "好".to_string()])],
    );
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://127.0.0.1:5273/v1/chat/completions".to_string(),
    ))]);
    let (sender, mut receiver) = unbounded();

    let update = run_quick_translate_streaming_service_with_app_dir_and_native_local_ai_client(
        request,
        &temp_dir,
        &sender,
        &mut windows_ai_client,
        &mut foundry_resolver,
    );

    let result = update
        .outcome
        .result
        .expect("streaming explicit WindowsAI should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(
        update.outcome.streamed_chunks,
        vec!["你".to_string(), "好".to_string()]
    );
    match receiver.try_recv() {
        Ok(Message::QuickTranslateStreamChunk(chunk)) => assert_eq!(chunk.text, "你"),
        other => panic!("expected first WindowsAI stream chunk, got {other:?}"),
    }
    match receiver.try_recv() {
        Ok(Message::QuickTranslateStreamChunk(chunk)) => assert_eq!(chunk.text, "好"),
        other => panic!("expected second WindowsAI stream chunk, got {other:?}"),
    }
    assert!(matches!(
        receiver.try_recv(),
        Err(TryRecvError::Empty | TryRecvError::Closed)
    ));
    assert_eq!(foundry_resolver.calls, 0);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn streaming_explicit_windows_ai_client_emits_first_phi_chunk_before_client_returns() {
    let temp_dir = unique_temp_dir("easydict-streaming-explicit-windows-ai-live-client");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 143,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };
    let (first_chunk_tx, first_chunk_rx) = std::sync::mpsc::channel();
    let (release_tx, release_rx) = std::sync::mpsc::channel();
    let mut windows_ai_client = BlockingWindowsAiStreamClient {
        first_chunk_tx,
        release_rx,
        ready_state_calls: 0,
        stream_prompts: Vec::new(),
    };
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://127.0.0.1:5273/v1/chat/completions".to_string(),
    ))]);
    let (sender, mut receiver) = unbounded();
    let thread_temp_dir = temp_dir.clone();

    let worker = std::thread::spawn(move || {
        let update = run_quick_translate_streaming_service_with_app_dir_and_native_local_ai_client(
            request,
            &thread_temp_dir,
            &sender,
            &mut windows_ai_client,
            &mut foundry_resolver,
        );
        (
            update,
            foundry_resolver.calls,
            windows_ai_client.ready_state_calls,
            windows_ai_client.stream_prompts,
        )
    });

    first_chunk_rx
        .recv_timeout(std::time::Duration::from_secs(10))
        .expect("first Phi chunk should be emitted before the client returns");
    match receiver.try_recv() {
        Ok(Message::QuickTranslateStreamChunk(chunk)) => {
            assert_eq!(chunk.query_id, 143);
            assert_eq!(chunk.text, "你");
        }
        other => panic!("expected first live WindowsAI stream chunk, got {other:?}"),
    }

    release_tx
        .send(())
        .expect("blocking client should still wait for release");
    let (update, foundry_calls, ready_state_calls, stream_prompts) =
        worker.join().expect("stream worker should finish");

    match receiver.try_recv() {
        Ok(Message::QuickTranslateStreamChunk(chunk)) => assert_eq!(chunk.text, "好"),
        other => panic!("expected second WindowsAI stream chunk, got {other:?}"),
    }
    assert!(matches!(
        receiver.try_recv(),
        Err(TryRecvError::Empty | TryRecvError::Closed)
    ));
    assert_eq!(update.outcome.streamed_chunks, ["你", "好"]);
    assert_eq!(
        update
            .outcome
            .result
            .expect("streaming explicit WindowsAI should succeed")
            .translated_text,
        "你好"
    );
    assert_eq!(foundry_calls, 0);
    assert_eq!(ready_state_calls, 1);
    assert_eq!(stream_prompts.len(), 1);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn explicit_windows_ai_client_routes_grammar_natively() {
    let temp_dir = unique_temp_dir("easydict-explicit-windows-ai-grammar-client");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 142,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::GrammarCorrection,
        execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
        params: TranslateParams {
            text: "He go home.".to_string(),
            from: Some("en".to_string()),
            to: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: Some(GrammarCorrectParams {
            text: "He go home.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            include_explanations: true,
        }),
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };
    let mut windows_ai_client = RecordingWindowsAiClient::with_stream_responses(
        [WindowsAiReadyState::Ready],
        [Ok(vec![
            "[CORRECTED]He goes home.[/CORRECTED]\n".to_string(),
            "[EXPLANATION]Subject-verb agreement.[/EXPLANATION]".to_string(),
        ])],
    );
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://127.0.0.1:5273/v1/chat/completions".to_string(),
    ))]);

    let update = run_quick_translate_service_with_app_dir_and_native_local_ai_client(
        request,
        &temp_dir,
        &mut windows_ai_client,
        &mut foundry_resolver,
    );

    let result = update
        .outcome
        .result
        .expect("explicit WindowsAI grammar should use the injected client");
    assert_eq!(result.translated_text, "He goes home.");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    assert_eq!(result.service_name.as_deref(), Some("Phi Silica"));
    let grammar_result = update
        .outcome
        .grammar_result
        .expect("grammar preview should be retained");
    assert_eq!(grammar_result.corrected_text, "He goes home.");
    assert_eq!(
        grammar_result.explanation.as_deref(),
        Some("Subject-verb agreement.")
    );
    assert!(grammar_result.has_corrections);
    assert_eq!(windows_ai_client.stream_prompts.len(), 1);
    assert!(windows_ai_client.stream_prompts[0].contains("Correct the grammar"));
    assert_eq!(foundry_resolver.calls, 0);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_openai_quick_translate_discovers_foundry_local_endpoint_when_empty() {
    let request = QuickTranslateServiceRequest {
        query_id: 27,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::FOUNDRY_LOCAL.to_string()),
            foundry_local_model: Some("phi-3-mini".to_string()),
            ..SettingsSnapshot::default()
        },
    };
    let mut backend = NativeOpenAiQuickTranslateBackend::with_foundry_local_endpoint_resolver(
        RecordingOpenAiHttpClient::with_responses([Ok(chat_completion_sse(&["你好"]))]),
        RecordingFoundryLocalEndpointResolver::new([Ok(Some(
            "http://localhost:5273/openai/status".to_string(),
        ))]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("native Foundry Local endpoint discovery should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    assert_eq!(backend.foundry_local_endpoint_resolver().calls, 1);
    assert_eq!(backend.foundry_local_endpoint_resolver().status_calls, 2);
    assert_eq!(backend.foundry_local_endpoint_resolver().start_calls, 1);
    assert_eq!(
        backend.foundry_local_endpoint_resolver().load_model_calls,
        vec!["phi-3-mini".to_string()]
    );
    let requests = &backend.http_client().requests;
    assert_eq!(
        requests[0].endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(requests[0].body["model"], "phi-3-mini");
}

#[test]
fn native_openai_quick_translate_discovers_foundry_local_grammar_endpoint_when_empty() {
    let request = QuickTranslateServiceRequest {
        query_id: 128,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::GrammarCorrection,
        execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
        params: TranslateParams {
            text: "He go home.".to_string(),
            from: Some("en".to_string()),
            to: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: Some(GrammarCorrectParams {
            text: "He go home.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            include_explanations: true,
        }),
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::FOUNDRY_LOCAL.to_string()),
            foundry_local_model: Some("phi-3-mini".to_string()),
            ..SettingsSnapshot::default()
        },
    };
    assert!(quick_translate_request_can_route_natively(&request));
    let mut backend = NativeOpenAiQuickTranslateBackend::with_foundry_local_endpoint_resolver(
        RecordingOpenAiHttpClient::with_responses([Ok(chat_completion_sse(&[
            "[CORRECTED]He goes home.[/CORRECTED]\n\
             [EXPLANATION]Subject-verb agreement.[/EXPLANATION]",
        ]))]),
        RecordingFoundryLocalEndpointResolver::new([Ok(Some(
            "http://localhost:5273/openai/status".to_string(),
        ))]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("explicit Foundry Local grammar should discover the endpoint natively");
    assert_eq!(result.translated_text, "He goes home.");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    let grammar_result = update
        .outcome
        .grammar_result
        .expect("grammar details should be parsed from the native stream");
    assert_eq!(grammar_result.corrected_text, "He goes home.");
    assert_eq!(
        grammar_result.explanation.as_deref(),
        Some("Subject-verb agreement.")
    );
    assert_eq!(backend.foundry_local_endpoint_resolver().calls, 1);
    let requests = &backend.http_client().requests;
    assert_eq!(
        requests[0].endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(requests[0].body["model"], "phi-3-mini");
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn local_ai_worker_backend_stream_uses_dedicated_worker_and_maps_languages() {
    let request = QuickTranslateServiceRequest {
        query_id: 28,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: Some("Use concise wording.".to_string()),
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            ..SettingsSnapshot::default()
        },
    };
    let mut backend = LocalAiWorkerQuickTranslateBackend::new(mock_local_ai_worker_facade());

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("local AI dedicated worker stream should succeed");
    assert_eq!(
        result.translated_text,
        "English>SimplifiedChinese>Auto>Use concise wording."
    );
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    assert_eq!(
        update.outcome.streamed_chunks,
        vec!["English>SimplifiedChinese>Auto>Use concise wording.".to_string()]
    );
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn local_ai_worker_backend_translate_reuses_stream_worker() {
    let request = QuickTranslateServiceRequest {
        query_id: 128,
        service: quick_service("windows-local-ai", "Windows Local AI", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };
    let mut backend = LocalAiWorkerQuickTranslateBackend::new(mock_local_ai_worker_facade());

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("non-streaming local AI request should reuse stream worker");
    assert_eq!(
        result.translated_text,
        "English>SimplifiedChinese>WindowsAI"
    );
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    assert!(update.outcome.streamed_chunks.is_empty());
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn local_ai_worker_backend_grammar_uses_dedicated_worker_and_preserves_explanation_flag() {
    let request = QuickTranslateServiceRequest {
        query_id: 29,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::GrammarCorrection,
        execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
        params: TranslateParams {
            text: "I has an apple.".to_string(),
            from: Some("en".to_string()),
            to: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: Some(GrammarCorrectParams {
            text: "I has an apple.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            include_explanations: true,
        }),
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };
    let mut backend = LocalAiWorkerQuickTranslateBackend::new(mock_local_ai_worker_facade());

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("local AI dedicated grammar worker should succeed");
    assert_eq!(result.translated_text, "I have an apple.");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    let grammar_result = update
        .outcome
        .grammar_result
        .expect("grammar preview should be retained");
    assert_eq!(grammar_result.corrected_text, "I have an apple.");
    assert_eq!(grammar_result.explanation.as_deref(), Some("include=True"));
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn local_ai_worker_backend_stream_maps_extended_nllb_languages() {
    let request = QuickTranslateServiceRequest {
        query_id: 129,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Ahoj".to_string(),
            from: Some("sk-SK".to_string()),
            to: Some("lt-LT".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::OPENVINO.to_string()),
            ..SettingsSnapshot::default()
        },
    };
    let mut backend = LocalAiWorkerQuickTranslateBackend::new(mock_local_ai_worker_facade());

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("local AI bridge should receive extended NLLB language names");
    assert_eq!(result.translated_text, "Slovak>Lithuanian>OpenVINO");
    assert_eq!(result.service_id.as_deref(), Some("windows-local-ai"));
    assert_eq!(
        update.outcome.streamed_chunks,
        vec!["Slovak>Lithuanian>OpenVINO".to_string()]
    );
}

#[test]
fn native_openvino_backend_translates_with_nllb_translator() {
    let tokenizer = RecordingNllbTokenizer;
    let engine = RecordingNllbEngine {
        generated: vec![200, 201, 202],
        ..RecordingNllbEngine::default()
    };
    let translator = NllbTranslator::new(tokenizer, engine).with_max_new_tokens(3);
    let mut backend = NativeOpenVinoQuickTranslateBackend::new(translator);
    backend
        .configure(&SettingsSnapshot::default())
        .expect("OpenVINO backend configure should be a no-op");
    let params = TranslateParams {
        text: "Hello".to_string(),
        from: Some("en".to_string()),
        to: Some("zh-Hans".to_string()),
        services: Some(vec!["windows-local-ai".to_string()]),
        custom_prompt: None,
    };

    let stream = backend
        .translate_stream(&params)
        .expect("fake NLLB translator should translate");

    assert_eq!(stream.chunks, vec!["你", "好"]);
    assert_eq!(stream.result.translated_text, "你好");
    assert_eq!(
        stream.result.service_id.as_deref(),
        Some("windows-local-ai")
    );
    assert_eq!(
        stream.result.service_name.as_deref(),
        Some("OpenVINO (local NLLB)")
    );
    assert_eq!(
        backend.translator().engine().last_call.as_ref().unwrap(),
        &RecordingNllbEngineCall {
            input_ids: vec![101, 42, 2],
            forced_bos: 256001,
            max_new_tokens: 3,
        }
    );
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
            custom_prompt: None,
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
fn native_openai_quick_translate_rejects_dotnet_unsupported_language_without_http_request() {
    let request = QuickTranslateServiceRequest {
        query_id: 24,
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
            to: Some("ms".to_string()),
            services: Some(vec!["openai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: openai_settings(),
    };
    let mut backend = NativeOpenAiQuickTranslateBackend::new(RecordingOpenAiHttpClient::default());

    let update = run_quick_translate_service(&mut backend, &request);

    let error = update
        .outcome
        .result
        .expect_err("unsupported OpenAI language should fail locally");
    assert_eq!(
        error.message,
        "Language pair not supported: English -> Malay"
    );
    assert!(backend.http_client().requests.is_empty());
}

#[test]
fn native_openai_quick_translate_rejects_ollama_service_unsupported_language_without_http_request()
{
    let request = QuickTranslateServiceRequest {
        query_id: 25,
        service: QuickTranslateService {
            id: "ollama".to_string(),
            name: "Ollama".to_string(),
            enabled_query: true,
            grammar_capable: true,
            streaming_capable: true,
        },
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("uk".to_string()),
            services: Some(vec!["ollama".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };
    let mut backend = NativeOpenAiQuickTranslateBackend::new(RecordingOpenAiHttpClient::default());

    let update = run_quick_translate_service(&mut backend, &request);

    let error = update
        .outcome
        .result
        .expect_err("unsupported Ollama language should fail locally");
    assert_eq!(
        error.message,
        "Language pair not supported: English -> Ukrainian"
    );
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
            custom_prompt: None,
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
            custom_prompt: None,
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
            custom_prompt: None,
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
fn native_custom_streaming_quick_translate_rejects_doubao_unsupported_language_without_http_request(
) {
    let request = QuickTranslateServiceRequest {
        query_id: 28,
        service: quick_service("doubao", "Doubao", false, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("bn".to_string()),
            services: Some(vec!["doubao".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: doubao_settings(),
    };
    let mut backend = NativeCustomStreamingQuickTranslateBackend::new(
        RecordingCustomStreamingHttpClient::default(),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let error = update
        .outcome
        .result
        .expect_err("unsupported Doubao language should fail locally");
    assert_eq!(
        error.message,
        "Language pair not supported: English -> Bengali"
    );
    assert!(backend.http_client().requests.is_empty());
}

#[test]
fn native_traditional_http_quick_translate_supports_google_and_google_web() {
    let request = QuickTranslateServiceRequest {
        query_id: 29,
        service: quick_service("google", "Google Translate", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: None,
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["google".to_string()]),
            custom_prompt: None,
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

    let web_request = QuickTranslateServiceRequest {
        query_id: 29,
        service: quick_service("google_web", "Google Dict", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["google_web".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };
    let mut web_backend = NativeTraditionalHttpQuickTranslateBackend::new(
        RecordingTraditionalHttpClient::with_responses([Ok(
            r#"[[["你好","hello",null,"heh-loh"]],[],"en"]"#.to_string(),
        )]),
    );

    let web_update = run_quick_translate_service(&mut web_backend, &web_request);
    let web_result = web_update
        .outcome
        .result
        .expect("native Google Dict should succeed");
    assert_eq!(web_result.translated_text, "你好");
    assert_eq!(web_result.service_id.as_deref(), Some("google_web"));
    assert_eq!(web_result.service_name.as_deref(), Some("Google Dict"));
    assert!(web_result.word_result.unwrap().phonetics.is_some());
    let web_plan = &web_backend.http_client().requests[0];
    assert_eq!(web_plan.service_kind, TraditionalHttpServiceKind::GoogleWeb);
    assert!(!web_plan.endpoint.contains("dj=1"));
}

#[cfg(feature = "enable-linguee-service")]
#[test]
fn feature_enabled_linguee_catalog_entry_routes_to_native_traditional_http() {
    let descriptor =
        find_translation_service_descriptor("linguee").expect("Linguee feature should register");
    assert_eq!(descriptor.kind, TranslationServiceKind::Dictionary);

    let request = QuickTranslateServiceRequest {
        query_id: 30,
        service: quick_service(
            descriptor.service_id,
            descriptor.display_name,
            descriptor.grammar_capable,
            descriptor.streaming_capable,
        ),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("fr".to_string()),
            services: Some(vec![descriptor.service_id.to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };

    assert!(
        quick_translate_request_can_route_natively(&request),
        "feature-enabled Linguee should dispatch through the native traditional HTTP route"
    );

    let mut backend = NativeTraditionalHttpQuickTranslateBackend::new(
        RecordingTraditionalHttpClient::with_responses([Ok(
            r#"[{"translations":[{"text":"Bonjour"}]}]"#.to_string(),
        )]),
    );

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("feature-enabled Linguee native route should succeed");
    assert_eq!(result.translated_text, "Bonjour");
    assert_eq!(result.service_id.as_deref(), Some("linguee"));
    assert_eq!(result.service_name.as_deref(), Some("Linguee Dictionary"));

    let plan = &backend.http_client().requests[0];
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::Linguee);
    assert_eq!(plan.method, "GET");
    assert!(plan
        .endpoint
        .contains("linguee-api.fly.dev/api/v2/translations"));
    assert!(plan.endpoint.contains("query=Hello"));
    assert!(plan.endpoint.contains("src=en"));
    assert!(plan.endpoint.contains("dst=fr"));
}

#[test]
fn native_traditional_http_quick_translate_rejects_unsupported_language_without_http_request() {
    let request = QuickTranslateServiceRequest {
        query_id: 30,
        service: quick_service("google", "Google Translate", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("auto".to_string()),
            services: Some(vec!["google".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };
    let mut backend =
        NativeTraditionalHttpQuickTranslateBackend::new(RecordingTraditionalHttpClient::default());

    let update = run_quick_translate_service(&mut backend, &request);

    let error = update
        .outcome
        .result
        .expect_err("unsupported Google language should fail locally");
    assert_eq!(
        error.message,
        "Language pair not supported: English -> Auto"
    );
    assert!(backend.http_client().requests.is_empty());
}

#[test]
fn native_traditional_http_quick_translate_supports_caiyun_deepl_niutrans_volcano_and_youdao() {
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
            Ok(r#"{"errorCode":"0","translation":["你好"],"l":"en2zh-CHS"}"#.to_string()),
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
            custom_prompt: None,
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
            custom_prompt: None,
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
            custom_prompt: None,
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
            custom_prompt: None,
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

    let youdao_request = QuickTranslateServiceRequest {
        query_id: 33,
        service: quick_service("youdao", "Youdao", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["youdao".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            youdao_app_key: Some("youdao-key".to_string()),
            youdao_app_secret: Some("youdao-secret".to_string()),
            youdao_use_official_api: Some(true),
            ..SettingsSnapshot::default()
        },
    };

    let youdao_update = run_quick_translate_service(&mut backend, &youdao_request);
    let youdao = youdao_update
        .outcome
        .result
        .expect("native Youdao OpenAPI should succeed");
    assert_eq!(youdao.translated_text, "你好");
    assert_eq!(youdao.service_id.as_deref(), Some("youdao"));
    assert_eq!(youdao.detected_language.as_deref(), Some("en"));
    let youdao_plan = &backend.http_client().requests[4];
    assert_eq!(youdao_plan.method, "POST");
    assert_eq!(
        youdao_plan.service_kind,
        TraditionalHttpServiceKind::YoudaoOpenApi
    );
    let youdao_body = youdao_plan.body.as_deref().unwrap();
    assert!(youdao_body.contains("appKey=youdao-key"));
    assert!(youdao_body.contains("signType=v3"));
}

#[test]
fn native_traditional_http_quick_translate_routes_default_deepl_web_mode() {
    let mut backend = NativeTraditionalHttpQuickTranslateBackend::new(
        RecordingTraditionalHttpClient::with_responses([Ok(
            r#"{"result":{"texts":[{"text":"Hallo Welt"}],"lang":"EN"}}"#.to_string(),
        )]),
    );
    let request = QuickTranslateServiceRequest {
        query_id: 34,
        service: quick_service("deepl", "DeepL", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello world".to_string(),
            from: Some("en".to_string()),
            to: Some("de".to_string()),
            services: Some(vec!["deepl".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };

    let update = run_quick_translate_service(&mut backend, &request);
    let result = update
        .outcome
        .result
        .expect("native DeepL web should succeed by default");
    assert_eq!(result.translated_text, "Hallo Welt");
    assert_eq!(result.service_id.as_deref(), Some("deepl"));
    assert_eq!(result.service_name.as_deref(), Some("DeepL"));
    assert_eq!(
        update.outcome.streamed_chunks,
        vec!["Hallo Welt".to_string()]
    );
    let plan = &backend.http_client().requests[0];
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::DeepLWeb);
    assert_eq!(plan.endpoint, "https://www2.deepl.com/jsonrpc");
    let body: serde_json::Value = serde_json::from_str(plan.body.as_deref().unwrap()).unwrap();
    assert_eq!(body["params"]["lang"]["source_lang_user_selected"], "EN");
    assert_eq!(body["params"]["lang"]["target_lang"], "DE");
}

#[test]
fn native_traditional_http_quick_translate_routes_default_youdao_web_mode() {
    let mut backend = NativeTraditionalHttpQuickTranslateBackend::new(
        RecordingTraditionalHttpClient::with_responses([Ok(
            r#"{"ec":{"word":{"trs":[{"pos":"int.","tran":"喂；你好"}]}}}"#.to_string(),
        )]),
    );
    let request = QuickTranslateServiceRequest {
        query_id: 34,
        service: quick_service("youdao", "Youdao", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["youdao".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };

    let update = run_quick_translate_service(&mut backend, &request);
    let result = update
        .outcome
        .result
        .expect("native Youdao web dictionary should succeed");
    assert_eq!(result.translated_text, "int. 喂；你好");
    assert_eq!(result.service_id.as_deref(), Some("youdao"));
    assert!(result.word_result.unwrap().definitions.is_some());
    assert_eq!(
        update.outcome.streamed_chunks,
        vec!["int. 喂；你好".to_string()]
    );
    let plan = &backend.http_client().requests[0];
    assert_eq!(plan.service_kind, TraditionalHttpServiceKind::YoudaoWebDict);
    assert!(plan
        .endpoint
        .contains("jsonapi_s?doctype=json&jsonversion=4"));
    assert!(plan.body.as_deref().unwrap().contains("keyfrom=webdict"));

    let mut sentence_backend = NativeTraditionalHttpQuickTranslateBackend::new(
        RecordingTraditionalHttpClient::with_responses([
            Ok(r#"{"code":0,"data":{"secretKey":"secret-key"}}"#.to_string()),
            Ok(
                r#"{"translateResult":[{"tgt":"句子翻译","src":"Hello world."}],"code":0}"#
                    .to_string(),
            ),
        ]),
    );
    let sentence_request = QuickTranslateServiceRequest {
        query_id: 35,
        params: TranslateParams {
            text: "Hello world. This should use the native Youdao webtranslate route.".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["youdao".to_string()]),
            custom_prompt: None,
        },
        ..request
    };
    let sentence_update = run_quick_translate_service(&mut sentence_backend, &sentence_request);
    let sentence_result = sentence_update
        .outcome
        .result
        .expect("native Youdao webtranslate should handle sentences");
    assert_eq!(sentence_result.translated_text, "句子翻译");
    assert_eq!(sentence_backend.http_client().requests.len(), 2);
    assert_eq!(
        sentence_backend.http_client().requests[0].service_kind,
        TraditionalHttpServiceKind::YoudaoWebTranslateKey
    );
    assert_eq!(
        sentence_backend.http_client().requests[1].service_kind,
        TraditionalHttpServiceKind::YoudaoWebTranslate
    );
}

struct FakeBingClient {
    html: String,
    response_body: String,
    requested_hosts: Vec<String>,
    translate_plans: Vec<TraditionalHttpRequestPlan>,
}

impl BingHttpClient for FakeBingClient {
    fn fetch_translator_html(
        &mut self,
        host: &str,
    ) -> Result<BingTranslatorPage, OpenAiExecutionError> {
        self.requested_hosts.push(host.to_string());
        Ok(BingTranslatorPage {
            html: self.html.clone(),
            resolved_host: host.to_string(),
        })
    }

    fn execute_translate(
        &mut self,
        plan: &TraditionalHttpRequestPlan,
    ) -> Result<BingHttpResponse, OpenAiExecutionError> {
        self.translate_plans.push(plan.clone());
        Ok(BingHttpResponse {
            status: 200,
            body: self.response_body.clone(),
        })
    }
}

#[test]
fn default_bing_catalog_entry_routes_to_native_two_phase_backend() {
    let descriptor =
        find_translation_service_descriptor("bing").expect("Bing should be in default catalog");
    assert_eq!(descriptor.kind, TranslationServiceKind::TextTranslation);

    let request = QuickTranslateServiceRequest {
        query_id: 39,
        service: quick_service(
            descriptor.service_id,
            descriptor.display_name,
            descriptor.grammar_capable,
            descriptor.streaming_capable,
        ),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec![descriptor.service_id.to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };

    assert!(
        quick_translate_request_can_route_natively(&request),
        "default Bing catalog entry should dispatch through the native Bing route"
    );

    let client = FakeBingClient {
        html: r#"<script>var _G={IG:"IGTOKEN1234"};</script>
            <div data-iid="translator.5028.3"></div>
            <script>params_AbusePreventionHelper=[1700000000000,"tok",3600000];</script>"#
            .to_string(),
        response_body:
            r#"[{"detectedLanguage":{"language":"en"},"translations":[{"text":"你好"}]}]"#
                .to_string(),
        requested_hosts: Vec::new(),
        translate_plans: Vec::new(),
    };
    let mut backend = NativeBingQuickTranslateBackend::new(client);

    let update = run_quick_translate_service(&mut backend, &request);

    let result = update
        .outcome
        .result
        .expect("default Bing native route should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("bing"));
    assert_eq!(result.service_name.as_deref(), Some("Bing Translate"));
    assert_eq!(backend.http_client().requested_hosts, ["www.bing.com"]);
    assert_eq!(backend.http_client().translate_plans.len(), 1);
    assert!(backend.http_client().translate_plans[0]
        .endpoint
        .starts_with("https://www.bing.com/ttranslatev3"));
}

#[test]
fn native_bing_quick_translate_backend_runs_two_phase_flow() {
    let client = FakeBingClient {
        html: r#"<script>var _G={IG:"IGTOKEN1234"};</script>
            <div data-iid="translator.5028.3"></div>
            <script>params_AbusePreventionHelper=[1700000000000,"tok",3600000];</script>"#
            .to_string(),
        response_body:
            r#"[{"detectedLanguage":{"language":"en"},"translations":[{"text":"你好"}]}]"#
                .to_string(),
        requested_hosts: Vec::new(),
        translate_plans: Vec::new(),
    };
    let mut backend = NativeBingQuickTranslateBackend::new(client);

    let request = QuickTranslateServiceRequest {
        query_id: 40,
        service: quick_service("bing", "Bing Translate", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["bing".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };

    let update = run_quick_translate_service(&mut backend, &request);
    let result = update.outcome.result.expect("native Bing should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(result.service_id.as_deref(), Some("bing"));
    assert_eq!(result.detected_language.as_deref(), Some("en"));
    assert_eq!(
        backend.http_client().requested_hosts,
        vec!["www.bing.com".to_string()]
    );
    assert!(backend.http_client().translate_plans[0]
        .endpoint
        .starts_with("https://www.bing.com/ttranslatev3"));
}

#[test]
fn native_bing_quick_translate_backend_uses_china_host_when_international_services_are_disabled() {
    let client = FakeBingClient {
        html: r#"<script>var _G={IG:"IGTOKEN1234"};</script>
            <div data-iid="translator.5028.3"></div>
            <script>params_AbusePreventionHelper=[1700000000000,"tok",3600000];</script>"#
            .to_string(),
        response_body:
            r#"[{"detectedLanguage":{"language":"en"},"translations":[{"text":"你好"}]}]"#
                .to_string(),
        requested_hosts: Vec::new(),
        translate_plans: Vec::new(),
    };
    let mut backend = NativeBingQuickTranslateBackend::new(client);
    let mut settings = SettingsSnapshot::default();
    settings.enable_international_services = Some(false);

    let request = QuickTranslateServiceRequest {
        query_id: 41,
        service: quick_service("bing", "Bing Translate", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["bing".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings,
    };

    let update = run_quick_translate_service(&mut backend, &request);
    let result = update.outcome.result.expect("native Bing should succeed");
    assert_eq!(result.translated_text, "你好");
    assert_eq!(
        backend.http_client().requested_hosts,
        vec!["cn.bing.com".to_string()]
    );
    assert!(backend.http_client().translate_plans[0]
        .endpoint
        .starts_with("https://cn.bing.com/ttranslatev3"));
}

#[test]
fn native_bing_rejects_unsupported_language_before_fetching_session_page() {
    let client = FakeBingClient {
        html: String::new(),
        response_body: String::new(),
        requested_hosts: Vec::new(),
        translate_plans: Vec::new(),
    };
    let mut backend = NativeBingQuickTranslateBackend::new(client);
    let request = QuickTranslateServiceRequest {
        query_id: 42,
        service: quick_service("bing", "Bing Translate", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("auto".to_string()),
            services: Some(vec!["bing".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };

    let update = run_quick_translate_service(&mut backend, &request);

    let error = update
        .outcome
        .result
        .expect_err("unsupported Bing language should fail locally");
    assert_eq!(
        error.message,
        "Language pair not supported: English -> Auto"
    );
    assert!(
        backend.http_client().requested_hosts.is_empty(),
        "Bing session page should not be fetched for an invalid language pair"
    );
    assert!(
        backend.http_client().translate_plans.is_empty(),
        "Bing translate request should not be built for an invalid language pair"
    );
}

#[test]
fn quick_translate_applies_and_renders_result_alternatives() {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello".to_string();
    state.results = vec![QuickTranslateResult::new("linguee", "Linguee Dictionary", true).into()];
    let plan = begin_quick_translate(&mut state).expect("translate should begin");

    let dto = TranslationResultDto {
        translated_text: "Hallo".to_string(),
        service_id: Some("linguee".to_string()),
        service_name: Some("Linguee Dictionary".to_string()),
        detected_language: None,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: Some(12),
        alternatives: Some(vec!["Servus".to_string(), "Hallöchen".to_string()]),
        word_result: None,
        raw_html: None,
    };
    let update = QuickTranslateServiceUpdate {
        query_id: plan.query_id,
        outcome: QuickTranslateServiceOutcome {
            service: plan.services[0].clone(),
            grammar_result: None,
            streamed_chunks: Vec::new(),
            result: Ok(dto),
        },
    };

    apply_quick_translate_service_update(&mut state, update);

    let preview = &state.results[0];
    assert_eq!(
        preview.alternatives.as_deref(),
        Some(&["Servus".to_string(), "Hallöchen".to_string()][..])
    );
    // The rendered body appends alternatives below the primary translation.
    let body = preview.result_body();
    assert!(body.starts_with("Hallo"));
    assert!(body.contains("Also: Servus; Hallöchen"));

    // A later query clears stale alternatives.
    state.source_text = "World".to_string();
    begin_quick_translate(&mut state).expect("second query begins");
    assert_eq!(state.results[0].alternatives, None);
}

#[test]
fn quick_translate_applies_renders_and_clears_word_result() {
    let mut state = EasydictUiState::default();
    state.source_text = "hello".to_string();
    state.results = vec![QuickTranslateResult::new("youdao", "Youdao", true).into()];
    let plan = begin_quick_translate(&mut state).expect("translate should begin");

    let dto = TranslationResultDto {
        translated_text: "hello".to_string(),
        service_id: Some("youdao".to_string()),
        service_name: Some("Youdao".to_string()),
        detected_language: Some("en".to_string()),
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: Some(18),
        alternatives: None,
        word_result: Some(WordResultDto {
            phonetics: Some(vec![PhoneticDto {
                text: Some("heh-loh".to_string()),
                audio_url: Some("https://dict.youdao.com/dictvoice?audio=hello".to_string()),
                accent: Some("US".to_string()),
            }]),
            definitions: Some(vec![DefinitionDto {
                part_of_speech: Some("int.".to_string()),
                meanings: Some(vec!["used as a greeting".to_string()]),
            }]),
            examples: Some(vec!["Hello, world.".to_string()]),
            word_forms: Some(vec![WordFormDto {
                name: Some("plural".to_string()),
                value: Some("hellos".to_string()),
            }]),
            synonyms: Some(vec![SynonymDto {
                part_of_speech: Some("n.".to_string()),
                meaning: Some("greeting".to_string()),
                words: Some(vec!["salutation".to_string(), "welcome".to_string()]),
            }]),
        }),
        raw_html: None,
    };
    let update = QuickTranslateServiceUpdate {
        query_id: plan.query_id,
        outcome: QuickTranslateServiceOutcome {
            service: plan.services[0].clone(),
            grammar_result: None,
            streamed_chunks: Vec::new(),
            result: Ok(dto),
        },
    };

    apply_quick_translate_service_update(&mut state, update);

    let preview = &state.results[0];
    assert!(preview.word_result.is_some());
    let body = preview.result_body();
    assert!(body.contains("Phonetics: US /heh-loh/"));
    assert!(body.contains("Definitions: int. used as a greeting"));
    assert!(body.contains("Examples: Hello, world."));
    assert!(body.contains("Forms: plural: hellos"));
    assert!(body.contains("Synonyms: n. greeting: salutation, welcome"));

    state.source_text = "world".to_string();
    begin_quick_translate(&mut state).expect("second query begins");
    assert_eq!(state.results[0].word_result, None);
}

#[test]
fn quick_translate_phonetic_enrichment_fetches_youdao_for_english_word_without_target_phonetics() {
    let request = phonetic_enrichment_request("en");
    let update = phonetic_enrichment_update(&request, "hello", None);
    let mut cache = PhoneticMemoryCache::new();
    let mut flights = PhoneticFlightTracker::default();
    let mut client = RecordingTraditionalHttpClient::with_responses([Ok(youdao_phonetic_json())]);

    let enriched = enrich_quick_translate_update_with_youdao_phonetics(
        &request,
        update,
        &mut cache,
        &mut flights,
        &mut client,
    );

    assert_eq!(client.requests.len(), 1);
    assert_eq!(
        client.requests[0].service_kind,
        TraditionalHttpServiceKind::YoudaoWebDict
    );
    assert!(client.requests[0]
        .body
        .as_deref()
        .is_some_and(|body| body.contains("q=hello")));
    let phonetics = enriched
        .outcome
        .result
        .expect("enriched result should stay successful")
        .word_result
        .expect("phonetic enrichment should create word result")
        .phonetics
        .expect("phonetics should be merged");
    assert_eq!(phonetics.len(), 2);
    assert_eq!(phonetics[0].accent.as_deref(), Some("US"));
    assert_eq!(phonetics[0].text.as_deref(), Some("həˈloʊ"));
    assert_eq!(phonetics[1].accent.as_deref(), Some("UK"));

    let cached = cache.get("hello").expect("phonetics should be cached");
    assert_eq!(cached.len(), 2);
}

#[test]
fn quick_translate_phonetic_enrichment_skips_non_english_sentence_and_existing_target_phonetics() {
    let mut cache = PhoneticMemoryCache::new();
    let mut flights = PhoneticFlightTracker::default();
    let mut client = RecordingTraditionalHttpClient::default();

    let non_english_request = phonetic_enrichment_request("zh-Hans");
    let non_english = enrich_quick_translate_update_with_youdao_phonetics(
        &non_english_request,
        phonetic_enrichment_update(&non_english_request, "你好", None),
        &mut cache,
        &mut flights,
        &mut client,
    );
    assert!(non_english.outcome.result.unwrap().word_result.is_none());

    let sentence_request = phonetic_enrichment_request("en");
    let sentence = enrich_quick_translate_update_with_youdao_phonetics(
        &sentence_request,
        phonetic_enrichment_update(&sentence_request, "Hello there.", None),
        &mut cache,
        &mut flights,
        &mut client,
    );
    assert!(sentence.outcome.result.unwrap().word_result.is_none());

    let existing_target = enrich_quick_translate_update_with_youdao_phonetics(
        &sentence_request,
        phonetic_enrichment_update(
            &sentence_request,
            "hello",
            Some(vec![PhoneticDto {
                text: Some("existing".to_string()),
                audio_url: None,
                accent: Some("US".to_string()),
            }]),
        ),
        &mut cache,
        &mut flights,
        &mut client,
    );
    let existing_phonetics = existing_target
        .outcome
        .result
        .unwrap()
        .word_result
        .unwrap()
        .phonetics
        .unwrap();
    assert_eq!(existing_phonetics.len(), 1);
    assert_eq!(existing_phonetics[0].text.as_deref(), Some("existing"));
    assert!(client.requests.is_empty());
}

#[test]
fn quick_translate_phonetic_enrichment_uses_phonetic_cache_before_youdao_request() {
    let request = phonetic_enrichment_request("en");
    let update = phonetic_enrichment_update(&request, "hello", None);
    let mut cache = PhoneticMemoryCache::new();
    cache.insert(
        "hello",
        vec![Phonetic {
            text: Some("cached".to_string()),
            audio_url: Some("https://example.invalid/cached.mp3".to_string()),
            accent: Some("US".to_string()),
        }],
    );
    let mut flights = PhoneticFlightTracker::default();
    let mut client = RecordingTraditionalHttpClient::default();

    let enriched = enrich_quick_translate_update_with_youdao_phonetics(
        &request,
        update,
        &mut cache,
        &mut flights,
        &mut client,
    );

    assert!(client.requests.is_empty());
    let phonetics = enriched
        .outcome
        .result
        .unwrap()
        .word_result
        .unwrap()
        .phonetics
        .unwrap();
    assert_eq!(phonetics.len(), 1);
    assert_eq!(phonetics[0].text.as_deref(), Some("cached"));
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
            custom_prompt: None,
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
fn pop_button_anchor_uses_dotnet_show_at_offset() {
    assert_eq!(
        PopButtonAnchor::new(400, 240).window_position_dips(),
        (408.0, 208.0)
    );
}

#[test]
fn pop_button_selection_text_ready_shows_pop_and_schedules_auto_dismiss() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::SelectionTextReady {
        text: "  selected text  ".to_string(),
        anchor_x: 400,
        anchor_y: 240,
        generation: 7,
    });

    assert_eq!(
        app.state.pop_button.pending_text.as_deref(),
        Some("selected text")
    );
    assert!(app.state.pop_button.visible);
    assert_eq!(app.state.pop_button.generation, 7);
    assert_eq!(app.state.pop_button.anchor.expect("anchor").x, 400);
    assert_eq!(app.state.pop_button.anchor.expect("anchor").y, 240);
    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::ShowAt { id, x, y }
            if id.as_str() == "pop-button" && *x == 408.0 && *y == 208.0
    )));
    assert!(contains_future_task(&task));
}

#[test]
fn pop_button_ignores_empty_and_stale_selection_text() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let empty = app.update(Message::SelectionTextReady {
        text: "   ".to_string(),
        anchor_x: 10,
        anchor_y: 20,
        generation: 1,
    });
    assert_eq!(app.state.pop_button.pending_text, None);
    assert!(!app.state.pop_button.visible);
    assert!(contains_window_command(&empty, |command| matches!(
        command,
        WindowCommand::Hide(id) if id.as_str() == "pop-button"
    )));

    app.update(Message::SelectionTextReady {
        text: "fresh".to_string(),
        anchor_x: 30,
        anchor_y: 40,
        generation: 3,
    });
    let stale = app.update(Message::SelectionTextReady {
        text: "stale".to_string(),
        anchor_x: 50,
        anchor_y: 60,
        generation: 2,
    });

    assert_eq!(app.state.pop_button.pending_text.as_deref(), Some("fresh"));
    assert_eq!(task_kind(&stale), "none");
}

#[test]
fn pop_button_click_translates_pending_text_in_mini_window() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::SelectionTextReady {
        text: "Bonjour from selection".to_string(),
        anchor_x: 200,
        anchor_y: 120,
        generation: 5,
    });

    let task = app.update(Message::PopButtonClicked);

    assert_eq!(app.state.pop_button.pending_text, None);
    assert!(!app.state.pop_button.visible);
    assert_eq!(app.state.mini.text, "Bonjour from selection");
    assert!(app.state.mini.is_translating);
    assert!(app.state.mini.active_query_id.is_some());
    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::Hide(id) if id.as_str() == "pop-button"
    )));
    assert!(contains_window_command(&task, |command| matches!(
        command,
        WindowCommand::Show(id) if id.as_str() == "mini"
    )));
    assert!(contains_platform_command(
        &task,
        &PlatformCommand::CaptureTextInsertionTarget
    ));
    assert!(contains_future_task(&task) || contains_stream_task(&task));
}

#[test]
fn pop_button_dismiss_and_auto_dismiss_clear_matching_generation() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };
    app.update(Message::SelectionTextReady {
        text: "dismiss me".to_string(),
        anchor_x: 10,
        anchor_y: 20,
        generation: 9,
    });

    let stale = app.update(Message::PopButtonAutoDismiss(8));
    assert_eq!(task_kind(&stale), "none");
    assert!(app.state.pop_button.visible);

    let auto = app.update(Message::PopButtonAutoDismiss(9));
    assert_eq!(app.state.pop_button.pending_text, None);
    assert!(!app.state.pop_button.visible);
    assert!(contains_window_command(&auto, |command| matches!(
        command,
        WindowCommand::Hide(id) if id.as_str() == "pop-button"
    )));

    app.update(Message::SelectionTextReady {
        text: "dismiss manually".to_string(),
        anchor_x: 10,
        anchor_y: 20,
        generation: 10,
    });
    let manual = app.update(Message::DismissPopButton);
    assert_eq!(app.state.pop_button.pending_text, None);
    assert!(!app.state.pop_button.visible);
    assert!(contains_window_command(&manual, |command| matches!(
        command,
        WindowCommand::Hide(id) if id.as_str() == "pop-button"
    )));
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
            arguments: browser_registrar_arguments("install"),
        })
    );

    let uninstall = app.update(Message::UninstallBrowserSupport);
    assert_eq!(
        platform_command(&uninstall),
        Some(PlatformCommand::RunBundledExecutable {
            executable_name: BROWSER_REGISTRAR_EXE.to_string(),
            arguments: browser_registrar_arguments("uninstall"),
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
    let discovered_after_gap = temp_dir.join("oxford.4.MDD");
    let ignored_non_numeric = temp_dir.join("Oxford.assets.mdd");
    fs::write(&mdx_path, b"mdx").expect("MDX file should be created");
    fs::write(&mdd_path, b"mdd").expect("MDD file should be created");
    fs::write(&first_numbered, b"mdd1").expect("numbered MDD file should be created");
    fs::write(&second_numbered, b"mdd2").expect("numbered MDD file should be created");
    fs::write(&discovered_after_gap, b"mdd4").expect("gap MDD file should be created");
    fs::write(&ignored_non_numeric, b"mdd-assets")
        .expect("non-numeric same-stem MDD file should be created");

    let mut state = EasydictUiState::default();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));

    let dictionary = &state.settings.imported_mdx_dictionaries[0];
    assert_eq!(
        dictionary.mdd_file_paths,
        vec![
            path_string(&mdd_path),
            path_string(&first_numbered),
            path_string(&second_numbered),
            path_string(&discovered_after_gap),
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
fn selected_mdx_dictionary_detects_encrypted_mdx_header() {
    let temp_dir = unique_temp_dir("easydict-mdx-encrypted-header");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Secure Dictionary.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));

    let dictionary = &state.settings.imported_mdx_dictionaries[0];
    assert_eq!(dictionary.service_id, "mdx::secure-dictionary");
    assert!(dictionary.is_encrypted);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn selected_mdx_dictionary_keeps_plain_mdx_header_unencrypted() {
    let temp_dir = unique_temp_dir("easydict-mdx-plain-header");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Plain Dictionary.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="No" />"#,
    );

    let mut state = EasydictUiState::default();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));

    let dictionary = &state.settings.imported_mdx_dictionaries[0];
    assert_eq!(dictionary.service_id, "mdx::plain-dictionary");
    assert!(!dictionary.is_encrypted);

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
        mdd_resources_inlined: false,
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
    assert_eq!(result.translated_text, "A fruit");
    assert!(!result.translated_text.contains("<div"));
    assert_eq!(result.raw_html, None);
    let definition = result
        .word_result
        .as_ref()
        .and_then(|word| word.definitions.as_ref())
        .and_then(|definitions| definitions.first())
        .expect("MDX result should expose readable dictionary definition");
    assert_eq!(definition.part_of_speech.as_deref(), Some("dictionary"));
    assert_eq!(
        definition.meanings.as_deref(),
        Some(&["A fruit".to_string()][..])
    );
}

#[test]
fn mdx_service_result_keeps_rich_html_only_when_mdd_resources_are_attached() {
    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    state.settings.imported_mdx_dictionaries[0].mdd_file_paths =
        vec![r"C:\Dicts\Demo Dictionary.mdd".to_string()];
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut backend = RecordingBackend::with_mdx_responses([Ok(MdxLookupResult {
        entries: vec![MdxLookupEntry {
            key: "apple".to_string(),
            html: r#"<div><span>A fruit</span><img src="data:image/png;base64,iVBORw=="></div>"#
                .to_string(),
            dictionary_name: Some("Demo Dictionary".to_string()),
        }],
        mdd_resources_inlined: true,
    })]);

    let outcome = run_quick_translate(&mut backend, &plan);
    let result = outcome.results[0].result.as_ref().expect("MDX result");

    assert_eq!(result.translated_text, "A fruit");
    assert!(!result.translated_text.contains("<span"));
    assert_eq!(
        result.raw_html.as_deref(),
        Some(r#"<div><span>A fruit</span><img src="data:image/png;base64,iVBORw=="></div>"#)
    );
    let raw_html = result.raw_html.clone();

    let update = QuickTranslateServiceUpdate {
        query_id: plan.query_id,
        outcome: outcome.results.into_iter().next().expect("service outcome"),
    };
    apply_quick_translate_service_update(&mut state, update);

    assert_eq!(state.results[0].body, "A fruit");
    assert_eq!(state.results[0].raw_html, raw_html);
    assert_eq!(
        state.results[0].result_body(),
        "A fruit\nDefinitions: dictionary A fruit"
    );

    state.source_text = "banana".to_string();
    begin_quick_translate(&mut state).expect("second query begins");
    assert_eq!(state.results[0].raw_html, None);
}

#[test]
fn mdx_service_result_drops_rich_html_when_mdd_resources_were_not_inlined() {
    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    state.settings.imported_mdx_dictionaries[0].mdd_file_paths =
        vec![r"C:\Dicts\missing.mdd".to_string()];
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut backend = RecordingBackend::with_mdx_responses([Ok(MdxLookupResult {
        entries: vec![MdxLookupEntry {
            key: "apple".to_string(),
            html: r#"<div><span>A fruit</span><img src="images/logo.png"></div>"#.to_string(),
            dictionary_name: Some("Demo Dictionary".to_string()),
        }],
        mdd_resources_inlined: false,
    })]);

    let outcome = run_quick_translate(&mut backend, &plan);
    let result = outcome.results[0].result.as_ref().expect("MDX result");

    assert_eq!(result.translated_text, "A fruit");
    assert_eq!(result.raw_html, None);
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
        mdd_resources_inlined: false,
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
fn unencrypted_mdx_service_routes_natively_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-native-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut requests = plan.service_requests();
    let request = requests.remove(0);

    assert!(quick_translate_request_can_route_natively(&request));

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("missing dictionary file should be reported by native MDX");

    assert!(error.message.contains("MDX dictionary file not found"));
    assert!(!error.message.contains("CompatHost"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn non_native_service_is_rejected_by_default_app_dir_route() {
    let temp_dir = unique_temp_dir("easydict-no-generic-compat-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 91,
        service: quick_service("legacy-dotnet", "Legacy .NET Service", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["legacy-dotnet".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    };

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("non-native service should fail locally");

    assert!(error.message.contains("Rust-native quick translate route"));
    assert!(!error.message.contains("CompatHost"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn auto_foundry_local_endpoint_routes_natively_without_local_ai_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-auto-foundry-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 92,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_endpoint: Some("foundry-local-invalid".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert!(quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("invalid endpoint should fail in the native OpenAI route");

    assert!(!error.message.contains("CompatHost"));
    assert!(!error.message.contains("LocalAi"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn auto_local_ai_without_foundry_endpoint_stays_on_local_ai_worker_route() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile = EnvironmentVariableGuard::set("EASYDICT_RUNTIME_PROFILE", "hybrid");
    let temp_dir = unique_temp_dir("easydict-auto-local-ai-empty-foundry-still-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 98,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(None)]);
    let update =
        run_quick_translate_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver(
            request,
            &temp_dir,
            RetainedWorkerPolicy::all_enabled(),
            &mut foundry_resolver,
        );
    let error = update
        .outcome
        .result
        .expect_err("Auto without a configured Foundry endpoint should preserve worker order");

    assert!(error.message.contains("Local AI worker"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn explicit_worker_policy_without_hybrid_runtime_profile_stays_rust_only() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile = EnvironmentVariableGuard::remove("EASYDICT_RUNTIME_PROFILE");
    let _generic_runtime_profile = EnvironmentVariableGuard::remove("RUNTIME_PROFILE");
    let temp_dir = unique_temp_dir("easydict-explicit-worker-policy-stays-rust-only");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 106,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(None)]);
    let update =
        run_quick_translate_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver(
            request,
            &temp_dir,
            RetainedWorkerPolicy::all_enabled(),
            &mut foundry_resolver,
        );
    let error = update
        .outcome
        .result
        .expect_err("injected worker policy must still require explicit hybrid runtime");

    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains("Local AI worker executable"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn packaged_auto_local_ai_without_foundry_or_openvino_cache_fails_locally_without_worker_probe() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _foundry_cli = EnvironmentVariableGuard::set(
        FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
        "__missing_foundry_cli__.cmd",
    );
    let temp_dir = unique_temp_dir("easydict-packaged-auto-local-ai-native-only");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 103,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("default packaged LocalAI should fail locally without worker probing");

    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains(".NET Local AI workers"));
    assert!(!error.message.contains("Local AI worker executable"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn streaming_app_dir_auto_local_ai_without_foundry_or_openvino_cache_fails_locally_without_worker_probe(
) {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile = EnvironmentVariableGuard::set("EASYDICT_RUNTIME_PROFILE", "hybrid");
    let _winrt_disabled = EnvironmentVariableGuard::set("EASYDICT_WINDOWS_AI_DISABLE_WINRT", "1");
    let temp_dir = unique_temp_dir("easydict-streaming-auto-local-ai-native-only");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 104,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(None)]);
    let (sender, mut receiver) = unbounded();
    let update = run_quick_translate_streaming_service_with_app_dir_and_foundry_resolver(
        request,
        &temp_dir,
        &sender,
        &mut foundry_resolver,
    );
    let error = update
        .outcome
        .result
        .expect_err("default streaming LocalAI should fail locally without worker probing");

    assert_eq!(foundry_resolver.calls, 1);
    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains(".NET Local AI workers"));
    assert!(!error.message.contains("Local AI worker executable"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));
    assert_eq!(receiver.try_recv(), Err(TryRecvError::Empty));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn streaming_foundry_resolver_helper_explicit_windows_ai_uses_default_native_client() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _winrt_disabled = EnvironmentVariableGuard::set("EASYDICT_WINDOWS_AI_DISABLE_WINRT", "1");
    let temp_dir = unique_temp_dir("easydict-streaming-explicit-windows-ai-default-client");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 105,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(Some(
        "http://127.0.0.1:5273/v1/chat/completions".to_string(),
    ))]);
    let (sender, mut receiver) = unbounded();
    let update = run_quick_translate_streaming_service_with_app_dir_and_foundry_resolver(
        request,
        &temp_dir,
        &sender,
        &mut foundry_resolver,
    );
    let error = update
        .outcome
        .result
        .expect_err("explicit WindowsAI should use the default Rust-native client");

    assert_eq!(foundry_resolver.calls, 0);
    assert!(error.message.contains("Phi Silica is not supported"));
    assert!(!error
        .message
        .contains("requires a Rust-native Phi Silica generation route"));
    assert!(!error.message.contains(".NET Local AI workers"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));
    assert_eq!(receiver.try_recv(), Err(TryRecvError::Empty));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[cfg(all(
    feature = "retained-dotnet-workers",
    target_os = "windows",
    target_arch = "x86_64"
))]
#[test]
fn auto_local_ai_without_foundry_endpoint_uses_cache_ready_native_openvino_before_worker() {
    let temp_dir = unique_temp_dir("easydict-auto-local-ai-cache-ready-native-openvino");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    install_open_vino_cache(&temp_dir);
    let request = QuickTranslateServiceRequest {
        query_id: 101,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(None)]);
    let update =
        run_quick_translate_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver(
            request,
            &temp_dir,
            RetainedWorkerPolicy::all_enabled(),
            &mut foundry_resolver,
        );
    let error = update
        .outcome
        .result
        .expect_err("fake Auto OpenVINO cache should fail inside the native ORT route");

    assert!(
        error.message.contains("tokenizer.json") || error.message.contains("onnxruntime.dll"),
        "unexpected Auto OpenVINO native route error: {}",
        error.message
    );
    assert!(!error.message.contains("Local AI worker"));
    assert!(!error.message.contains(".NET"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn auto_local_ai_without_foundry_endpoint_can_disable_retained_local_ai_worker_route() {
    let temp_dir = unique_temp_dir("easydict-auto-local-ai-retained-worker-disabled");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 100,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let mut foundry_resolver = RecordingFoundryLocalEndpointResolver::new([Ok(None)]);
    let update =
        run_quick_translate_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver(
            request,
            &temp_dir,
            RetainedWorkerPolicy::all_enabled().without_local_ai_worker(),
            &mut foundry_resolver,
        );
    let error = update
        .outcome
        .result
        .expect_err("disabled retained LocalAI worker should fail locally");

    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains(".NET Local AI workers"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));
    assert!(!error.message.contains("executable"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn packaged_local_ai_windows_ai_route_uses_native_client_without_worker_probe() {
    let _winrt_disabled = EnvironmentVariableGuard::set("EASYDICT_WINDOWS_AI_DISABLE_WINRT", "1");
    let temp_dir = unique_temp_dir("easydict-packaged-local-ai-windows-ai-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 102,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("WindowsAI should use the Rust-native client before worker probing");

    assert!(error.message.contains("Phi Silica is not supported"));
    assert!(!error
        .message
        .contains("requires a Rust-native Phi Silica generation route"));
    assert!(!error.message.contains(".NET Local AI workers"));
    assert!(!error.message.contains("Local AI worker executable"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn openvino_local_ai_grammar_fails_locally_without_local_ai_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-openvino-grammar-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 93,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::GrammarCorrection,
        execution_kind: QuickTranslateExecutionKind::GrammarCorrection,
        params: TranslateParams {
            text: "He go home.".to_string(),
            from: Some("en".to_string()),
            to: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: Some(GrammarCorrectParams {
            text: "He go home.".to_string(),
            language: Some("en".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            include_explanations: true,
        }),
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::OPENVINO.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("OpenVINO grammar should fail locally before LocalAI bridge startup");

    assert!(error
        .message
        .contains("No local AI provider supports grammar correction"));
    assert!(!error.message.contains("CompatHost executable not found"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn openvino_local_ai_target_auto_fails_locally_without_local_ai_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-openvino-target-auto-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 94,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("auto".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::OPENVINO.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("OpenVINO target Auto should fail locally before LocalAI bridge startup");

    assert!(error
        .message
        .contains("No local AI provider supports this language pair"));
    assert!(!error.message.contains("CompatHost executable not found"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn openvino_local_ai_unknown_target_language_fails_locally_without_worker() {
    let temp_dir = unique_temp_dir("easydict-openvino-unknown-target-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 100,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("hr".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::OPENVINO.to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("unknown OpenVINO target should fail before cache or worker startup");

    assert!(error
        .message
        .contains("No local AI provider supports this language pair"));
    assert!(!error
        .message
        .contains("OpenVINO runtime or NLLB-200 model is not downloaded"));
    assert!(!error.message.contains("Local AI worker"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn auto_foundry_target_auto_fails_locally_before_native_foundry_or_compat_host_startup() {
    let temp_dir = unique_temp_dir("easydict-auto-foundry-target-auto-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 96,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("auto".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_endpoint: Some("foundry-local-invalid".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert!(quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("LocalAI target Auto should fail before native Foundry execution");

    assert!(error
        .message
        .contains("No local AI provider supports this language pair"));
    assert!(!error.message.contains("foundry-local-invalid"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn auto_foundry_unknown_target_language_fails_locally_before_endpoint_use() {
    let temp_dir = unique_temp_dir("easydict-auto-foundry-unknown-target-no-endpoint");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 101,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("hr".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::AUTO.to_string()),
            foundry_local_endpoint: Some("foundry-local-invalid".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("unknown LocalAI target should fail before native Foundry execution");

    assert!(error
        .message
        .contains("No local AI provider supports this language pair"));
    assert!(!error.message.contains("foundry-local-invalid"));
    assert!(!error.message.contains("requires a Rust-native route"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn explicit_foundry_target_auto_fails_locally_before_native_foundry_or_worker_startup() {
    let temp_dir = unique_temp_dir("easydict-explicit-foundry-target-auto-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 99,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("auto".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::FOUNDRY_LOCAL.to_string()),
            foundry_local_endpoint: Some("foundry-local-invalid".to_string()),
            foundry_local_model: Some("qwen2.5-0.5b".to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert!(quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("explicit Foundry target Auto should fail before native endpoint use");

    assert!(error
        .message
        .contains("No local AI provider supports this language pair"));
    assert!(!error.message.contains("foundry-local-invalid"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));
    assert!(!error.message.contains("Local AI worker"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn windows_ai_unknown_source_language_fails_locally_before_worker_required_error() {
    let temp_dir = unique_temp_dir("easydict-windows-ai-unknown-source-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 102,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("hr".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("unknown WindowsAI source should fail before worker-required fallback");

    assert!(error
        .message
        .contains("No local AI provider supports this language pair"));
    assert!(!error.message.contains("requires a Rust-native route"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn windows_ai_target_auto_fails_locally_without_local_ai_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-windows-ai-target-auto-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 97,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("auto".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::WINDOWS_AI.to_string()),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("WindowsAI target Auto should fail before LocalAI bridge startup");

    assert!(error
        .message
        .contains("No local AI provider supports this language pair"));
    assert!(!error.message.contains("CompatHost executable not found"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn openvino_local_ai_supported_translation_without_cached_model_fails_locally_without_worker() {
    let temp_dir = unique_temp_dir("easydict-openvino-supported-cache-missing-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 95,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::OPENVINO.to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("OpenVINO cache miss should fail locally before LocalAI bridge startup");

    assert!(error
        .message
        .contains("OpenVINO runtime or NLLB-200 model is not downloaded"));
    assert!(error.message.contains("Download model"));
    assert!(!error.message.contains("Local AI worker"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn local_ai_provider_alias_open_vino_routes_to_openvino_preflight_without_worker() {
    let temp_dir = unique_temp_dir("easydict-openvino-alias-cache-missing-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = QuickTranslateServiceRequest {
        query_id: 116,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some("open_vino".to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(!quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("OpenVINO alias should fail in native preflight before worker startup");

    assert!(error
        .message
        .contains("OpenVINO runtime or NLLB-200 model is not downloaded"));
    assert!(!error.message.contains("Local AI worker"));
    assert!(!error.message.contains(".NET"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[cfg(all(target_os = "windows", target_arch = "x86_64"))]
#[test]
fn openvino_local_ai_supported_translation_with_cached_model_routes_to_native_ort_without_worker() {
    let temp_dir = unique_temp_dir("easydict-openvino-supported-cache-ready-native-ort");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    install_open_vino_cache(&temp_dir);
    let request = QuickTranslateServiceRequest {
        query_id: 98,
        service: quick_service("windows-local-ai", "Windows Local AI", true, true),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::TranslateStream,
        params: TranslateParams {
            text: "Hello".to_string(),
            from: Some("en".to_string()),
            to: Some("zh-Hans".to_string()),
            services: Some(vec!["windows-local-ai".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot {
            local_ai_provider: Some(local_ai_provider_modes::OPENVINO.to_string()),
            cache_dir: Some(path_string(&temp_dir)),
            ..SettingsSnapshot::default()
        },
    };

    assert!(quick_translate_request_can_route_natively(&request));
    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("fake OpenVINO cache should fail inside the Rust-native ORT route");

    assert!(
        error.message.contains("tokenizer.json") || error.message.contains("onnxruntime.dll"),
        "unexpected OpenVINO native route error: {}",
        error.message
    );
    assert!(!error.message.contains("Local AI worker"));
    assert!(!error.message.contains(".NET"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn encrypted_mdx_service_routes_natively_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-encrypted-native-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Secure Dictionary.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut requests = plan.service_requests();
    let request = requests.remove(0);

    assert!(quick_translate_request_can_route_natively(&request));

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("truncated encrypted MDX should fail in the native reader");
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn key_info_encrypted_mdx_service_routes_natively_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-key-info-native-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Key Info Dictionary.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="2" />"#,
    );

    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut requests = plan.service_requests();
    let request = requests.remove(0);

    assert!(quick_translate_request_can_route_natively(&request));

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("truncated test MDX should fail in the native reader");

    assert!(!error.message.contains("credentials are required"));
    assert!(!error.message.contains("CompatHost"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn unsupported_encrypted_mdx_service_fails_locally_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-unsupported-encrypted-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Combined Dictionary.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="3" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[0].regcode = Some(valid_mdx_regcode());
    state.settings.imported_mdx_dictionaries[0].email = Some("email@example.com".to_string());
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut requests = plan.service_requests();
    let request = requests.remove(0);

    assert!(!quick_translate_request_can_route_natively(&request));

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("unsupported encrypted MDX should fail locally");

    assert!(error
        .message
        .contains("not supported by the Rust-native MDX reader"));
    assert!(!error.message.contains("credentials are required"));
    assert!(!error.message.contains("CompatHost executable not found"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn encrypted_mdx_with_invalid_regcode_fails_locally_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-invalid-regcode-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Secure Dictionary.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[0].regcode = Some("not a base64 regcode".to_string());
    state.settings.imported_mdx_dictionaries[0].email = Some("email@example.com".to_string());
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut requests = plan.service_requests();
    let request = requests.remove(0);

    assert!(!quick_translate_request_can_route_natively(&request));

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("invalid encrypted MDX regcode should fail locally");

    assert!(error.message.contains("Base64"));
    assert!(!error.message.contains("CompatHost"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn encrypted_mdx_without_credentials_fails_locally_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-encrypted-no-creds-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Secure Dictionary.mdx".to_string(),
    )));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut requests = plan.service_requests();
    let request = requests.remove(0);

    assert!(!quick_translate_request_can_route_natively(&request));

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("encrypted MDX without credentials should fail locally");

    assert!(error.message.contains("credentials are required"));
    assert!(!error.message.contains("CompatHost"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn encrypted_mdx_with_credentials_missing_file_fails_locally_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-encrypted-missing-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut state = EasydictUiState::default();
    state.source_text = "apple".to_string();
    state.results = Vec::new();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Secure Dictionary.mdx".to_string(),
    )));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[0].regcode = Some(valid_mdx_regcode());
    state.settings.imported_mdx_dictionaries[0].email = Some("email@example.com".to_string());
    let plan = begin_quick_translate(&mut state).expect("MDX query should begin");
    let mut requests = plan.service_requests();
    let request = requests.remove(0);

    assert!(!quick_translate_request_can_route_natively(&request));

    let update = run_quick_translate_service_with_app_dir(request, &temp_dir);
    let error = update
        .outcome
        .result
        .expect_err("missing encrypted MDX should fail locally");

    assert!(error.message.contains("MDX dictionary file not found"));
    assert!(!error.message.contains("CompatHost"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
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
            mdd_resources_inlined: false,
        }),
        Ok(MdxLookupResult {
            entries: vec![MdxLookupEntry {
                key: "application".to_string(),
                html: "<div>application</div>".to_string(),
                dictionary_name: None,
            }],
            mdd_resources_inlined: false,
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
fn local_dictionary_suggestion_runner_uses_persistent_native_index_and_skips_fresh_key_reload() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-native-index");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Demo Dictionary.mdx");
    fs::write(
        &mdx_path,
        "not a real mdx fixture; fake reader supplies keys",
    )
    .expect("source file should be written");
    let index_root = temp_dir.join("index");

    let mut state = EasydictUiState::default();
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    let mut first_factory = RecordingNativeIndexReaderFactory::with_key_sets([vec![
        "apple".to_string(),
        "application".to_string(),
        "banana".to_string(),
    ]]);
    let first_update = run_local_dictionary_suggestion_request_with_native_index_root(
        request.clone(),
        &index_root,
        &mut first_factory,
    );

    assert_eq!(first_factory.opened, ["mdx::demo-dictionary"]);
    assert_eq!(
        first_update.suggestions,
        vec![
            LocalDictionarySuggestion {
                key: "apple".to_string(),
                dictionary_name: "Demo Dictionary".to_string(),
            },
            LocalDictionarySuggestion {
                key: "application".to_string(),
                dictionary_name: "Demo Dictionary".to_string(),
            },
        ]
    );
    assert_eq!(first_update.error, None);
    assert!(index_root
        .join("mdx%3A%3Ademo-dictionary")
        .join("index.bin")
        .exists());

    let mut second_factory =
        RecordingNativeIndexReaderFactory::with_key_sets([vec!["apricot".to_string()]]);
    let second_update = run_local_dictionary_suggestion_request_with_native_index_root(
        request,
        &index_root,
        &mut second_factory,
    );

    assert!(second_factory.opened.is_empty());
    assert_eq!(second_update.suggestions, first_update.suggestions);
    assert_eq!(second_update.error, None);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn local_dictionary_suggestion_runner_uses_settings_cache_dir_for_native_index() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-settings-cache-index");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let legacy_local_app_data = temp_dir.join("legacy-localappdata");
    let portable_cache_root = temp_dir.join("portable-cache");
    let _local_app_data_guard =
        EnvironmentVariableGuard::set("LOCALAPPDATA", &path_string(&legacy_local_app_data));
    let mdx_path = temp_dir.join("Portable Dictionary.mdx");
    fs::write(
        &mdx_path,
        "not a real mdx fixture; fake reader supplies keys",
    )
    .expect("source file should be written");

    let mut state = EasydictUiState::default();
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));
    state.source_text = "app".to_string();
    let mut request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");
    request.settings.cache_dir = Some(path_string(&portable_cache_root));

    let mut factory = RecordingNativeIndexReaderFactory::with_key_sets([vec![
        "apple".to_string(),
        "application".to_string(),
    ]]);
    let update =
        easydict_app::local_dictionary::run_local_dictionary_suggestion_request_with_native_index_and_reader_factory(
            request,
            &mut factory,
        );

    assert_eq!(update.error, None);
    assert_eq!(
        update
            .suggestions
            .iter()
            .map(|suggestion| suggestion.key.as_str())
            .collect::<Vec<_>>(),
        ["apple", "application"]
    );
    assert!(portable_cache_root
        .join("mdx_index")
        .join("mdx%3A%3Aportable-dictionary")
        .join("index.bin")
        .exists());
    assert!(
        !legacy_local_app_data
            .join("Easydict")
            .join("mdx_index")
            .exists(),
        "settings cache_dir should keep native dictionary indexes out of the legacy default root"
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn local_dictionary_native_index_stops_after_full_suggestions_without_opening_later_dictionaries() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-native-index-full-stop");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let first_mdx_path = temp_dir.join("First Dictionary.mdx");
    let second_mdx_path = temp_dir.join("Second Dictionary.mdx");
    fs::write(&first_mdx_path, "fake first mdx").expect("first source file should be written");
    fs::write(&second_mdx_path, "fake second mdx").expect("second source file should be written");
    let index_root = temp_dir.join("index");

    let mut state = EasydictUiState::default();
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(path_string(
        &first_mdx_path,
    ))));
    state.apply(Message::MdxDictionarySelected(Some(path_string(
        &second_mdx_path,
    ))));
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    let first_keys = (0..25)
        .map(|index| format!("application-{index:02}"))
        .collect::<Vec<_>>();
    let mut factory = RecordingNativeIndexReaderFactory::with_key_sets([first_keys]);
    let update = run_local_dictionary_suggestion_request_with_native_index_root(
        request,
        &index_root,
        &mut factory,
    );

    assert_eq!(factory.opened, ["mdx::first-dictionary"]);
    assert_eq!(update.suggestions.len(), 20);
    assert_eq!(update.error, None);
    assert_eq!(update.suggestions[0].key, "application-00");
    assert_eq!(update.suggestions[19].key, "application-19");
    assert!(
        !index_root.join("mdx%3A%3Asecond-dictionary").exists(),
        "default native suggestions should not build or open later dictionaries once the result list is full"
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn local_dictionary_suggestion_runner_routes_wildcard_queries_through_native_index() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-native-wildcard-index");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Tea Dictionary.mdx");
    fs::write(
        &mdx_path,
        "not a real mdx fixture; fake reader supplies keys",
    )
    .expect("source file should be written");
    let index_root = temp_dir.join("index");

    let mut state = EasydictUiState::default();
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(path_string(&mdx_path))));
    state.source_text = "tea*".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    let mut factory = RecordingNativeIndexReaderFactory::with_key_sets([vec![
        "toast".to_string(),
        "teapot".to_string(),
        "tealight".to_string(),
        "teatime".to_string(),
    ]]);
    let update = run_local_dictionary_suggestion_request_with_native_index_root(
        request,
        &index_root,
        &mut factory,
    );

    assert_eq!(factory.opened, ["mdx::tea-dictionary"]);
    assert_eq!(
        update.suggestions,
        vec![
            LocalDictionarySuggestion {
                key: "tealight".to_string(),
                dictionary_name: "Tea Dictionary".to_string(),
            },
            LocalDictionarySuggestion {
                key: "teapot".to_string(),
                dictionary_name: "Tea Dictionary".to_string(),
            },
            LocalDictionarySuggestion {
                key: "teatime".to_string(),
                dictionary_name: "Tea Dictionary".to_string(),
            },
        ]
    );
    assert_eq!(update.error, None);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn encrypted_local_dictionary_suggestions_with_valid_credentials_route_native_index_by_default() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-encrypted-native-index-default");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let encrypted_mdx_path = temp_dir.join("Secure Dictionary.mdx");
    write_mdx_header(
        &encrypted_mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );
    let index_root = temp_dir.join("index");

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(path_string(
        &encrypted_mdx_path,
    ))));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[0].regcode = Some(valid_mdx_regcode());
    state.settings.imported_mdx_dictionaries[0].email = Some("email@example.com".to_string());
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(local_dictionary_suggestion_request_can_route_natively(
        &request
    ));

    let mut factory = RecordingNativeIndexReaderFactory::with_key_sets([vec![
        "app".to_string(),
        "apple".to_string(),
        "banana".to_string(),
    ]]);
    let update = run_local_dictionary_suggestion_request_with_native_index_root(
        request,
        &index_root,
        &mut factory,
    );

    assert_eq!(factory.opened, ["mdx::secure-dictionary"]);
    assert_eq!(
        update.suggestions,
        vec![
            LocalDictionarySuggestion {
                key: "app".to_string(),
                dictionary_name: "Secure Dictionary".to_string(),
            },
            LocalDictionarySuggestion {
                key: "apple".to_string(),
                dictionary_name: "Secure Dictionary".to_string(),
            },
        ]
    );
    assert_eq!(update.error, None);
    assert!(index_root
        .join("mdx%3A%3Asecure-dictionary")
        .join("index.bin")
        .exists());

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
#[cfg(feature = "retained-dotnet-workers")]
fn local_dictionary_suggestions_route_plain_and_credential_encrypted_mdx_natively() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-valid-encrypted-native");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let encrypted_mdx_path = temp_dir.join("Secure Dictionary.mdx");
    write_mdx_header(
        &encrypted_mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(path_string(
        &encrypted_mdx_path,
    ))));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[0].regcode = Some(valid_mdx_regcode());
    state.settings.imported_mdx_dictionaries[0].email = Some("email@example.com".to_string());
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Plain Dictionary.mdx".to_string(),
    )));
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(local_dictionary_suggestion_request_can_route_natively(
        &request
    ));
    assert_eq!(
        native_mdx_lookup_local_input_error(
            &MdxLookupParams {
                dictionary_id: "mdx::secure-dictionary".to_string(),
                query: "app".to_string(),
                fuzzy: true,
            },
            &request.settings,
        ),
        None
    );
    assert!(native_mdx_lookup_can_route(
        &MdxLookupParams {
            dictionary_id: "mdx::plain-dictionary".to_string(),
            query: "app".to_string(),
            fuzzy: true,
        },
        &request.settings,
    ));

    let mut native_backend = RecordingSuggestionBackend::with_mdx_responses([
        Ok(MdxLookupResult {
            entries: vec![MdxLookupEntry {
                key: "app".to_string(),
                html: "<div>app</div>".to_string(),
                dictionary_name: Some("Secure Dictionary".to_string()),
            }],
            mdd_resources_inlined: false,
        }),
        Ok(MdxLookupResult {
            entries: vec![MdxLookupEntry {
                key: "application".to_string(),
                html: "<div>application</div>".to_string(),
                dictionary_name: None,
            }],
            mdd_resources_inlined: false,
        }),
    ]);
    let mut bridge_backend =
        RecordingSuggestionBackend::with_mdx_responses([Ok(MdxLookupResult {
            entries: vec![MdxLookupEntry {
                key: "app".to_string(),
                html: "<div>app</div>".to_string(),
                dictionary_name: Some("Secure Dictionary".to_string()),
            }],
            mdd_resources_inlined: false,
        })]);

    let update = run_local_dictionary_suggestion_request_with_routed_backends(
        &mut native_backend,
        &mut bridge_backend,
        request,
    );

    assert_eq!(native_backend.configure_calls.len(), 1);
    assert_eq!(bridge_backend.configure_calls.len(), 1);
    assert_eq!(native_backend.mdx_calls.len(), 2);
    assert_eq!(bridge_backend.mdx_calls.len(), 0);
    assert_eq!(
        native_backend.mdx_calls[0].dictionary_id,
        "mdx::secure-dictionary"
    );
    assert_eq!(
        native_backend.mdx_calls[1].dictionary_id,
        "mdx::plain-dictionary"
    );
    assert_eq!(
        update.suggestions,
        vec![
            LocalDictionarySuggestion {
                key: "app".to_string(),
                dictionary_name: "Secure Dictionary".to_string(),
            },
            LocalDictionarySuggestion {
                key: "application".to_string(),
                dictionary_name: "Plain Dictionary".to_string(),
            },
        ]
    );
    assert_eq!(update.error, None);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn encrypted_local_dictionary_suggestions_with_invalid_regcode_fail_locally_without_compat_host_spawn(
) {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-invalid-regcode-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let encrypted_mdx_path = temp_dir.join("Secure Dictionary.mdx");
    write_mdx_header(
        &encrypted_mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.apply(Message::MdxDictionarySelected(Some(path_string(
        &encrypted_mdx_path,
    ))));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[0].regcode = Some("not a base64 regcode".to_string());
    state.settings.imported_mdx_dictionaries[0].email = Some("email@example.com".to_string());
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(!local_dictionary_suggestion_request_can_route_natively(
        &request
    ));

    let update = run_local_dictionary_suggestion_request_with_app_dir(request, &temp_dir);

    assert!(update.suggestions.is_empty());
    assert!(
        matches!(
            update.error.as_deref(),
            Some(message) if message.contains("Base64")
                && !message.to_ascii_lowercase().contains("compat host")
        ),
        "unexpected encrypted suggestion error: {:?}",
        update.error
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn unsupported_encrypted_local_dictionary_suggestions_fail_locally_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-unsupported-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let encrypted_mdx_path = temp_dir.join("Combined Dictionary.mdx");
    write_mdx_header(
        &encrypted_mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="3" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.apply(Message::MdxDictionarySelected(Some(path_string(
        &encrypted_mdx_path,
    ))));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(!local_dictionary_suggestion_request_can_route_natively(
        &request
    ));

    let update = run_local_dictionary_suggestion_request_with_app_dir(request, &temp_dir);

    assert!(update.suggestions.is_empty());
    assert!(
        matches!(
            update.error.as_deref(),
            Some(message) if message.contains("not supported by the Rust-native MDX reader")
                && !message.contains("credentials are required")
                && !message.to_ascii_lowercase().contains("compat host")
        ),
        "unexpected unsupported encrypted suggestion error: {:?}",
        update.error
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
#[cfg(feature = "retained-dotnet-workers")]
fn mixed_local_dictionary_suggestions_skip_bridge_when_native_prefix_fills_results() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-native-full-no-bridge");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let encrypted_mdx_path = temp_dir.join("Secure Dictionary.mdx");
    write_mdx_header(
        &encrypted_mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Plain Dictionary.mdx".to_string(),
    )));
    state.apply(Message::MdxDictionarySelected(Some(path_string(
        &encrypted_mdx_path,
    ))));
    state.settings.imported_mdx_dictionaries[1].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[1].regcode = Some(valid_mdx_regcode());
    state.settings.imported_mdx_dictionaries[1].email = Some("email@example.com".to_string());
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(local_dictionary_suggestion_request_can_route_natively(
        &request
    ));

    let native_entries = (0..25)
        .map(|index| MdxLookupEntry {
            key: format!("application-{index:02}"),
            html: format!("<div>application-{index:02}</div>"),
            dictionary_name: None,
        })
        .collect::<Vec<_>>();
    let mut native_backend =
        RecordingSuggestionBackend::with_mdx_responses([Ok(MdxLookupResult {
            entries: native_entries,
            mdd_resources_inlined: false,
        })]);
    let bridge_factory_called = Cell::new(false);

    let update = run_local_dictionary_suggestion_request_with_lazy_bridge(
        &mut native_backend,
        || -> Result<RecordingSuggestionBackend, LocalDictionarySuggestionError> {
            bridge_factory_called.set(true);
            Err(LocalDictionarySuggestionError::new(
                "bridge should not be created when native suggestions are full",
            ))
        },
        request,
    );

    assert_eq!(native_backend.configure_calls.len(), 1);
    assert_eq!(native_backend.mdx_calls.len(), 1);
    assert!(!bridge_factory_called.get());
    assert_eq!(update.suggestions.len(), 20);
    assert_eq!(update.error, None);
    assert_eq!(update.suggestions[0].key, "application-00");
    assert_eq!(update.suggestions[19].key, "application-19");

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn unencrypted_local_dictionary_suggestions_route_natively_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-native-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Demo Dictionary.mdx".to_string(),
    )));
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(local_dictionary_suggestion_request_can_route_natively(
        &request
    ));

    let update = run_local_dictionary_suggestion_request_with_app_dir(request, &temp_dir);

    assert!(update.suggestions.is_empty());
    assert!(matches!(
        update.error.as_deref(),
        Some(message) if message.contains("MDX dictionary file not found")
    ));
    assert!(!update
        .error
        .as_deref()
        .unwrap_or_default()
        .contains("CompatHost"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn mixed_local_dictionary_suggestions_with_missing_files_finish_without_compat_host_spawn() {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-mixed-missing-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.settings.imported_mdx_dictionaries.clear();
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Plain Dictionary.mdx".to_string(),
    )));
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Secure Dictionary.mdx".to_string(),
    )));
    state.settings.imported_mdx_dictionaries[1].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[1].regcode = Some("reg".to_string());
    state.settings.imported_mdx_dictionaries[1].email = Some("email@example.com".to_string());
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(!local_dictionary_suggestion_request_can_route_natively(
        &request
    ));

    let update = run_local_dictionary_suggestion_request_with_app_dir(request, &temp_dir);

    assert!(update.suggestions.is_empty());
    assert!(
        matches!(
            update.error.as_deref(),
            Some(message) if message.contains("MDX dictionary file not found")
                && !message.to_ascii_lowercase().contains("compat host")
        ),
        "unexpected mixed suggestion error: {:?}",
        update.error
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn encrypted_local_dictionary_suggestions_with_credentials_missing_file_fail_locally_without_compat_host_spawn(
) {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-encrypted-missing-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Secure Dictionary.mdx".to_string(),
    )));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.settings.imported_mdx_dictionaries[0].regcode = Some("reg".to_string());
    state.settings.imported_mdx_dictionaries[0].email = Some("email@example.com".to_string());
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(!local_dictionary_suggestion_request_can_route_natively(
        &request
    ));

    let update = run_local_dictionary_suggestion_request_with_app_dir(request, &temp_dir);

    assert!(update.suggestions.is_empty());
    assert!(matches!(
        update.error.as_deref(),
        Some(message) if message.contains("MDX dictionary file not found")
    ));
    assert!(!update
        .error
        .as_deref()
        .unwrap_or_default()
        .to_ascii_lowercase()
        .contains("compat host"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn encrypted_local_dictionary_suggestions_without_credentials_fail_locally_without_compat_host_spawn(
) {
    let temp_dir = unique_temp_dir("easydict-mdx-suggestions-encrypted-no-creds-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut state = EasydictUiState::default();
    state.settings.local_dictionary_suggestions = true;
    state.apply(Message::MdxDictionarySelected(Some(
        r"C:\Dicts\Secure Dictionary.mdx".to_string(),
    )));
    state.settings.imported_mdx_dictionaries[0].is_encrypted = true;
    state.source_text = "app".to_string();
    let request =
        begin_local_dictionary_suggestions(&mut state).expect("suggestion request should start");

    assert!(!local_dictionary_suggestion_request_can_route_natively(
        &request
    ));

    let update = run_local_dictionary_suggestion_request_with_app_dir(request, &temp_dir);

    assert!(update.suggestions.is_empty());
    assert!(matches!(
        update.error.as_deref(),
        Some(message) if message.contains("credentials are required")
    ));
    assert!(!update
        .error
        .as_deref()
        .unwrap_or_default()
        .contains("CompatHost"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
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
            arguments: browser_registrar_arguments("install"),
        })
    );

    let browser_uninstall = app.update(Message::TrayCommand(TRAY_BROWSER_UNINSTALL.to_string()));
    assert_eq!(
        platform_command(&browser_uninstall),
        Some(PlatformCommand::RunBundledExecutable {
            executable_name: BROWSER_REGISTRAR_EXE.to_string(),
            arguments: browser_registrar_arguments("uninstall"),
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
    assert_ne!(named_events[0].name, r"Local\Easydict-OcrTranslate");
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

    // Opening settings now spawns the async on-disk runtime-status check and
    // shows the loading overlay until it resolves.
    assert_eq!(task_kind(&task), "future");
    assert!(app.state.settings_open);
    assert!(app.state.settings.settings_runtime.is_loading());
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
fn settings_runtime_status_loaded_updates_open_vino_panel_status_when_idle() {
    let mut state = EasydictUiState::default();

    state.apply(Message::SettingsRuntimeStatusLoaded(
        easydict_app::settings_status::SettingsRuntimeStatus {
            layout_model: "Available".to_string(),
            cjk_font: "Available".to_string(),
            windows_ai_status: "Phi Silica is ready.".to_string(),
            foundry_local_status:
                "Foundry Local is ready at http://localhost:5273/v1/chat/completions.".to_string(),
            open_vino_status: "NLLB-200 model ready".to_string(),
            open_vino_download_progress: "Idle".to_string(),
        },
    ));

    assert_eq!(state.settings.layout_model_status, "Available");
    assert_eq!(state.settings.cjk_font_status, "Available");
    assert_eq!(
        state.settings.foundry_local_status,
        "Foundry Local is ready at http://localhost:5273/v1/chat/completions."
    );
    assert_eq!(state.settings.open_vino_status, "NLLB-200 model ready");
    assert_eq!(state.settings.open_vino_download_progress, "Idle");
    assert!(!state.settings.settings_runtime.is_loading());
}

#[test]
fn settings_runtime_status_loaded_preserves_queued_open_vino_download_state() {
    let mut state = EasydictUiState::default();

    state.apply(Message::DownloadOpenVinoModel);
    state.apply(Message::SettingsRuntimeStatusLoaded(
        easydict_app::settings_status::SettingsRuntimeStatus {
            layout_model: "Available".to_string(),
            cjk_font: "Available".to_string(),
            windows_ai_status: "Phi Silica is ready.".to_string(),
            foundry_local_status:
                "Foundry Local is ready at http://localhost:5273/v1/chat/completions.".to_string(),
            open_vino_status: "Model not downloaded".to_string(),
            open_vino_download_progress: "Idle".to_string(),
        },
    ));

    assert_eq!(
        state.settings.open_vino_status,
        "Download queued for NLLB-200 model (~360 MB)"
    );
    assert_eq!(state.settings.open_vino_download_progress, "Queued");
}

#[test]
fn settings_runtime_status_loaded_preserves_starting_foundry_local_status() {
    let mut state = EasydictUiState::default();

    state.apply(Message::StartFoundryLocal);
    state.apply(Message::SettingsRuntimeStatusLoaded(
        easydict_app::settings_status::SettingsRuntimeStatus {
            layout_model: "Available".to_string(),
            cjk_font: "Available".to_string(),
            windows_ai_status: "Phi Silica is ready.".to_string(),
            foundry_local_status:
                "Foundry Local is ready at http://localhost:5273/v1/chat/completions.".to_string(),
            open_vino_status: "NLLB-200 model ready".to_string(),
            open_vino_download_progress: "Idle".to_string(),
        },
    ));

    assert_eq!(
        state.settings.foundry_local_status,
        "Starting Foundry Local service..."
    );
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
    get_requests: Vec<OpenAiHttpGetRequestPlan>,
    get_responses: VecDeque<Result<Option<OpenAiHttpTextResponse>, OpenAiExecutionError>>,
}

impl RecordingOpenAiHttpClient {
    fn with_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
            get_requests: Vec::new(),
            get_responses: VecDeque::new(),
        }
    }

    fn with_responses_and_get_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
        get_responses: impl IntoIterator<
            Item = Result<Option<OpenAiHttpTextResponse>, OpenAiExecutionError>,
        >,
    ) -> Self {
        Self {
            requests: Vec::new(),
            responses: responses.into_iter().collect(),
            get_requests: Vec::new(),
            get_responses: get_responses.into_iter().collect(),
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

    fn get_text(
        &mut self,
        request: &OpenAiHttpGetRequestPlan,
    ) -> Result<Option<OpenAiHttpTextResponse>, OpenAiExecutionError> {
        self.get_requests.push(request.clone());
        self.get_responses.pop_front().unwrap_or(Ok(None))
    }
}

struct RecordingFoundryLocalEndpointResolver {
    calls: usize,
    status_calls: usize,
    start_calls: usize,
    load_model_calls: Vec<String>,
    responses: VecDeque<Result<Option<String>, FoundryLocalError>>,
}

struct RecordingWindowsAiProbe {
    ready_state_calls: usize,
    states: VecDeque<WindowsAiReadyState>,
}

struct RecordingWindowsAiClient {
    ready_state_calls: usize,
    states: VecDeque<WindowsAiReadyState>,
    generate_prompts: Vec<String>,
    generate_options: Vec<WindowsAiGenerationOptions>,
    generate_responses: VecDeque<Result<WindowsAiResponse, WindowsAiError>>,
    stream_prompts: Vec<String>,
    stream_options: Vec<WindowsAiGenerationOptions>,
    stream_responses: VecDeque<Result<Vec<String>, WindowsAiError>>,
    warm_up_prompts: Vec<String>,
    warm_up_options: Vec<WindowsAiGenerationOptions>,
}

struct BlockingWindowsAiStreamClient {
    first_chunk_tx: std::sync::mpsc::Sender<()>,
    release_rx: std::sync::mpsc::Receiver<()>,
    ready_state_calls: usize,
    stream_prompts: Vec<String>,
}

#[derive(Default)]
struct RecordingNllbTokenizer;

impl NllbTokenizer for RecordingNllbTokenizer {
    fn encode_source(&self, text: &str, source_flores_code: &str) -> Result<Vec<i32>, NllbError> {
        assert_eq!(text, "Hello");
        assert_eq!(source_flores_code, "eng_Latn");
        Ok(vec![101, 42, 2])
    }

    fn decode(&self, token_ids: &[i32]) -> Result<String, NllbError> {
        match token_ids {
            [200] => Ok("你".to_string()),
            [200, 201] => Ok("你".to_string()),
            [200, 201, 202] => Ok("你好".to_string()),
            _ => Err(NllbError::new("unexpected NLLB token ids")),
        }
    }

    fn language_token_id(&self, flores_code: &str) -> Result<i32, NllbError> {
        assert_eq!(flores_code, "zho_Hans");
        Ok(256001)
    }
}

#[derive(Default)]
struct RecordingNllbEngine {
    generated: Vec<i32>,
    last_call: Option<RecordingNllbEngineCall>,
}

impl NllbInferenceEngine for RecordingNllbEngine {
    fn generate(
        &mut self,
        encoder_input_ids: &[i32],
        forced_bos_token_id: i32,
        max_new_tokens: usize,
    ) -> Result<Vec<i32>, NllbError> {
        self.last_call = Some(RecordingNllbEngineCall {
            input_ids: encoder_input_ids.to_vec(),
            forced_bos: forced_bos_token_id,
            max_new_tokens,
        });
        Ok(self.generated.clone())
    }
}

#[derive(Debug, Eq, PartialEq)]
struct RecordingNllbEngineCall {
    input_ids: Vec<i32>,
    forced_bos: i32,
    max_new_tokens: usize,
}

impl RecordingFoundryLocalEndpointResolver {
    fn new(responses: impl IntoIterator<Item = Result<Option<String>, FoundryLocalError>>) -> Self {
        Self {
            calls: 0,
            status_calls: 0,
            start_calls: 0,
            load_model_calls: Vec::new(),
            responses: responses.into_iter().collect(),
        }
    }
}

impl RecordingWindowsAiProbe {
    fn new(states: impl IntoIterator<Item = WindowsAiReadyState>) -> Self {
        Self {
            ready_state_calls: 0,
            states: states.into_iter().collect(),
        }
    }
}

impl RecordingWindowsAiClient {
    fn with_stream_responses(
        states: impl IntoIterator<Item = WindowsAiReadyState>,
        stream_responses: impl IntoIterator<Item = Result<Vec<String>, WindowsAiError>>,
    ) -> Self {
        Self {
            ready_state_calls: 0,
            states: states.into_iter().collect(),
            generate_prompts: Vec::new(),
            generate_options: Vec::new(),
            generate_responses: VecDeque::new(),
            stream_prompts: Vec::new(),
            stream_options: Vec::new(),
            stream_responses: stream_responses.into_iter().collect(),
            warm_up_prompts: Vec::new(),
            warm_up_options: Vec::new(),
        }
    }
}

impl WindowsAiLanguageModelProbe for RecordingWindowsAiProbe {
    fn ready_state(&mut self) -> WindowsAiReadyState {
        self.ready_state_calls += 1;
        self.states
            .pop_front()
            .unwrap_or(WindowsAiReadyState::NotSupportedOnCurrentSystem)
    }
}

impl WindowsAiLanguageModelProbe for RecordingWindowsAiClient {
    fn ready_state(&mut self) -> WindowsAiReadyState {
        self.ready_state_calls += 1;
        self.states
            .pop_front()
            .unwrap_or(WindowsAiReadyState::NotSupportedOnCurrentSystem)
    }
}

impl WindowsAiLanguageModelProbe for BlockingWindowsAiStreamClient {
    fn ready_state(&mut self) -> WindowsAiReadyState {
        self.ready_state_calls += 1;
        WindowsAiReadyState::Ready
    }
}

impl WindowsAiLanguageModelClient for RecordingWindowsAiClient {
    fn generate(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<WindowsAiResponse, WindowsAiError> {
        self.generate_prompts.push(prompt.to_string());
        self.generate_options.push(options);
        self.generate_responses
            .pop_front()
            .unwrap_or_else(|| Ok(WindowsAiResponse::complete(String::new())))
    }

    fn generate_stream(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<Vec<String>, WindowsAiError> {
        self.stream_prompts.push(prompt.to_string());
        self.stream_options.push(options);
        self.stream_responses
            .pop_front()
            .unwrap_or_else(|| Ok(Vec::new()))
    }

    fn warm_up(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<(), WindowsAiError> {
        self.warm_up_prompts.push(prompt.to_string());
        self.warm_up_options.push(options);
        Ok(())
    }
}

impl WindowsAiLanguageModelClient for BlockingWindowsAiStreamClient {
    fn generate(
        &mut self,
        _prompt: &str,
        _options: WindowsAiGenerationOptions,
    ) -> Result<WindowsAiResponse, WindowsAiError> {
        Err(WindowsAiError::new(
            "blocking stream client should not run non-streaming generation",
        ))
    }

    fn generate_stream(
        &mut self,
        _prompt: &str,
        _options: WindowsAiGenerationOptions,
    ) -> Result<Vec<String>, WindowsAiError> {
        Err(WindowsAiError::new(
            "blocking stream client should use the observing route",
        ))
    }

    fn generate_stream_observing_chunks(
        &mut self,
        prompt: &str,
        _options: WindowsAiGenerationOptions,
        on_chunk: &mut dyn FnMut(&str),
    ) -> Result<Vec<String>, WindowsAiError> {
        self.stream_prompts.push(prompt.to_string());
        on_chunk("你");
        self.first_chunk_tx
            .send(())
            .map_err(|error| WindowsAiError::new(error.to_string()))?;
        self.release_rx
            .recv_timeout(std::time::Duration::from_secs(10))
            .map_err(|error| WindowsAiError::new(error.to_string()))?;
        on_chunk("好");
        Ok(vec!["你".to_string(), "好".to_string()])
    }

    fn warm_up(
        &mut self,
        _prompt: &str,
        _options: WindowsAiGenerationOptions,
    ) -> Result<(), WindowsAiError> {
        Ok(())
    }
}

impl FoundryLocalEndpointResolver for RecordingFoundryLocalEndpointResolver {
    fn resolve_chat_completions_endpoint(&mut self) -> Result<Option<String>, FoundryLocalError> {
        self.calls += 1;
        self.responses.pop_front().unwrap_or_else(|| {
            Err(FoundryLocalError::new(
                FoundryLocalErrorCode::ServiceUnavailable,
                "test Foundry Local endpoint response was not queued",
            ))
        })
    }
}

impl FoundryLocalRuntimeController for RecordingFoundryLocalEndpointResolver {
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
        self.start_calls += 1;
        Ok(())
    }

    fn load_model(&mut self, model: &str) -> Result<(), FoundryLocalError> {
        self.load_model_calls.push(model.to_string());
        Ok(())
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

struct EnvironmentVariableGuard {
    name: &'static str,
    original: Option<String>,
}

impl EnvironmentVariableGuard {
    fn set(name: &'static str, value: &str) -> Self {
        let original = std::env::var(name).ok();
        std::env::set_var(name, value);
        Self { name, original }
    }

    #[cfg(feature = "retained-dotnet-workers")]
    fn remove(name: &'static str) -> Self {
        let original = std::env::var(name).ok();
        std::env::remove_var(name);
        Self { name, original }
    }
}

impl Drop for EnvironmentVariableGuard {
    fn drop(&mut self) {
        if let Some(value) = self.original.as_ref() {
            std::env::set_var(self.name, value);
        } else {
            std::env::remove_var(self.name);
        }
    }
}

fn path_string(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

fn install_open_vino_cache(base: &Path) {
    let paths = NllbModelPaths::from_cache_base(base);
    install_complete_open_vino_file_set(&paths.model_dir, NLLB_MODEL_FILES);
    install_complete_open_vino_file_set(&paths.runtime_dir, OPENVINO_RUNTIME_FILES);
}

fn install_complete_open_vino_file_set(dir: &Path, files: &[&str]) {
    fs::create_dir_all(dir).expect("OpenVINO cache dir should be created");
    for file in files {
        fs::write(dir.join(file), b"x").expect("OpenVINO cache file should be written");
    }
    fs::write(dir.join(MODEL_COMPLETION_SENTINEL), b"x")
        .expect("OpenVINO sentinel should be written");
}

fn valid_mdx_regcode() -> String {
    "MDEyMzQ1Njc4OTo7PD0+Pw==".to_string()
}

fn write_mdx_header(path: &Path, header_xml: &str) {
    let mut header_bytes = header_xml
        .encode_utf16()
        .flat_map(u16::to_le_bytes)
        .collect::<Vec<_>>();
    header_bytes.extend_from_slice(&[0, 0]);

    let mut file_bytes = Vec::new();
    file_bytes.extend_from_slice(&(header_bytes.len() as u32).to_be_bytes());
    file_bytes.extend_from_slice(&header_bytes);
    file_bytes.extend_from_slice(&[0, 0, 0, 0]);
    fs::write(path, file_bytes).expect("MDX header should be written");
}

#[cfg(feature = "retained-dotnet-workers")]
fn mock_local_ai_worker_facade() -> DirectWorkerFacade {
    DirectWorkerFacade::spawn_worker(
        WorkerCommand::new("powershell.exe")
            .arg("-NoProfile")
            .arg("-ExecutionPolicy")
            .arg("Bypass")
            .arg("-Command")
            .arg(MOCK_LOCAL_AI_WORKER_SCRIPT),
        worker_kinds::LOCAL_AI,
    )
    .expect("mock local AI worker must spawn")
}

#[cfg(feature = "retained-dotnet-workers")]
const MOCK_LOCAL_AI_WORKER_SCRIPT: &str = r#"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding = [System.Text.Encoding]::UTF8

function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

Write-JsonLine ([ordered]@{
    event = 'ready'
    data = [ordered]@{
        workerKind = 'localai'
        workerVersion = '1.0.0'
        protocolVersion = 1
        capabilities = @('configure', 'translate_stream', 'grammar_stream', 'cancel', 'shutdown')
    }
})

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $request = $line | ConvertFrom-Json
    switch ([string]$request.method) {
        'configure' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
        }
        'translate_stream' {
            $from = [string]$request.params.fromLanguage
            $to = [string]$request.params.toLanguage
            $provider = [string]$request.params.providerMode
            $prompt = [string]$request.params.customPrompt
            $text = "$from>$to>$provider"
            if (-not [string]::IsNullOrWhiteSpace($prompt)) {
                $text = "$text>$prompt"
            }
            Write-JsonLine ([ordered]@{
                event = 'chunk'
                id = $request.id
                data = [ordered]@{ text = $text }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    done = $true
                    fullText = $text
                }
            })
        }
        'grammar_stream' {
            $include = if ($request.params.includeExplanations -eq $true) { 'True' } else { 'False' }
            $raw = "[CORRECTED]I have an apple.[/CORRECTED]`n[EXPLANATION]include=$include[/EXPLANATION]"
            Write-JsonLine ([ordered]@{
                event = 'chunk'
                id = $request.id
                data = [ordered]@{ text = $raw }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    done = $true
                    fullText = $raw
                }
            })
        }
        'shutdown' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
            exit 0
        }
        default {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'method_not_found'
                    message = "unexpected method: $($request.method)"
                }
            })
        }
    }
}
"#;

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

struct RecordingNativeIndexReaderFactory {
    opened: Vec<String>,
    key_sets: VecDeque<Vec<String>>,
}

impl RecordingNativeIndexReaderFactory {
    fn with_key_sets(key_sets: impl IntoIterator<Item = Vec<String>>) -> Self {
        Self {
            opened: Vec::new(),
            key_sets: key_sets.into_iter().collect(),
        }
    }
}

impl NativeMdxDictionaryReaderFactory for RecordingNativeIndexReaderFactory {
    type Reader = RecordingNativeIndexReader;

    fn open(
        &mut self,
        dictionary: &ImportedMdxDictionarySnapshot,
    ) -> Result<Self::Reader, NativeMdxLookupError> {
        self.opened.push(dictionary.service_id.clone());
        let keys = self
            .key_sets
            .pop_front()
            .ok_or_else(|| NativeMdxLookupError::new("test key set was not queued"))?;
        Ok(RecordingNativeIndexReader { keys })
    }
}

struct RecordingNativeIndexReader {
    keys: Vec<String>,
}

impl NativeMdxDictionaryReader for RecordingNativeIndexReader {
    fn lookup(&mut self, _query: &str) -> Result<Option<(String, String)>, NativeMdxLookupError> {
        Ok(None)
    }

    fn all_keys(&mut self) -> Result<Vec<String>, NativeMdxLookupError> {
        Ok(self.keys.clone())
    }

    fn fuzzy_keys(
        &mut self,
        _query: &str,
        _max_results: usize,
        _max_distance: usize,
    ) -> Result<Vec<String>, NativeMdxLookupError> {
        Ok(Vec::new())
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

fn quick_translate_cache_app<const N: usize>(service_ids: [&'static str; N]) -> EasydictApp {
    let mut state = EasydictUiState::default();
    state.source_text = "Hello cache".to_string();
    state.source_language = "auto".to_string();
    state.target_language = "zh-Hans".to_string();
    state.results = service_ids
        .into_iter()
        .map(|service_id| {
            QuickTranslateResult::new(
                service_id,
                quick_translate_test_service_name(service_id),
                false,
            )
            .into()
        })
        .collect();
    EasydictApp { state }
}

fn quick_translate_test_service_name(service_id: &str) -> &'static str {
    match service_id {
        "bing" => "Bing Translate",
        "openai" => "OpenAI",
        _ => "Google Translate",
    }
}

fn google_cache_request(text: &str) -> TranslationCacheRequest {
    cache_request("google", text)
}

fn cache_request(service_id: &str, text: &str) -> TranslationCacheRequest {
    TranslationCacheRequest::new(
        service_id,
        TranslationLanguage::Auto,
        TranslationLanguage::SimplifiedChinese,
        text,
    )
}

fn phonetic_enrichment_request(to: &str) -> QuickTranslateServiceRequest {
    QuickTranslateServiceRequest {
        query_id: 97,
        service: quick_service("google", "Google Translate", false, false),
        query_mode: QuickQueryMode::Translation,
        execution_kind: QuickTranslateExecutionKind::Translate,
        params: TranslateParams {
            text: "你好".to_string(),
            from: Some("zh-Hans".to_string()),
            to: Some(to.to_string()),
            services: Some(vec!["google".to_string()]),
            custom_prompt: None,
        },
        grammar_params: None,
        settings: SettingsSnapshot::default(),
    }
}

fn phonetic_enrichment_update(
    request: &QuickTranslateServiceRequest,
    translated_text: &str,
    phonetics: Option<Vec<PhoneticDto>>,
) -> QuickTranslateServiceUpdate {
    let mut result = dto(
        &request.service.id,
        &request.service.name,
        translated_text,
        request.params.from.as_deref(),
        Some(36),
    );
    result.word_result = phonetics.map(|phonetics| WordResultDto {
        phonetics: Some(phonetics),
        definitions: None,
        examples: None,
        word_forms: None,
        synonyms: None,
    });

    QuickTranslateServiceUpdate {
        query_id: request.query_id,
        outcome: QuickTranslateServiceOutcome {
            service: request.service.clone(),
            grammar_result: None,
            streamed_chunks: Vec::new(),
            result: Ok(result),
        },
    }
}

fn youdao_phonetic_json() -> String {
    r#"{
        "simple": {
            "word": [{
                "usphone": "həˈloʊ",
                "usspeech": "hello&type=1",
                "ukphone": "həˈləʊ",
                "ukspeech": "hello&type=2"
            }]
        },
        "ec": {
            "word": {
                "trs": [{"pos": "int.", "tran": "hello"}]
            }
        }
    }"#
    .to_string()
}

fn quick_translate_update(
    query_id: u64,
    service_id: &str,
    service_name: &str,
    translated_text: &str,
) -> QuickTranslateServiceUpdate {
    QuickTranslateServiceUpdate {
        query_id,
        outcome: QuickTranslateServiceOutcome {
            service: QuickTranslateService {
                id: service_id.to_string(),
                name: service_name.to_string(),
                enabled_query: true,
                grammar_capable: false,
                streaming_capable: false,
            },
            grammar_result: None,
            streamed_chunks: Vec::new(),
            result: Ok(dto(
                service_id,
                service_name,
                translated_text,
                Some("en"),
                Some(42),
            )),
        },
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
        alternatives: None,
        word_result: None,
        raw_html: None,
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
        alternatives: None,
        word_result: None,
        raw_html: None,
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

fn builtin_proxy_settings() -> SettingsSnapshot {
    SettingsSnapshot {
        built_in_ai_model: Some("glm-4-flash".to_string()),
        device_id: Some("device-id".to_string()),
        device_token: Some("device-token".to_string()),
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

fn local_ai_route_matrix_request(
    query_id: u64,
    service_id: &str,
    provider_mode: Option<&str>,
    execution_kind: QuickTranslateExecutionKind,
    from: &str,
    to: &str,
    foundry_endpoint: Option<&str>,
    foundry_model: Option<&str>,
    cache_dir: Option<&str>,
) -> QuickTranslateServiceRequest {
    let is_grammar = execution_kind == QuickTranslateExecutionKind::GrammarCorrection;
    let service_name = if service_id == "windows-local-ai" {
        "Windows Local AI"
    } else {
        service_id
    };

    QuickTranslateServiceRequest {
        query_id,
        service: quick_service(service_id, service_name, true, true),
        query_mode: if is_grammar {
            QuickQueryMode::GrammarCorrection
        } else {
            QuickQueryMode::Translation
        },
        execution_kind,
        params: TranslateParams {
            text: if is_grammar {
                "He go home.".to_string()
            } else {
                "Hello".to_string()
            },
            from: Some(from.to_string()),
            to: Some(to.to_string()),
            services: Some(vec![service_id.to_string()]),
            custom_prompt: None,
        },
        grammar_params: is_grammar.then(|| GrammarCorrectParams {
            text: "He go home.".to_string(),
            language: Some(from.to_string()),
            services: Some(vec![service_id.to_string()]),
            include_explanations: true,
        }),
        settings: SettingsSnapshot {
            local_ai_provider: provider_mode.map(str::to_string),
            foundry_local_endpoint: foundry_endpoint.map(str::to_string),
            foundry_local_model: foundry_model.map(str::to_string),
            cache_dir: cache_dir.map(str::to_string),
            ..SettingsSnapshot::default()
        },
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
        Task::ScrollToTop(_) => "scroll_to_top",
        Task::ScrollTo { .. } => "scroll_to",
        Task::ReadClipboardText(_) => "read_clipboard",
        Task::CaptureScreenRegion { .. } => "capture_screen",
        Task::CaptureScreenWindows { .. } => "capture_screen_windows",
        Task::OpenFileDialog { .. } => "file_dialog",
        Task::OpenFolderDialog { .. } => "folder_dialog",
    }
}

fn quick_translate_service_finished_updates(
    task: &Task<Message>,
) -> Vec<QuickTranslateServiceUpdate> {
    match task {
        Task::Message(Message::QuickTranslateServiceFinished(update)) => vec![update.clone()],
        Task::Batch(tasks) => tasks
            .iter()
            .flat_map(quick_translate_service_finished_updates)
            .collect(),
        _ => Vec::new(),
    }
}

fn platform_command(task: &Task<Message>) -> Option<PlatformCommand> {
    match task {
        Task::Platform(command) => Some(command.clone()),
        _ => None,
    }
}

fn browser_registrar_arguments(command: &str) -> Vec<String> {
    vec![
        command.to_string(),
        "--bridge-root-name".to_string(),
        RUST_BRIDGE_ROOT_NAME.to_string(),
    ]
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

fn contains_future_task(task: &Task<Message>) -> bool {
    match task {
        Task::Future(_) => true,
        Task::Batch(tasks) => tasks.iter().any(contains_future_task),
        _ => false,
    }
}

fn contains_stream_task(task: &Task<Message>) -> bool {
    match task {
        Task::Stream(_) => true,
        Task::Batch(tasks) => tasks.iter().any(contains_stream_task),
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
