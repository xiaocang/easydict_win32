use std::{fs, path::Path, time::Duration};

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
        "mini" => return mini_window_options(),
        "fixed" => return fixed_window_options(),
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
        let (inner, initial_task) = if preview_mode {
            (EasydictApp { state: flags }, Task::none())
        } else {
            EasydictApp::new(flags)
        };
        if preview_mode {
            dump_preview_schema_if_requested(&inner);
        }

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

        let task = self.inner.update(message);
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
