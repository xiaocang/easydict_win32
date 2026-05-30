use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use iced::widget::operation as widget_operation;
use iced::{window as iced_window, Element, Point, Subscription, Task};
use win_fluent::a11y::{A11yNode, A11yRole};
use win_fluent::platform::{Hotkey, HotkeyKey, HotkeyModifier};
use win_fluent::prelude::*;
use win_fluent_backend_iced::{IcedAdapter, IcedHotkeyEvent, IcedTextEditorContent};

const MINI_INPUT_ID: &str = "mini.input";
const CHINESE_PROBE_TEXT: &str = "中文输入";
const EVIDENCE_SCHEMA_VERSION: u32 = 1;

pub fn main() -> iced::Result {
    let mode = DaemonMode::from_args();

    iced::daemon(move || DaemonState::new(mode.clone()), update, view)
        .title("win_fluent Mini Daemon")
        .subscription(subscription)
        .run()
}

#[derive(Clone, Debug)]
enum DaemonMessage {
    StartResidentProbe,
    WindowOpened(iced_window::Id),
    WindowEvent(iced_window::Id, iced_window::Event),
    InputChanged(String),
    Translate,
    StreamChunk {
        generation: u64,
        text: String,
        done: bool,
    },
    HotkeyPressed(String),
    HotkeyError(String),
    SendHotkeyProbe,
    HotkeyProbeSent(Result<(), String>),
    ProbeTimedOut,
    TextInputSent(Result<TextInputProbeResult, String>),
    CheckProbeInputArrived {
        method: TextInputMethod,
    },
    CaptureBeforeStream,
    BeforeStreamScreenshot(VisualFrame),
    CaptureAfterStream,
    AfterStreamScreenshot(VisualFrame),
    CheckWindowMode {
        phase: ModeCheckPhase,
    },
    WindowModeChecked {
        phase: ModeCheckPhase,
        mode: iced_window::Mode,
    },
    WindowRawId(u64),
    CheckEditorFocused {
        phase: ModeCheckPhase,
    },
    EditorFocusedChecked {
        phase: ModeCheckPhase,
        focused: bool,
    },
    MemoryMeasured {
        phase: &'static str,
        result: Result<MemoryFrame, String>,
    },
    Exit,
    ExitWithCode(i32),
}

struct DaemonState {
    input: String,
    editor: IcedTextEditorContent,
    result: String,
    view: View<DaemonMessage>,
    evidence: EvidenceRecorder,
    a11y_evidence: A11yEvidence,
    window_id: Option<iced_window::Id>,
    window_raw_id: Option<u64>,
    window_visible: bool,
    window_has_focus: bool,
    focus_event_seen: bool,
    generation: u64,
    streaming: bool,
    hotkey_enabled: bool,
    hotkey_count: u64,
    stream_delay_ms: u64,
    input_method: TextInputMethod,
    resident_probe_exit: bool,
    probe_step: ProbeStep,
    chinese_input_sent: bool,
    chinese_input_seen: bool,
    clipboard_touched: bool,
    clipboard_restored: Option<bool>,
    visual_before: Option<VisualFrame>,
    visual_after: Option<VisualFrame>,
    visual_diff: Option<VisualDiff>,
    visual_smoke_seen: bool,
    memory_frames: Vec<MemoryMeasurement>,
    memory_smoke_seen: bool,
    a11y_smoke_seen: bool,
    hide_seen: bool,
    restore_seen: bool,
}

impl DaemonState {
    fn new(mode: DaemonMode) -> (Self, Task<DaemonMessage>) {
        let input = String::new();
        let result = String::new();
        let editor = IcedTextEditorContent::with_text(&input);
        let view = build_view(&input, &result, false);
        let evidence = EvidenceRecorder::new(mode.evidence_dir.clone(), mode.resident_probe_exit);
        evidence.record_event(
            "probe_config",
            &[
                ("resident_probe_exit", json_bool(mode.resident_probe_exit)),
                ("hotkey_enabled", json_bool(mode.hotkey_enabled)),
                ("input_method", json_str(mode.input_method.as_str())),
                ("stream_delay_ms", mode.stream_delay_ms.to_string()),
            ],
        );
        let a11y_evidence = accessibility_smoke(&view);
        print_accessibility_smoke(&a11y_evidence);
        evidence.record_event(
            "a11y_token_tree",
            &[
                ("root", json_str(a11y_evidence.root.as_str())),
                (
                    "name",
                    json_str(a11y_evidence.name.as_deref().unwrap_or("")),
                ),
                ("text_inputs", a11y_evidence.text_inputs.to_string()),
                ("buttons", a11y_evidence.buttons.to_string()),
                ("lists", a11y_evidence.lists.to_string()),
                ("ok", json_bool(a11y_evidence.ok)),
            ],
        );
        let a11y_smoke_seen = a11y_evidence.ok;

        let mut tasks = vec![memory_task("resident_no_window")];
        if mode.resident_probe_exit && !evidence.is_ready() {
            tasks.push(Task::done(DaemonMessage::ExitWithCode(11)));
        }
        if mode.resident_probe_exit {
            tasks.push(delayed_message_task(300, DaemonMessage::StartResidentProbe));
            tasks.push(delayed_message_task(45_000, DaemonMessage::ProbeTimedOut));
        }

        (
            Self {
                input,
                editor,
                result,
                view,
                evidence,
                a11y_evidence,
                window_id: None,
                window_raw_id: None,
                window_visible: false,
                window_has_focus: false,
                focus_event_seen: false,
                generation: 0,
                streaming: false,
                hotkey_enabled: mode.hotkey_enabled,
                hotkey_count: 0,
                stream_delay_ms: mode.stream_delay_ms,
                input_method: mode.input_method,
                resident_probe_exit: mode.resident_probe_exit,
                probe_step: if mode.resident_probe_exit {
                    ProbeStep::WaitingStartup
                } else {
                    ProbeStep::Manual
                },
                chinese_input_sent: false,
                chinese_input_seen: false,
                clipboard_touched: false,
                clipboard_restored: None,
                visual_before: None,
                visual_after: None,
                visual_diff: None,
                visual_smoke_seen: false,
                memory_frames: Vec::new(),
                memory_smoke_seen: false,
                a11y_smoke_seen,
                hide_seen: false,
                restore_seen: false,
            },
            Task::batch(tasks),
        )
    }

    fn rebuild_view(&mut self) {
        self.view = build_view(&self.input, &self.result, self.streaming);
    }

    fn open_mini_window(&mut self) -> Task<DaemonMessage> {
        let options = mini_window_options();
        let (settings, placement_report) = mini_window_settings(&options);
        let (window_id, open) = iced_window::open(settings);
        self.window_id = Some(window_id);
        self.window_visible = true;
        self.window_has_focus = false;
        println!("HOTKEY_ACTION action=show_open id={window_id:?} {placement_report}");
        self.evidence.record_event(
            "hotkey_action",
            &[
                ("action", json_str("show_open")),
                ("window_id", json_str(&format!("{window_id:?}"))),
                ("placement", json_str(&placement_report)),
            ],
        );

        Task::batch(vec![
            open.map(DaemonMessage::WindowOpened),
            memory_task("mini_open_requested"),
        ])
    }

    fn hide_mini_window(&mut self) -> Task<DaemonMessage> {
        let Some(window_id) = self.window_id else {
            return Task::none();
        };

        self.window_visible = false;
        self.window_has_focus = false;
        println!("HOTKEY_ACTION action=hide id={window_id:?}");
        self.evidence.record_event(
            "hotkey_action",
            &[
                ("action", json_str("hide")),
                ("window_id", json_str(&format!("{window_id:?}"))),
            ],
        );

        Task::batch(vec![
            iced_window::set_mode::<DaemonMessage>(window_id, iced_window::Mode::Hidden),
            delayed_message_task(
                300,
                DaemonMessage::CheckWindowMode {
                    phase: ModeCheckPhase::Hidden,
                },
            ),
        ])
    }

    fn restore_and_focus_mini_window(&mut self) -> Task<DaemonMessage> {
        let Some(window_id) = self.window_id else {
            return self.open_mini_window();
        };

        self.window_visible = true;
        println!("HOTKEY_ACTION action=restore_focus id={window_id:?}");
        self.evidence.record_event(
            "hotkey_action",
            &[
                ("action", json_str("restore_focus")),
                ("window_id", json_str(&format!("{window_id:?}"))),
            ],
        );
        show_focus_tasks(window_id, ModeCheckPhase::Restored)
    }

    fn start_translation(&mut self) -> Task<DaemonMessage> {
        self.generation = self.generation.wrapping_add(1);
        self.result.clear();
        self.streaming = true;
        self.rebuild_view();
        stream_translation_task(self.generation, self.input.clone(), self.stream_delay_ms)
    }

    fn handle_probe_completion(&mut self) -> Option<Task<DaemonMessage>> {
        if self.resident_probe_exit
            && self.chinese_input_seen
            && self.visual_smoke_seen
            && self.memory_smoke_seen
            && self.a11y_smoke_seen
            && self.hide_seen
            && self.restore_seen
        {
            println!(
                "DAEMON_SMOKE_DONE hotkeys={} chinese_input={} visual={} memory={} a11y={} hide={} restore={} focus_event={}",
                self.hotkey_count,
                self.chinese_input_seen,
                self.visual_smoke_seen,
                self.memory_smoke_seen,
                self.a11y_smoke_seen,
                self.hide_seen,
                self.restore_seen,
                self.focus_event_seen
            );
            self.evidence.record_event(
                "daemon_smoke_done",
                &[
                    ("hotkeys", self.hotkey_count.to_string()),
                    ("chinese_input", json_bool(self.chinese_input_seen)),
                    ("visual", json_bool(self.visual_smoke_seen)),
                    ("memory", json_bool(self.memory_smoke_seen)),
                    ("a11y", json_bool(self.a11y_smoke_seen)),
                    ("hide", json_bool(self.hide_seen)),
                    ("restore", json_bool(self.restore_seen)),
                    ("focus_event", json_bool(self.focus_event_seen)),
                ],
            );
            return Some(Task::done(DaemonMessage::Exit));
        }

        None
    }

    fn write_evidence_manifest(&self, status: &str, exit_code: i32) {
        self.evidence.write_manifest(self, status, exit_code);
    }
}

fn update(state: &mut DaemonState, message: DaemonMessage) -> Task<DaemonMessage> {
    match message {
        DaemonMessage::StartResidentProbe => {
            println!(
                "DAEMON_STARTED default_window=None no_default_window=true source=iced_daemon"
            );
            state.evidence.record_event(
                "daemon_started",
                &[
                    ("default_window", json_null()),
                    ("no_default_window", json_bool(true)),
                    ("source", json_str("iced_daemon")),
                ],
            );
            state.probe_step = ProbeStep::Opening;
            delayed_message_task(300, DaemonMessage::SendHotkeyProbe)
        }
        DaemonMessage::WindowOpened(window_id) => {
            state.window_id = Some(window_id);
            state.window_visible = true;
            println!("WINDOW_OPENED id={window_id:?}");
            state.evidence.record_event(
                "window_opened",
                &[("window_id", json_str(&format!("{window_id:?}")))],
            );
            Task::batch(vec![
                iced_window::raw_id::<DaemonMessage>(window_id).map(DaemonMessage::WindowRawId),
                show_focus_tasks(window_id, ModeCheckPhase::Opened),
            ])
        }
        DaemonMessage::WindowEvent(window_id, event) => {
            if Some(window_id) == state.window_id {
                match event {
                    iced_window::Event::Focused => {
                        state.window_has_focus = true;
                        state.focus_event_seen = true;
                        println!("WINDOW_FOCUSED id={window_id:?}");
                        state.evidence.record_event(
                            "window_focused",
                            &[("window_id", json_str(&format!("{window_id:?}")))],
                        );
                    }
                    iced_window::Event::Unfocused => {
                        state.window_has_focus = false;
                    }
                    iced_window::Event::Closed => {
                        state.window_id = None;
                        state.window_visible = false;
                        state.window_has_focus = false;
                        println!("WINDOW_CLOSED id={window_id:?}");
                        state.evidence.record_event(
                            "window_closed",
                            &[("window_id", json_str(&format!("{window_id:?}")))],
                        );
                    }
                    _ => {}
                }
            }

            Task::none()
        }
        DaemonMessage::InputChanged(value) => {
            let observed_input = value;
            state.input = if state.resident_probe_exit && !state.chinese_input_seen {
                merge_probe_input(&state.input, &observed_input)
            } else {
                observed_input.clone()
            };
            state.editor = IcedTextEditorContent::with_text(&state.input);
            state.result.clear();
            state.streaming = false;
            state.rebuild_view();

            if state.resident_probe_exit && !state.chinese_input_seen {
                state.evidence.record_event(
                    "ime_input_observed",
                    &[
                        ("raw_text", json_str(&observed_input)),
                        ("accumulated_text", json_str(&state.input)),
                        ("exact", json_bool(state.input == CHINESE_PROBE_TEXT)),
                    ],
                );
            }

            if state.resident_probe_exit
                && !state.chinese_input_seen
                && state.input == CHINESE_PROBE_TEXT
            {
                state.chinese_input_seen = true;
                state.probe_step = ProbeStep::Streaming;
                println!("IME_INPUT_DONE text={:?}", state.input);
                state.evidence.record_event(
                    "ime_input_done",
                    &[
                        ("text", json_str(&state.input)),
                        ("method", json_str(state.input_method.as_str())),
                        ("clipboard_touched", json_bool(state.clipboard_touched)),
                        (
                            "clipboard_restored",
                            json_option_bool(state.clipboard_restored),
                        ),
                    ],
                );
                return Task::batch(vec![
                    Task::done(DaemonMessage::CaptureBeforeStream),
                    delayed_message_task(100, DaemonMessage::Translate),
                ]);
            }

            Task::none()
        }
        DaemonMessage::Translate => state.start_translation(),
        DaemonMessage::StreamChunk {
            generation,
            text,
            done,
        } => {
            if generation == state.generation {
                state.result.push_str(&text);
                state.streaming = !done;
                state.rebuild_view();

                if done {
                    println!(
                        "STREAM_DONE generation={} bytes={} text={:?}",
                        state.generation,
                        state.result.len(),
                        state.result
                    );
                    state.evidence.record_event(
                        "stream_done",
                        &[
                            ("generation", state.generation.to_string()),
                            ("bytes", state.result.len().to_string()),
                            ("text", json_str(&state.result)),
                        ],
                    );

                    if state.resident_probe_exit {
                        return Task::done(DaemonMessage::CaptureAfterStream);
                    }
                }
            }

            Task::none()
        }
        DaemonMessage::HotkeyPressed(id) => {
            state.hotkey_count = state.hotkey_count.wrapping_add(1);
            println!("HOTKEY_TRIGGERED id={id} count={}", state.hotkey_count);
            state.evidence.record_event(
                "hotkey_triggered",
                &[
                    ("id", json_str(&id)),
                    ("count", state.hotkey_count.to_string()),
                    ("step", json_str(&format!("{:?}", state.probe_step))),
                ],
            );

            match state.probe_step {
                ProbeStep::Opening => state.open_mini_window(),
                ProbeStep::Hiding => state.hide_mini_window(),
                ProbeStep::Restoring => state.restore_and_focus_mini_window(),
                _ if state.window_id.is_none() => state.open_mini_window(),
                _ if state.window_visible && state.window_has_focus => state.hide_mini_window(),
                _ => state.restore_and_focus_mini_window(),
            }
        }
        DaemonMessage::HotkeyError(error) => {
            println!("HOTKEY_ERROR error={error:?}");
            state
                .evidence
                .record_event("hotkey_error", &[("error", json_str(&error))]);
            if state.resident_probe_exit {
                Task::done(DaemonMessage::ExitWithCode(2))
            } else {
                Task::none()
            }
        }
        DaemonMessage::SendHotkeyProbe => send_hotkey_probe_task(),
        DaemonMessage::HotkeyProbeSent(result) => match result {
            Ok(()) => {
                println!("HOTKEY_PROBE_SENT step={:?}", state.probe_step);
                state.evidence.record_event(
                    "hotkey_probe_sent",
                    &[("step", json_str(&format!("{:?}", state.probe_step)))],
                );
                Task::none()
            }
            Err(error) => {
                println!("HOTKEY_PROBE_SEND_FAILED error={error:?}");
                state
                    .evidence
                    .record_event("hotkey_probe_send_failed", &[("error", json_str(&error))]);
                if state.resident_probe_exit {
                    Task::done(DaemonMessage::ExitWithCode(2))
                } else {
                    Task::none()
                }
            }
        },
        DaemonMessage::ProbeTimedOut => {
            if state.resident_probe_exit && state.probe_step != ProbeStep::Done {
                println!(
                    "DAEMON_SMOKE_TIMEOUT step={:?} hotkeys={} chinese_input={} visual={} memory={} a11y={} hide={} restore={}",
                    state.probe_step,
                    state.hotkey_count,
                    state.chinese_input_seen,
                    state.visual_smoke_seen,
                    state.memory_smoke_seen,
                    state.a11y_smoke_seen,
                    state.hide_seen,
                    state.restore_seen
                );
                state.evidence.record_event(
                    "daemon_smoke_timeout",
                    &[
                        ("step", json_str(&format!("{:?}", state.probe_step))),
                        ("hotkeys", state.hotkey_count.to_string()),
                        ("chinese_input", json_bool(state.chinese_input_seen)),
                        ("visual", json_bool(state.visual_smoke_seen)),
                        ("memory", json_bool(state.memory_smoke_seen)),
                        ("a11y", json_bool(state.a11y_smoke_seen)),
                        ("hide", json_bool(state.hide_seen)),
                        ("restore", json_bool(state.restore_seen)),
                    ],
                );
                Task::done(DaemonMessage::ExitWithCode(3))
            } else {
                Task::none()
            }
        }
        DaemonMessage::TextInputSent(result) => match result {
            Ok(probe) => {
                state.input_method = probe.method;
                state.clipboard_touched = probe.clipboard_touched;
                state.clipboard_restored = probe.clipboard_restored;
                println!(
                    "IME_INPUT_SENT method={} clipboard_touched={} clipboard_restored={:?} text={CHINESE_PROBE_TEXT:?}",
                    probe.method.as_str(),
                    probe.clipboard_touched,
                    probe.clipboard_restored
                );
                state.evidence.record_event(
                    "ime_input_sent",
                    &[
                        ("method", json_str(probe.method.as_str())),
                        ("text", json_str(CHINESE_PROBE_TEXT)),
                        ("clipboard_touched", json_bool(probe.clipboard_touched)),
                        (
                            "clipboard_restored",
                            json_option_bool(probe.clipboard_restored),
                        ),
                    ],
                );
                if state.resident_probe_exit && probe.method == TextInputMethod::UnicodeSendInput {
                    delayed_message_task(
                        1_200,
                        DaemonMessage::CheckProbeInputArrived {
                            method: probe.method,
                        },
                    )
                } else {
                    Task::none()
                }
            }
            Err(error) => {
                println!("IME_INPUT_SEND_FAILED error={error:?}");
                state.evidence.record_event(
                    "ime_input_send_failed",
                    &[
                        ("method", json_str(state.input_method.as_str())),
                        ("error", json_str(&error)),
                    ],
                );
                if state.resident_probe_exit {
                    Task::done(DaemonMessage::ExitWithCode(6))
                } else {
                    Task::none()
                }
            }
        },
        DaemonMessage::CheckProbeInputArrived { method } => {
            if state.resident_probe_exit
                && !state.chinese_input_seen
                && method == TextInputMethod::UnicodeSendInput
            {
                println!(
                    "IME_INPUT_FALLBACK from=unicode_sendinput to=clipboard_paste reason=no_input_changed"
                );
                state.evidence.record_event(
                    "ime_input_fallback",
                    &[
                        ("from", json_str("unicode_sendinput")),
                        ("to", json_str("clipboard_paste")),
                        ("reason", json_str("no_input_changed")),
                    ],
                );
                state.input_method = TextInputMethod::ClipboardPaste;
                return Task::batch(vec![
                    focus_editor_task(),
                    send_probe_text_input_task(CHINESE_PROBE_TEXT, TextInputMethod::ClipboardPaste),
                ]);
            }

            Task::none()
        }
        DaemonMessage::CaptureBeforeStream => {
            if let Some(window_id) = state.window_id {
                IcedAdapter::window_screenshot(window_id)
                    .map(VisualFrame::from_window_screenshot)
                    .map(DaemonMessage::BeforeStreamScreenshot)
            } else {
                Task::none()
            }
        }
        DaemonMessage::BeforeStreamScreenshot(frame) => {
            println!(
                "VISUAL_BEFORE width={} height={} dpi={} scale={} dips={}x{} checksum={}",
                frame.width,
                frame.height,
                frame.dpi,
                frame.scale_factor,
                frame.width_dips,
                frame.height_dips,
                frame.checksum
            );
            let artifact = state.evidence.write_visual_frame("visual_before", &frame);
            state.evidence.record_event(
                "visual_before",
                &[
                    ("width", frame.width.to_string()),
                    ("height", frame.height.to_string()),
                    ("dpi", frame.dpi.to_string()),
                    ("scale_factor", frame.scale_factor.to_string()),
                    ("width_dips", frame.width_dips.to_string()),
                    ("height_dips", frame.height_dips.to_string()),
                    ("checksum", frame.checksum.to_string()),
                    ("artifact", json_option_string(artifact.as_deref())),
                ],
            );
            state.visual_before = Some(frame);
            Task::none()
        }
        DaemonMessage::CaptureAfterStream => {
            if let Some(window_id) = state.window_id {
                IcedAdapter::window_screenshot(window_id)
                    .map(VisualFrame::from_window_screenshot)
                    .map(DaemonMessage::AfterStreamScreenshot)
            } else {
                Task::none()
            }
        }
        DaemonMessage::AfterStreamScreenshot(after) => {
            if let Some(before) = &state.visual_before {
                let diff = before.diff(&after);
                state.visual_smoke_seen = diff.changed_pixels > 0;
                state.visual_diff = Some(diff);
                println!(
                    "VISUAL_SMOKE width={} height={} dpi={} scale={} dips={}x{} changed_pixels={} total_delta={} before_checksum={} after_checksum={}",
                    after.width,
                    after.height,
                    after.dpi,
                    after.scale_factor,
                    after.width_dips,
                    after.height_dips,
                    diff.changed_pixels,
                    diff.total_delta,
                    before.checksum,
                    after.checksum
                );
                let artifact = state.evidence.write_visual_frame("visual_after", &after);
                state.evidence.record_event(
                    "visual_smoke",
                    &[
                        ("width", after.width.to_string()),
                        ("height", after.height.to_string()),
                        ("dpi", after.dpi.to_string()),
                        ("scale_factor", after.scale_factor.to_string()),
                        ("width_dips", after.width_dips.to_string()),
                        ("height_dips", after.height_dips.to_string()),
                        ("changed_pixels", diff.changed_pixels.to_string()),
                        ("total_delta", diff.total_delta.to_string()),
                        ("before_checksum", before.checksum.to_string()),
                        ("after_checksum", after.checksum.to_string()),
                        ("artifact", json_option_string(artifact.as_deref())),
                    ],
                );
            } else {
                println!("VISUAL_SMOKE missing_before=true");
                state
                    .evidence
                    .record_event("visual_smoke", &[("missing_before", json_bool(true))]);
            }

            state.visual_after = Some(after);
            state.probe_step = ProbeStep::Hiding;
            Task::batch(vec![
                memory_task("mini_after_stream"),
                delayed_message_task(300, DaemonMessage::SendHotkeyProbe),
            ])
        }
        DaemonMessage::CheckWindowMode { phase } => {
            if let Some(window_id) = state.window_id {
                iced_window::mode(window_id)
                    .map(move |mode| DaemonMessage::WindowModeChecked { phase, mode })
            } else {
                Task::none()
            }
        }
        DaemonMessage::WindowModeChecked { phase, mode } => match phase {
            ModeCheckPhase::Hidden => {
                state.hide_seen = mode == iced_window::Mode::Hidden;
                println!("WINDOW_HIDDEN mode={mode:?} verified={}", state.hide_seen);
                state.evidence.record_event(
                    "window_hidden",
                    &[
                        ("mode", json_str(&format!("{mode:?}"))),
                        ("verified", json_bool(state.hide_seen)),
                    ],
                );

                if state.resident_probe_exit {
                    if state.hide_seen {
                        state.probe_step = ProbeStep::Restoring;
                        delayed_message_task(300, DaemonMessage::SendHotkeyProbe)
                    } else {
                        Task::done(DaemonMessage::ExitWithCode(7))
                    }
                } else {
                    Task::none()
                }
            }
            ModeCheckPhase::Restored | ModeCheckPhase::Opened => {
                let restored = mode == iced_window::Mode::Windowed;
                if phase == ModeCheckPhase::Restored {
                    state.restore_seen = restored;
                }
                println!("WINDOW_RESTORED mode={mode:?} phase={phase:?} verified={restored}");
                state.evidence.record_event(
                    "window_restored",
                    &[
                        ("mode", json_str(&format!("{mode:?}"))),
                        ("phase", json_str(&format!("{phase:?}"))),
                        ("verified", json_bool(restored)),
                    ],
                );

                if !restored && state.resident_probe_exit {
                    return Task::done(DaemonMessage::ExitWithCode(7));
                }

                Task::done(DaemonMessage::CheckEditorFocused { phase })
            }
        },
        DaemonMessage::WindowRawId(raw_id) => {
            state.window_raw_id = Some(raw_id);
            println!("WINDOW_RAW_ID raw_id={raw_id}");
            state
                .evidence
                .record_event("window_raw_id", &[("raw_id", raw_id.to_string())]);
            Task::done(DaemonMessage::CheckEditorFocused {
                phase: ModeCheckPhase::Opened,
            })
        }
        DaemonMessage::CheckEditorFocused { phase } => widget_operation::is_focused(MINI_INPUT_ID)
            .map(move |focused| DaemonMessage::EditorFocusedChecked { phase, focused }),
        DaemonMessage::EditorFocusedChecked { phase, focused } => {
            println!("INPUT_FOCUS phase={phase:?} focused={focused}");
            state.evidence.record_event(
                "input_focus",
                &[
                    ("phase", json_str(&format!("{phase:?}"))),
                    ("focused", json_bool(focused)),
                ],
            );

            if state.resident_probe_exit
                && matches!(phase, ModeCheckPhase::Opened)
                && focused
                && !state.chinese_input_sent
                && !state.chinese_input_seen
            {
                state.chinese_input_sent = true;
                return send_probe_text_input_task(CHINESE_PROBE_TEXT, state.input_method);
            }

            if state.resident_probe_exit
                && matches!(phase, ModeCheckPhase::Restored)
                && state.restore_seen
            {
                if !focused {
                    return Task::done(DaemonMessage::ExitWithCode(8));
                }
                if let Some(done) = state.handle_probe_completion() {
                    state.probe_step = ProbeStep::Done;
                    return done;
                }

                println!(
                    "DAEMON_SMOKE_INCOMPLETE chinese_input={} visual={} memory={} a11y={} hide={} restore={}",
                    state.chinese_input_seen,
                    state.visual_smoke_seen,
                    state.memory_smoke_seen,
                    state.a11y_smoke_seen,
                    state.hide_seen,
                    state.restore_seen
                );
                return Task::done(DaemonMessage::ExitWithCode(10));
            }

            Task::none()
        }
        DaemonMessage::MemoryMeasured { phase, result } => match result {
            Ok(frame) => {
                state.memory_frames.push(MemoryMeasurement { phase, frame });
                state.memory_smoke_seen = true;
                println!(
                    "MEMORY_SMOKE phase={phase} private_bytes_mb={:.1} working_set_mb={:.1}",
                    frame.private_bytes_mb(),
                    frame.working_set_mb()
                );
                state.evidence.record_event(
                    "memory_smoke",
                    &[
                        ("phase", json_str(phase)),
                        ("private_bytes", frame.private_bytes.to_string()),
                        ("working_set_bytes", frame.working_set_bytes.to_string()),
                        (
                            "private_bytes_mb",
                            json_number(format!("{:.1}", frame.private_bytes_mb())),
                        ),
                        (
                            "working_set_mb",
                            json_number(format!("{:.1}", frame.working_set_mb())),
                        ),
                    ],
                );
                Task::none()
            }
            Err(error) => {
                println!("MEMORY_SMOKE phase={phase} error={error:?}");
                state.evidence.record_event(
                    "memory_smoke",
                    &[("phase", json_str(phase)), ("error", json_str(&error))],
                );
                if state.resident_probe_exit {
                    Task::done(DaemonMessage::ExitWithCode(9))
                } else {
                    Task::none()
                }
            }
        },
        DaemonMessage::Exit => {
            state.write_evidence_manifest("passed", 0);
            std::process::exit(0);
        }
        DaemonMessage::ExitWithCode(code) => {
            state.write_evidence_manifest("failed", code);
            std::process::exit(code);
        }
    }
}

fn subscription(state: &DaemonState) -> Subscription<DaemonMessage> {
    let mut subscriptions = Vec::new();

    if state.hotkey_enabled {
        subscriptions.push(IcedAdapter::hotkey_subscription(mini_hotkey()).map(map_hotkey_event));
    }

    if state.window_id.is_some() {
        subscriptions
            .push(iced_window::events().map(|(id, event)| DaemonMessage::WindowEvent(id, event)));
    }

    Subscription::batch(subscriptions)
}

fn map_hotkey_event(event: IcedHotkeyEvent) -> DaemonMessage {
    match event {
        IcedHotkeyEvent::Pressed { id } => DaemonMessage::HotkeyPressed(id),
        IcedHotkeyEvent::Error { message } => DaemonMessage::HotkeyError(message),
    }
}

fn view(state: &DaemonState, _window_id: iced_window::Id) -> Element<'_, DaemonMessage> {
    IcedAdapter::compile_view_with_text_editors(&state.view, |id| {
        (id == MINI_INPUT_ID).then_some(&state.editor)
    })
}

fn build_view(input: &str, result: &str, streaming: bool) -> View<DaemonMessage> {
    let result_text = if result.is_empty() {
        "Press Translate to start streaming.".to_string()
    } else {
        result.to_string()
    };

    page("Mini Window")
        .content(
            column((
                text_editor(input)
                    .id(MINI_INPUT_ID)
                    .placeholder("Text to translate")
                    .min_height(56)
                    .max_height(56)
                    .focused(true)
                    .on_input(DaemonMessage::InputChanged),
                service_result_list([ResultItem::new("demo", "Demo Provider", result_text)
                    .status(if streaming {
                        ResultStatus::Streaming
                    } else if result.is_empty() {
                        ResultStatus::Loading
                    } else {
                        ResultStatus::Ready
                    })])
                .on_copy(DaemonMessage::Translate)
                .on_speak(DaemonMessage::Translate),
                command_bar((
                    primary_button(if streaming { "Streaming" } else { "Translate" })
                        .icon(icon::translate())
                        .enabled(!streaming)
                        .on_press(DaemonMessage::Translate),
                    button("Hide")
                        .icon(icon::clear())
                        .on_press(DaemonMessage::SendHotkeyProbe),
                ))
                .compact(true),
            ))
            .padding(14)
            .spacing(10),
        )
        .into_view()
}

fn merge_probe_input(current: &str, observed: &str) -> String {
    if observed == CHINESE_PROBE_TEXT {
        return observed.to_string();
    }

    if CHINESE_PROBE_TEXT.starts_with(observed) {
        return observed.to_string();
    }

    if observed.chars().count() == 1 {
        let candidate = format!("{current}{observed}");
        if CHINESE_PROBE_TEXT.starts_with(&candidate) {
            return candidate;
        }
    }

    observed.to_string()
}

fn mini_window_options() -> WindowOptions {
    WindowOptions::new("mini", "win_fluent Mini Window")
        .size(420.0, 360.0)
        .min_size(320.0, 220.0)
        .level(WindowLevel::TopMost)
        .frame(WindowFrame::Acrylic)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 })
        .skip_taskbar(true)
}

#[cfg(windows)]
fn mini_window_settings(options: &WindowOptions) -> (iced::window::Settings, String) {
    match win_fluent_platform_win::WindowsPlatformAdapter::resolve_window_placement(options) {
        Ok(placement) => {
            let settings = IcedAdapter::window_settings_with_position(
                options,
                Point::new(placement.x as f32, placement.y as f32),
            );
            (
                settings,
                format!(
                    "placement={}x{}@{},{} dpi={} work={}x{}@{},{} physical_work={}x{}@{},{}",
                    placement.width,
                    placement.height,
                    placement.x,
                    placement.y,
                    placement.dpi,
                    placement.work_area.width(),
                    placement.work_area.height(),
                    placement.work_area.left,
                    placement.work_area.top,
                    placement.physical_work_area.width(),
                    placement.physical_work_area.height(),
                    placement.physical_work_area.left,
                    placement.physical_work_area.top,
                ),
            )
        }
        Err(error) => (
            IcedAdapter::window_settings(options),
            format!("placement_unresolved={error:?}"),
        ),
    }
}

#[cfg(not(windows))]
fn mini_window_settings(options: &WindowOptions) -> (iced::window::Settings, String) {
    (
        IcedAdapter::window_settings(options),
        "placement_unresolved=non-windows".to_string(),
    )
}

fn show_focus_tasks(window_id: iced_window::Id, phase: ModeCheckPhase) -> Task<DaemonMessage> {
    Task::batch(vec![
        iced_window::set_mode::<DaemonMessage>(window_id, iced_window::Mode::Windowed),
        iced_window::minimize::<DaemonMessage>(window_id, false),
        iced_window::gain_focus::<DaemonMessage>(window_id),
        focus_editor_task(),
        delayed_message_task(500, DaemonMessage::CheckWindowMode { phase }),
    ])
}

fn focus_editor_task() -> Task<DaemonMessage> {
    widget_operation::focus::<DaemonMessage>(MINI_INPUT_ID)
}

fn stream_translation_task(generation: u64, input: String, delay_ms: u64) -> Task<DaemonMessage> {
    let chunks = [
        "Streaming ",
        "translation ",
        "for: ",
        input.as_str(),
        "\n\nThis validates token-driven incremental UI updates.",
    ];
    let last = chunks.len().saturating_sub(1);

    Task::batch(chunks.into_iter().enumerate().map(move |(index, text)| {
        let text = text.to_string();
        Task::perform(
            async move {
                std::thread::sleep(Duration::from_millis(delay_ms * (index as u64 + 1)));
                text
            },
            move |text| DaemonMessage::StreamChunk {
                generation,
                text,
                done: index == last,
            },
        )
    }))
}

fn delayed_message_task(delay_ms: u64, message: DaemonMessage) -> Task<DaemonMessage> {
    Task::perform(
        async move {
            std::thread::sleep(Duration::from_millis(delay_ms));
            message
        },
        |message| message,
    )
}

fn mini_hotkey() -> Hotkey {
    Hotkey::new("mini.toggle", HotkeyKey::Function(24))
        .modifier(HotkeyModifier::Control)
        .modifier(HotkeyModifier::Alt)
        .modifier(HotkeyModifier::Shift)
}

fn select_all_hotkey() -> Hotkey {
    Hotkey::new("probe.select_all", HotkeyKey::Character('a')).modifier(HotkeyModifier::Control)
}

fn backspace_hotkey() -> Hotkey {
    Hotkey::new("probe.backspace", HotkeyKey::Named("backspace".to_string()))
}

#[cfg(windows)]
fn send_hotkey_probe_task() -> Task<DaemonMessage> {
    Task::perform(
        async move {
            std::thread::sleep(Duration::from_millis(150));
            win_fluent_platform_win::WindowsPlatformAdapter::send_hotkey_input_for_probe(
                &mini_hotkey(),
            )
            .map_err(|error| format!("{error:?}"))
        },
        DaemonMessage::HotkeyProbeSent,
    )
}

#[cfg(not(windows))]
fn send_hotkey_probe_task() -> Task<DaemonMessage> {
    Task::done(DaemonMessage::HotkeyProbeSent(Err(
        "unsupported platform".to_string()
    )))
}

#[cfg(windows)]
fn send_probe_text_input_task(text: &'static str, method: TextInputMethod) -> Task<DaemonMessage> {
    Task::perform(
        async move {
            std::thread::sleep(Duration::from_millis(200));
            match method {
                TextInputMethod::UnicodeSendInput => {
                    win_fluent_platform_win::WindowsPlatformAdapter::send_unicode_text_input_for_probe(
                        text,
                    )
                    .map_err(|error| format!("{error:?}"))?;
                    Ok(TextInputProbeResult {
                        method,
                        clipboard_touched: false,
                        clipboard_restored: None,
                    })
                }
                TextInputMethod::ClipboardPaste => {
                    let snapshot =
                        win_fluent_platform_win::WindowsPlatformAdapter::clipboard_text_snapshot_for_probe(
                        )
                        .map_err(|error| format!("{error:?}"))?;

                    win_fluent_platform_win::WindowsPlatformAdapter::send_hotkey_input_for_probe(
                        &select_all_hotkey(),
                    )
                    .map_err(|error| format!("{error:?}"))?;
                    std::thread::sleep(Duration::from_millis(80));
                    for _ in 0..12 {
                        win_fluent_platform_win::WindowsPlatformAdapter::send_hotkey_input_for_probe(
                            &backspace_hotkey(),
                        )
                        .map_err(|error| format!("{error:?}"))?;
                        std::thread::sleep(Duration::from_millis(20));
                    }
                    std::thread::sleep(Duration::from_millis(120));
                    win_fluent_platform_win::WindowsPlatformAdapter::send_clipboard_text_paste_for_probe(
                        text,
                    )
                    .map_err(|error| format!("{error:?}"))?;
                    std::thread::sleep(Duration::from_millis(300));
                    win_fluent_platform_win::WindowsPlatformAdapter::restore_clipboard_text_for_probe(
                        &snapshot,
                    )
                    .map_err(|error| format!("{error:?}"))?;

                    Ok(TextInputProbeResult {
                        method,
                        clipboard_touched: true,
                        clipboard_restored: Some(true),
                    })
                }
            }
        },
        DaemonMessage::TextInputSent,
    )
}

#[cfg(not(windows))]
fn send_probe_text_input_task(
    _text: &'static str,
    _method: TextInputMethod,
) -> Task<DaemonMessage> {
    Task::done(DaemonMessage::TextInputSent(Err(
        "unsupported platform".to_string()
    )))
}

fn memory_task(phase: &'static str) -> Task<DaemonMessage> {
    Task::perform(async move { current_memory_frame() }, move |result| {
        DaemonMessage::MemoryMeasured { phase, result }
    })
}

#[cfg(windows)]
fn current_memory_frame() -> Result<MemoryFrame, String> {
    let memory = win_fluent_platform_win::WindowsPlatformAdapter::current_process_memory()
        .map_err(|error| format!("{error:?}"))?;
    Ok(MemoryFrame {
        private_bytes: memory.private_bytes,
        working_set_bytes: memory.working_set_bytes,
    })
}

#[cfg(not(windows))]
fn current_memory_frame() -> Result<MemoryFrame, String> {
    Err("unsupported platform".to_string())
}

fn accessibility_smoke(view: &View<DaemonMessage>) -> A11yEvidence {
    let tree = win_fluent::resolve_accessibility_tree(view);
    let mut counts = A11yCounts::default();
    count_a11y_roles(&tree, &mut counts);
    let ok = tree.role == A11yRole::Application
        && counts.text_inputs == 1
        && counts.buttons >= 2
        && counts.lists == 1;

    A11yEvidence {
        root: format!("{:?}", tree.role),
        name: tree.name.clone(),
        text_inputs: counts.text_inputs,
        buttons: counts.buttons,
        lists: counts.lists,
        ok,
    }
}

fn print_accessibility_smoke(evidence: &A11yEvidence) {
    println!(
        "A11Y_SMOKE root={} name={:?} text_inputs={} buttons={} lists={} ok={}",
        evidence.root,
        evidence.name,
        evidence.text_inputs,
        evidence.buttons,
        evidence.lists,
        evidence.ok
    );
}

fn count_a11y_roles(node: &A11yNode, counts: &mut A11yCounts) {
    match node.role {
        A11yRole::TextInput => counts.text_inputs += 1,
        A11yRole::Button => counts.buttons += 1,
        A11yRole::List => counts.lists += 1,
        _ => {}
    }

    for child in &node.children {
        count_a11y_roles(child, counts);
    }
}

#[derive(Default)]
struct A11yCounts {
    text_inputs: usize,
    buttons: usize,
    lists: usize,
}

#[derive(Clone, Debug)]
struct A11yEvidence {
    root: String,
    name: Option<String>,
    text_inputs: usize,
    buttons: usize,
    lists: usize,
    ok: bool,
}

#[derive(Clone, Debug)]
struct VisualFrame {
    width: u32,
    height: u32,
    dpi: u32,
    scale_factor: f32,
    width_dips: f32,
    height_dips: f32,
    checksum: u64,
    rgba: Vec<u8>,
}

impl VisualFrame {
    fn from_window_screenshot(screenshot: WindowScreenshot) -> Self {
        Self {
            width: screenshot.width_physical,
            height: screenshot.height_physical,
            dpi: screenshot.dpi,
            scale_factor: screenshot.scale_factor,
            width_dips: screenshot.width_dips,
            height_dips: screenshot.height_dips,
            checksum: screenshot.checksum(),
            rgba: screenshot.rgba,
        }
    }

    fn diff(&self, after: &Self) -> VisualDiff {
        let mut changed_pixels = 0usize;
        let mut total_delta = 0u64;

        for (before, after) in self.rgba.chunks_exact(4).zip(after.rgba.chunks_exact(4)) {
            let mut pixel_delta = 0u16;
            for channel in 0..4 {
                pixel_delta += before[channel].abs_diff(after[channel]) as u16;
            }
            if pixel_delta > 0 {
                changed_pixels += 1;
                total_delta += u64::from(pixel_delta);
            }
        }

        VisualDiff {
            changed_pixels,
            total_delta,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct VisualDiff {
    changed_pixels: usize,
    total_delta: u64,
}

#[derive(Clone, Copy, Debug)]
struct MemoryFrame {
    private_bytes: usize,
    working_set_bytes: usize,
}

impl MemoryFrame {
    fn private_bytes_mb(self) -> f64 {
        self.private_bytes as f64 / 1024.0 / 1024.0
    }

    fn working_set_mb(self) -> f64 {
        self.working_set_bytes as f64 / 1024.0 / 1024.0
    }
}

#[derive(Clone, Copy, Debug)]
struct MemoryMeasurement {
    phase: &'static str,
    frame: MemoryFrame,
}

#[derive(Debug)]
struct EvidenceRecorder {
    dir: Option<PathBuf>,
    events_path: Option<PathBuf>,
    error: Option<String>,
}

impl EvidenceRecorder {
    fn new(requested_dir: Option<PathBuf>, enabled: bool) -> Self {
        if !enabled && requested_dir.is_none() {
            return Self {
                dir: None,
                events_path: None,
                error: None,
            };
        }

        let dir = requested_dir.unwrap_or_else(default_evidence_dir);
        match fs::create_dir_all(&dir) {
            Ok(()) => {
                let events_path = dir.join("events.jsonl");
                match fs::write(&events_path, b"") {
                    Ok(()) => {
                        println!("EVIDENCE_DIR path={}", dir.display());
                        Self {
                            dir: Some(dir),
                            events_path: Some(events_path),
                            error: None,
                        }
                    }
                    Err(error) => {
                        println!("EVIDENCE_ERROR operation=init_events error={error:?}");
                        Self {
                            dir: Some(dir),
                            events_path: None,
                            error: Some(format!("init_events: {error}")),
                        }
                    }
                }
            }
            Err(error) => {
                println!("EVIDENCE_ERROR operation=create_dir error={error:?}");
                Self {
                    dir: Some(dir),
                    events_path: None,
                    error: Some(format!("create_dir: {error}")),
                }
            }
        }
    }

    fn is_ready(&self) -> bool {
        self.dir.is_some() && self.events_path.is_some() && self.error.is_none()
    }

    fn record_event(&self, event: &str, fields: &[(&str, String)]) {
        let Some(events_path) = &self.events_path else {
            return;
        };

        let mut line = String::new();
        line.push_str("{\"ts_ms\":");
        line.push_str(&now_ms().to_string());
        line.push_str(",\"event\":");
        line.push_str(&json_str(event));
        for (key, value) in fields {
            line.push(',');
            line.push_str(&json_string_literal(key));
            line.push(':');
            line.push_str(value);
        }
        line.push_str("}\n");

        match OpenOptions::new().append(true).open(events_path) {
            Ok(mut file) => {
                if let Err(error) = file.write_all(line.as_bytes()) {
                    println!("EVIDENCE_ERROR operation=append_event error={error:?}");
                }
            }
            Err(error) => {
                println!("EVIDENCE_ERROR operation=open_events error={error:?}");
            }
        }
    }

    fn write_visual_frame(&self, name: &str, frame: &VisualFrame) -> Option<String> {
        let dir = self.dir.as_ref()?;
        let path = dir.join(format!("{name}.ppm"));
        let mut bytes = format!("P6\n{} {}\n255\n", frame.width, frame.height).into_bytes();
        for pixel in frame.rgba.chunks_exact(4) {
            bytes.extend_from_slice(&pixel[..3]);
        }

        match fs::write(&path, bytes) {
            Ok(()) => Some(path.display().to_string()),
            Err(error) => {
                println!("EVIDENCE_ERROR operation=write_visual_frame error={error:?}");
                None
            }
        }
    }

    fn write_manifest(&self, state: &DaemonState, status: &str, exit_code: i32) {
        let Some(dir) = &self.dir else {
            return;
        };

        let path = dir.join("manifest.json");
        let content = self.manifest_json(state, status, exit_code);
        match fs::write(&path, content) {
            Ok(()) => println!("EVIDENCE_MANIFEST path={}", path.display()),
            Err(error) => println!("EVIDENCE_ERROR operation=write_manifest error={error:?}"),
        }
    }

    fn manifest_json(&self, state: &DaemonState, status: &str, exit_code: i32) -> String {
        let evidence_dir = self
            .dir
            .as_ref()
            .map(|path| path.display().to_string())
            .unwrap_or_default();
        let events_path = self
            .events_path
            .as_ref()
            .map(|path| path.display().to_string())
            .unwrap_or_default();
        let visual_before_path = self
            .dir
            .as_ref()
            .map(|dir| dir.join("visual_before.ppm").display().to_string());
        let visual_after_path = self
            .dir
            .as_ref()
            .map(|dir| dir.join("visual_after.ppm").display().to_string());

        format!(
            concat!(
                "{{\n",
                "  \"schema_version\": {schema_version},\n",
                "  \"status\": {status},\n",
                "  \"exit_code\": {exit_code},\n",
                "  \"generated_at_ms\": {generated_at_ms},\n",
                "  \"process\": {{ \"pid\": {pid}, \"os\": {os}, \"arch\": {arch} }},\n",
                "  \"artifacts\": {{ \"dir\": {evidence_dir}, \"events\": {events_path} }},\n",
                "  \"daemon\": {{ \"default_window\": null, \"no_default_window\": true, \"source\": \"iced_daemon\" }},\n",
                "  \"window\": {{ \"raw_id\": {window_raw_id}, \"visible\": {window_visible}, \"focused\": {window_focused}, \"hide_verified\": {hide_seen}, \"restore_verified\": {restore_seen}, \"focus_event_seen\": {focus_event_seen} }},\n",
                "  \"input\": {{ \"method\": {input_method}, \"text\": {probe_text}, \"sent\": {input_sent}, \"seen\": {input_seen}, \"clipboard_touched\": {clipboard_touched}, \"clipboard_restored\": {clipboard_restored} }},\n",
                "  \"visual\": {visual},\n",
                "  \"memory\": {memory},\n",
                "  \"accessibility\": {accessibility},\n",
                "  \"checks\": {{ \"hotkeys\": {hotkeys}, \"visual\": {visual_seen}, \"memory\": {memory_seen}, \"a11y\": {a11y_seen} }}\n",
                "}}\n"
            ),
            schema_version = EVIDENCE_SCHEMA_VERSION,
            status = json_str(status),
            exit_code = exit_code,
            generated_at_ms = now_ms(),
            pid = std::process::id(),
            os = json_str(std::env::consts::OS),
            arch = json_str(std::env::consts::ARCH),
            evidence_dir = json_str(&evidence_dir),
            events_path = json_str(&events_path),
            window_raw_id = json_option_u64(state.window_raw_id),
            window_visible = json_bool(state.window_visible),
            window_focused = json_bool(state.window_has_focus),
            hide_seen = json_bool(state.hide_seen),
            restore_seen = json_bool(state.restore_seen),
            focus_event_seen = json_bool(state.focus_event_seen),
            input_method = json_str(state.input_method.as_str()),
            probe_text = json_str(CHINESE_PROBE_TEXT),
            input_sent = json_bool(state.chinese_input_sent),
            input_seen = json_bool(state.chinese_input_seen),
            clipboard_touched = json_bool(state.clipboard_touched),
            clipboard_restored = json_option_bool(state.clipboard_restored),
            visual = visual_json(
                state.visual_before.as_ref(),
                state.visual_after.as_ref(),
                state.visual_diff,
                visual_before_path.as_deref(),
                visual_after_path.as_deref(),
            ),
            memory = memory_json(&state.memory_frames),
            accessibility = accessibility_json(&state.a11y_evidence),
            hotkeys = state.hotkey_count,
            visual_seen = json_bool(state.visual_smoke_seen),
            memory_seen = json_bool(state.memory_smoke_seen),
            a11y_seen = json_bool(state.a11y_smoke_seen),
        )
    }
}

fn default_evidence_dir() -> PathBuf {
    std::env::temp_dir()
        .join("win_fluent_mini_daemon_evidence")
        .join(format!("run-{}-{}", now_ms(), std::process::id()))
}

fn visual_json(
    before: Option<&VisualFrame>,
    after: Option<&VisualFrame>,
    diff: Option<VisualDiff>,
    before_path: Option<&str>,
    after_path: Option<&str>,
) -> String {
    let before = visual_frame_json(before, before_path);
    let after = visual_frame_json(after, after_path);
    let diff = diff
        .map(|diff| {
            format!(
                "{{ \"changed_pixels\": {}, \"total_delta\": {} }}",
                diff.changed_pixels, diff.total_delta
            )
        })
        .unwrap_or_else(json_null);

    format!("{{ \"before\": {before}, \"after\": {after}, \"diff\": {diff} }}")
}

fn visual_frame_json(frame: Option<&VisualFrame>, artifact: Option<&str>) -> String {
    match frame {
        Some(frame) => format!(
            "{{ \"width\": {}, \"height\": {}, \"dpi\": {}, \"scale_factor\": {}, \"width_dips\": {}, \"height_dips\": {}, \"checksum\": {}, \"artifact\": {} }}",
            frame.width,
            frame.height,
            frame.dpi,
            frame.scale_factor,
            frame.width_dips,
            frame.height_dips,
            frame.checksum,
            json_option_string(artifact)
        ),
        None => json_null(),
    }
}

fn memory_json(measurements: &[MemoryMeasurement]) -> String {
    let values = measurements
        .iter()
        .map(|measurement| {
            format!(
                "{{ \"phase\": {}, \"private_bytes\": {}, \"working_set_bytes\": {}, \"private_bytes_mb\": {:.1}, \"working_set_mb\": {:.1} }}",
                json_str(measurement.phase),
                measurement.frame.private_bytes,
                measurement.frame.working_set_bytes,
                measurement.frame.private_bytes_mb(),
                measurement.frame.working_set_mb(),
            )
        })
        .collect::<Vec<_>>();
    format!("[{}]", values.join(", "))
}

fn accessibility_json(evidence: &A11yEvidence) -> String {
    format!(
        "{{ \"source\": \"win_fluent_token_tree\", \"root\": {}, \"name\": {}, \"text_inputs\": {}, \"buttons\": {}, \"lists\": {}, \"ok\": {} }}",
        json_str(&evidence.root),
        json_option_string(evidence.name.as_deref()),
        evidence.text_inputs,
        evidence.buttons,
        evidence.lists,
        json_bool(evidence.ok),
    )
}

fn now_ms() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default()
}

fn json_str(value: &str) -> String {
    json_string_literal(value)
}

fn json_string_literal(value: &str) -> String {
    let mut escaped = String::with_capacity(value.len() + 2);
    escaped.push('"');
    for ch in value.chars() {
        match ch {
            '"' => escaped.push_str("\\\""),
            '\\' => escaped.push_str("\\\\"),
            '\n' => escaped.push_str("\\n"),
            '\r' => escaped.push_str("\\r"),
            '\t' => escaped.push_str("\\t"),
            ch if ch.is_control() => escaped.push_str(&format!("\\u{:04x}", ch as u32)),
            ch => escaped.push(ch),
        }
    }
    escaped.push('"');
    escaped
}

fn json_bool(value: bool) -> String {
    value.to_string()
}

fn json_null() -> String {
    "null".to_string()
}

fn json_number(value: String) -> String {
    value
}

fn json_option_bool(value: Option<bool>) -> String {
    value.map(json_bool).unwrap_or_else(json_null)
}

fn json_option_string(value: Option<&str>) -> String {
    value.map(json_str).unwrap_or_else(json_null)
}

fn json_option_u64(value: Option<u64>) -> String {
    value
        .map(|value| value.to_string())
        .unwrap_or_else(json_null)
}

#[derive(Clone)]
struct DaemonMode {
    hotkey_enabled: bool,
    resident_probe_exit: bool,
    stream_delay_ms: u64,
    input_method: TextInputMethod,
    evidence_dir: Option<PathBuf>,
}

impl DaemonMode {
    fn from_args() -> Self {
        let args = std::env::args().collect::<Vec<_>>();
        Self::from_args_values(&args)
    }

    fn from_args_values(args: &[String]) -> Self {
        let resident_probe_exit = args.iter().any(|arg| arg == "--resident-probe-exit");
        Self {
            hotkey_enabled: resident_probe_exit || args.iter().any(|arg| arg == "--hotkey"),
            resident_probe_exit,
            stream_delay_ms: stream_delay_ms(args),
            input_method: input_method(args),
            evidence_dir: evidence_dir(args),
        }
    }
}

fn stream_delay_ms(args: &[String]) -> u64 {
    args.iter()
        .find_map(|arg| arg.strip_prefix("--stream-delay-ms="))
        .and_then(|value| value.parse::<u64>().ok())
        .filter(|value| (1..=5_000).contains(value))
        .unwrap_or(90)
}

fn input_method(args: &[String]) -> TextInputMethod {
    args.iter()
        .find_map(|arg| arg.strip_prefix("--input-method="))
        .and_then(TextInputMethod::parse)
        .unwrap_or(TextInputMethod::UnicodeSendInput)
}

fn evidence_dir(args: &[String]) -> Option<PathBuf> {
    args.iter()
        .find_map(|arg| arg.strip_prefix("--evidence-dir="))
        .map(PathBuf::from)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum TextInputMethod {
    UnicodeSendInput,
    ClipboardPaste,
}

impl TextInputMethod {
    fn parse(value: &str) -> Option<Self> {
        match value {
            "unicode" | "unicode-sendinput" | "unicode_sendinput" => Some(Self::UnicodeSendInput),
            "clipboard" | "clipboard-paste" | "clipboard_paste" => Some(Self::ClipboardPaste),
            _ => None,
        }
    }

    fn as_str(self) -> &'static str {
        match self {
            Self::UnicodeSendInput => "unicode_sendinput",
            Self::ClipboardPaste => "clipboard_paste",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct TextInputProbeResult {
    method: TextInputMethod,
    clipboard_touched: bool,
    clipboard_restored: Option<bool>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ProbeStep {
    Manual,
    WaitingStartup,
    Opening,
    Streaming,
    Hiding,
    Restoring,
    Done,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ModeCheckPhase {
    Opened,
    Hidden,
    Restored,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resident_probe_starts_hotkey_enabled_without_window() {
        let mode = DaemonMode::from_args_values(&[
            "demo".to_string(),
            "--resident-probe-exit".to_string(),
            "--stream-delay-ms=250".to_string(),
        ]);
        let (state, _task) = DaemonState::new(mode);

        assert!(state.hotkey_enabled);
        assert!(state.resident_probe_exit);
        assert_eq!(state.probe_step, ProbeStep::WaitingStartup);
        assert!(state.window_id.is_none());
        assert!(!state.window_visible);
        assert_eq!(state.stream_delay_ms, 250);
    }

    #[test]
    fn mini_daemon_view_has_accessibility_smoke_shape() {
        let view = build_view(CHINESE_PROBE_TEXT, "Result", false);
        let tree = win_fluent::resolve_accessibility_tree(&view);
        let mut counts = A11yCounts::default();
        count_a11y_roles(&tree, &mut counts);

        assert_eq!(tree.role, A11yRole::Application);
        assert_eq!(counts.text_inputs, 1);
        assert!(counts.buttons >= 2);
        assert_eq!(counts.lists, 1);
    }

    #[test]
    fn visual_diff_detects_changed_pixels() {
        let before = VisualFrame {
            width: 1,
            height: 1,
            dpi: 96,
            scale_factor: 1.0,
            width_dips: 1.0,
            height_dips: 1.0,
            checksum: 0,
            rgba: vec![0, 0, 0, 255],
        };
        let after = VisualFrame {
            width: 1,
            height: 1,
            dpi: 96,
            scale_factor: 1.0,
            width_dips: 1.0,
            height_dips: 1.0,
            checksum: 0,
            rgba: vec![1, 2, 3, 255],
        };

        let diff = before.diff(&after);

        assert_eq!(diff.changed_pixels, 1);
        assert_eq!(diff.total_delta, 6);
    }

    #[test]
    fn probe_input_merges_character_callbacks() {
        let mut value = String::new();
        for ch in CHINESE_PROBE_TEXT.chars() {
            value = merge_probe_input(&value, &ch.to_string());
        }

        assert_eq!(value, CHINESE_PROBE_TEXT);
    }
}
