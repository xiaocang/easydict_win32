use easydict_app::compat_protocol::{
    local_ai_provider_modes, BlockTranslatedEventData, ProgressEventData, SettingsSnapshot,
    StatusEventData, TranslateDocumentParams, TranslateDocumentResult,
};
use easydict_app::{
    apply_long_document_outcome, begin_long_document_translate, build_long_document_request,
    long_document_request_can_route_natively, long_document_source_hash, run_long_document_request,
    run_long_document_request_with_native_route, run_long_document_request_with_packaged_app_dir,
    run_long_document_request_with_packaged_app_dir_and_worker_policy,
    run_native_text_long_document_request_with_translator, AppMode, EasydictApp, EasydictUiState,
    LongDocumentBackend, LongDocumentBackendError, LongDocumentEvent, LongDocumentInput,
    LongDocumentOutcome, LongDocumentTranslationCache, Message, NativeLongDocumentTranslator,
    QuickTranslateServiceRequest, RetainedWorkerPolicy, TRANSLATION_LANGUAGE_IDS,
};
use std::fs;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};
use win_fluent::prelude::{Application, ResultStatus, Task};

#[test]
fn long_document_translate_builds_file_request_and_marks_loading() {
    let mut state = EasydictUiState::default();
    state.mode = AppMode::LongDocument;
    state.long_document.selected_file = r"C:\Docs\paper.md".to_string();
    state.long_document.input_mode = "markdown".to_string();
    state.long_document.output_mode = "both".to_string();
    state.long_document.source_language = "en".to_string();
    state.long_document.target_language = "zh-Hans".to_string();
    state.long_document.service = "openai".to_string();
    state.long_document.page_range = " 1-3,5 ".to_string();
    state.long_document.concurrency = "8".to_string();
    state.long_document.two_pass_context = false;
    state.settings.layout_detection_mode = "VisionLLM".to_string();
    state.settings.formula_font_pattern = "CM[A-Z]+".to_string();
    state.settings.formula_char_pattern = "[_^]".to_string();
    state.settings.custom_translation_prompt = "Keep technical terms consistent.".to_string();
    state.settings.proxy_enabled = true;
    state.settings.proxy_url = "http://127.0.0.1:7890".to_string();
    state.settings.proxy_bypass_local = false;

    let request = begin_long_document_translate(&mut state).expect("request should start");

    assert_eq!(request.query_id, 1);
    assert_eq!(
        request.input,
        LongDocumentInput::File(r"C:\Docs\paper.md".to_string())
    );
    assert_eq!(request.params.input_path, r"C:\Docs\paper.md");
    assert_eq!(
        request.params.output_path.as_deref(),
        Some(r"C:\Docs\paper_translated.md")
    );
    assert_eq!(request.params.input_mode, "Markdown");
    assert_eq!(request.params.output_mode, "Both");
    assert_eq!(request.params.from, "English");
    assert_eq!(request.params.to, "SimplifiedChinese");
    assert_eq!(
        request.params.layout_detection.as_deref(),
        Some("VisionLLM")
    );
    assert_eq!(request.params.page_range, None);
    assert_eq!(request.settings.long_doc_max_concurrency, Some(8));
    assert_eq!(
        request.settings.long_doc_enable_document_context_pass,
        Some(false)
    );
    assert_eq!(
        request.settings.layout_detection_mode.as_deref(),
        Some("VisionLLM")
    );
    assert_eq!(
        request.settings.formula_font_pattern.as_deref(),
        Some("CM[A-Z]+")
    );
    assert_eq!(
        request.settings.formula_char_pattern.as_deref(),
        Some("[_^]")
    );
    assert_eq!(
        request.settings.long_doc_custom_prompt.as_deref(),
        Some("Keep technical terms consistent.")
    );
    assert_eq!(request.settings.proxy_enabled, Some(true));
    assert_eq!(
        request.settings.proxy_uri.as_deref(),
        Some("http://127.0.0.1:7890")
    );
    assert_eq!(request.settings.proxy_bypass_local, Some(false));
    assert_eq!(state.next_query_id, 2);
    assert_eq!(state.long_document.active_query_id, Some(1));
    assert!(state.long_document.is_translating);
    assert_eq!(state.long_document.status_text, "Translating document");
}

#[test]
fn long_document_foundry_local_profile_forces_sequential_translation_and_skips_context_pass() {
    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::AUTO.to_string(),
                foundry_local_endpoint: "http://127.0.0.1:5273/v1/chat/completions".to_string(),
                foundry_local_model: "qwen2.5-0.5b".to_string(),
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document.".to_string(),
                input_mode: "plaintext".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "windows-local-ai".to_string(),
                concurrency: "8".to_string(),
                two_pass_context: true,
                ..Default::default()
            },
            ..Default::default()
        },
        52,
    )
    .expect("local AI Foundry long document request");

    assert_eq!(request.params.service_id, "windows-local-ai");
    assert_eq!(request.settings.long_doc_max_concurrency, Some(1));
    assert_eq!(
        request.settings.long_doc_enable_document_context_pass,
        Some(false)
    );
    assert!(long_document_request_can_route_natively(&request));
}

#[test]
fn long_document_windows_ai_profile_preserves_user_concurrency_and_context_pass() {
    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::WINDOWS_AI.to_string(),
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document.".to_string(),
                input_mode: "plaintext".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "windows-local-ai".to_string(),
                concurrency: "8".to_string(),
                two_pass_context: true,
                ..Default::default()
            },
            ..Default::default()
        },
        53,
    )
    .expect("local AI WindowsAI long document request");

    assert_eq!(request.settings.long_doc_max_concurrency, Some(8));
    assert_eq!(
        request.settings.long_doc_enable_document_context_pass,
        Some(true)
    );
    assert!(!long_document_request_can_route_natively(&request));
}

#[test]
fn long_document_request_maps_all_selectable_targets_to_dotnet_language_names() {
    let expected = [
        ("zh-Hans", "SimplifiedChinese"),
        ("zh-Hant", "TraditionalChinese"),
        ("ja", "Japanese"),
        ("ko", "Korean"),
        ("en", "English"),
        ("de", "German"),
        ("nl", "Dutch"),
        ("sv", "Swedish"),
        ("no", "Norwegian"),
        ("da", "Danish"),
        ("fr", "French"),
        ("es", "Spanish"),
        ("pt", "Portuguese"),
        ("it", "Italian"),
        ("ro", "Romanian"),
        ("ru", "Russian"),
        ("pl", "Polish"),
        ("cs", "Czech"),
        ("uk", "Ukrainian"),
        ("bg", "Bulgarian"),
        ("sk", "Slovak"),
        ("sl", "Slovenian"),
        ("et", "Estonian"),
        ("lv", "Latvian"),
        ("lt", "Lithuanian"),
        ("el", "Greek"),
        ("hu", "Hungarian"),
        ("fi", "Finnish"),
        ("tr", "Turkish"),
        ("ar", "Arabic"),
        ("fa", "Persian"),
        ("he", "Hebrew"),
        ("hi", "Hindi"),
        ("bn", "Bengali"),
        ("ta", "Tamil"),
        ("te", "Telugu"),
        ("ur", "Urdu"),
        ("vi", "Vietnamese"),
        ("th", "Thai"),
        ("id", "Indonesian"),
        ("ms", "Malay"),
        ("tl", "Filipino"),
        ("zh-classical", "ClassicalChinese"),
    ];

    assert_eq!(TRANSLATION_LANGUAGE_IDS.len(), expected.len());
    for (index, (language_id, expected_language)) in expected.into_iter().enumerate() {
        assert_eq!(TRANSLATION_LANGUAGE_IDS[index], language_id);

        let request = build_long_document_request(
            &EasydictUiState {
                long_document: easydict_app::LongDocumentState {
                    selected_file: "No file selected".to_string(),
                    source_text: "A short document".to_string(),
                    input_mode: "plaintext".to_string(),
                    target_language: language_id.to_string(),
                    ..Default::default()
                },
                ..Default::default()
            },
            index as u64 + 100,
        )
        .expect("language-mapped request");

        assert_eq!(
            request.params.to, expected_language,
            "language id {language_id} should map to the .NET Language enum name"
        );
    }
}

#[test]
fn long_document_inline_text_uses_plain_text_input_when_no_file_is_selected() {
    let mut state = EasydictUiState::default();
    state.long_document.selected_file = "No file selected".to_string();
    state.long_document.input_mode = "pdf".to_string();
    state.long_document.source_text = "A long pasted document".to_string();

    let request = build_long_document_request(&state, 7).expect("inline text request");

    assert_eq!(
        request.input,
        LongDocumentInput::InlineText("A long pasted document".to_string())
    );
    assert_eq!(request.params.input_path, "");
    assert_eq!(request.params.output_path, None);
    assert_eq!(request.params.input_mode, "PlainText");
}

#[test]
fn long_document_text_file_request_infers_native_mode_from_extension_when_mode_is_default() {
    let mut state = EasydictUiState::default();
    state.long_document.selected_file = r"C:\Docs\notes.txt".to_string();
    state.long_document.input_mode = "pdf".to_string();
    state.long_document.service = "google".to_string();

    let request = build_long_document_request(&state, 8).expect("text file request");

    assert_eq!(request.params.input_mode, "PlainText");
    assert_eq!(
        request.params.output_path.as_deref(),
        Some(r"C:\Docs\notes_translated.txt")
    );
    assert!(long_document_request_can_route_natively(&request));
}

#[test]
fn long_document_markdown_file_request_infers_native_mode_from_extension_when_mode_is_default() {
    let mut state = EasydictUiState::default();
    state.long_document.selected_file = r"C:\Docs\paper.markdown".to_string();
    state.long_document.input_mode = "pdf".to_string();
    state.long_document.service = "google".to_string();

    let request = build_long_document_request(&state, 9).expect("markdown file request");

    assert_eq!(request.params.input_mode, "Markdown");
    assert_eq!(
        request.params.output_path.as_deref(),
        Some(r"C:\Docs\paper_translated.md")
    );
    assert!(long_document_request_can_route_natively(&request));
}

#[test]
fn long_document_text_modes_ignore_stale_page_range_and_stay_native() {
    let mut state = EasydictUiState::default();
    state.long_document.selected_file = r"C:\Docs\notes.txt".to_string();
    state.long_document.input_mode = "plaintext".to_string();
    state.long_document.page_range = "1-3".to_string();
    state.long_document.service = "google".to_string();

    let request = build_long_document_request(&state, 10).expect("text file request");

    assert_eq!(request.params.input_mode, "PlainText");
    assert_eq!(request.params.page_range, None);
    assert!(long_document_request_can_route_natively(&request));
}

#[test]
fn long_document_pdf_mode_preserves_page_range_for_native_route() {
    let mut state = EasydictUiState::default();
    state.long_document.selected_file = r"C:\Docs\paper.pdf".to_string();
    state.long_document.input_mode = "pdf".to_string();
    state.long_document.page_range = "1-3".to_string();
    state.long_document.service = "google".to_string();

    let request = build_long_document_request(&state, 11).expect("pdf file request");

    assert_eq!(request.params.input_mode, "Pdf");
    assert_eq!(request.params.page_range.as_deref(), Some("1-3"));
    assert!(long_document_request_can_route_natively(&request));
}

#[test]
fn long_document_file_request_uses_configured_output_folder_and_input_extension() {
    let mut state = EasydictUiState::default();
    state.long_document.selected_file = r"C:\Docs\paper.pdf".to_string();
    state.long_document.output_folder = r"D:\Translated".to_string();
    state.long_document.input_mode = "plaintext".to_string();

    let request = build_long_document_request(&state, 9).expect("file request");

    assert_eq!(
        request.params.output_path.as_deref(),
        Some(r"D:\Translated\paper_translated.txt")
    );
    assert_eq!(request.params.input_mode, "PlainText");
}

#[test]
fn long_document_inline_text_uses_configured_output_folder_when_available() {
    let mut state = EasydictUiState::default();
    state.long_document.selected_file = "No file selected".to_string();
    state.long_document.source_text = "# Intro".to_string();
    state.long_document.input_mode = "markdown".to_string();
    state.long_document.output_folder = r"D:\Translated".to_string();

    let request = build_long_document_request(&state, 10).expect("inline text request");

    assert_eq!(
        request.params.output_path.as_deref(),
        Some(r"D:\Translated\inline-document_translated.md")
    );
    assert_eq!(request.params.input_mode, "Markdown");
}

#[test]
fn app_update_long_document_translate_starts_runtime_task_only_in_long_document_mode() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let quick_task = app.update(Message::Translate);
    assert_eq!(task_kind(&quick_task), "none");
    assert!(!app.state.long_document.is_translating);

    app.state.mode = AppMode::LongDocument;
    app.state.long_document.selected_file = r"C:\Docs\paper.txt".to_string();
    app.state.long_document.input_mode = "plaintext".to_string();

    let long_doc_task = app.update(Message::Translate);

    assert_eq!(task_kind(&long_doc_task), "future");
    assert!(app.state.long_document.is_translating);
    assert_eq!(app.state.long_document.active_query_id, Some(1));
}

#[test]
fn app_update_long_document_browse_starts_file_dialog_only_in_long_document_mode() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let quick_task = app.update(Message::BrowseFile);
    assert_eq!(task_kind(&quick_task), "none");

    app.state.mode = AppMode::LongDocument;
    app.state.long_document.output_folder = r"C:\Docs".to_string();

    let browse_task = app.update(Message::BrowseFile);

    assert_eq!(task_kind(&browse_task), "file_dialog");

    let output_browse_task = app.update(Message::BrowseOutputFolder);
    assert_eq!(task_kind(&output_browse_task), "folder_dialog");

    app.state.long_document.is_translating = true;
    let locked_task = app.update(Message::BrowseFile);
    assert_eq!(task_kind(&locked_task), "none");
    let locked_output_task = app.update(Message::BrowseOutputFolder);
    assert_eq!(task_kind(&locked_output_task), "none");
}

#[test]
fn long_document_file_selection_updates_path_mode_and_output_folder() {
    let mut state = EasydictUiState::default();
    state.long_document.selected_file = "No file selected".to_string();
    state.long_document.input_mode = "pdf".to_string();
    state.long_document.last_error = Some("old error".to_string());
    state.long_document.last_output_path = Some(r"C:\Old\out.pdf".to_string());
    state.long_document.progress_percentage = Some(42.0);
    state.long_document.progress_detail = Some("old progress".to_string());
    state.long_document.last_translated_block = Some("old block".to_string());

    state.apply(Message::LongDocumentFileSelected(Some(
        r"C:\Docs\paper.markdown".to_string(),
    )));

    assert_eq!(state.long_document.selected_file, r"C:\Docs\paper.markdown");
    assert_eq!(state.long_document.input_mode, "markdown");
    assert_eq!(state.long_document.output_folder, r"C:\Docs");
    assert_eq!(state.long_document.status_text, "Ready");
    assert_eq!(state.long_document.last_error, None);
    assert_eq!(state.long_document.last_output_path, None);
    assert_eq!(state.long_document.progress_percentage, None);
    assert_eq!(state.long_document.progress_detail, None);
    assert_eq!(state.long_document.last_translated_block, None);

    state.apply(Message::LongDocumentFileSelected(None));
    assert_eq!(state.long_document.selected_file, r"C:\Docs\paper.markdown");

    state.long_document.last_error = Some("old output error".to_string());
    state.apply(Message::LongDocumentOutputFolderSelected(Some(
        r"D:\Translated".to_string(),
    )));
    assert_eq!(state.long_document.output_folder, r"D:\Translated");
    assert_eq!(state.long_document.status_text, "Output folder selected");
    assert_eq!(state.long_document.last_error, None);

    state.apply(Message::LongDocumentOutputFolderSelected(None));
    assert_eq!(state.long_document.output_folder, r"D:\Translated");
}

#[test]
fn long_document_settings_are_locked_while_translating() {
    let mut state = EasydictUiState::default();
    state.long_document.is_translating = true;
    state.long_document.source_text = "original text".to_string();
    state.long_document.selected_file = r"C:\Docs\paper.pdf".to_string();
    state.long_document.source_language = "en".to_string();
    state.long_document.target_language = "zh-Hans".to_string();
    state.long_document.service = "google".to_string();
    state.long_document.input_mode = "pdf".to_string();
    state.long_document.output_mode = "bilingual".to_string();
    state.long_document.concurrency = "4".to_string();
    state.long_document.page_range = "1-3".to_string();
    state.long_document.output_folder = r"C:\Docs".to_string();
    state.long_document.two_pass_context = true;

    state.apply(Message::LongDocumentSourceTextChanged(
        "changed text".to_string(),
    ));
    state.apply(Message::LongDocumentFileSelected(Some(
        r"C:\Other\changed.txt".to_string(),
    )));
    state.apply(Message::LongDocumentSourceLanguageChanged("ja".to_string()));
    state.apply(Message::LongDocumentTargetLanguageChanged("ko".to_string()));
    state.apply(Message::LongDocumentServiceChanged("openai".to_string()));
    state.apply(Message::LongDocumentInputModeChanged(
        "plaintext".to_string(),
    ));
    state.apply(Message::LongDocumentOutputModeChanged("mono".to_string()));
    state.apply(Message::LongDocumentConcurrencyChanged("8".to_string()));
    state.apply(Message::LongDocumentPageRangeChanged("9".to_string()));
    state.apply(Message::LongDocumentOutputFolderSelected(Some(
        r"D:\Translated".to_string(),
    )));
    state.apply(Message::ToggleTwoPassContext(false));

    assert_eq!(state.long_document.source_text, "original text");
    assert_eq!(state.long_document.selected_file, r"C:\Docs\paper.pdf");
    assert_eq!(state.long_document.source_language, "en");
    assert_eq!(state.long_document.target_language, "zh-Hans");
    assert_eq!(state.long_document.service, "google");
    assert_eq!(state.long_document.input_mode, "pdf");
    assert_eq!(state.long_document.output_mode, "bilingual");
    assert_eq!(state.long_document.concurrency, "4");
    assert_eq!(state.long_document.page_range, "1-3");
    assert_eq!(state.long_document.output_folder, r"C:\Docs");
    assert!(state.long_document.two_pass_context);

    state.long_document.is_translating = false;
    state.apply(Message::LongDocumentPageRangeChanged("9".to_string()));
    assert_eq!(state.long_document.page_range, "9");
}

#[test]
fn long_document_outcome_updates_output_status_and_history() {
    let mut state = EasydictUiState::default();
    state.long_document.active_query_id = Some(9);
    state.long_document.is_translating = true;

    apply_long_document_outcome(
        &mut state,
        LongDocumentOutcome {
            query_id: 9,
            input_label: "paper.md".to_string(),
            events: vec![],
            result: Ok(result(
                "Completed",
                Some(r"C:\Docs\paper.translated.md"),
                Some(r"C:\Docs\paper.bilingual.md"),
            )),
        },
    );

    assert_eq!(state.long_document.active_query_id, None);
    assert!(!state.long_document.is_translating);
    assert_eq!(state.long_document.last_error, None);
    assert_eq!(
        state.long_document.last_output_path.as_deref(),
        Some(r"C:\Docs\paper.bilingual.md")
    );
    assert_eq!(state.long_document.output_folder, r"C:\Docs");
    assert_eq!(state.long_document.status_text, "Completed (4/4)");
    assert_eq!(state.long_document.history[0].service_name, "paper.md");
    assert!(state.long_document.history[0]
        .body
        .contains("Output: C:\\Docs\\paper.bilingual.md"));
}

#[test]
fn stale_long_document_outcome_does_not_replace_newer_query() {
    let mut state = EasydictUiState::default();
    state.long_document.active_query_id = Some(10);
    state.long_document.is_translating = true;
    state.long_document.status_text = "Translating document".to_string();

    apply_long_document_outcome(
        &mut state,
        LongDocumentOutcome {
            query_id: 9,
            input_label: "old.md".to_string(),
            events: vec![],
            result: Err(LongDocumentBackendError::new("old failure")),
        },
    );

    assert_eq!(state.long_document.active_query_id, Some(10));
    assert!(state.long_document.is_translating);
    assert_eq!(state.long_document.status_text, "Translating document");
    assert_eq!(state.long_document.last_error, None);
}

#[test]
fn long_document_backend_failure_records_retryable_error() {
    let mut state = EasydictUiState::default();
    state.long_document.active_query_id = Some(3);
    state.long_document.is_translating = true;

    apply_long_document_outcome(
        &mut state,
        LongDocumentOutcome {
            query_id: 3,
            input_label: "paper.pdf".to_string(),
            events: vec![],
            result: Err(LongDocumentBackendError::new("worker unavailable")),
        },
    );

    assert!(!state.long_document.is_translating);
    assert_eq!(
        state.long_document.last_error.as_deref(),
        Some("worker unavailable")
    );
    assert_eq!(state.long_document.history[0].status, ResultStatus::Error);
}

#[test]
fn long_document_events_update_progress_detail_and_last_block() {
    let mut state = EasydictUiState::default();
    state.long_document.active_query_id = Some(12);
    state.long_document.is_translating = true;

    apply_long_document_outcome(
        &mut state,
        LongDocumentOutcome {
            query_id: 12,
            input_label: "paper.md".to_string(),
            events: vec![
                LongDocumentEvent::Status(StatusEventData {
                    message: "Parsing document".to_string(),
                }),
                LongDocumentEvent::Progress(ProgressEventData {
                    stage: "Translating".to_string(),
                    current_block: 1,
                    total_blocks: 4,
                    current_page: 2,
                    total_pages: 6,
                    percentage: 25.5,
                    current_block_preview: Some("First paragraph".to_string()),
                }),
                LongDocumentEvent::BlockTranslated(BlockTranslatedEventData {
                    chunk_index: 1,
                    page_number: Some(2),
                    source_block_id: Some("p2-b1".to_string()),
                    translated_text: "第一段".to_string(),
                    retry_count: 0,
                    last_error: None,
                }),
            ],
            result: Err(LongDocumentBackendError::new("worker unavailable")),
        },
    );

    assert!(!state.long_document.is_translating);
    assert_eq!(state.long_document.progress_percentage, Some(25.5));
    assert_eq!(
        state.long_document.progress_detail.as_deref(),
        Some("Translating: block 1/4, page 2/6 - First paragraph")
    );
    assert_eq!(
        state.long_document.last_translated_block.as_deref(),
        Some("第一段")
    );
    assert_eq!(state.long_document.status_text, "Error: worker unavailable");
}

#[test]
fn run_long_document_request_calls_backend_with_locked_params() {
    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: r"C:\Docs\paper.md".to_string(),
                input_mode: "markdown".to_string(),
                output_mode: "bilingual".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                page_range: "2".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        11,
    )
    .expect("request");
    let mut backend = RecordingLongDocBackend::ok(result(
        "Completed",
        Some(r"C:\Docs\paper.translated.md"),
        None,
    ));

    let outcome = run_long_document_request(&mut backend, request);

    assert!(outcome.result.is_ok());
    assert_eq!(backend.calls.len(), 1);
    assert_eq!(backend.calls[0].input_mode, "Markdown");
    assert_eq!(
        backend.calls[0].output_path.as_deref(),
        Some(r"C:\Docs\paper_translated.md")
    );
    assert_eq!(backend.calls[0].output_mode, "Bilingual");
    assert_eq!(backend.calls[0].page_range, None);
    assert_eq!(backend.settings.len(), 1);
    assert_eq!(
        backend.settings[0].long_doc_enable_document_context_pass,
        Some(true)
    );
}

#[test]
fn run_long_document_request_preserves_backend_events() {
    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: r"C:\Docs\paper.md".to_string(),
                input_mode: "markdown".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        13,
    )
    .expect("request");
    let event = LongDocumentEvent::Status(StatusEventData {
        message: "Parsing document".to_string(),
    });
    let mut backend = RecordingLongDocBackend::ok(result(
        "Completed",
        Some(r"C:\Docs\paper.translated.md"),
        None,
    ))
    .with_events(vec![event.clone()]);

    let outcome = run_long_document_request(&mut backend, request);

    assert_eq!(outcome.events, vec![event]);
    assert!(backend.events.is_empty());
}

#[test]
fn long_document_native_route_is_limited_to_text_modes_and_migrated_services() {
    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: r"C:\Docs\paper.txt".to_string(),
                input_mode: "plaintext".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        21,
    )
    .expect("plain text request");
    assert!(long_document_request_can_route_natively(&request));

    let mut paged_text_request = request.clone();
    paged_text_request.params.page_range = Some("1-2".to_string());
    assert!(long_document_request_can_route_natively(
        &paged_text_request
    ));

    let mut mdx_request = request.clone();
    mdx_request.params.service_id = "mdx::demo".to_string();
    assert!(!long_document_request_can_route_natively(&mdx_request));

    let mut google_dict_request = request.clone();
    google_dict_request.params.service_id = "google_web".to_string();
    assert!(!long_document_request_can_route_natively(
        &google_dict_request
    ));

    let pdf_request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: r"C:\Docs\paper.pdf".to_string(),
                input_mode: "pdf".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        22,
    )
    .expect("simple pdf request");
    assert!(long_document_request_can_route_natively(&pdf_request));

    let mut paged_pdf_request = pdf_request;
    paged_pdf_request.params.page_range = Some("1-2".to_string());
    assert!(long_document_request_can_route_natively(&paged_pdf_request));
}

#[test]
fn long_document_native_route_accepts_migrated_text_translation_services() {
    for service_id in [
        "ollama",
        "deepseek",
        "groq",
        "zhipu",
        "github",
        "custom-openai",
        "gemini",
        "doubao",
        "deepl",
        "caiyun",
        "niutrans",
        "volcano",
    ] {
        let request = build_long_document_request(
            &EasydictUiState {
                long_document: easydict_app::LongDocumentState {
                    selected_file: r"C:\Docs\paper.md".to_string(),
                    input_mode: "markdown".to_string(),
                    service: service_id.to_string(),
                    ..Default::default()
                },
                ..Default::default()
            },
            22,
        )
        .unwrap_or_else(|_| panic!("long document request should build for {service_id}"));

        assert!(
            long_document_request_can_route_natively(&request),
            "{service_id} should be accepted by the Rust-native text long document route"
        );
    }
}

#[test]
fn native_text_long_document_runner_translates_chunks_and_writes_outputs() {
    let temp_dir = unique_temp_dir("longdoc-native-text");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                custom_translation_prompt: "Preserve glossary terms.".to_string(),
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "# Intro\n\nFirst paragraph.\n\nSecond paragraph.".to_string(),
                input_mode: "markdown".to_string(),
                output_mode: "both".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        22,
    )
    .expect("native markdown request");
    let mut translator = RecordingNativeLongDocTranslator::default();

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome.result.expect("native long document result");

    assert_eq!(result.state, "Completed");
    assert_eq!(result.succeeded_chunks, result.total_chunks);
    let calls = translator.calls();
    assert!(!calls.is_empty());
    assert!(calls.iter().all(|call| {
        call.params.from.as_deref() == Some("en")
            && call.params.to.as_deref() == Some("zh")
            && call.params.services.as_deref() == Some(&["google".to_string()][..])
            && call.params.custom_prompt.as_deref() == Some("Preserve glossary terms.")
    }));

    let output_path = result.output_path.expect("monolingual output path");
    let bilingual_path = result.bilingual_output_path.expect("bilingual output path");
    let monolingual = fs::read_to_string(&output_path).expect("monolingual output");
    let bilingual = fs::read_to_string(&bilingual_path).expect("bilingual output");

    assert!(monolingual.contains("[zh]"));
    assert!(monolingual.contains("\r\n\r\n"));
    assert!(bilingual.contains("> # Intro"));
    assert!(bilingual.ends_with("\r\n---"));
    assert!(outcome.events.iter().any(|event| matches!(
        event,
        LongDocumentEvent::Status(status)
            if status.message == "Translating text document natively"
    )));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_formula_protection_sends_placeholders_and_restores_output() {
    let temp_dir = unique_temp_dir("longdoc-native-text-formula-protection");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                custom_translation_prompt: "Keep notation stable.".to_string(),
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: r"The value \alpha depends on h_{t-1}.".to_string(),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        71,
    )
    .expect("formula text request should build");
    let mut translator = RecordingNativeLongDocTranslator::default();

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome.result.expect("formula native run should succeed");

    let calls = translator.calls();
    assert_eq!(calls.len(), 1);
    assert!(calls[0].params.text.contains("{v0}"));
    assert!(calls[0].params.text.contains("{v1}"));
    assert!(!calls[0].params.text.contains(r"\alpha"));
    assert!(calls[0]
        .params
        .custom_prompt
        .as_deref()
        .unwrap_or_default()
        .contains("Keep all {vN} placeholders exactly as-is"));
    assert!(calls[0]
        .params
        .custom_prompt
        .as_deref()
        .unwrap_or_default()
        .contains("Keep notation stable."));

    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    assert!(output.contains(r"\alpha"));
    assert!(output.contains("h_{t-1}"));
    assert!(!output.contains("{v0}"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_formula_only_chunk_is_preserved_without_translator() {
    let temp_dir = unique_temp_dir("longdoc-native-text-formula-skip");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "αβγδεζηθ".to_string(),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        72,
    )
    .expect("formula-only request should build");
    let mut translator = RecordingNativeLongDocTranslator::default();

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("formula-only native run should preserve original");

    assert_eq!(translator.call_count(), 0);
    assert_eq!(result.state, "Completed");
    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    assert_eq!(output.trim(), "αβγδεζηθ");
    assert!(outcome.events.iter().any(|event| matches!(
        event,
        LongDocumentEvent::BlockTranslated(block)
            if block.translated_text == "αβγδεζηθ" && block.last_error.is_none()
    )));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_formula_quality_loss_retries_with_demoted_soft_span() {
    let temp_dir = unique_temp_dir("longdoc-native-text-formula-retry");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "We use h_{t-1} in the recurrence.".to_string(),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        73,
    )
    .expect("formula retry request should build");
    let mut translator = FormulaQualityRetryTranslator::default();

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("formula quality retry should recover on second attempt");

    let calls = translator.calls();
    assert_eq!(calls.len(), 2);
    assert!(calls[0].params.text.contains("{v0}"));
    assert!(calls[1].params.text.contains("$h_{t-1}$"));
    assert!(calls[1]
        .params
        .custom_prompt
        .as_deref()
        .unwrap_or_default()
        .contains("previous translation attempt lost some protected content"));

    let retry_block = outcome
        .events
        .iter()
        .find_map(|event| match event {
            LongDocumentEvent::BlockTranslated(block) => Some(block),
            _ => None,
        })
        .expect("block translated event should exist");
    assert_eq!(retry_block.retry_count, 1);
    assert_eq!(retry_block.last_error, None);

    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    assert!(output.contains("h_{t-1}"));
    assert!(!output.contains("{v0}"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_formula_cache_uses_original_hash_and_restored_text() {
    let temp_dir = unique_temp_dir("longdoc-native-text-formula-cache");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let source_text = r"The value \alpha depends on h_{t-1}.";
    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: true,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: source_text.to_string(),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        74,
    )
    .expect("formula cache request should build");
    request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());
    request.settings.enable_translation_cache = Some(true);

    let mut first_translator = RecordingNativeLongDocTranslator::default();
    run_native_text_long_document_request_with_translator(&mut first_translator, request.clone())
        .result
        .expect("first formula cache run should succeed");
    assert_eq!(first_translator.call_count(), 1);

    let mut cache = LongDocumentTranslationCache::open(temp_dir.join("translation_cache.db"))
        .expect("cache should open");
    let cached = cache
        .try_get(
            "google",
            "English",
            "SimplifiedChinese",
            &long_document_source_hash(source_text),
        )
        .expect("cache lookup should succeed")
        .expect("original source hash should be cached");
    assert!(cached.contains(r"\alpha"));
    assert!(cached.contains("h_{t-1}"));
    assert!(!cached.contains("{v0}"));
    drop(cache);

    request.params.output_path = Some(temp_dir.join("formula-cached.txt").display().to_string());
    let mut second_translator = RecordingNativeLongDocTranslator::default();
    let second =
        run_native_text_long_document_request_with_translator(&mut second_translator, request);
    let result = second
        .result
        .expect("second formula cache run should hit cache");

    assert_eq!(second_translator.call_count(), 0);
    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    assert!(output.contains(r"\alpha"));
    assert!(output.contains("h_{t-1}"));
    assert!(!output.contains("{v0}"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_cache_stores_and_reuses_persistent_chunks() {
    let temp_dir = unique_temp_dir("longdoc-native-text-cache");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut request = native_plaintext_cache_request(&temp_dir, true);
    let mut first_translator = RecordingNativeLongDocTranslator::default();

    let first = run_native_text_long_document_request_with_translator(
        &mut first_translator,
        request.clone(),
    );

    assert!(
        first
            .result
            .expect("first run should succeed")
            .succeeded_chunks
            > 0
    );
    assert_eq!(first_translator.call_count(), 1);

    let cache = LongDocumentTranslationCache::open(temp_dir.join("translation_cache.db"))
        .expect("cache should open");
    assert_eq!(cache.entry_count().expect("entry count should load"), 1);

    request.params.output_path = Some(temp_dir.join("cached-output.txt").display().to_string());
    let mut second_translator = RecordingNativeLongDocTranslator::default();

    let second =
        run_native_text_long_document_request_with_translator(&mut second_translator, request);
    let result = second.result.expect("second run should succeed from cache");

    assert_eq!(second_translator.call_count(), 0);
    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("cached output should be written");
    assert!(output.contains("[zh] First paragraph."));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_cache_disabled_ignores_persistent_hit() {
    let temp_dir = unique_temp_dir("longdoc-native-text-cache-disabled");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let source_text = "First paragraph.";
    let mut cache = LongDocumentTranslationCache::open(temp_dir.join("translation_cache.db"))
        .expect("cache should open");
    cache
        .set(
            "google",
            "English",
            "SimplifiedChinese",
            &long_document_source_hash(source_text),
            source_text,
            "cached translation should be ignored",
        )
        .expect("cache entry should be stored");

    let request = native_plaintext_cache_request(&temp_dir, false);
    let mut translator = RecordingNativeLongDocTranslator::default();

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome.result.expect("disabled cache run should succeed");

    assert_eq!(translator.call_count(), 1);
    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    assert!(output.contains("[zh] First paragraph."));
    assert!(!output.contains("cached translation should be ignored"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn long_document_request_clamps_max_concurrency_setting() {
    fn parsed_concurrency(value: &str) -> Option<u32> {
        build_long_document_request(
            &EasydictUiState {
                long_document: easydict_app::LongDocumentState {
                    selected_file: "No file selected".to_string(),
                    source_text: "A short document.".to_string(),
                    input_mode: "plaintext".to_string(),
                    source_language: "en".to_string(),
                    target_language: "zh-Hans".to_string(),
                    service: "google".to_string(),
                    concurrency: value.to_string(),
                    ..Default::default()
                },
                ..Default::default()
            },
            46,
        )
        .expect("request should build")
        .settings
        .long_doc_max_concurrency
    }

    assert_eq!(parsed_concurrency("0"), Some(1));
    assert_eq!(parsed_concurrency("8"), Some(8));
    assert_eq!(parsed_concurrency("17"), Some(16));
    assert_eq!(parsed_concurrency("999"), Some(16));
    assert_eq!(parsed_concurrency(""), None);
    assert_eq!(parsed_concurrency("abc"), None);
}

#[test]
fn native_text_long_document_runner_honors_bounded_concurrency_and_keeps_output_order() {
    let temp_dir = unique_temp_dir("longdoc-native-text-concurrency");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let source_text = (0..5)
        .map(|index| format!("section-{index} {}", "word ".repeat(700)))
        .collect::<Vec<_>>()
        .join("\n\n");
    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text,
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                concurrency: "2".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        45,
    )
    .expect("plain text request should build");
    let mut translator = RecordingNativeLongDocTranslator::delayed(25);

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("concurrent native run should succeed");

    assert!(translator.call_count() > 2);
    assert_eq!(translator.max_active_calls(), 2);

    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    let first = output
        .find("section-0")
        .expect("first section should exist");
    let last = output.find("section-4").expect("last section should exist");
    assert!(first < last);

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_runner_respects_clamped_max_concurrency() {
    let high_temp_dir = unique_temp_dir("longdoc-native-text-concurrency-clamped-high");
    fs::create_dir_all(&high_temp_dir).expect("temp dir should be created");
    let high_request = native_plaintext_concurrency_request(&high_temp_dir, Some("99"), 20);
    let mut high_translator = RecordingNativeLongDocTranslator::delayed(25);

    let high_outcome =
        run_native_text_long_document_request_with_translator(&mut high_translator, high_request);
    high_outcome
        .result
        .expect("high concurrency run should succeed");

    assert_eq!(high_translator.call_count(), 20);
    assert_eq!(high_translator.max_active_calls(), 16);
    fs::remove_dir_all(&high_temp_dir).ok();

    let default_temp_dir = unique_temp_dir("longdoc-native-text-concurrency-default");
    fs::create_dir_all(&default_temp_dir).expect("temp dir should be created");
    let default_request = native_plaintext_concurrency_request(&default_temp_dir, None, 4);
    let mut default_translator = RecordingNativeLongDocTranslator::delayed(10);

    let default_outcome = run_native_text_long_document_request_with_translator(
        &mut default_translator,
        default_request,
    );
    default_outcome
        .result
        .expect("default concurrency run should succeed");

    assert_eq!(default_translator.call_count(), 4);
    assert_eq!(default_translator.max_active_calls(), 1);
    fs::remove_dir_all(&default_temp_dir).ok();
}

#[test]
fn native_text_long_document_runner_preserves_output_order_when_chunks_complete_out_of_order() {
    let temp_dir = unique_temp_dir("longdoc-native-text-concurrency-out-of-order");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let source_text = native_long_text_markers(&["slow-0", "fast-1", "mid-2"]);
    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text,
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                concurrency: "3".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        47,
    )
    .expect("plain text request should build");
    let mut translator = RecordingNativeLongDocTranslator::with_marker_delays([
        ("slow-0", 80),
        ("fast-1", 1),
        ("mid-2", 20),
    ]);

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("out-of-order native run should succeed");

    assert_ne!(
        translator.completion_order(),
        vec![
            "slow-0".to_string(),
            "fast-1".to_string(),
            "mid-2".to_string()
        ]
    );
    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    let slow = output.find("slow-0").expect("slow chunk should exist");
    let fast = output.find("fast-1").expect("fast chunk should exist");
    let mid = output.find("mid-2").expect("mid chunk should exist");
    assert!(slow < fast);
    assert!(fast < mid);

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_runner_reports_original_failed_chunk_indexes() {
    let temp_dir = unique_temp_dir("longdoc-native-text-concurrency-failures");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "fail-1", "ok-2", "fail-3"]),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                concurrency: "2".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        48,
    )
    .expect("plain text request should build");
    let mut translator = RecordingNativeLongDocTranslator::failing_on(["fail-1", "fail-3"]);

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("partial native run should still export successful chunks");

    assert_eq!(result.state, "PartiallyCompleted");
    assert_eq!(result.succeeded_chunks, 2);
    assert_eq!(result.failed_chunk_indexes, Some(vec![1, 3]));
    let error_indexes = outcome
        .events
        .iter()
        .filter_map(|event| match event {
            LongDocumentEvent::BlockTranslated(block) if block.last_error.is_some() => {
                Some(block.chunk_index)
            }
            _ => None,
        })
        .collect::<Vec<_>>();
    assert_eq!(error_indexes, vec![1, 3]);

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_runner_retries_failed_chunk_once_and_keeps_output_order() {
    let temp_dir = unique_temp_dir("longdoc-native-text-retry-success");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "retry-1"]),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                concurrency: "2".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        51,
    )
    .expect("plain text request should build");
    let mut translator = RecordingNativeLongDocTranslator::failing_first_attempt_on(["retry-1"]);

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("transient retry native run should succeed");

    assert_eq!(result.state, "Completed");
    assert_eq!(translator.call_count(), 3);
    let retry_block = outcome
        .events
        .iter()
        .find_map(|event| match event {
            LongDocumentEvent::BlockTranslated(block) if block.chunk_index == 1 => Some(block),
            _ => None,
        })
        .expect("retry block event should be emitted");
    assert_eq!(retry_block.retry_count, 1);
    assert_eq!(retry_block.last_error, None);

    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    let ok = output.find("ok-0").expect("ok chunk should exist");
    let retried = output.find("retry-1").expect("retried chunk should exist");
    assert!(ok < retried);

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_runner_fails_after_single_retry_with_original_index() {
    let temp_dir = unique_temp_dir("longdoc-native-text-retry-failure");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "fail-1"]),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                concurrency: "2".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        52,
    )
    .expect("plain text request should build");
    let mut translator = RecordingNativeLongDocTranslator::failing_on(["fail-1"]);

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("partial retry native run should still export successful chunks");

    assert_eq!(result.state, "PartiallyCompleted");
    assert_eq!(result.succeeded_chunks, 1);
    assert_eq!(result.failed_chunk_indexes, Some(vec![1]));
    assert_eq!(translator.call_count(), 3);
    let failure_block = outcome
        .events
        .iter()
        .find_map(|event| match event {
            LongDocumentEvent::BlockTranslated(block) if block.last_error.is_some() => Some(block),
            _ => None,
        })
        .expect("failure block event should be emitted");
    assert_eq!(failure_block.chunk_index, 1);
    assert_eq!(failure_block.retry_count, 1);
    assert!(failure_block.translated_text.starts_with("fail-1"));
    assert!(failure_block
        .last_error
        .as_deref()
        .unwrap_or_default()
        .contains("failed chunk fail-1"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_cache_hits_do_not_take_translator_slots() {
    let temp_dir = unique_temp_dir("longdoc-native-text-cache-concurrency");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let source_text = native_long_text_markers(&["cached-0", "miss-1", "cached-2", "miss-3"]);
    let mut probe_request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text,
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                concurrency: "2".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        49,
    )
    .expect("plain text request should build");
    let mut probe_translator = RecordingNativeLongDocTranslator::default();
    run_native_text_long_document_request_with_translator(
        &mut probe_translator,
        probe_request.clone(),
    )
    .result
    .expect("probe native run should succeed");
    let split_chunks = probe_translator
        .calls()
        .into_iter()
        .map(|call| call.params.text)
        .collect::<Vec<_>>();
    assert_eq!(split_chunks.len(), 4);

    let mut cache = LongDocumentTranslationCache::open(temp_dir.join("translation_cache.db"))
        .expect("cache should open");
    for (index, translated) in [(0, "[cached] cached-0"), (2, "[cached] cached-2")] {
        cache
            .set(
                "google",
                "English",
                "SimplifiedChinese",
                &long_document_source_hash(&split_chunks[index]),
                &split_chunks[index],
                translated,
            )
            .expect("cache entry should be stored");
    }
    drop(cache);

    probe_request.params.output_path = Some(
        temp_dir
            .join("mixed-cache-output.txt")
            .display()
            .to_string(),
    );
    probe_request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());
    probe_request.settings.enable_translation_cache = Some(true);
    let mut translator = RecordingNativeLongDocTranslator::delayed(25);

    let outcome =
        run_native_text_long_document_request_with_translator(&mut translator, probe_request);
    let result = outcome
        .result
        .expect("mixed cache native run should succeed");

    assert_eq!(translator.call_count(), 2);
    assert_eq!(translator.max_active_calls(), 2);
    let called_text = translator
        .calls()
        .into_iter()
        .map(|call| call.params.text)
        .collect::<Vec<_>>();
    assert_eq!(called_text.len(), 2);
    assert!(called_text.contains(&split_chunks[1]));
    assert!(called_text.contains(&split_chunks[3]));
    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    let cached_0 = output.find("[cached] cached-0").expect("cached 0 output");
    let miss_1 = output.find("[zh] miss-1").expect("miss 1 output");
    let cached_2 = output.find("[cached] cached-2").expect("cached 2 output");
    let miss_3 = output.find("[zh] miss-3").expect("miss 3 output");
    assert!(cached_0 < miss_1);
    assert!(miss_1 < cached_2);
    assert!(cached_2 < miss_3);

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_cache_ignores_blank_cached_translation() {
    let temp_dir = unique_temp_dir("longdoc-native-text-cache-blank");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let source_text = "First paragraph.";
    let mut cache = LongDocumentTranslationCache::open(temp_dir.join("translation_cache.db"))
        .expect("cache should open");
    cache
        .set(
            "google",
            "English",
            "SimplifiedChinese",
            &long_document_source_hash(source_text),
            source_text,
            "   ",
        )
        .expect("blank cache entry should be stored");
    drop(cache);

    let request = native_plaintext_cache_request(&temp_dir, true);
    let mut translator = RecordingNativeLongDocTranslator::default();

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("blank cache run should fall back to translator");

    assert_eq!(translator.call_count(), 1);
    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    assert!(output.contains("[zh] First paragraph."));

    let mut cache = LongDocumentTranslationCache::open(temp_dir.join("translation_cache.db"))
        .expect("cache should reopen");
    let cached = cache
        .try_get(
            "google",
            "English",
            "SimplifiedChinese",
            &long_document_source_hash(source_text),
        )
        .expect("cache read should succeed")
        .expect("cache entry should still exist");
    assert_eq!(cached, "[zh] First paragraph.");

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_runner_maps_extended_languages_to_quick_codes() {
    let temp_dir = unique_temp_dir("longdoc-native-extended-language-codes");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "sk".to_string(),
                target_language: "pt".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        37,
    )
    .expect("native extended-language request");

    assert_eq!(request.params.from, "Slovak");
    assert_eq!(request.params.to, "Portuguese");

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    outcome.result.expect("native long document result");

    assert!(translator.calls().iter().all(|call| {
        call.params.from.as_deref() == Some("sk") && call.params.to.as_deref() == Some("pt")
    }));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_text_long_document_runner_extracts_pdf_and_writes_text_outputs() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-text");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("paper.pdf");
    fs::write(&input_path, minimal_pdf_with_text("Hello PDF")).expect("pdf should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                output_mode: "both".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        25,
    )
    .expect("native pdf request");
    assert_eq!(request.params.input_mode, "Pdf");
    assert!(request.params.page_range.is_none());
    assert!(long_document_request_can_route_natively(&request));

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome.result.expect("native pdf long document result");

    assert_eq!(result.state, "Completed");
    let output_path = result.output_path.expect("monolingual output path");
    assert!(
        output_path.ends_with("paper_translated.pdf"),
        "native PDF text output should write a real PDF when content-stream replacement succeeds: {output_path}"
    );
    let bilingual_path = result.bilingual_output_path.expect("bilingual output path");
    assert!(bilingual_path.ends_with("paper_translated-bilingual.txt"));

    let output_pdf = lopdf::Document::load(&output_path).expect("native PDF output should open");
    assert_eq!(output_pdf.get_pages().len(), 1);
    let monolingual_pages =
        easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
            &output_path,
        )
        .expect("native PDF output text should extract");
    let bilingual = fs::read_to_string(&bilingual_path).expect("bilingual output");
    assert!(translator.calls()[0].params.text.contains("Hello PDF"));
    assert!(monolingual_pages.join("\n").contains("[zh] Hello PDF"));
    assert!(bilingual.contains("Hello PDF"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_hex_content_stream_runner_exports_real_pdf() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-hex-export");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("hex-stream.pdf");
    let stream = format!(
        "BT /F1 24 Tf 100 700 Td <{}> Tj ET",
        ascii_hex("Fallback hex route")
    );
    fs::write(
        &input_path,
        minimal_pdf_with_page_streams(&[stream.as_str()]),
    )
    .expect("pdf should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        68,
    )
    .expect("native pdf request");
    assert!(long_document_request_can_route_natively(&request));

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome.result.expect("native pdf long document result");

    assert_eq!(result.state, "Completed");
    let output_path = result.output_path.expect("monolingual output path");
    assert!(output_path.ends_with("hex-stream_translated.pdf"));
    assert!(result.bilingual_output_path.is_none());
    let output_pdf = lopdf::Document::load(&output_path).expect("native PDF output should open");
    assert_eq!(output_pdf.get_pages().len(), 1);
    let monolingual_pages =
        easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
            &output_path,
        )
        .expect("native PDF output text should extract");
    assert!(translator.calls()[0]
        .params
        .text
        .contains("Fallback hex route"));
    assert!(monolingual_pages
        .join("\n")
        .contains("[zh] Fallback hex route"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_text_long_document_runner_filters_page_range() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-page-range");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("paper.pdf");
    fs::write(
        &input_path,
        minimal_pdf_with_pages(&["First PDF page", "Second PDF page", "Third PDF page"]),
    )
    .expect("pdf should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                page_range: "2".to_string(),
                output_mode: "both".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        26,
    )
    .expect("native pdf request");
    assert_eq!(request.params.page_range.as_deref(), Some("2"));
    assert!(long_document_request_can_route_natively(&request));

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome.result.expect("native pdf long document result");

    assert_eq!(result.state, "Completed");
    let calls = translator.calls();
    assert_eq!(calls.len(), 1);
    let translated_text = &calls[0].params.text;
    assert!(translated_text.contains("Second PDF page"));
    assert!(!translated_text.contains("First PDF page"));
    assert!(!translated_text.contains("Third PDF page"));

    let bilingual_path = result.bilingual_output_path.expect("bilingual output path");
    let bilingual = fs::read_to_string(&bilingual_path).expect("bilingual output");
    assert!(bilingual.contains("Second PDF page"));
    assert!(!bilingual.contains("First PDF page"));
    assert!(!bilingual.contains("Third PDF page"));

    let output_path = result.output_path.expect("monolingual output path");
    assert!(output_path.ends_with("paper_translated.pdf"));
    let output_pdf = lopdf::Document::load(&output_path).expect("native PDF output should open");
    assert_eq!(output_pdf.get_pages().len(), 1);
    let monolingual_pages =
        easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
            &output_path,
        )
        .expect("native PDF output text should extract");
    let monolingual = monolingual_pages.join("\n");
    assert!(monolingual.contains("Second PDF page"));
    assert!(!monolingual.contains("First PDF page"));
    assert!(!monolingual.contains("Third PDF page"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn empty_native_pdf_text_fails_locally_without_long_document_backend() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-empty-fallback");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("scanned-placeholder.pdf");
    fs::write(&input_path, minimal_pdf_with_text("   ")).expect("pdf should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        27,
    )
    .expect("native pdf request");
    assert!(long_document_request_can_route_natively(&request));

    let mut backend = RecordingLongDocBackend::ok(result(
        "Completed",
        Some(
            temp_dir
                .join("worker-output.pdf")
                .to_string_lossy()
                .as_ref(),
        ),
        None,
    ));

    let outcome = run_long_document_request_with_native_route(&mut backend, request);
    let error = outcome
        .result
        .expect_err("empty native PDF text should fail locally");

    assert_eq!(backend.calls.len(), 0);
    assert!(error.message.contains("no selectable text"));
    assert!(!error.message.to_ascii_lowercase().contains("worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn failed_native_pdf_text_extraction_fails_locally_without_long_document_backend() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-extract-fallback");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("complex.pdf");
    fs::write(&input_path, b"%PDF-1.4\nnot a valid parseable pdf").expect("pdf should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        28,
    )
    .expect("native pdf request");
    assert!(long_document_request_can_route_natively(&request));

    let mut backend = RecordingLongDocBackend::ok(result(
        "Completed",
        Some(
            temp_dir
                .join("worker-output.pdf")
                .to_string_lossy()
                .as_ref(),
        ),
        None,
    ));

    let outcome = run_long_document_request_with_native_route(&mut backend, request);
    let error = outcome
        .result
        .expect_err("failed native PDF text extraction should fail locally");

    assert_eq!(backend.calls.len(), 0);
    assert!(error.message.contains("Could not extract PDF text"));
    assert!(!error.message.to_ascii_lowercase().contains("worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_content_stream_fallback_extracts_literal_text_by_page() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-content-stream-fallback");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("literal-stream.pdf");
    fs::write(
        &input_path,
        minimal_pdf_with_pages(&["Fallback first page", "Fallback second page"]),
    )
    .expect("pdf should be written");

    let pages = easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
        input_path.to_string_lossy().as_ref(),
    )
    .expect("content streams should be extracted");

    assert_eq!(
        pages.iter().map(|page| page.trim()).collect::<Vec<_>>(),
        ["Fallback first page", "Fallback second page"]
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_content_stream_fallback_extracts_hex_text_by_page() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-hex-stream-fallback");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("hex-stream.pdf");
    let stream = format!(
        "BT /F1 24 Tf 100 700 Td <{}> Tj ET",
        ascii_hex("Fallback hex page")
    );
    fs::write(
        &input_path,
        minimal_pdf_with_page_streams(&[stream.as_str()]),
    )
    .expect("pdf should be written");

    let pages = easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
        input_path.to_string_lossy().as_ref(),
    )
    .expect("content streams should be extracted");

    assert_eq!(pages.len(), 1);
    assert!(pages[0].contains("Fallback hex page"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn failed_native_pdf_text_extraction_does_not_probe_packaged_longdoc_worker() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-retained-worker-disabled");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("complex.pdf");
    fs::write(&input_path, b"%PDF-1.4\nnot a valid parseable pdf").expect("pdf should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        30,
    )
    .expect("native pdf request");
    assert!(long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir_and_worker_policy(
        request,
        &temp_dir,
        RetainedWorkerPolicy::all_enabled(),
    );
    let error = outcome
        .result
        .expect_err("native PDF extraction failure should stay local");

    assert!(error.message.contains("Could not extract PDF text"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));
    assert!(!error.message.contains("executable"));
    assert!(!error.message.contains("requires a Rust-native route"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn missing_native_pdf_file_does_not_fall_back_to_long_document_backend() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-missing-input");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("missing.pdf");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        29,
    )
    .expect("native pdf request");
    assert!(long_document_request_can_route_natively(&request));

    let mut backend = RecordingLongDocBackend::ok(result(
        "Completed",
        Some(
            temp_dir
                .join("worker-output.pdf")
                .to_string_lossy()
                .as_ref(),
        ),
        None,
    ));

    let outcome = run_long_document_request_with_native_route(&mut backend, request);
    let error = outcome
        .result
        .expect_err("missing input should remain a local input error");

    assert_eq!(backend.calls.len(), 0);
    assert!(error.message.contains("Could not read PDF document"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_packaged_app_dir_runner_does_not_spawn_packaged_worker() {
    let temp_dir = unique_temp_dir("longdoc-native-missing-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "openai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        23,
    )
    .expect("native text request");

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome.result.unwrap_err();

    assert!(error.message.contains("API key"));
    assert!(
        !error.message.contains("Long Document worker"),
        "native text longdoc should fail locally before packaged worker fallback: {}",
        error.message
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_file_long_document_packaged_app_dir_runner_does_not_spawn_worker_when_mode_is_default(
) {
    let temp_dir = unique_temp_dir("longdoc-native-file-default-mode-missing-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("notes.txt");
    fs::write(&input_path, "A short document").expect("text input should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                page_range: "1-3".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "openai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        24,
    )
    .expect("native text file request");

    assert_eq!(request.params.input_mode, "PlainText");
    assert_eq!(request.params.page_range, None);
    assert!(long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome.result.unwrap_err();

    assert!(error.message.contains("API key"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn stale_text_page_range_long_document_packaged_app_dir_runner_does_not_spawn_worker() {
    let temp_dir = unique_temp_dir("longdoc-native-text-stale-page-range-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("notes.txt");
    fs::write(&input_path, "A short document").expect("text input should be written");

    let mut request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "openai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        35,
    )
    .expect("native text file request");
    request.params.page_range = Some("1-3".to_string());

    assert_eq!(request.params.input_mode, "PlainText");
    assert!(long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("native text route should fail at the native provider");

    assert!(error.message.contains("API key"));
    assert!(
        !error.message.contains("Long Document worker"),
        "stale text page range should not spawn the retained worker: {}",
        error.message
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_all_pages_packaged_app_dir_runner_does_not_spawn_worker() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-all-pages-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("paper.pdf");
    fs::write(
        &input_path,
        minimal_pdf_with_pages(&["First PDF page", "Second PDF page"]),
    )
    .expect("pdf should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                page_range: "all".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "openai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        34,
    )
    .expect("native pdf request");

    assert_eq!(request.params.input_mode, "Pdf");
    assert_eq!(request.params.page_range.as_deref(), Some("all"));
    assert!(long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("native PDF text route should fail at the native provider");

    assert!(error.message.contains("API key"));
    assert!(
        !error.message.contains("Long Document worker"),
        "selectable PDF text route should not spawn the retained worker: {}",
        error.message
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_hex_content_stream_packaged_app_dir_runner_does_not_spawn_worker() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-hex-stream-no-worker");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("hex-stream.pdf");
    let stream = format!(
        "BT /F1 24 Tf 100 700 Td <{}> Tj ET",
        ascii_hex("Fallback hex route")
    );
    fs::write(
        &input_path,
        minimal_pdf_with_page_streams(&[stream.as_str()]),
    )
    .expect("pdf should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                page_range: "all".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "openai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        53,
    )
    .expect("native pdf request");

    assert_eq!(request.params.input_mode, "Pdf");
    assert!(long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("native PDF hex text route should fail at the native provider");

    assert!(error.message.contains("API key"));
    assert!(
        !error.message.contains("Long Document worker"),
        "hex content-stream PDF route should not spawn the retained worker: {}",
        error.message
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn missing_worker_file_long_document_packaged_app_dir_runner_does_not_spawn_worker() {
    let temp_dir = unique_temp_dir("longdoc-worker-missing-file-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("missing.txt");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        31,
    )
    .expect("worker-routed missing file request");

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("missing input should fail locally before worker startup");

    assert!(error.message.contains("Could not read long document input"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn invalid_native_output_folder_fails_locally_without_provider_or_worker() {
    let temp_dir = unique_temp_dir("longdoc-native-output-folder-file-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_folder_file = temp_dir.join("not-a-folder");
    fs::write(&output_folder_file, "not a directory").expect("conflicting file should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: output_folder_file.to_string_lossy().to_string(),
                service: "openai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        32,
    )
    .expect("native output preflight request");

    assert!(long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("invalid output folder should fail before native provider calls");

    assert!(error
        .message
        .contains("Could not create long document output folder"));
    assert!(!error.message.contains("API key"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn invalid_worker_output_folder_fails_locally_without_worker_spawn() {
    let temp_dir = unique_temp_dir("longdoc-worker-output-folder-file-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_folder_file = temp_dir.join("not-a-folder");
    fs::write(&output_folder_file, "not a directory").expect("conflicting file should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: output_folder_file.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        33,
    )
    .expect("worker output preflight request");

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("invalid output folder should fail before worker startup");

    assert!(error
        .message
        .contains("Could not create long document output folder"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_target_auto_long_document_fails_locally_without_provider_or_worker() {
    let temp_dir = unique_temp_dir("longdoc-native-target-auto-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "openai".to_string(),
                target_language: "auto".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        35,
    )
    .expect("native target-auto request");

    assert_eq!(request.params.to, "Auto");
    assert!(long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("target Auto should fail before native provider calls");

    assert!(error.message.contains("target language cannot be Auto"));
    assert!(!error.message.contains("API key"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn worker_target_auto_long_document_fails_locally_without_worker_spawn() {
    let temp_dir = unique_temp_dir("longdoc-worker-target-auto-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short local AI document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                target_language: "auto".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        36,
    )
    .expect("worker target-auto request");

    assert_eq!(request.params.to, "Auto");
    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("target Auto should fail before worker startup");

    assert!(error.message.contains("target language cannot be Auto"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn stale_dictionary_long_document_service_fails_locally_without_worker_spawn() {
    let temp_dir = unique_temp_dir("longdoc-stale-dictionary-service-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "mdx::demo".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        32,
    )
    .expect("stale dictionary service request");

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("dictionary service should fail locally before worker startup");

    assert!(error
        .message
        .contains("Dictionary services are not available"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn registered_dictionary_long_document_service_fails_locally_without_worker_spawn() {
    let temp_dir = unique_temp_dir("longdoc-registered-dictionary-service-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "google_web".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        33,
    )
    .expect("registered dictionary service request");

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("registered dictionary service should fail locally before worker startup");

    assert!(error
        .message
        .contains("Dictionary services are not available"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn unknown_long_document_service_fails_locally_without_worker_spawn() {
    let temp_dir = unique_temp_dir("longdoc-unknown-service-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "legacy-dotnet-service".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        34,
    )
    .expect("unknown service request");

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("unknown service should fail locally before worker startup");

    assert!(error.message.contains("is not registered"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn local_ai_long_document_worker_route_fails_locally_without_nested_dotnet_workers() {
    let temp_dir = unique_temp_dir("longdoc-local-ai-no-nested-dotnet-workers");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("source.txt");
    fs::write(
        &input_path,
        "A long document that still needs the retained worker.",
    )
    .expect("input file should be written");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::OPENVINO.to_string(),
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        33,
    )
    .expect("local AI worker-routed request");

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, &temp_dir);
    let error = outcome
        .result
        .expect_err("non-native LocalAI long document should fail before worker startup");

    assert!(error.message.contains("requires a Rust-native route"));
    assert!(error.message.contains(".NET workers"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn legacy_local_ai_long_document_service_id_maps_to_native_windows_local_ai_route() {
    let temp_dir = unique_temp_dir("longdoc-native-local-ai-legacy-id");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::AUTO.to_string(),
                foundry_local_endpoint: "foundry-local-invalid".to_string(),
                foundry_local_model: "qwen2.5-0.5b".to_string(),
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short local AI document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "local-ai".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        25,
    )
    .expect("native local AI text request");

    assert_eq!(request.params.service_id, "windows-local-ai");
    assert!(long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("invalid native endpoint should fail locally");

    assert!(
        !error.message.contains("Long Document worker"),
        "native local AI longdoc should fail locally before packaged worker fallback: {}",
        error.message
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn stale_foundry_local_long_document_service_id_maps_to_windows_local_ai_route() {
    let temp_dir = unique_temp_dir("longdoc-native-foundry-local-stale-id");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::OPENVINO.to_string(),
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A stale Foundry Local document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "foundry-local".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        36,
    )
    .expect("stale foundry local request");

    assert_eq!(request.params.service_id, "windows-local-ai");
    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("non-native stale Foundry Local longdoc should fail before worker startup");

    assert!(error.message.contains("requires a Rust-native route"));
    assert!(error.message.contains(".NET workers"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn direct_stale_foundry_local_long_document_request_fails_before_worker_startup() {
    let temp_dir = unique_temp_dir("longdoc-direct-foundry-local-stale-id");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("source.txt");
    fs::write(&input_path, "A direct stale Foundry Local document")
        .expect("input file should be written");

    let mut request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "google".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        37,
    )
    .expect("direct stale foundry request");
    request.params.service_id = "foundry-local".to_string();

    let outcome = run_long_document_request_with_packaged_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("direct stale Foundry Local longdoc should fail before worker startup");

    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains("is not registered"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn settings_section_change_emits_scroll_reset_to_top() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let task = app.update(Message::SettingsSectionChanged("services".to_string()));

    assert_eq!(task_kind(&task), "scroll_to_top");
    match task {
        Task::ScrollToTop(id) => assert_eq!(id, "MainScrollViewer"),
        other => panic!("expected scroll-to-top task, got {}", task_kind(&other)),
    }
    // The section change is still applied.
    assert_eq!(
        app.state.settings.selected_section,
        easydict_app::SettingsSection::Services
    );
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
        Task::ReadClipboardText(_) => "read_clipboard",
        Task::CaptureScreenRegion { .. } => "capture_screen",
        Task::CaptureScreenWindows { .. } => "capture_screen_windows",
        Task::OpenFileDialog { .. } => "file_dialog",
        Task::OpenFolderDialog { .. } => "folder_dialog",
    }
}

fn result(
    state: &str,
    output_path: Option<&str>,
    bilingual_output_path: Option<&str>,
) -> TranslateDocumentResult {
    TranslateDocumentResult {
        state: state.to_string(),
        output_path: output_path.map(str::to_string),
        bilingual_output_path: bilingual_output_path.map(str::to_string),
        total_chunks: 4,
        succeeded_chunks: 4,
        failed_chunk_indexes: None,
        quality_report: None,
        result_json_path: None,
    }
}

struct RecordingLongDocBackend {
    settings: Vec<SettingsSnapshot>,
    calls: Vec<TranslateDocumentParams>,
    result: Result<TranslateDocumentResult, LongDocumentBackendError>,
    events: Vec<LongDocumentEvent>,
}

impl RecordingLongDocBackend {
    fn ok(result: TranslateDocumentResult) -> Self {
        Self {
            settings: Vec::new(),
            calls: Vec::new(),
            result: Ok(result),
            events: Vec::new(),
        }
    }

    fn with_events(mut self, events: Vec<LongDocumentEvent>) -> Self {
        self.events = events;
        self
    }
}

impl LongDocumentBackend for RecordingLongDocBackend {
    fn configure_longdoc_settings(
        &mut self,
        settings: &SettingsSnapshot,
    ) -> Result<(), LongDocumentBackendError> {
        self.settings.push(settings.clone());
        Ok(())
    }

    fn longdoc_translate(
        &mut self,
        params: &TranslateDocumentParams,
    ) -> Result<TranslateDocumentResult, LongDocumentBackendError> {
        self.calls.push(params.clone());
        self.result.clone()
    }

    fn take_longdoc_events(&mut self) -> Vec<LongDocumentEvent> {
        std::mem::take(&mut self.events)
    }
}

#[derive(Clone, Default)]
struct RecordingNativeLongDocTranslator {
    calls: Arc<Mutex<Vec<QuickTranslateServiceRequest>>>,
    active_calls: Arc<Mutex<usize>>,
    max_active_calls: Arc<Mutex<usize>>,
    completion_order: Arc<Mutex<Vec<String>>>,
    fail_substrings: Arc<Vec<String>>,
    transient_failures_by_substring: Arc<Mutex<Vec<(String, usize)>>>,
    delay_by_substring: Arc<Vec<(String, u64)>>,
    delay_ms: u64,
}

impl NativeLongDocumentTranslator for RecordingNativeLongDocTranslator {
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError> {
        {
            let mut active = self.active_calls.lock().expect("active lock");
            *active += 1;
            let mut max_active = self.max_active_calls.lock().expect("max active lock");
            *max_active = (*max_active).max(*active);
        }
        if self.delay_ms > 0 {
            let delay_ms = self
                .delay_by_substring
                .iter()
                .find(|(needle, _)| request.params.text.contains(needle))
                .map(|(_, delay_ms)| *delay_ms)
                .unwrap_or(self.delay_ms);
            thread::sleep(std::time::Duration::from_millis(delay_ms));
        }
        let marker = request
            .params
            .text
            .split_whitespace()
            .next()
            .unwrap_or("")
            .to_string();
        let transient_failure = {
            let mut failures = self
                .transient_failures_by_substring
                .lock()
                .expect("transient failures lock");
            failures
                .iter_mut()
                .find(|(needle, remaining)| *remaining > 0 && request.params.text.contains(needle))
                .map(|(needle, remaining)| {
                    *remaining -= 1;
                    needle.clone()
                })
        };
        let result = if transient_failure.is_some() {
            Err(LongDocumentBackendError::new(format!(
                "transient chunk {marker}"
            )))
        } else if self
            .fail_substrings
            .iter()
            .any(|needle| request.params.text.contains(needle))
        {
            Err(LongDocumentBackendError::new(format!(
                "failed chunk {marker}"
            )))
        } else {
            Ok(format!("[zh] {}", request.params.text.trim()))
        };
        self.calls.lock().expect("calls lock").push(request);
        self.completion_order
            .lock()
            .expect("completion order lock")
            .push(marker);
        *self.active_calls.lock().expect("active lock") -= 1;
        result
    }
}

impl RecordingNativeLongDocTranslator {
    fn delayed(delay_ms: u64) -> Self {
        Self {
            delay_ms,
            ..Default::default()
        }
    }

    fn with_marker_delays(delays: impl IntoIterator<Item = (&'static str, u64)>) -> Self {
        Self {
            delay_ms: 1,
            delay_by_substring: Arc::new(
                delays
                    .into_iter()
                    .map(|(marker, delay_ms)| (marker.to_string(), delay_ms))
                    .collect(),
            ),
            ..Default::default()
        }
    }

    fn failing_on(markers: impl IntoIterator<Item = &'static str>) -> Self {
        Self {
            fail_substrings: Arc::new(markers.into_iter().map(str::to_string).collect()),
            ..Default::default()
        }
    }

    fn failing_first_attempt_on(markers: impl IntoIterator<Item = &'static str>) -> Self {
        Self {
            transient_failures_by_substring: Arc::new(Mutex::new(
                markers
                    .into_iter()
                    .map(|marker| (marker.to_string(), 1))
                    .collect(),
            )),
            ..Default::default()
        }
    }

    fn calls(&self) -> Vec<QuickTranslateServiceRequest> {
        self.calls.lock().expect("calls lock").clone()
    }

    fn call_count(&self) -> usize {
        self.calls.lock().expect("calls lock").len()
    }

    fn max_active_calls(&self) -> usize {
        *self.max_active_calls.lock().expect("max active lock")
    }

    fn completion_order(&self) -> Vec<String> {
        self.completion_order
            .lock()
            .expect("completion order lock")
            .clone()
    }
}

#[derive(Clone, Default)]
struct FormulaQualityRetryTranslator {
    calls: Arc<Mutex<Vec<QuickTranslateServiceRequest>>>,
}

impl NativeLongDocumentTranslator for FormulaQualityRetryTranslator {
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError> {
        let call_index = {
            let mut calls = self.calls.lock().expect("calls lock");
            calls.push(request.clone());
            calls.len()
        };

        if call_index == 1 {
            Ok("[zh] placeholder was lost".to_string())
        } else {
            Ok(format!("[zh] {}", request.params.text.trim()))
        }
    }
}

impl FormulaQualityRetryTranslator {
    fn calls(&self) -> Vec<QuickTranslateServiceRequest> {
        self.calls.lock().expect("calls lock").clone()
    }
}

fn native_plaintext_cache_request(
    temp_dir: &std::path::Path,
    enable_cache: bool,
) -> easydict_app::LongDocumentServiceRequest {
    let mut request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "First paragraph.".to_string(),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        44,
    )
    .expect("plain text request should build");
    request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());
    request.settings.enable_translation_cache = Some(enable_cache);
    request
}

fn native_plaintext_concurrency_request(
    temp_dir: &std::path::Path,
    concurrency: Option<&str>,
    chunk_count: usize,
) -> easydict_app::LongDocumentServiceRequest {
    let source_text = native_long_text_markers(
        &(0..chunk_count)
            .map(|index| format!("chunk-{index}"))
            .collect::<Vec<_>>(),
    );

    build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text,
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                concurrency: concurrency.unwrap_or_default().to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        50,
    )
    .expect("plain text request should build")
}

fn native_long_text_markers<S: AsRef<str>>(markers: &[S]) -> String {
    markers
        .iter()
        .map(|marker| native_long_text_marker(marker.as_ref()))
        .collect::<Vec<_>>()
        .join("\n\n")
}

fn native_long_text_marker(marker: &str) -> String {
    format!("{marker} {}", "word ".repeat(300))
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!("{prefix}-{stamp}"))
}

fn minimal_pdf_with_text(text: &str) -> Vec<u8> {
    minimal_pdf_with_pages(&[text])
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
    let mut objects = Vec::new();
    let page_object_numbers = (0..page_streams.len())
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
        page_streams.len()
    ));
    objects.push("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".to_string());

    for (index, stream) in page_streams.iter().enumerate() {
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

fn ascii_hex(text: &str) -> String {
    text.as_bytes()
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect()
}
