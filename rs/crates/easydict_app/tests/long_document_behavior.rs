use easydict_app::compat_protocol::{
    BlockTranslatedEventData, ProgressEventData, SettingsSnapshot, StatusEventData,
    TranslateDocumentParams, TranslateDocumentResult,
};
use easydict_app::{
    apply_long_document_outcome, begin_long_document_translate, build_long_document_request,
    run_long_document_request, AppMode, EasydictApp, EasydictUiState, LongDocumentBackend,
    LongDocumentBackendError, LongDocumentEvent, LongDocumentInput, LongDocumentOutcome, Message,
};
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
    assert_eq!(request.params.to, "ChineseSimplified");
    assert_eq!(
        request.params.layout_detection.as_deref(),
        Some("VisionLLM")
    );
    assert_eq!(request.params.page_range.as_deref(), Some("1-3,5"));
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

    app.state.long_document.is_translating = true;
    let locked_task = app.update(Message::BrowseFile);
    assert_eq!(task_kind(&locked_task), "none");
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
    assert_eq!(backend.calls[0].page_range.as_deref(), Some("2"));
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
        Task::OpenFileDialog { .. } => "file_dialog",
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
