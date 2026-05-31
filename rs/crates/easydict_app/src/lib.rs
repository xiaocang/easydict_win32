#![forbid(unsafe_code)]

mod i18n;
pub mod state;
pub mod theme;
pub mod ui;
pub mod window_options;

pub use state::{
    AppMode, ConnectionStatus, EasydictUiState, FloatingWindowState, LongDocumentState, Message,
    PreviewScenario, SettingsSection, SettingsState, TranslationResultPreview,
};
pub use theme::easydict_theme_tokens;
pub use ui::{
    capture_overlay_view, fixed_window_view, main_window_view, mini_window_view, pop_button_view,
    settings_view,
};
pub use window_options::{
    capture_overlay_window_options, fixed_window_options, main_window_options, mini_window_options,
    pop_button_window_options, settings_window_options,
};

use win_fluent::prelude::*;

pub struct EasydictApp {
    pub state: EasydictUiState,
}

impl Application for EasydictApp {
    type Message = Message;
    type Flags = EasydictUiState;

    fn new(flags: Self::Flags) -> (Self, Task<Self::Message>) {
        (Self { state: flags }, Task::none())
    }

    fn title(&self, window: &WindowId) -> String {
        match window.as_str() {
            "main" => "Easydict".to_string(),
            "settings" => "Easydict Settings".to_string(),
            "mini" => "Easydict Mini".to_string(),
            "fixed" => "Easydict Fixed".to_string(),
            "capture-overlay" => "Easydict Capture".to_string(),
            "pop-button" => "Easydict Selection".to_string(),
            _ => "Easydict".to_string(),
        }
    }

    fn view(&self, window: &WindowId) -> View<Self::Message> {
        match window.as_str() {
            "settings" => settings_view(&self.state.settings),
            "mini" => mini_window_view(&self.state.mini),
            "fixed" => fixed_window_view(&self.state.fixed),
            "capture-overlay" => capture_overlay_view(),
            "pop-button" => pop_button_view(),
            _ => main_window_view(&self.state),
        }
    }

    fn update(&mut self, message: Self::Message) -> Task<Self::Message> {
        let task = match &message {
            Message::MinimizeWindow => Task::window(WindowCommand::MinimizeCurrent(true)),
            Message::ToggleMaximizeWindow => Task::window(WindowCommand::ToggleMaximizeCurrent),
            Message::CloseWindow => Task::window(WindowCommand::CloseCurrent),
            _ => Task::none(),
        };

        self.state.apply(message);
        task
    }

    fn theme(&self) -> ThemeMode {
        self.state.settings.theme
    }

    fn theme_tokens(&self) -> ThemeTokens {
        easydict_theme_tokens(self.state.settings.theme)
    }
}
