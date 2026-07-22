use std::collections::HashMap;
use std::fmt;
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use crate::quick_translate::{
    QuickTranslateServiceRequest, QuickTranslateServiceUpdate, QuickTranslateStreamChunk,
};
use crate::state::Message;
use win_fluent::platform::Hotkey;
use win_fluent::subscription::WindowEvent;

const DEBUG_ENV: &str = "EASYDICT_RS_DEBUG";
const VERBOSE_ENV: &str = "EASYDICT_RS_DEBUG_VERBOSE";

static STARTED_AT: OnceLock<Instant> = OnceLock::new();
static DEBUG_ENABLED: OnceLock<bool> = OnceLock::new();
static VERBOSE_ENABLED: OnceLock<bool> = OnceLock::new();
static LAST_SUBSCRIPTION_PLAN: OnceLock<Mutex<Option<String>>> = OnceLock::new();
static QUICK_TRANSLATE_STARTS: OnceLock<Mutex<HashMap<(u64, String), Instant>>> = OnceLock::new();

pub(crate) fn enabled() -> bool {
    *DEBUG_ENABLED.get_or_init(|| env_value_enabled(std::env::var(DEBUG_ENV).ok()))
}

pub(crate) fn verbose_enabled() -> bool {
    *VERBOSE_ENABLED.get_or_init(|| enabled() && env_value_enabled(std::env::var(VERBOSE_ENV).ok()))
}

pub(crate) fn log_startup() {
    if !enabled() {
        return;
    }

    log(
        "app",
        format_args!(
            "debug enabled version={} verbose={}",
            env!("CARGO_PKG_VERSION"),
            verbose_enabled()
        ),
    );
}

pub(crate) fn log(category: &str, args: fmt::Arguments<'_>) {
    if !enabled() {
        return;
    }

    let started_at = STARTED_AT.get_or_init(Instant::now);
    eprintln!(
        "[easydict-rs][{category}] +{}ms {args}",
        started_at.elapsed().as_millis()
    );
}

pub(crate) fn log_message(message: &Message) {
    if !enabled() {
        return;
    }

    match message {
        Message::HotkeyTriggered(id) => {
            log("event", format_args!("message=HotkeyTriggered id={id}"));
        }
        Message::TrayCommand(id) => {
            log("event", format_args!("message=TrayCommand id={id}"));
        }
        Message::WindowEvent(event) => {
            let (name, window_id) = window_event_parts(event);
            log(
                "event",
                format_args!("message=WindowEvent event={name} window={window_id}"),
            );
        }
        Message::ClipboardTextReceived(text) => {
            log(
                "event",
                format_args!(
                    "message=ClipboardTextReceived present={} text_len={}",
                    text.is_some(),
                    optional_text_len(text.as_deref())
                ),
            );
        }
        Message::TrayClipboardTextReceived(text) => {
            log(
                "event",
                format_args!(
                    "message=TrayClipboardTextReceived present={} text_len={}",
                    text.is_some(),
                    optional_text_len(text.as_deref())
                ),
            );
        }
        Message::TrayClipboardReadFinished(result) => {
            log(
                "event",
                format_args!(
                    "message=TrayClipboardReadFinished status={} text_len={} error_len={}",
                    result_status(result),
                    result
                        .as_ref()
                        .ok()
                        .and_then(|text| text.as_deref())
                        .map(char_len)
                        .unwrap_or(0),
                    result
                        .as_ref()
                        .err()
                        .map(|error| char_len(error))
                        .unwrap_or(0)
                ),
            );
        }
        Message::ClipboardMonitorFailed(error) => {
            log(
                "event",
                format_args!(
                    "message=ClipboardMonitorFailed error_len={}",
                    char_len(error)
                ),
            );
        }
        Message::ClipboardMonitorRecovered => {
            log("event", format_args!("message=ClipboardMonitorRecovered"));
        }
        Message::TextSelectionCaptureFinished(result) => {
            log(
                "event",
                format_args!(
                    "message=TextSelectionCaptureFinished status={} text_len={} error_len={}",
                    result_status(result),
                    result
                        .as_ref()
                        .ok()
                        .and_then(|text| text.as_deref())
                        .map(char_len)
                        .unwrap_or(0),
                    result
                        .as_ref()
                        .err()
                        .map(|error| char_len(error))
                        .unwrap_or(0)
                ),
            );
        }
        Message::SelectionTextReady {
            text,
            anchor_x,
            anchor_y,
            generation,
        } => {
            log(
                "event",
                format_args!(
                    "message=SelectionTextReady text_len={} anchor=({}, {}) generation={generation}",
                    char_len(text),
                    anchor_x,
                    anchor_y
                ),
            );
        }
        Message::MouseSelectionInputHookEvent(_) if verbose_enabled() => {
            log(
                "event",
                format_args!("message=MouseSelectionInputHookEvent verbose=true"),
            );
        }
        Message::MouseSelectionInputHookEvent(_) => {}
        Message::QuickTranslateStreamChunk(chunk) => {
            log_quick_translate_stream_chunk(chunk);
        }
        Message::QuickTranslateServiceFinished(_) => {}
        Message::SourceTextChanged(text)
        | Message::FloatingTextChanged(text)
        | Message::LongDocumentSourceTextChanged(text) => {
            if verbose_enabled() {
                log(
                    "ui",
                    format_args!(
                        "message={} text_len={} text_hash={:016x}",
                        message_variant_name(message),
                        char_len(text),
                        stable_text_hash(text)
                    ),
                );
            }
        }
        Message::FloatingSurfaceTextChanged(surface, text) => {
            if verbose_enabled() {
                log(
                    "ui",
                    format_args!(
                        "message=FloatingSurfaceTextChanged surface={surface:?} text_len={}",
                        char_len(text)
                    ),
                );
            }
        }
        Message::CaptureMouseMoved(point) => {
            if verbose_enabled() {
                log(
                    "event",
                    format_args!("message=CaptureMouseMoved x={} y={}", point.x, point.y),
                );
            }
        }
        Message::CaptureWindowsChanged(windows) => {
            if verbose_enabled() {
                log(
                    "event",
                    format_args!("message=CaptureWindowsChanged count={}", windows.len()),
                );
            }
        }
        Message::CaptureSelectionChanged(selection) => {
            if verbose_enabled() {
                log(
                    "event",
                    format_args!(
                        "message=CaptureSelectionChanged present={}",
                        selection.is_some()
                    ),
                );
            }
        }
        Message::ServiceProviderSettingChanged(provider, field, _) => {
            log(
                "ui",
                format_args!(
                    "message=ServiceProviderSettingChanged provider={provider} field={field:?} value=redacted"
                ),
            );
        }
        Message::OpenAIApiKeyChanged(_)
        | Message::DeepLApiKeyChanged(_)
        | Message::CaiyunApiKeyChanged(_)
        | Message::NiuTransApiKeyChanged(_)
        | Message::YoudaoAppSecretChanged(_)
        | Message::VolcanoSecretAccessKeyChanged(_)
        | Message::OcrApiKeyChanged(_) => {
            log(
                "ui",
                format_args!("message={} value=redacted", message_variant_name(message)),
            );
        }
        Message::FloatingSourceLanguageChanged(_, value)
        | Message::FloatingTargetLanguageChanged(_, value) => {
            log(
                "ui",
                format_args!("message={} value={value}", message_variant_name(message)),
            );
        }
        Message::ModeChanged(value)
        | Message::SourceLanguageChanged(value)
        | Message::TargetLanguageChanged(value)
        | Message::LongDocumentSourceLanguageChanged(value)
        | Message::LongDocumentTargetLanguageChanged(value)
        | Message::LongDocumentServiceChanged(value)
        | Message::LongDocumentInputModeChanged(value)
        | Message::LongDocumentOutputModeChanged(value) => {
            log(
                "ui",
                format_args!("message={} value={value}", message_variant_name(message)),
            );
        }
        Message::ToggleLongDocumentHistoryExpanded(value) => {
            log(
                "ui",
                format_args!("message={} value={value}", message_variant_name(message)),
            );
        }
        _ if high_volume_message(message) => {}
        _ if button_like_message(message) => {
            log(
                "button",
                format_args!("message={}", message_variant_name(message)),
            );
        }
        _ => {
            log(
                "ui",
                format_args!("message={}", message_variant_name(message)),
            );
        }
    }
}

pub(crate) fn log_subscription_plan(hotkeys: &[Hotkey], windows: &[&str], tray: bool) {
    if !enabled() {
        return;
    }

    let hotkey_ids = hotkeys
        .iter()
        .map(|hotkey| hotkey.id.as_str())
        .collect::<Vec<_>>()
        .join(",");
    let window_ids = windows.join(",");
    let summary = format!(
        "hotkeys={} [{}] tray={} windows={} [{}]",
        hotkeys.len(),
        hotkey_ids,
        tray,
        windows.len(),
        window_ids
    );

    let state = LAST_SUBSCRIPTION_PLAN.get_or_init(|| Mutex::new(None));
    let Ok(mut last) = state.lock() else {
        return;
    };
    if last.as_deref() == Some(summary.as_str()) {
        return;
    }

    log("event", format_args!("subscriptions {summary}"));
    *last = Some(summary);
}

pub(crate) fn log_quick_translate_request(
    request: &QuickTranslateServiceRequest,
    bypass_cache_read: bool,
) {
    if !enabled() {
        return;
    }

    track_quick_translate_start(request);
    log(
        "network",
        format_args!(
            "quick_translate_start query_id={} service={} service_name={} mode={:?} kind={:?} retry={} text_len={} from={} to={} timeout_ms={}",
            request.query_id,
            request.service.id,
            request.service.name,
            request.query_mode,
            request.execution_kind,
            bypass_cache_read,
            char_len(&request.params.text),
            request.params.from.as_deref().unwrap_or("auto"),
            request.params.to.as_deref().unwrap_or("auto"),
            request
                .settings
                .request_timeout_ms
                .map(|value| value.to_string())
                .unwrap_or_else(|| "default".to_string())
        ),
    );
}

pub(crate) fn log_quick_translate_cache_hit(request: &QuickTranslateServiceRequest) {
    if !enabled() {
        return;
    }

    log(
        "network",
        format_args!(
            "quick_translate_cache_hit query_id={} service={} text_len={}",
            request.query_id,
            request.service.id,
            char_len(&request.params.text)
        ),
    );
}

pub(crate) fn log_quick_translate_finished(update: &QuickTranslateServiceUpdate) {
    if !enabled() {
        return;
    }

    let elapsed = take_quick_translate_start(update.query_id, &update.outcome.service.id);
    match &update.outcome.result {
        Ok(result) => {
            log(
                "network",
                format_args!(
                    "quick_translate_finish query_id={} service={} status=ok elapsed_ms={} result_len={} chunks={} grammar={}",
                    update.query_id,
                    update.outcome.service.id,
                    elapsed_ms_or_unknown(elapsed),
                    char_len(&result.translated_text),
                    update.outcome.streamed_chunks.len(),
                    update.outcome.grammar_result.is_some()
                ),
            );
        }
        Err(error) => {
            log(
                "network",
                format_args!(
                    "quick_translate_finish query_id={} service={} status=error elapsed_ms={} error_len={} chunks={} grammar={}",
                    update.query_id,
                    update.outcome.service.id,
                    elapsed_ms_or_unknown(elapsed),
                    char_len(&error.message),
                    update.outcome.streamed_chunks.len(),
                    update.outcome.grammar_result.is_some()
                ),
            );
        }
    }
}

pub(crate) fn log_quick_translate_stream_chunk(chunk: &QuickTranslateStreamChunk) {
    if !verbose_enabled() {
        return;
    }

    log(
        "network",
        format_args!(
            "quick_translate_stream_chunk query_id={} service={} chunk_len={}",
            chunk.query_id,
            chunk.service.id,
            char_len(&chunk.text)
        ),
    );
}

pub(crate) fn log_http_start(client: &str, method: &str, url: &str) -> Instant {
    let started = Instant::now();
    if !enabled() {
        return started;
    }

    log(
        "http",
        format_args!(
            "start client={client} method={method} url={}",
            safe_url_for_log(url)
        ),
    );
    started
}

pub(crate) fn log_http_finish(
    client: &str,
    method: &str,
    url: &str,
    status: u16,
    bytes: Option<usize>,
    started: Instant,
) {
    if !enabled() {
        return;
    }

    log(
        "http",
        format_args!(
            "finish client={client} method={method} url={} status={status} bytes={} elapsed_ms={}",
            safe_url_for_log(url),
            bytes
                .map(|value| value.to_string())
                .unwrap_or_else(|| "unknown".to_string()),
            started.elapsed().as_millis()
        ),
    );
}

pub(crate) fn log_http_reqwest_error(
    client: &str,
    method: &str,
    url: &str,
    stage: &str,
    error: &reqwest::Error,
    started: Instant,
) {
    if !enabled() {
        return;
    }

    log(
        "http",
        format_args!(
            "error client={client} method={method} url={} stage={stage} timeout={} connect={} request={} body={} elapsed_ms={}",
            safe_url_for_log(url),
            error.is_timeout(),
            error.is_connect(),
            error.is_request(),
            error.is_body(),
            started.elapsed().as_millis()
        ),
    );
}

pub(crate) fn log_http_io_error(
    client: &str,
    method: &str,
    url: &str,
    stage: &str,
    error: &std::io::Error,
    started: Instant,
) {
    if !enabled() {
        return;
    }

    log(
        "http",
        format_args!(
            "error client={client} method={method} url={} stage={stage} io_kind={:?} elapsed_ms={}",
            safe_url_for_log(url),
            error.kind(),
            started.elapsed().as_millis()
        ),
    );
}

pub(crate) fn log_http_pipeline_error(
    client: &str,
    method: &str,
    url: &str,
    stage: &str,
    started: Instant,
) {
    if !enabled() {
        return;
    }

    log(
        "http",
        format_args!(
            "error client={client} method={method} url={} stage={stage} elapsed_ms={}",
            safe_url_for_log(url),
            started.elapsed().as_millis()
        ),
    );
}

fn track_quick_translate_start(request: &QuickTranslateServiceRequest) {
    let starts = QUICK_TRANSLATE_STARTS.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(mut starts) = starts.lock() {
        starts.insert(
            (request.query_id, request.service.id.clone()),
            Instant::now(),
        );
    }
}

fn take_quick_translate_start(query_id: u64, service_id: &str) -> Option<Duration> {
    let starts = QUICK_TRANSLATE_STARTS.get_or_init(|| Mutex::new(HashMap::new()));
    starts
        .lock()
        .ok()
        .and_then(|mut starts| starts.remove(&(query_id, service_id.to_string())))
        .map(|started| started.elapsed())
}

fn elapsed_ms_or_unknown(elapsed: Option<Duration>) -> String {
    elapsed
        .map(|duration| duration.as_millis().to_string())
        .unwrap_or_else(|| "unknown".to_string())
}

fn env_value_enabled(value: Option<String>) -> bool {
    value
        .as_deref()
        .map(str::trim)
        .map(|value| {
            value.eq_ignore_ascii_case("1")
                || value.eq_ignore_ascii_case("true")
                || value.eq_ignore_ascii_case("yes")
                || value.eq_ignore_ascii_case("on")
        })
        .unwrap_or(false)
}

fn result_status<T, E>(result: &Result<T, E>) -> &'static str {
    if result.is_ok() {
        "ok"
    } else {
        "error"
    }
}

fn optional_text_len(value: Option<&str>) -> usize {
    value.map(char_len).unwrap_or(0)
}

fn char_len(value: &str) -> usize {
    value.chars().count()
}

fn stable_text_hash(value: &str) -> u64 {
    value
        .as_bytes()
        .iter()
        .fold(0xcbf29ce484222325, |hash, byte| {
            (hash ^ u64::from(*byte)).wrapping_mul(0x100000001b3)
        })
}

fn window_event_parts(event: &WindowEvent) -> (&'static str, &str) {
    match event {
        WindowEvent::Opened(id) => ("Opened", id.as_str()),
        WindowEvent::CloseRequested(id) => ("CloseRequested", id.as_str()),
        WindowEvent::Closed(id) => ("Closed", id.as_str()),
        WindowEvent::Focused(id) => ("Focused", id.as_str()),
        WindowEvent::Unfocused(id) => ("Unfocused", id.as_str()),
        WindowEvent::DpiChanged(id) => ("DpiChanged", id.as_str()),
    }
}

fn high_volume_message(message: &Message) -> bool {
    matches!(
        message,
        Message::SourceTextChanged(_)
            | Message::FloatingTextChanged(_)
            | Message::FloatingSurfaceTextChanged(_, _)
            | Message::LongDocumentSourceTextChanged(_)
            | Message::CaptureMouseMoved(_)
            | Message::CaptureWindowsChanged(_)
            | Message::CaptureSelectionChanged(_)
            | Message::MouseSelectionInputHookEvent(_)
            | Message::QuickTranslateStreamChunk(_)
    )
}

fn button_like_message(message: &Message) -> bool {
    matches!(
        message,
        Message::SourceTextSubmitted
            | Message::TestOcrConnection
            | Message::DownloadLayoutModel
            | Message::DeleteLayoutModel
            | Message::DownloadCjkFont
            | Message::DeleteCjkFont
            | Message::ClearTranslationCache
            | Message::TestOpenAI
            | Message::RefreshOllamaModels
            | Message::TestOllama
            | Message::PrepareLocalAiModel
            | Message::OpenWindowsAiUpdate
            | Message::StartFoundryLocal
            | Message::InstallFoundryLocal
            | Message::OpenFoundryLocalDocs
            | Message::DownloadOpenVinoModel
            | Message::TestServiceProvider(_)
            | Message::TestCaiyun
            | Message::TestNiuTrans
            | Message::TestYoudao
            | Message::TestVolcano
            | Message::ToggleResultExpanded(_)
            | Message::ToggleResultExpandedIn(_, _)
            | Message::InstallBrowserSupport
            | Message::UninstallBrowserSupport
            | Message::SwapLanguages
            | Message::SwapFloatingLanguages(_)
            | Message::QuickTranslate
            | Message::QuickTranslateIn(_)
            | Message::FocusLocalDictionarySuggestions
            | Message::MoveLocalDictionarySuggestion(_)
            | Message::CommitLocalDictionarySuggestion
            | Message::DismissLocalDictionarySuggestions
            | Message::ExitLocalDictionarySuggestions
            | Message::CaptureLeftButtonDown(_)
            | Message::CaptureLeftButtonUp(_)
            | Message::CaptureDoubleClick(_)
            | Message::CaptureRightButtonDown
            | Message::CaptureMouseWheel { .. }
            | Message::CaptureEscape
            | Message::Translate
            | Message::CopyResult
            | Message::CopyResultIn(_, _)
            | Message::ReplaceResult
            | Message::ReplaceResultIn(_, _)
            | Message::RetryResult
            | Message::RetryResultIn(_, _)
            | Message::SpeakResult
            | Message::SpeakResultIn(_, _)
            | Message::OpenSettings
            | Message::SettingsRuntimeLoadingDelayElapsed(_)
            | Message::Back
            | Message::SaveSettingsChanges
            | Message::DiscardSettingsChanges
            | Message::CancelSettingsChangesDialog
            | Message::DismissSettingsError
            | Message::OpenSettingsLink(_)
            | Message::MinimizeWindow
            | Message::ToggleMaximizeWindow
            | Message::DragWindow
            | Message::CloseMainWindow
            | Message::CloseWindow
            | Message::BrowseFile
            | Message::BrowseOutputFolder
            | Message::ImportMdxDictionary
            | Message::RescanMdxMddFiles(_)
            | Message::RequestDeleteMdxDictionary(_)
            | Message::ConfirmDeleteMdxDictionary
            | Message::CancelDeleteMdxDictionary
            | Message::ApplyLocalDictionarySuggestion(_)
            | Message::RetryLongDocument
            | Message::ClearHistory
            | Message::ConfirmCapture
            | Message::CancelCapture
            | Message::TranslateSelection
            | Message::DismissPopButton
            | Message::PopButtonClicked
            | Message::Noop
    )
}

fn message_variant_name(message: &Message) -> &'static str {
    match message {
        Message::ModeChanged(_) => "ModeChanged",
        Message::SourceTextChanged(_) => "SourceTextChanged",
        Message::SourceTextSubmitted => "SourceTextSubmitted",
        Message::FloatingTextChanged(_) => "FloatingTextChanged",
        Message::FloatingSurfaceTextChanged(_, _) => "FloatingSurfaceTextChanged",
        Message::LongDocumentSourceTextChanged(_) => "LongDocumentSourceTextChanged",
        Message::SourceLanguageChanged(_) => "SourceLanguageChanged",
        Message::TargetLanguageChanged(_) => "TargetLanguageChanged",
        Message::FloatingSourceLanguageChanged(_, _) => "FloatingSourceLanguageChanged",
        Message::FloatingTargetLanguageChanged(_, _) => "FloatingTargetLanguageChanged",
        Message::LongDocumentSourceLanguageChanged(_) => "LongDocumentSourceLanguageChanged",
        Message::LongDocumentTargetLanguageChanged(_) => "LongDocumentTargetLanguageChanged",
        Message::LongDocumentServiceChanged(_) => "LongDocumentServiceChanged",
        Message::LongDocumentInputModeChanged(_) => "LongDocumentInputModeChanged",
        Message::LongDocumentOutputModeChanged(_) => "LongDocumentOutputModeChanged",
        Message::LongDocumentConcurrencyChanged(_) => "LongDocumentConcurrencyChanged",
        Message::LongDocumentPageRangeChanged(_) => "LongDocumentPageRangeChanged",
        Message::LongDocumentFileDialogFinished(_) => "LongDocumentFileDialogFinished",
        Message::LongDocumentFileSelected(_) => "LongDocumentFileSelected",
        Message::LongDocumentOutputFolderDialogFinished(_) => {
            "LongDocumentOutputFolderDialogFinished"
        }
        Message::LongDocumentOutputFolderSelected(_) => "LongDocumentOutputFolderSelected",
        Message::MdxDictionaryDialogFinished(_) => "MdxDictionaryDialogFinished",
        Message::MdxDictionarySelected(_) => "MdxDictionarySelected",
        Message::SettingsSectionChanged(_) => "SettingsSectionChanged",
        Message::DesktopShellActionFinished(_) => "DesktopShellActionFinished",
        Message::DesktopIntegrationActionFinished(_) => "DesktopIntegrationActionFinished",
        Message::ThemeChanged(_) => "ThemeChanged",
        Message::SystemThemeChanged(_) => "SystemThemeChanged",
        Message::ToggleMinimizeToTray(_) => "ToggleMinimizeToTray",
        Message::ToggleStartMinimized(_) => "ToggleStartMinimized",
        Message::ToggleMonitorClipboard(_) => "ToggleMonitorClipboard",
        Message::ToggleMouseSelectionTranslate(_) => "ToggleMouseSelectionTranslate",
        Message::MouseSelectionExcludedAppsChanged(_) => "MouseSelectionExcludedAppsChanged",
        Message::ToggleLaunchAtStartup(_) => "ToggleLaunchAtStartup",
        Message::ToggleShellContextMenu(_) => "ToggleShellContextMenu",
        Message::ToggleInternationalServices(_) => "ToggleInternationalServices",
        Message::ToggleHideEmptyServiceResults(_) => "ToggleHideEmptyServiceResults",
        Message::TtsSpeedChanged(_) => "TtsSpeedChanged",
        Message::ToggleAutoPlayTranslation(_) => "ToggleAutoPlayTranslation",
        Message::OcrEngineChanged(_) => "OcrEngineChanged",
        Message::OcrApiKeyChanged(_) => "OcrApiKeyChanged",
        Message::OcrEndpointChanged(_) => "OcrEndpointChanged",
        Message::OcrModelChanged(_) => "OcrModelChanged",
        Message::OcrSystemPromptChanged(_) => "OcrSystemPromptChanged",
        Message::TestOcrConnection => "TestOcrConnection",
        Message::LayoutDetectionModeChanged(_) => "LayoutDetectionModeChanged",
        Message::VisionLayoutServiceChanged(_) => "VisionLayoutServiceChanged",
        Message::DownloadLayoutModel => "DownloadLayoutModel",
        Message::DeleteLayoutModel => "DeleteLayoutModel",
        Message::DownloadCjkFont => "DownloadCjkFont",
        Message::DeleteCjkFont => "DeleteCjkFont",
        Message::FormulaFontPatternChanged(_) => "FormulaFontPatternChanged",
        Message::FormulaCharPatternChanged(_) => "FormulaCharPatternChanged",
        Message::ToggleTranslationCache(_) => "ToggleTranslationCache",
        Message::ClearTranslationCache => "ClearTranslationCache",
        Message::CustomTranslationPromptChanged(_) => "CustomTranslationPromptChanged",
        Message::ToggleProxyEnabled(_) => "ToggleProxyEnabled",
        Message::ProxyUrlChanged(_) => "ProxyUrlChanged",
        Message::ToggleProxyBypassLocal(_) => "ToggleProxyBypassLocal",
        Message::DeepLApiKeyChanged(_) => "DeepLApiKeyChanged",
        Message::ToggleDeepLUseFreeApi(_) => "ToggleDeepLUseFreeApi",
        Message::ToggleDeepLUseQualityOptimized(_) => "ToggleDeepLUseQualityOptimized",
        Message::OpenAIApiKeyChanged(_) => "OpenAIApiKeyChanged",
        Message::OpenAIEndpointChanged(_) => "OpenAIEndpointChanged",
        Message::OpenAIModelChanged(_) => "OpenAIModelChanged",
        Message::OpenAIApiFormatChanged(_) => "OpenAIApiFormatChanged",
        Message::TestOpenAI => "TestOpenAI",
        Message::OllamaEndpointChanged(_) => "OllamaEndpointChanged",
        Message::OllamaModelChanged(_) => "OllamaModelChanged",
        Message::RefreshOllamaModels => "RefreshOllamaModels",
        Message::TestOllama => "TestOllama",
        Message::LocalAiProviderChanged(_) => "LocalAiProviderChanged",
        Message::PrepareLocalAiModel => "PrepareLocalAiModel",
        Message::OpenWindowsAiUpdate => "OpenWindowsAiUpdate",
        Message::FoundryLocalEndpointChanged(_) => "FoundryLocalEndpointChanged",
        Message::FoundryLocalModelChanged(_) => "FoundryLocalModelChanged",
        Message::StartFoundryLocal => "StartFoundryLocal",
        Message::FoundryLocalPrepareFinished(_) => "FoundryLocalPrepareFinished",
        Message::WindowsAiPrepareFinished(_) => "WindowsAiPrepareFinished",
        Message::InstallFoundryLocal => "InstallFoundryLocal",
        Message::OpenFoundryLocalDocs => "OpenFoundryLocalDocs",
        Message::OpenVinoDeviceChanged(_) => "OpenVinoDeviceChanged",
        Message::DownloadOpenVinoModel => "DownloadOpenVinoModel",
        Message::OpenVinoDownloadFinished(_) => "OpenVinoDownloadFinished",
        Message::ServiceProviderSettingChanged(_, _, _) => "ServiceProviderSettingChanged",
        Message::TestServiceProvider(_) => "TestServiceProvider",
        Message::ToggleServiceConfigurationExpanded(_, _) => "ToggleServiceConfigurationExpanded",
        Message::CaiyunApiKeyChanged(_) => "CaiyunApiKeyChanged",
        Message::TestCaiyun => "TestCaiyun",
        Message::NiuTransApiKeyChanged(_) => "NiuTransApiKeyChanged",
        Message::TestNiuTrans => "TestNiuTrans",
        Message::YoudaoAppKeyChanged(_) => "YoudaoAppKeyChanged",
        Message::YoudaoAppSecretChanged(_) => "YoudaoAppSecretChanged",
        Message::ToggleYoudaoUseOfficialApi(_) => "ToggleYoudaoUseOfficialApi",
        Message::TestYoudao => "TestYoudao",
        Message::VolcanoAccessKeyIdChanged(_) => "VolcanoAccessKeyIdChanged",
        Message::VolcanoSecretAccessKeyChanged(_) => "VolcanoSecretAccessKeyChanged",
        Message::TestVolcano => "TestVolcano",
        Message::ToggleLocalDictionarySuggestions(_) => "ToggleLocalDictionarySuggestions",
        Message::UiLanguageChanged(_) => "UiLanguageChanged",
        Message::OcrLanguageChanged(_) => "OcrLanguageChanged",
        Message::FirstLanguageChanged(_) => "FirstLanguageChanged",
        Message::SecondLanguageChanged(_) => "SecondLanguageChanged",
        Message::ToggleAutoSelectTargetLanguage(_) => "ToggleAutoSelectTargetLanguage",
        Message::ToggleSelectedLanguage(_, _) => "ToggleSelectedLanguage",
        Message::ToggleTranslationLanguagesExpanded(_) => "ToggleTranslationLanguagesExpanded",
        Message::ToggleHotkey(_, _) => "ToggleHotkey",
        Message::HotkeyShortcutChanged(_, _) => "HotkeyShortcutChanged",
        Message::ToggleMiniAutoClose(_) => "ToggleMiniAutoClose",
        Message::ToggleFixedAlwaysOnTop(_) => "ToggleFixedAlwaysOnTop",
        Message::ToggleWindowReorderMode(_) => "ToggleWindowReorderMode",
        Message::ToggleWindowService(_, _, _) => "ToggleWindowService",
        Message::ToggleWindowServiceQuery(_, _, _) => "ToggleWindowServiceQuery",
        Message::MoveWindowService(_, _, _) => "MoveWindowService",
        Message::ToggleTwoPassContext(_) => "ToggleTwoPassContext",
        Message::ToggleLongDocumentHistoryExpanded(_) => "ToggleLongDocumentHistoryExpanded",
        Message::TogglePin(_) => "TogglePin",
        Message::ToggleResultExpanded(_) => "ToggleResultExpanded",
        Message::ToggleResultExpandedIn(_, _) => "ToggleResultExpandedIn",
        Message::InstallBrowserSupport => "InstallBrowserSupport",
        Message::UninstallBrowserSupport => "UninstallBrowserSupport",
        Message::BrowserSupportActionFinished(_) => "BrowserSupportActionFinished",
        Message::BrowserSupportStatusLoaded(_) => "BrowserSupportStatusLoaded",
        Message::SwapLanguages => "SwapLanguages",
        Message::SwapFloatingLanguages(_) => "SwapFloatingLanguages",
        Message::QuickTranslate => "QuickTranslate",
        Message::QuickTranslateIn(_) => "QuickTranslateIn",
        Message::QuickTranslateFinished(_) => "QuickTranslateFinished",
        Message::QuickTranslateServiceFinished(_) => "QuickTranslateServiceFinished",
        Message::QuickTranslateStreamChunk(_) => "QuickTranslateStreamChunk",
        Message::LocalDictionarySuggestionsFinished(_) => "LocalDictionarySuggestionsFinished",
        Message::FocusLocalDictionarySuggestions => "FocusLocalDictionarySuggestions",
        Message::MoveLocalDictionarySuggestion(_) => "MoveLocalDictionarySuggestion",
        Message::CommitLocalDictionarySuggestion => "CommitLocalDictionarySuggestion",
        Message::DismissLocalDictionarySuggestions => "DismissLocalDictionarySuggestions",
        Message::ExitLocalDictionarySuggestions => "ExitLocalDictionarySuggestions",
        Message::LongDocumentFinished(_) => "LongDocumentFinished",
        Message::OcrCaptureFinished(_) => "OcrCaptureFinished",
        Message::SilentOcrCaptureFinished(_) => "SilentOcrCaptureFinished",
        Message::OcrCaptureFailed { .. } => "OcrCaptureFailed",
        Message::OcrCaptureCancelled(_) => "OcrCaptureCancelled",
        Message::OcrRecognizeFinished(_) => "OcrRecognizeFinished",
        Message::CaptureSelectionChanged(_) => "CaptureSelectionChanged",
        Message::CaptureWindowsSnapshotFinished(_) => "CaptureWindowsSnapshotFinished",
        Message::CaptureWindowsChanged(_) => "CaptureWindowsChanged",
        Message::CaptureMouseMoved(_) => "CaptureMouseMoved",
        Message::CaptureLeftButtonDown(_) => "CaptureLeftButtonDown",
        Message::CaptureLeftButtonUp(_) => "CaptureLeftButtonUp",
        Message::CaptureDoubleClick(_) => "CaptureDoubleClick",
        Message::CaptureRightButtonDown => "CaptureRightButtonDown",
        Message::CaptureMouseWheel { .. } => "CaptureMouseWheel",
        Message::CaptureEscape => "CaptureEscape",
        Message::HotkeyTriggered(_) => "HotkeyTriggered",
        Message::TrayCommand(_) => "TrayCommand",
        Message::WindowEvent(_) => "WindowEvent",
        Message::ClipboardTextReceived(_) => "ClipboardTextReceived",
        Message::TrayClipboardTextReceived(_) => "TrayClipboardTextReceived",
        Message::TrayClipboardReadFinished(_) => "TrayClipboardReadFinished",
        Message::ClipboardMonitorFailed(_) => "ClipboardMonitorFailed",
        Message::ClipboardMonitorRecovered => "ClipboardMonitorRecovered",
        Message::TextSelectionCaptureFinished(_) => "TextSelectionCaptureFinished",
        Message::TextInsertionFinished(_) => "TextInsertionFinished",
        Message::Translate => "Translate",
        Message::CopyResult => "CopyResult",
        Message::CopyResultIn(_, _) => "CopyResultIn",
        Message::ReplaceResult => "ReplaceResult",
        Message::ReplaceResultIn(_, _) => "ReplaceResultIn",
        Message::RetryResult => "RetryResult",
        Message::RetryResultIn(_, _) => "RetryResultIn",
        Message::SpeakResult => "SpeakResult",
        Message::SpeakResultIn(_, _) => "SpeakResultIn",
        Message::SpeakResultFinished(_) => "SpeakResultFinished",
        Message::ClipboardOperationFinished(_) => "ClipboardOperationFinished",
        Message::OpenSettings => "OpenSettings",
        Message::SettingsRuntimeLoadingDelayElapsed(_) => "SettingsRuntimeLoadingDelayElapsed",
        Message::SettingsRuntimeStatusLoaded(_, _) => "SettingsRuntimeStatusLoaded",
        Message::SettingsSaveFinished(_) => "SettingsSaveFinished",
        Message::BuiltInAiDeviceRegistrationFinished(_) => "BuiltInAiDeviceRegistrationFinished",
        Message::Back => "Back",
        Message::SaveSettingsChanges => "SaveSettingsChanges",
        Message::DiscardSettingsChanges => "DiscardSettingsChanges",
        Message::CancelSettingsChangesDialog => "CancelSettingsChangesDialog",
        Message::DismissSettingsError => "DismissSettingsError",
        Message::OpenSettingsLink(_) => "OpenSettingsLink",
        Message::MinimizeWindow => "MinimizeWindow",
        Message::ToggleMaximizeWindow => "ToggleMaximizeWindow",
        Message::DragWindow => "DragWindow",
        Message::CloseMainWindow => "CloseMainWindow",
        Message::CloseWindow => "CloseWindow",
        Message::BrowseFile => "BrowseFile",
        Message::BrowseOutputFolder => "BrowseOutputFolder",
        Message::ImportMdxDictionary => "ImportMdxDictionary",
        Message::MdxDictionaryEmailChanged(_, _) => "MdxDictionaryEmailChanged",
        Message::MdxDictionaryRegcodeChanged(_, _) => "MdxDictionaryRegcodeChanged",
        Message::RescanMdxMddFiles(_) => "RescanMdxMddFiles",
        Message::RequestDeleteMdxDictionary(_) => "RequestDeleteMdxDictionary",
        Message::ConfirmDeleteMdxDictionary => "ConfirmDeleteMdxDictionary",
        Message::CancelDeleteMdxDictionary => "CancelDeleteMdxDictionary",
        Message::ApplyLocalDictionarySuggestion(_) => "ApplyLocalDictionarySuggestion",
        Message::RetryLongDocument => "RetryLongDocument",
        Message::ClearHistory => "ClearHistory",
        Message::ConfirmCapture => "ConfirmCapture",
        Message::CancelCapture => "CancelCapture",
        Message::TranslateSelection => "TranslateSelection",
        Message::SelectionTextReady { .. } => "SelectionTextReady",
        Message::MouseSelectionInputHookEvent(_) => "MouseSelectionInputHookEvent",
        Message::MouseSelectionPendingMultiClickElapsed(_) => {
            "MouseSelectionPendingMultiClickElapsed"
        }
        Message::DismissPopButton => "DismissPopButton",
        Message::PopButtonAutoDismiss(_) => "PopButtonAutoDismiss",
        Message::PopButtonClicked => "PopButtonClicked",
        Message::PreviewScrollReady => "PreviewScrollReady",
        #[cfg(feature = "parity-diagnostics")]
        Message::PreviewControlSignaled => "PreviewControlSignaled",
        #[cfg(feature = "parity-diagnostics")]
        Message::PreviewControlArtifactsWritten(_) => "PreviewControlArtifactsWritten",
        #[cfg(feature = "parity-diagnostics")]
        Message::PreviewControlTimedOut(_) => "PreviewControlTimedOut",
        Message::Noop => "Noop",
    }
}

fn safe_url_for_log(input: &str) -> String {
    match reqwest::Url::parse(input) {
        Ok(url) => {
            let host = url.host_str().unwrap_or_default();
            let port = url
                .port()
                .map(|port| format!(":{port}"))
                .unwrap_or_default();
            format!("{}://{}{}{}", url.scheme(), host, port, url.path())
        }
        Err(_) => input
            .split(['?', '#'])
            .next()
            .unwrap_or_default()
            .to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn env_value_enabled_accepts_common_true_values() {
        for value in ["1", "true", "TRUE", "yes", "on"] {
            assert!(env_value_enabled(Some(value.to_string())));
        }
        for value in ["", "0", "false", "off", "no"] {
            assert!(!env_value_enabled(Some(value.to_string())));
        }
        assert!(!env_value_enabled(None));
    }

    #[test]
    fn stable_text_hash_tracks_character_order_without_logging_content() {
        assert_eq!(stable_text_hash("abc"), 0xe71fa2190541574b);
        assert_ne!(stable_text_hash("abc"), stable_text_hash("cba"));
    }

    #[test]
    fn safe_url_for_log_removes_query_fragment_and_userinfo() {
        assert_eq!(
            safe_url_for_log("https://user:secret@example.com:8443/v1/chat?api_key=secret#top"),
            "https://example.com:8443/v1/chat"
        );
        assert_eq!(
            safe_url_for_log("not-a-url?api_key=secret#frag"),
            "not-a-url"
        );
    }
}
