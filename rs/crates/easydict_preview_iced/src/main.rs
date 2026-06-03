use std::{fs, path::Path, time::Duration};

use easydict_app::{
    capture_overlay_window_options, fixed_window_options, mini_window_options,
    pop_button_view_with_state, pop_button_window_options, preview_control_state_from_id,
    settings_window_options, EasydictApp, EasydictUiState, Message,
};
use win_fluent::prelude::*;

fn main() {
    win_fluent_backend_iced::run_single_window_application::<PreviewApp>(
        EasydictUiState::preview_from_env(),
        preview_window_options(),
    )
    .expect("Easydict preview runtime failed");
}

fn preview_window_options() -> WindowOptions {
    match preview_window_id().as_str() {
        "settings" => return settings_window_options(),
        "mini" => return mini_window_options(),
        "fixed" => return fixed_window_options(),
        "capture-overlay" => return capture_overlay_window_options(),
        "pop-button" => return pop_button_window_options(),
        _ => {}
    }

    let settings_preview = std::env::var("EASYDICT_PREVIEW_SETTINGS_OPEN")
        .ok()
        .is_some_and(|value| preview_env_truthy(&value));
    let (width, height) = if settings_preview {
        (620.0, 720.0)
    } else {
        (940.0, 1220.0)
    };
    let min_width = if settings_preview { 560.0 } else { 640.0 };

    WindowOptions::new("main", "Easydict Rust Main Window Preview")
        .size(width, height)
        .min_size(min_width, 720.0)
        .frame(WindowFrame::Borderless)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::Explicit { x: 40.0, y: 20.0 })
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
        .unwrap_or_else(|| "main".to_string())
}

fn preview_env_truthy(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "1" | "true" | "yes" | "on"
    )
}

fn pop_button_preview_state() -> ControlState {
    std::env::var("EASYDICT_PREVIEW_POPBUTTON_STATE")
        .ok()
        .map(|value| preview_control_state_from_id(&value))
        .unwrap_or_default()
}

struct PreviewApp {
    inner: EasydictApp,
}

impl Application for PreviewApp {
    type Message = Message;
    type Flags = EasydictUiState;

    fn new(flags: Self::Flags) -> (Self, Task<Self::Message>) {
        let (inner, initial_task) = EasydictApp::new(flags);
        dump_preview_schema_if_requested(&inner);

        let auto_toggle_task = std::env::var("EASYDICT_PREVIEW_AUTO_TOGGLE_RESULT")
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
            .unwrap_or_else(Task::none);

        (
            Self { inner },
            Task::batch([initial_task, auto_toggle_task]),
        )
    }

    fn title(&self, window: &WindowId) -> String {
        self.inner.title(window)
    }

    fn view(&self, window: &WindowId) -> View<Self::Message> {
        if window.as_str() == "pop-button" {
            return pop_button_view_with_state(pop_button_preview_state());
        }

        self.inner.view(window)
    }

    fn update(&mut self, message: Self::Message) -> Task<Self::Message> {
        self.inner.update(message)
    }

    fn theme(&self) -> ThemeMode {
        self.inner.theme()
    }
}

fn dump_preview_schema_if_requested(app: &EasydictApp) {
    let Ok(path) = std::env::var("EASYDICT_PREVIEW_SCHEMA_PATH") else {
        return;
    };

    let window_id = WindowId::new(preview_window_id());
    let view = if window_id.as_str() == "pop-button" {
        pop_button_view_with_state(pop_button_preview_state())
    } else {
        app.view(&window_id)
    };
    let schema = view_schema(&view).snapshot();

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
