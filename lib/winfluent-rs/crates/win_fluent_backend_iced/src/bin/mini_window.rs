use std::time::Duration;

use iced::{window as iced_window, Element, Point, Subscription, Task};
use win_fluent::platform::{Hotkey, HotkeyKey, HotkeyModifier};
use win_fluent::prelude::*;
use win_fluent_backend_iced::{IcedAdapter, IcedHotkeyEvent, IcedTextEditorContent};

pub fn main() -> iced::Result {
    let options = mini_window_options();
    let mode = MiniMode::from_args();
    let (settings, placement_report) = mini_window_settings(&options);

    if mode.print_placement {
        println!("{placement_report}");
        return Ok(());
    }

    iced::application(move || MiniState::new(mode), update, view)
        .title("win_fluent Mini Window")
        .window(settings)
        .subscription(subscription)
        .exit_on_close_request(true)
        .run()
}

#[derive(Debug, Clone)]
enum MiniMessage {
    InputChanged(String),
    Translate,
    StreamChunk {
        generation: u64,
        text: String,
        done: bool,
    },
    HotkeyPressed(String),
    HotkeyError(String),
    StartLifecycleProbe,
    LifecycleWindowFound(Option<iced_window::Id>),
    LifecycleRawId(u64),
    CheckLifecycleMinimized,
    CheckLifecycleRestored,
    LifecycleMinimizedState {
        phase: LifecyclePhase,
        minimized: Option<bool>,
    },
    SendHotkeyProbe,
    HotkeyProbeSent(Result<(), String>),
    HotkeyProbeTimedOut,
    Copy,
    Speak,
    Exit,
    ExitWithCode(i32),
}

struct MiniState {
    input: String,
    editor: IcedTextEditorContent,
    result: String,
    window_id: Option<iced_window::Id>,
    generation: u64,
    streaming: bool,
    exit_after_stream: bool,
    exit_after_hotkey_stream: bool,
    hotkey_enabled: bool,
    hotkey_count: u64,
    hotkey_stream_done: bool,
    lifecycle_probe_exit: bool,
    lifecycle_minimized_seen: bool,
    lifecycle_restored_seen: bool,
    stream_delay_ms: u64,
    view: View<MiniMessage>,
}

impl MiniState {
    fn new(mode: MiniMode) -> (Self, Task<MiniMessage>) {
        let input = "Selected text from another app".to_string();
        let editor = IcedTextEditorContent::with_text(&input);
        let result = String::new();
        let generation = if mode.auto_stream { 1 } else { 0 };
        let streaming = mode.auto_stream;
        let view = build_view(&input, &result, streaming);
        let mut initial_tasks = Vec::new();
        if mode.auto_stream {
            initial_tasks.push(stream_translation_task(
                generation,
                input.clone(),
                mode.stream_delay_ms,
            ));
        }
        if mode.hotkey_probe_exit {
            initial_tasks.push(delayed_message_task(800, MiniMessage::SendHotkeyProbe));
            initial_tasks.push(delayed_message_task(
                6_000,
                MiniMessage::HotkeyProbeTimedOut,
            ));
        }
        if mode.lifecycle_probe_exit {
            initial_tasks.push(delayed_message_task(500, MiniMessage::StartLifecycleProbe));
            initial_tasks.push(delayed_message_task(
                10_000,
                MiniMessage::HotkeyProbeTimedOut,
            ));
        }

        (
            Self {
                input,
                editor,
                result,
                window_id: None,
                generation,
                streaming,
                exit_after_stream: mode.auto_stream_exit,
                exit_after_hotkey_stream: mode.hotkey_probe_exit || mode.lifecycle_probe_exit,
                hotkey_enabled: mode.hotkey_enabled,
                hotkey_count: 0,
                hotkey_stream_done: false,
                lifecycle_probe_exit: mode.lifecycle_probe_exit,
                lifecycle_minimized_seen: false,
                lifecycle_restored_seen: false,
                stream_delay_ms: mode.stream_delay_ms,
                view,
            },
            Task::batch(initial_tasks),
        )
    }

    fn start_translation(&mut self) -> Task<MiniMessage> {
        self.generation = self.generation.wrapping_add(1);
        self.result.clear();
        self.streaming = true;
        self.rebuild_view();
        stream_translation_task(self.generation, self.input.clone(), self.stream_delay_ms)
    }

    fn rebuild_view(&mut self) {
        self.view = build_view(&self.input, &self.result, self.streaming);
    }
}

fn update(state: &mut MiniState, message: MiniMessage) -> Task<MiniMessage> {
    match message {
        MiniMessage::InputChanged(value) => {
            state.input = value;
            state.editor = IcedTextEditorContent::with_text(&state.input);
            state.result.clear();
            state.streaming = false;
            state.rebuild_view();
            Task::none()
        }
        MiniMessage::Translate => state.start_translation(),
        MiniMessage::StreamChunk {
            generation,
            text,
            done,
        } => {
            if generation == state.generation {
                state.result.push_str(&text);
                state.streaming = !done;
                state.rebuild_view();

                if done && state.exit_after_hotkey_stream && state.hotkey_count > 0 {
                    state.hotkey_stream_done = true;
                    println!(
                        "HOTKEY_STREAM_DONE hotkeys={} generation={} bytes={} text={:?}",
                        state.hotkey_count,
                        state.generation,
                        state.result.len(),
                        state.result
                    );

                    if state.lifecycle_probe_exit && !state.lifecycle_restored_seen {
                        return Task::none();
                    }

                    return Task::done(MiniMessage::Exit);
                }

                if done && state.exit_after_stream {
                    println!(
                        "STREAM_DONE generation={} bytes={} text={:?}",
                        state.generation,
                        state.result.len(),
                        state.result
                    );
                    return Task::done(MiniMessage::Exit);
                }
            }

            Task::none()
        }
        MiniMessage::HotkeyPressed(id) => {
            state.hotkey_count = state.hotkey_count.wrapping_add(1);
            println!("HOTKEY_TRIGGERED id={id} count={}", state.hotkey_count);

            if state.streaming {
                Task::none()
            } else {
                let translation = state.start_translation();
                if state.lifecycle_probe_exit {
                    if let Some(window_id) = state.window_id {
                        return Task::batch(vec![restore_and_focus_task(window_id), translation]);
                    }
                }

                translation
            }
        }
        MiniMessage::HotkeyError(error) => {
            println!("HOTKEY_ERROR error={error:?}");
            state.result = format!("Hotkey error: {error}");
            state.streaming = false;
            state.rebuild_view();

            if state.exit_after_hotkey_stream {
                Task::done(MiniMessage::ExitWithCode(2))
            } else {
                Task::none()
            }
        }
        MiniMessage::StartLifecycleProbe => {
            iced_window::latest().map(MiniMessage::LifecycleWindowFound)
        }
        MiniMessage::LifecycleWindowFound(window_id) => match window_id {
            Some(window_id) => {
                state.window_id = Some(window_id);
                println!("WINDOW_LIFECYCLE_FOUND id={window_id:?}");
                Task::batch(vec![
                    iced_window::raw_id::<MiniMessage>(window_id).map(MiniMessage::LifecycleRawId),
                    iced_window::minimize::<MiniMessage>(window_id, true),
                    delayed_message_task(600, MiniMessage::CheckLifecycleMinimized),
                ])
            }
            None => {
                println!("WINDOW_LIFECYCLE_MISSING");
                Task::done(MiniMessage::ExitWithCode(4))
            }
        },
        MiniMessage::LifecycleRawId(raw_id) => {
            println!("WINDOW_RAW_ID raw_id={raw_id}");
            Task::none()
        }
        MiniMessage::CheckLifecycleMinimized => {
            if let Some(window_id) = state.window_id {
                iced_window::is_minimized(window_id).map(|minimized| {
                    MiniMessage::LifecycleMinimizedState {
                        phase: LifecyclePhase::Minimized,
                        minimized,
                    }
                })
            } else {
                println!("WINDOW_LIFECYCLE_MISSING");
                Task::done(MiniMessage::ExitWithCode(4))
            }
        }
        MiniMessage::CheckLifecycleRestored => {
            if let Some(window_id) = state.window_id {
                iced_window::is_minimized(window_id).map(|minimized| {
                    MiniMessage::LifecycleMinimizedState {
                        phase: LifecyclePhase::Restored,
                        minimized,
                    }
                })
            } else {
                println!("WINDOW_LIFECYCLE_MISSING");
                Task::done(MiniMessage::ExitWithCode(4))
            }
        }
        MiniMessage::LifecycleMinimizedState { phase, minimized } => match phase {
            LifecyclePhase::Minimized => {
                state.lifecycle_minimized_seen = minimized == Some(true);
                println!(
                    "WINDOW_MINIMIZED minimized={minimized:?} verified={}",
                    state.lifecycle_minimized_seen
                );

                if state.lifecycle_minimized_seen {
                    delayed_message_task(300, MiniMessage::SendHotkeyProbe)
                } else {
                    Task::done(MiniMessage::ExitWithCode(4))
                }
            }
            LifecyclePhase::Restored => {
                state.lifecycle_restored_seen = minimized == Some(false);
                println!(
                    "WINDOW_RESTORED minimized={minimized:?} verified={}",
                    state.lifecycle_restored_seen
                );

                if !state.lifecycle_restored_seen {
                    return Task::done(MiniMessage::ExitWithCode(4));
                }

                if state.lifecycle_probe_exit && state.hotkey_stream_done {
                    println!(
                        "WINDOW_LIFECYCLE_DONE minimized_seen={} restored_seen={} hotkey_stream_done={}",
                        state.lifecycle_minimized_seen,
                        state.lifecycle_restored_seen,
                        state.hotkey_stream_done
                    );
                    Task::done(MiniMessage::Exit)
                } else {
                    Task::none()
                }
            }
        },
        MiniMessage::SendHotkeyProbe => send_hotkey_probe_task(),
        MiniMessage::HotkeyProbeSent(result) => match result {
            Ok(()) => {
                println!("HOTKEY_PROBE_SENT");
                Task::none()
            }
            Err(error) => {
                println!("HOTKEY_PROBE_SEND_FAILED error={error:?}");
                if state.exit_after_hotkey_stream {
                    Task::done(MiniMessage::ExitWithCode(2))
                } else {
                    Task::none()
                }
            }
        },
        MiniMessage::HotkeyProbeTimedOut => {
            if state.lifecycle_probe_exit
                && !(state.lifecycle_minimized_seen
                    && state.lifecycle_restored_seen
                    && state.hotkey_stream_done)
            {
                println!(
                    "WINDOW_LIFECYCLE_TIMEOUT minimized_seen={} restored_seen={} hotkey_stream_done={} hotkeys={}",
                    state.lifecycle_minimized_seen,
                    state.lifecycle_restored_seen,
                    state.hotkey_stream_done,
                    state.hotkey_count
                );
                Task::done(MiniMessage::ExitWithCode(3))
            } else if state.exit_after_hotkey_stream && state.hotkey_count == 0 {
                println!("HOTKEY_PROBE_TIMEOUT");
                Task::done(MiniMessage::ExitWithCode(3))
            } else {
                Task::none()
            }
        }
        MiniMessage::Copy | MiniMessage::Speak => Task::none(),
        MiniMessage::Exit => {
            std::process::exit(0);
        }
        MiniMessage::ExitWithCode(code) => {
            std::process::exit(code);
        }
    }
}

#[derive(Clone, Copy, Debug)]
enum LifecyclePhase {
    Minimized,
    Restored,
}

fn subscription(state: &MiniState) -> Subscription<MiniMessage> {
    if state.hotkey_enabled {
        IcedAdapter::hotkey_subscription(mini_hotkey()).map(map_hotkey_event)
    } else {
        Subscription::none()
    }
}

fn map_hotkey_event(event: IcedHotkeyEvent) -> MiniMessage {
    match event {
        IcedHotkeyEvent::Pressed { id } => MiniMessage::HotkeyPressed(id),
        IcedHotkeyEvent::Error { message } => MiniMessage::HotkeyError(message),
    }
}

fn view(state: &MiniState) -> Element<'_, MiniMessage> {
    IcedAdapter::compile_view_with_text_editors(&state.view, |id| {
        (id == "mini.input").then_some(&state.editor)
    })
}

fn stream_translation_task(generation: u64, input: String, delay_ms: u64) -> Task<MiniMessage> {
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
            move |text| MiniMessage::StreamChunk {
                generation,
                text,
                done: index == last,
            },
        )
    }))
}

fn delayed_message_task(delay_ms: u64, message: MiniMessage) -> Task<MiniMessage> {
    Task::perform(
        async move {
            std::thread::sleep(Duration::from_millis(delay_ms));
            message
        },
        |message| message,
    )
}

fn restore_and_focus_task(window_id: iced_window::Id) -> Task<MiniMessage> {
    Task::batch(vec![
        iced_window::minimize::<MiniMessage>(window_id, false),
        iced_window::gain_focus::<MiniMessage>(window_id),
        delayed_message_task(600, MiniMessage::CheckLifecycleRestored),
    ])
}

fn mini_hotkey() -> Hotkey {
    Hotkey::new("mini.translate", HotkeyKey::Function(24))
        .modifier(HotkeyModifier::Control)
        .modifier(HotkeyModifier::Alt)
        .modifier(HotkeyModifier::Shift)
}

#[cfg(windows)]
fn send_hotkey_probe_task() -> Task<MiniMessage> {
    Task::perform(
        async move {
            std::thread::sleep(Duration::from_millis(150));
            win_fluent_platform_win::WindowsPlatformAdapter::send_hotkey_input_for_probe(
                &mini_hotkey(),
            )
            .map_err(|error| format!("{error:?}"))
        },
        MiniMessage::HotkeyProbeSent,
    )
}

#[cfg(not(windows))]
fn send_hotkey_probe_task() -> Task<MiniMessage> {
    Task::done(MiniMessage::HotkeyProbeSent(Err(
        "unsupported platform".to_string()
    )))
}

fn build_view(input: &str, result: &str, streaming: bool) -> View<MiniMessage> {
    let result_text = if result.is_empty() {
        "Press Translate to start streaming.".to_string()
    } else {
        result.to_string()
    };

    page("Mini Window")
        .content(
            column((
                text_editor(input)
                    .id("mini.input")
                    .placeholder("Text to translate")
                    .min_height(56)
                    .max_height(56)
                    .focused(true)
                    .on_input(MiniMessage::InputChanged),
                service_result_list([ResultItem::new("demo", "Demo Provider", result_text)
                    .status(if streaming {
                        ResultStatus::Streaming
                    } else if result.is_empty() {
                        ResultStatus::Loading
                    } else {
                        ResultStatus::Ready
                    })])
                .on_copy(MiniMessage::Copy)
                .on_speak(MiniMessage::Speak),
                command_bar((
                    primary_button(if streaming { "Streaming" } else { "Translate" })
                        .icon(icon::translate())
                        .enabled(!streaming)
                        .on_press(MiniMessage::Translate),
                    button("Copy")
                        .icon(icon::copy())
                        .on_press(MiniMessage::Copy),
                    button("Speak")
                        .icon(icon::speaker())
                        .enabled(!result.is_empty() && !streaming)
                        .on_press(MiniMessage::Speak),
                ))
                .compact(true),
            ))
            .padding(14)
            .spacing(10),
        )
        .into_view()
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
                    "PLACEMENT width={} height={} x={} y={} dpi={} work={}x{}@{},{} physical_work={}x{}@{},{}",
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
            format!("PLACEMENT_UNRESOLVED error={error:?}"),
        ),
    }
}

#[cfg(not(windows))]
fn mini_window_settings(options: &WindowOptions) -> (iced::window::Settings, String) {
    (
        IcedAdapter::window_settings(options),
        "PLACEMENT_UNRESOLVED platform=non-windows".to_string(),
    )
}

#[derive(Clone, Copy)]
struct MiniMode {
    auto_stream: bool,
    auto_stream_exit: bool,
    hotkey_enabled: bool,
    hotkey_probe_exit: bool,
    lifecycle_probe_exit: bool,
    print_placement: bool,
    stream_delay_ms: u64,
}

impl MiniMode {
    fn from_args() -> Self {
        let args = std::env::args().collect::<Vec<_>>();
        Self::from_args_values(&args)
    }

    fn from_args_values(args: &[String]) -> Self {
        let auto_stream_exit = args.iter().any(|arg| arg == "--auto-stream-exit");
        let auto_stream_stay = args.iter().any(|arg| arg == "--auto-stream-stay");
        let hotkey_probe_exit = args.iter().any(|arg| arg == "--hotkey-probe-exit");
        let lifecycle_probe_exit = args.iter().any(|arg| arg == "--lifecycle-probe-exit");
        Self {
            auto_stream: auto_stream_exit || auto_stream_stay,
            auto_stream_exit,
            hotkey_enabled: hotkey_probe_exit
                || lifecycle_probe_exit
                || args.iter().any(|arg| arg == "--hotkey"),
            hotkey_probe_exit,
            lifecycle_probe_exit,
            print_placement: args.iter().any(|arg| arg == "--print-placement"),
            stream_delay_ms: stream_delay_ms(&args),
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mini_view_uses_streaming_state_tokens() {
        let view = build_view("Text", "Partial", true);
        let snapshot = win_fluent_testkit::view_snapshot(&view);

        assert!(snapshot.contains("Page title=\"Mini Window\""));
        assert!(snapshot.contains("max_height=56"));
        assert!(snapshot.contains("status=Streaming"));
        assert!(snapshot.contains("label=\"Streaming\""));
    }

    #[test]
    fn mini_mode_can_auto_stream_without_exiting() {
        let mode = MiniMode {
            auto_stream: true,
            auto_stream_exit: false,
            hotkey_enabled: false,
            hotkey_probe_exit: false,
            lifecycle_probe_exit: false,
            print_placement: false,
            stream_delay_ms: 250,
        };

        let (state, _task) = MiniState::new(mode);

        assert!(state.streaming);
        assert!(!state.exit_after_stream);
        assert_eq!(state.generation, 1);
        assert_eq!(state.stream_delay_ms, 250);
    }

    #[test]
    fn hotkey_probe_mode_enables_hotkey_without_initial_stream() {
        let mode =
            MiniMode::from_args_values(&["demo".to_string(), "--hotkey-probe-exit".to_string()]);

        let (state, _task) = MiniState::new(mode);

        assert!(!state.streaming);
        assert!(state.hotkey_enabled);
        assert!(state.exit_after_hotkey_stream);
        assert_eq!(state.hotkey_count, 0);
    }

    #[test]
    fn lifecycle_probe_mode_enables_hotkey_and_exit_checks() {
        let mode =
            MiniMode::from_args_values(&["demo".to_string(), "--lifecycle-probe-exit".to_string()]);

        let (state, _task) = MiniState::new(mode);

        assert!(!state.streaming);
        assert!(state.hotkey_enabled);
        assert!(state.exit_after_hotkey_stream);
        assert!(state.lifecycle_probe_exit);
        assert!(!state.lifecycle_minimized_seen);
        assert!(!state.lifecycle_restored_seen);
    }

    #[test]
    fn lifecycle_minimized_and_restored_states_are_recorded() {
        let mode = MiniMode {
            auto_stream: false,
            auto_stream_exit: false,
            hotkey_enabled: true,
            hotkey_probe_exit: false,
            lifecycle_probe_exit: true,
            print_placement: false,
            stream_delay_ms: 1,
        };
        let (mut state, _task) = MiniState::new(mode);

        let _task = update(
            &mut state,
            MiniMessage::LifecycleMinimizedState {
                phase: LifecyclePhase::Minimized,
                minimized: Some(true),
            },
        );
        assert!(state.lifecycle_minimized_seen);

        let _task = update(
            &mut state,
            MiniMessage::LifecycleMinimizedState {
                phase: LifecyclePhase::Restored,
                minimized: Some(false),
            },
        );
        assert!(state.lifecycle_restored_seen);
    }

    #[test]
    fn hotkey_event_starts_streaming_translation() {
        let mode = MiniMode {
            auto_stream: false,
            auto_stream_exit: false,
            hotkey_enabled: true,
            hotkey_probe_exit: false,
            lifecycle_probe_exit: false,
            print_placement: false,
            stream_delay_ms: 1,
        };
        let (mut state, _task) = MiniState::new(mode);

        let _task = update(
            &mut state,
            MiniMessage::HotkeyPressed("mini.translate".to_string()),
        );

        assert_eq!(state.hotkey_count, 1);
        assert!(state.streaming);
        assert_eq!(state.generation, 1);
        assert!(state.result.is_empty());
    }

    #[test]
    fn stream_delay_argument_is_bounded() {
        assert_eq!(
            stream_delay_ms(&["demo".to_string(), "--stream-delay-ms=500".to_string()]),
            500
        );
        assert_eq!(
            stream_delay_ms(&["demo".to_string(), "--stream-delay-ms=0".to_string()]),
            90
        );
        assert_eq!(
            stream_delay_ms(&["demo".to_string(), "--stream-delay-ms=6000".to_string()]),
            90
        );
    }

    #[test]
    fn mini_window_options_map_to_topmost_iced_settings() {
        let settings = IcedAdapter::window_settings(&mini_window_options());

        assert_eq!(settings.size, iced::Size::new(420.0, 360.0));
        assert_eq!(settings.level, iced::window::Level::AlwaysOnTop);
        assert!(settings.transparent);
        assert!(!settings.decorations);

        #[cfg(windows)]
        assert!(settings.platform_specific.skip_taskbar);
    }

    #[cfg(windows)]
    #[test]
    fn mini_window_settings_apply_resolved_windows_position() {
        let (settings, report) = mini_window_settings(&mini_window_options());

        assert!(report.starts_with("PLACEMENT width=420 height=360"));
        match settings.position {
            iced::window::Position::Specific(point) => {
                assert!(point.x.is_finite());
                assert!(point.y.is_finite());
            }
            position => panic!("expected resolved Mini Window position, got {position:?}"),
        }
    }
}
