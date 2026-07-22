#[cfg(feature = "parity-diagnostics")]
use std::{collections::BTreeMap, time::Instant};
use std::{fs, path::Path, time::Duration};

#[cfg(feature = "parity-diagnostics")]
pub mod control;
use easydict_app::{
    capture_overlay_window_options, default_settings_storage_path, default_ui_language,
    fixed_window_options, load_settings_file, main_window_options_for_settings,
    mini_window_options, pop_button_view_with_state, pop_button_window_options,
    preview_control_state_from_id, settings_window_options, EasydictApp, EasydictUiState, Message,
    SettingsState, MAIN_WINDOW_DEFAULT_HEIGHT_DIPS, MAIN_WINDOW_DEFAULT_WIDTH_DIPS,
    MAIN_WINDOW_MIN_HEIGHT_DIPS, MAIN_WINDOW_MIN_WIDTH_DIPS, SETTINGS_WINDOW_DEFAULT_HEIGHT_DIPS,
    SETTINGS_WINDOW_DEFAULT_WIDTH_DIPS, SETTINGS_WINDOW_MIN_HEIGHT_DIPS,
    SETTINGS_WINDOW_MIN_WIDTH_DIPS,
};
use win_fluent::prelude::*;

fn main() {
    let preview_mode = preview_mode_requested();
    let initial_state = initial_state_for_mode(preview_mode);
    if preview_mode {
        dump_preview_schema_on_large_stack(initial_state.clone());
    }
    let window_options = initial_window_options(preview_mode, &initial_state);

    win_fluent_backend_iced::run_single_window_application::<PreviewApp>(
        initial_state,
        window_options,
    )
    .expect("Easydict Rust runtime failed");
}

fn preview_mode_requested() -> bool {
    let executable = std::env::current_exe().ok();
    preview_mode_requested_for_executable(
        executable.as_deref(),
        std::env::vars().map(|(name, _)| name),
    )
}

fn preview_mode_requested_for_executable<I, S>(executable: Option<&Path>, names: I) -> bool
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    if is_packaged_production_alias(executable) {
        return false;
    }

    preview_mode_requested_from_names(names)
}

fn is_packaged_production_alias(executable: Option<&Path>) -> bool {
    executable
        .and_then(Path::file_stem)
        .and_then(|stem| stem.to_str())
        .is_some_and(|stem| stem.eq_ignore_ascii_case("Easydict.Rust"))
}

fn preview_mode_requested_from_names<I, S>(names: I) -> bool
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    names
        .into_iter()
        .any(|name| name.as_ref().starts_with("EASYDICT_PREVIEW_"))
}

fn initial_state_for_mode(preview_mode: bool) -> EasydictUiState {
    if preview_mode {
        EasydictUiState::preview_from_env()
    } else {
        production_initial_state()
    }
}

fn production_initial_state() -> EasydictUiState {
    let settings = load_settings_file(default_settings_storage_path())
        .ok()
        .map(|loaded| loaded.settings);
    production_initial_state_with_settings(settings)
}

fn production_initial_state_with_settings(settings: Option<SettingsState>) -> EasydictUiState {
    let mut state = EasydictUiState::default();
    if let Some(settings) = settings {
        state.settings = settings;
    } else {
        state.settings.ui_language = default_ui_language();
    }

    state.source_text.clear();
    state.detected_language = None;
    state.services_completed = 0;
    state.results.clear();
    state.long_document.history.clear();
    state.mini.text.clear();
    state.mini.services_completed = 0;
    state.mini.results.clear();
    state.fixed.text.clear();
    state.fixed.services_completed = 0;
    state.fixed.results.clear();
    // Rebuild the result rows from the loaded enabled-service settings; otherwise
    // the lists stay empty and the app reports "No translation services are
    // enabled" even though services are enabled in the persisted settings.
    state.sync_window_service_results();
    state.settings.unsaved_changes = false;
    state.settings.show_unsaved_changes_dialog = false;
    state.saved_settings = state.settings.clone();
    state.saved_settings.unsaved_changes = false;
    state.saved_settings.show_unsaved_changes_dialog = false;
    state
}

fn initial_window_options(preview_mode: bool, state: &EasydictUiState) -> WindowOptions {
    if preview_mode {
        preview_window_options()
    } else {
        main_window_options_for_settings(&state.settings)
    }
}

fn preview_window_options() -> WindowOptions {
    let window_id = preview_window_id();
    match window_id.as_str() {
        "settings" => return settings_window_options(),
        "mini" => return floating_preview_window_options(mini_window_options()),
        "fixed" => return floating_preview_window_options(fixed_window_options()),
        "capture-overlay" => return capture_overlay_window_options(),
        "pop-button" => return pop_button_window_options(),
        _ => {}
    }

    let settings_preview = preview_settings_open();
    let (default_width, default_height) = if settings_preview {
        (
            SETTINGS_WINDOW_DEFAULT_WIDTH_DIPS,
            SETTINGS_WINDOW_DEFAULT_HEIGHT_DIPS,
        )
    } else {
        (
            MAIN_WINDOW_DEFAULT_WIDTH_DIPS,
            MAIN_WINDOW_DEFAULT_HEIGHT_DIPS,
        )
    };
    let width = preview_env_f32("EASYDICT_PREVIEW_WIDTH_DIPS").unwrap_or(default_width);
    let height = preview_env_f32("EASYDICT_PREVIEW_HEIGHT_DIPS").unwrap_or(default_height);
    let min_width = if settings_preview {
        SETTINGS_WINDOW_MIN_WIDTH_DIPS
    } else {
        MAIN_WINDOW_MIN_WIDTH_DIPS
    };
    let min_height = if settings_preview {
        SETTINGS_WINDOW_MIN_HEIGHT_DIPS
    } else {
        MAIN_WINDOW_MIN_HEIGHT_DIPS
    };
    let title = if settings_preview {
        "Easydict Settings"
    } else {
        "Easydict Rust Main Window Preview"
    };

    WindowOptions::new(window_id, title)
        .size(width, height)
        .min_size(min_width, min_height)
        .frame(WindowFrame::Borderless)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::Explicit { x: 40.0, y: 20.0 })
}

fn floating_preview_window_options(options: WindowOptions) -> WindowOptions {
    let width = preview_env_f32("EASYDICT_PREVIEW_WIDTH_DIPS").unwrap_or(options.width);
    let height = preview_env_f32("EASYDICT_PREVIEW_HEIGHT_DIPS").unwrap_or(options.height);
    options.size(width, height)
}

fn preview_window_id() -> String {
    std::env::var("EASYDICT_PREVIEW_WINDOW")
        .ok()
        .map(|value| match value.trim().to_ascii_lowercase().as_str() {
            "settings" => "settings".to_string(),
            "mini" => "mini".to_string(),
            "fixed" => "fixed".to_string(),
            "capture" | "capture-overlay" | "ocr" | "ocr-overlay" => "capture-overlay".to_string(),
            "popbutton" | "pop-button" => "pop-button".to_string(),
            _ => "main".to_string(),
        })
        .unwrap_or_else(|| {
            if preview_settings_open() {
                "settings".to_string()
            } else {
                "main".to_string()
            }
        })
}

fn preview_settings_open() -> bool {
    std::env::var("EASYDICT_PREVIEW_SETTINGS_OPEN")
        .ok()
        .is_some_and(|value| preview_env_truthy(&value))
}

fn preview_env_truthy(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "1" | "true" | "yes" | "on"
    )
}

fn preview_env_f32(name: &str) -> Option<f32> {
    std::env::var(name)
        .ok()
        .and_then(|value| value.trim().parse::<f32>().ok())
        .filter(|value| value.is_finite() && *value > 0.0)
}

fn pop_button_preview_state() -> ControlState {
    std::env::var("EASYDICT_PREVIEW_POPBUTTON_STATE")
        .ok()
        .map(|value| preview_control_state_from_id(&value))
        .unwrap_or_default()
}

#[derive(Clone, Debug)]
struct PreviewScroll {
    target_id: String,
    y: f32,
    remaining_retries: u8,
    retry_delay_ms: u64,
}

fn preview_scroll_from_env() -> Option<PreviewScroll> {
    let raw_percent = std::env::var("EASYDICT_PREVIEW_SCROLL_PERCENT").ok()?;
    let y = raw_percent
        .trim()
        .parse::<f32>()
        .ok()
        .map(|value| if value > 1.0 { value / 100.0 } else { value })?
        .clamp(0.0, 1.0);
    let target_id = std::env::var("EASYDICT_PREVIEW_SCROLL_TARGET")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| "MainScrollViewer".to_string());
    let remaining_retries = std::env::var("EASYDICT_PREVIEW_SCROLL_RETRY_COUNT")
        .ok()
        .and_then(|value| value.parse::<u8>().ok())
        .unwrap_or(2);
    let retry_delay_ms = std::env::var("EASYDICT_PREVIEW_SCROLL_RETRY_DELAY_MS")
        .ok()
        .and_then(|value| value.parse::<u64>().ok())
        .unwrap_or(450);

    Some(PreviewScroll {
        target_id,
        y,
        remaining_retries,
        retry_delay_ms,
    })
}

fn delayed_preview_scroll_ready(delay_ms: u64) -> Task<Message> {
    Task::perform(
        async move {
            std::thread::sleep(Duration::from_millis(delay_ms));
        },
        |_| Message::PreviewScrollReady,
    )
}

#[cfg(feature = "parity-diagnostics")]
const PREVIEW_CONTROL_POLL_INTERVAL: Duration = Duration::from_millis(20);
#[cfg(feature = "parity-diagnostics")]
const PREVIEW_CONTROL_RENDER_TIMEOUT: Duration = Duration::from_secs(10);

#[cfg(feature = "parity-diagnostics")]
struct PendingRender {
    request: control::PreviewControlRequest,
    schema: String,
    started: Instant,
    writing: bool,
}

#[cfg(feature = "parity-diagnostics")]
struct PreviewControlRuntime {
    launch: control::ControlLaunchSettings,
    state: control::PreviewControlState,
    startup_environment: BTreeMap<String, String>,
    pending: Option<PendingRender>,
    width: f32,
    height: f32,
}

struct PreviewApp {
    inner: EasydictApp,
    pending_scroll: Option<PreviewScroll>,
    preview_mode: bool,
    #[cfg(feature = "parity-diagnostics")]
    control: Option<PreviewControlRuntime>,
}

#[cfg(feature = "parity-diagnostics")]
fn delayed_control_message(generation: u64, timed_out: bool) -> Task<Message> {
    Task::perform(
        async move {
            if !timed_out {
                std::thread::sleep(PREVIEW_CONTROL_POLL_INTERVAL);
            }
        },
        move |_| {
            if timed_out {
                Message::PreviewControlTimedOut(generation)
            } else {
                Message::PreviewControlArtifactsWritten(generation)
            }
        },
    )
}

#[cfg(feature = "parity-diagnostics")]
fn write_error_ack_task(
    launch: control::ControlLaunchSettings,
    generation: u64,
    error: control::ControlError,
) -> Task<Message> {
    Task::perform(
        async move {
            let ack = control::PreviewControlAck::error(
                launch.session_id,
                generation,
                error.code,
                error.message,
            );
            if let Err(error) = control::write_ack(&launch.ack_path, &ack) {
                eprintln!("Failed to write preview control acknowledgement: {error}");
            }
        },
        move |_| Message::PreviewControlArtifactsWritten(generation),
    )
}

#[cfg(feature = "parity-diagnostics")]
fn write_generation_task(
    launch: control::ControlLaunchSettings,
    request: control::PreviewControlRequest,
    schema: String,
    generation: win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration,
    render_duration_ms: u64,
    failure: Option<(&'static str, String)>,
) -> Task<Message> {
    let request_generation = request.generation;
    Task::perform(
        async move {
            let diagnostics = serde_json::to_string_pretty(&generation);
            let artifact_paths = diagnostics
                .as_deref()
                .map_err(|error| error.to_string())
                .and_then(|diagnostics| {
                    control::write_runtime_artifacts(
                        &launch.output_root,
                        &request.artifact_stem,
                        &schema,
                        &control::bounds_snapshot(&generation),
                        diagnostics,
                    )
                    .map_err(|error| error.to_string())
                });
            let mut ack = match (artifact_paths, failure) {
                (Ok(paths), None) => control::PreviewControlAck::rendered(
                    launch.session_id.clone(),
                    request.generation,
                    paths,
                    generation.observed_control_ids.clone(),
                    generation.missing_control_ids.clone(),
                    render_duration_ms,
                ),
                (Ok(paths), Some((code, message))) => {
                    let mut ack = control::PreviewControlAck::error(
                        launch.session_id.clone(),
                        request.generation,
                        code,
                        message,
                    );
                    ack.artifact_paths = paths;
                    ack.observed_control_ids = generation.observed_control_ids.clone();
                    ack.missing_control_ids = generation.missing_control_ids.clone();
                    ack.render_duration_ms = Some(render_duration_ms);
                    ack
                }
                (Err(message), _) => control::PreviewControlAck::error(
                    launch.session_id.clone(),
                    request.generation,
                    control::ERR_INVALID_SESSION,
                    message,
                ),
            };
            ack.schema = control::CONTROL_ACK_SCHEMA.to_string();
            if let Err(error) = control::write_ack(&launch.ack_path, &ack) {
                eprintln!("Failed to write preview control acknowledgement: {error}");
            }
        },
        move |_| Message::PreviewControlArtifactsWritten(request_generation),
    )
}

#[cfg(feature = "parity-diagnostics")]
fn measured_client_dimensions(
    generation: &win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration,
) -> Option<(f32, f32)> {
    let root_id = format!("{}.window", generation.window_id);
    generation
        .controls
        .iter()
        .find(|control| control.id == root_id)
        .map(|root| (root.width, root.height))
}

#[cfg(feature = "parity-diagnostics")]
fn generation_required_control_ids(
    window: &WindowId,
    request: &control::PreviewControlRequest,
) -> Vec<String> {
    let mut required = request.required_control_ids.clone();
    if request.width_dips.is_some() || request.height_dips.is_some() {
        let root_id = format!("{}.window", window.as_str());
        if !required.contains(&root_id) {
            required.push(root_id);
        }
    }
    required
}

#[cfg(feature = "parity-diagnostics")]
fn missing_required_source_facts(
    window: &WindowId,
    request: &control::PreviewControlRequest,
    generation: &win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration,
) -> Vec<String> {
    generation_required_control_ids(window, request)
        .into_iter()
        .filter(|required_id| {
            !generation
                .controls
                .iter()
                .any(|control| control.id == *required_id && control.has_source_facts())
        })
        .collect()
}

#[cfg(feature = "parity-diagnostics")]
fn dimension_mismatch_message(
    request: &control::PreviewControlRequest,
    generation: &win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration,
) -> String {
    let requested_width = request
        .width_dips
        .map_or_else(|| "unchanged".to_string(), |value| format!("{value:.2}"));
    let requested_height = request
        .height_dips
        .map_or_else(|| "unchanged".to_string(), |value| format!("{value:.2}"));
    let measured = measured_client_dimensions(generation).map_or_else(
        || "unavailable".to_string(),
        |(width, height)| format!("{width:.2}x{height:.2}"),
    );
    format!(
        "requested client size {requested_width}x{requested_height} DIP; measured client size {measured} DIP; required tolerance is 1 DIP"
    )
}

#[cfg(feature = "parity-diagnostics")]
#[derive(Debug, Eq, PartialEq)]
enum GenerationSizeGate {
    Ready,
    Retry,
    TimedOut(String),
}

#[cfg(feature = "parity-diagnostics")]
fn generation_size_gate(
    request: &control::PreviewControlRequest,
    generation: &win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration,
    timed_out: bool,
) -> GenerationSizeGate {
    if requested_dimensions_match(request, generation) {
        GenerationSizeGate::Ready
    } else if timed_out {
        GenerationSizeGate::TimedOut(dimension_mismatch_message(request, generation))
    } else {
        GenerationSizeGate::Retry
    }
}

#[cfg(feature = "parity-diagnostics")]
fn requested_dimensions_match(
    request: &control::PreviewControlRequest,
    generation: &win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration,
) -> bool {
    let root_id = format!("{}.window", generation.window_id);
    let Some(root) = generation
        .controls
        .iter()
        .find(|control| control.id == root_id)
    else {
        return request.width_dips.is_none() && request.height_dips.is_none();
    };
    request
        .width_dips
        .is_none_or(|width| (root.width - width).abs() <= 1.0)
        && request
            .height_dips
            .is_none_or(|height| (root.height - height).abs() <= 1.0)
}

impl PreviewApp {
    #[cfg(feature = "parity-diagnostics")]
    fn handle_control_signal(&mut self) -> Task<Message> {
        let Some(runtime) = self.control.as_mut() else {
            return Task::none();
        };
        let request = match control::read_request(&runtime.launch.request_path) {
            Ok(request) => request,
            Err(error) => {
                return write_error_ack_task(
                    runtime.launch.clone(),
                    runtime.state.last_generation().saturating_add(1),
                    error,
                );
            }
        };
        let generation = request.generation;
        let mut environment =
            match control::request_environment(&runtime.startup_environment, &request) {
                Ok(environment) => environment,
                Err(error) => {
                    return write_error_ack_task(runtime.launch.clone(), generation, error);
                }
            };
        if let Some(width) = request.width_dips {
            environment.insert("EASYDICT_PREVIEW_WIDTH_DIPS".to_string(), width.to_string());
        }
        if let Some(height) = request.height_dips {
            environment.insert(
                "EASYDICT_PREVIEW_HEIGHT_DIPS".to_string(),
                height.to_string(),
            );
        }
        if let Err(error) = runtime
            .state
            .validate_and_accept(&request, &runtime.launch.output_root)
        {
            return write_error_ack_task(runtime.launch.clone(), generation, error);
        }
        if request.command == "shutdown" {
            win_fluent_backend_iced::runtime_diagnostics::clear();
            return Task::exit();
        }

        let width = request.width_dips.unwrap_or(runtime.width);
        let height = request.height_dips.unwrap_or(runtime.height);
        runtime.width = width;
        runtime.height = height;
        self.inner = EasydictApp {
            state: EasydictUiState::preview_from_lookup(|name| environment.get(name).cloned()),
        };
        let window = WindowId::new(preview_window_id());
        let schema = preview_generation_schema_on_large_stack(
            self.inner.state.clone(),
            window.as_str().to_string(),
            generation,
        );
        self.control
            .as_mut()
            .expect("control runtime exists")
            .pending = Some(PendingRender {
            request: request.clone(),
            schema,
            started: Instant::now(),
            writing: false,
        });
        let required_control_ids = generation_required_control_ids(&window, &request);
        win_fluent_backend_iced::runtime_diagnostics::begin_generation(
            window.as_str(),
            generation,
            required_control_ids,
        );
        Task::batch([
            Task::window(WindowCommand::Resize {
                id: window,
                width,
                height,
            }),
            delayed_control_message(generation, false),
        ])
    }

    #[cfg(feature = "parity-diagnostics")]
    fn poll_control_generation(&mut self, generation: u64) -> Task<Message> {
        let Some(runtime) = self.control.as_mut() else {
            return Task::none();
        };
        let Some(pending) = runtime.pending.as_mut() else {
            return Task::none();
        };
        if pending.request.generation != generation {
            return Task::none();
        }
        if pending.writing {
            runtime.pending = None;
            return Task::none();
        }
        if let Some(completed) =
            win_fluent_backend_iced::runtime_diagnostics::take_completed(generation)
        {
            let window = WindowId::new(preview_window_id());
            let timed_out = pending.started.elapsed() >= PREVIEW_CONTROL_RENDER_TIMEOUT;
            let missing_source_facts =
                missing_required_source_facts(&window, &pending.request, &completed);
            let failure = if missing_source_facts.is_empty() {
                match generation_size_gate(&pending.request, &completed, timed_out) {
                    GenerationSizeGate::Ready => None,
                    GenerationSizeGate::TimedOut(message) => {
                        Some((control::ERR_RENDER_TIMEOUT, message))
                    }
                    GenerationSizeGate::Retry => {
                        win_fluent_backend_iced::runtime_diagnostics::begin_generation(
                            window.as_str(),
                            generation,
                            generation_required_control_ids(&window, &pending.request),
                        );
                        return delayed_control_message(generation, false);
                    }
                }
            } else if timed_out {
                Some((
                    control::ERR_RENDER_TIMEOUT,
                    format!(
                        "required controls did not settle with structural provenance: {}",
                        missing_source_facts.join(", ")
                    ),
                ))
            } else {
                win_fluent_backend_iced::runtime_diagnostics::begin_generation(
                    window.as_str(),
                    generation,
                    generation_required_control_ids(&window, &pending.request),
                );
                return delayed_control_message(generation, false);
            };
            pending.writing = true;
            return write_generation_task(
                runtime.launch.clone(),
                pending.request.clone(),
                pending.schema.clone(),
                completed,
                pending.started.elapsed().as_millis() as u64,
                failure,
            );
        }
        if pending.started.elapsed() >= PREVIEW_CONTROL_RENDER_TIMEOUT {
            return delayed_control_message(generation, true);
        }
        delayed_control_message(generation, false)
    }

    #[cfg(feature = "parity-diagnostics")]
    fn time_out_control_generation(&mut self, generation: u64) -> Task<Message> {
        let Some(runtime) = self.control.as_mut() else {
            return Task::none();
        };
        let Some(pending) = runtime.pending.as_mut() else {
            return Task::none();
        };
        if pending.request.generation != generation || pending.writing {
            return Task::none();
        }
        let partial = win_fluent_backend_iced::runtime_diagnostics::take_active(generation)
            .unwrap_or_else(|| {
                win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration {
                    schema: control::RUNTIME_DIAGNOSTICS_SCHEMA.to_string(),
                    window_id: preview_window_id(),
                    generation,
                    controls: Vec::new(),
                    observed_control_ids: Vec::new(),
                    missing_control_ids: pending.request.required_control_ids.clone(),
                }
            });
        let failure = if partial.missing_control_ids.is_empty() {
            (
                control::ERR_RENDER_TIMEOUT,
                "preview generation did not complete before the internal timeout".to_string(),
            )
        } else {
            (
                control::ERR_MISSING_REQUIRED_CONTROL,
                format!(
                    "preview generation did not render required controls: {}",
                    partial.missing_control_ids.join(", ")
                ),
            )
        };
        pending.writing = true;
        write_generation_task(
            runtime.launch.clone(),
            pending.request.clone(),
            pending.schema.clone(),
            partial,
            pending.started.elapsed().as_millis() as u64,
            Some(failure),
        )
    }
}

impl Application for PreviewApp {
    type Message = Message;
    type Flags = EasydictUiState;

    fn new(flags: Self::Flags) -> (Self, Task<Self::Message>) {
        let preview_mode = preview_mode_requested();
        let (inner, initial_task) = if preview_mode {
            (EasydictApp { state: flags }, Task::none())
        } else {
            EasydictApp::new(flags)
        };

        let auto_toggle_task = if preview_mode {
            std::env::var("EASYDICT_PREVIEW_AUTO_TOGGLE_RESULT")
                .ok()
                .map(|id| {
                    let delay_ms = std::env::var("EASYDICT_PREVIEW_AUTO_TOGGLE_DELAY_MS")
                        .ok()
                        .and_then(|value| value.parse::<u64>().ok())
                        .unwrap_or(650);
                    Task::perform(
                        async move {
                            std::thread::sleep(Duration::from_millis(delay_ms));
                            id
                        },
                        Message::ToggleResultExpanded,
                    )
                })
                .unwrap_or_else(Task::none)
        } else {
            Task::none()
        };

        let pending_scroll = preview_mode.then(preview_scroll_from_env).flatten();
        let preview_scroll_task = if pending_scroll.is_some() {
            let delay_ms = std::env::var("EASYDICT_PREVIEW_SCROLL_DELAY_MS")
                .ok()
                .and_then(|value| value.parse::<u64>().ok())
                .unwrap_or(900);
            Task::perform(
                async move {
                    std::thread::sleep(Duration::from_millis(delay_ms));
                },
                |_| Message::PreviewScrollReady,
            )
        } else {
            Task::none()
        };

        #[cfg(feature = "parity-diagnostics")]
        let control = {
            let startup_environment = std::env::vars().collect::<BTreeMap<_, _>>();
            match control::ControlLaunchSettings::from_lookup(|name| {
                startup_environment.get(name).cloned()
            }) {
                Ok(Some(launch)) => {
                    let options = preview_window_options();
                    Some(PreviewControlRuntime {
                        state: control::PreviewControlState::new(launch.session_id.clone()),
                        launch,
                        startup_environment,
                        pending: None,
                        width: options.width,
                        height: options.height,
                    })
                }
                Ok(None) => None,
                Err(error) => {
                    eprintln!("Invalid preview control launch settings: {error}");
                    None
                }
            }
        };

        let startup_tasks = Task::batch([initial_task, auto_toggle_task, preview_scroll_task]);

        (
            Self {
                inner,
                pending_scroll,
                preview_mode,
                #[cfg(feature = "parity-diagnostics")]
                control,
            },
            startup_tasks,
        )
    }

    fn title(&self, window: &WindowId) -> String {
        self.inner.title(window)
    }

    fn view(&self, window: &WindowId) -> View<Self::Message> {
        if self.preview_mode && window.as_str() == "pop-button" {
            return pop_button_view_with_state(pop_button_preview_state());
        }

        self.inner.view(window)
    }

    fn update(&mut self, message: Self::Message) -> Task<Self::Message> {
        #[cfg(feature = "parity-diagnostics")]
        match message {
            Message::PreviewControlSignaled => return self.handle_control_signal(),
            Message::PreviewControlArtifactsWritten(generation) => {
                return self.poll_control_generation(generation);
            }
            Message::PreviewControlTimedOut(generation) => {
                return self.time_out_control_generation(generation);
            }
            _ => {}
        }

        let is_preview_scroll_ready = message == Message::PreviewScrollReady;
        if is_preview_scroll_ready {
            if let Some(scroll) = self.pending_scroll.as_mut() {
                let task = Task::scroll_to(scroll.target_id.clone(), 0.0, scroll.y);
                if scroll.remaining_retries == 0 {
                    self.pending_scroll = None;
                    return task;
                }

                scroll.remaining_retries -= 1;
                return Task::batch([task, delayed_preview_scroll_ready(scroll.retry_delay_ms)]);
            }

            return Task::none();
        }

        self.inner.update(message)
    }

    fn window_options(&self, window: &WindowId) -> Option<WindowOptions> {
        let options = self.inner.window_options(window)?;
        if self.preview_mode && matches!(window.as_str(), "mini" | "fixed") {
            Some(floating_preview_window_options(options))
        } else {
            Some(options)
        }
    }

    fn subscription(&self) -> Subscription<Self::Message> {
        self.inner.subscription()
    }

    fn tray_menu(&self) -> Option<TrayMenu<Self::Message>> {
        self.inner.tray_menu()
    }

    fn named_events(&self) -> Vec<NamedEventRegistration<Self::Message>> {
        let mut events = self.inner.named_events();
        #[cfg(feature = "parity-diagnostics")]
        if let Some(control) = &self.control {
            events.push(
                NamedEventRegistration::new(control.launch.event_name.clone())
                    .on_signal(Message::PreviewControlSignaled),
            );
        }
        events
    }

    fn shell_verbs(&self) -> Vec<ShellVerb> {
        self.inner.shell_verbs()
    }

    fn protocol_registrations(&self) -> Vec<ProtocolRegistration> {
        self.inner.protocol_registrations()
    }

    fn theme(&self) -> ThemeMode {
        self.inner.theme()
    }

    fn theme_tokens(&self) -> ThemeTokens {
        self.inner.theme_tokens()
    }
}

fn preview_schema_snapshot_on_large_stack(state: EasydictUiState, window_id: String) -> String {
    std::thread::Builder::new()
        .name("easydict-preview-schema".to_string())
        .stack_size(8 * 1024 * 1024)
        .spawn(move || {
            let app = EasydictApp { state };
            let window_id = WindowId::new(window_id);
            let view = if window_id.as_str() == "pop-button" {
                pop_button_view_with_state(pop_button_preview_state())
            } else {
                app.view(&window_id)
            };
            view_schema(&view).snapshot()
        })
        .expect("failed to spawn Easydict preview schema thread")
        .join()
        .expect("Easydict preview schema thread panicked")
}

#[cfg(feature = "parity-diagnostics")]
fn preview_generation_schema_on_large_stack(
    state: EasydictUiState,
    window_id: String,
    generation: u64,
) -> String {
    std::thread::Builder::new()
        .name("easydict-preview-generation-schema".to_string())
        .stack_size(8 * 1024 * 1024)
        .spawn(move || {
            let app = EasydictApp { state };
            let window_id = WindowId::new(window_id);
            let view = if window_id.as_str() == "pop-button" {
                pop_button_view_with_state(pop_button_preview_state())
            } else {
                app.view(&window_id)
            };
            win_fluent_backend_iced::runtime_diagnostics::prepare_generation_view(
                generation, &view,
            );
            view_schema(&view).snapshot()
        })
        .expect("failed to spawn Easydict preview generation schema thread")
        .join()
        .expect("Easydict preview generation schema thread panicked")
}

fn dump_preview_schema_on_large_stack(state: EasydictUiState) {
    let Ok(path) = std::env::var("EASYDICT_PREVIEW_SCHEMA_PATH") else {
        return;
    };
    let schema = preview_schema_snapshot_on_large_stack(state, preview_window_id());
    if let Some(parent) = Path::new(&path).parent() {
        if let Err(error) = fs::create_dir_all(parent) {
            eprintln!("Failed to create preview schema directory: {error}");
            return;
        }
    }
    if let Err(error) = fs::write(&path, schema) {
        eprintln!("Failed to write preview schema to {path}: {error}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    static ENV_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    fn preview_app_forwards_easydict_theme_tokens() {
        let state =
            EasydictUiState::preview(easydict_app::PreviewScenario::Initial, ThemeMode::Light);
        let (app, _) = PreviewApp::new(state);

        assert_eq!(
            app.theme_tokens(),
            easydict_app::easydict_theme_tokens(ThemeMode::Light)
        );
    }

    #[test]
    fn preview_scroll_waits_for_dedicated_ready_message() {
        let state =
            EasydictUiState::preview(easydict_app::PreviewScenario::Initial, ThemeMode::Light);
        let mut app = PreviewApp {
            inner: EasydictApp { state },
            pending_scroll: Some(PreviewScroll {
                target_id: "MainScrollViewer".to_string(),
                y: 0.5,
                remaining_retries: 0,
                retry_delay_ms: 1,
            }),
            preview_mode: true,
            #[cfg(feature = "parity-diagnostics")]
            control: None,
        };

        assert!(matches!(app.update(Message::Noop), Task::None));
        assert!(app.pending_scroll.is_some());

        let task = app.update(Message::PreviewScrollReady);

        assert!(app.pending_scroll.is_none());
        let Task::ScrollTo { id, x, y } = task else {
            panic!("expected preview scroll task");
        };
        assert_eq!(id, "MainScrollViewer");
        assert_eq!(x, 0.0);
        assert_eq!(y, 0.5);
    }

    #[test]
    fn preview_scroll_retries_to_survive_late_scroll_layout() {
        let state =
            EasydictUiState::preview(easydict_app::PreviewScenario::Initial, ThemeMode::Light);
        let mut app = PreviewApp {
            inner: EasydictApp { state },
            pending_scroll: Some(PreviewScroll {
                target_id: "MainScrollViewer".to_string(),
                y: 0.75,
                remaining_retries: 1,
                retry_delay_ms: 1,
            }),
            preview_mode: true,
            #[cfg(feature = "parity-diagnostics")]
            control: None,
        };

        let first = app.update(Message::PreviewScrollReady);

        assert!(app.pending_scroll.is_some());
        let Task::Batch(first_batch) = first else {
            panic!("expected scroll retry batch");
        };
        assert!(first_batch.iter().any(|task| matches!(
            task,
            Task::ScrollTo { id, x, y }
                if id == "MainScrollViewer" && *x == 0.0 && *y == 0.75
        )));
        assert!(first_batch
            .iter()
            .any(|task| matches!(task, Task::Future(_))));

        let second = app.update(Message::PreviewScrollReady);

        assert!(app.pending_scroll.is_none());
        let Task::ScrollTo { id, x, y } = second else {
            panic!("expected final preview scroll task");
        };
        assert_eq!(id, "MainScrollViewer");
        assert_eq!(x, 0.0);
        assert_eq!(y, 0.75);
    }

    #[test]
    fn settings_open_preview_defaults_to_settings_window_size() {
        let _guard = ENV_LOCK.lock().expect("env lock");
        let previous_window = std::env::var("EASYDICT_PREVIEW_WINDOW").ok();
        let previous_settings_open = std::env::var("EASYDICT_PREVIEW_SETTINGS_OPEN").ok();

        std::env::remove_var("EASYDICT_PREVIEW_WINDOW");
        std::env::set_var("EASYDICT_PREVIEW_SETTINGS_OPEN", "1");

        let options = preview_window_options();

        restore_env("EASYDICT_PREVIEW_WINDOW", previous_window);
        restore_env("EASYDICT_PREVIEW_SETTINGS_OPEN", previous_settings_open);

        assert_eq!(options.id.as_str(), "settings");
        assert_eq!(options.width, SETTINGS_WINDOW_DEFAULT_WIDTH_DIPS);
        assert_eq!(options.height, SETTINGS_WINDOW_DEFAULT_HEIGHT_DIPS);
        assert_eq!(options.min_width, Some(SETTINGS_WINDOW_MIN_WIDTH_DIPS));
        assert_eq!(options.min_height, Some(SETTINGS_WINDOW_MIN_HEIGHT_DIPS));
    }

    #[test]
    fn preview_mode_detector_only_uses_preview_environment_prefix() {
        assert!(preview_mode_requested_from_names([
            "PATH",
            "EASYDICT_PREVIEW_SCENARIO"
        ]));
        assert!(!preview_mode_requested_from_names([
            "PATH",
            "EASYDICT_RUNTIME_PROFILE",
            "EASYDICT_APP_VERSION"
        ]));
    }

    #[test]
    fn preview_mode_detector_allows_preview_binary_environment() {
        assert!(preview_mode_requested_for_executable(
            Some(std::path::Path::new(
                r"C:\Easydict\tools\easydict_preview_iced.exe"
            )),
            ["PATH", "EASYDICT_PREVIEW_SCENARIO"]
        ));
    }

    #[test]
    fn preview_mode_detector_ignores_preview_environment_for_packaged_rust_alias() {
        assert!(!preview_mode_requested_for_executable(
            Some(std::path::Path::new(r"C:\Easydict\Easydict.Rust.exe")),
            ["PATH", "EASYDICT_PREVIEW_SCENARIO"]
        ));
        assert!(!preview_mode_requested_for_executable(
            Some(std::path::Path::new(r"C:\Easydict\easydict.rust.EXE")),
            ["EASYDICT_PREVIEW_WINDOW"]
        ));
    }

    #[test]
    fn preview_mode_detector_keeps_preview_fallback_when_executable_is_unknown() {
        assert!(preview_mode_requested_for_executable(
            None::<&std::path::Path>,
            ["EASYDICT_PREVIEW_SCENARIO"]
        ));
    }

    #[test]
    fn preview_app_skips_real_startup_side_effect_tasks() {
        let _guard = ENV_LOCK.lock().expect("env lock");
        let previous_scenario = std::env::var("EASYDICT_PREVIEW_SCENARIO").ok();
        let previous_toggle = std::env::var("EASYDICT_PREVIEW_AUTO_TOGGLE_RESULT").ok();
        let previous_scroll = std::env::var("EASYDICT_PREVIEW_SCROLL_PERCENT").ok();

        std::env::set_var("EASYDICT_PREVIEW_SCENARIO", "initial");
        std::env::remove_var("EASYDICT_PREVIEW_AUTO_TOGGLE_RESULT");
        std::env::remove_var("EASYDICT_PREVIEW_SCROLL_PERCENT");

        let state =
            EasydictUiState::preview(easydict_app::PreviewScenario::Initial, ThemeMode::Light);
        let (_app, task) = PreviewApp::new(state);

        restore_env("EASYDICT_PREVIEW_SCENARIO", previous_scenario);
        restore_env("EASYDICT_PREVIEW_AUTO_TOGGLE_RESULT", previous_toggle);
        restore_env("EASYDICT_PREVIEW_SCROLL_PERCENT", previous_scroll);

        assert!(
            matches!(task, Task::None),
            "preview screenshots must not start production desktop integration tasks"
        );
    }

    #[test]
    fn production_initial_state_removes_preview_demo_content_and_loads_settings() {
        let mut settings = SettingsState::default();
        settings.ui_language = "zh-CN".to_string();
        settings.monitor_clipboard = true;
        settings.unsaved_changes = true;
        settings.show_unsaved_changes_dialog = true;

        let state = production_initial_state_with_settings(Some(settings));

        assert_eq!(state.settings.ui_language, "zh-CN");
        assert!(state.settings.monitor_clipboard);
        assert!(!state.settings.unsaved_changes);
        assert!(!state.settings.show_unsaved_changes_dialog);
        assert_eq!(state.saved_settings, state.settings);
        assert!(state.source_text.is_empty());
        // Result rows are rebuilt from the enabled-service settings (pending,
        // with no demo bodies) rather than left empty — otherwise the app reports
        // "No translation services are enabled" even with services configured.
        assert!(!state.results.is_empty());
        assert!(state.results.iter().all(|result| result.body.is_empty()));
        assert!(state.long_document.history.is_empty());
        assert!(state.mini.text.is_empty());
        assert!(!state.mini.results.is_empty());
        assert!(state
            .mini
            .results
            .iter()
            .all(|result| result.body.is_empty()));
        assert!(state.fixed.text.is_empty());
        assert!(!state.fixed.results.is_empty());
        assert!(state
            .fixed
            .results
            .iter()
            .all(|result| result.body.is_empty()));
    }

    #[test]
    fn production_window_options_use_real_main_window_contract() {
        let mut settings = SettingsState::default();
        settings.minimize_to_tray = true;
        settings.start_minimized = true;
        let state = production_initial_state_with_settings(Some(settings));

        let options = initial_window_options(false, &state);

        assert_eq!(options.id.as_str(), "main");
        assert_eq!(options.title, "Easydict");
        assert_eq!(options.placement, WindowPlacement::Center);
        assert!(!options.visible_on_start);
    }

    #[test]
    fn production_runtime_plan_keeps_tray_and_real_window_surfaces_when_start_hidden() {
        let mut settings = SettingsState::default();
        settings.minimize_to_tray = true;
        settings.start_minimized = true;
        let state = production_initial_state_with_settings(Some(settings));

        let plan = RuntimePlan::<PreviewApp>::new(state);

        let tray_menu = plan
            .desktop_integration
            .tray_menu
            .as_ref()
            .expect("production tray menu should survive preview wrapper");
        assert_eq!(
            tray_menu.default_item_id.as_deref(),
            Some(easydict_app::TRAY_SHOW_MAIN)
        );
        let tray_item_ids = tray_menu
            .items
            .iter()
            .map(|item| item.id.as_str())
            .collect::<Vec<_>>();
        assert!(tray_item_ids.contains(&easydict_app::TRAY_SHOW_MAIN));
        assert!(tray_item_ids.contains(&easydict_app::TRAY_TRANSLATE_CLIPBOARD));
        assert!(tray_item_ids.contains(&easydict_app::TRAY_OCR_TRANSLATE));
        assert!(tray_item_ids.contains(&easydict_app::TRAY_OPEN_SETTINGS));
        assert!(tray_item_ids.contains(&easydict_app::TRAY_EXIT));
        assert!(tray_menu
            .items
            .iter()
            .any(|item| item.label == "Show Easydict"));

        let subscription = plan.app.subscription();
        assert!(subscription_contains_kind(&subscription, |kind| {
            matches!(kind, SubscriptionKind::Tray)
        }));
        assert!(subscription_contains_kind(&subscription, |kind| {
            matches!(kind, SubscriptionKind::Window(id) if id.as_str() == "main")
        }));

        let main_options = plan
            .app
            .window_options(&WindowId::new("main"))
            .expect("main window options");
        assert_eq!(main_options.id.as_str(), "main");
        assert!(!main_options.visible_on_start);

        let mini_options = plan
            .app
            .window_options(&WindowId::new("mini"))
            .expect("mini window options");
        assert_eq!(mini_options.id.as_str(), "mini");
        assert_eq!(mini_options.level, WindowLevel::TopMost);
        assert!(mini_options.skip_taskbar);

        let pop_button_options = plan
            .app
            .window_options(&WindowId::new("pop-button"))
            .expect("pop-button window options");
        assert_eq!(pop_button_options.id.as_str(), "pop-button");
        assert_eq!(pop_button_options.level, WindowLevel::ToolWindow);
        assert!(pop_button_options.skip_taskbar);
        assert!(pop_button_options.no_activate);
    }

    #[test]
    fn preview_window_options_still_use_preview_contract_when_requested() {
        let _guard = ENV_LOCK.lock().expect("env lock");
        let state = production_initial_state_with_settings(None);
        let options = initial_window_options(true, &state);

        assert_eq!(options.id.as_str(), "main");
        assert_eq!(options.title, "Easydict Rust Main Window Preview");
        assert!(matches!(
            options.placement,
            WindowPlacement::Explicit { x: 40.0, y: 20.0 }
        ));
    }

    #[test]
    fn floating_preview_window_options_honor_explicit_size_environment() {
        let _guard = ENV_LOCK.lock().expect("env lock");
        let previous_window = std::env::var("EASYDICT_PREVIEW_WINDOW").ok();
        let previous_width = std::env::var("EASYDICT_PREVIEW_WIDTH_DIPS").ok();
        let previous_height = std::env::var("EASYDICT_PREVIEW_HEIGHT_DIPS").ok();

        std::env::set_var("EASYDICT_PREVIEW_WINDOW", "mini");
        std::env::set_var("EASYDICT_PREVIEW_WIDTH_DIPS", "318.5");
        std::env::set_var("EASYDICT_PREVIEW_HEIGHT_DIPS", "299.25");

        let options = preview_window_options();

        assert_eq!(options.id.as_str(), "mini");
        assert_eq!(options.width, 318.5);
        assert_eq!(options.height, 299.25);
        assert_eq!(options.min_width, Some(280.0));
        assert_eq!(options.min_height, Some(200.0));

        let state =
            EasydictUiState::preview(easydict_app::PreviewScenario::Initial, ThemeMode::Light);
        let (app, _) = PreviewApp::new(state);
        let runtime_options = app
            .window_options(&WindowId::new("mini"))
            .expect("mini window options");
        assert_eq!(runtime_options.width, 318.5);
        assert_eq!(runtime_options.height, 299.25);

        restore_env("EASYDICT_PREVIEW_WINDOW", previous_window);
        restore_env("EASYDICT_PREVIEW_WIDTH_DIPS", previous_width);
        restore_env("EASYDICT_PREVIEW_HEIGHT_DIPS", previous_height);
    }

    #[cfg(feature = "parity-diagnostics")]
    #[test]
    fn first_generation_waits_for_requested_client_size_after_launch_resize() {
        let request = control::PreviewControlRequest {
            session_id: "session".to_string(),
            generation: 1,
            command: "render".to_string(),
            scenario: "main.target-language-dropdown-open".to_string(),
            artifact_stem: "main-open.g1".to_string(),
            width_dips: Some(846.0),
            height_dips: Some(913.0),
            overrides: BTreeMap::new(),
            required_control_ids: Vec::new(),
        };
        let window = WindowId::new("main");
        assert_eq!(
            generation_required_control_ids(&window, &request),
            vec!["main.window".to_string()]
        );

        let generation_at = |width, height| {
            let root = win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticNode {
                path: "main.window".to_string(),
                id: "main.window".to_string(),
                kind: "Page".to_string(),
                x: 0.0,
                y: 0.0,
                width,
                height,
                declarative_values: BTreeMap::new(),
                style_classes: Vec::new(),
                constructor_source: None,
                property_sources: BTreeMap::new(),
                resolved_values: BTreeMap::new(),
                token: None,
            };
            win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration {
                schema: control::RUNTIME_DIAGNOSTICS_SCHEMA.to_string(),
                window_id: "main".to_string(),
                generation: 1,
                controls: vec![root],
                observed_control_ids: vec!["main.window".to_string()],
                missing_control_ids: Vec::new(),
            }
        };

        let launch_size_draw = generation_at(750.0, 600.0);
        assert_eq!(
            generation_size_gate(&request, &launch_size_draw, false),
            GenerationSizeGate::Retry
        );
        let GenerationSizeGate::TimedOut(message) =
            generation_size_gate(&request, &launch_size_draw, true)
        else {
            panic!("a persistent resize mismatch must time out");
        };
        assert!(message.contains("requested client size 846.00x913.00 DIP"));
        assert!(message.contains("measured client size 750.00x600.00 DIP"));

        let resized_draw = generation_at(846.0, 913.0);
        assert_eq!(
            generation_size_gate(&request, &resized_draw, false),
            GenerationSizeGate::Ready
        );
    }

    #[cfg(feature = "parity-diagnostics")]
    #[test]
    fn first_two_generations_require_full_target_combo_provenance_before_ack() {
        let window = WindowId::new("main");
        let request = control::PreviewControlRequest {
            session_id: "session".to_string(),
            generation: 1,
            command: "render".to_string(),
            scenario: "main.target-language-dropdown-open".to_string(),
            artifact_stem: "main-open.g1".to_string(),
            width_dips: None,
            height_dips: None,
            overrides: BTreeMap::new(),
            required_control_ids: vec!["TargetLangCombo".to_string()],
        };
        let source = win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticSource {
            file: "crates\\easydict_app\\src\\ui.rs".to_string(),
            line: 2301,
            column: 9,
        };

        for generation in [1, 2] {
            let target =
                win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticNode {
                    path: "root/0:main.root/1:ModeSwitchOverlay/0:main.surface/1:QuickTranslateContent/0:QuickTranslateContent.Content/1:main.quick.action_bar/0:ActionBarWide/3:TargetLangCombo".to_string(),
                    id: "TargetLangCombo".to_string(),
                    kind: "ComboBox".to_string(),
                    x: 0.0,
                    y: 0.0,
                    width: 138.0,
                    height: 40.0,
                    declarative_values: BTreeMap::from([(
                        "width".to_string(),
                        "fixed:138".to_string(),
                    )]),
                    style_classes: Vec::new(),
                    constructor_source: Some(source.clone()),
                    property_sources: BTreeMap::from([
                        ("id".to_string(), source.clone()),
                        ("width".to_string(), source.clone()),
                    ]),
                    resolved_values: BTreeMap::new(),
                    token: None,
                };
            let diagnostics =
                win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration {
                    schema: control::RUNTIME_DIAGNOSTICS_SCHEMA.to_string(),
                    window_id: "main".to_string(),
                    generation,
                    controls: vec![target],
                    observed_control_ids: vec!["TargetLangCombo".to_string()],
                    missing_control_ids: Vec::new(),
                };

            assert!(
                missing_required_source_facts(&window, &request, &diagnostics).is_empty(),
                "generation {generation} should be acknowledgement-ready"
            );
        }

        let short_target = win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticNode {
            path: "TargetLangCombo".to_string(),
            id: "TargetLangCombo".to_string(),
            kind: "ComboBox".to_string(),
            x: 0.0,
            y: 0.0,
            width: 138.0,
            height: 40.0,
            declarative_values: BTreeMap::new(),
            style_classes: Vec::new(),
            constructor_source: None,
            property_sources: BTreeMap::new(),
            resolved_values: BTreeMap::new(),
            token: None,
        };
        let unsettled = win_fluent_backend_iced::runtime_diagnostics::RuntimeDiagnosticGeneration {
            schema: control::RUNTIME_DIAGNOSTICS_SCHEMA.to_string(),
            window_id: "main".to_string(),
            generation: 1,
            controls: vec![short_target],
            observed_control_ids: vec!["TargetLangCombo".to_string()],
            missing_control_ids: Vec::new(),
        };
        assert_eq!(
            missing_required_source_facts(&window, &request, &unsettled),
            vec!["TargetLangCombo".to_string()]
        );
    }

    #[test]
    fn preview_app_forwards_desktop_runtime_surfaces_from_easydict_app() {
        let mut state =
            EasydictUiState::preview(easydict_app::PreviewScenario::Initial, ThemeMode::Light);
        state.settings.shell_context_menu = true;

        let (app, _) = PreviewApp::new(state);

        assert!(app.tray_menu().is_some());
        assert!(app.named_events().is_empty());
        assert_eq!(app.shell_verbs().len(), 1);
        assert_eq!(app.protocol_registrations().len(), 1);
        assert!(matches!(app.subscription(), Subscription::Batch(_)));
        assert!(app.window_options(&WindowId::new("mini")).is_some());
    }

    #[test]
    fn preview_runtime_plan_captures_inner_desktop_integration_entries() {
        let mut state =
            EasydictUiState::preview(easydict_app::PreviewScenario::Initial, ThemeMode::Light);
        state.settings.shell_context_menu = true;

        let plan = RuntimePlan::<PreviewApp>::new(state);

        assert!(plan.desktop_integration.has_entries());
        assert!(plan.desktop_integration.named_events.is_empty());
        assert_eq!(plan.desktop_integration.shell_verbs.len(), 1);
        assert_eq!(plan.desktop_integration.protocol_registrations.len(), 1);
    }

    fn restore_env(name: &str, value: Option<String>) {
        if let Some(value) = value {
            std::env::set_var(name, value);
        } else {
            std::env::remove_var(name);
        }
    }

    fn subscription_contains_kind<Message>(
        subscription: &Subscription<Message>,
        predicate: impl Fn(&SubscriptionKind) -> bool + Copy,
    ) -> bool {
        match subscription {
            Subscription::None => false,
            Subscription::Event { kind, .. } => predicate(kind),
            Subscription::Batch(items) => items
                .iter()
                .any(|item| subscription_contains_kind(item, predicate)),
        }
    }
}
