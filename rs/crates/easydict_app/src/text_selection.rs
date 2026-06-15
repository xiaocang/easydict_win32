use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use crate::mouse_selection::EASYDICT_SYNTHETIC_KEY;

pub const CLIPBOARD_POLL_INTERVAL_MS: u64 = 30;
pub const STANDARD_CLIPBOARD_TIMEOUT_MS: u64 = 450;
pub const ELECTRON_CLIPBOARD_TIMEOUT_MS: u64 = 1_200;
pub const TRANSLATE_SELECTION_HOTKEY_DELAY_MS: u64 = 150;
pub const NON_TEXT_FAILURE_THRESHOLD: u32 = 2;
pub const SUPPRESSION_WINDOW_MS: i64 = 5 * 60 * 1_000;

pub const VK_CONTROL: u16 = 0x11;
pub const VK_C: u16 = 0x43;
pub const VK_SHIFT: u16 = 0x10;
pub const VK_MENU: u16 = 0x12;
pub const VK_LWIN: u16 = 0x5B;
pub const VK_RWIN: u16 = 0x5C;

const ELECTRON_PROCESS_NAMES: &[&str] = &[
    "code",
    "code - insiders",
    "slack",
    "discord",
    "teams",
    "notion",
    "obsidian",
    "postman",
    "figma",
    "spotify",
    "whatsapp",
    "signal",
    "telegram desktop",
];

const TERMINAL_PROCESS_NAMES: &[&str] = &[
    "windowsterminal",
    "cmd",
    "powershell",
    "pwsh",
    "conhost",
    "mintty",
    "alacritty",
    "wezterm",
    "hyper",
    "terminus",
    "wsl",
    "wslhost",
    "mobaxterm",
    "mobaxterm_personal",
    "putty",
    "kitty",
    "solar_putty",
    "xshell",
    "xshell_rc",
    "securecrt",
    "tabby",
    "conemu",
    "conemu64",
    "cmder",
    "fluentterminal",
    "termius",
    "bitvise",
    "bvssh",
    "mremoteng",
    "rlogin",
    "poderosa",
    "teraterm",
    "ttermpro",
    "smartty",
    "f_secure_ssh_client",
];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ClipWaitResult {
    Timeout,
    Success,
    NonTextPayload,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextSelectionAttempt {
    Uia,
    Clipboard { timeout_ms: u64 },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum TextSelectionPlan {
    SkipOwnProcess,
    Suppressed { normalized_process_name: String },
    Attempts(Vec<TextSelectionAttempt>),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextSelectionTarget {
    pub raw_process_name: Option<String>,
    pub normalized_process_name: String,
    pub is_electron: bool,
    pub is_terminal: bool,
    pub is_own_process: bool,
}

impl TextSelectionTarget {
    pub fn new(
        process_name: Option<&str>,
        process_id: Option<u32>,
        current_process_id: u32,
    ) -> Self {
        let normalized_process_name = normalize_process_name(process_name);
        Self {
            raw_process_name: process_name.map(str::to_string),
            is_electron: is_electron_process_name(process_name),
            is_terminal: is_terminal_process_name(process_name),
            is_own_process: process_id == Some(current_process_id),
            normalized_process_name,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ClipboardProbe {
    pub sequence_changed: bool,
    pub has_text: bool,
    pub text_is_blank: bool,
    pub available_format_count: u32,
}

impl ClipboardProbe {
    pub const fn text(sequence_changed: bool, text_is_blank: bool) -> Self {
        Self {
            sequence_changed,
            has_text: true,
            text_is_blank,
            available_format_count: 1,
        }
    }

    pub const fn non_text(sequence_changed: bool, available_format_count: u32) -> Self {
        Self {
            sequence_changed,
            has_text: false,
            text_is_blank: true,
            available_format_count,
        }
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ClipWaitState {
    consecutive_non_text_ticks: u32,
}

impl ClipWaitState {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn consecutive_non_text_ticks(&self) -> u32 {
        self.consecutive_non_text_ticks
    }

    pub fn observe(&mut self, probe: Option<ClipboardProbe>) -> Option<ClipWaitResult> {
        let Some(probe) = probe.filter(|probe| probe.sequence_changed) else {
            self.consecutive_non_text_ticks = 0;
            return None;
        };

        if probe.has_text && !probe.text_is_blank {
            self.consecutive_non_text_ticks = 0;
            return Some(ClipWaitResult::Success);
        }

        if !probe.has_text && probe.available_format_count > 0 {
            self.consecutive_non_text_ticks = self.consecutive_non_text_ticks.saturating_add(1);
            if self.consecutive_non_text_ticks >= NON_TEXT_FAILURE_THRESHOLD {
                return Some(ClipWaitResult::NonTextPayload);
            }
            return None;
        }

        self.consecutive_non_text_ticks = 0;
        None
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ProcessSelectionStats {
    pub consecutive_non_text_failures: u32,
    pub suppressed_until_ticks: i64,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct TextSelectionSuppressionTracker {
    process_stats: HashMap<String, ProcessSelectionStats>,
}

impl TextSelectionSuppressionTracker {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn record_outcome(
        &mut self,
        normalized_process_name: &str,
        outcome: ClipWaitResult,
        now_ticks: i64,
    ) {
        if normalized_process_name.is_empty() {
            return;
        }

        match outcome {
            ClipWaitResult::Success => {
                if let Some(stats) = self.process_stats.get_mut(normalized_process_name) {
                    stats.consecutive_non_text_failures = 0;
                    stats.suppressed_until_ticks = 0;
                }
            }
            ClipWaitResult::NonTextPayload => {
                let stats = self
                    .process_stats
                    .entry(normalized_process_name.to_string())
                    .or_insert(ProcessSelectionStats {
                        consecutive_non_text_failures: 0,
                        suppressed_until_ticks: 0,
                    });
                stats.consecutive_non_text_failures =
                    stats.consecutive_non_text_failures.saturating_add(1);
                if stats.consecutive_non_text_failures >= NON_TEXT_FAILURE_THRESHOLD {
                    stats.suppressed_until_ticks = now_ticks.saturating_add(SUPPRESSION_WINDOW_MS);
                }
            }
            ClipWaitResult::Timeout => {}
        }
    }

    pub fn is_suppressed(
        &self,
        normalized_process_name: &str,
        now_ticks: i64,
        is_electron: bool,
        is_terminal: bool,
    ) -> bool {
        if normalized_process_name.is_empty() || is_electron || is_terminal {
            return false;
        }

        self.process_stats
            .get(normalized_process_name)
            .is_some_and(|stats| stats.suppressed_until_ticks > now_ticks)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SyntheticKeyInput {
    pub virtual_key: u16,
    pub key_up: bool,
    pub extra_info: isize,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AttemptOutcome {
    NotAttempted,
    Success,
    NoText(ClipWaitResult),
    BackendError(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextSelectionAttemptResult {
    pub attempt: TextSelectionAttempt,
    pub outcome: AttemptOutcome,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextSelectionFinalOutcome {
    Selected {
        record_success: bool,
    },
    NoSelection {
        record_clipboard_outcome: Option<ClipWaitResult>,
    },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ClipboardSelectionResult {
    pub text: Option<String>,
    pub outcome: ClipWaitResult,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextSelectionCaptureResult {
    pub text: Option<String>,
    pub plan: TextSelectionPlan,
    pub attempts: Vec<TextSelectionAttemptResult>,
    pub final_outcome: TextSelectionFinalOutcome,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextSelectionBackendError {
    pub message: String,
}

impl TextSelectionBackendError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl std::fmt::Display for TextSelectionBackendError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for TextSelectionBackendError {}

pub trait TextSelectionBackend {
    fn selected_text_via_uia(&mut self) -> Result<Option<String>, TextSelectionBackendError>;

    fn selected_text_via_clipboard(
        &mut self,
        timeout_ms: u64,
    ) -> Result<ClipboardSelectionResult, TextSelectionBackendError>;
}

#[derive(Debug, Default)]
pub struct NativeTextSelectionBackend;

impl NativeTextSelectionBackend {
    pub fn new() -> Self {
        Self
    }
}

impl TextSelectionBackend for NativeTextSelectionBackend {
    fn selected_text_via_uia(&mut self) -> Result<Option<String>, TextSelectionBackendError> {
        easydict_windows_text_selection::selected_text_via_uia_with_timeout(
            easydict_windows_text_selection::DEFAULT_UIA_EXECUTION_TIMEOUT_MS,
        )
        .map_err(|error| TextSelectionBackendError::new(error.to_string()))
    }

    fn selected_text_via_clipboard(
        &mut self,
        timeout_ms: u64,
    ) -> Result<ClipboardSelectionResult, TextSelectionBackendError> {
        native_selected_text_via_clipboard(timeout_ms)
            .map_err(|error| TextSelectionBackendError::new(error.to_string()))
    }
}

static NATIVE_SUPPRESSION_TRACKER: OnceLock<Mutex<TextSelectionSuppressionTracker>> =
    OnceLock::new();

pub fn normalize_process_name(process_name: Option<&str>) -> String {
    let Some(process_name) = process_name else {
        return String::new();
    };
    let mut normalized = process_name.trim().to_ascii_lowercase();
    if normalized.is_empty() {
        return String::new();
    }

    normalized = normalized.replace(['-', ' '], "_");
    normalized = trim_trailing_version_suffix(&normalized);
    normalized = trim_trailing_numeric_suffix_after_non_digit(&normalized);
    normalized.trim_matches(['_', '.']).to_string()
}

pub fn is_electron_process_name(process_name: Option<&str>) -> bool {
    let Some(process_name) = process_name else {
        return false;
    };
    ELECTRON_PROCESS_NAMES
        .iter()
        .any(|known| known.eq_ignore_ascii_case(process_name))
}

pub fn is_terminal_process_name(process_name: Option<&str>) -> bool {
    let normalized = normalize_process_name(process_name);
    !normalized.is_empty()
        && TERMINAL_PROCESS_NAMES
            .iter()
            .any(|known| *known == normalized)
}

pub fn build_text_selection_plan(
    target: &TextSelectionTarget,
    suppression: &TextSelectionSuppressionTracker,
    now_ticks: i64,
) -> TextSelectionPlan {
    if target.is_own_process {
        return TextSelectionPlan::SkipOwnProcess;
    }

    if suppression.is_suppressed(
        &target.normalized_process_name,
        now_ticks,
        target.is_electron,
        target.is_terminal,
    ) {
        return TextSelectionPlan::Suppressed {
            normalized_process_name: target.normalized_process_name.clone(),
        };
    }

    if target.is_electron {
        return TextSelectionPlan::Attempts(vec![
            TextSelectionAttempt::Clipboard {
                timeout_ms: ELECTRON_CLIPBOARD_TIMEOUT_MS,
            },
            TextSelectionAttempt::Uia,
        ]);
    }

    if target.is_terminal {
        return TextSelectionPlan::Attempts(vec![TextSelectionAttempt::Uia]);
    }

    TextSelectionPlan::Attempts(vec![
        TextSelectionAttempt::Uia,
        TextSelectionAttempt::Clipboard {
            timeout_ms: STANDARD_CLIPBOARD_TIMEOUT_MS,
        },
    ])
}

pub fn capture_text_selection_with_backend<B: TextSelectionBackend>(
    target: &TextSelectionTarget,
    suppression: &mut TextSelectionSuppressionTracker,
    now_ticks: i64,
    backend: &mut B,
) -> TextSelectionCaptureResult {
    let plan = build_text_selection_plan(target, suppression, now_ticks);
    let TextSelectionPlan::Attempts(attempts) = &plan else {
        return TextSelectionCaptureResult {
            text: None,
            plan,
            attempts: Vec::new(),
            final_outcome: TextSelectionFinalOutcome::NoSelection {
                record_clipboard_outcome: None,
            },
        };
    };

    let mut attempt_results = Vec::new();
    let mut selected_text = None;

    for attempt in attempts {
        let outcome = match attempt {
            TextSelectionAttempt::Uia => match backend.selected_text_via_uia() {
                Ok(Some(text)) if !text.trim().is_empty() => {
                    selected_text = Some(text);
                    AttemptOutcome::Success
                }
                Ok(_) => AttemptOutcome::NotAttempted,
                Err(error) => AttemptOutcome::BackendError(error.to_string()),
            },
            TextSelectionAttempt::Clipboard { timeout_ms } => {
                match backend.selected_text_via_clipboard(*timeout_ms) {
                    Ok(result) => {
                        if result.outcome == ClipWaitResult::Success {
                            selected_text = result.text;
                            AttemptOutcome::Success
                        } else {
                            AttemptOutcome::NoText(result.outcome)
                        }
                    }
                    Err(error) => AttemptOutcome::BackendError(error.to_string()),
                }
            }
        };

        let success = matches!(outcome, AttemptOutcome::Success);
        attempt_results.push(TextSelectionAttemptResult {
            attempt: *attempt,
            outcome,
        });

        if success {
            break;
        }
    }

    let final_outcome = finalize_text_selection_attempts(&attempt_results);
    match final_outcome {
        TextSelectionFinalOutcome::Selected {
            record_success: true,
        } => suppression.record_outcome(
            &target.normalized_process_name,
            ClipWaitResult::Success,
            now_ticks,
        ),
        TextSelectionFinalOutcome::NoSelection {
            record_clipboard_outcome: Some(outcome),
        } => suppression.record_outcome(&target.normalized_process_name, outcome, now_ticks),
        _ => {}
    }

    TextSelectionCaptureResult {
        text: selected_text,
        plan,
        attempts: attempt_results,
        final_outcome,
    }
}

pub fn capture_native_selected_text_after_hotkey_delay_result(
) -> Result<Option<String>, TextSelectionBackendError> {
    thread::sleep(Duration::from_millis(TRANSLATE_SELECTION_HOTKEY_DELAY_MS));
    capture_native_selected_text_result()
}

pub fn capture_native_selected_text_result() -> Result<Option<String>, TextSelectionBackendError> {
    let Some(foreground) = easydict_windows_text_selection::foreground_text_selection_target().ok()
    else {
        return Ok(None);
    };
    let process_name = easydict_windows_text_selection::process_name_for_id(foreground.process_id)
        .ok()
        .flatten();
    let target = TextSelectionTarget::new(
        process_name.as_deref(),
        Some(foreground.process_id),
        std::process::id(),
    );
    let now_ticks = now_millis_since_unix_epoch();
    let mut backend = NativeTextSelectionBackend::new();
    let tracker = NATIVE_SUPPRESSION_TRACKER
        .get_or_init(|| Mutex::new(TextSelectionSuppressionTracker::new()));
    let mut suppression = tracker
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());

    let result =
        capture_text_selection_with_backend(&target, &mut suppression, now_ticks, &mut backend);
    selected_text_from_capture_result(&result)
}

pub fn capture_native_selected_text_after_hotkey_delay() -> Option<String> {
    match capture_native_selected_text_after_hotkey_delay_result() {
        Ok(text) => text,
        Err(_) => None,
    }
}

pub fn capture_native_selected_text() -> Option<String> {
    match capture_native_selected_text_result() {
        Ok(text) => text,
        Err(_) => None,
    }
}

pub fn selected_text_from_capture_result(
    result: &TextSelectionCaptureResult,
) -> Result<Option<String>, TextSelectionBackendError> {
    if let Some(text) = result.text.as_ref().filter(|text| !text.trim().is_empty()) {
        return Ok(Some(text.clone()));
    }

    if let Some(message) = first_backend_error_message(&result.attempts) {
        return Err(TextSelectionBackendError::new(message));
    }

    Ok(None)
}

fn first_backend_error_message(attempts: &[TextSelectionAttemptResult]) -> Option<&str> {
    attempts.iter().find_map(|result| match &result.outcome {
        AttemptOutcome::BackendError(message) => Some(message.as_str()),
        _ => None,
    })
}

pub fn clipboard_restore_required(
    original_clipboard_text: Option<&str>,
    selected_text: Option<&str>,
) -> bool {
    match (original_clipboard_text, selected_text) {
        (Some(original), Some(selected)) => original != selected,
        (Some(_), None) => false,
        (None, Some(_)) => true,
        (None, None) => false,
    }
}

fn native_selected_text_via_clipboard(
    timeout_ms: u64,
) -> Result<ClipboardSelectionResult, easydict_windows_text_selection::WindowsTextSelectionError> {
    let target = easydict_windows_text_selection::foreground_text_selection_target()?;
    let original_clipboard = easydict_windows_text_selection::clipboard_text_snapshot().ok();
    let baseline_sequence = original_clipboard
        .as_ref()
        .map(|snapshot| snapshot.sequence_number)
        .unwrap_or_else(easydict_windows_text_selection::clipboard_sequence_number);

    easydict_windows_text_selection::focus_window_and_send_ctrl_c(
        target.hwnd,
        EASYDICT_SYNTHETIC_KEY,
    )?;

    let deadline = Instant::now() + Duration::from_millis(timeout_ms);
    let mut state = ClipWaitState::new();
    let mut selected_text = None;
    let mut outcome = ClipWaitResult::Timeout;

    while Instant::now() < deadline {
        thread::sleep(Duration::from_millis(CLIPBOARD_POLL_INTERVAL_MS));
        let snapshot = easydict_windows_text_selection::clipboard_text_snapshot().ok();
        let probe = snapshot.as_ref().map(|snapshot| ClipboardProbe {
            sequence_changed: snapshot.sequence_number != baseline_sequence,
            has_text: snapshot.text.is_some(),
            text_is_blank: snapshot
                .text
                .as_deref()
                .is_none_or(|text| text.trim().is_empty()),
            available_format_count: snapshot.available_format_count,
        });

        if let Some(result) = state.observe(probe) {
            outcome = result;
            if result == ClipWaitResult::Success {
                selected_text = snapshot.and_then(|snapshot| snapshot.text);
            }
            break;
        }
    }

    restore_clipboard_after_selection(original_clipboard.as_ref(), selected_text.as_deref())?;

    Ok(ClipboardSelectionResult {
        text: selected_text,
        outcome,
    })
}

fn restore_clipboard_after_selection(
    original_clipboard: Option<&easydict_windows_text_selection::ClipboardTextSnapshot>,
    selected_text: Option<&str>,
) -> Result<(), easydict_windows_text_selection::WindowsTextSelectionError> {
    let original_text = original_clipboard.and_then(|snapshot| snapshot.text.as_deref());
    if !clipboard_restore_required(original_text, selected_text) {
        return Ok(());
    }

    if let Some(original_text) = original_text {
        easydict_windows_text_selection::set_clipboard_text(original_text)
    } else {
        easydict_windows_text_selection::clear_clipboard()
    }
}

fn now_millis_since_unix_epoch() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| i64::try_from(duration.as_millis()).unwrap_or(i64::MAX))
        .unwrap_or_default()
}

pub fn synthetic_ctrl_c_input_plan() -> Vec<SyntheticKeyInput> {
    vec![
        SyntheticKeyInput {
            virtual_key: VK_MENU,
            key_up: true,
            extra_info: EASYDICT_SYNTHETIC_KEY,
        },
        SyntheticKeyInput {
            virtual_key: VK_SHIFT,
            key_up: true,
            extra_info: EASYDICT_SYNTHETIC_KEY,
        },
        SyntheticKeyInput {
            virtual_key: VK_LWIN,
            key_up: true,
            extra_info: EASYDICT_SYNTHETIC_KEY,
        },
        SyntheticKeyInput {
            virtual_key: VK_RWIN,
            key_up: true,
            extra_info: EASYDICT_SYNTHETIC_KEY,
        },
        SyntheticKeyInput {
            virtual_key: VK_CONTROL,
            key_up: false,
            extra_info: EASYDICT_SYNTHETIC_KEY,
        },
        SyntheticKeyInput {
            virtual_key: VK_C,
            key_up: false,
            extra_info: EASYDICT_SYNTHETIC_KEY,
        },
        SyntheticKeyInput {
            virtual_key: VK_C,
            key_up: true,
            extra_info: EASYDICT_SYNTHETIC_KEY,
        },
        SyntheticKeyInput {
            virtual_key: VK_CONTROL,
            key_up: true,
            extra_info: EASYDICT_SYNTHETIC_KEY,
        },
    ]
}

pub fn finalize_text_selection_attempts(
    attempt_results: &[TextSelectionAttemptResult],
) -> TextSelectionFinalOutcome {
    for result in attempt_results {
        if result.outcome == AttemptOutcome::Success {
            return TextSelectionFinalOutcome::Selected {
                record_success: true,
            };
        }
    }

    let first_clipboard_outcome = attempt_results.iter().find_map(|result| match result {
        TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Clipboard { .. },
            outcome: AttemptOutcome::NoText(outcome),
        } => Some(*outcome),
        _ => None,
    });

    TextSelectionFinalOutcome::NoSelection {
        record_clipboard_outcome: first_clipboard_outcome,
    }
}

fn trim_trailing_version_suffix(value: &str) -> String {
    let mut end = value.len();
    while let Some((separator_start, digits_start)) = trailing_version_suffix_bounds(&value[..end])
    {
        if !value[digits_start..end]
            .chars()
            .any(|ch| ch.is_ascii_digit())
        {
            break;
        }
        end = separator_start;
    }
    value[..end].to_string()
}

fn trailing_version_suffix_bounds(value: &str) -> Option<(usize, usize)> {
    let bytes = value.as_bytes();
    let mut index = bytes.len();
    if index == 0 {
        return None;
    }

    while index > 0 && (bytes[index - 1].is_ascii_digit() || bytes[index - 1] == b'.') {
        index -= 1;
    }
    if index < bytes.len() && index > 0 && bytes[index - 1] == b'v' {
        index -= 1;
    }
    if index == 0 || !matches!(bytes[index - 1], b'.' | b'_' | b'-' | b' ') {
        return None;
    }

    let separator_start = index - 1;
    let digits_start = index;
    Some((separator_start, digits_start))
}

fn trim_trailing_numeric_suffix_after_non_digit(value: &str) -> String {
    let mut split = value.len();
    while split > 0 && value.as_bytes()[split - 1].is_ascii_digit() {
        split -= 1;
    }

    if split < value.len() && split > 0 && !value.as_bytes()[split - 1].is_ascii_digit() {
        value[..split].to_string()
    } else {
        value.to_string()
    }
}
