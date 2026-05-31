use std::time::Duration;

use easydict_app::{EasydictApp, EasydictUiState, Message};
use win_fluent::prelude::*;

fn main() {
    win_fluent_backend_iced::run_single_window_application::<PreviewApp>(
        EasydictUiState::preview_from_env(),
        preview_window_options(),
    )
    .expect("Easydict preview runtime failed");
}

fn preview_window_options() -> WindowOptions {
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

fn preview_env_truthy(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "1" | "true" | "yes" | "on"
    )
}

struct PreviewApp {
    inner: EasydictApp,
}

impl Application for PreviewApp {
    type Message = Message;
    type Flags = EasydictUiState;

    fn new(flags: Self::Flags) -> (Self, Task<Self::Message>) {
        let (inner, initial_task) = EasydictApp::new(flags);
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
        self.inner.view(window)
    }

    fn update(&mut self, message: Self::Message) -> Task<Self::Message> {
        self.inner.update(message)
    }

    fn theme(&self) -> ThemeMode {
        self.inner.theme()
    }
}
