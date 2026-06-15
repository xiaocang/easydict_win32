use easydict_app::mouse_selection::EASYDICT_SYNTHETIC_KEY;
use easydict_app::text_selection::{
    build_text_selection_plan, capture_native_selected_text, clipboard_restore_required,
    finalize_text_selection_attempts, is_electron_process_name, is_terminal_process_name,
    normalize_process_name, selected_text_from_capture_result, synthetic_ctrl_c_input_plan,
    AttemptOutcome, ClipWaitResult, ClipWaitState, ClipboardProbe, ClipboardSelectionResult,
    TextSelectionAttempt, TextSelectionAttemptResult, TextSelectionBackend,
    TextSelectionBackendError, TextSelectionFinalOutcome, TextSelectionPlan,
    TextSelectionSuppressionTracker, TextSelectionTarget, ELECTRON_CLIPBOARD_TIMEOUT_MS,
    NON_TEXT_FAILURE_THRESHOLD, STANDARD_CLIPBOARD_TIMEOUT_MS, SUPPRESSION_WINDOW_MS, VK_C,
    VK_CONTROL, VK_LWIN, VK_MENU, VK_RWIN, VK_SHIFT,
};

#[cfg(windows)]
static TERMINAL_SMOKE_CTRL_EVENT_MARKER: std::sync::OnceLock<std::path::PathBuf> =
    std::sync::OnceLock::new();

#[test]
fn process_name_normalization_matches_dotnet_terminal_aliases() {
    assert_eq!(
        normalize_process_name(Some(" MobaXterm_Personal_26.2 ")),
        "mobaxterm_personal"
    );
    assert_eq!(normalize_process_name(Some("Xshell7")), "xshell");
    assert_eq!(normalize_process_name(Some("solar-putty")), "solar_putty");
    assert_eq!(
        normalize_process_name(Some("f-secure ssh client")),
        "f_secure_ssh_client"
    );
    assert_eq!(normalize_process_name(Some("ConEmu64")), "conemu");
    assert_eq!(normalize_process_name(Some("notepad")), "notepad");
    assert_eq!(normalize_process_name(Some("   ")), "");
    assert_eq!(normalize_process_name(None), "");
}

#[test]
fn terminal_detection_handles_versioned_and_normalized_names() {
    for process in [
        "mobaxterm",
        "MobaXterm_Personal_26.2",
        "Xshell7",
        "solar-putty",
        "f-secure ssh client",
        "WindowsTerminal",
    ] {
        assert!(
            is_terminal_process_name(Some(process)),
            "{process} should be classified as terminal"
        );
    }

    for process in ["notepad", "chrome", "Code"] {
        assert!(
            !is_terminal_process_name(Some(process)),
            "{process} should not be classified as terminal"
        );
    }
}

#[test]
fn powershell_terminal_names_are_classification_only_and_skip_clipboard_shortcuts() {
    let suppression = TextSelectionSuppressionTracker::new();
    let current_pid = 42;

    for process in ["powershell", "PowerShell", "pwsh", "PWSH"] {
        assert!(
            is_terminal_process_name(Some(process)),
            "{process} should be classified as a terminal process"
        );

        let target = TextSelectionTarget::new(Some(process), Some(100), current_pid);
        assert_eq!(
            build_text_selection_plan(&target, &suppression, 1_000),
            TextSelectionPlan::Attempts(vec![TextSelectionAttempt::Uia]),
            "{process} should use UIA-only terminal capture without clipboard Ctrl+C"
        );
    }
}

#[test]
fn electron_detection_uses_dotnet_exact_process_catalog() {
    assert!(is_electron_process_name(Some("Code")));
    assert!(is_electron_process_name(Some("code - insiders")));
    assert!(is_electron_process_name(Some("telegram desktop")));

    assert!(!is_electron_process_name(Some("code-insiders")));
    assert!(!is_electron_process_name(Some("chrome")));
    assert!(!is_electron_process_name(None));
}

#[test]
fn selection_plan_matches_dotnet_strategy_by_app_type() {
    let suppression = TextSelectionSuppressionTracker::new();
    let current_pid = 42;

    let electron = TextSelectionTarget::new(Some("Code"), Some(100), current_pid);
    assert_eq!(
        build_text_selection_plan(&electron, &suppression, 1_000),
        TextSelectionPlan::Attempts(vec![
            TextSelectionAttempt::Clipboard {
                timeout_ms: ELECTRON_CLIPBOARD_TIMEOUT_MS
            },
            TextSelectionAttempt::Uia,
        ])
    );

    let terminal =
        TextSelectionTarget::new(Some("MobaXterm_Personal_26.2"), Some(101), current_pid);
    assert_eq!(
        build_text_selection_plan(&terminal, &suppression, 1_000),
        TextSelectionPlan::Attempts(vec![TextSelectionAttempt::Uia])
    );

    let desktop = TextSelectionTarget::new(Some("notepad"), Some(102), current_pid);
    assert_eq!(
        build_text_selection_plan(&desktop, &suppression, 1_000),
        TextSelectionPlan::Attempts(vec![
            TextSelectionAttempt::Uia,
            TextSelectionAttempt::Clipboard {
                timeout_ms: STANDARD_CLIPBOARD_TIMEOUT_MS
            },
        ])
    );
}

#[test]
fn selection_plan_skips_own_process_before_attempting_capture() {
    let suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(Some("Easydict.Rust"), Some(42), 42);

    assert_eq!(
        build_text_selection_plan(&target, &suppression, 1_000),
        TextSelectionPlan::SkipOwnProcess
    );
}

#[test]
fn selection_plan_treats_unknown_process_like_regular_dotnet_app() {
    let suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(None, Some(100), 42);

    assert_eq!(
        build_text_selection_plan(&target, &suppression, 1_000),
        TextSelectionPlan::Attempts(vec![
            TextSelectionAttempt::Uia,
            TextSelectionAttempt::Clipboard {
                timeout_ms: STANDARD_CLIPBOARD_TIMEOUT_MS
            },
        ])
    );
}

#[test]
fn clip_wait_requires_two_consecutive_non_text_payload_ticks() {
    let mut state = ClipWaitState::new();

    assert_eq!(state.observe(Some(ClipboardProbe::non_text(true, 2))), None);
    assert_eq!(state.consecutive_non_text_ticks(), 1);
    assert_eq!(
        state.observe(Some(ClipboardProbe::non_text(true, 2))),
        Some(ClipWaitResult::NonTextPayload)
    );
}

#[test]
fn clip_wait_resets_non_text_counter_on_empty_or_unreadable_ticks() {
    let mut state = ClipWaitState::new();

    assert_eq!(state.observe(Some(ClipboardProbe::non_text(true, 1))), None);
    assert_eq!(state.consecutive_non_text_ticks(), 1);
    assert_eq!(state.observe(None), None);
    assert_eq!(state.consecutive_non_text_ticks(), 0);

    assert_eq!(state.observe(Some(ClipboardProbe::non_text(true, 1))), None);
    assert_eq!(
        state.observe(Some(ClipboardProbe::text(true, true))),
        None,
        "text format with blank text keeps polling like the .NET ClipWait path"
    );
    assert_eq!(state.consecutive_non_text_ticks(), 0);
}

#[test]
fn clip_wait_succeeds_on_non_blank_text() {
    let mut state = ClipWaitState::new();

    assert_eq!(
        state.observe(Some(ClipboardProbe::text(true, false))),
        Some(ClipWaitResult::Success)
    );
}

#[test]
fn suppression_tracks_confirmed_non_text_payloads_only() {
    let mut tracker = TextSelectionSuppressionTracker::new();
    let process = normalize_process_name(Some("potplayermini64"));
    let now = 1_000;

    tracker.record_outcome(&process, ClipWaitResult::NonTextPayload, now);
    assert!(!tracker.is_suppressed(&process, now + 1, false, false));

    tracker.record_outcome(&process, ClipWaitResult::Timeout, now + 2);
    assert!(!tracker.is_suppressed(&process, now + 3, false, false));

    tracker.record_outcome(&process, ClipWaitResult::NonTextPayload, now + 4);
    assert!(tracker.is_suppressed(&process, now + 5, false, false));
}

#[test]
fn suppression_expires_and_success_lifts_it() {
    let mut tracker = TextSelectionSuppressionTracker::new();
    let process = "potplayermini";
    let now = 10_000;

    for offset in 0..NON_TEXT_FAILURE_THRESHOLD {
        tracker.record_outcome(
            process,
            ClipWaitResult::NonTextPayload,
            now + i64::from(offset),
        );
    }
    assert!(tracker.is_suppressed(process, now + 10, false, false));
    assert!(!tracker.is_suppressed(process, now + SUPPRESSION_WINDOW_MS + 1, false, false));

    tracker.record_outcome(process, ClipWaitResult::Success, now + 20);
    assert!(!tracker.is_suppressed(process, now + 21, false, false));
}

#[test]
fn suppression_exempts_electron_and_terminal_apps() {
    let mut tracker = TextSelectionSuppressionTracker::new();
    let now = 1_000;

    tracker.record_outcome("code", ClipWaitResult::NonTextPayload, now);
    tracker.record_outcome("code", ClipWaitResult::NonTextPayload, now);
    assert!(!tracker.is_suppressed("code", now + 1, true, false));

    tracker.record_outcome("windowsterminal", ClipWaitResult::NonTextPayload, now);
    tracker.record_outcome("windowsterminal", ClipWaitResult::NonTextPayload, now);
    assert!(!tracker.is_suppressed("windowsterminal", now + 1, false, true));
}

#[test]
fn suppression_never_locks_unknown_process_names() {
    let mut tracker = TextSelectionSuppressionTracker::new();
    let process = normalize_process_name(None);
    let now = 1_000;

    tracker.record_outcome(&process, ClipWaitResult::NonTextPayload, now);
    tracker.record_outcome(&process, ClipWaitResult::NonTextPayload, now + 1);

    assert!(!tracker.is_suppressed(&process, now + 2, false, false));
}

#[test]
fn suppressed_regular_app_skips_selection_attempts() {
    let mut tracker = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(Some("PotPlayerMini64"), Some(100), 42);
    tracker.record_outcome(
        &target.normalized_process_name,
        ClipWaitResult::NonTextPayload,
        1_000,
    );
    tracker.record_outcome(
        &target.normalized_process_name,
        ClipWaitResult::NonTextPayload,
        1_001,
    );

    assert_eq!(
        build_text_selection_plan(&target, &tracker, 1_002),
        TextSelectionPlan::Suppressed {
            normalized_process_name: "potplayermini".to_string()
        }
    );
}

#[test]
fn clipboard_restore_rule_matches_dotnet_clipboard_path() {
    assert!(clipboard_restore_required(Some("old"), Some("new")));
    assert!(clipboard_restore_required(None, Some("copied")));
    assert!(!clipboard_restore_required(Some("same"), Some("same")));
    assert!(!clipboard_restore_required(Some("old"), None));
    assert!(!clipboard_restore_required(None, None));
}

#[test]
fn synthetic_ctrl_c_plan_flushes_modifiers_before_ctrl_c() {
    let plan = synthetic_ctrl_c_input_plan();
    let keys = plan
        .iter()
        .map(|input| (input.virtual_key, input.key_up))
        .collect::<Vec<_>>();

    assert_eq!(
        keys,
        vec![
            (VK_MENU, true),
            (VK_SHIFT, true),
            (VK_LWIN, true),
            (VK_RWIN, true),
            (VK_CONTROL, false),
            (VK_C, false),
            (VK_C, true),
            (VK_CONTROL, true),
        ]
    );
    assert!(plan
        .iter()
        .all(|input| input.extra_info == EASYDICT_SYNTHETIC_KEY));
}

#[test]
fn attempt_reducer_records_success_for_uia_or_clipboard_text() {
    assert_eq!(
        finalize_text_selection_attempts(&[TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Uia,
            outcome: AttemptOutcome::Success,
        }]),
        TextSelectionFinalOutcome::Selected {
            record_success: true
        }
    );

    assert_eq!(
        finalize_text_selection_attempts(&[TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Clipboard {
                timeout_ms: ELECTRON_CLIPBOARD_TIMEOUT_MS
            },
            outcome: AttemptOutcome::Success,
        }]),
        TextSelectionFinalOutcome::Selected {
            record_success: true
        }
    );
}

#[test]
fn attempt_reducer_keeps_electron_clipboard_outcome_without_second_ctrl_c() {
    let outcome = finalize_text_selection_attempts(&[
        TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Clipboard {
                timeout_ms: ELECTRON_CLIPBOARD_TIMEOUT_MS,
            },
            outcome: AttemptOutcome::NoText(ClipWaitResult::NonTextPayload),
        },
        TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Uia,
            outcome: AttemptOutcome::NoText(ClipWaitResult::Timeout),
        },
    ]);

    assert_eq!(
        outcome,
        TextSelectionFinalOutcome::NoSelection {
            record_clipboard_outcome: Some(ClipWaitResult::NonTextPayload)
        }
    );
}

#[test]
fn attempt_reducer_records_regular_clipboard_fallback_failure() {
    let outcome = finalize_text_selection_attempts(&[
        TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Uia,
            outcome: AttemptOutcome::NoText(ClipWaitResult::Timeout),
        },
        TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Clipboard {
                timeout_ms: STANDARD_CLIPBOARD_TIMEOUT_MS,
            },
            outcome: AttemptOutcome::NoText(ClipWaitResult::Timeout),
        },
    ]);

    assert_eq!(
        outcome,
        TextSelectionFinalOutcome::NoSelection {
            record_clipboard_outcome: Some(ClipWaitResult::Timeout)
        }
    );
}

#[test]
fn attempt_reducer_has_no_clipboard_outcome_for_terminal_uia_only_miss() {
    assert_eq!(
        finalize_text_selection_attempts(&[TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Uia,
            outcome: AttemptOutcome::NoText(ClipWaitResult::Timeout),
        }]),
        TextSelectionFinalOutcome::NoSelection {
            record_clipboard_outcome: None
        }
    );
}

#[test]
fn attempt_reducer_does_not_treat_backend_errors_as_clipboard_suppression_outcomes() {
    assert_eq!(
        finalize_text_selection_attempts(&[TextSelectionAttemptResult {
            attempt: TextSelectionAttempt::Clipboard {
                timeout_ms: STANDARD_CLIPBOARD_TIMEOUT_MS,
            },
            outcome: AttemptOutcome::BackendError("clipboard access denied".to_string()),
        }]),
        TextSelectionFinalOutcome::NoSelection {
            record_clipboard_outcome: None,
        }
    );
}

#[test]
fn capture_backend_uses_desktop_uia_then_clipboard_and_records_success() {
    let mut backend = FakeTextSelectionBackend::new()
        .with_uia(None)
        .with_clipboard(ClipboardSelectionResult {
            text: Some(" copied selection ".to_string()),
            outcome: ClipWaitResult::Success,
        });
    let mut suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(Some("notepad"), Some(100), 42);

    let result = easydict_app::text_selection::capture_text_selection_with_backend(
        &target,
        &mut suppression,
        1_000,
        &mut backend,
    );

    assert_eq!(result.text.as_deref(), Some(" copied selection "));
    assert_eq!(
        backend.calls,
        vec![
            BackendCall::Uia,
            BackendCall::Clipboard(STANDARD_CLIPBOARD_TIMEOUT_MS)
        ]
    );
    assert_eq!(
        result.attempts,
        vec![
            TextSelectionAttemptResult {
                attempt: TextSelectionAttempt::Uia,
                outcome: AttemptOutcome::NotAttempted,
            },
            TextSelectionAttemptResult {
                attempt: TextSelectionAttempt::Clipboard {
                    timeout_ms: STANDARD_CLIPBOARD_TIMEOUT_MS,
                },
                outcome: AttemptOutcome::Success,
            },
        ]
    );
    assert!(!suppression.is_suppressed("notepad", 1_001, false, false));
}

#[test]
fn capture_backend_preserves_uia_error_diagnostic_and_continues_to_clipboard() {
    let mut backend = FakeTextSelectionBackend::new()
        .with_uia_error("UIA provider unavailable")
        .with_clipboard(ClipboardSelectionResult {
            text: Some("clipboard fallback".to_string()),
            outcome: ClipWaitResult::Success,
        });
    let mut suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(Some("notepad"), Some(100), 42);

    let result = easydict_app::text_selection::capture_text_selection_with_backend(
        &target,
        &mut suppression,
        1_000,
        &mut backend,
    );

    assert_eq!(result.text.as_deref(), Some("clipboard fallback"));
    assert_eq!(
        selected_text_from_capture_result(&result)
            .expect("clipboard fallback should be selected")
            .as_deref(),
        Some("clipboard fallback")
    );
    assert_eq!(
        result.attempts,
        vec![
            TextSelectionAttemptResult {
                attempt: TextSelectionAttempt::Uia,
                outcome: AttemptOutcome::BackendError("UIA provider unavailable".to_string()),
            },
            TextSelectionAttemptResult {
                attempt: TextSelectionAttempt::Clipboard {
                    timeout_ms: STANDARD_CLIPBOARD_TIMEOUT_MS,
                },
                outcome: AttemptOutcome::Success,
            },
        ]
    );
    assert!(!suppression.is_suppressed("notepad", 1_001, false, false));
}

#[test]
fn capture_backend_preserves_clipboard_error_without_suppression() {
    let mut backend = FakeTextSelectionBackend::new()
        .with_uia(None)
        .with_clipboard_error("clipboard is locked");
    let mut suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(Some("notepad"), Some(100), 42);

    let result = easydict_app::text_selection::capture_text_selection_with_backend(
        &target,
        &mut suppression,
        1_000,
        &mut backend,
    );

    assert_eq!(result.text, None);
    let error = selected_text_from_capture_result(&result)
        .expect_err("clipboard backend error should surface when no attempt succeeds");
    assert_eq!(error.message, "clipboard is locked");
    assert_eq!(
        result.attempts,
        vec![
            TextSelectionAttemptResult {
                attempt: TextSelectionAttempt::Uia,
                outcome: AttemptOutcome::NotAttempted,
            },
            TextSelectionAttemptResult {
                attempt: TextSelectionAttempt::Clipboard {
                    timeout_ms: STANDARD_CLIPBOARD_TIMEOUT_MS,
                },
                outcome: AttemptOutcome::BackendError("clipboard is locked".to_string()),
            },
        ]
    );
    assert_eq!(
        result.final_outcome,
        TextSelectionFinalOutcome::NoSelection {
            record_clipboard_outcome: None,
        }
    );
    assert!(!suppression.is_suppressed("notepad", 1_001, false, false));
}

#[test]
fn capture_backend_treats_unknown_process_like_regular_dotnet_app() {
    let mut backend = FakeTextSelectionBackend::new()
        .with_uia(None)
        .with_clipboard(ClipboardSelectionResult {
            text: Some("fallback selection".to_string()),
            outcome: ClipWaitResult::Success,
        });
    let mut suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(None, Some(100), 42);

    let result = easydict_app::text_selection::capture_text_selection_with_backend(
        &target,
        &mut suppression,
        1_000,
        &mut backend,
    );

    assert_eq!(result.text.as_deref(), Some("fallback selection"));
    assert_eq!(
        backend.calls,
        vec![
            BackendCall::Uia,
            BackendCall::Clipboard(STANDARD_CLIPBOARD_TIMEOUT_MS)
        ]
    );
    assert!(!suppression.is_suppressed("", 1_001, false, false));
}

#[test]
fn capture_backend_uses_electron_clipboard_first_and_skips_uia_after_success() {
    let mut backend = FakeTextSelectionBackend::new().with_clipboard(ClipboardSelectionResult {
        text: Some("electron text".to_string()),
        outcome: ClipWaitResult::Success,
    });
    let mut suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(Some("Code"), Some(100), 42);

    let result = easydict_app::text_selection::capture_text_selection_with_backend(
        &target,
        &mut suppression,
        1_000,
        &mut backend,
    );

    assert_eq!(result.text.as_deref(), Some("electron text"));
    assert_eq!(
        backend.calls,
        vec![BackendCall::Clipboard(ELECTRON_CLIPBOARD_TIMEOUT_MS)]
    );
}

#[test]
fn capture_backend_keeps_terminal_uia_only_without_clipboard_fallback() {
    let mut backend = FakeTextSelectionBackend::new().with_uia(None);
    let mut suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(Some("WindowsTerminal"), Some(100), 42);

    let result = easydict_app::text_selection::capture_text_selection_with_backend(
        &target,
        &mut suppression,
        1_000,
        &mut backend,
    );

    assert_eq!(result.text, None);
    assert_eq!(backend.calls, vec![BackendCall::Uia]);
    assert_eq!(
        result.final_outcome,
        TextSelectionFinalOutcome::NoSelection {
            record_clipboard_outcome: None,
        }
    );
}

#[cfg(windows)]
#[test]
fn terminal_text_selection_does_not_send_ctrl_c_to_console_when_enabled() {
    if !windows_terminal_text_selection_smoke_enabled() {
        return;
    }

    let temp_dir = std::env::temp_dir().join(format!(
        "easydict-terminal-selection-smoke-{}-{:?}",
        std::process::id(),
        std::thread::current().id()
    ));
    std::fs::create_dir_all(&temp_dir).expect("terminal smoke temp dir");
    let helper_exe = temp_dir.join("WindowsTerminal.exe");
    std::fs::copy(
        std::env::current_exe().expect("current test exe"),
        &helper_exe,
    )
    .expect("copy terminal smoke helper");

    let ready_marker = temp_dir.join("ready.txt");
    let hwnd_marker = temp_dir.join("hwnd.txt");
    let ctrl_event_marker = temp_dir.join("ctrl-event.txt");
    let stop_marker = temp_dir.join("stop.txt");

    const CREATE_NEW_CONSOLE: u32 = 0x0000_0010;
    let mut command = std::process::Command::new(&helper_exe);
    command
        .arg("--exact")
        .arg("terminal_text_selection_console_helper_child")
        .arg("--ignored")
        .arg("--nocapture")
        .env("EASYDICT_WINDOWS_TERMINAL_TEXT_SELECTION_SMOKE_CHILD", "1")
        .env("EASYDICT_TERMINAL_SMOKE_READY", &ready_marker)
        .env("EASYDICT_TERMINAL_SMOKE_HWND", &hwnd_marker)
        .env("EASYDICT_TERMINAL_SMOKE_CTRL_EVENT", &ctrl_event_marker)
        .env("EASYDICT_TERMINAL_SMOKE_STOP", &stop_marker);

    use std::os::windows::process::CommandExt;
    command.creation_flags(CREATE_NEW_CONSOLE);
    let child = command.spawn().expect("terminal smoke helper should start");
    let mut helper = TerminalSmokeChild::new(child, stop_marker, temp_dir);

    assert!(
        wait_for_file(&ready_marker, std::time::Duration::from_secs(5)),
        "terminal smoke helper should become ready"
    );
    let hwnd = wait_for_hwnd_marker(&hwnd_marker, std::time::Duration::from_secs(5))
        .expect("terminal smoke helper should publish console HWND");
    focus_terminal_smoke_window(hwnd);

    let selected = capture_native_selected_text();
    assert!(
        selected.is_none(),
        "terminal smoke does not need selected text, but must not use clipboard fallback"
    );
    std::thread::sleep(std::time::Duration::from_millis(300));

    assert!(
        !ctrl_event_marker.exists(),
        "terminal smoke helper should not receive Ctrl+C/SIGINT"
    );
    assert!(
        helper.is_running(),
        "terminal smoke helper should still be running after capture"
    );

    helper.shutdown();
}

#[cfg(windows)]
#[ignore = "spawned as a helper by terminal_text_selection_does_not_send_ctrl_c_to_console_when_enabled"]
#[test]
fn terminal_text_selection_console_helper_child() {
    if std::env::var("EASYDICT_WINDOWS_TERMINAL_TEXT_SELECTION_SMOKE_CHILD").as_deref() != Ok("1") {
        return;
    }

    let ready_marker = std::path::PathBuf::from(
        std::env::var_os("EASYDICT_TERMINAL_SMOKE_READY").expect("ready marker env"),
    );
    let hwnd_marker = std::path::PathBuf::from(
        std::env::var_os("EASYDICT_TERMINAL_SMOKE_HWND").expect("hwnd marker env"),
    );
    let ctrl_event_marker = std::path::PathBuf::from(
        std::env::var_os("EASYDICT_TERMINAL_SMOKE_CTRL_EVENT").expect("ctrl marker env"),
    );
    let stop_marker = std::path::PathBuf::from(
        std::env::var_os("EASYDICT_TERMINAL_SMOKE_STOP").expect("stop marker env"),
    );

    let _ = TERMINAL_SMOKE_CTRL_EVENT_MARKER.set(ctrl_event_marker);
    unsafe {
        windows::Win32::System::Console::SetConsoleCtrlHandler(
            Some(terminal_smoke_ctrl_handler),
            true,
        )
        .expect("terminal smoke Ctrl handler should install");
    }

    let hwnd = unsafe { windows::Win32::System::Console::GetConsoleWindow() };
    std::fs::write(&hwnd_marker, format!("{}", hwnd.0 as isize)).expect("write HWND marker");
    std::fs::write(&ready_marker, b"ready").expect("write ready marker");

    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
    while std::time::Instant::now() < deadline {
        if stop_marker.exists() {
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    }
}

#[test]
fn capture_backend_records_non_text_clipboard_suppression_for_regular_apps() {
    let mut suppression = TextSelectionSuppressionTracker::new();
    let target = TextSelectionTarget::new(Some("PotPlayerMini64"), Some(100), 42);

    for tick in 1..=NON_TEXT_FAILURE_THRESHOLD {
        let mut backend = FakeTextSelectionBackend::new()
            .with_uia(None)
            .with_clipboard(ClipboardSelectionResult {
                text: None,
                outcome: ClipWaitResult::NonTextPayload,
            });

        let result = easydict_app::text_selection::capture_text_selection_with_backend(
            &target,
            &mut suppression,
            tick as i64,
            &mut backend,
        );

        assert_eq!(result.text, None);
    }

    assert!(suppression.is_suppressed(
        "potplayermini",
        NON_TEXT_FAILURE_THRESHOLD as i64 + 1,
        false,
        false
    ));
}

#[cfg(windows)]
fn windows_terminal_text_selection_smoke_enabled() -> bool {
    std::env::var("EASYDICT_WINDOWS_TERMINAL_TEXT_SELECTION_SMOKE")
        .map(|value| {
            matches!(
                value.trim().to_ascii_lowercase().as_str(),
                "1" | "true" | "yes" | "on"
            )
        })
        .unwrap_or(false)
}

#[cfg(windows)]
fn wait_for_file(path: &std::path::Path, timeout: std::time::Duration) -> bool {
    let deadline = std::time::Instant::now() + timeout;
    while std::time::Instant::now() < deadline {
        if path.exists() {
            return true;
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    }
    false
}

#[cfg(windows)]
fn wait_for_hwnd_marker(path: &std::path::Path, timeout: std::time::Duration) -> Option<isize> {
    let deadline = std::time::Instant::now() + timeout;
    while std::time::Instant::now() < deadline {
        if let Ok(value) = std::fs::read_to_string(path) {
            if let Ok(hwnd) = value.trim().parse::<isize>() {
                if hwnd != 0 {
                    return Some(hwnd);
                }
            }
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    }
    None
}

#[cfg(windows)]
fn focus_terminal_smoke_window(hwnd: isize) {
    use windows::Win32::Foundation::HWND;
    use windows::Win32::UI::WindowsAndMessaging::{
        BringWindowToTop, SetForegroundWindow, ShowWindow, SW_RESTORE,
    };

    let hwnd = HWND(hwnd as *mut std::ffi::c_void);
    unsafe {
        let _ = ShowWindow(hwnd, SW_RESTORE);
        let _ = BringWindowToTop(hwnd);
        let _ = SetForegroundWindow(hwnd);
    }
    std::thread::sleep(std::time::Duration::from_millis(300));
}

#[cfg(windows)]
unsafe extern "system" fn terminal_smoke_ctrl_handler(ctrl_type: u32) -> windows::core::BOOL {
    if ctrl_type == windows::Win32::System::Console::CTRL_C_EVENT {
        if let Some(path) = TERMINAL_SMOKE_CTRL_EVENT_MARKER.get() {
            let _ = std::fs::write(path, b"ctrl-c");
        }
        return windows::core::BOOL(1);
    }

    windows::core::BOOL(0)
}

#[cfg(windows)]
struct TerminalSmokeChild {
    child: std::process::Child,
    stop_marker: std::path::PathBuf,
    temp_dir: std::path::PathBuf,
    shutdown: bool,
}

#[cfg(windows)]
impl TerminalSmokeChild {
    fn new(
        child: std::process::Child,
        stop_marker: std::path::PathBuf,
        temp_dir: std::path::PathBuf,
    ) -> Self {
        Self {
            child,
            stop_marker,
            temp_dir,
            shutdown: false,
        }
    }

    fn is_running(&mut self) -> bool {
        matches!(self.child.try_wait(), Ok(None))
    }

    fn shutdown(&mut self) {
        if self.shutdown {
            return;
        }

        let _ = std::fs::write(&self.stop_marker, b"stop");
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(3);
        while std::time::Instant::now() < deadline {
            if !self.is_running() {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
        if self.is_running() {
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
        let _ = std::fs::remove_dir_all(&self.temp_dir);
        self.shutdown = true;
    }
}

#[cfg(windows)]
impl Drop for TerminalSmokeChild {
    fn drop(&mut self) {
        self.shutdown();
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum BackendCall {
    Uia,
    Clipboard(u64),
}

#[derive(Default)]
struct FakeTextSelectionBackend {
    calls: Vec<BackendCall>,
    uia_results: Vec<Result<Option<String>, TextSelectionBackendError>>,
    clipboard_results: Vec<Result<ClipboardSelectionResult, TextSelectionBackendError>>,
}

impl FakeTextSelectionBackend {
    fn new() -> Self {
        Self::default()
    }

    fn with_uia(mut self, text: Option<&str>) -> Self {
        self.uia_results
            .push(Ok(text.map(std::string::ToString::to_string)));
        self
    }

    fn with_uia_error(mut self, message: &str) -> Self {
        self.uia_results
            .push(Err(TextSelectionBackendError::new(message)));
        self
    }

    fn with_clipboard(mut self, result: ClipboardSelectionResult) -> Self {
        self.clipboard_results.push(Ok(result));
        self
    }

    fn with_clipboard_error(mut self, message: &str) -> Self {
        self.clipboard_results
            .push(Err(TextSelectionBackendError::new(message)));
        self
    }
}

impl TextSelectionBackend for FakeTextSelectionBackend {
    fn selected_text_via_uia(&mut self) -> Result<Option<String>, TextSelectionBackendError> {
        self.calls.push(BackendCall::Uia);
        self.uia_results.remove(0)
    }

    fn selected_text_via_clipboard(
        &mut self,
        timeout_ms: u64,
    ) -> Result<ClipboardSelectionResult, TextSelectionBackendError> {
        self.calls.push(BackendCall::Clipboard(timeout_ms));
        self.clipboard_results.remove(0)
    }
}
