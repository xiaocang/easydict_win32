use easydict_app::long_document::run_long_document_request_with_current_app_dir;
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::long_document::run_long_document_request_with_packaged_app_dir_and_worker_policy;
use easydict_app::protocol::{
    local_ai_provider_modes, BlockTranslatedEventData, ProgressEventData, SettingsSnapshot,
    StatusEventData, TranslateDocumentParams, TranslateDocumentResult,
};
#[cfg(feature = "retained-dotnet-workers")]
use easydict_app::RetainedWorkerPolicy;
use easydict_app::{
    apply_long_document_outcome, begin_long_document_retry_failed, begin_long_document_translate,
    build_long_document_request, long_document_request_can_route_natively,
    long_document_source_hash,
    retry_failed_native_text_long_document_from_result_json_with_translator,
    run_long_document_request, run_long_document_request_with_app_dir,
    run_long_document_request_with_app_dir_and_native_local_ai_client,
    run_long_document_request_with_native_route,
    run_native_text_long_document_request_with_translator,
    run_native_text_long_document_request_with_translator_and_cancellation, AppMode, EasydictApp,
    EasydictUiState, FoundryLocalEndpointResolver, FoundryLocalError,
    FoundryLocalRuntimeController, FoundryLocalRuntimeState, FoundryLocalRuntimeStatus,
    LongDocumentBackend, LongDocumentBackendError, LongDocumentEvent, LongDocumentInput,
    LongDocumentOutcome, LongDocumentTranslationCache, Message, NativeLongDocumentTranslator,
    NativeOpenAiQuickTranslateBackend, NativeOpenVinoQuickTranslateBackend, OpenAiExecutionError,
    OpenAiExecutionErrorCode, OpenAiHttpClient, OpenAiHttpRequestPlan, QuickTranslateBackend,
    QuickTranslateExecutionKind, QuickTranslateServiceRequest, TRANSLATION_LANGUAGE_IDS,
};
use easydict_nllb::{NllbError, NllbInferenceEngine, NllbTokenizer, NllbTranslator};
use easydict_windows_ai::{
    WindowsAiError, WindowsAiGenerationOptions, WindowsAiLanguageModelClient,
    WindowsAiLanguageModelProbe, WindowsAiReadyState, WindowsAiResponse,
};
use std::collections::VecDeque;
use std::fs;
use std::path::PathBuf;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc, Mutex,
};
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};
use win_fluent::prelude::{Application, ResultStatus, Task};

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

const RUNTIME_PROFILE_ENVIRONMENT_VARIABLE: &str = "EASYDICT_RUNTIME_PROFILE";
#[cfg(feature = "retained-dotnet-workers")]
const GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE: &str = "RUNTIME_PROFILE";

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
    assert_eq!(
        request.params.result_json_path.as_deref(),
        Some(r"C:\Docs\paper_translated.result.json")
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
    assert_eq!(request.params.request_timeout_ms, Some(30_000));
    assert_eq!(request.settings.request_timeout_ms, Some(30_000));
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
fn long_document_retry_failed_starts_from_default_result_json_sidecar() {
    let mut state = EasydictUiState {
        mode: AppMode::LongDocument,
        long_document: easydict_app::LongDocumentState {
            selected_file: r"C:\Docs\notes.txt".to_string(),
            input_mode: "plaintext".to_string(),
            service: "google".to_string(),
            source_language: "en".to_string(),
            target_language: "zh-Hans".to_string(),
            ..Default::default()
        },
        ..Default::default()
    };

    let (request, result_json_path) =
        begin_long_document_retry_failed(&mut state).expect("retry request should start");

    assert_eq!(request.query_id, 1);
    assert_eq!(
        request.params.output_path.as_deref(),
        Some(r"C:\Docs\notes_translated.txt")
    );
    assert_eq!(
        request.params.result_json_path.as_deref(),
        Some(r"C:\Docs\notes_translated.result.json")
    );
    assert_eq!(result_json_path, r"C:\Docs\notes_translated.result.json");
    assert_eq!(state.next_query_id, 2);
    assert_eq!(state.long_document.active_query_id, Some(1));
    assert!(state.long_document.is_translating);
    assert_eq!(state.long_document.status_text, "Translating document");
}

#[test]
fn long_document_retry_failed_requires_stable_result_json_sidecar() {
    let mut state = EasydictUiState {
        mode: AppMode::LongDocument,
        long_document: easydict_app::LongDocumentState {
            selected_file: "No file selected".to_string(),
            source_text: "Inline text has no stable output folder yet.".to_string(),
            input_mode: "plaintext".to_string(),
            service: "google".to_string(),
            ..Default::default()
        },
        ..Default::default()
    };

    let error = begin_long_document_retry_failed(&mut state)
        .expect_err("inline retry without sidecar should fail locally");

    assert_eq!(
        error.to_string(),
        "Retry Failed requires a Rust-native result JSON checkpoint for this document."
    );
    assert_eq!(state.next_query_id, 1);
    assert_eq!(state.long_document.active_query_id, None);
    assert!(!state.long_document.is_translating);
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
    assert_eq!(request.params.request_timeout_ms, Some(120_000));
    assert_eq!(request.settings.request_timeout_ms, Some(120_000));
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
    assert_eq!(request.params.request_timeout_ms, Some(30_000));
    assert_eq!(request.settings.request_timeout_ms, Some(30_000));
    assert!(!long_document_request_can_route_natively(&request));
}

#[test]
fn long_document_request_maps_all_selectable_targets_to_dotnet_language_names() {
    let expected = [
        ("zh-Hans", "SimplifiedChinese"),
        ("zh-Hant", "TraditionalChinese"),
        ("ja", "Japanese"),
        ("ko", "Korean"),
        ("zh-classical", "ClassicalChinese"),
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
    assert_eq!(request.params.result_json_path, None);
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
fn app_update_retry_long_document_requires_sidecar_and_starts_retry_task() {
    let mut app = EasydictApp {
        state: EasydictUiState::default(),
    };

    let quick_task = app.update(Message::RetryLongDocument);
    assert_eq!(task_kind(&quick_task), "none");
    assert!(!app.state.long_document.is_translating);

    app.state.mode = AppMode::LongDocument;
    app.state.long_document.selected_file = "No file selected".to_string();
    app.state.long_document.source_text = "Inline text cannot derive a stable sidecar.".to_string();
    app.state.long_document.input_mode = "plaintext".to_string();

    let missing_sidecar_task = app.update(Message::RetryLongDocument);
    assert_eq!(task_kind(&missing_sidecar_task), "none");
    assert_eq!(
        app.state.long_document.last_error.as_deref(),
        Some("Retry Failed requires a Rust-native result JSON checkpoint for this document.")
    );
    assert!(!app.state.long_document.is_translating);

    app.state.long_document.selected_file = r"C:\Docs\paper.txt".to_string();
    app.state.long_document.source_text.clear();
    app.state.long_document.input_mode = "plaintext".to_string();
    app.state.long_document.service = "google".to_string();

    let retry_task = app.update(Message::RetryLongDocument);

    assert_eq!(task_kind(&retry_task), "future");
    assert!(app.state.long_document.is_translating);
    assert_eq!(app.state.long_document.active_query_id, Some(1));
    assert_eq!(app.state.long_document.last_error, None);
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

    assert_eq!(task_kind(&browse_task), "future");

    let output_browse_task = app.update(Message::BrowseOutputFolder);
    assert_eq!(task_kind(&output_browse_task), "future");

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
fn long_document_file_dialog_error_surfaces_last_error_and_success_clears_dialog_error() {
    let mut state = EasydictUiState::default();

    state.apply(Message::LongDocumentFileDialogFinished(Err(
        "Windows dialogs are only available on Windows".to_string(),
    )));
    assert_eq!(state.long_document.status_text, "File dialog failed");
    assert_eq!(
        state.long_document.last_error.as_deref(),
        Some("File dialog failed: Windows dialogs are only available on Windows")
    );

    state.apply(Message::LongDocumentFileDialogFinished(Ok(None)));
    assert_eq!(state.long_document.last_error, None);

    state.long_document.last_error = Some("provider failed".to_string());
    state.apply(Message::LongDocumentOutputFolderDialogFinished(Ok(None)));
    assert_eq!(
        state.long_document.last_error.as_deref(),
        Some("provider failed"),
        "dialog cancellation should not clear unrelated LongDoc errors"
    );

    state.apply(Message::LongDocumentOutputFolderDialogFinished(Err(
        "IFileDialog::Show failed with native error -1".to_string(),
    )));
    assert_eq!(
        state.long_document.last_error.as_deref(),
        Some("File dialog failed: IFileDialog::Show failed with native error -1")
    );
    state.apply(Message::LongDocumentOutputFolderDialogFinished(Ok(Some(
        r"D:\Translated".to_string(),
    ))));
    assert_eq!(state.long_document.output_folder, r"D:\Translated");
    assert_eq!(state.long_document.last_error, None);
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
            && call.settings.request_timeout_ms == Some(30_000)
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
fn native_text_long_document_params_timeout_flows_to_chunk_requests() {
    let temp_dir = unique_temp_dir("longdoc-native-text-params-timeout");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A timeout-sensitive document.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        2201,
    )
    .expect("native text timeout request");
    request.params.request_timeout_ms = Some(120_000);
    request.settings.request_timeout_ms = None;

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    outcome.result.expect("native long document result");

    let calls = translator.calls();
    assert!(!calls.is_empty());
    assert!(
        calls
            .iter()
            .all(|call| call.settings.request_timeout_ms == Some(120_000)),
        "params.requestTimeoutMs should become the native chunk request timeout: {calls:#?}"
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_context_pass_injects_prompt() {
    let temp_dir = unique_temp_dir("longdoc-native-text-context-pass");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let source_text =
        native_long_text_markers(&["Transformer-intro", "Transformer-middle", "Transformer-end"]);
    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                custom_translation_prompt: "Preserve glossary terms.".to_string(),
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
                two_pass_context: true,
                ..Default::default()
            },
            ..Default::default()
        },
        23,
    )
    .expect("native context request should build");
    let mut translator = ContextAwareNativeLongDocTranslator::default();

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome.result.expect("native context run should succeed");

    assert_eq!(result.state, "Completed");
    assert_eq!(translator.context_calls().len(), 1);
    let translation_calls = translator.translation_calls();
    assert_eq!(translation_calls.len(), 3);
    for call in translation_calls {
        let prompt = call.params.custom_prompt.unwrap_or_default();
        assert!(prompt.contains("Document summary: Transformer paper page."));
        assert!(prompt.contains("Use these term translations consistently across the document:"));
        assert!(prompt.contains("Transformer -> Transformer"));
        assert!(prompt.contains("Preserve glossary terms."));
    }

    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    assert!(output.contains("[zh] Transformer-intro"));
    assert!(output.contains("[zh] Transformer-middle"));
    assert!(output.contains("[zh] Transformer-end"));
    assert!(outcome.events.iter().any(|event| matches!(
        event,
        LongDocumentEvent::Status(status)
            if status.message == "Analyzing document context natively"
    )));
    assert!(outcome.events.iter().any(|event| matches!(
        event,
        LongDocumentEvent::Progress(progress) if progress.stage == "DocumentContext"
    )));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_context_pass_preserves_hinted_short_chunk() {
    let temp_dir = unique_temp_dir("longdoc-native-text-context-preserve");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "BLEU-28.4".to_string(),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                two_pass_context: true,
                ..Default::default()
            },
            ..Default::default()
        },
        24,
    )
    .expect("native context preservation request should build");
    let mut translator = ContextAwareNativeLongDocTranslator::default();

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("native context preservation run should succeed");

    assert_eq!(result.state, "Completed");
    assert_eq!(translator.context_calls().len(), 1);
    assert_eq!(translator.translation_calls().len(), 0);
    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("output should be written");
    assert_eq!(output.trim(), "BLEU-28.4");
    assert!(outcome.events.iter().any(|event| matches!(
        event,
        LongDocumentEvent::BlockTranslated(block)
            if block.translated_text == "BLEU-28.4" && block.last_error.is_none()
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
fn native_text_long_document_runner_stops_before_export_when_cancelled_after_first_chunk() {
    let temp_dir = unique_temp_dir("longdoc-native-text-cancel-after-first");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("cancelled-output.txt");
    let cancelled = Arc::new(AtomicBool::new(false));

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["chunk-0", "chunk-1", "chunk-2"]),
                input_mode: "plaintext".to_string(),
                output_mode: "monolingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                concurrency: "1".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        78,
    )
    .expect("plain text cancellation request should build");
    request.params.output_path = Some(output_path.display().to_string());

    let mut translator = CancellingNativeLongDocTranslator::after_calls(1, cancelled.clone());
    let outcome = run_native_text_long_document_request_with_translator_and_cancellation(
        &mut translator,
        request,
        || cancelled.load(Ordering::SeqCst),
    );
    let error = outcome
        .result
        .expect_err("cancelled native run should report an error");

    assert!(error.message.contains("cancelled"));
    assert_eq!(
        translator.call_count(),
        1,
        "native runner should not schedule later chunks after cancellation"
    );
    assert!(
        !outcome.events.iter().any(|event| matches!(
            event,
            LongDocumentEvent::Progress(progress) if progress.stage == "Exporting"
        )),
        "cancelled run should not enter export stage"
    );
    assert!(
        !output_path.exists(),
        "cancelled native run should not write partial output"
    );

    fs::remove_dir_all(&temp_dir).ok();
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
    let cached_0_source = split_chunks
        .iter()
        .find(|chunk| chunk.contains("cached-0"))
        .expect("cached-0 source chunk should be recorded")
        .clone();
    let cached_2_source = split_chunks
        .iter()
        .find(|chunk| chunk.contains("cached-2"))
        .expect("cached-2 source chunk should be recorded")
        .clone();
    let miss_1_source = split_chunks
        .iter()
        .find(|chunk| chunk.contains("miss-1"))
        .expect("miss-1 source chunk should be recorded")
        .clone();
    let miss_3_source = split_chunks
        .iter()
        .find(|chunk| chunk.contains("miss-3"))
        .expect("miss-3 source chunk should be recorded")
        .clone();

    let mut cache = LongDocumentTranslationCache::open(temp_dir.join("translation_cache.db"))
        .expect("cache should open");
    for (source, translated) in [
        (&cached_0_source, "[cached] cached-0"),
        (&cached_2_source, "[cached] cached-2"),
    ] {
        cache
            .set(
                "google",
                "English",
                "SimplifiedChinese",
                &long_document_source_hash(source),
                source,
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
    assert!(called_text.contains(&miss_1_source));
    assert!(called_text.contains(&miss_3_source));
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

    let mut request = build_long_document_request(
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
    let result_json_path = temp_dir.join("paper-result.json");
    request.params.result_json_path = Some(result_json_path.display().to_string());
    assert_eq!(request.params.input_mode, "Pdf");
    assert!(request.params.page_range.is_none());
    assert!(long_document_request_can_route_natively(&request));

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome.result.expect("native pdf long document result");
    let expected_result = result.clone();

    assert_eq!(result.state, "Completed");
    let quality_report_json = result
        .quality_report
        .as_deref()
        .expect("native PDF result should include a quality report");
    let quality_report: serde_json::Value =
        serde_json::from_str(quality_report_json).expect("PDF quality report should parse");
    assert_eq!(quality_report["totalBlocks"], 1);
    assert_eq!(quality_report["translatedBlocks"], 1);
    assert_eq!(
        quality_report["backfillMetrics"]["candidateBlocks"],
        serde_json::json!(1)
    );
    assert_eq!(
        quality_report["backfillMetrics"]["renderedBlocks"],
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
    let sidecar_json =
        fs::read_to_string(&result_json_path).expect("PDF result JSON sidecar should be written");
    let sidecar: TranslateDocumentResult = serde_json::from_str(&sidecar_json)
        .expect("PDF result JSON sidecar should remain result-compatible");
    assert_eq!(sidecar, expected_result);
    let sidecar_value: serde_json::Value =
        serde_json::from_str(&sidecar_json).expect("PDF result JSON sidecar should parse");
    assert_eq!(
        sidecar_value["qualityReport"].as_str(),
        Some(quality_report_json)
    );
    assert_eq!(sidecar_value["checkpoint"]["inputMode"], "Pdf");
    assert_eq!(
        sidecar_value["checkpoint"]["pdf"]["failedChunkIndexes"],
        serde_json::json!([])
    );
    assert!(sidecar_value["checkpoint"]["pdf"]["sourceChunks"][0]
        .as_str()
        .unwrap_or_default()
        .contains("Hello PDF"));
    assert!(sidecar_value["checkpoint"]["pdf"]["translatedChunks"]["0"]
        .as_str()
        .unwrap_or_default()
        .contains("[zh] Hello PDF"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_result_json_retry_failed_updates_text_and_pdf_checkpoints() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-retry-failed");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("paper.pdf");
    let output_path = temp_dir.join("paper-result.pdf");
    let result_json_path = temp_dir.join("paper-result.json");
    fs::write(
        &input_path,
        minimal_pdf_with_pages(&["ok PDF", "fail PDF", "ok PDF 2"]),
    )
    .expect("pdf should be written");

    let mut request = build_long_document_request(
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
        84,
    )
    .expect("native PDF retry-failed request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut first_translator = RecordingNativeLongDocTranslator::failing_on(["fail PDF"]);
    let first = run_native_text_long_document_request_with_translator(
        &mut first_translator,
        request.clone(),
    )
    .result
    .expect("partial native PDF run should write checkpoint sidecar");
    assert_eq!(first.state, "PartiallyCompleted");
    assert_eq!(first.failed_chunk_indexes, Some(vec![1]));
    let first_sidecar_json =
        fs::read_to_string(&result_json_path).expect("partial PDF sidecar should exist");
    let first_sidecar: serde_json::Value =
        serde_json::from_str(&first_sidecar_json).expect("partial PDF sidecar should parse");
    assert_eq!(
        first_sidecar["checkpoint"]["text"]["failedChunkIndexes"],
        serde_json::json!([1])
    );
    assert_eq!(
        first_sidecar["checkpoint"]["pdf"]["failedChunkIndexes"],
        serde_json::json!([1])
    );

    let mut retry_translator = RecordingNativeLongDocTranslator::default();
    let retry = retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut retry_translator,
        request,
        &result_json_path,
    )
    .result
    .expect("PDF retry failed should complete from checkpoint");

    assert_eq!(retry.state, "Completed");
    assert_eq!(retry.failed_chunk_indexes, None);
    let retry_calls = retry_translator.calls();
    assert_eq!(retry_calls.len(), 1);
    assert!(retry_calls[0].params.text.contains("fail PDF"));
    assert!(!retry_calls[0].params.text.contains("ok PDF 2"));

    let output_pages =
        easydict_app::long_document::extract_native_pdf_text_from_content_stream_pages(
            retry.output_path.as_deref().expect("retry PDF output path"),
        )
        .expect("retry PDF output text should extract");
    let output_text = output_pages.join("\n");
    assert!(output_text.contains("[zh] ok PDF"));
    assert!(output_text.contains("[zh] fail PDF"));
    assert!(output_text.contains("[zh] ok PDF 2"));

    let retry_sidecar_json =
        fs::read_to_string(&result_json_path).expect("retry PDF sidecar should exist");
    let retry_sidecar: serde_json::Value =
        serde_json::from_str(&retry_sidecar_json).expect("retry PDF sidecar should parse");
    assert_eq!(
        retry_sidecar["checkpoint"]["text"]["failedChunkIndexes"],
        serde_json::json!([])
    );
    assert_eq!(
        retry_sidecar["checkpoint"]["pdf"]["failedChunkIndexes"],
        serde_json::json!([])
    );
    assert!(retry_sidecar["checkpoint"]["pdf"]["translatedChunks"]["1"]
        .as_str()
        .unwrap_or_default()
        .contains("[zh] fail PDF"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_result_json_retry_failed_restores_output_page_range_and_export_mode() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-retry-route-metadata");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("paper.pdf");
    let output_path = temp_dir.join("paper-result.pdf");
    let wrong_output_path = temp_dir.join("wrong-result.pdf");
    let result_json_path = temp_dir.join("paper-result.json");
    fs::write(
        &input_path,
        minimal_pdf_with_pages(&["ignore page one", "fail PDF", "ok PDF 3"]),
    )
    .expect("pdf should be written");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "pdf".to_string(),
                output_mode: "both".to_string(),
                page_range: "2-3".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        8401,
    )
    .expect("native PDF retry route metadata request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.pdf_export_mode = Some("ContentStreamReplacement".to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut first_translator = RecordingNativeLongDocTranslator::failing_on(["fail PDF"]);
    let first = run_native_text_long_document_request_with_translator(
        &mut first_translator,
        request.clone(),
    )
    .result
    .expect("partial native PDF run should write route metadata sidecar");
    assert_eq!(first.state, "PartiallyCompleted");
    assert_eq!(first.failed_chunk_indexes, Some(vec![0]));

    let mut retry_request = request.clone();
    retry_request.params.output_path = Some(wrong_output_path.display().to_string());
    retry_request.params.pdf_export_mode = Some("Overlay".to_string());
    retry_request.params.page_range = Some("1".to_string());
    let mut retry_translator = RecordingNativeLongDocTranslator::default();
    let retry = retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut retry_translator,
        retry_request,
        &result_json_path,
    )
    .result
    .expect("PDF retry should restore sidecar route metadata");

    assert_eq!(retry.state, "Completed");
    assert_eq!(
        retry.output_path.as_deref(),
        Some(output_path.to_str().unwrap())
    );
    assert!(
        !wrong_output_path.exists(),
        "retry must not export to the current request's stale output path"
    );

    let retry_sidecar_json =
        fs::read_to_string(&result_json_path).expect("retry PDF sidecar should exist");
    let retry_sidecar: serde_json::Value =
        serde_json::from_str(&retry_sidecar_json).expect("retry PDF sidecar should parse");
    assert_eq!(
        retry_sidecar["checkpoint"]["outputPath"],
        output_path.display().to_string()
    );
    assert_eq!(retry_sidecar["checkpoint"]["pageRange"], "2-3");
    assert_eq!(
        retry_sidecar["checkpoint"]["pdfExportMode"],
        "ContentStreamReplacement"
    );
    assert_eq!(
        retry_sidecar["checkpoint"]["text"]["failedChunkIndexes"],
        serde_json::json!([])
    );

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

    let outcome = run_long_document_request_with_app_dir(request, &temp_dir);
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
fn app_dir_longdoc_runner_defaults_to_rust_only_even_when_worker_policy_can_enable_hybrid() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let temp_dir = unique_temp_dir("longdoc-packaged-runner-default-rust-only");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A long document that still needs the retained worker.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "google".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        54,
    )
    .expect("longdoc request should be built");
    request.params.input_mode = "Docx".to_string();

    assert!(!long_document_request_can_route_natively(&request));

    let default_outcome = run_long_document_request_with_app_dir(request.clone(), &temp_dir);
    let default_error = default_outcome
        .result
        .expect_err("default rs runner should keep retained LongDoc disabled");

    assert!(default_error
        .message
        .contains("requires a Rust-native route"));
    assert!(!default_error.message.contains(".NET Long Document workers"));
    assert!(!default_error
        .message
        .contains("Long Document worker executable"));

    #[cfg(feature = "retained-dotnet-workers")]
    {
        let _runtime_profile =
            EnvironmentVariableGuard::set(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");
        let hybrid_outcome = run_long_document_request_with_packaged_app_dir_and_worker_policy(
            request,
            &temp_dir,
            RetainedWorkerPolicy::all_enabled(),
        );
        let hybrid_error = hybrid_outcome
            .result
            .expect_err("explicit hybrid policy should not route this request natively");

        assert!(hybrid_error.message.contains("Long Document worker"));
        assert!(hybrid_error.message.contains("I/O error"));
    }

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn current_app_dir_runner_ignores_hybrid_runtime_profile_before_worker_probe() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _easydict_runtime_profile =
        EnvironmentVariableGuard::set(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");
    let _generic_runtime_profile = EnvironmentVariableGuard::set("RUNTIME_PROFILE", "hybrid");
    let temp_dir = unique_temp_dir("longdoc-current-app-dir-hybrid-env-rust-only");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A current-app-dir request that still needs the retained worker."
                    .to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "google".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        55,
    )
    .expect("longdoc request should be built");
    request.params.input_mode = "Docx".to_string();

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_current_app_dir(request);
    let error = outcome
        .result
        .expect_err("default current-app-dir runner should keep retained LongDoc disabled");

    assert!(error.message.contains("requires a Rust-native route"));
    for forbidden in [
        ".NET Long Document workers",
        "Long Document worker executable",
        "CompatHost",
        "DOTNET_ROOT",
    ] {
        assert!(
            !error.message.contains(forbidden),
            "current-app-dir runner should fail before retained worker probing: {}",
            error.message
        );
    }

    fs::remove_dir_all(&temp_dir).ok();
}

#[cfg(feature = "retained-dotnet-workers")]
#[test]
fn explicit_longdoc_worker_policy_without_hybrid_runtime_profile_stays_rust_only() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile = EnvironmentVariableGuard::remove(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
    let _generic_runtime_profile =
        EnvironmentVariableGuard::remove(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
    let temp_dir = unique_temp_dir("longdoc-explicit-worker-policy-stays-rust-only");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A long document that still needs the retained worker.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "google".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        57,
    )
    .expect("longdoc request should be built");
    request.params.input_mode = "Docx".to_string();

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_packaged_app_dir_and_worker_policy(
        request,
        &temp_dir,
        RetainedWorkerPolicy::all_enabled(),
    );
    let error = outcome
        .result
        .expect_err("injected worker policy must still require explicit hybrid runtime");

    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains(".NET Long Document workers"));
    assert!(!error.message.contains("Long Document worker executable"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_route_helper_ignores_hybrid_environment_and_keeps_retained_worker_disabled() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile =
        EnvironmentVariableGuard::set(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");

    let temp_dir = unique_temp_dir("longdoc-native-route-hybrid-env-rust-only");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let mut request = build_long_document_request(
        &EasydictUiState {
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A long document that should not enter retained workers.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "google".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        55,
    )
    .expect("longdoc request should be built");
    request.params.input_mode = "Docx".to_string();

    assert!(!long_document_request_can_route_natively(&request));

    let mut backend = RecordingLongDocBackend::ok(result("Completed", None, None));
    let outcome = run_long_document_request_with_native_route(&mut backend, request);
    let error = outcome
        .result
        .expect_err("native route helper should keep retained LongDoc disabled");

    assert_eq!(backend.calls.len(), 0);
    assert!(error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains(".NET Long Document workers"));
    assert!(!error.message.contains("Long Document worker executable"));

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
fn native_text_long_document_with_app_dir_runner_does_not_spawn_retained_worker() {
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome.result.unwrap_err();

    assert!(error.message.contains("API key"));
    assert!(
        !error.message.contains("Long Document worker"),
        "native text longdoc should fail locally before retained worker fallback: {}",
        error.message
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_file_long_document_with_app_dir_runner_does_not_spawn_worker_when_mode_is_default() {
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome.result.unwrap_err();

    assert!(error.message.contains("API key"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn stale_text_page_range_long_document_with_app_dir_runner_does_not_spawn_worker() {
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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
fn native_pdf_all_pages_with_app_dir_runner_does_not_spawn_worker() {
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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
fn native_pdf_hex_content_stream_with_app_dir_runner_does_not_spawn_worker() {
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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
fn missing_worker_file_long_document_with_app_dir_runner_does_not_spawn_worker() {
    let temp_dir = unique_temp_dir("longdoc-worker-missing-file-no-host");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("missing.txt");

    let mut request = build_long_document_request(
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
    request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("missing input should fail locally before worker startup");

    assert!(
        error.message.contains("Could not read long document input")
            || error.message.contains("Could not read text document"),
        "missing input should fail locally before worker startup: {}",
        error.message
    );
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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
fn native_text_both_output_prechecks_bilingual_path_before_writing_monolingual_file() {
    let temp_dir = unique_temp_dir("longdoc-native-text-both-output-precheck");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("atomic-output.txt");
    let bilingual_path = temp_dir.join("atomic-output-bilingual.txt");
    fs::create_dir_all(&bilingual_path).expect("conflicting bilingual directory should exist");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_mode: "both".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        76,
    )
    .expect("native text output precheck request");
    request.params.output_path = Some(output_path.display().to_string());

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let error = outcome
        .result
        .expect_err("conflicting bilingual path should fail before writing output");

    assert!(error.message.contains("Long document output path"));
    assert!(error.message.contains("is a directory"));
    assert_eq!(
        translator.call_count(),
        0,
        "invalid native output targets should be rejected before provider translation starts"
    );
    assert!(
        !output_path.exists(),
        "monolingual output should not be partially written when bilingual target is invalid"
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_markdown_both_output_prechecks_bilingual_path_before_provider_translation() {
    let temp_dir = unique_temp_dir("longdoc-native-markdown-both-output-precheck");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("notes.md");
    let output_path = temp_dir.join("notes-translated.md");
    let bilingual_path = temp_dir.join("notes-translated-bilingual.md");
    fs::write(&input_path, "# Title\n\nA short markdown document.")
        .expect("markdown input should be written");
    fs::create_dir_all(&bilingual_path).expect("conflicting bilingual directory should exist");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
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
        176,
    )
    .expect("native markdown output precheck request");
    request.params.output_path = Some(output_path.display().to_string());

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let error = outcome
        .result
        .expect_err("conflicting markdown bilingual path should fail before provider translation");

    assert!(error.message.contains("Long document output path"));
    assert!(error.message.contains("is a directory"));
    assert_eq!(
        translator.call_count(),
        0,
        "invalid markdown output targets should be rejected before provider translation starts"
    );
    assert!(
        !output_path.exists(),
        "monolingual markdown output should not be partially written when bilingual target is invalid"
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_long_document_writes_result_json_sidecar() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-sidecar");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("translated.txt");
    let result_json_path = temp_dir.join("translated-result.json");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        78,
    )
    .expect("native text result sidecar request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("native text translation should succeed");

    assert_eq!(
        result.result_json_path.as_deref(),
        Some(result_json_path.to_str().unwrap())
    );
    assert!(output_path.exists());
    let sidecar_json =
        fs::read_to_string(&result_json_path).expect("result JSON sidecar should be written");
    let sidecar: TranslateDocumentResult =
        serde_json::from_str(&sidecar_json).expect("result JSON sidecar should deserialize");
    assert_eq!(sidecar, result);
    let quality_report_json = result
        .quality_report
        .as_deref()
        .expect("native result should include a quality report");
    let quality_report: serde_json::Value =
        serde_json::from_str(quality_report_json).expect("quality report should be JSON");
    assert_eq!(quality_report["totalBlocks"], 1);
    assert_eq!(quality_report["translatedBlocks"], 1);
    assert_eq!(quality_report["skippedBlocks"], 0);
    assert_eq!(quality_report["failedBlocks"], serde_json::json!([]));
    assert_eq!(quality_report["stageTimingsMs"], serde_json::json!({}));
    assert_eq!(quality_report["backfillMetrics"], serde_json::Value::Null);
    let sidecar_value: serde_json::Value =
        serde_json::from_str(&sidecar_json).expect("result JSON sidecar value should deserialize");
    assert_eq!(
        sidecar_value["qualityReport"].as_str(),
        Some(quality_report_json)
    );
    assert_eq!(sidecar_value["checkpoint"]["inputMode"], "PlainText");
    assert_eq!(sidecar_value["checkpoint"]["outputMode"], "Monolingual");
    assert_eq!(sidecar_value["checkpoint"]["serviceId"], "google");
    assert_eq!(sidecar_value["checkpoint"]["routeMetadataVersion"], 1);
    assert_eq!(
        sidecar_value["checkpoint"]["outputPath"],
        output_path.display().to_string()
    );
    assert_eq!(
        sidecar_value["checkpoint"]["text"]["sourceChunks"],
        serde_json::json!(["A short document"])
    );
    assert_eq!(
        sidecar_value["checkpoint"]["text"]["failedChunkIndexes"],
        serde_json::json!([])
    );
    assert_eq!(
        sidecar_value["checkpoint"]["text"]["translatedChunks"]["0"],
        "[zh] A short document"
    );
    assert!(
        sidecar_value["checkpoint"].get("pdf").is_none(),
        "plain text sidecar should not include a PDF checkpoint"
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_result_json_sidecar_persists_partial_checkpoint() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-partial-checkpoint");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("translated.txt");
    let result_json_path = temp_dir.join("translated-result.json");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "fail-1", "ok-2"]),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        80,
    )
    .expect("native text partial result sidecar request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut translator = RecordingNativeLongDocTranslator::failing_on(["fail-1"]);
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("partial native text translation should export successful chunks");

    assert_eq!(result.state, "PartiallyCompleted");
    assert_eq!(result.failed_chunk_indexes, Some(vec![1]));
    let sidecar_json =
        fs::read_to_string(&result_json_path).expect("partial result JSON sidecar should exist");
    let sidecar: TranslateDocumentResult = serde_json::from_str(&sidecar_json)
        .expect("partial result JSON sidecar should remain result-compatible");
    assert_eq!(sidecar, result);
    let quality_report: serde_json::Value = serde_json::from_str(
        result
            .quality_report
            .as_deref()
            .expect("partial native result should include a quality report"),
    )
    .expect("partial quality report should parse");
    assert_eq!(quality_report["totalBlocks"], 3);
    assert_eq!(quality_report["translatedBlocks"], 2);
    assert_eq!(quality_report["skippedBlocks"], 0);
    assert_eq!(
        quality_report["failedBlocks"][0]["irBlockId"],
        "checkpoint-1"
    );
    assert_eq!(
        quality_report["failedBlocks"][0]["sourceBlockId"],
        "native-p1-b2"
    );
    assert_eq!(quality_report["failedBlocks"][0]["pageNumber"], 1);
    assert_eq!(quality_report["failedBlocks"][0]["retryCount"], 0);
    assert_eq!(
        quality_report["failedBlocks"][0]["error"],
        "Translation failed or missing translated text."
    );
    let sidecar_value: serde_json::Value = serde_json::from_str(&sidecar_json)
        .expect("partial result JSON sidecar value should parse");
    let text_checkpoint = &sidecar_value["checkpoint"]["text"];
    assert_eq!(
        text_checkpoint["failedChunkIndexes"],
        serde_json::json!([1])
    );
    assert!(text_checkpoint["sourceChunks"][1]
        .as_str()
        .unwrap_or_default()
        .starts_with("fail-1"));
    assert!(text_checkpoint["translatedChunks"]["0"]
        .as_str()
        .unwrap_or_default()
        .starts_with("[zh] ok-0"));
    assert!(
        text_checkpoint["translatedChunks"].get("1").is_none(),
        "failed chunks should stay absent from translatedChunks for Retry Failed"
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_result_json_retry_failed_retranslates_only_failed_chunks() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-retry-failed");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("translated.txt");
    let result_json_path = temp_dir.join("translated-result.json");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "fail-1", "ok-2"]),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        81,
    )
    .expect("native text retry-failed request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut first_translator = RecordingNativeLongDocTranslator::failing_on(["fail-1"]);
    let first = run_native_text_long_document_request_with_translator(
        &mut first_translator,
        request.clone(),
    )
    .result
    .expect("partial native text run should write checkpoint sidecar");
    assert_eq!(first.state, "PartiallyCompleted");
    assert_eq!(first.failed_chunk_indexes, Some(vec![1]));

    let mut retry_translator = RecordingNativeLongDocTranslator::default();
    let retry = retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut retry_translator,
        request,
        &result_json_path,
    )
    .result
    .expect("retry failed should complete from checkpoint");

    assert_eq!(retry.state, "Completed");
    assert_eq!(retry.total_chunks, 3);
    assert_eq!(retry.succeeded_chunks, 3);
    assert_eq!(retry.failed_chunk_indexes, None);
    let calls = retry_translator.calls();
    assert_eq!(calls.len(), 1);
    assert!(
        calls[0].params.text.starts_with("fail-1"),
        "retry should only send the failed chunk back to the provider"
    );

    let output = fs::read_to_string(&output_path).expect("retry output should be written");
    assert!(output.contains("[zh] ok-0"));
    assert!(output.contains("[zh] fail-1"));
    assert!(output.contains("[zh] ok-2"));
    let sidecar_json =
        fs::read_to_string(&result_json_path).expect("retry result sidecar should be written");
    let sidecar_value: serde_json::Value =
        serde_json::from_str(&sidecar_json).expect("retry sidecar should parse");
    assert_eq!(sidecar_value["state"], "Completed");
    let retry_quality_report: serde_json::Value = serde_json::from_str(
        retry
            .quality_report
            .as_deref()
            .expect("retry result should include a refreshed quality report"),
    )
    .expect("retry quality report should parse");
    assert_eq!(retry_quality_report["totalBlocks"], 3);
    assert_eq!(retry_quality_report["translatedBlocks"], 3);
    assert_eq!(retry_quality_report["failedBlocks"], serde_json::json!([]));
    assert_eq!(
        sidecar_value["qualityReport"].as_str(),
        retry.quality_report.as_deref()
    );
    assert_eq!(
        sidecar_value["checkpoint"]["text"]["failedChunkIndexes"],
        serde_json::json!([])
    );
    assert!(sidecar_value["checkpoint"]["text"]["translatedChunks"]["1"]
        .as_str()
        .unwrap_or_default()
        .starts_with("[zh] fail-1"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_result_json_retry_failed_rejects_incomplete_checkpoint_without_export() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-retry-incomplete");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("translated.txt");
    let result_json_path = temp_dir.join("translated-result.json");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "ok-1"]),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        80,
    )
    .expect("native text incomplete retry checkpoint request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut first_translator = RecordingNativeLongDocTranslator::default();
    run_native_text_long_document_request_with_translator(&mut first_translator, request.clone())
        .result
        .expect("completed native text run should write checkpoint sidecar");

    let mut sidecar_value: serde_json::Value = serde_json::from_str(
        &fs::read_to_string(&result_json_path).expect("result sidecar should exist"),
    )
    .expect("result sidecar should parse");
    sidecar_value["checkpoint"]["text"]["translatedChunks"]
        .as_object_mut()
        .expect("translatedChunks should be an object")
        .remove("1");
    sidecar_value["checkpoint"]["text"]["failedChunkIndexes"] = serde_json::json!([]);
    let corrupted_sidecar =
        serde_json::to_string_pretty(&sidecar_value).expect("corrupted sidecar should serialize");
    fs::write(&result_json_path, &corrupted_sidecar).expect("corrupted sidecar should be written");
    fs::remove_file(&output_path).expect("completed output should be removable");

    let mut retry_translator = RecordingNativeLongDocTranslator::default();
    let outcome = retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut retry_translator,
        request,
        &result_json_path,
    );
    let error = outcome
        .result
        .expect_err("incomplete checkpoint should fail before retry/export");

    assert!(error.message.contains("checkpoint chunk 1"));
    assert!(error.message.contains("has no translated text"));
    assert!(error.message.contains("not marked failed"));
    assert!(!error.message.contains("Long Document worker"));
    assert!(!error.message.contains("CompatHost"));
    assert!(!error.message.contains(".NET"));
    assert_eq!(
        retry_translator.call_count(),
        0,
        "incomplete checkpoint should not call the provider"
    );
    assert!(
        !output_path.exists(),
        "incomplete checkpoint should not write output"
    );
    let sidecar_after =
        fs::read_to_string(&result_json_path).expect("corrupted sidecar should remain readable");
    assert_eq!(
        sidecar_after, corrupted_sidecar,
        "incomplete checkpoint should not rewrite the sidecar"
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_result_json_retry_failed_prechecks_restored_output_before_provider_translation() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-retry-output-precheck");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("translated.txt");
    let result_json_path = temp_dir.join("translated-result.json");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "fail-1", "ok-2"]),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        84,
    )
    .expect("native text retry output precheck request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut first_translator = RecordingNativeLongDocTranslator::failing_on(["fail-1"]);
    run_native_text_long_document_request_with_translator(&mut first_translator, request.clone())
        .result
        .expect("partial native text run should write checkpoint sidecar");
    fs::remove_file(&output_path).expect("partial output file should be removable");
    fs::create_dir_all(&output_path).expect("conflicting restored output directory should exist");

    let mut retry_translator = RecordingNativeLongDocTranslator::default();
    let outcome = retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut retry_translator,
        request,
        &result_json_path,
    );
    let error = outcome
        .result
        .expect_err("conflicting restored output path should fail before provider translation");

    assert!(error.message.contains("Long document output path"));
    assert!(error.message.contains("is a directory"));
    assert_eq!(
        retry_translator.call_count(),
        0,
        "invalid restored retry output target should be rejected before provider translation starts"
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_result_json_retry_failed_restores_original_bilingual_output_path() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-retry-bilingual-route");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let base_output_path = temp_dir.join("translated.txt");
    let bilingual_output_path = temp_dir.join("translated-bilingual.txt");
    let double_bilingual_output_path = temp_dir.join("translated-bilingual-bilingual.txt");
    let result_json_path = temp_dir.join("translated-result.json");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "fail-1"]),
                input_mode: "plaintext".to_string(),
                output_mode: "bilingual".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        8101,
    )
    .expect("native text bilingual retry request");
    request.params.output_path = Some(base_output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut first_translator = RecordingNativeLongDocTranslator::failing_on(["fail-1"]);
    let first = run_native_text_long_document_request_with_translator(
        &mut first_translator,
        request.clone(),
    )
    .result
    .expect("partial bilingual run should write checkpoint sidecar");
    assert_eq!(first.state, "PartiallyCompleted");
    assert_eq!(
        first.output_path.as_deref(),
        Some(bilingual_output_path.to_str().unwrap())
    );

    let mut retry_request = request.clone();
    retry_request.params.output_path = first.output_path.clone();
    let mut retry_translator = RecordingNativeLongDocTranslator::default();
    let retry = retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut retry_translator,
        retry_request,
        &result_json_path,
    )
    .result
    .expect("retry should restore the original bilingual output base path");

    assert_eq!(retry.state, "Completed");
    assert_eq!(
        retry.output_path.as_deref(),
        Some(bilingual_output_path.to_str().unwrap())
    );
    assert!(
        !double_bilingual_output_path.exists(),
        "retry must not derive a second bilingual suffix from the previous result path"
    );
    let output =
        fs::read_to_string(&bilingual_output_path).expect("bilingual retry output should exist");
    assert!(output.contains("[zh] ok-0"));
    assert!(output.contains("[zh] fail-1"));

    let retry_sidecar_json =
        fs::read_to_string(&result_json_path).expect("retry sidecar should exist");
    let retry_sidecar: serde_json::Value =
        serde_json::from_str(&retry_sidecar_json).expect("retry sidecar should parse");
    assert_eq!(
        retry_sidecar["checkpoint"]["outputPath"],
        base_output_path.display().to_string()
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_result_json_retry_failed_keeps_remaining_failures_partial() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-retry-still-partial");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("translated.txt");
    let result_json_path = temp_dir.join("translated-result.json");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: native_long_text_markers(&["ok-0", "fail-1"]),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        82,
    )
    .expect("native text retry-failed partial request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut first_translator = RecordingNativeLongDocTranslator::failing_on(["fail-1"]);
    run_native_text_long_document_request_with_translator(&mut first_translator, request.clone())
        .result
        .expect("partial native text run should write checkpoint sidecar");

    let mut retry_translator = RecordingNativeLongDocTranslator::failing_on(["fail-1"]);
    let retry = retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut retry_translator,
        request,
        &result_json_path,
    )
    .result
    .expect("retry failed should still export previous successful chunks");

    assert_eq!(retry.state, "PartiallyCompleted");
    assert_eq!(retry.succeeded_chunks, 1);
    assert_eq!(retry.failed_chunk_indexes, Some(vec![1]));
    assert_eq!(retry_translator.call_count(), 2);
    let output = fs::read_to_string(&output_path).expect("partial retry output should be written");
    assert!(output.contains("[zh] ok-0"));
    assert!(output.contains("[Chunk 2 translation failed.]"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_result_json_retry_failed_without_failures_reexports_without_provider_call() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-retry-no-failures");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("translated.txt");
    let result_json_path = temp_dir.join("translated-result.json");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        83,
    )
    .expect("native text retry no-failures request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut first_translator = RecordingNativeLongDocTranslator::default();
    run_native_text_long_document_request_with_translator(&mut first_translator, request.clone())
        .result
        .expect("completed native text run should write checkpoint sidecar");
    fs::write(&output_path, "stale output").expect("stale output should be writable");

    let mut retry_translator = RecordingNativeLongDocTranslator::default();
    let retry = retry_failed_native_text_long_document_from_result_json_with_translator(
        &mut retry_translator,
        request,
        &result_json_path,
    )
    .result
    .expect("retry without failed chunks should finalize from checkpoint");

    assert_eq!(retry.state, "Completed");
    assert_eq!(retry.failed_chunk_indexes, None);
    assert_eq!(
        retry_translator.call_count(),
        0,
        "no failed chunks should not call the provider during retry"
    );
    let output = fs::read_to_string(&output_path).expect("output should be reexported");
    assert_eq!(output, "[zh] A short document");

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_text_result_json_path_prechecked_before_provider_translation() {
    let temp_dir = unique_temp_dir("longdoc-native-result-json-precheck");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let output_path = temp_dir.join("translated.txt");
    let result_json_path = temp_dir.join("translated-result.json");
    fs::create_dir_all(&result_json_path).expect("conflicting sidecar directory should exist");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A short document".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                service: "google".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        79,
    )
    .expect("native text result sidecar precheck request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let error = outcome
        .result
        .expect_err("conflicting result JSON path should fail before provider translation");

    assert!(error.message.contains("Long document output path"));
    assert!(error.message.contains("is a directory"));
    assert_eq!(
        translator.call_count(),
        0,
        "invalid result JSON target should be rejected before provider translation starts"
    );
    assert!(!output_path.exists());

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_result_json_path_prechecked_before_provider_translation() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-result-json-precheck");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("paper.pdf");
    let output_path = temp_dir.join("paper-result.pdf");
    let result_json_path = temp_dir.join("paper-result.json");
    fs::write(
        &input_path,
        minimal_pdf_with_pages(&["PDF sidecar preflight"]),
    )
    .expect("pdf should be written");
    fs::create_dir_all(&result_json_path).expect("conflicting PDF sidecar directory should exist");

    let mut request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                translation_cache_enabled: false,
                ..Default::default()
            },
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
        179,
    )
    .expect("native PDF result sidecar precheck request");
    request.params.output_path = Some(output_path.display().to_string());
    request.params.result_json_path = Some(result_json_path.display().to_string());

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let error = outcome
        .result
        .expect_err("conflicting PDF result JSON path should fail before provider translation");

    assert!(error.message.contains("Long document output path"));
    assert!(error.message.contains("is a directory"));
    assert_eq!(
        translator.call_count(),
        0,
        "invalid PDF result JSON target should be rejected before provider translation starts"
    );
    assert!(
        !output_path.exists(),
        "PDF output should not be written when result JSON target is invalid"
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn native_pdf_both_output_prechecks_bilingual_text_path_before_writing_pdf_file() {
    let temp_dir = unique_temp_dir("longdoc-native-pdf-both-output-precheck");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let input_path = temp_dir.join("paper.pdf");
    let output_path = temp_dir.join("paper-result.pdf");
    let bilingual_path = temp_dir.join("paper-result-bilingual.txt");
    fs::write(&input_path, minimal_pdf_with_pages(&["Atomic PDF"])).expect("pdf should be written");
    fs::create_dir_all(&bilingual_path).expect("conflicting bilingual directory should exist");

    let mut request = build_long_document_request(
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
        77,
    )
    .expect("native PDF output precheck request");
    request.params.output_path = Some(output_path.display().to_string());

    let mut translator = RecordingNativeLongDocTranslator::default();
    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let error = outcome
        .result
        .expect_err("conflicting bilingual text path should fail before writing PDF");

    assert!(error.message.contains("Long document output path"));
    assert!(error.message.contains("is a directory"));
    assert_eq!(
        translator.call_count(),
        0,
        "invalid native PDF output targets should be rejected before provider translation starts"
    );
    assert!(
        !output_path.exists(),
        "PDF output should not be partially written when bilingual text target is invalid"
    );

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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("unknown service should fail locally before worker startup");

    assert!(error.message.contains("is not registered"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn default_windows_ai_long_document_reports_local_phi_status_without_nested_dotnet_workers() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _winrt_disabled = EnvironmentVariableGuard::set("EASYDICT_WINDOWS_AI_DISABLE_WINRT", "1");

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
                local_ai_provider: local_ai_provider_modes::WINDOWS_AI.to_string(),
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: input_path.to_string_lossy().to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                two_pass_context: false,
                ..Default::default()
            },
            ..Default::default()
        },
        33,
    )
    .expect("local AI WindowsAI request");

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_app_dir(request, &temp_dir);
    let error = outcome
        .result
        .expect_err("disabled WindowsAI WinRT should fail locally before worker startup");

    assert!(error.message.contains("Phi Silica"));
    assert!(!error.message.contains(".NET workers"));
    assert!(!error.message.contains("Long Document worker"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn windows_ai_local_ai_long_document_uses_native_client_without_worker() {
    let temp_dir = unique_temp_dir("longdoc-windows-ai-native-client");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::WINDOWS_AI.to_string(),
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A WindowsAI document.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                two_pass_context: false,
                ..Default::default()
            },
            ..Default::default()
        },
        54,
    )
    .expect("WindowsAI long document request");

    assert!(!long_document_request_can_route_natively(&request));

    let mut client = RecordingWindowsAiClient::with_generate_responses(
        [WindowsAiReadyState::Ready, WindowsAiReadyState::Ready],
        [Ok(WindowsAiResponse::complete(
            "WindowsAI translated document",
        ))],
    );
    let mut resolver =
        RecordingFoundryLocalEndpointResolver::new(Some("foundry-local-invalid".to_string()));

    let outcome = run_long_document_request_with_app_dir_and_native_local_ai_client(
        request,
        r"C:\MissingWorkerApp",
        &mut client,
        &mut resolver,
    );
    let result = outcome
        .result
        .expect("WindowsAI native LongDoc should succeed");

    assert_eq!(resolver.calls(), 0);
    assert_eq!(client.ready_state_calls(), 1);
    let prompts = client.generate_prompts();
    assert_eq!(prompts.len(), 1);
    assert!(prompts[0].contains("A WindowsAI document."));

    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("native WindowsAI output");
    assert!(output.contains("WindowsAI translated document"));
    assert!(outcome.events.iter().any(|event| matches!(
        event,
        LongDocumentEvent::Status(status)
            if status.message == "Translating text document natively"
    )));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn windows_ai_long_document_context_pass_uses_raw_generation_before_translation() {
    let temp_dir = unique_temp_dir("longdoc-windows-ai-context-pass");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::WINDOWS_AI.to_string(),
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "Transformer paper paragraph.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                two_pass_context: true,
                ..Default::default()
            },
            ..Default::default()
        },
        57,
    )
    .expect("WindowsAI context-pass long document request");

    let mut client = RecordingWindowsAiClient::with_generate_responses(
        [WindowsAiReadyState::Ready, WindowsAiReadyState::Ready],
        [
            Ok(WindowsAiResponse::complete(
                serde_json::json!({
                    "summary": "Transformer paper page.",
                    "glossary": {
                        "Transformer": "Transformer"
                    },
                    "preservation_hints": []
                })
                .to_string(),
            )),
            Ok(WindowsAiResponse::complete(
                "WindowsAI translated with context",
            )),
        ],
    );
    let mut resolver =
        RecordingFoundryLocalEndpointResolver::new(Some("foundry-local-invalid".to_string()));

    let outcome = run_long_document_request_with_app_dir_and_native_local_ai_client(
        request,
        r"C:\MissingWorkerApp",
        &mut client,
        &mut resolver,
    );
    let result = outcome
        .result
        .expect("WindowsAI context-pass LongDoc should succeed");

    assert_eq!(resolver.calls(), 0);
    let prompts = client.generate_prompts();
    assert_eq!(prompts.len(), 2);
    assert!(prompts[0].contains("Do NOT translate the document text"));
    assert!(prompts[0].contains("Transformer paper paragraph."));
    assert!(
        !prompts[0].contains("You are a professional translation engine"),
        "context prompt must be sent as raw generation, not wrapped as translation: {}",
        prompts[0]
    );
    assert!(prompts[1].contains("You are a professional translation engine"));
    assert!(prompts[1].contains("Document summary: Transformer paper page."));
    assert!(prompts[1].contains("Transformer -> Transformer"));
    assert!(prompts[1].contains("Transformer paper paragraph."));

    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("native WindowsAI context output");
    assert!(output.contains("WindowsAI translated with context"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn auto_windows_ai_ready_routes_long_document_before_foundry_probe() {
    let temp_dir = unique_temp_dir("longdoc-auto-windows-ai-ready");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::AUTO.to_string(),
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "Auto should prefer ready WindowsAI.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        55,
    )
    .expect("Auto local AI long document request");

    let mut client = RecordingWindowsAiClient::with_generate_responses(
        [WindowsAiReadyState::Ready, WindowsAiReadyState::Ready],
        [Ok(WindowsAiResponse::complete(
            "Auto WindowsAI translated document",
        ))],
    );
    let mut resolver =
        RecordingFoundryLocalEndpointResolver::new(Some("foundry-local-invalid".to_string()));

    let outcome = run_long_document_request_with_app_dir_and_native_local_ai_client(
        request,
        r"C:\MissingWorkerApp",
        &mut client,
        &mut resolver,
    );
    let result = outcome
        .result
        .expect("Auto-ready WindowsAI native LongDoc should succeed");

    assert_eq!(resolver.calls(), 0);
    assert_eq!(client.ready_state_calls(), 2);
    assert_eq!(client.generate_prompts().len(), 1);

    let output = fs::read_to_string(result.output_path.expect("output path"))
        .expect("native WindowsAI output");
    assert!(output.contains("Auto WindowsAI translated document"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn auto_windows_ai_not_ready_continues_long_document_foundry_fallback() {
    let temp_dir = unique_temp_dir("longdoc-auto-windows-ai-not-ready-foundry");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::AUTO.to_string(),
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "Auto should fall back when WindowsAI is not ready.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        56,
    )
    .expect("Auto local AI long document request");

    let mut client =
        RecordingWindowsAiClient::with_generate_responses([WindowsAiReadyState::NotReady], []);
    let mut resolver =
        RecordingFoundryLocalEndpointResolver::new(Some("foundry-local-invalid".to_string()));

    let outcome = run_long_document_request_with_app_dir_and_native_local_ai_client(
        request,
        r"C:\MissingWorkerApp",
        &mut client,
        &mut resolver,
    );
    let error = outcome
        .result
        .expect_err("invalid Foundry fallback should fail natively");

    assert_eq!(resolver.calls(), 1);
    assert_eq!(client.ready_state_calls(), 1);
    assert!(client.generate_prompts().is_empty());
    assert!(!error.message.contains("Long Document worker"));
    assert!(!error.message.contains("retained .NET workers"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn explicit_foundry_local_long_document_with_empty_endpoint_discovers_endpoint_natively_without_worker(
) {
    let temp_dir = unique_temp_dir("longdoc-explicit-foundry-empty-endpoint");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let request = build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::FOUNDRY_LOCAL.to_string(),
                foundry_local_endpoint: String::new(),
                foundry_local_model: "phi-3-mini".to_string(),
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "Foundry should be discovered for LongDoc.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        58,
    )
    .expect("explicit Foundry Local long document request");
    assert!(long_document_request_can_route_natively(&request));

    let http_client =
        RecordingFoundryOpenAiHttpClient::with_sse_responses([Ok(chat_completion_sse(&[
            "Foundry translated LongDoc chunk",
        ]))]);
    let resolver = RecordingFoundryLocalEndpointResolver::new(Some(
        "http://localhost:5273/openai/status".to_string(),
    ));
    let mut translator =
        FoundryOpenAiNativeLongDocTranslator::new(http_client.clone(), resolver.clone());

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("explicit Foundry Local LongDoc should translate through native OpenAI route");

    assert_eq!(resolver.calls(), 1);
    assert_eq!(resolver.status_calls(), 2);
    assert_eq!(resolver.start_calls(), 1);
    assert_eq!(resolver.load_model_calls(), vec!["phi-3-mini".to_string()]);
    let requests = http_client.requests();
    assert_eq!(requests.len(), 1);
    assert_eq!(
        requests[0].endpoint,
        "http://localhost:5273/v1/chat/completions"
    );
    assert_eq!(requests[0].body["model"], "phi-3-mini");

    let output_path = result.output_path.expect("translated output path");
    let output = fs::read_to_string(&output_path).expect("translated output");
    assert!(output.contains("Foundry translated LongDoc chunk"));

    let diagnostics = format!("{:?}\n{}", outcome.events, output);
    assert!(!diagnostics.contains("Long Document worker"));
    assert!(!diagnostics.contains("CompatHost"));
    assert!(!diagnostics.contains(".NET workers"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn openvino_local_ai_long_document_cache_miss_reports_native_download_preflight() {
    let temp_dir = unique_temp_dir("longdoc-openvino-cache-missing-native-preflight");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mut request = openvino_local_ai_long_document_request(&temp_dir, "en", "zh-Hans", 38);
    request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_app_dir(request, &temp_dir);
    let error = outcome
        .result
        .expect_err("OpenVINO cache miss should fail before retained workers");

    assert!(error
        .message
        .contains("OpenVINO runtime or NLLB-200 model is not downloaded"));
    assert!(error.message.contains("Download model"));
    assert!(!error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains("Long Document worker"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn local_ai_provider_alias_open_vino_long_document_uses_openvino_preflight() {
    let temp_dir = unique_temp_dir("longdoc-openvino-alias-cache-missing-native-preflight");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mut request = openvino_local_ai_long_document_request(&temp_dir, "en", "zh-Hans", 40);
    request.settings.local_ai_provider = Some("open_vino".to_string());
    request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_app_dir(request, &temp_dir);
    let error = outcome
        .result
        .expect_err("OpenVINO alias should fail before retained workers");

    assert!(error
        .message
        .contains("OpenVINO runtime or NLLB-200 model is not downloaded"));
    assert!(!error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains("Long Document worker"));
    assert!(!error.message.contains(".NET"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn openvino_local_ai_long_document_unknown_target_reports_native_language_preflight() {
    let temp_dir = unique_temp_dir("longdoc-openvino-unknown-target-native-preflight");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mut request = openvino_local_ai_long_document_request(&temp_dir, "en", "zh-Hans", 39);
    request.params.to = "hr".to_string();
    request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());

    assert!(!long_document_request_can_route_natively(&request));

    let outcome = run_long_document_request_with_app_dir(request, &temp_dir);
    let error = outcome
        .result
        .expect_err("unsupported OpenVINO target should fail before retained workers");

    assert!(error
        .message
        .contains("No local AI provider supports this language pair"));
    assert!(!error
        .message
        .contains("OpenVINO runtime or NLLB-200 model is not downloaded"));
    assert!(!error.message.contains("requires a Rust-native route"));
    assert!(!error.message.contains("Long Document worker"));
    assert!(!error.message.to_ascii_lowercase().contains("compat host"));

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn openvino_local_ai_long_document_translates_with_injected_native_nllb_translator() {
    let temp_dir = unique_temp_dir("longdoc-openvino-native-nllb-success");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let request = openvino_local_ai_long_document_request(&temp_dir, "en", "zh-Hans", 57);
    let mut translator = RecordingOpenVinoNllbLongDocTranslator::with_generated([200, 201, 202]);

    let outcome = run_native_text_long_document_request_with_translator(&mut translator, request);
    let result = outcome
        .result
        .expect("OpenVINO NLLB long document route should translate natively");

    assert_eq!(result.state, "Completed");
    assert_eq!(result.total_chunks, 1);
    assert_eq!(result.succeeded_chunks, 1);
    let output_path = result.output_path.expect("monolingual output path");
    let output = fs::read_to_string(&output_path).expect("translated output");
    assert!(output.contains("你好"));

    let requests = translator.requests();
    assert_eq!(requests.len(), 1);
    let request = &requests[0];
    assert_eq!(request.service.id, "windows-local-ai");
    assert_eq!(
        request.execution_kind,
        QuickTranslateExecutionKind::TranslateStream
    );
    assert_eq!(request.params.from.as_deref(), Some("en"));
    assert_eq!(request.params.to.as_deref(), Some("zh"));
    assert_eq!(
        request.params.services.as_deref(),
        Some(&["windows-local-ai".to_string()][..])
    );
    assert_eq!(
        request.settings.local_ai_provider.as_deref(),
        Some(local_ai_provider_modes::OPENVINO)
    );

    assert_eq!(
        translator.engine_calls(),
        vec![RecordingLongDocNllbEngineCall {
            input_ids: vec![101, 42, 2],
            forced_bos: 256001,
            max_new_tokens: 3,
        }]
    );

    fs::remove_dir_all(&temp_dir).ok();
}

#[test]
fn local_ai_long_document_route_matrix_stays_native_before_worker_fallback() {
    const READY_READY: &[WindowsAiReadyState] =
        &[WindowsAiReadyState::Ready, WindowsAiReadyState::Ready];
    const NOT_READY: &[WindowsAiReadyState] = &[WindowsAiReadyState::NotReady];
    const NO_WINDOWS_AI_PROBE: &[WindowsAiReadyState] = &[];

    #[derive(Clone, Copy)]
    enum ExpectedRoute {
        Success(&'static str),
        Error(&'static str),
    }

    struct RouteCase {
        name: &'static str,
        provider: &'static str,
        foundry_endpoint_setting: &'static str,
        foundry_probe_endpoint: Option<&'static str>,
        ready_states: &'static [WindowsAiReadyState],
        windows_ai_response: Option<&'static str>,
        expected: ExpectedRoute,
        expected_generic_native: bool,
        expected_resolver_calls: usize,
        expected_windows_ai_generations: usize,
    }

    let cases = [
        RouteCase {
            name: "explicit WindowsAI ready",
            provider: local_ai_provider_modes::WINDOWS_AI,
            foundry_endpoint_setting: "",
            foundry_probe_endpoint: Some("foundry-local-invalid"),
            ready_states: READY_READY,
            windows_ai_response: Some("matrix WindowsAI translation"),
            expected: ExpectedRoute::Success("matrix WindowsAI translation"),
            expected_generic_native: false,
            expected_resolver_calls: 0,
            expected_windows_ai_generations: 1,
        },
        RouteCase {
            name: "Auto WindowsAI ready",
            provider: local_ai_provider_modes::AUTO,
            foundry_endpoint_setting: "",
            foundry_probe_endpoint: Some("foundry-local-invalid"),
            ready_states: READY_READY,
            windows_ai_response: Some("matrix Auto WindowsAI translation"),
            expected: ExpectedRoute::Success("matrix Auto WindowsAI translation"),
            expected_generic_native: false,
            expected_resolver_calls: 0,
            expected_windows_ai_generations: 1,
        },
        RouteCase {
            name: "Auto probes Foundry after WindowsAI not ready",
            provider: local_ai_provider_modes::AUTO,
            foundry_endpoint_setting: "",
            foundry_probe_endpoint: Some("foundry-local-invalid"),
            ready_states: NOT_READY,
            windows_ai_response: None,
            expected: ExpectedRoute::Error("OpenAI HTTP request failed: builder error"),
            expected_generic_native: false,
            expected_resolver_calls: 1,
            expected_windows_ai_generations: 0,
        },
        RouteCase {
            name: "explicit FoundryLocal configured endpoint",
            provider: local_ai_provider_modes::FOUNDRY_LOCAL,
            foundry_endpoint_setting: "foundry-local-invalid",
            foundry_probe_endpoint: None,
            ready_states: NO_WINDOWS_AI_PROBE,
            windows_ai_response: None,
            expected: ExpectedRoute::Error("OpenAI HTTP request failed: builder error"),
            expected_generic_native: true,
            expected_resolver_calls: 0,
            expected_windows_ai_generations: 0,
        },
        RouteCase {
            name: "explicit OpenVINO cache preflight",
            provider: local_ai_provider_modes::OPENVINO,
            foundry_endpoint_setting: "",
            foundry_probe_endpoint: Some("foundry-local-invalid"),
            ready_states: NO_WINDOWS_AI_PROBE,
            windows_ai_response: None,
            expected: ExpectedRoute::Error("OpenVINO runtime or NLLB-200 model is not downloaded"),
            expected_generic_native: false,
            expected_resolver_calls: 0,
            expected_windows_ai_generations: 0,
        },
    ];

    for (index, case) in cases.into_iter().enumerate() {
        let temp_dir = unique_temp_dir(&format!(
            "longdoc-local-ai-route-matrix-{}",
            case.name.replace([' ', '-'], "_")
        ));
        fs::create_dir_all(&temp_dir).expect("temp dir should be created");
        let app_dir = temp_dir.join("stale-app-dir");
        write_stale_longdoc_local_ai_payload_markers(&app_dir);

        let mut request = local_ai_long_document_matrix_request(
            &temp_dir,
            case.provider,
            case.foundry_endpoint_setting,
            70 + index as u64,
        );
        if case.provider == local_ai_provider_modes::OPENVINO {
            request.settings.cache_dir = Some(temp_dir.to_string_lossy().to_string());
        }
        assert_eq!(
            long_document_request_can_route_natively(&request),
            case.expected_generic_native,
            "{} should match the expected generic native routing boundary",
            case.name
        );

        let responses = case
            .windows_ai_response
            .map(|text| Ok(WindowsAiResponse::complete(text)))
            .into_iter();
        let mut client = RecordingWindowsAiClient::with_generate_responses(
            case.ready_states.iter().copied(),
            responses,
        );
        let mut resolver = RecordingFoundryLocalEndpointResolver::new(
            case.foundry_probe_endpoint.map(str::to_string),
        );

        let outcome = run_long_document_request_with_app_dir_and_native_local_ai_client(
            request,
            &app_dir,
            &mut client,
            &mut resolver,
        );
        let diagnostics = format!("{:?}", outcome.events);

        match case.expected {
            ExpectedRoute::Success(expected_text) => {
                let result = outcome.result.unwrap_or_else(|error| {
                    panic!(
                        "{} should succeed: {error:?}; ready_state_calls={}; generate_prompts={:?}",
                        case.name,
                        client.ready_state_calls(),
                        client.generate_prompts()
                    )
                });
                let output_path = result.output_path.expect("matrix output path");
                let output = fs::read_to_string(&output_path).expect("matrix output should exist");
                assert!(
                    output.contains(expected_text),
                    "{} should write native output containing {expected_text:?}: {output}",
                    case.name
                );
                assert_no_retained_longdoc_markers(case.name, &format!("{diagnostics}\n{output}"));
            }
            ExpectedRoute::Error(expected_text) => {
                let error = outcome
                    .result
                    .expect_err("matrix case should fail on a native/preflight boundary");
                assert!(
                    error.message.contains(expected_text),
                    "{} should fail on the expected native/preflight boundary {expected_text:?}: {}",
                    case.name,
                    error.message
                );
                assert_no_retained_longdoc_markers(
                    case.name,
                    &format!("{diagnostics}\n{}", error.message),
                );
            }
        }

        assert_eq!(
            resolver.calls(),
            case.expected_resolver_calls,
            "{} should only touch the expected native Foundry probe path",
            case.name
        );
        assert_eq!(
            client.generate_prompts().len(),
            case.expected_windows_ai_generations,
            "{} should only use the expected native WindowsAI generation path",
            case.name
        );

        fs::remove_dir_all(&temp_dir).ok();
    }
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("invalid native endpoint should fail locally");

    assert!(
        !error.message.contains("Long Document worker"),
        "native local AI longdoc should fail locally before retained worker fallback: {}",
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
                translation_cache_enabled: false,
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
    let error = outcome
        .result
        .expect_err("non-native stale Foundry Local longdoc should fail before worker startup");

    assert!(!error.message.contains(".NET workers"));
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

    let outcome = run_long_document_request_with_app_dir(request, r"C:\MissingWorkerApp");
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
        Task::Exit => "exit",
        Task::ScrollToTop(_) => "scroll_to_top",
        Task::ScrollTo { .. } => "scroll_to",
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
struct RecordingWindowsAiClient {
    ready_state_calls: Arc<Mutex<usize>>,
    states: Arc<Mutex<VecDeque<WindowsAiReadyState>>>,
    generate_prompts: Arc<Mutex<Vec<String>>>,
    generate_options: Arc<Mutex<Vec<WindowsAiGenerationOptions>>>,
    generate_responses: Arc<Mutex<VecDeque<Result<WindowsAiResponse, WindowsAiError>>>>,
}

impl RecordingWindowsAiClient {
    fn with_generate_responses(
        states: impl IntoIterator<Item = WindowsAiReadyState>,
        generate_responses: impl IntoIterator<Item = Result<WindowsAiResponse, WindowsAiError>>,
    ) -> Self {
        Self {
            ready_state_calls: Arc::new(Mutex::new(0)),
            states: Arc::new(Mutex::new(states.into_iter().collect())),
            generate_prompts: Arc::new(Mutex::new(Vec::new())),
            generate_options: Arc::new(Mutex::new(Vec::new())),
            generate_responses: Arc::new(Mutex::new(generate_responses.into_iter().collect())),
        }
    }

    fn ready_state_calls(&self) -> usize {
        *self
            .ready_state_calls
            .lock()
            .expect("ready state calls lock")
    }

    fn generate_prompts(&self) -> Vec<String> {
        self.generate_prompts
            .lock()
            .expect("generate prompts lock")
            .clone()
    }
}

impl WindowsAiLanguageModelProbe for RecordingWindowsAiClient {
    fn ready_state(&mut self) -> WindowsAiReadyState {
        *self
            .ready_state_calls
            .lock()
            .expect("ready state calls lock") += 1;
        self.states
            .lock()
            .expect("ready states lock")
            .pop_front()
            .unwrap_or(WindowsAiReadyState::NotSupportedOnCurrentSystem)
    }
}

impl WindowsAiLanguageModelClient for RecordingWindowsAiClient {
    fn generate(
        &mut self,
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<WindowsAiResponse, WindowsAiError> {
        self.generate_prompts
            .lock()
            .expect("generate prompts lock")
            .push(prompt.to_string());
        self.generate_options
            .lock()
            .expect("generate options lock")
            .push(options);
        self.generate_responses
            .lock()
            .expect("generate responses lock")
            .pop_front()
            .unwrap_or_else(|| Ok(WindowsAiResponse::complete(String::new())))
    }

    fn generate_stream(
        &mut self,
        _prompt: &str,
        _options: WindowsAiGenerationOptions,
    ) -> Result<Vec<String>, WindowsAiError> {
        Err(WindowsAiError::new(
            "recording WindowsAI LongDoc client did not expect streaming",
        ))
    }

    fn warm_up(
        &mut self,
        _prompt: &str,
        _options: WindowsAiGenerationOptions,
    ) -> Result<(), WindowsAiError> {
        Ok(())
    }
}

#[derive(Clone, Default)]
struct RecordingFoundryLocalEndpointResolver {
    calls: Arc<Mutex<usize>>,
    status_calls: Arc<Mutex<usize>>,
    start_calls: Arc<Mutex<usize>>,
    load_model_calls: Arc<Mutex<Vec<String>>>,
    endpoint: Option<String>,
}

impl RecordingFoundryLocalEndpointResolver {
    fn new(endpoint: Option<String>) -> Self {
        Self {
            calls: Arc::new(Mutex::new(0)),
            status_calls: Arc::new(Mutex::new(0)),
            start_calls: Arc::new(Mutex::new(0)),
            load_model_calls: Arc::new(Mutex::new(Vec::new())),
            endpoint,
        }
    }

    fn calls(&self) -> usize {
        *self.calls.lock().expect("resolver calls lock")
    }

    fn status_calls(&self) -> usize {
        *self.status_calls.lock().expect("status calls lock")
    }

    fn start_calls(&self) -> usize {
        *self.start_calls.lock().expect("start calls lock")
    }

    fn load_model_calls(&self) -> Vec<String> {
        self.load_model_calls
            .lock()
            .expect("load model calls lock")
            .clone()
    }
}

impl FoundryLocalEndpointResolver for RecordingFoundryLocalEndpointResolver {
    fn resolve_chat_completions_endpoint(&mut self) -> Result<Option<String>, FoundryLocalError> {
        *self.calls.lock().expect("resolver calls lock") += 1;
        Ok(self.endpoint.clone())
    }
}

impl FoundryLocalRuntimeController for RecordingFoundryLocalEndpointResolver {
    fn get_status(&mut self) -> Result<FoundryLocalRuntimeStatus, FoundryLocalError> {
        let mut status_calls = self.status_calls.lock().expect("status calls lock");
        *status_calls += 1;
        let state = if *status_calls == 1 {
            FoundryLocalRuntimeState::NotRunning
        } else {
            FoundryLocalRuntimeState::Running
        };
        Ok(FoundryLocalRuntimeStatus::new(state))
    }

    fn start_service(&mut self) -> Result<(), FoundryLocalError> {
        *self.start_calls.lock().expect("start calls lock") += 1;
        Ok(())
    }

    fn load_model(&mut self, model: &str) -> Result<(), FoundryLocalError> {
        self.load_model_calls
            .lock()
            .expect("load model calls lock")
            .push(model.to_string());
        Ok(())
    }
}

#[derive(Clone, Default)]
struct RecordingFoundryOpenAiHttpClient {
    requests: Arc<Mutex<Vec<OpenAiHttpRequestPlan>>>,
    responses: Arc<Mutex<VecDeque<Result<String, OpenAiExecutionError>>>>,
}

impl RecordingFoundryOpenAiHttpClient {
    fn with_sse_responses(
        responses: impl IntoIterator<Item = Result<String, OpenAiExecutionError>>,
    ) -> Self {
        Self {
            requests: Arc::new(Mutex::new(Vec::new())),
            responses: Arc::new(Mutex::new(responses.into_iter().collect())),
        }
    }

    fn requests(&self) -> Vec<OpenAiHttpRequestPlan> {
        self.requests.lock().expect("OpenAI requests lock").clone()
    }
}

impl OpenAiHttpClient for RecordingFoundryOpenAiHttpClient {
    fn post_sse(
        &mut self,
        request: &OpenAiHttpRequestPlan,
    ) -> Result<String, OpenAiExecutionError> {
        self.requests
            .lock()
            .expect("OpenAI requests lock")
            .push(request.clone());
        self.responses
            .lock()
            .expect("OpenAI responses lock")
            .pop_front()
            .unwrap_or_else(|| {
                Err(OpenAiExecutionError::new(
                    OpenAiExecutionErrorCode::Unknown,
                    "test OpenAI response was not queued",
                ))
            })
    }
}

#[derive(Clone)]
struct FoundryOpenAiNativeLongDocTranslator {
    http_client: RecordingFoundryOpenAiHttpClient,
    resolver: RecordingFoundryLocalEndpointResolver,
}

impl FoundryOpenAiNativeLongDocTranslator {
    fn new(
        http_client: RecordingFoundryOpenAiHttpClient,
        resolver: RecordingFoundryLocalEndpointResolver,
    ) -> Self {
        Self {
            http_client,
            resolver,
        }
    }
}

impl NativeLongDocumentTranslator for FoundryOpenAiNativeLongDocTranslator {
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError> {
        let mut backend = NativeOpenAiQuickTranslateBackend::with_foundry_local_endpoint_resolver(
            self.http_client.clone(),
            self.resolver.clone(),
        );
        backend
            .configure(&request.settings)
            .map_err(|error| LongDocumentBackendError::new(error.message))?;
        backend
            .translate_stream(&request.params)
            .map(|streamed| streamed.result.translated_text)
            .map_err(|error| LongDocumentBackendError::new(error.message))
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

#[derive(Clone, Default)]
struct RecordingLongDocNllbTokenizer {
    encoded_sources: Arc<Mutex<Vec<(String, String)>>>,
    target_language_codes: Arc<Mutex<Vec<String>>>,
}

impl NllbTokenizer for RecordingLongDocNllbTokenizer {
    fn encode_source(&self, text: &str, source_flores_code: &str) -> Result<Vec<i32>, NllbError> {
        self.encoded_sources
            .lock()
            .expect("encoded sources lock")
            .push((text.to_string(), source_flores_code.to_string()));
        assert_eq!(text, "A local AI long document chunk.");
        assert_eq!(source_flores_code, "eng_Latn");
        Ok(vec![101, 42, 2])
    }

    fn decode(&self, token_ids: &[i32]) -> Result<String, NllbError> {
        match token_ids {
            [200] => Ok("你".to_string()),
            [200, 201] => Ok("你".to_string()),
            [200, 201, 202] => Ok("你好".to_string()),
            _ => Err(NllbError::new("unexpected long document NLLB token ids")),
        }
    }

    fn language_token_id(&self, flores_code: &str) -> Result<i32, NllbError> {
        self.target_language_codes
            .lock()
            .expect("target language codes lock")
            .push(flores_code.to_string());
        assert_eq!(flores_code, "zho_Hans");
        Ok(256001)
    }
}

#[derive(Clone)]
struct RecordingLongDocNllbEngine {
    generated: Arc<Vec<i32>>,
    calls: Arc<Mutex<Vec<RecordingLongDocNllbEngineCall>>>,
}

impl RecordingLongDocNllbEngine {
    fn with_generated(generated: impl IntoIterator<Item = i32>) -> Self {
        Self {
            generated: Arc::new(generated.into_iter().collect()),
            calls: Arc::new(Mutex::new(Vec::new())),
        }
    }

    fn calls(&self) -> Vec<RecordingLongDocNllbEngineCall> {
        self.calls.lock().expect("NLLB engine calls lock").clone()
    }
}

impl Default for RecordingLongDocNllbEngine {
    fn default() -> Self {
        Self::with_generated([])
    }
}

impl NllbInferenceEngine for RecordingLongDocNllbEngine {
    fn generate(
        &mut self,
        encoder_input_ids: &[i32],
        forced_bos_token_id: i32,
        max_new_tokens: usize,
    ) -> Result<Vec<i32>, NllbError> {
        self.calls
            .lock()
            .expect("NLLB engine calls lock")
            .push(RecordingLongDocNllbEngineCall {
                input_ids: encoder_input_ids.to_vec(),
                forced_bos: forced_bos_token_id,
                max_new_tokens,
            });
        Ok((*self.generated).clone())
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct RecordingLongDocNllbEngineCall {
    input_ids: Vec<i32>,
    forced_bos: i32,
    max_new_tokens: usize,
}

#[derive(Clone)]
struct RecordingOpenVinoNllbLongDocTranslator {
    tokenizer: RecordingLongDocNllbTokenizer,
    engine: RecordingLongDocNllbEngine,
    requests: Arc<Mutex<Vec<QuickTranslateServiceRequest>>>,
}

impl RecordingOpenVinoNllbLongDocTranslator {
    fn with_generated(generated: impl IntoIterator<Item = i32>) -> Self {
        Self {
            tokenizer: RecordingLongDocNllbTokenizer::default(),
            engine: RecordingLongDocNllbEngine::with_generated(generated),
            requests: Arc::new(Mutex::new(Vec::new())),
        }
    }

    fn requests(&self) -> Vec<QuickTranslateServiceRequest> {
        self.requests
            .lock()
            .expect("OpenVINO LongDoc requests lock")
            .clone()
    }

    fn engine_calls(&self) -> Vec<RecordingLongDocNllbEngineCall> {
        self.engine.calls()
    }
}

impl NativeLongDocumentTranslator for RecordingOpenVinoNllbLongDocTranslator {
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError> {
        if is_native_document_context_request(&request) {
            return Err(LongDocumentBackendError::new(
                "OpenVINO NLLB test translator ignores document context probes",
            ));
        }

        self.requests
            .lock()
            .expect("OpenVINO LongDoc requests lock")
            .push(request.clone());

        let translator =
            NllbTranslator::new(self.tokenizer.clone(), self.engine.clone()).with_max_new_tokens(3);
        let mut backend = NativeOpenVinoQuickTranslateBackend::new(translator);
        backend
            .configure(&request.settings)
            .map_err(|error| LongDocumentBackendError::new(error.message))?;
        let stream = backend
            .translate_stream(&request.params)
            .map_err(|error| LongDocumentBackendError::new(error.message))?;
        Ok(stream.result.translated_text)
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
        if is_native_document_context_request(&request) {
            return Err(LongDocumentBackendError::new(
                "recording translator ignores document context probes",
            ));
        }

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

#[derive(Clone)]
struct CancellingNativeLongDocTranslator {
    inner: RecordingNativeLongDocTranslator,
    cancel_after_calls: usize,
    cancelled: Arc<AtomicBool>,
}

impl CancellingNativeLongDocTranslator {
    fn after_calls(cancel_after_calls: usize, cancelled: Arc<AtomicBool>) -> Self {
        Self {
            inner: RecordingNativeLongDocTranslator::default(),
            cancel_after_calls,
            cancelled,
        }
    }

    fn call_count(&self) -> usize {
        self.inner.call_count()
    }
}

impl NativeLongDocumentTranslator for CancellingNativeLongDocTranslator {
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError> {
        let result = self.inner.translate_chunk(request);
        if self.inner.call_count() >= self.cancel_after_calls {
            self.cancelled.store(true, Ordering::SeqCst);
        }
        result
    }
}

#[derive(Clone, Default)]
struct ContextAwareNativeLongDocTranslator {
    context_calls: Arc<Mutex<Vec<QuickTranslateServiceRequest>>>,
    translation_calls: Arc<Mutex<Vec<QuickTranslateServiceRequest>>>,
}

impl NativeLongDocumentTranslator for ContextAwareNativeLongDocTranslator {
    fn translate_chunk(
        &mut self,
        request: QuickTranslateServiceRequest,
    ) -> Result<String, LongDocumentBackendError> {
        if is_native_document_context_map_request(&request) {
            let preserved = request
                .params
                .text
                .split("\n\n")
                .find(|chunk| chunk.contains("BLEU-28.4"))
                .unwrap_or("BLEU-28.4")
                .trim()
                .to_string();
            self.context_calls
                .lock()
                .expect("context calls lock")
                .push(request);
            return Ok(serde_json::json!({
                "summary": "Transformer paper page.",
                "glossary": {
                    "Transformer": "Transformer"
                },
                "preservation_hints": [preserved]
            })
            .to_string());
        }

        if is_native_document_context_reduce_request(&request) {
            self.context_calls
                .lock()
                .expect("context calls lock")
                .push(request);
            return Ok("Merged Transformer paper summary.".to_string());
        }

        self.translation_calls
            .lock()
            .expect("translation calls lock")
            .push(request.clone());
        Ok(format!("[zh] {}", request.params.text.trim()))
    }
}

impl ContextAwareNativeLongDocTranslator {
    fn context_calls(&self) -> Vec<QuickTranslateServiceRequest> {
        self.context_calls
            .lock()
            .expect("context calls lock")
            .clone()
    }

    fn translation_calls(&self) -> Vec<QuickTranslateServiceRequest> {
        self.translation_calls
            .lock()
            .expect("translation calls lock")
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
        if is_native_document_context_request(&request) {
            return Err(LongDocumentBackendError::new(
                "formula retry translator ignores document context probes",
            ));
        }

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

fn is_native_document_context_request(request: &QuickTranslateServiceRequest) -> bool {
    is_native_document_context_map_request(request)
        || is_native_document_context_reduce_request(request)
}

fn is_native_document_context_map_request(request: &QuickTranslateServiceRequest) -> bool {
    request
        .params
        .custom_prompt
        .as_deref()
        .unwrap_or_default()
        .contains("Do NOT translate the document text")
}

fn is_native_document_context_reduce_request(request: &QuickTranslateServiceRequest) -> bool {
    request
        .params
        .custom_prompt
        .as_deref()
        .unwrap_or_default()
        .contains("Merge them into a single 1-3 sentence summary")
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

fn openvino_local_ai_long_document_request(
    temp_dir: &std::path::Path,
    source_language: &str,
    target_language: &str,
    query_id: u64,
) -> easydict_app::LongDocumentServiceRequest {
    build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: local_ai_provider_modes::OPENVINO.to_string(),
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: "A local AI long document chunk.".to_string(),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: source_language.to_string(),
                target_language: target_language.to_string(),
                ..Default::default()
            },
            ..Default::default()
        },
        query_id,
    )
    .expect("OpenVINO local AI long document request")
}

fn local_ai_long_document_matrix_request(
    temp_dir: &std::path::Path,
    provider: &str,
    foundry_endpoint: &str,
    query_id: u64,
) -> easydict_app::LongDocumentServiceRequest {
    build_long_document_request(
        &EasydictUiState {
            settings: easydict_app::SettingsState {
                local_ai_provider: provider.to_string(),
                foundry_local_endpoint: foundry_endpoint.to_string(),
                foundry_local_model: "phi-3-mini".to_string(),
                translation_cache_enabled: false,
                ..Default::default()
            },
            long_document: easydict_app::LongDocumentState {
                selected_file: "No file selected".to_string(),
                source_text: format!("A {provider} local AI long document chunk."),
                input_mode: "plaintext".to_string(),
                output_folder: temp_dir.to_string_lossy().to_string(),
                service: "windows-local-ai".to_string(),
                source_language: "en".to_string(),
                target_language: "zh-Hans".to_string(),
                two_pass_context: false,
                ..Default::default()
            },
            ..Default::default()
        },
        query_id,
    )
    .expect("LocalAI route matrix long document request")
}

fn write_stale_longdoc_local_ai_payload_markers(app_dir: &std::path::Path) {
    let longdoc_worker_dir = app_dir.join("workers").join("longdoc");
    let localai_worker_dir = app_dir.join("workers").join("localai");
    let dotnet_host_dir = app_dir.join("dotnet").join("host").join("fxr");
    let dotnet_shared_dir = app_dir
        .join("dotnet")
        .join("shared")
        .join("Microsoft.NETCore.App");
    fs::create_dir_all(&longdoc_worker_dir).expect("stale LongDoc worker dir should be created");
    fs::create_dir_all(&localai_worker_dir).expect("stale LocalAI worker dir should be created");
    fs::create_dir_all(&dotnet_host_dir).expect("stale dotnet host dir should be created");
    fs::create_dir_all(&dotnet_shared_dir).expect("stale dotnet shared dir should be created");
    fs::write(
        app_dir.join("Easydict.CompatHost.exe"),
        b"stale compat host",
    )
    .expect("stale CompatHost marker should be written");
    fs::write(
        longdoc_worker_dir.join("Easydict.Workers.LongDoc.exe"),
        b"stale longdoc worker",
    )
    .expect("stale LongDoc worker marker should be written");
    fs::write(
        localai_worker_dir.join("Easydict.Workers.LocalAi.exe"),
        b"stale localai worker",
    )
    .expect("stale LocalAI worker marker should be written");
    fs::write(app_dir.join("dotnet").join("dotnet.exe"), b"stale dotnet")
        .expect("stale dotnet marker should be written");
    fs::write(dotnet_host_dir.join("hostfxr.dll"), b"stale hostfxr")
        .expect("stale hostfxr marker should be written");
    fs::write(
        dotnet_shared_dir.join("System.Private.CoreLib.dll"),
        b"stale corelib",
    )
    .expect("stale CoreLib marker should be written");
}

fn assert_no_retained_longdoc_markers(context: &str, diagnostics: &str) {
    for forbidden in [
        "Long Document worker",
        ".NET Long Document workers",
        ".NET Local AI workers",
        "retained .NET workers",
        "Easydict.Workers",
        "Easydict.CompatHost",
        "CompatHost",
        "dotnet.exe",
        "hostfxr",
        "DOTNET_ROOT",
    ] {
        assert!(
            !diagnostics.contains(forbidden),
            "{context} should stay on native/preflight routes before retained marker {forbidden}: {diagnostics}"
        );
    }
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
