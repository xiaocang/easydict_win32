use std::{fs, path::Path, time::Duration};

use easydict_app::{
    capture_overlay_window_options, default_settings_storage_path, fixed_window_options,
    load_settings_file, main_window_options_for_settings, mini_window_options,
    pop_button_view_with_state, pop_button_window_options, preview_control_state_from_id,
    settings_window_options, EasydictApp, EasydictUiState, Message, SettingsState,
    MAIN_WINDOW_DEFAULT_HEIGHT_DIPS, MAIN_WINDOW_DEFAULT_WIDTH_DIPS, MAIN_WINDOW_MIN_HEIGHT_DIPS,
    MAIN_WINDOW_MIN_WIDTH_DIPS,
};
use win_fluent::prelude::*;

fn main() {
    let preview_mode = preview_mode_requested();
    let initial_state = initial_state_for_mode(preview_mode);
    let window_options = initial_window_options(preview_mode, &initial_state);

    win_fluent_backend_iced::run_single_window_application::<PreviewApp>(
        initial_state,
        window_options,
    )
    .expect("Easydict Rust runtime failed");
}

fn preview_mode_requested() -> bool {
    preview_mode_requested_from_names(std::env::vars().map(|(name, _)| name))
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
        "mini" => return mini_window_options(),
        "fixed" => return fixed_window_options(),
        "capture-overlay" => return capture_overlay_window_options(),
        "pop-button" => return pop_button_window_options(),
        _ => {}
    }

    let settings_preview = preview_settings_open();
    let (default_width, default_height) = if settings_preview {
        (846.0, 913.0)
    } else {
        (
            MAIN_WINDOW_DEFAULT_WIDTH_DIPS,
            MAIN_WINDOW_DEFAULT_HEIGHT_DIPS,
        )
    };
    let width = preview_env_f32("EASYDICT_PREVIEW_WIDTH_DIPS").unwrap_or(default_width);
    let height = preview_env_f32("EASYDICT_PREVIEW_HEIGHT_DIPS").unwrap_or(default_height);
    let min_width = if settings_preview {
        760.0
    } else {
        MAIN_WINDOW_MIN_WIDTH_DIPS
    };
    let min_height = if settings_preview {
        620.0
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

    Some(PreviewScroll { target_id, y })
}

struct PreviewApp {
    inner: EasydictApp,
    pending_scroll: Option<PreviewScroll>,
    preview_mode: bool,
}

impl Application for PreviewApp {
    type Message = Message;
    type Flags = EasydictUiState;

    fn new(flags: Self::Flags) -> (Self, Task<Self::Message>) {
        let preview_mode = preview_mode_requested();
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

        let pending_scroll = preview_scroll_from_env();
        let preview_scroll_task = pending_scroll
            .as_ref()
            .map(|_| {
                let delay_ms = std::env::var("EASYDICT_PREVIEW_SCROLL_DELAY_MS")
                    .ok()
                    .and_then(|value| value.parse::<u64>().ok())
                    .unwrap_or(900);
                Task::perform(
                    async move {
                        std::thread::sleep(Duration::from_millis(delay_ms));
                    },
                    |_| Message::Noop,
                )
            })
            .unwrap_or_else(Task::none);

        (
            Self {
                inner,
                pending_scroll,
                preview_mode,
            },
            Task::batch([initial_task, auto_toggle_task, preview_scroll_task]),
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
        let is_noop = message == Message::Noop;
        let task = self.inner.update(message);
        if is_noop {
            if let Some(scroll) = self.pending_scroll.take() {
                return Task::batch([task, Task::scroll_to(scroll.target_id, 0.0, scroll.y)]);
            }
        }

        task
    }

    fn window_options(&self, window: &WindowId) -> Option<WindowOptions> {
        self.inner.window_options(window)
    }

    fn subscription(&self) -> Subscription<Self::Message> {
        self.inner.subscription()
    }

    fn tray_menu(&self) -> Option<TrayMenu<Self::Message>> {
        self.inner.tray_menu()
    }

    fn named_events(&self) -> Vec<NamedEventRegistration<Self::Message>> {
        self.inner.named_events()
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
        assert_eq!(options.width, 846.0);
        assert_eq!(options.height, 913.0);
        assert_eq!(options.min_width, Some(760.0));
        assert_eq!(options.min_height, Some(620.0));
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
        assert!(state.results.is_empty());
        assert!(state.long_document.history.is_empty());
        assert!(state.mini.text.is_empty());
        assert!(state.mini.results.is_empty());
        assert!(state.fixed.text.is_empty());
        assert!(state.fixed.results.is_empty());
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
    fn preview_window_options_still_use_preview_contract_when_requested() {
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
    fn preview_app_forwards_desktop_runtime_surfaces_from_easydict_app() {
        let mut state =
            EasydictUiState::preview(easydict_app::PreviewScenario::Initial, ThemeMode::Light);
        state.settings.shell_context_menu = true;

        let (app, _) = PreviewApp::new(state);

        assert!(app.tray_menu().is_some());
        assert_eq!(app.named_events().len(), 1);
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
        assert_eq!(plan.desktop_integration.named_events.len(), 1);
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
}
