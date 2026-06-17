use std::collections::HashMap;
use std::env;
use std::fmt;
use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use iced::advanced::{
    clipboard::Clipboard,
    layout, mouse, overlay, renderer, text as iced_text_core,
    widget::{self, Operation, Tree},
    Layout, Renderer as _, Shell, Widget,
};
use iced::widget::text_editor as iced_text_editor_state;
use iced::widget::{
    button as iced_button, checkbox as iced_checkbox, column as iced_column,
    container as iced_container, image as iced_image, mouse_area as iced_mouse_area,
    opaque as iced_opaque, pick_list as iced_pick_list, progress_bar as iced_progress_bar,
    responsive as iced_responsive,
    row as iced_row, scrollable as iced_scrollable, slider as iced_slider, space as iced_space,
    stack as iced_stack, text as iced_text, text_editor as iced_text_editor,
    text_input as iced_text_input, toggler as iced_toggler,
};
use iced::{
    alignment, font, keyboard, window, Background, Border, Color, Element, Event, Font,
    Length as IcedLength, Padding as IcedPadding, Pixels, Point, Rectangle, Shadow, Size,
    Subscription, Vector,
};
use win_fluent::action::{Action, ActionKind};
use win_fluent::command::CommandToken;
use win_fluent::icon;
#[cfg(all(windows, feature = "legacy-powershell-dialogs"))]
use win_fluent::platform::FileDialogFilter;
use win_fluent::platform::{
    FileDialogOptions, FolderDialogOptions, Hotkey, HotkeyKey, HotkeyModifier, PlatformCommand,
    ProtocolRegistration, ShellVerb,
};
use win_fluent::runtime::{Application as FluentApplication, DesktopIntegrationPlan, RuntimePlan};
use win_fluent::screenshot::WindowScreenshot;
use win_fluent::state::{ControlState, ValidationSeverity};
use win_fluent::style::FluentStyle;
use win_fluent::subscription::{
    PlatformEvent, Subscription as FluentSubscription, SubscriptionKind as FluentSubscriptionKind,
    WindowEvent,
};
use win_fluent::task::Task as FluentTask;
use win_fluent::theme::{Color as FluentColor, ThemeMode, ThemeTokens};
use win_fluent::view::{
    AdaptiveSwitchToken, Alignment, BusyOverlayToken, ButtonKind, CaptureOverlayToken, CardKind,
    CardToken, CheckBoxToken, CollapseTransition, ComboBoxItem, Edges, ExpanderToken,
    FlyoutButtonToken, InfoBarToken, LayoutDistribution, LayoutKind, LayoutToken, Length,
    OverlayToken,
    PointerPosition, PointerRegionAction, PointerRegionToken, PointerWheel, ProgressBarToken,
    ProgressRingToken, ResultCardToken, ResultItem, ResultListToken, ResultStatus, ScrollPolicy,
    SettingsRowToken, SliderToken, StatusBadgeToken, TextEditorChrome, TextEditorKey,
    TextEditorKeyBinding, TextEditorKeyModifiers, TextEditorToken, TextStyle, TextToken,
    TextWrapping, TitleBarToken, View, ViewToken, WrapToken,
};
use win_fluent::window::{
    WindowCommand, WindowFrame, WindowId, WindowLevel, WindowOptions, WindowPlacement,
    WindowResizeMode,
};

pub type IcedElement<'a, Message> = Element<'a, Message>;
pub type IcedTextEditorContent = iced_text_editor_state::Content<iced::Renderer>;

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum IcedHotkeyEvent {
    Pressed { id: String },
    Error { message: String },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum IcedNamedEvent {
    Signaled { name: String },
    Error { name: String, message: String },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum IcedTrayEvent {
    Command { id: String },
    Error { message: String },
}

pub struct IcedAdapter;

impl IcedAdapter {
    pub fn compile_view<'a, Message>(view: &'a View<Message>) -> IcedElement<'a, Message>
    where
        Message: Clone + Send + 'static,
    {
        Self::compile_view_with_theme(view, &ThemeTokens::fluent_light())
    }

    pub fn compile_view_with_theme<'a, Message>(
        view: &'a View<Message>,
        theme: &ThemeTokens,
    ) -> IcedElement<'a, Message>
    where
        Message: Clone + Send + 'static,
    {
        compile_view_with_text_editors_and_visual(
            view,
            |_| None::<&IcedTextEditorContent>,
            IcedVisualTheme::from_tokens(theme),
        )
    }

    pub fn compile_view_with_text_editors<'a, Message, Provider>(
        view: &'a View<Message>,
        provider: Provider,
    ) -> IcedElement<'a, Message>
    where
        Message: Clone + Send + 'static,
        Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
    {
        Self::compile_view_with_text_editors_and_theme(view, provider, &ThemeTokens::fluent_light())
    }

    pub fn compile_view_with_text_editors_and_theme<'a, Message, Provider>(
        view: &'a View<Message>,
        provider: Provider,
        theme: &ThemeTokens,
    ) -> IcedElement<'a, Message>
    where
        Message: Clone + Send + 'static,
        Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
    {
        compile_view_with_text_editors_and_visual(
            view,
            provider,
            IcedVisualTheme::from_tokens(theme),
        )
    }

    pub fn window_settings(options: &WindowOptions) -> iced::window::Settings {
        window_settings(options)
    }

    pub fn window_settings_with_position(
        options: &WindowOptions,
        position: Point,
    ) -> iced::window::Settings {
        window_settings_with_position(options, position)
    }

    pub fn hotkey_subscription(hotkey: Hotkey) -> Subscription<IcedHotkeyEvent> {
        iced_hotkey_subscription(hotkey)
    }

    pub fn named_event_subscription(
        name: String,
        auto_reset: bool,
    ) -> Subscription<IcedNamedEvent> {
        iced_named_event_subscription(name, auto_reset)
    }

    pub fn tray_subscription(
        plan: win_fluent_platform_win::WindowsTrayPlan,
    ) -> Subscription<IcedTrayEvent> {
        iced_tray_subscription(plan)
    }

    pub fn window_screenshot(id: iced::window::Id) -> iced::Task<WindowScreenshot> {
        iced::window::screenshot(id).map(|screenshot| {
            Self::screenshot_frame(screenshot)
                .expect("iced window screenshots must have a valid RGBA buffer")
        })
    }

    pub fn screenshot_frame(
        screenshot: iced::window::Screenshot,
    ) -> Result<WindowScreenshot, win_fluent::ScreenshotError> {
        WindowScreenshot::from_physical_rgba(
            screenshot.size.width,
            screenshot.size.height,
            screenshot.scale_factor,
            screenshot.rgba.as_ref().to_vec(),
        )
    }
}

pub fn run_single_window_application<App>(
    flags: App::Flags,
    options: WindowOptions,
) -> Result<(), String>
where
    App: FluentApplication,
    App::Flags: Clone + Send + 'static,
    App::Message: fmt::Debug,
{
    let boot_options = options.clone();

    iced::daemon(
        move || IcedSingleWindowRuntime::<App>::boot(flags.clone(), boot_options.clone()),
        IcedSingleWindowRuntime::<App>::update,
        IcedSingleWindowRuntime::<App>::view,
    )
    .title(|state: &IcedSingleWindowRuntime<App>, window| state.title_for_native_window(window))
    .subscription(IcedSingleWindowRuntime::<App>::subscription)
    .run()
    .map_err(|error| error.to_string())
}

#[derive(Debug, Clone)]
enum IcedRuntimeMessage<Message> {
    App(Message),
    PlatformEvent(PlatformEvent),
    FocusWidget(String),
    WindowOpened(window::Id),
    WindowClosed(window::Id),
    WindowCloseRequested(window::Id),
    WindowNativeEvent(window::Id, window::Event),
}

#[derive(Clone)]
struct RuntimeWindow {
    logical_id: WindowId,
    options: WindowOptions,
}

struct IcedSingleWindowRuntime<App: FluentApplication> {
    app: App,
    boot_window_id: WindowId,
    boot_window_options: WindowOptions,
    window_title_overrides: HashMap<WindowId, String>,
    pending_windows: HashMap<window::Id, RuntimeWindow>,
    native_windows: HashMap<window::Id, RuntimeWindow>,
    logical_windows: HashMap<WindowId, window::Id>,
    views: HashMap<WindowId, View<App::Message>>,
    text_editors: HashMap<WindowId, TextEditorCache>,
    desktop_integration: DesktopIntegrationPlan<App::Message>,
    /// Native id of the window that most recently gained OS focus. Used to route
    /// `*Current` window commands (close/minimize/drag) to the window the user is
    /// actually interacting with, instead of defaulting to the boot window — which
    /// otherwise made e.g. the mini/fixed close buttons act on the main window.
    focused_native_window: Option<window::Id>,
}

impl<App> IcedSingleWindowRuntime<App>
where
    App: FluentApplication,
    App::Message: fmt::Debug,
{
    fn boot(
        flags: App::Flags,
        options: WindowOptions,
    ) -> (Self, iced::Task<IcedRuntimeMessage<App::Message>>) {
        let plan = RuntimePlan::<App>::new(flags);
        let mut runtime = Self::new(plan.app, options, plan.desktop_integration);
        let initial_task = runtime.fluent_task(plan.initial_task);
        let open_task = if runtime.boot_window_options.visible_on_start {
            runtime.open_window_task(runtime.boot_window_options.clone(), None)
        } else {
            iced::Task::none()
        };
        let focus_task = runtime.delayed_focused_text_editor_task();

        (
            runtime,
            iced::Task::batch([open_task, initial_task, focus_task]),
        )
    }

    fn new(
        app: App,
        window_options: WindowOptions,
        desktop_integration: DesktopIntegrationPlan<App::Message>,
    ) -> Self {
        let boot_window_id = window_options.id.clone();

        let mut runtime = Self {
            app,
            boot_window_id,
            boot_window_options: window_options,
            window_title_overrides: HashMap::new(),
            pending_windows: HashMap::new(),
            native_windows: HashMap::new(),
            logical_windows: HashMap::new(),
            views: HashMap::new(),
            text_editors: HashMap::new(),
            desktop_integration,
            focused_native_window: None,
        };
        let boot_window_id = runtime.boot_window_id.clone();
        runtime.sync_window_view(&boot_window_id, None);
        runtime
    }

    fn rebuild_views(&mut self) {
        let mut windows = vec![self.boot_window_id.clone()];
        windows.extend(self.logical_windows.keys().cloned());
        windows.extend(
            self.pending_windows
                .values()
                .map(|window| window.logical_id.clone()),
        );
        windows.sort_by(|left, right| left.as_str().cmp(right.as_str()));
        windows.dedup();

        for window in windows {
            self.sync_window_view(&window, None);
        }
    }

    fn sync_window_view(&mut self, window: &WindowId, view: Option<View<App::Message>>) {
        let view = view.unwrap_or_else(|| self.app.view(window));
        self.text_editors
            .entry(window.clone())
            .or_default()
            .sync(&view);
        self.views.insert(window.clone(), view);
    }

    fn title_for_logical_window(&self, window: &WindowId) -> String {
        self.window_title_overrides
            .get(window)
            .cloned()
            .unwrap_or_else(|| self.app.title(window))
    }

    fn title_for_native_window(&self, window: window::Id) -> String {
        let logical = self.logical_window_for_native(window);
        self.title_for_logical_window(&logical)
    }

    fn update(
        state: &mut Self,
        message: IcedRuntimeMessage<App::Message>,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        match message {
            IcedRuntimeMessage::App(message) => {
                let task = state.app.update(message);
                state.rebuild_views();
                iced::Task::batch([state.fluent_task(task), state.focused_text_editor_task()])
            }
            IcedRuntimeMessage::PlatformEvent(event) => state
                .platform_event_task(event)
                .unwrap_or_else(iced::Task::none),
            IcedRuntimeMessage::FocusWidget(id) => iced::widget::operation::focus(id),
            IcedRuntimeMessage::WindowOpened(window_id) => {
                let runtime_window =
                    state
                        .pending_windows
                        .remove(&window_id)
                        .unwrap_or_else(|| RuntimeWindow {
                            logical_id: state.boot_window_id.clone(),
                            options: state.boot_window_options.clone(),
                        });
                let logical_id = runtime_window.logical_id.clone();
                state.logical_windows.insert(logical_id.clone(), window_id);
                state
                    .native_windows
                    .insert(window_id, runtime_window.clone());
                state.sync_window_view(&logical_id, None);
                let opened_task = state
                    .platform_event_task(PlatformEvent::Window(WindowEvent::Opened(
                        logical_id.clone(),
                    )))
                    .unwrap_or_else(iced::Task::none);
                // Give freshly opened activatable windows OS keyboard focus so
                // their inputs are typeable immediately (mirrors the show path).
                let focus_task = if runtime_window.options.no_activate {
                    iced::Task::none()
                } else {
                    window::gain_focus(window_id)
                };
                iced::Task::batch([
                    apply_native_window_options_task(window_id, runtime_window.options, true),
                    state.delayed_focused_text_editor_task(),
                    focus_task,
                    opened_task,
                ])
            }
            IcedRuntimeMessage::WindowClosed(window_id) => {
                if state.focused_native_window == Some(window_id) {
                    state.focused_native_window = None;
                }
                let logical_id = state.logical_window_for_native(window_id);
                if let Some(runtime_window) = state.native_windows.remove(&window_id) {
                    state.logical_windows.remove(&runtime_window.logical_id);
                }
                state
                    .platform_event_task(PlatformEvent::Window(WindowEvent::Closed(logical_id)))
                    .unwrap_or_else(iced::Task::none)
            }
            IcedRuntimeMessage::WindowCloseRequested(window_id) => {
                let logical_id = state.logical_window_for_native(window_id);
                let event = close_requested_platform_event(&logical_id);
                state
                    .platform_event_task(event)
                    .unwrap_or_else(|| state.close_request_fallback_task(window_id, &logical_id))
            }
            IcedRuntimeMessage::WindowNativeEvent(window_id, event) => {
                if matches!(event, window::Event::Focused) {
                    state.focused_native_window = Some(window_id);
                }
                let logical_id = state.logical_window_for_native(window_id);
                let event = match event {
                    window::Event::Focused => WindowEvent::Focused(logical_id),
                    window::Event::Rescaled(_) => WindowEvent::DpiChanged(logical_id),
                    _ => return iced::Task::none(),
                };
                state
                    .platform_event_task(PlatformEvent::Window(event))
                    .unwrap_or_else(iced::Task::none)
            }
        }
    }

    fn platform_event_task(
        &mut self,
        event: PlatformEvent,
    ) -> Option<iced::Task<IcedRuntimeMessage<App::Message>>> {
        let message = map_platform_event(&self.app.subscription(), event)?;
        let task = self.app.update(message);
        self.rebuild_views();
        Some(iced::Task::batch([
            self.fluent_task(task),
            self.focused_text_editor_task(),
        ]))
    }

    fn view(state: &Self, window: window::Id) -> IcedElement<'_, IcedRuntimeMessage<App::Message>> {
        let theme = state.app.theme_tokens();
        let logical = state.logical_window_for_native(window);
        let view = state
            .views
            .get(&logical)
            .or_else(|| state.views.get(&state.boot_window_id))
            .expect("runtime must keep a view for the boot window");
        let text_editors = state.text_editors.get(&logical);
        IcedAdapter::compile_view_with_text_editors_and_theme(
            view,
            move |id| text_editors.and_then(|cache| cache.get(id)),
            &theme,
        )
        .map(IcedRuntimeMessage::App)
    }

    fn subscription(state: &Self) -> Subscription<IcedRuntimeMessage<App::Message>> {
        let _desktop_entry_count = state.desktop_integration.entry_count();
        let tray_menu = state
            .app
            .tray_menu()
            .or_else(|| state.desktop_integration.tray_menu.clone());
        let tray_plan = tray_menu
            .as_ref()
            .and_then(win_fluent_platform_win::WindowsPlatformAdapter::plan_tray);
        Subscription::batch([
            window::close_requests().map(IcedRuntimeMessage::WindowCloseRequested),
            window::close_events().map(IcedRuntimeMessage::WindowClosed),
            window::events().map(|(id, event)| IcedRuntimeMessage::WindowNativeEvent(id, event)),
            fluent_subscription(state.app.subscription(), tray_plan.as_ref()),
        ])
    }

    fn fluent_task(
        &mut self,
        task: FluentTask<App::Message>,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        match task {
            FluentTask::None => iced::Task::none(),
            FluentTask::Message(message) => iced::Task::done(IcedRuntimeMessage::App(message)),
            FluentTask::Batch(tasks) => {
                iced::Task::batch(tasks.into_iter().map(|task| self.fluent_task(task)))
            }
            FluentTask::Future(future) => iced::Task::future(future).map(IcedRuntimeMessage::App),
            FluentTask::Stream(stream) => iced::Task::stream(stream).map(IcedRuntimeMessage::App),
            FluentTask::Window(command) => self.window_command(command),
            FluentTask::Platform(command) => self.platform_command(command),
            FluentTask::Exit => iced::exit(),
            FluentTask::ScrollToTop(id) => iced::widget::operation::snap_to(
                iced::advanced::widget::Id::from(id),
                iced::widget::scrollable::RelativeOffset::START,
            ),
            FluentTask::ScrollTo { id, x, y } => iced::widget::operation::snap_to(
                iced::advanced::widget::Id::from(id),
                iced::widget::scrollable::RelativeOffset { x, y },
            ),
            FluentTask::ReadClipboardText(map) => {
                iced::clipboard::read().map(move |text| IcedRuntimeMessage::App(map(text)))
            }
            FluentTask::CaptureScreenRegion { request, map } => {
                iced::Task::future(async move { run_platform_capture_screen_region(request) })
                    .map(move |capture| IcedRuntimeMessage::App(map(capture)))
            }
            FluentTask::CaptureScreenWindows { request, map } => {
                iced::Task::future(async move { run_platform_capture_screen_windows(request) })
                    .map(move |windows| IcedRuntimeMessage::App(map(windows)))
            }
            FluentTask::OpenFileDialog { options, map } => {
                iced::Task::future(async move { run_platform_open_file_dialog(options) })
                    .map(move |path| IcedRuntimeMessage::App(map(path)))
            }
            FluentTask::OpenFolderDialog { options, map } => {
                iced::Task::future(async move { run_platform_open_folder_dialog(options) })
                    .map(move |path| IcedRuntimeMessage::App(map(path)))
            }
        }
    }

    fn focused_text_editor_task(&self) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        self.focused_text_editor_id()
            .map(IcedRuntimeMessage::FocusWidget)
            .map(iced::Task::done)
            .unwrap_or_else(iced::Task::none)
    }

    fn delayed_focused_text_editor_task(&self) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        self.focused_text_editor_id()
            .map(|id| {
                iced::Task::perform(
                    async move {
                        std::thread::sleep(Duration::from_millis(150));
                        id
                    },
                    IcedRuntimeMessage::FocusWidget,
                )
            })
            .unwrap_or_else(iced::Task::none)
    }

    fn focused_text_editor_id(&self) -> Option<String> {
        self.views.values().find_map(focused_text_editor_id)
    }

    fn close_request_fallback_task(
        &self,
        native_id: window::Id,
        logical_id: &WindowId,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        let close = window::close(native_id);
        if logical_id == &self.boot_window_id && !self.desktop_integration.has_entries() {
            iced::Task::batch([close, iced::exit()])
        } else {
            close
        }
    }

    fn platform_command(
        &self,
        command: PlatformCommand,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        match command {
            PlatformCommand::CaptureTextInsertionTarget => {
                iced::Task::future(async move { run_platform_capture_text_insertion_target() })
                    .discard()
            }
            PlatformCommand::WriteClipboardText(text) => {
                iced::clipboard::write::<IcedRuntimeMessage<App::Message>>(text)
            }
            PlatformCommand::InsertText(text) => {
                iced::Task::future(async move { run_platform_insert_text(text) }).discard()
            }
            PlatformCommand::OpenUrl(url) => {
                iced::Task::future(async move { run_platform_open_url(url) }).discard()
            }
            PlatformCommand::RegisterShellVerb(verb) => {
                iced::Task::future(async move { run_platform_register_shell_verb(verb) }).discard()
            }
            PlatformCommand::UnregisterShellVerb(verb) => {
                iced::Task::future(async move { run_platform_unregister_shell_verb(verb) })
                    .discard()
            }
            PlatformCommand::RegisterProtocol(protocol) => {
                iced::Task::future(async move { run_platform_register_protocol(protocol) })
                    .discard()
            }
            PlatformCommand::UnregisterProtocol(protocol) => {
                iced::Task::future(async move { run_platform_unregister_protocol(protocol) })
                    .discard()
            }
            PlatformCommand::RunBundledExecutable {
                executable_name,
                arguments,
            } => iced::Task::future(async move {
                run_platform_bundled_executable(executable_name, arguments)
            })
            .discard(),
            PlatformCommand::SpeakText { text, language } => {
                iced::Task::future(async move { run_platform_speak_text(text, language) }).discard()
            }
        }
    }

    fn window_command(
        &mut self,
        command: WindowCommand<App::Message>,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        match command {
            WindowCommand::CloseCurrent => self.with_current_window(window::close),
            WindowCommand::MinimizeCurrent(minimized) => {
                self.with_current_window(move |window_id| window::minimize(window_id, minimized))
            }
            WindowCommand::ToggleMaximizeCurrent => {
                self.with_current_window(window::toggle_maximize)
            }
            WindowCommand::DragCurrent => self.with_current_window(window::drag),
            WindowCommand::Close(id) => self
                .with_logical_window(&id, window::close)
                .unwrap_or_else(iced::Task::none),
            WindowCommand::Show(id) => self.show_logical_window(id, None),
            WindowCommand::ShowAt { id, x, y } => self.show_logical_window(id, Some((x, y))),
            WindowCommand::Hide(id) => self
                .with_logical_window(&id, |window_id| {
                    window::set_mode::<IcedRuntimeMessage<App::Message>>(
                        window_id,
                        window::Mode::Hidden,
                    )
                })
                .unwrap_or_else(iced::Task::none),
            WindowCommand::ToggleVisibility(id) => self
                .with_logical_window(&id, {
                    let options = self.visible_options_for_logical_window(&id);
                    move |window_id| {
                        let show_options = options.clone();
                        window::mode(window_id).then(move |mode| {
                            if mode == window::Mode::Hidden {
                                show_window_task::<App::Message>(window_id, show_options.clone())
                            } else {
                                window::set_mode::<IcedRuntimeMessage<App::Message>>(
                                    window_id,
                                    window::Mode::Hidden,
                                )
                            }
                        })
                    }
                })
                .unwrap_or_else(|| self.show_logical_window(id, None)),
            WindowCommand::Focus(id) => self
                .with_logical_window(&id, window::gain_focus)
                .unwrap_or_else(iced::Task::none),
            WindowCommand::Minimize { id, minimized } => self
                .with_logical_window(&id, move |window_id| {
                    window::minimize::<IcedRuntimeMessage<App::Message>>(window_id, minimized)
                })
                .unwrap_or_else(iced::Task::none),
            WindowCommand::Maximize { id, maximized } => self
                .with_logical_window(&id, move |window_id| {
                    window::maximize::<IcedRuntimeMessage<App::Message>>(window_id, maximized)
                })
                .unwrap_or_else(iced::Task::none),
            WindowCommand::ToggleMaximize(id) => self
                .with_logical_window(&id, window::toggle_maximize)
                .unwrap_or_else(iced::Task::none),
            WindowCommand::SetAlwaysOnTop { id, enabled } => self
                .with_logical_window(&id, move |window_id| {
                    let level = if enabled {
                        window::Level::AlwaysOnTop
                    } else {
                        window::Level::Normal
                    };
                    window::set_level::<IcedRuntimeMessage<App::Message>>(window_id, level)
                })
                .unwrap_or_else(iced::Task::none),
            WindowCommand::SetTitle { id, title } => {
                self.window_title_overrides.insert(id, title);
                iced::Task::none()
            }
            WindowCommand::Open { options, view } => self.open_window_task(options, Some(view)),
            WindowCommand::ReplaceView { id, view } => {
                self.sync_window_view(&id, Some(view));
                iced::Task::none()
            }
        }
    }

    fn show_logical_window(
        &mut self,
        id: WindowId,
        position: Option<(f32, f32)>,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        let mut options = self.visible_options_for_logical_window(&id);
        if let Some((x, y)) = position {
            options = show_at_window_options(&options, x, y);
        }

        if let Some(task) = self.with_logical_window(&id, {
            let options = options.clone();
            move |window_id| show_window_task::<App::Message>(window_id, options.clone())
        }) {
            return task;
        }

        self.open_window_task(options, None)
    }

    fn open_window_task(
        &mut self,
        options: WindowOptions,
        view: Option<View<App::Message>>,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        let logical_id = options.id.clone();
        self.sync_window_view(&logical_id, view);
        let settings = window_settings(&options);
        let (native_id, task) = window::open(settings);
        self.pending_windows.insert(
            native_id,
            RuntimeWindow {
                logical_id,
                options,
            },
        );
        task.map(move |_| IcedRuntimeMessage::WindowOpened(native_id))
    }

    fn with_current_window(
        &self,
        command: impl Fn(window::Id) -> iced::Task<IcedRuntimeMessage<App::Message>> + Send + 'static,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        // Prefer the window the user is currently interacting with (the one that
        // last gained OS focus) so close/minimize/drag buttons act on their own
        // window rather than always falling through to the boot/main window.
        if let Some(window_id) = self
            .focused_native_window
            .filter(|id| self.native_windows.contains_key(id))
        {
            return command(window_id);
        }

        if let Some(window_id) = self.logical_windows.get(&self.boot_window_id).copied() {
            return command(window_id);
        }

        window::latest().then(move |window_id| match window_id {
            Some(window_id) => command(window_id),
            None => iced::Task::none(),
        })
    }

    fn with_logical_window(
        &self,
        id: &WindowId,
        command: impl Fn(window::Id) -> iced::Task<IcedRuntimeMessage<App::Message>> + Send + 'static,
    ) -> Option<iced::Task<IcedRuntimeMessage<App::Message>>> {
        self.logical_windows.get(id).copied().map(command)
    }

    fn logical_window_for_native(&self, id: window::Id) -> WindowId {
        self.native_windows
            .get(&id)
            .or_else(|| self.pending_windows.get(&id))
            .map(|window| window.logical_id.clone())
            .unwrap_or_else(|| self.boot_window_id.clone())
    }

    fn options_for_logical_window(&self, id: &WindowId) -> WindowOptions {
        if id == &self.boot_window_id {
            self.app
                .window_options(id)
                .unwrap_or_else(|| self.boot_window_options.clone())
        } else {
            self.app
                .window_options(id)
                .unwrap_or_else(|| WindowOptions::new(id.clone(), self.app.title(id)))
        }
    }

    fn visible_options_for_logical_window(&self, id: &WindowId) -> WindowOptions {
        let mut options = self.options_for_logical_window(id);
        options.visible_on_start = true;
        options
    }
}

fn show_at_window_options(options: &WindowOptions, x: f32, y: f32) -> WindowOptions {
    options
        .clone()
        .placement(WindowPlacement::Explicit { x, y })
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ShowWindowStep {
    ApplyNativeOptions { delayed_check: bool },
    ResolvePlacement,
    ShowWindowed,
}

fn show_window_steps(options: &WindowOptions) -> Vec<ShowWindowStep> {
    let mut steps = Vec::new();
    if should_apply_native_options_before_show(options) {
        steps.push(ShowWindowStep::ApplyNativeOptions {
            delayed_check: false,
        });
    }

    steps.push(ShowWindowStep::ResolvePlacement);
    steps.push(ShowWindowStep::ShowWindowed);
    steps.push(ShowWindowStep::ApplyNativeOptions {
        delayed_check: true,
    });
    steps
}

fn should_apply_native_options_before_show(_options: &WindowOptions) -> bool {
    true
}

fn show_window_task<Message>(
    window_id: window::Id,
    options: WindowOptions,
) -> iced::Task<IcedRuntimeMessage<Message>>
where
    Message: Send + 'static,
{
    let mut tasks = Vec::new();

    for step in show_window_steps(&options) {
        match step {
            ShowWindowStep::ApplyNativeOptions { delayed_check } => tasks.push(
                apply_native_window_options_task(window_id, options.clone(), delayed_check),
            ),
            ShowWindowStep::ResolvePlacement => {
                #[cfg(windows)]
                if let Some((position, size)) = resolved_window_position_and_size(&options) {
                    tasks.push(window::resize::<IcedRuntimeMessage<Message>>(
                        window_id, size,
                    ));
                    tasks.push(window::move_to::<IcedRuntimeMessage<Message>>(
                        window_id, position,
                    ));
                }
            }
            ShowWindowStep::ShowWindowed => tasks.push(window::set_mode::<
                IcedRuntimeMessage<Message>,
            >(
                window_id, window::Mode::Windowed
            )),
        }
    }

    // Bring activatable windows to the foreground and give them keyboard focus
    // when shown. Without this, re-showing a hidden mini/fixed window leaves it
    // unfocused, so its text editor never receives keystrokes. `no_activate`
    // utility windows (e.g. the selection pop-button) must stay unfocused.
    if !options.no_activate {
        tasks.push(window::gain_focus::<IcedRuntimeMessage<Message>>(window_id));
    }

    iced::Task::batch(tasks)
}

#[cfg(windows)]
fn resolved_window_position_and_size(options: &WindowOptions) -> Option<(Point, Size)> {
    let placement =
        win_fluent_platform_win::WindowsPlatformAdapter::resolve_window_placement(options).ok()?;
    Some((
        Point::new(placement.x as f32, placement.y as f32),
        Size::new(placement.width as f32, placement.height as f32),
    ))
}

fn apply_native_window_options_task<Message>(
    window_id: window::Id,
    options: WindowOptions,
    delayed_check: bool,
) -> iced::Task<IcedRuntimeMessage<Message>>
where
    Message: Send + 'static,
{
    #[cfg(windows)]
    {
        let immediate_options = options.clone();
        let immediate = window::run(window_id, move |handle| {
            apply_native_window_options(handle, &immediate_options);
        })
        .discard();
        if !delayed_check {
            return immediate;
        }

        let delayed = iced::Task::perform(
            async move {
                std::thread::sleep(Duration::from_millis(150));
                (window_id, options)
            },
            |options| options,
        )
        .then(|(window_id, options)| {
            window::run(window_id, move |handle| {
                apply_native_window_options(handle, &options);
            })
            .discard()
        });

        return iced::Task::batch([immediate, delayed]);
    }

    #[cfg(not(windows))]
    {
        let _ = (window_id, options, delayed_check);
        iced::Task::none()
    }
}

#[cfg(windows)]
fn apply_native_window_options(handle: &dyn window::Window, options: &WindowOptions) {
    use iced::window::raw_window_handle::RawWindowHandle;

    let Ok(window_handle) = handle.window_handle() else {
        return;
    };

    let RawWindowHandle::Win32(raw_handle) = window_handle.as_raw() else {
        return;
    };

    let _ = win_fluent_platform_win::WindowsPlatformAdapter::apply_window_options_to_hwnd(
        raw_handle.hwnd.get(),
        options,
    );
}

fn close_requested_platform_event(window_id: &WindowId) -> PlatformEvent {
    PlatformEvent::Window(WindowEvent::CloseRequested(window_id.clone()))
}

fn fluent_subscription<Message>(
    subscription: FluentSubscription<Message>,
    tray_plan: Option<&win_fluent_platform_win::WindowsTrayPlan>,
) -> Subscription<IcedRuntimeMessage<Message>>
where
    Message: Clone + Send + 'static,
{
    match subscription {
        FluentSubscription::None => Subscription::none(),
        FluentSubscription::Batch(subscriptions) => Subscription::batch(
            subscriptions
                .into_iter()
                .map(|subscription| fluent_subscription::<Message>(subscription, tray_plan)),
        ),
        FluentSubscription::Event { kind, .. } => match kind {
            FluentSubscriptionKind::Hotkey(hotkey) => {
                IcedAdapter::hotkey_subscription(hotkey).map(|event| match event {
                    IcedHotkeyEvent::Pressed { id } => {
                        IcedRuntimeMessage::PlatformEvent(PlatformEvent::HotkeyPressed(id))
                    }
                    IcedHotkeyEvent::Error { message } => {
                        IcedRuntimeMessage::PlatformEvent(PlatformEvent::Custom {
                            kind: "hotkey_error".to_string(),
                            value: message,
                        })
                    }
                })
            }
            FluentSubscriptionKind::NamedEvent { name, auto_reset } => {
                IcedAdapter::named_event_subscription(name, auto_reset).map(|event| match event {
                    IcedNamedEvent::Signaled { name } => {
                        IcedRuntimeMessage::PlatformEvent(PlatformEvent::NamedEventSignaled(name))
                    }
                    IcedNamedEvent::Error { name, message } => {
                        IcedRuntimeMessage::PlatformEvent(PlatformEvent::Custom {
                            kind: format!("named_event_error:{name}"),
                            value: message,
                        })
                    }
                })
            }
            FluentSubscriptionKind::Tray => tray_plan
                .cloned()
                .map(IcedAdapter::tray_subscription)
                .unwrap_or_else(Subscription::none)
                .map(|event| match event {
                    IcedTrayEvent::Command { id } => {
                        IcedRuntimeMessage::PlatformEvent(PlatformEvent::TrayCommand(id))
                    }
                    IcedTrayEvent::Error { message } => {
                        IcedRuntimeMessage::PlatformEvent(PlatformEvent::Custom {
                            kind: "tray_error".to_string(),
                            value: message,
                        })
                    }
                }),
            FluentSubscriptionKind::Clipboard
            | FluentSubscriptionKind::Theme
            | FluentSubscriptionKind::Window(_)
            | FluentSubscriptionKind::Custom(_) => Subscription::none(),
        },
    }
}

fn map_platform_event<Message>(
    subscription: &FluentSubscription<Message>,
    event: PlatformEvent,
) -> Option<Message>
where
    Message: Clone + Send + 'static,
{
    match subscription {
        FluentSubscription::None => None,
        FluentSubscription::Event { map, .. } => map(event),
        FluentSubscription::Batch(subscriptions) => subscriptions
            .iter()
            .find_map(|subscription| map_platform_event(subscription, event.clone())),
    }
}

fn run_platform_open_file_dialog(options: FileDialogOptions) -> Option<String> {
    #[cfg(all(windows, feature = "legacy-powershell-dialogs"))]
    {
        let filter = file_dialog_filter_string(&options.filters);
        let mut script = String::new();
        script.push_str("Add-Type -AssemblyName System.Windows.Forms\n");
        script.push_str("$dialog = New-Object System.Windows.Forms.OpenFileDialog\n");
        script.push_str("$dialog.CheckFileExists = $true\n");
        script.push_str("$dialog.Multiselect = $false\n");
        script.push_str(&format!("$dialog.Title = {}\n", ps_quote(&options.title)));
        script.push_str(&format!("$dialog.Filter = {}\n", ps_quote(&filter)));

        if let Some(directory) = options.initial_directory.as_deref() {
            script.push_str(&format!("$initialDirectory = {}\n", ps_quote(directory)));
            script.push_str(
                "if ([System.IO.Directory]::Exists($initialDirectory)) { $dialog.InitialDirectory = $initialDirectory }\n",
            );
        }

        script.push_str(
            "if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($dialog.FileName) }\n",
        );

        let output = std::process::Command::new("powershell")
            .arg("-NoProfile")
            .arg("-STA")
            .arg("-WindowStyle")
            .arg("Hidden")
            .arg("-Command")
            .arg(script)
            .output()
            .ok()?;

        if !output.status.success() {
            return None;
        }

        let path = String::from_utf8_lossy(&output.stdout).trim().to_string();
        (!path.is_empty()).then_some(path)
    }

    #[cfg(not(all(windows, feature = "legacy-powershell-dialogs")))]
    {
        let _ = options;
        None
    }
}

fn run_platform_open_folder_dialog(options: FolderDialogOptions) -> Option<String> {
    #[cfg(all(windows, feature = "legacy-powershell-dialogs"))]
    {
        let mut script = String::new();
        script.push_str("Add-Type -AssemblyName System.Windows.Forms\n");
        script.push_str("$dialog = New-Object System.Windows.Forms.FolderBrowserDialog\n");
        script.push_str(&format!(
            "$dialog.Description = {}\n",
            ps_quote(&options.title)
        ));
        script.push_str("$dialog.ShowNewFolderButton = $true\n");

        if let Some(directory) = options.initial_directory.as_deref() {
            script.push_str(&format!("$initialDirectory = {}\n", ps_quote(directory)));
            script.push_str(
                "if ([System.IO.Directory]::Exists($initialDirectory)) { $dialog.SelectedPath = $initialDirectory }\n",
            );
        }

        script.push_str(
            "if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($dialog.SelectedPath) }\n",
        );

        let output = std::process::Command::new("powershell")
            .arg("-NoProfile")
            .arg("-STA")
            .arg("-WindowStyle")
            .arg("Hidden")
            .arg("-Command")
            .arg(script)
            .output()
            .ok()?;

        if !output.status.success() {
            return None;
        }

        let path = String::from_utf8_lossy(&output.stdout).trim().to_string();
        (!path.is_empty()).then_some(path)
    }

    #[cfg(not(all(windows, feature = "legacy-powershell-dialogs")))]
    {
        let _ = options;
        None
    }
}

#[cfg(all(windows, feature = "legacy-powershell-dialogs"))]
fn file_dialog_filter_string(filters: &[FileDialogFilter]) -> String {
    let mut parts = Vec::new();

    for filter in filters {
        if filter.patterns.is_empty() {
            continue;
        }

        let patterns = filter.patterns.join(";");
        parts.push(format!("{} ({})", filter.name, patterns));
        parts.push(patterns);
    }

    parts.push("All files (*.*)".to_string());
    parts.push("*.*".to_string());
    parts.join("|")
}

#[cfg(all(windows, feature = "legacy-powershell-dialogs"))]
fn ps_quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

fn run_platform_insert_text(text: String) -> Result<(), String> {
    #[cfg(windows)]
    {
        win_fluent_platform_win::WindowsPlatformAdapter::insert_text(&text)
            .map_err(|error| format!("{error:?}"))
    }

    #[cfg(not(windows))]
    {
        let _ = text;
        Ok(())
    }
}

fn run_platform_open_url(url: String) -> Result<(), String> {
    easydict_windows_shell::open_url(&url).map_err(|error| error.to_string())
}

fn run_platform_register_shell_verb(verb: ShellVerb) -> Result<(), String> {
    #[cfg(windows)]
    {
        let plan = win_fluent_platform_win::WindowsPlatformAdapter::plan_shell_verbs(&[verb])
            .into_iter()
            .next()
            .ok_or_else(|| "shell verb produced no registry plan".to_string())?;
        let executable_path = current_executable_path_string()?;
        win_fluent_platform_win::WindowsPlatformAdapter::register_shell_verb(
            &plan,
            &executable_path,
        )
        .map_err(|error| format!("{error:?}"))
    }

    #[cfg(not(windows))]
    {
        let _ = verb;
        Ok(())
    }
}

fn run_platform_unregister_shell_verb(verb: ShellVerb) -> Result<(), String> {
    #[cfg(windows)]
    {
        let plan = win_fluent_platform_win::WindowsPlatformAdapter::plan_shell_verbs(&[verb])
            .into_iter()
            .next()
            .ok_or_else(|| "shell verb produced no registry plan".to_string())?;
        win_fluent_platform_win::WindowsPlatformAdapter::unregister_shell_verb(&plan)
            .map_err(|error| format!("{error:?}"))
    }

    #[cfg(not(windows))]
    {
        let _ = verb;
        Ok(())
    }
}

fn run_platform_register_protocol(protocol: ProtocolRegistration) -> Result<(), String> {
    #[cfg(windows)]
    {
        let plan = win_fluent_platform_win::WindowsPlatformAdapter::plan_protocol_registrations(&[
            protocol,
        ])
        .into_iter()
        .next()
        .ok_or_else(|| "protocol produced no registry plan".to_string())?;
        let executable_path = current_executable_path_string()?;
        win_fluent_platform_win::WindowsPlatformAdapter::register_protocol_registration(
            &plan,
            &executable_path,
        )
        .map_err(|error| format!("{error:?}"))
    }

    #[cfg(not(windows))]
    {
        let _ = protocol;
        Ok(())
    }
}

fn run_platform_unregister_protocol(protocol: ProtocolRegistration) -> Result<(), String> {
    #[cfg(windows)]
    {
        let plan = win_fluent_platform_win::WindowsPlatformAdapter::plan_protocol_registrations(&[
            protocol,
        ])
        .into_iter()
        .next()
        .ok_or_else(|| "protocol produced no registry plan".to_string())?;
        win_fluent_platform_win::WindowsPlatformAdapter::unregister_protocol_registration(&plan)
            .map_err(|error| format!("{error:?}"))
    }

    #[cfg(not(windows))]
    {
        let _ = protocol;
        Ok(())
    }
}

fn run_platform_bundled_executable(
    executable_name: String,
    arguments: Vec<String>,
) -> Result<(), String> {
    easydict_windows_shell::run_bundled_executable(&executable_name, &arguments)
        .map_err(|error| error.to_string())
}

#[cfg(windows)]
fn current_executable_path_string() -> Result<String, String> {
    let path = env::current_exe().map_err(|error| error.to_string())?;
    validate_platform_registry_executable_path(&path)?;
    Ok(path.display().to_string())
}

fn validate_platform_registry_executable_path(path: &Path) -> Result<(), String> {
    easydict_windows_shell::validate_command_executable_target(path)
        .map_err(|error| error.to_string())
}

fn run_platform_capture_text_insertion_target() -> Result<(), String> {
    #[cfg(windows)]
    {
        win_fluent_platform_win::WindowsPlatformAdapter::capture_text_insertion_target()
            .map_err(|error| format!("{error:?}"))
    }

    #[cfg(not(windows))]
    {
        Ok(())
    }
}

fn run_platform_capture_screen_region(
    request: win_fluent::platform::ScreenCaptureRequest,
) -> Option<win_fluent::platform::ScreenCaptureResult> {
    #[cfg(windows)]
    {
        win_fluent_platform_win::WindowsPlatformAdapter::capture_screen_region_with_request(request)
            .ok()
    }

    #[cfg(not(windows))]
    {
        let _ = request;
        None
    }
}

fn run_platform_capture_screen_windows(
    request: win_fluent::platform::ScreenWindowSnapshotRequest,
) -> Vec<win_fluent::platform::ScreenWindow> {
    #[cfg(windows)]
    {
        win_fluent_platform_win::WindowsPlatformAdapter::capture_screen_windows_with_request(
            request,
        )
        .unwrap_or_default()
    }

    #[cfg(not(windows))]
    {
        let _ = request;
        Vec::new()
    }
}

fn run_platform_speak_text(text: String, language: Option<String>) -> Result<(), String> {
    #[cfg(windows)]
    {
        win_fluent_platform_win::WindowsPlatformAdapter::speak_text(&text, language.as_deref())
            .map_err(|error| format!("{error:?}"))
    }

    #[cfg(not(windows))]
    {
        let _ = (text, language);
        Ok(())
    }
}

#[derive(Default)]
struct TextEditorCache {
    contents: HashMap<String, IcedTextEditorContent>,
}

impl TextEditorCache {
    fn sync<Message>(&mut self, view: &View<Message>) {
        let mut values = HashMap::new();
        collect_text_editor_values(view, &mut values);

        self.contents.retain(|id, _| values.contains_key(id));

        for (id, text) in values {
            let matches_view = self
                .contents
                .get(&id)
                .is_some_and(|content| content.text() == text);

            if !matches_view {
                self.contents
                    .insert(id, IcedTextEditorContent::with_text(&text));
            }
        }
    }

    fn get(&self, id: &str) -> Option<&IcedTextEditorContent> {
        self.contents.get(id)
    }
}

fn collect_text_editor_values<Message>(view: &View<Message>, values: &mut HashMap<String, String>) {
    match view.token() {
        ViewToken::Page(token) => {
            if let Some(content) = &token.content {
                collect_text_editor_values(content, values);
            }
        }
        ViewToken::TitleBar(token) => {
            for command in &token.commands {
                collect_text_editor_values(command, values);
            }
        }
        ViewToken::BusyOverlay(token) => collect_text_editor_values(&token.content, values),
        ViewToken::TextEditor(token) => {
            if let Some(id) = &token.id {
                values.insert(id.clone(), token.text.clone());
            }
        }
        ViewToken::Card(token) => {
            if let Some(content) = &token.content {
                collect_text_editor_values(content, values);
            }
            for trailing in &token.trailing {
                collect_text_editor_values(trailing, values);
            }
        }
        ViewToken::CommandBar(token) => {
            for item in &token.items {
                collect_text_editor_values(item, values);
            }
        }
        ViewToken::NavigationView(token) => {
            if let Some(content) = &token.content {
                collect_text_editor_values(content, values);
            }
        }
        ViewToken::Dialog(token) => {
            if let Some(content) = &token.content {
                collect_text_editor_values(content, values);
            }
        }
        ViewToken::Layout(token) => {
            for child in &token.children {
                collect_text_editor_values(child, values);
            }
        }
        ViewToken::Wrap(token) => {
            for child in &token.children {
                collect_text_editor_values(child, values);
            }
        }
        ViewToken::Overlay(token) => {
            collect_text_editor_values(&token.base, values);
            for layer in &token.layers {
                collect_text_editor_values(&layer.content, values);
            }
        }
        ViewToken::AdaptiveSwitch(token) => {
            collect_text_editor_values(&token.wide, values);
            collect_text_editor_values(&token.narrow, values);
        }
        ViewToken::Lazy(token) => collect_text_editor_values(&token.content, values),
        ViewToken::ScrollView(token) => {
            if let Some(content) = &token.content {
                collect_text_editor_values(content, values);
            }
        }
        ViewToken::Expander(token) => {
            if token.expanded {
                if let Some(content) = &token.content {
                    collect_text_editor_values(content, values);
                }
            }
            for trailing in &token.trailing {
                collect_text_editor_values(trailing, values);
            }
        }
        ViewToken::SettingsRow(token) => {
            if let Some(content) = &token.content {
                collect_text_editor_values(content, values);
            }
            for trailing in &token.trailing {
                collect_text_editor_values(trailing, values);
            }
        }
        ViewToken::PointerRegion(token) => collect_text_editor_values(&token.content, values),
        ViewToken::CaptureOverlay(_) => {}
        ViewToken::Image(_) => {}
        ViewToken::Custom(token) => {
            for child in &token.children {
                collect_text_editor_values(child, values);
            }
        }
        ViewToken::Text(_)
        | ViewToken::Button(_)
        | ViewToken::FlyoutButton(_)
        | ViewToken::StatusBadge(_)
        | ViewToken::InfoBar(_)
        | ViewToken::ProgressRing(_)
        | ViewToken::ProgressBar(_)
        | ViewToken::Spacer(_)
        | ViewToken::CheckBox(_)
        | ViewToken::ToggleSwitch(_)
        | ViewToken::Slider(_)
        | ViewToken::ComboBox(_)
        | ViewToken::ResultCard(_)
        | ViewToken::ResultList(_) => {}
    }
}

fn window_settings_with_position(
    options: &WindowOptions,
    position: Point,
) -> iced::window::Settings {
    let mut settings = window_settings(options);
    settings.position = iced::window::Position::Specific(position);
    settings
}

fn window_settings(options: &WindowOptions) -> iced::window::Settings {
    let mut settings = iced::window::Settings {
        size: Size::new(options.width, options.height),
        min_size: match (options.min_width, options.min_height) {
            (Some(width), Some(height)) => Some(Size::new(width, height)),
            _ => None,
        },
        visible: options.visible_on_start,
        resizable: options.resize_mode == WindowResizeMode::CanResize,
        minimizable: options.resize_mode != WindowResizeMode::Fixed,
        decorations: options.frame == WindowFrame::Standard,
        transparent: options.frame == WindowFrame::Acrylic,
        level: match options.level {
            WindowLevel::Normal => iced::window::Level::Normal,
            WindowLevel::TopMost | WindowLevel::ToolWindow => iced::window::Level::AlwaysOnTop,
        },
        position: match options.placement {
            WindowPlacement::Center => iced::window::Position::Centered,
            WindowPlacement::Explicit { x, y } => {
                iced::window::Position::Specific(Point::new(x, y))
            }
            WindowPlacement::Monitor
            | WindowPlacement::WorkArea
            | WindowPlacement::CursorOffset { .. }
            | WindowPlacement::TopRight { .. } => iced::window::Position::Default,
        },
        ..iced::window::Settings::default()
    };

    #[cfg(windows)]
    {
        settings.platform_specific.skip_taskbar = options.skip_taskbar;
        settings.platform_specific.undecorated_shadow = options.frame != WindowFrame::Standard;
        apply_windows_screen_constraints(&mut settings, options);
    }

    settings
}

#[cfg(windows)]
fn apply_windows_screen_constraints(
    settings: &mut iced::window::Settings,
    options: &WindowOptions,
) {
    if let Ok(placement) =
        win_fluent_platform_win::WindowsPlatformAdapter::resolve_window_placement(options)
    {
        let width = placement.width as f32;
        let height = placement.height as f32;
        settings.size = Size::new(width, height);
        settings.position =
            iced::window::Position::Specific(Point::new(placement.x as f32, placement.y as f32));
        settings.min_size = settings
            .min_size
            .map(|min| Size::new(min.width.min(width), min.height.min(height)));
    }
}

fn focused_text_editor_id<Message>(view: &View<Message>) -> Option<String> {
    match view.token() {
        ViewToken::Page(token) => token.content.as_deref().and_then(focused_text_editor_id),
        ViewToken::TitleBar(token) => token.commands.iter().find_map(focused_text_editor_id),
        ViewToken::BusyOverlay(token) => focused_text_editor_id(&token.content),
        ViewToken::TextEditor(token) => token.state.focused.then(|| token.id.clone()).flatten(),
        ViewToken::Card(token) => token
            .content
            .as_deref()
            .and_then(focused_text_editor_id)
            .or_else(|| token.trailing.iter().find_map(focused_text_editor_id)),
        ViewToken::CommandBar(token) => token.items.iter().find_map(focused_text_editor_id),
        ViewToken::FlyoutButton(_) => None,
        ViewToken::NavigationView(token) => {
            token.content.as_deref().and_then(focused_text_editor_id)
        }
        ViewToken::Dialog(token) => token.content.as_deref().and_then(focused_text_editor_id),
        ViewToken::Layout(token) => token.children.iter().find_map(focused_text_editor_id),
        ViewToken::Wrap(token) => token.children.iter().find_map(focused_text_editor_id),
        ViewToken::Overlay(token) => focused_text_editor_id(&token.base).or_else(|| {
            token
                .layers
                .iter()
                .find_map(|layer| focused_text_editor_id(&layer.content))
        }),
        ViewToken::AdaptiveSwitch(token) => {
            focused_text_editor_id(&token.wide).or_else(|| focused_text_editor_id(&token.narrow))
        }
        ViewToken::Lazy(token) => focused_text_editor_id(&token.content),
        ViewToken::ScrollView(token) => token.content.as_deref().and_then(focused_text_editor_id),
        ViewToken::Expander(token) => {
            let content_focus = if token.expanded {
                token.content.as_deref().and_then(focused_text_editor_id)
            } else {
                None
            };
            content_focus.or_else(|| token.trailing.iter().find_map(focused_text_editor_id))
        }
        ViewToken::SettingsRow(token) => token
            .content
            .as_deref()
            .and_then(focused_text_editor_id)
            .or_else(|| token.trailing.iter().find_map(focused_text_editor_id)),
        ViewToken::PointerRegion(token) => focused_text_editor_id(&token.content),
        ViewToken::Button(_)
        | ViewToken::StatusBadge(_)
        | ViewToken::InfoBar(_)
        | ViewToken::ProgressRing(_)
        | ViewToken::ProgressBar(_)
        | ViewToken::Spacer(_)
        | ViewToken::Text(_)
        | ViewToken::CheckBox(_)
        | ViewToken::ToggleSwitch(_)
        | ViewToken::Slider(_)
        | ViewToken::ComboBox(_)
        | ViewToken::ResultCard(_)
        | ViewToken::ResultList(_)
        | ViewToken::CaptureOverlay(_)
        | ViewToken::Image(_)
        | ViewToken::Custom(_) => None,
    }
}

fn compile_view_with_text_editors_and_visual<'a, Message, Provider>(
    view: &'a View<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    match view.token() {
        ViewToken::Page(token) => {
            let content = token
                .content
                .as_deref()
                .map(|content| compile_view_with_text_editors_and_visual(content, provider, visual))
                .unwrap_or_else(empty);
            iced_container(content)
                .width(IcedLength::Fill)
                .height(IcedLength::Fill)
                .padding(0)
                .style(move |_| page_container_style(visual))
                .into()
        }
        ViewToken::TitleBar(token) => compile_title_bar(token, provider, visual),
        ViewToken::Text(token) => compile_text_token(token, visual),
        ViewToken::Button(token) => {
            let kind = token.kind;
            let state = token.state.clone();
            let mut content = button_content(
                &token.label,
                kind,
                token.icon.as_ref(),
                token.text_style,
                token.font_size,
                visual,
                state.selected,
            );
            // Buttons given an explicit fixed height (e.g. the 40px service
            // action buttons) would otherwise top-align their icon+label. Fill
            // the button interior and center so the content sits on the optical
            // midline like a WinUI button. Width stays Shrink, so content-sized
            // buttons keep hugging their label.
            if matches!(token.height, Some(Length::Fixed(_))) {
                content = iced_container(content)
                    .height(IcedLength::Fill)
                    .align_x(alignment::Horizontal::Center)
                    .align_y(alignment::Vertical::Center)
                    .into();
            }
            let mut control = iced_button(content).style(move |_, status| {
                let status = button_status_with_state(&state, status);
                button_style_with_state(visual, kind, state.focused, state.selected, status)
            });

            control = match kind {
                ButtonKind::Icon => control
                    .width(IcedLength::Fixed(visual.icon_button_size))
                    .height(IcedLength::Fixed(visual.icon_button_size)),
                ButtonKind::ResultAction => control
                    .width(IcedLength::Fixed(visual.result_action_button_size))
                    .height(IcedLength::Fixed(visual.result_action_button_size)),
                ButtonKind::FloatingAction => control
                    .width(IcedLength::Fixed(visual.floating_action_button_size))
                    .height(IcedLength::Fixed(visual.floating_action_button_size)),
                ButtonKind::PrimaryRound => control
                    .width(IcedLength::Fixed(visual.primary_icon_button_size()))
                    .height(IcedLength::Fixed(visual.primary_icon_button_size())),
                ButtonKind::Primary if token.icon.is_some() && token.label.trim().is_empty() => {
                    control
                        .width(IcedLength::Fixed(visual.primary_icon_button_size()))
                        .height(IcedLength::Fixed(visual.primary_icon_button_size()))
                }
                ButtonKind::Tile => control
                    .width(IcedLength::Fixed(86.0))
                    .height(IcedLength::Fixed(76.0)),
                ButtonKind::Primary
                | ButtonKind::Chip
                | ButtonKind::Subtle
                | ButtonKind::Link
                | ButtonKind::Standard => control,
            };

            control = if let Some(padding) = token.padding {
                control.padding(layout_padding(0, Some(padding)))
            } else {
                match kind {
                    ButtonKind::Icon
                    | ButtonKind::ResultAction
                    | ButtonKind::FloatingAction
                    | ButtonKind::PrimaryRound => control.padding(0),
                    ButtonKind::Primary
                        if token.icon.is_some() && token.label.trim().is_empty() =>
                    {
                        control.padding(0)
                    }
                    ButtonKind::Primary => control.padding([8, 14]),
                    ButtonKind::Chip => control.padding([7, 12]),
                    ButtonKind::Tile => control.padding([8, 10]),
                    ButtonKind::Subtle => control.padding([6, 10]),
                    ButtonKind::Link => control.padding(0),
                    ButtonKind::Standard => control.padding([6, 12]),
                }
            };

            if let Some(width) = token.width {
                control = control.width(iced_length(width));
            }
            if let Some(height) = token.height {
                control = control.height(iced_length(height));
            }

            if token.state.enabled {
                if let Some(message) = token.action.press() {
                    control = control.on_press(message);
                }
            }

            let mut element: IcedElement<'a, Message> = control.into();
            if !token.margin.is_zero() {
                element = iced_container(element)
                    .padding(layout_padding(0, Some(token.margin)))
                    .into();
            }
            element
        }
        ViewToken::FlyoutButton(token) => compile_flyout_button(token, visual),
        ViewToken::StatusBadge(token) => compile_status_badge(token, visual),
        ViewToken::InfoBar(token) => compile_info_bar(token, visual),
        ViewToken::ProgressRing(token) => compile_progress_ring(token, visual),
        ViewToken::ProgressBar(token) => compile_progress_bar(token, visual),
        ViewToken::BusyOverlay(token) => compile_busy_overlay(token, provider, visual),
        ViewToken::Card(token) => compile_card(token, provider, visual),
        ViewToken::Spacer(token) => iced_space()
            .width(iced_length(token.width))
            .height(iced_length(token.height))
            .into(),
        ViewToken::TextEditor(token) => compile_text_editor(token, provider, visual),
        ViewToken::CheckBox(token) => compile_check_box(token, visual),
        ViewToken::ToggleSwitch(token) => {
            let mut control = iced_toggler(token.checked)
                .label(toggle_switch_label(&token.label, token.checked))
                .size(20)
                .spacing(14)
                .text_size(visual.body_size)
                .style({
                    let state = token.state.clone();
                    let checked = token.checked;
                    move |_, status| {
                        let status = toggle_switch_status_with_state(&state, checked, status);
                        toggle_switch_style_with_state(visual, status, &state)
                    }
                });

            if token.state.enabled && token.action.kind() == ActionKind::BoolInput {
                let action = token.action.clone();
                control = control.on_toggle(move |value| {
                    action
                        .input_bool(value)
                        .expect("toggle action must produce a message")
                });
            }

            let mut control: IcedElement<'a, Message> = control.into();
            if token.width.is_some() || token.height.is_some() {
                let mut frame = iced_container(control)
                    .align_x(alignment::Horizontal::Center)
                    .align_y(alignment::Vertical::Center);
                if let Some(width) = token.width {
                    frame = frame.width(iced_length(width));
                }
                if let Some(height) = token.height {
                    frame = frame.height(iced_length(height));
                }
                control = frame.into();
            }

            let mut element: IcedElement<'a, Message> = if let Some(header) = token
                .header
                .as_deref()
                .filter(|value| !value.trim().is_empty())
            {
                iced_column(vec![compile_text(header, TextStyle::Body, visual), control])
                    .spacing(14)
                    .into()
            } else {
                control
            };

            if !token.margin.is_zero() || token.align_y != Alignment::Start {
                let mut frame = iced_container(element);
                if !token.margin.is_zero() {
                    let margin = token.margin;
                    frame = frame.padding(IcedPadding {
                        top: f32::from(margin.top),
                        right: f32::from(margin.right),
                        bottom: f32::from(margin.bottom),
                        left: f32::from(margin.left),
                    });
                }
                if token.align_y != Alignment::Start {
                    frame = frame.align_y(vertical_alignment(token.align_y));
                }
                element = frame.into();
            }

            element
        }
        ViewToken::Slider(token) => compile_slider(token, visual),
        ViewToken::ComboBox(token) => compile_combo_box(
            &token.items,
            token.selected.as_deref(),
            token.label.as_deref(),
            token.placeholder.as_deref(),
            token.width,
            token.height,
            &token.action,
            &token.state,
            visual,
        ),
        ViewToken::CommandBar(token) => {
            let children = token
                .items
                .iter()
                .map(|item| compile_view_with_text_editors_and_visual(item, provider, visual))
                .collect::<Vec<_>>();
            let children = distribute_children(children, LayoutKind::Row, token.distribution);
            iced_row(children)
                .spacing(if token.compact { 4 } else { 8 })
                .align_y(vertical_alignment(token.align))
                .width(iced_length(token.width))
                .into()
        }
        ViewToken::NavigationView(token) => {
            let action = token.action.clone();
            let mut nav_items = iced_column(Vec::new()).spacing(4);

            for item in &token.items {
                let selected = token.selected.as_deref() == Some(item.id.as_str());
                let label = if selected {
                    format!("> {}", item.label)
                } else {
                    item.label.clone()
                };
                let mut item_button = iced_button(iced_text(label))
                    .style(move |_, status| button_style(visual, ButtonKind::Subtle, status));

                if matches!(action.kind(), ActionKind::SelectionInput) {
                    let action = action.clone();
                    let id = item.id.clone();
                    item_button = item_button.on_press(
                        action
                            .input_text(id)
                            .expect("navigation action must produce a message"),
                    );
                }

                nav_items = nav_items.push(item_button);
            }

            if let Some(content) = &token.content {
                iced_row(vec![
                    iced_container(nav_items).width(180).into(),
                    compile_view_with_text_editors_and_visual(content, provider, visual),
                ])
                .spacing(16)
                .into()
            } else {
                nav_items.into()
            }
        }
        ViewToken::Dialog(token) => {
            let mut content = iced_column(vec![compile_text(
                &token.title,
                TextStyle::Subtitle,
                visual,
            )])
            .padding(24)
            .spacing(12);

            if let Some(child) = &token.content {
                content = content.push(compile_view_with_text_editors_and_visual(
                    child, provider, visual,
                ));
            }

            let mut commands = Vec::new();
            if let Some(command) = &token.primary {
                commands.push(compile_command(command, visual));
            }
            if let Some(command) = &token.secondary {
                commands.push(compile_command(command, visual));
            }
            if !commands.is_empty() {
                content = content.push(iced_row(commands).spacing(8));
            }

            // Modal chrome matching the WinUI ContentDialog: a solid surface
            // card with rounded corners and a deep shadow, width-capped so the
            // dialog never spans the window.
            iced_container(content)
                .max_width(560.0)
                .style(move |_| dialog_container_style(visual))
                .into()
        }
        ViewToken::Layout(token) => {
            // The capture-tip pill draws white text on a dark chip over the
            // capture scrim (WinUI overlay tip bar); children inherit the
            // inverted text palette.
            let visual = if token.style.has("capture-tip") {
                IcedVisualTheme {
                    text_primary: Color::WHITE,
                    text_secondary: Color::WHITE.scale_alpha(0.85),
                    ..visual
                }
            } else {
                visual
            };
            let children = token
                .children
                .iter()
                .map(|child| compile_view_with_text_editors_and_visual(child, provider, visual))
                .collect::<Vec<_>>();
            let children = distribute_children(children, token.kind, token.distribution);
            let content: IcedElement<'a, Message> = match token.kind {
                LayoutKind::Column => iced_column(children)
                    .padding(layout_padding(token.padding, token.padding_edges))
                    .spacing(u32::from(token.spacing))
                    .width(iced_length(token.width))
                    .height(iced_length(token.height))
                    .align_x(horizontal_alignment(token.align))
                    .into(),
                LayoutKind::Row => iced_row(children)
                    .padding(layout_padding(token.padding, token.padding_edges))
                    .spacing(u32::from(token.spacing))
                    .width(iced_length(token.width))
                    .height(iced_length(token.height))
                    .align_y(vertical_alignment(token.align))
                    .into(),
            };

            let styled =
                apply_layout_style(content, &token.style, token.width, token.height, visual);
            apply_layout_box(styled, token)
        }
        ViewToken::Wrap(token) => compile_wrap(token, provider, visual),
        ViewToken::Overlay(token) => compile_overlay(token, provider, visual),
        ViewToken::AdaptiveSwitch(token) => compile_adaptive_switch(token, provider, visual),
        ViewToken::Lazy(token) => {
            compile_view_with_text_editors_and_visual(&token.content, provider, visual)
        }
        ViewToken::ScrollView(token) => {
            let content = token
                .content
                .as_deref()
                .map(|content| compile_view_with_text_editors_and_visual(content, provider, visual))
                .unwrap_or_else(empty);
            let mut scroll = iced_scrollable(iced_container(content).width(IcedLength::Fill))
                .width(IcedLength::Fill)
                .height(IcedLength::Fill)
                .direction(scroll_direction(
                    token.horizontal,
                    token.vertical,
                    token.scrollbars_visible,
                ));
            if let Some(id) = &token.id {
                // Expose the scroll id so `Task::scroll_to_top` can target it.
                scroll = scroll.id(iced::advanced::widget::Id::from(id.clone()));
            }
            scroll.into()
        }
        ViewToken::Expander(token) => compile_expander(token, provider, visual),
        ViewToken::SettingsRow(token) => compile_settings_row(token, provider, visual),
        ViewToken::ResultCard(token) => compile_result_card(token, visual),
        ViewToken::ResultList(token) => compile_result_list(token, visual),
        ViewToken::PointerRegion(token) => compile_pointer_region(token, provider, visual),
        ViewToken::CaptureOverlay(token) => compile_capture_overlay(token, visual),
        ViewToken::Image(token) => compile_image(token, visual),
        ViewToken::Custom(token) => {
            let mut content = iced_column(vec![compile_text(
                &token.control,
                TextStyle::Caption,
                visual,
            )])
            .spacing(8);
            for child in &token.children {
                content = content.push(compile_view_with_text_editors_and_visual(
                    child, provider, visual,
                ));
            }
            content.into()
        }
    }
}

fn compile_pointer_region<'a, Message, Provider>(
    token: &'a PointerRegionToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let content = compile_view_with_text_editors_and_visual(&token.content, provider, visual);
    PointerRegionWidget::new(token, content).into()
}

fn compile_capture_overlay<'a, Message>(
    token: &'a CaptureOverlayToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let rect = token.selection_rect.or(token.detected_rect);
    let Some(rect) = rect else {
        return iced_container(iced_space())
            .width(IcedLength::Fixed(1.0))
            .height(IcedLength::Fixed(1.0))
            .into();
    };

    let width = (rect.width.max(24) as f32).min(900.0);
    let height = (rect.height.max(24) as f32).min(500.0);
    let selected = token.selection_rect.is_some();
    let border_color = if selected {
        visual.accent
    } else {
        visual.warning
    };
    let fill_color = if selected {
        visual.accent.scale_alpha(0.10)
    } else {
        visual.warning.scale_alpha(0.12)
    };

    let frame: IcedElement<'a, Message> = iced_container(iced_space())
        .width(IcedLength::Fixed(width))
        .height(IcedLength::Fixed(height))
        .style(move |_| capture_overlay_frame_style(visual, border_color, fill_color, selected))
        .into();

    let framed: IcedElement<'a, Message> = if token.handles_visible {
        capture_overlay_with_handles(frame, visual)
    } else {
        frame
    };

    let chip = iced_container(
        iced_text(format!("{} x {}", rect.width.max(0), rect.height.max(0)))
            .font(text_font(TextStyle::Caption))
            .size(visual.caption_size)
            .color(visual.text_on_accent),
    )
    .padding([2, 8])
    .style(move |_| capture_overlay_size_chip_style(visual, selected));

    let mut content = iced_column(vec![framed, chip.into()])
        .spacing(6)
        .align_x(alignment::Horizontal::Center);

    if token.magnifier_visible {
        content = content.push(capture_overlay_magnifier(width, height, visual));
    }

    content.into()
}

fn capture_overlay_with_handles<'a, Message>(
    frame: IcedElement<'a, Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let handles = iced_row(vec![
        capture_overlay_handle(visual),
        capture_overlay_handle(visual),
        capture_overlay_handle(visual),
        capture_overlay_handle(visual),
        capture_overlay_handle(visual),
        capture_overlay_handle(visual),
        capture_overlay_handle(visual),
        capture_overlay_handle(visual),
    ])
    .spacing(6)
    .align_y(alignment::Vertical::Center);

    iced_column(vec![frame, handles.into()])
        .spacing(8)
        .align_x(alignment::Horizontal::Center)
        .into()
}

fn capture_overlay_handle<'a, Message>(visual: IcedVisualTheme) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    iced_container(iced_space())
        .width(IcedLength::Fixed(9.0))
        .height(IcedLength::Fixed(9.0))
        .style(move |_| capture_overlay_handle_style(visual))
        .into()
}

fn capture_overlay_magnifier<'a, Message>(
    width: f32,
    height: f32,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let inner = iced_container(
        iced_text(format!("{width:.0} x {height:.0}"))
            .font(text_font(TextStyle::Caption))
            .size(visual.caption_size)
            .color(visual.text_primary),
    )
    .width(IcedLength::Fixed(128.0))
    .height(IcedLength::Fixed(76.0))
    .align_x(alignment::Horizontal::Center)
    .align_y(alignment::Vertical::Center)
    .style(move |_| capture_overlay_magnifier_style(visual));

    inner.into()
}

fn compile_text<'a, Message>(
    value: &str,
    style: TextStyle,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    iced_text(value.to_string())
        .font(text_font_for_value(style, value))
        .size(text_size(style, visual))
        .color(text_color(style, visual))
        .into()
}

fn compile_text_token<'a, Message>(
    token: &'a TextToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let text = iced_text(token.value.clone())
        .font(text_font_for_value(token.style, &token.value))
        .size(
            token
                .font_size
                .map(f32::from)
                .unwrap_or_else(|| text_size(token.style, visual)),
        )
        .color(text_color(token.style, visual))
        .wrapping(iced_text_wrapping(token.wrapping));

    let has_custom_alignment = token.align_x != win_fluent::view::Alignment::Start
        || token.align_y != win_fluent::view::Alignment::Start;
    if token.width.is_none()
        && token.height.is_none()
        && token.margin.is_zero()
        && !has_custom_alignment
        && !is_private_use_icon_text(&token.value)
    {
        return text.into();
    }

    let mut container = iced_container(text);
    if let Some(width) = token.width {
        container = container.width(iced_length(width));
    }
    if let Some(height) = token.height {
        container = container.height(iced_length(height));
    }
    if !token.margin.is_zero() {
        let margin = token.margin;
        container = container.padding(iced::Padding {
            top: f32::from(margin.top),
            right: f32::from(margin.right),
            bottom: f32::from(margin.bottom),
            left: f32::from(margin.left),
        });
    }
    if has_custom_alignment {
        container = container
            .align_x(horizontal_alignment(token.align_x))
            .align_y(vertical_alignment(token.align_y));
    }
    if is_private_use_icon_text(&token.value) {
        container = container
            .align_x(alignment::Horizontal::Center)
            .align_y(alignment::Vertical::Center);
    }
    container.into()
}

fn iced_text_wrapping(wrapping: TextWrapping) -> iced::widget::text::Wrapping {
    match wrapping {
        TextWrapping::Word => iced::widget::text::Wrapping::Word,
        TextWrapping::None => iced::widget::text::Wrapping::None,
    }
}

fn compile_slider<'a, Message>(
    token: &'a SliderToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    if !token.state.enabled || token.action.kind() != ActionKind::NumberInput {
        return compile_read_only_slider(token, visual);
    }

    let action = token.action.clone();
    iced_slider(token.min..=token.max, token.value, move |value| {
        action
            .input_number(value)
            .expect("slider action must produce a message")
    })
    .step(token.step)
    .width(iced_length(token.width))
    .height(20)
    .style({
        let state = token.state.clone();
        move |_, status| slider_style_with_state(visual, status, &state)
    })
    .into()
}

fn compile_read_only_slider<'a, Message>(
    token: &'a SliderToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let span = (token.max - token.min).max(f32::EPSILON);
    let ratio = ((token.value - token.min) / span).clamp(0.0, 1.0);
    let active_portion = ((ratio * 998.0).round() as u16).saturating_add(1).min(999);
    let inactive_portion = 1000_u16.saturating_sub(active_portion).max(1);
    let state = token.state.clone();
    let active_state = state.clone();
    let inactive_state = state.clone();
    let thumb_state = state;

    let active_rail: IcedElement<'a, Message> = iced_container(iced_space())
        .height(IcedLength::Fixed(4.0))
        .width(IcedLength::FillPortion(active_portion))
        .style(move |_| slider_read_only_rail_style(visual, &active_state, true))
        .into();
    let thumb: IcedElement<'a, Message> = iced_container(iced_space())
        .height(IcedLength::Fixed(16.0))
        .width(IcedLength::Fixed(16.0))
        .style(move |_| slider_read_only_thumb_style(visual, &thumb_state))
        .into();
    let inactive_rail: IcedElement<'a, Message> = iced_container(iced_space())
        .height(IcedLength::Fixed(4.0))
        .width(IcedLength::FillPortion(inactive_portion))
        .style(move |_| slider_read_only_rail_style(visual, &inactive_state, false))
        .into();

    iced_container(
        iced_row(vec![active_rail, thumb, inactive_rail])
            .align_y(alignment::Vertical::Center)
            .height(IcedLength::Fixed(20.0)),
    )
    .width(iced_length(token.width))
    .into()
}

struct PointerRegionWidget<'a, Message> {
    content: IcedElement<'a, Message>,
    width: Length,
    height: Length,
    move_action: PointerRegionAction<Message>,
    left_down_action: PointerRegionAction<Message>,
    left_up_action: PointerRegionAction<Message>,
    double_click_action: PointerRegionAction<Message>,
    right_down_action: Action<Message>,
    wheel_action: PointerRegionAction<Message>,
    escape_action: Action<Message>,
}

#[derive(Default)]
struct PointerRegionState {
    last_left_down: Option<(Instant, PointerPosition)>,
}

impl<'a, Message: Clone> PointerRegionWidget<'a, Message> {
    fn new(token: &PointerRegionToken<Message>, content: IcedElement<'a, Message>) -> Self {
        Self {
            content,
            width: token.width,
            height: token.height,
            move_action: token.move_action.clone(),
            left_down_action: token.left_down_action.clone(),
            left_up_action: token.left_up_action.clone(),
            double_click_action: token.double_click_action.clone(),
            right_down_action: token.right_down_action.clone(),
            wheel_action: token.wheel_action.clone(),
            escape_action: token.escape_action.clone(),
        }
    }
}

impl<Message> Widget<Message, iced::Theme, iced::Renderer> for PointerRegionWidget<'_, Message>
where
    Message: Clone,
{
    fn tag(&self) -> widget::tree::Tag {
        widget::tree::Tag::of::<PointerRegionState>()
    }

    fn state(&self) -> widget::tree::State {
        widget::tree::State::new(PointerRegionState::default())
    }

    fn children(&self) -> Vec<Tree> {
        vec![Tree::new(&self.content)]
    }

    fn diff(&self, tree: &mut Tree) {
        tree.diff_children(std::slice::from_ref(&self.content));
    }

    fn size(&self) -> Size<IcedLength> {
        Size::new(iced_length(self.width), iced_length(self.height))
    }

    fn layout(
        &mut self,
        tree: &mut Tree,
        renderer: &iced::Renderer,
        limits: &layout::Limits,
    ) -> layout::Node {
        let child = self.content.as_widget_mut().layout(
            &mut tree.children[0],
            renderer,
            &limits
                .width(iced_length(self.width))
                .height(iced_length(self.height)),
        );
        let size = limits.resolve(
            iced_length(self.width),
            iced_length(self.height),
            child.size(),
        );
        layout::Node::with_children(size, vec![child])
    }

    fn update(
        &mut self,
        tree: &mut Tree,
        event: &Event,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        renderer: &iced::Renderer,
        clipboard: &mut dyn Clipboard,
        shell: &mut Shell<'_, Message>,
        viewport: &Rectangle,
    ) {
        let bounds = layout.bounds();
        let position = pointer_position(bounds, cursor);
        let state = tree.state.downcast_mut::<PointerRegionState>();

        match event {
            Event::Mouse(mouse::Event::CursorMoved { .. }) => {
                if let Some(position) = position {
                    publish_pointer(&self.move_action, position, shell);
                }
            }
            Event::Mouse(mouse::Event::ButtonPressed(mouse::Button::Left)) => {
                if let Some(position) = position {
                    let now = Instant::now();
                    let is_double_click = state
                        .last_left_down
                        .map(|(last_at, last_position)| {
                            now.duration_since(last_at) <= Duration::from_millis(500)
                                && pointer_distance_within(last_position, position, 4)
                        })
                        .unwrap_or(false);
                    state.last_left_down = Some((now, position));

                    if is_double_click
                        && self.double_click_action.kind()
                            != win_fluent::view::PointerRegionActionKind::None
                    {
                        publish_pointer(&self.double_click_action, position, shell);
                    } else {
                        publish_pointer(&self.left_down_action, position, shell);
                    }
                }
            }
            Event::Mouse(mouse::Event::ButtonReleased(mouse::Button::Left)) => {
                if let Some(position) = position {
                    publish_pointer(&self.left_up_action, position, shell);
                }
            }
            Event::Mouse(mouse::Event::ButtonPressed(mouse::Button::Right)) => {
                if position.is_some() {
                    if let Some(message) = self.right_down_action.press() {
                        shell.publish(message);
                    }
                }
            }
            Event::Mouse(mouse::Event::WheelScrolled { delta }) => {
                if let Some(position) = position {
                    if let Some(message) = self.wheel_action.wheel_at(PointerWheel {
                        delta: wheel_delta(*delta),
                        position,
                    }) {
                        shell.publish(message);
                    }
                }
            }
            Event::Keyboard(keyboard::Event::KeyPressed { key, .. })
                if matches!(key, keyboard::Key::Named(keyboard::key::Named::Escape)) =>
            {
                if let Some(message) = self.escape_action.press() {
                    shell.publish(message);
                }
            }
            _ => {}
        }

        self.content.as_widget_mut().update(
            &mut tree.children[0],
            event,
            layout.children().next().unwrap(),
            cursor,
            renderer,
            clipboard,
            shell,
            viewport,
        );
    }

    fn operate(
        &mut self,
        tree: &mut Tree,
        layout: Layout<'_>,
        renderer: &iced::Renderer,
        operation: &mut dyn Operation,
    ) {
        self.content.as_widget_mut().operate(
            &mut tree.children[0],
            layout.children().next().unwrap(),
            renderer,
            operation,
        );
    }

    fn draw(
        &self,
        tree: &Tree,
        renderer: &mut iced::Renderer,
        theme: &iced::Theme,
        style: &renderer::Style,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
    ) {
        self.content.as_widget().draw(
            &tree.children[0],
            renderer,
            theme,
            style,
            layout.children().next().unwrap(),
            cursor,
            viewport,
        );
    }

    fn mouse_interaction(
        &self,
        tree: &Tree,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
        renderer: &iced::Renderer,
    ) -> mouse::Interaction {
        if pointer_position(layout.bounds(), cursor).is_some() {
            return mouse::Interaction::Crosshair;
        }

        self.content.as_widget().mouse_interaction(
            &tree.children[0],
            layout.children().next().unwrap(),
            cursor,
            viewport,
            renderer,
        )
    }
}

impl<'a, Message> From<PointerRegionWidget<'a, Message>> for IcedElement<'a, Message>
where
    Message: 'a + Clone,
{
    fn from(region: PointerRegionWidget<'a, Message>) -> Self {
        Self::new(region)
    }
}

fn pointer_position(bounds: Rectangle, cursor: mouse::Cursor) -> Option<PointerPosition> {
    let position = cursor.position_in(bounds)?;
    Some(PointerPosition::new(
        position.x.round() as i32,
        position.y.round() as i32,
    ))
}

fn publish_pointer<Message>(
    action: &PointerRegionAction<Message>,
    position: PointerPosition,
    shell: &mut Shell<'_, Message>,
) {
    if let Some(message) = action.at(position) {
        shell.publish(message);
    }
}

fn pointer_distance_within(
    previous: PointerPosition,
    current: PointerPosition,
    max_distance: i32,
) -> bool {
    (current.x - previous.x).abs() <= max_distance && (current.y - previous.y).abs() <= max_distance
}

fn wheel_delta(delta: mouse::ScrollDelta) -> i32 {
    match delta {
        mouse::ScrollDelta::Lines { y, .. } => (y * 120.0).round() as i32,
        mouse::ScrollDelta::Pixels { y, .. } => y.round() as i32,
    }
}

fn text_editor_key_binding<Message: Clone>(
    bindings: &[TextEditorKeyBinding<Message>],
    key_press: &iced_text_editor_state::KeyPress,
) -> Option<iced_text_editor_state::Binding<Message>> {
    if !matches!(
        key_press.status,
        iced_text_editor_state::Status::Focused { .. }
    ) {
        return None;
    }

    let key = text_editor_key_from_iced(&key_press.key)?;
    let modifiers = TextEditorKeyModifiers {
        shift: key_press.modifiers.shift(),
        control: key_press.modifiers.control(),
        alt: key_press.modifiers.alt(),
        logo: key_press.modifiers.logo(),
    };

    bindings
        .iter()
        .find(|binding| binding.key == key && binding.modifiers == modifiers)
        .map(|binding| iced_text_editor_state::Binding::Custom(binding.message.clone()))
}

fn text_editor_key_from_iced(key: &keyboard::Key) -> Option<TextEditorKey> {
    match key.as_ref() {
        keyboard::Key::Named(keyboard::key::Named::Enter) => Some(TextEditorKey::Enter),
        keyboard::Key::Named(keyboard::key::Named::Tab) => Some(TextEditorKey::Tab),
        keyboard::Key::Named(keyboard::key::Named::Escape) => Some(TextEditorKey::Escape),
        keyboard::Key::Named(keyboard::key::Named::ArrowUp) => Some(TextEditorKey::ArrowUp),
        keyboard::Key::Named(keyboard::key::Named::ArrowDown) => Some(TextEditorKey::ArrowDown),
        _ => None,
    }
}

static DEFAULT_APP_ICON_SVG: &[u8] = br##"<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16" shape-rendering="crispEdges"><path fill="#000000" fill-opacity="0.004" d="M1 0h1v1H1zM14 0h1v1H14zM0 1h1v1H0zM0 14h1v1H0zM1 15h1v1H1z"/><path fill="#000000" fill-opacity="0.008" d="M15 1h1v1H15zM15 14h1v1H15zM14 15h1v1H14z"/><path fill="#000000" fill-opacity="0.020" d="M2 0h1v1H2zM13 0h1v1H13zM0 2h1v1H0zM15 2h1v1H15zM0 13h1v1H0zM2 15h1v1H2z"/><path fill="#000000" fill-opacity="0.024" d="M15 13h1v1H15zM13 15h1v1H13z"/><path fill="#000000" fill-opacity="0.035" d="M3 0h1v1H3zM12 0h1v1H12zM0 3h1v1H0zM15 3h1v1H15zM0 12h1v1H0zM3 15h1v1H3zM12 15h1v1H12z"/><path fill="#000000" fill-opacity="0.039" d="M15 12h1v1H15z"/><path fill="#333333" fill-opacity="0.039" d="M1 1h1v1H1z"/><path fill="#000000" fill-opacity="0.043" d="M4 0h1v1H4zM11 0h1v1H11zM0 4h1v1H0zM15 4h1v1H15zM0 11h1v1H0zM15 11h1v1H15zM4 15h1v1H4zM11 15h1v1H11z"/><path fill="#000000" fill-opacity="0.047" d="M5 0h1v1H5zM6 0h1v1H6zM7 0h1v1H7zM8 0h1v1H8zM9 0h1v1H9zM10 0h1v1H10zM0 5h1v1H0zM15 5h1v1H15zM0 6h1v1H0zM15 6h1v1H15zM0 7h1v1H0zM15 7h1v1H15zM0 8h1v1H0zM15 8h1v1H15zM0 9h1v1H0zM15 9h1v1H15zM0 10h1v1H0zM15 10h1v1H15zM5 15h1v1H5zM6 15h1v1H6zM7 15h1v1H7zM8 15h1v1H8zM9 15h1v1H9zM10 15h1v1H10z"/><path fill="#404040" fill-opacity="0.047" d="M14 1h1v1H14zM1 14h1v1H1z"/><path fill="#3B3B3B" fill-opacity="0.051" d="M14 14h1v1H14z"/><path fill="#D7D7D7" fill-opacity="0.569" d="M2 1h1v1H2zM1 2h1v1H1z"/><path fill="#CECECE" fill-opacity="0.576" d="M1 13h1v1H1zM2 14h1v1H2z"/><path fill="#D4D4D4" fill-opacity="0.584" d="M13 1h1v1H13zM14 2h1v1H14z"/><path fill="#CECECE" fill-opacity="0.588" d="M14 13h1v1H14zM13 14h1v1H13z"/><path fill="#EEEEEE" fill-opacity="0.906" d="M1 12h1v1H1z"/><path fill="#F3F3F3" fill-opacity="0.906" d="M1 3h1v1H1z"/><path fill="#F5F5F5" fill-opacity="0.906" d="M3 1h1v1H3zM12 1h1v1H12z"/><path fill="#ECECEC" fill-opacity="0.910" d="M3 14h1v1H3z"/><path fill="#ECECEC" fill-opacity="0.914" d="M12 14h1v1H12z"/><path fill="#EFEFEF" fill-opacity="0.914" d="M14 12h1v1H14z"/><path fill="#F3F3F3" fill-opacity="0.914" d="M14 3h1v1H14z"/><path fill="#F7F7F7" fill-opacity="0.984" d="M1 11h1v1H1z"/><path fill="#FCFCFC" fill-opacity="0.984" d="M1 4h1v1H1z"/><path fill="#FDFDFD" fill-opacity="0.984" d="M4 1h1v1H4z"/><path fill="#FEFEFE" fill-opacity="0.984" d="M11 1h1v1H11z"/><path fill="#F5F5F5" fill-opacity="0.992" d="M4 14h1v1H4z"/><path fill="#F6F6F6" fill-opacity="0.992" d="M11 14h1v1H11z"/><path fill="#F7F7F7" fill-opacity="0.992" d="M14 11h1v1H14z"/><path fill="#FCFCFC" fill-opacity="0.992" d="M14 4h1v1H14z"/><path fill="#F9F9F9" fill-opacity="0.996" d="M1 10h1v1H1z"/><path fill="#FFFFFF" fill-opacity="0.996" d="M5 1h1v1H5zM10 1h1v1H10z"/><path fill="#000000" d="M3 7h1v1H3zM4 7h1v1H4zM6 7h1v1H6zM7 7h1v1H7zM3 8h1v1H3zM7 8h1v1H7zM3 9h1v1H3zM7 9h1v1H7zM3 10h1v1H3zM5 10h1v1H5zM7 10h1v1H7z"/><path fill="#171717" d="M10 5h1v1H10z"/><path fill="#181818" d="M4 11h1v1H4z"/><path fill="#1B1B1B" d="M6 8h1v1H6zM4 10h1v1H4zM6 10h1v1H6z"/><path fill="#212121" d="M4 8h1v1H4z"/><path fill="#272727" d="M8 8h1v1H8z"/><path fill="#2C2C2C" d="M10 6h1v1H10z"/><path fill="#323232" d="M2 8h1v1H2zM2 9h1v1H2z"/><path fill="#363636" d="M5 7h1v1H5z"/><path fill="#393939" d="M8 9h1v1H8z"/><path fill="#404040" d="M2 7h1v1H2z"/><path fill="#414141" d="M2 10h1v1H2z"/><path fill="#444444" d="M8 7h1v1H8z"/><path fill="#494949" d="M8 10h1v1H8z"/><path fill="#686868" d="M7 6h1v1H7z"/><path fill="#696969" d="M11 5h1v1H11z"/><path fill="#6C6C6C" d="M12 8h1v1H12z"/><path fill="#707070" d="M3 11h1v1H3z"/><path fill="#787878" d="M13 4h1v1H13zM5 9h1v1H5z"/><path fill="#7C7C7C" d="M7 4h1v1H7zM6 9h1v1H6z"/><path fill="#7F7F7F" d="M13 7h1v1H13z"/><path fill="#808080" d="M9 5h1v1H9zM4 9h1v1H4z"/><path fill="#868686" d="M7 5h1v1H7z"/><path fill="#898989" d="M13 6h1v1H13z"/><path fill="#8A8A8A" d="M13 5h1v1H13z"/><path fill="#9D9D9D" d="M10 8h1v1H10z"/><path fill="#A4A4A4" d="M11 8h1v1H11z"/><path fill="#A6A6A6" d="M10 4h1v1H10z"/><path fill="#ACACAC" d="M9 8h1v1H9z"/><path fill="#B4B4B4" d="M5 8h1v1H5z"/><path fill="#B5B5B5" d="M12 9h1v1H12z"/><path fill="#B8B8B8" d="M5 11h1v1H5z"/><path fill="#BCBCBC" d="M8 4h1v1H8zM12 4h1v1H12z"/><path fill="#BDBDBD" d="M9 4h1v1H9zM11 4h1v1H11zM11 7h1v1H11z"/><path fill="#C0C0C0" d="M3 12h1v1H3z"/><path fill="#C1C1C1" d="M4 6h1v1H4zM6 6h1v1H6z"/><path fill="#C2C2C2" d="M3 6h1v1H3zM5 6h1v1H5z"/><path fill="#C3C3C3" d="M9 7h1v1H9zM11 9h1v1H11z"/><path fill="#C6C6C6" d="M6 11h1v1H6z"/><path fill="#C7C7C7" d="M7 11h1v1H7z"/><path fill="#D2D2D2" d="M4 12h1v1H4z"/><path fill="#D8D8D8" d="M11 6h1v1H11z"/><path fill="#DBDBDB" d="M13 8h1v1H13z"/><path fill="#DFDFDF" d="M10 3h1v1H10zM11 3h1v1H11z"/><path fill="#E0E0E0" d="M8 3h1v1H8zM9 3h1v1H9zM12 3h1v1H12z"/><path fill="#E4E4E4" d="M9 6h1v1H9z"/><path fill="#E6E6E6" d="M10 7h1v1H10z"/><path fill="#E8E8E8" d="M2 6h1v1H2z"/><path fill="#E9E9E9" d="M2 11h1v1H2z"/><path fill="#EAEAEA" d="M8 11h1v1H8z"/><path fill="#EDEDED" d="M8 6h1v1H8z"/><path fill="#F3F3F3" d="M12 7h1v1H12z"/><path fill="#F6F6F6" d="M13 3h1v1H13zM5 14h1v1H5zM6 14h1v1H6zM7 14h1v1H7zM9 14h1v1H9z"/><path fill="#F7F7F7" d="M2 12h1v1H2zM5 12h1v1H5zM8 12h1v1H8zM10 12h1v1H10zM11 12h1v1H11zM13 12h1v1H13zM2 13h1v1H2zM3 13h1v1H3zM4 13h1v1H4zM5 13h1v1H5zM6 13h1v1H6zM7 13h1v1H7zM8 13h1v1H8zM9 13h1v1H9zM10 13h1v1H10zM11 13h1v1H11zM12 13h1v1H12zM13 13h1v1H13zM8 14h1v1H8zM10 14h1v1H10z"/><path fill="#F8F8F8" d="M7 3h1v1H7zM9 11h1v1H9zM10 11h1v1H10zM11 11h1v1H11zM12 11h1v1H12zM13 11h1v1H13zM6 12h1v1H6zM7 12h1v1H7zM9 12h1v1H9zM12 12h1v1H12z"/><path fill="#F9F9F9" d="M9 10h1v1H9zM10 10h1v1H10zM11 10h1v1H11zM12 10h1v1H12zM13 10h1v1H13zM14 10h1v1H14z"/><path fill="#FAFAFA" d="M1 8h1v1H1zM14 8h1v1H14zM1 9h1v1H1zM9 9h1v1H9zM10 9h1v1H10zM13 9h1v1H13zM14 9h1v1H14z"/><path fill="#FBFBFB" d="M1 7h1v1H1zM14 7h1v1H14z"/><path fill="#FCFCFC" d="M2 5h1v1H2zM3 5h1v1H3zM4 5h1v1H4zM5 5h1v1H5zM6 5h1v1H6zM14 5h1v1H14zM1 6h1v1H1zM14 6h1v1H14z"/><path fill="#FDFDFD" d="M2 4h1v1H2zM3 4h1v1H3zM5 4h1v1H5zM1 5h1v1H1z"/><path fill="#FEFEFE" d="M6 1h1v1H6zM7 1h1v1H7zM9 1h1v1H9zM2 2h1v1H2zM3 2h1v1H3zM4 2h1v1H4zM5 2h1v1H5zM6 2h1v1H6zM7 2h1v1H7zM9 2h1v1H9zM2 3h1v1H2zM3 3h1v1H3zM4 3h1v1H4zM5 3h1v1H5zM6 3h1v1H6zM4 4h1v1H4zM6 4h1v1H6z"/><path fill="#FFFFFF" d="M8 1h1v1H8zM8 2h1v1H8zM10 2h1v1H10zM11 2h1v1H11zM12 2h1v1H12zM13 2h1v1H13zM8 5h1v1H8zM12 5h1v1H12zM12 6h1v1H12z"/></svg>"##;

fn compile_title_bar<'a, Message, Provider>(
    token: &'a TitleBarToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let mut title_bits: Vec<IcedElement<'a, Message>> = Vec::new();

    if let Some(icon) = &token.icon {
        title_bits.push(title_bar_icon_element(icon, visual.text_primary));
    }

    title_bits.push(
        iced_text(token.title.clone())
            .font(title_bar_title_font())
            .size(title_bar_title_size(visual))
            .color(visual.text_primary)
            .into(),
    );

    if let Some(subtitle) = &token.subtitle {
        title_bits.push(
            iced_text(subtitle.clone())
                .font(text_font(TextStyle::Caption))
                .size(text_size(TextStyle::Caption, visual))
                .color(visual.text_primary)
                .into(),
        );
    }

    let title_cluster = iced_row(title_bits)
        .align_y(alignment::Vertical::Center)
        .spacing(8)
        .width(IcedLength::Shrink);

    let left = iced_container(title_cluster)
        .padding([0, 12])
        .height(IcedLength::Fixed(visual.title_bar_height))
        .align_y(alignment::Vertical::Center);

    // The title text plus the empty stretch beside it form the draggable
    // region. Caption buttons and command controls stay outside so their
    // clicks are not swallowed by the window-move gesture.
    let drag_region = iced_row(vec![
        left.into(),
        iced_space().width(IcedLength::Fill).into(),
    ])
    .height(IcedLength::Fixed(visual.title_bar_height))
    .width(IcedLength::Fill)
    .align_y(alignment::Vertical::Center);

    let drag_region: IcedElement<'a, Message> = match token.drag_action.press() {
        Some(message) => iced_mouse_area(drag_region).on_press(message).into(),
        None => drag_region.into(),
    };

    let mut right_controls = iced_row(Vec::new())
        .align_y(alignment::Vertical::Center)
        .spacing(0);
    for command in &token.commands {
        right_controls = right_controls.push(compile_view_with_text_editors_and_visual(
            command, provider, visual,
        ));
    }

    if token.show_caption_controls {
        right_controls = right_controls
            .push(caption_button(
                CaptionButtonKind::Minimize,
                &token.minimize_action,
                visual,
            ))
            .push(caption_button(
                CaptionButtonKind::ToggleMaximize,
                &token.toggle_maximize_action,
                visual,
            ))
            .push(caption_button(
                CaptionButtonKind::Close,
                &token.close_action,
                visual,
            ));
    }

    let row = iced_row(vec![drag_region, right_controls.into()])
        .height(IcedLength::Fixed(visual.title_bar_height))
        .width(IcedLength::Fill)
        .align_y(alignment::Vertical::Center);

    iced_container(row)
        .height(IcedLength::Fixed(visual.title_bar_height))
        .width(IcedLength::Fill)
        .style(move |_| title_bar_container_style(visual))
        .into()
}

fn compile_status_badge<'a, Message>(
    token: &StatusBadgeToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mut children: Vec<IcedElement<'a, Message>> = Vec::new();
    let severity = token.severity;

    if let Some(icon) = &token.icon {
        children.push(icon_element(icon, 12.0, visual.text_on_accent));
    } else {
        // .NET renders an 8x8 DIP Ellipse dot; the bullet glyph fills ~0.6em, so a
        // 13px font size yields an ~8px visual diameter to match the WinUI status pill.
        children.push(
            iced_text("●")
                .font(text_font(TextStyle::Caption))
                .size(13.0)
                .color(visual.text_on_accent)
                .into(),
        );
    }

    children.push(
        iced_text(token.label.clone())
            .font(text_font(TextStyle::Caption))
            .size(text_size(TextStyle::Caption, visual))
            .color(visual.text_on_accent)
            .into(),
    );

    iced_container(
        iced_row(children)
            .spacing(6)
            .align_y(alignment::Vertical::Center),
    )
    .padding([6, 12])
    .align_y(alignment::Vertical::Center)
    .style(move |_| status_badge_container_style(visual, severity))
    .into()
}

fn compile_info_bar<'a, Message>(
    token: &InfoBarToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let severity = token.severity;
    let accent = info_bar_accent_color(visual, severity);

    // Filled severity badge: a saturated circle with a white glyph (✓ / ! / i),
    // matching the WinUI InfoBar icons. A caller-supplied icon overrides it.
    let icon: IcedElement<'a, Message> = match &token.icon {
        Some(custom) => icon_element(custom, 16.0, accent),
        None => info_bar_badge(severity, accent, visual),
    };

    // Title (bold) over message (wrapping). Empty fields are skipped so a
    // title-only or message-only bar still renders tightly.
    let mut text_column: Vec<IcedElement<'a, Message>> = Vec::new();
    if !token.title.is_empty() {
        text_column.push(
            iced_text(token.title.clone())
                .font(text_font_for_value(TextStyle::BodyStrong, &token.title))
                .size(text_size(TextStyle::BodyStrong, visual))
                .color(visual.text_primary)
                .into(),
        );
    }
    if !token.message.is_empty() {
        text_column.push(
            iced_text(token.message.clone())
                .font(text_font_for_value(TextStyle::Body, &token.message))
                .size(text_size(TextStyle::Body, visual))
                .color(visual.text_primary)
                .into(),
        );
    }

    let body = iced_column(text_column).spacing(2).width(IcedLength::Fill);

    iced_container(
        iced_row(vec![icon, body.into()])
            .spacing(12)
            .align_y(alignment::Vertical::Top),
    )
    .width(IcedLength::Fill)
    .padding([12, 16])
    .style(move |_| info_bar_style(visual, severity))
    .into()
}

fn compile_flyout_button<'a, Message>(
    token: &'a FlyoutButtonToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    if token.state.enabled && matches!(token.action.kind(), ActionKind::SelectionInput) {
        let choices = token
            .items
            .iter()
            .filter(|item| item.enabled)
            .map(|item| ComboChoice {
                id: item.id.clone(),
                label: if item.checked {
                    format!("● {}", item.label)
                } else {
                    item.label.clone()
                },
            })
            .collect::<Vec<_>>();
        let action = token.action.clone();

        let trigger_width = if token.label.is_empty() {
            IcedLength::Fixed(24.0)
        } else {
            IcedLength::Shrink
        };

        let padding = token
            .padding
            .map(edges_padding)
            .unwrap_or_else(|| IcedPadding::from([2.0, 4.0]));
        let border_width = token.border_width.map(f32::from).unwrap_or(0.0);
        let radius = token.radius.map(f32::from).unwrap_or(visual.radius_control);

        let pick_list = iced_pick_list(choices, Option::<ComboChoice>::None, move |choice| {
            action
                .input_text(choice.id)
                .expect("flyout selection action must produce a message")
        })
        .placeholder(token.label.clone())
        .width(trigger_width)
        .padding(padding)
        .text_size(text_size(TextStyle::Body, visual))
        .style(move |_, status| flyout_pick_list_style(visual, status, border_width, radius))
        .menu_style(move |_| menu_style(visual));

        return if token.align_y != Alignment::Start {
            iced_container(pick_list)
                .align_y(vertical_alignment(token.align_y))
                .into()
        } else {
            pick_list.into()
        };
    }

    let kind = ButtonKind::Subtle;
    let padding = token
        .padding
        .map(edges_padding)
        .unwrap_or_else(|| IcedPadding::from([4.0, 8.0]));
    let control = iced_button(button_content(
        &token.label,
        kind,
        token.icon.as_ref(),
        None,
        None,
        visual,
        false,
    ))
    .padding(padding)
    .style(move |_, status| button_style(visual, kind, status));

    if token.align_y != Alignment::Start {
        iced_container(control)
            .align_y(vertical_alignment(token.align_y))
            .into()
    } else {
        control.into()
    }
}

fn compile_progress_ring<'a, Message>(
    token: &ProgressRingToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    if token.active {
        let ring: IcedElement<'a, Message> =
            Element::new(AnimatedProgressRing::new(token.size, visual.accent));

        if let Some(label) = &token.label {
            return iced_row(vec![ring, compile_text(label, TextStyle::Caption, visual)])
                .spacing(6)
                .align_y(alignment::Vertical::Center)
                .into();
        }

        return ring;
    }

    let label = token.label.as_deref().unwrap_or("");

    iced_text(label.to_string())
        .font(text_font(TextStyle::Caption))
        .size(token.size as f32)
        .color(visual.text_secondary)
        .into()
}

fn compile_progress_bar<'a, Message>(
    token: &ProgressBarToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let value = if token.active {
        token.value.unwrap_or(35.0)
    } else {
        0.0
    };
    let active = token.active;

    iced_progress_bar(0.0..=100.0, value)
        .length(iced_length(token.width))
        .girth(IcedLength::Fixed(f32::from(token.height)))
        .style(move |_| progress_bar_style(visual, active))
        .into()
}

const PROGRESS_RING_SEGMENTS: usize = 8;
const PROGRESS_RING_FRAME_MS: u64 = 100;

#[derive(Debug)]
struct AnimatedProgressRing {
    size: u16,
    color: Color,
}

impl AnimatedProgressRing {
    fn new(size: u16, color: Color) -> Self {
        Self { size, color }
    }
}

#[derive(Debug)]
struct AnimatedProgressRingState {
    started_at: Option<iced::time::Instant>,
    frame_index: usize,
}

impl AnimatedProgressRingState {
    fn new() -> Self {
        Self {
            started_at: None,
            frame_index: 0,
        }
    }

    fn tick(&mut self, active: bool, now: iced::time::Instant) -> (bool, bool) {
        if !active {
            let changed = self.started_at.take().is_some() || self.frame_index != 0;
            self.frame_index = 0;
            return (changed, false);
        }

        let started_at = *self.started_at.get_or_insert(now);
        let next_frame = progress_ring_frame_index(started_at, now);
        let changed = next_frame != self.frame_index;
        self.frame_index = next_frame;
        (changed, true)
    }
}

impl<Message> Widget<Message, iced::Theme, iced::Renderer> for AnimatedProgressRing {
    fn tag(&self) -> widget::tree::Tag {
        widget::tree::Tag::of::<AnimatedProgressRingState>()
    }

    fn state(&self) -> widget::tree::State {
        widget::tree::State::new(AnimatedProgressRingState::new())
    }

    fn size(&self) -> Size<IcedLength> {
        let size = f32::from(self.size);
        Size::new(IcedLength::Fixed(size), IcedLength::Fixed(size))
    }

    fn layout(
        &mut self,
        _tree: &mut Tree,
        _renderer: &iced::Renderer,
        limits: &layout::Limits,
    ) -> layout::Node {
        let size = f32::from(self.size);
        layout::Node::new(limits.resolve(
            IcedLength::Fixed(size),
            IcedLength::Fixed(size),
            Size::new(size, size),
        ))
    }

    fn update(
        &mut self,
        tree: &mut Tree,
        event: &Event,
        _layout: Layout<'_>,
        _cursor: mouse::Cursor,
        _renderer: &iced::Renderer,
        _clipboard: &mut dyn Clipboard,
        shell: &mut Shell<'_, Message>,
        _viewport: &Rectangle,
    ) {
        if let Event::Window(window::Event::RedrawRequested(now)) = event {
            let (_changed, animating) = tree
                .state
                .downcast_mut::<AnimatedProgressRingState>()
                .tick(true, *now);

            if animating {
                shell.request_redraw();
            }
        }
    }

    fn draw(
        &self,
        tree: &Tree,
        renderer: &mut iced::Renderer,
        _theme: &iced::Theme,
        _style: &renderer::Style,
        layout: Layout<'_>,
        _cursor: mouse::Cursor,
        viewport: &Rectangle,
    ) {
        let Some(bounds) = layout.bounds().intersection(viewport) else {
            return;
        };
        let widget_bounds = layout.bounds();
        let center = Point::new(
            widget_bounds.x + widget_bounds.width / 2.0,
            widget_bounds.y + widget_bounds.height / 2.0,
        );
        let state = tree.state.downcast_ref::<AnimatedProgressRingState>();
        let frame_index = state.frame_index;
        let dot_size = (widget_bounds.width.min(widget_bounds.height) / 5.5).max(2.0);
        let radius = (widget_bounds.width.min(widget_bounds.height) - dot_size) / 2.0;

        renderer.with_layer(bounds, |renderer| {
            for segment in 0..PROGRESS_RING_SEGMENTS {
                let angle = -std::f32::consts::FRAC_PI_2
                    + (segment as f32 / PROGRESS_RING_SEGMENTS as f32) * std::f32::consts::TAU;
                let alpha = progress_ring_segment_alpha(segment, frame_index);
                let point = Point::new(
                    center.x + angle.cos() * radius - dot_size / 2.0,
                    center.y + angle.sin() * radius - dot_size / 2.0,
                );

                renderer.fill_quad(
                    renderer::Quad {
                        bounds: Rectangle {
                            x: point.x,
                            y: point.y,
                            width: dot_size,
                            height: dot_size,
                        },
                        border: Border::default().rounded(dot_size / 2.0),
                        shadow: Shadow::default(),
                        snap: true,
                    },
                    self.color.scale_alpha(alpha),
                );
            }
        });
    }
}

fn progress_ring_frame_index(started_at: iced::time::Instant, now: iced::time::Instant) -> usize {
    let elapsed_ms = now.saturating_duration_since(started_at).as_millis() as u64;
    ((elapsed_ms / PROGRESS_RING_FRAME_MS) as usize) % PROGRESS_RING_SEGMENTS
}

fn progress_ring_segment_alpha(segment: usize, frame_index: usize) -> f32 {
    let distance = (segment + PROGRESS_RING_SEGMENTS - (frame_index % PROGRESS_RING_SEGMENTS))
        % PROGRESS_RING_SEGMENTS;
    (1.0 - (distance as f32 * 0.095)).clamp(0.22, 1.0)
}

fn compile_busy_overlay<'a, Message, Provider>(
    token: &'a BusyOverlayToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let content = compile_view_with_text_editors_and_visual(&token.content, provider, visual);
    let indicator: IcedElement<'a, Message> = iced_column(vec![
        compile_progress_ring(
            &ProgressRingToken {
                id: None,
                active: true,
                size: 20,
                label: None,
                a11y: win_fluent::A11yHint::default(),
            },
            visual,
        ),
        compile_text(
            token.label.as_deref().unwrap_or("Loading"),
            TextStyle::Caption,
            visual,
        ),
    ])
    .spacing(8)
    .align_x(alignment::Horizontal::Center)
    .into();

    Element::new(AnimatedBusyOverlay::new(
        content,
        indicator,
        token.active,
        token.opacity,
        token.fade_transition_ms,
        token.blocks_input,
        visual,
    ))
}

struct AnimatedBusyOverlay<'a, Message> {
    content: IcedElement<'a, Message>,
    indicator: IcedElement<'a, Message>,
    active: bool,
    opacity: f32,
    fade_transition_ms: u16,
    blocks_input: bool,
    visual: IcedVisualTheme,
}

impl<'a, Message> AnimatedBusyOverlay<'a, Message> {
    fn new(
        content: IcedElement<'a, Message>,
        indicator: IcedElement<'a, Message>,
        active: bool,
        opacity: f32,
        fade_transition_ms: u16,
        blocks_input: bool,
        visual: IcedVisualTheme,
    ) -> Self {
        Self {
            content,
            indicator,
            active,
            opacity,
            fade_transition_ms,
            blocks_input,
            visual,
        }
    }
}

#[derive(Debug)]
struct AnimatedBusyOverlayState {
    progress: f32,
    from: f32,
    target: f32,
    started_at: Option<iced::time::Instant>,
}

impl AnimatedBusyOverlayState {
    fn new(active: bool) -> Self {
        let progress = if active { 1.0 } else { 0.0 };
        Self {
            progress,
            from: progress,
            target: progress,
            started_at: None,
        }
    }

    fn set_target(&mut self, active: bool, duration_ms: u16) {
        let target = if active { 1.0 } else { 0.0 };
        if (self.target - target).abs() <= f32::EPSILON {
            return;
        }

        self.from = self.progress;
        self.target = target;
        self.started_at = None;

        if duration_ms == 0 {
            self.progress = target;
            self.from = target;
        }
    }

    fn tick(&mut self, now: iced::time::Instant, duration_ms: u16) -> (bool, bool) {
        if (self.progress - self.target).abs() <= 0.001 {
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return (false, false);
        }

        if duration_ms == 0 {
            let changed = (self.progress - self.target).abs() > 0.001;
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return (changed, false);
        }

        let previous = self.progress;
        let started_at = *self.started_at.get_or_insert(now);
        let elapsed_ms = now.saturating_duration_since(started_at).as_secs_f32() * 1000.0;
        self.progress = busy_overlay_fade_progress(self.from, self.target, elapsed_ms, duration_ms);

        if elapsed_ms >= f32::from(duration_ms) {
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return ((previous - self.progress).abs() > 0.001, false);
        }

        ((previous - self.progress).abs() > 0.001, true)
    }

    fn is_visible_or_targeting_visible(&self) -> bool {
        self.progress > 0.001 || self.target > 0.001
    }

    fn opacity(&self, requested_opacity: f32) -> f32 {
        requested_opacity.clamp(0.0, 1.0) * self.progress.clamp(0.0, 1.0)
    }
}

impl<Message> Widget<Message, iced::Theme, iced::Renderer> for AnimatedBusyOverlay<'_, Message> {
    fn tag(&self) -> widget::tree::Tag {
        widget::tree::Tag::of::<AnimatedBusyOverlayState>()
    }

    fn state(&self) -> widget::tree::State {
        widget::tree::State::new(AnimatedBusyOverlayState::new(self.active))
    }

    fn children(&self) -> Vec<Tree> {
        vec![Tree::new(&self.content), Tree::new(&self.indicator)]
    }

    fn diff(&self, tree: &mut Tree) {
        let children = [&self.content, &self.indicator];
        tree.diff_children(&children);
        tree.state
            .downcast_mut::<AnimatedBusyOverlayState>()
            .set_target(self.active, self.fade_transition_ms);
    }

    fn size(&self) -> Size<IcedLength> {
        self.content.as_widget().size()
    }

    fn layout(
        &mut self,
        tree: &mut Tree,
        renderer: &iced::Renderer,
        limits: &layout::Limits,
    ) -> layout::Node {
        let content = self
            .content
            .as_widget_mut()
            .layout(&mut tree.children[0], renderer, limits);
        let size = content.size();
        let indicator_limits = layout::Limits::new(Size::ZERO, size);
        let mut indicator = self.indicator.as_widget_mut().layout(
            &mut tree.children[1],
            renderer,
            &indicator_limits,
        );
        let indicator_size = indicator.size();
        indicator.move_to_mut(Point::new(
            ((size.width - indicator_size.width) / 2.0).max(0.0),
            ((size.height - indicator_size.height) / 2.0).max(0.0),
        ));

        layout::Node::with_children(size, vec![content, indicator])
    }

    fn update(
        &mut self,
        tree: &mut Tree,
        event: &Event,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        renderer: &iced::Renderer,
        clipboard: &mut dyn Clipboard,
        shell: &mut Shell<'_, Message>,
        viewport: &Rectangle,
    ) {
        let is_redraw = matches!(event, Event::Window(window::Event::RedrawRequested(_)));
        if let Event::Window(window::Event::RedrawRequested(now)) = event {
            let (changed, animating) = tree
                .state
                .downcast_mut::<AnimatedBusyOverlayState>()
                .tick(*now, self.fade_transition_ms);
            if changed {
                shell.invalidate_widgets();
            }
            if animating {
                shell.request_redraw();
            }
        }

        let state = tree.state.downcast_ref::<AnimatedBusyOverlayState>();
        let overlay_visible = state.is_visible_or_targeting_visible();
        if overlay_visible {
            self.indicator.as_widget_mut().update(
                &mut tree.children[1],
                event,
                layout.child(1),
                cursor,
                renderer,
                clipboard,
                shell,
                viewport,
            );
        }

        let blocks_input = self.blocks_input && overlay_visible && !is_redraw;
        if blocks_input {
            shell.capture_event();
            return;
        }

        self.content.as_widget_mut().update(
            &mut tree.children[0],
            event,
            layout.child(0),
            cursor,
            renderer,
            clipboard,
            shell,
            viewport,
        );
    }

    fn operate(
        &mut self,
        tree: &mut Tree,
        layout: Layout<'_>,
        renderer: &iced::Renderer,
        operation: &mut dyn Operation,
    ) {
        operation.container(None, layout.bounds());
        operation.traverse(&mut |operation| {
            self.content.as_widget_mut().operate(
                &mut tree.children[0],
                layout.child(0),
                renderer,
                operation,
            );

            if tree
                .state
                .downcast_ref::<AnimatedBusyOverlayState>()
                .is_visible_or_targeting_visible()
            {
                self.indicator.as_widget_mut().operate(
                    &mut tree.children[1],
                    layout.child(1),
                    renderer,
                    operation,
                );
            }
        });
    }

    fn draw(
        &self,
        tree: &Tree,
        renderer: &mut iced::Renderer,
        theme: &iced::Theme,
        style: &renderer::Style,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
    ) {
        self.content.as_widget().draw(
            &tree.children[0],
            renderer,
            theme,
            style,
            layout.child(0),
            cursor,
            viewport,
        );

        let state = tree.state.downcast_ref::<AnimatedBusyOverlayState>();
        let opacity = state.opacity(self.opacity);
        if opacity <= 0.001 {
            return;
        }

        let Some(clipped) = layout.bounds().intersection(viewport) else {
            return;
        };
        let bounds = layout.bounds();
        renderer.with_layer(clipped, |renderer| {
            renderer.fill_quad(
                renderer::Quad {
                    bounds,
                    border: Border::default(),
                    shadow: Shadow::default(),
                    snap: true,
                },
                Color::BLACK.scale_alpha(opacity),
            );
            self.indicator.as_widget().draw(
                &tree.children[1],
                renderer,
                theme,
                &renderer::Style {
                    text_color: self.visual.text_primary,
                },
                layout.child(1),
                cursor,
                viewport,
            );
        });
    }

    fn mouse_interaction(
        &self,
        tree: &Tree,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
        renderer: &iced::Renderer,
    ) -> mouse::Interaction {
        let state = tree.state.downcast_ref::<AnimatedBusyOverlayState>();
        if self.blocks_input
            && state.is_visible_or_targeting_visible()
            && cursor.is_over(layout.bounds())
        {
            return mouse::Interaction::Wait;
        }

        self.content.as_widget().mouse_interaction(
            &tree.children[0],
            layout.child(0),
            cursor,
            viewport,
            renderer,
        )
    }
}

fn busy_overlay_fade_progress(from: f32, target: f32, elapsed_ms: f32, duration_ms: u16) -> f32 {
    win_fluent::motion::Transition::fluent_content(duration_ms)
        .value_at(elapsed_ms, from, target)
        .clamp(0.0, 1.0)
}

/// Builds a single overlay stack-layer: a full-size container that aligns its
/// content, optionally paints a scrim behind it, and optionally captures input
/// (so content beneath cannot be interacted with). Shared by `compile_overlay`
/// and `compile_busy_overlay`.
fn overlay_layer_element<'a, Message>(
    content: IcedElement<'a, Message>,
    align_x: alignment::Horizontal,
    align_y: alignment::Vertical,
    scrim: Option<f32>,
    blocks_input: bool,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mut layer = iced_container(content)
        .width(IcedLength::Fill)
        .height(IcedLength::Fill)
        .align_x(align_x)
        .align_y(align_y);

    if let Some(opacity) = scrim {
        layer = layer.style(move |_| busy_overlay_style(visual, opacity));
    }

    if blocks_input {
        iced_opaque(layer)
    } else {
        layer.into()
    }
}

fn compile_overlay<'a, Message, Provider>(
    token: &'a OverlayToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let base = compile_view_with_text_editors_and_visual(&token.base, provider, visual);
    if token.layers.is_empty() {
        return base;
    }

    let mut stack = Vec::with_capacity(token.layers.len() + 1);
    stack.push(base);
    for layer in &token.layers {
        let content = compile_view_with_text_editors_and_visual(&layer.content, provider, visual);
        stack.push(overlay_layer_element(
            content,
            horizontal_alignment(layer.align_x),
            vertical_alignment(layer.align_y),
            layer.scrim,
            layer.blocks_input,
            visual,
        ));
    }

    iced_stack(stack).into()
}

fn compile_adaptive_switch<'a, Message, Provider>(
    token: &'a AdaptiveSwitchToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let breakpoint_width = f32::from(token.breakpoint_width);
    let wide = &token.wide;
    let narrow = &token.narrow;

    iced_responsive(move |size| {
        if size.width >= breakpoint_width {
            compile_view_with_text_editors_and_visual(wide, provider, visual)
        } else {
            compile_view_with_text_editors_and_visual(narrow, provider, visual)
        }
    })
    .into()
}

fn compile_wrap<'a, Message, Provider>(
    token: &'a WrapToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    // A true flow layout (WinUI `ItemsWrapGrid`): the widget measures each child
    // at layout time and packs as many as fit the available width onto each row
    // — capped at `max_columns` — wrapping to new rows as the window narrows.
    // This is computed during the widget's own layout pass (not via
    // `responsive`), so it is correct inside the settings scrollable where the
    // available height is unbounded.
    let compiled = token
        .children
        .iter()
        .map(|child| compile_view_with_text_editors_and_visual(child, provider, visual))
        .collect::<Vec<_>>();

    Element::new(WrapFlow::new(
        compiled,
        usize::from(token.max_columns.max(1)),
        f32::from(token.spacing),
        f32::from(token.run_spacing),
    ))
}

fn apply_layout_style<'a, Message>(
    content: IcedElement<'a, Message>,
    style: &FluentStyle,
    width: Length,
    height: Length,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    if !layout_style_needs_container(style) {
        return content;
    }

    iced_container(content)
        .width(iced_length(width))
        .height(iced_length(height))
        .style({
            let style = style.clone();
            move |_| utility_container_style(&style, visual)
        })
        .into()
}

fn layout_style_needs_container(style: &FluentStyle) -> bool {
    style.has("surface-card")
        || style.has("input-surface")
        || style.has("capture-tip")
        || style.has("info-bar")
        || style.has_prefix("bg-")
        || style.has("border")
        || style.has_prefix("rounded")
        || style.has_prefix("shadow")
}

/// Applies the geometric "box" properties (`max-w-*`, `mx-auto`, `m-*`) that are
/// parsed structurally on the layout token, on top of any visual styling.
///
/// `max-width` + centering uses the nested double-container idiom verified in
/// `nested_container_centers_and_caps_max_width_in_layout_engine`: a single
/// capped container collapses to its max-width and sits flush-left, so an OUTER
/// fill-width container is required to center the capped INNER container within
/// the available space. `margin` becomes an outer transparent container padding.
fn apply_layout_box<'a, Message, Theme, Renderer>(
    content: Element<'a, Message, Theme, Renderer>,
    token: &LayoutToken<Message>,
) -> Element<'a, Message, Theme, Renderer>
where
    Message: 'a,
    Theme: iced::widget::container::Catalog + 'a,
    Renderer: iced::advanced::Renderer + 'a,
{
    let mut element = content;

    if let Some(max) = token.max_width {
        let inner = iced_container(element)
            .max_width(f32::from(max))
            .width(IcedLength::Fill);
        element = if token.center_x {
            iced_container(inner).center_x(IcedLength::Fill).into()
        } else {
            inner.into()
        };
    } else if token.center_x {
        // `mx-auto` without an explicit max-width centers a bounded-width child;
        // a fill-width child stays full-bleed, matching CSS auto-margins.
        element = iced_container(element).center_x(IcedLength::Fill).into();
    }

    if let Some(max) = token.max_height {
        element = iced_container(element).max_height(f32::from(max)).into();
    }

    if !token.margin.is_zero() {
        element = iced_container(element)
            .padding(iced_padding_from_edges(token.margin))
            .into();
    }

    element
}

fn iced_padding_from_edges(edges: Edges) -> IcedPadding {
    IcedPadding {
        top: f32::from(edges.top),
        right: f32::from(edges.right),
        bottom: f32::from(edges.bottom),
        left: f32::from(edges.left),
    }
}

fn compile_card<'a, Message, Provider>(
    token: &'a CardToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let is_headerless_card = token.title.trim().is_empty()
        && token.description.is_none()
        && token.icon.is_none()
        && token.trailing.is_empty();
    let card_padding = if token.kind == CardKind::FloatingInput {
        iced::Padding {
            top: 10.0,
            right: 12.0,
            bottom: 10.0,
            left: 12.0,
        }
    } else {
        iced::Padding::from(visual.card_padding)
    };
    let mut layout = iced_column(Vec::new())
        .padding(card_padding)
        .spacing(u32::from(token.content_spacing))
        .width(IcedLength::Fill);

    if !is_headerless_card {
        let title = label_with_icon(&token.title, token.icon.as_ref(), visual);
        let mut text_column = iced_column(vec![iced_text(title.clone())
            .font(text_font_for_value(TextStyle::BodyStrong, &title))
            .size(13.0)
            .color(visual.text_secondary)
            .into()])
        .spacing(4);

        if let Some(description) = &token.description {
            text_column = text_column.push(compile_text(description, TextStyle::Caption, visual));
        }

        let mut trailing = iced_row(Vec::new()).spacing(8);
        for child in &token.trailing {
            trailing = trailing.push(compile_view_with_text_editors_and_visual(
                child, provider, visual,
            ));
        }

        let header: IcedElement<'a, Message> = if token.trailing.is_empty() {
            text_column.into()
        } else {
            iced_row(vec![
                text_column.into(),
                iced_space().width(IcedLength::Fill).into(),
                trailing.into(),
            ])
            .align_y(alignment::Vertical::Center)
            .width(IcedLength::Fill)
            .into()
        };
        layout = layout.push(header);
    }

    if let Some(content) = &token.content {
        layout = layout.push(compile_view_with_text_editors_and_visual(
            content, provider, visual,
        ));
    }

    let mut container = iced_container(layout).width(IcedLength::Fill).style({
        let kind = token.kind;
        move |_| card_container_style(visual, kind)
    });
    if let Some(max_height) = token.max_height {
        container = container.max_height(f32::from(max_height));
    }
    let mut element: IcedElement<'a, Message> = container.into();
    if !token.margin.is_zero() {
        element = iced_container(element)
            .padding(iced_padding_from_edges(token.margin))
            .into();
    }
    element
}

fn compile_text_editor<'a, Message, Provider>(
    token: &'a TextEditorToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let placeholder = token.placeholder.as_deref().unwrap_or_default();
    let use_single_line_input = token.secure
        || token.min_height.is_none()
            && token.max_height.is_some_and(|height| height <= 40)
            && token.key_bindings.is_empty();

    if !use_single_line_input {
        if let Some(content) = token.id.as_deref().and_then(provider) {
            let mut control = iced_text_editor(content)
                .placeholder(placeholder.to_string())
                .font(text_font(token.text_style))
                .size(text_size(token.text_style, visual))
                .style({
                    let chrome = token.chrome;
                    let state = token.state.clone();
                    move |_, status| {
                        text_editor_style(
                            visual,
                            text_editor_status_with_state(&state, status),
                            chrome,
                        )
                    }
                });

            if let Some(id) = &token.id {
                control = control.id(id.clone());
            }

            if let Some(padding) = token.padding {
                control = control.padding(edges_padding(padding));
            }

            if let Some(Length::Fixed(width)) = token.width {
                control = control.width(f32::from(width));
            }

            if !token.key_bindings.is_empty() {
                let key_bindings = token.key_bindings.clone();
                control = control.key_binding(move |key_press| {
                    text_editor_key_binding(&key_bindings, &key_press)
                        .or_else(|| iced_text_editor_state::Binding::from_key_press(key_press))
                });
            }

            if let Some(height) = token.min_height {
                control = control.min_height(u32::from(height));
            }

            if let Some(height) = token.max_height {
                control = control.max_height(u32::from(height));
            }

            if token.state.enabled && !token.read_only {
                if matches!(
                    token.action.kind(),
                    ActionKind::TextInput | ActionKind::SelectionInput
                ) {
                    let action = token.action.clone();
                    let current = content.clone();
                    control = control.on_action(move |edit| {
                        let mut next = current.clone();
                        next.perform(edit);
                        action
                            .input_text(next.text())
                            .expect("text editor action must produce a message")
                    });
                }
            }

            return control.into();
        }
    }

    let mut control = iced_text_input(placeholder, &token.text)
        .font(text_font(token.text_style))
        .size(text_size(token.text_style, visual))
        .secure(token.secure)
        .style({
            let chrome = token.chrome;
            let state = token.state.clone();
            move |_, status| {
                text_input_style(visual, text_input_status_with_state(&state, status), chrome)
            }
        });

    if let Some(icon) = &token.trailing_icon {
        let symbol = icon_symbol(&icon.icon);
        control = control.icon(iced::widget::text_input::Icon {
            font: icon_symbol_font(symbol),
            code_point: symbol,
            size: Some(Pixels(14.0)),
            spacing: f32::from(icon.spacing),
            side: iced::widget::text_input::Side::Right,
        });
    }

    if let Some(id) = &token.id {
        control = control.id(id.clone());
    }

    if let Some(padding) = token.padding {
        control = control.padding(edges_padding(padding));
    }

    if let Some(width) = token.width {
        control = control.width(iced_length(width));
    }

    if token.state.enabled && !token.read_only {
        if matches!(
            token.action.kind(),
            ActionKind::TextInput | ActionKind::SelectionInput
        ) {
            let action = token.action.clone();
            control = control.on_input(move |value| {
                action
                    .input_text(value)
                    .expect("text input action must produce a message")
            });
        }
    }

    control.into()
}

fn compile_combo_box<'a, Message>(
    items: &[ComboBoxItem],
    selected: Option<&str>,
    label: Option<&str>,
    placeholder: Option<&str>,
    width: Length,
    height: Length,
    action: &Action<Message>,
    state: &'a ControlState,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let choices = items
        .iter()
        .map(|item| ComboChoice {
            id: item.id.clone(),
            label: item.label.clone(),
        })
        .collect::<Vec<_>>();
    let selected = selected.and_then(|id| choices.iter().find(|item| item.id == id).cloned());

    if !state.enabled || !matches!(action.kind(), ActionKind::SelectionInput) {
        let placeholder = placeholder.or(label).unwrap_or("Select");
        return compile_read_only_combo_box(
            selected
                .as_ref()
                .map(|item| item.label.as_str())
                .or(label)
                .unwrap_or_default(),
            placeholder,
            width,
            height,
            state,
            visual,
        );
    }

    let action = action.clone();
    let padding = combo_box_padding_for_height(height);

    iced_pick_list(choices, selected, move |choice| {
        action
            .input_text(choice.id)
            .expect("selection action must produce a message")
    })
    .placeholder(placeholder.or(label).unwrap_or("Select"))
    .width(iced_length(width))
    .padding(padding)
    .text_size(text_size(TextStyle::Body, visual))
    .handle(iced::widget::pick_list::Handle::Static(
        iced::widget::pick_list::Icon {
            font: caption_icon_font(),
            code_point: '\u{E70D}',
            size: Some(Pixels(12.0)),
            line_height: iced_text_core::LineHeight::default(),
            shaping: iced_text_core::Shaping::Basic,
        },
    ))
    .style({
        let state = state.clone();
        move |_, status| pick_list_style_with_state(visual, status, &state)
    })
    .menu_style(move |_| menu_style(visual))
    .into()
}

fn compile_read_only_combo_box<'a, Message>(
    value: &str,
    placeholder: &str,
    width: Length,
    height: Length,
    state: &'a ControlState,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let disabled = !state.enabled;
    let text_color = if disabled {
        visual.text_secondary.scale_alpha(visual.disabled_opacity)
    } else if value.is_empty() {
        visual.text_secondary
    } else {
        visual.text_primary
    };
    let label = if value.is_empty() { placeholder } else { value };
    let content = iced_row(vec![
        iced_text(label.to_string())
            .size(text_size(TextStyle::Body, visual))
            .color(text_color)
            .width(IcedLength::Fill)
            .into(),
        iced_text("\u{E70D}")
            .font(caption_icon_font())
            .size(12.0)
            .color(if disabled {
                visual.text_secondary.scale_alpha(visual.disabled_opacity)
            } else {
                visual.text_secondary
            })
            .into(),
    ])
    .align_y(alignment::Vertical::Center)
    .spacing(8);

    iced_container(content)
        .width(iced_length(width))
        .height(iced_length(height))
        .padding([8, 12])
        .align_y(alignment::Vertical::Center)
        .style(move |_| read_only_combo_box_style(visual, state))
        .into()
}

fn combo_box_padding_for_height(height: Length) -> IcedPadding {
    let vertical = match height {
        Length::Fixed(value) if value >= 40 => 12.0,
        Length::Fixed(value) if value <= 28 => 6.0,
        _ => 8.0,
    };
    IcedPadding::from([vertical, 12.0])
}

fn layout_padding(uniform: u16, edges: Option<Edges>) -> IcedPadding {
    edges
        .map(edges_padding)
        .unwrap_or_else(|| IcedPadding::from(f32::from(uniform)))
}

fn edges_padding(edges: Edges) -> IcedPadding {
    IcedPadding {
        top: f32::from(edges.top),
        right: f32::from(edges.right),
        bottom: f32::from(edges.bottom),
        left: f32::from(edges.left),
    }
}

fn compile_expander<'a, Message, Provider>(
    token: &'a ExpanderToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let mut text_column = iced_column(vec![expander_title_content(
        &token.title,
        token.icon.as_ref(),
        visual,
    )])
    .spacing(4);

    if let Some(description) = &token.description {
        text_column = text_column.push(compile_text(description, TextStyle::Caption, visual));
    }

    let has_expander_toggle = token.action.kind() == ActionKind::BoolInput;
    let mut trailing = iced_row(Vec::new()).spacing(expander_header_trailing_spacing(
        !token.trailing.is_empty(),
        has_expander_toggle,
    ));
    for child in &token.trailing {
        trailing = trailing.push(compile_view_with_text_editors_and_visual(
            child, provider, visual,
        ));
    }

    let mut toggle_message = None;
    if has_expander_toggle {
        let icon = win_fluent::IconToken::with_glyph(
            "expander-chevron",
            if token.expanded {
                '\u{E70E}'
            } else {
                '\u{E70D}'
            },
        );
        let action = token.action.clone();
        let next_expanded = !token.expanded;
        toggle_message = Some(
            action
                .input_bool(next_expanded)
                .expect("expander action must produce a message"),
        );
        trailing = trailing.push(expander_chevron_element(&icon, visual));
    }

    let has_header_controls = !token.trailing.is_empty() || has_expander_toggle;
    let header_content: IcedElement<'a, Message> = if has_header_controls {
        iced_row(vec![
            text_column.width(IcedLength::Fill).into(),
            trailing.into(),
        ])
        .spacing(12)
        .width(IcedLength::Fill)
        .align_y(alignment::Vertical::Center)
        .into()
    } else {
        iced_row(vec![text_column.width(IcedLength::Fill).into()])
            .spacing(12)
            .width(IcedLength::Fill)
            .align_y(alignment::Vertical::Center)
            .into()
    };

    let header_state = token.header_state.clone();
    let header: IcedElement<'a, Message> = if let Some(message) = toggle_message {
        let header_style = token.header_style.clone();
        let header_button = iced_button(header_content)
            .padding([7, 15])
            .width(IcedLength::Fill)
            .style(move |_, status| {
                expander_header_button_style(
                    visual,
                    &header_style,
                    button_status_with_state(&header_state, status),
                )
            })
            .on_press(message);
        iced_container(header_button)
            .padding(1)
            .width(IcedLength::Fill)
            .into()
    } else {
        iced_container(header_content)
            .padding([8, 16])
            .width(IcedLength::Fill)
            .into()
    };

    let mut layout = iced_column(vec![header]).spacing(0).width(IcedLength::Fill);

    if token.expanded {
        if let Some(content) = &token.content {
            let content = compile_view_with_text_editors_and_visual(content, provider, visual);
            let divider = iced_container(iced_space())
                .width(IcedLength::Fill)
                .height(IcedLength::Fixed(1.0))
                .style(move |_| expander_content_divider_style(visual));
            layout = layout.push(divider);
            let content_style = token.content_style.clone();
            layout = layout.push(
                iced_container(content)
                    .width(IcedLength::Fill)
                    .padding(IcedPadding {
                        top: 24.0,
                        right: 16.0,
                        bottom: 18.0,
                        left: 16.0,
                    })
                    .style(move |_| expander_content_container_style(visual, &content_style)),
            );
        }
    }

    let container_header_state = token.header_state.clone();
    let container_header_style = token.header_style.clone();

    iced_container(layout)
        .width(IcedLength::Fill)
        .style(move |_| {
            expander_container_style_with_state(
                visual,
                &container_header_state,
                &container_header_style,
            )
        })
        .into()
}

fn expander_chevron_element<'a, Message>(
    icon: &win_fluent::IconToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    iced_container(icon_element(
        icon,
        button_icon_size(ButtonKind::Icon),
        visual.text_primary,
    ))
    .width(IcedLength::Fixed(32.0))
    .height(IcedLength::Fixed(32.0))
    .align_x(alignment::Horizontal::Center)
    .align_y(alignment::Vertical::Center)
    .into()
}

fn expander_header_button_style(
    visual: IcedVisualTheme,
    style: &FluentStyle,
    status: iced::widget::button::Status,
) -> iced::widget::button::Style {
    let _ = status;
    let background = expander_header_background_color(visual, style);

    iced::widget::button::Style {
        background: Some(Background::Color(background)),
        text_color: visual.text_primary,
        border: control_border_with_radius(Color::TRANSPARENT, 0.0, visual.radius_control),
        shadow: Shadow::default(),
        ..iced::widget::button::Style::default()
    }
}

fn expander_header_trailing_spacing(has_trailing: bool, has_toggle: bool) -> u32 {
    if has_trailing && has_toggle {
        20
    } else {
        8
    }
}

fn compile_settings_row<'a, Message, Provider>(
    token: &'a SettingsRowToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent> + 'a,
{
    let title = label_with_icon(&token.title, token.icon.as_ref(), visual);
    let mut layout = iced_column(vec![compile_text(&title, TextStyle::Subtitle, visual)])
        .padding(24)
        .spacing(12)
        .width(IcedLength::Fill);

    let mut trailing = iced_row(Vec::new()).spacing(8);
    for child in &token.trailing {
        trailing = trailing.push(compile_view_with_text_editors_and_visual(
            child, provider, visual,
        ));
    }

    if let Some(content) = &token.content {
        layout = layout.push(compile_view_with_text_editors_and_visual(
            content, provider, visual,
        ));
    }

    if !token.trailing.is_empty() {
        layout = layout.push(trailing.align_y(alignment::Vertical::Center));
    }

    if let Some(description) = &token.description {
        layout = layout.push(compile_text(description, TextStyle::Caption, visual));
    }

    if token.content_align_x != Alignment::Start {
        layout = layout.align_x(horizontal_alignment(token.content_align_x));
    }

    let mut container = iced_container(layout)
        .width(IcedLength::Fill)
        .style(move |_| settings_row_container_style(visual));

    if token.align_x != Alignment::Start {
        container = container.align_x(horizontal_alignment(token.align_x));
    }

    let mut element: IcedElement<'a, Message> = container.into();

    if !token.margin.is_zero() {
        element = iced_container(element)
            .padding(iced_padding_from_edges(token.margin))
            .into();
    }

    element
}

fn compile_result_card<'a, Message>(
    token: &'a ResultCardToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let content = result_item_column(
        &token.item,
        &token.copy_action,
        &token.speak_action,
        &token.replace_action,
        &token.retry_action,
        &token.toggle_action,
        token.collapse_transition,
        visual,
    )
    .padding(0)
    .spacing(0);

    iced_container(content)
        .style(move |_| result_card_container_style(visual))
        .into()
}

fn compile_result_list<'a, Message>(
    token: &'a ResultListToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mut list = iced_column(Vec::new()).spacing(8);

    for item in &token.items {
        list = list.push(
            iced_container(
                result_item_column(
                    item,
                    &token.copy_action,
                    &token.speak_action,
                    &token.replace_action,
                    &token.retry_action,
                    &token.toggle_action,
                    token.collapse_transition,
                    visual,
                )
                .width(IcedLength::Fill),
            )
            .width(IcedLength::Fill)
            .style(move |_| result_card_container_style(visual)),
        );
    }

    let needs_container =
        token.max_height.is_some() || token.padding.is_some() || token.border_width.is_some();
    if !needs_container {
        return list.width(IcedLength::Fill).into();
    }

    let mut container = iced_container(list.width(IcedLength::Fill)).width(IcedLength::Fill);
    if let Some(padding) = token.padding {
        container = container.padding(iced_padding_from_edges(padding));
    }
    if let Some(max_height) = token.max_height {
        container = container.max_height(f32::from(max_height));
    }
    if let Some(border_width) = token.border_width {
        container =
            container.style(move |_| result_list_container_style(visual, f32::from(border_width)));
    }

    container.into()
}

fn result_item_column<'a, Message>(
    item: &ResultItem,
    copy_action: &Action<Message>,
    speak_action: &Action<Message>,
    replace_action: &Action<Message>,
    retry_action: &Action<Message>,
    toggle_action: &Action<Message>,
    collapse_transition: CollapseTransition,
    visual: IcedVisualTheme,
) -> iced::widget::Column<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mut header_left_children: Vec<IcedElement<'a, Message>> = Vec::new();
    let mut header_right_children: Vec<IcedElement<'a, Message>> = Vec::new();
    let primary_color = if item.dimmed {
        visual.text_secondary.scale_alpha(visual.dimmed_opacity)
    } else {
        visual.text_primary
    };
    let secondary_color = if item.dimmed {
        visual.text_secondary.scale_alpha(visual.dimmed_opacity)
    } else {
        visual.text_secondary
    };

    if visual.mode != ThemeMode::Minimal {
        if let Some(icon) = &item.icon {
            let icon_content = if let Some(image) = icon.image {
                service_icon_image_sized(image, 16.0)
            } else {
                let icon_color = service_result_icon_color(icon, primary_color);
                icon_element(icon, 16.0, icon_color)
            };
            header_left_children.push(
                iced_container(icon_content)
                    .width(IcedLength::Fixed(22.0))
                    .height(IcedLength::Fixed(visual.result_header_height))
                    .align_y(alignment::Vertical::Center)
                    .into(),
            );
        }
    }

    header_left_children.push(
        iced_text(item.title.clone())
            .font(text_font(TextStyle::BodyStrong))
            .size(visual.caption_size)
            .color(primary_color)
            .into(),
    );

    let header_hint = item
        .body
        .trim()
        .is_empty()
        .then_some(item.pending_hint.as_deref())
        .flatten();
    let header_metadata = item.metadata.as_deref().or(header_hint);
    if let Some(metadata) = header_metadata {
        header_right_children.push(
            iced_text(metadata.to_string())
                .font(text_font(TextStyle::Caption))
                .size(10.0)
                .color(secondary_color)
                .into(),
        );
    }

    match item.status {
        ResultStatus::Loading | ResultStatus::Streaming => {
            header_right_children.push(compile_progress_ring(
                &ProgressRingToken {
                    id: None,
                    active: true,
                    size: 13,
                    label: None,
                    a11y: win_fluent::A11yHint::default(),
                },
                visual,
            ));
        }
        ResultStatus::Error => {
            header_right_children.push(
                iced_text("\u{E783}")
                    .font(caption_icon_font())
                    .size(12.0)
                    .color(visual.error)
                    .into(),
            );
            push_result_action(
                &mut header_right_children,
                &item.id,
                "Retry",
                win_fluent::IconToken::with_glyph("retry", '\u{E72C}'),
                retry_action,
                visual,
            );
        }
        ResultStatus::Ready => {}
    }

    if item.toggleable {
        // Chevron semantics follow the WinUI reference: a pending click-to-query
        // row points right, a collapsed expander points down, and an expanded
        // one points up.
        header_right_children.push(
            iced_text(if item.expanded {
                "\u{E70E}"
            } else if header_hint.is_some() {
                "\u{E76C}"
            } else {
                "\u{E70D}"
            })
            .font(caption_icon_font())
            .size(10.0)
            .color(secondary_color)
            .into(),
        );
    }

    let header_left = iced_row(header_left_children)
        .spacing(6)
        .align_y(alignment::Vertical::Center);
    let header_right = iced_row(header_right_children)
        .spacing(8)
        .align_y(alignment::Vertical::Center);
    let header_content = iced_row(vec![
        header_left.into(),
        iced_space().width(IcedLength::Fill).into(),
        header_right.into(),
    ])
    .height(IcedLength::Fixed(visual.result_header_height))
    .width(IcedLength::Fill)
    .align_y(alignment::Vertical::Center);

    let header_state = item.header_state.clone();
    let mut header = iced_button(header_content)
        .height(IcedLength::Fixed(visual.result_header_height))
        .padding([0, 8])
        .width(IcedLength::Fill)
        .style(move |_, status| {
            let status = button_status_with_state(&header_state, status);
            result_header_button_style(visual, status)
        });

    if item.toggleable && matches!(toggle_action.kind(), ActionKind::SelectionInput) {
        if let Some(message) = toggle_action.input_text(item.id.clone()) {
            header = header.on_press(message);
        }
    }

    let mut content = iced_column(vec![header.into()]).width(IcedLength::Fill);

    let body_text = if item.body.trim().is_empty() {
        item.pending_hint.as_deref().unwrap_or_default()
    } else {
        item.body.as_str()
    };

    if !body_text.trim().is_empty() {
        let body: IcedElement<'a, Message> = iced_container(
            iced_text(body_text.to_string())
                // Match the WinUI ServiceResultItem `ResultText` (FontSize=13);
                // BodyLarge (15) renders the translation noticeably larger than
                // the .NET result bar.
                .font(text_font(TextStyle::BodyLarge))
                .size(13.0)
                .color(if item.status == ResultStatus::Error {
                    visual.error
                } else {
                    primary_color
                })
                .width(IcedLength::Fill),
        )
        // Match the WinUI ServiceResultItem `ContentArea` (Padding="10,8,10,10");
        // the tight [2, 8] left the translation crowded against the header divider.
        .padding(IcedPadding {
            top: 8.0,
            right: 10.0,
            bottom: 10.0,
            left: 10.0,
        })
        .width(IcedLength::Fill)
        .into();

        let actions = result_action_buttons(
            &item.id,
            item.status,
            copy_action,
            speak_action,
            replace_action,
            visual,
        );
        let body = if actions.is_empty() {
            body
        } else {
            let action_overlay: IcedElement<'a, Message> = iced_container(
                iced_row(actions)
                    .spacing(2)
                    .align_y(alignment::Vertical::Center),
            )
            .height(IcedLength::Fixed(27.0))
            .align_y(alignment::Vertical::Top)
            .into();
            Element::new(HoverRevealActions::new(
                body,
                action_overlay,
                item.actions_visible,
            ))
        };

        content = content.push(animated_collapse(
            item.id.clone(),
            body,
            item.expanded,
            collapse_transition,
        ));
    }

    content
}

fn service_result_icon_color(icon: &win_fluent::IconToken, fallback: Color) -> Color {
    match icon.name {
        "service-bing" => Color::from_rgb8(0, 120, 212),
        "service-local-ai" => Color::from_rgb8(100, 92, 230),
        "service-mdx" => Color::from_rgb8(35, 134, 54),
        "service-ai" => Color::from_rgb8(16, 110, 190),
        _ => fallback,
    }
}

struct HoverRevealActions<'a, Message> {
    body: IcedElement<'a, Message>,
    actions: IcedElement<'a, Message>,
    forced_visible: bool,
}

const HOVER_REVEAL_TRANSITION_MS: u16 = 0;
const HOVER_REVEAL_SLIDE_DIPS: f32 = 0.0;
const HOVER_REVEAL_INTERACTIVE_PROGRESS: f32 = 0.85;

impl<'a, Message> HoverRevealActions<'a, Message> {
    fn new(
        body: IcedElement<'a, Message>,
        actions: IcedElement<'a, Message>,
        forced_visible: bool,
    ) -> Self {
        Self {
            body,
            actions,
            forced_visible,
        }
    }
}

#[derive(Debug, Default)]
struct HoverRevealActionsState {
    hovered: bool,
    progress: f32,
    from: f32,
    target: f32,
    started_at: Option<iced::time::Instant>,
}

impl HoverRevealActionsState {
    fn new(visible: bool) -> Self {
        let progress = if visible { 1.0 } else { 0.0 };
        Self {
            hovered: false,
            progress,
            from: progress,
            target: progress,
            started_at: None,
        }
    }

    fn set_hovered(&mut self, hovered: bool) -> bool {
        let changed = self.hovered != hovered;
        self.hovered = hovered;
        changed
    }

    fn set_target(&mut self, visible: bool, duration_ms: u16) {
        let target = if visible { 1.0 } else { 0.0 };
        if (self.target - target).abs() <= f32::EPSILON {
            return;
        }

        self.from = self.progress;
        self.target = target;
        self.started_at = None;

        if duration_ms == 0 {
            self.progress = target;
            self.from = target;
        }
    }

    fn tick(&mut self, now: iced::time::Instant, duration_ms: u16) -> bool {
        if (self.progress - self.target).abs() <= 0.001 {
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return false;
        }

        if duration_ms == 0 {
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return false;
        }

        let started_at = *self.started_at.get_or_insert(now);
        let elapsed_ms = now.duration_since(started_at).as_secs_f32() * 1000.0;
        self.progress = hover_reveal_progress(self.from, self.target, elapsed_ms, duration_ms);

        if elapsed_ms >= f32::from(duration_ms) {
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return false;
        }

        true
    }

    fn target_visible(&self, forced_visible: bool) -> bool {
        forced_visible || self.hovered
    }

    fn drawn(&self, forced_visible: bool) -> bool {
        self.target_visible(forced_visible) || self.progress > 0.001
    }

    fn interactive(&self, forced_visible: bool) -> bool {
        self.target_visible(forced_visible) && self.progress >= HOVER_REVEAL_INTERACTIVE_PROGRESS
    }
}

impl<Message> Widget<Message, iced::Theme, iced::Renderer> for HoverRevealActions<'_, Message> {
    fn tag(&self) -> widget::tree::Tag {
        widget::tree::Tag::of::<HoverRevealActionsState>()
    }

    fn state(&self) -> widget::tree::State {
        widget::tree::State::new(HoverRevealActionsState::new(self.forced_visible))
    }

    fn children(&self) -> Vec<Tree> {
        vec![Tree::new(&self.body), Tree::new(&self.actions)]
    }

    fn diff(&self, tree: &mut Tree) {
        let state = tree.state.downcast_mut::<HoverRevealActionsState>();
        let target_visible = state.target_visible(self.forced_visible);
        state.set_target(target_visible, HOVER_REVEAL_TRANSITION_MS);

        let children = [&self.body, &self.actions];
        tree.diff_children(&children);
    }

    fn size(&self) -> Size<IcedLength> {
        self.body.as_widget().size()
    }

    fn layout(
        &mut self,
        tree: &mut Tree,
        renderer: &iced::Renderer,
        limits: &layout::Limits,
    ) -> layout::Node {
        let body = self
            .body
            .as_widget_mut()
            .layout(&mut tree.children[0], renderer, limits);
        let size = body.size();
        let action_limits = layout::Limits::new(Size::ZERO, size);
        let mut actions =
            self.actions
                .as_widget_mut()
                .layout(&mut tree.children[1], renderer, &action_limits);
        let action_size = actions.size();
        actions.move_to_mut(Point::new((size.width - action_size.width).max(0.0), 0.0));

        layout::Node::with_children(size, vec![body, actions])
    }

    fn update(
        &mut self,
        tree: &mut Tree,
        event: &Event,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        renderer: &iced::Renderer,
        clipboard: &mut dyn Clipboard,
        shell: &mut Shell<'_, Message>,
        viewport: &Rectangle,
    ) {
        let hovered = cursor.is_over(layout.bounds());
        let state = tree.state.downcast_mut::<HoverRevealActionsState>();
        if state.set_hovered(hovered) {
            let target_visible = state.target_visible(self.forced_visible);
            state.set_target(target_visible, HOVER_REVEAL_TRANSITION_MS);
            shell.request_redraw();
        }

        if let Event::Window(window::Event::RedrawRequested(now)) = event {
            if state.tick(*now, HOVER_REVEAL_TRANSITION_MS) {
                shell.request_redraw();
            }
        }

        if state.interactive(self.forced_visible) {
            self.actions.as_widget_mut().update(
                &mut tree.children[1],
                event,
                layout.child(1),
                cursor,
                renderer,
                clipboard,
                shell,
                viewport,
            );

            if shell.is_event_captured() {
                return;
            }
        }

        self.body.as_widget_mut().update(
            &mut tree.children[0],
            event,
            layout.child(0),
            cursor,
            renderer,
            clipboard,
            shell,
            viewport,
        );
    }

    fn operate(
        &mut self,
        tree: &mut Tree,
        layout: Layout<'_>,
        renderer: &iced::Renderer,
        operation: &mut dyn Operation,
    ) {
        operation.container(None, layout.bounds());
        operation.traverse(&mut |operation| {
            self.body.as_widget_mut().operate(
                &mut tree.children[0],
                layout.child(0),
                renderer,
                operation,
            );

            if tree
                .state
                .downcast_ref::<HoverRevealActionsState>()
                .interactive(self.forced_visible)
            {
                self.actions.as_widget_mut().operate(
                    &mut tree.children[1],
                    layout.child(1),
                    renderer,
                    operation,
                );
            }
        });
    }

    fn draw(
        &self,
        tree: &Tree,
        renderer: &mut iced::Renderer,
        theme: &iced::Theme,
        style: &renderer::Style,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
    ) {
        self.body.as_widget().draw(
            &tree.children[0],
            renderer,
            theme,
            style,
            layout.child(0),
            cursor,
            viewport,
        );

        let state = tree.state.downcast_ref::<HoverRevealActionsState>();
        if !state.drawn(self.forced_visible) {
            return;
        }
        let slide_offset = hover_reveal_slide_offset(state.progress);

        if let Some(viewport) = layout.bounds().intersection(viewport) {
            renderer.with_layer(viewport, |renderer| {
                renderer.with_translation(Vector::new(slide_offset, 0.0), |renderer| {
                    self.actions.as_widget().draw(
                        &tree.children[1],
                        renderer,
                        theme,
                        style,
                        layout.child(1),
                        cursor,
                        &viewport,
                    );
                });
            });
        }
    }

    fn mouse_interaction(
        &self,
        tree: &Tree,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
        renderer: &iced::Renderer,
    ) -> mouse::Interaction {
        if tree
            .state
            .downcast_ref::<HoverRevealActionsState>()
            .interactive(self.forced_visible)
        {
            let action_interaction = self.actions.as_widget().mouse_interaction(
                &tree.children[1],
                layout.child(1),
                cursor,
                viewport,
                renderer,
            );
            if action_interaction != mouse::Interaction::None {
                return action_interaction;
            }
        }

        self.body.as_widget().mouse_interaction(
            &tree.children[0],
            layout.child(0),
            cursor,
            viewport,
            renderer,
        )
    }
}

/// Pure flow-layout placement: given each child's measured `size`, packs them
/// left-to-right and returns each child's top-left `Point` plus the overall
/// content `Size`. Wraps to a new row when the next child would exceed
/// `max_width` or the current row already holds `max_columns` items. Extracted
/// from [`WrapFlow::layout`] so the wrapping rule can be unit-tested.
fn flow_positions(
    sizes: &[Size],
    max_width: f32,
    spacing: f32,
    run_spacing: f32,
    max_columns: usize,
) -> (Vec<Point>, Size) {
    let max_columns = max_columns.max(1);
    let mut positions = Vec::with_capacity(sizes.len());
    let mut x = 0.0f32;
    let mut y = 0.0f32;
    let mut row_height = 0.0f32;
    let mut col_in_row = 0usize;
    let mut content_width = 0.0f32;

    for size in sizes {
        let wrap =
            col_in_row > 0 && (col_in_row >= max_columns || x + size.width > max_width + 0.5);
        if wrap {
            y += row_height + run_spacing;
            x = 0.0;
            row_height = 0.0;
            col_in_row = 0;
        }

        positions.push(Point::new(x, y));
        x += size.width + spacing;
        row_height = row_height.max(size.height);
        col_in_row += 1;
        content_width = content_width.max(x - spacing);
    }

    (positions, Size::new(content_width, y + row_height))
}

/// A flow-layout container: packs children left-to-right, wrapping to a new row
/// when the next child would exceed the available width or the row already holds
/// `max_columns` items. Mirrors WinUI `ItemsWrapGrid` and — unlike `responsive`
/// — computes its own finite height, so it is correct inside scrollables.
struct WrapFlow<'a, Message> {
    children: Vec<IcedElement<'a, Message>>,
    max_columns: usize,
    spacing: f32,
    run_spacing: f32,
}

impl<'a, Message> WrapFlow<'a, Message> {
    fn new(
        children: Vec<IcedElement<'a, Message>>,
        max_columns: usize,
        spacing: f32,
        run_spacing: f32,
    ) -> Self {
        Self {
            children,
            max_columns: max_columns.max(1),
            spacing,
            run_spacing,
        }
    }
}

impl<Message> Widget<Message, iced::Theme, iced::Renderer> for WrapFlow<'_, Message> {
    fn children(&self) -> Vec<Tree> {
        self.children.iter().map(Tree::new).collect()
    }

    fn diff(&self, tree: &mut Tree) {
        tree.diff_children(&self.children);
    }

    fn size(&self) -> Size<IcedLength> {
        Size {
            width: IcedLength::Fill,
            height: IcedLength::Shrink,
        }
    }

    fn layout(
        &mut self,
        tree: &mut Tree,
        renderer: &iced::Renderer,
        limits: &layout::Limits,
    ) -> layout::Node {
        let max_width = limits.max().width;
        let child_limits = layout::Limits::new(Size::ZERO, Size::new(max_width, f32::INFINITY));

        let mut nodes = Vec::with_capacity(self.children.len());
        let mut sizes = Vec::with_capacity(self.children.len());
        for (index, child) in self.children.iter_mut().enumerate() {
            let node =
                child
                    .as_widget_mut()
                    .layout(&mut tree.children[index], renderer, &child_limits);
            sizes.push(node.size());
            nodes.push(node);
        }

        let (positions, content_size) = flow_positions(
            &sizes,
            max_width,
            self.spacing,
            self.run_spacing,
            self.max_columns,
        );
        for (node, position) in nodes.iter_mut().zip(positions) {
            node.move_to_mut(position);
        }

        let width = if max_width.is_finite() {
            max_width
        } else {
            content_size.width
        };
        layout::Node::with_children(Size::new(width, content_size.height), nodes)
    }

    fn update(
        &mut self,
        tree: &mut Tree,
        event: &Event,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        renderer: &iced::Renderer,
        clipboard: &mut dyn Clipboard,
        shell: &mut Shell<'_, Message>,
        viewport: &Rectangle,
    ) {
        for (index, child) in self.children.iter_mut().enumerate() {
            child.as_widget_mut().update(
                &mut tree.children[index],
                event,
                layout.child(index),
                cursor,
                renderer,
                clipboard,
                shell,
                viewport,
            );
        }
    }

    fn operate(
        &mut self,
        tree: &mut Tree,
        layout: Layout<'_>,
        renderer: &iced::Renderer,
        operation: &mut dyn Operation,
    ) {
        operation.container(None, layout.bounds());
        operation.traverse(&mut |operation| {
            for (index, child) in self.children.iter_mut().enumerate() {
                child.as_widget_mut().operate(
                    &mut tree.children[index],
                    layout.child(index),
                    renderer,
                    operation,
                );
            }
        });
    }

    fn draw(
        &self,
        tree: &Tree,
        renderer: &mut iced::Renderer,
        theme: &iced::Theme,
        style: &renderer::Style,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
    ) {
        for (index, child) in self.children.iter().enumerate() {
            child.as_widget().draw(
                &tree.children[index],
                renderer,
                theme,
                style,
                layout.child(index),
                cursor,
                viewport,
            );
        }
    }

    fn mouse_interaction(
        &self,
        tree: &Tree,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
        renderer: &iced::Renderer,
    ) -> mouse::Interaction {
        let mut interaction = mouse::Interaction::None;
        for (index, child) in self.children.iter().enumerate() {
            let child_interaction = child.as_widget().mouse_interaction(
                &tree.children[index],
                layout.child(index),
                cursor,
                viewport,
                renderer,
            );
            if child_interaction != mouse::Interaction::None {
                interaction = child_interaction;
            }
        }
        interaction
    }
}

fn hover_reveal_progress(from: f32, target: f32, elapsed_ms: f32, duration_ms: u16) -> f32 {
    win_fluent::motion::Transition::fluent_content(duration_ms)
        .value_at(elapsed_ms, from, target)
        .clamp(0.0, 1.0)
}

fn hover_reveal_slide_offset(progress: f32) -> f32 {
    (1.0 - progress.clamp(0.0, 1.0)) * HOVER_REVEAL_SLIDE_DIPS
}

fn result_action_buttons<'a, Message>(
    item_id: &str,
    status: ResultStatus,
    copy_action: &Action<Message>,
    speak_action: &Action<Message>,
    replace_action: &Action<Message>,
    visual: IcedVisualTheme,
) -> Vec<IcedElement<'a, Message>>
where
    Message: Clone + Send + 'static,
{
    let mut actions = Vec::new();
    if status != ResultStatus::Ready {
        return actions;
    }

    push_result_action(
        &mut actions,
        item_id,
        "Copy",
        icon::copy(),
        copy_action,
        visual,
    );
    push_result_action(
        &mut actions,
        item_id,
        "Replace",
        win_fluent::IconToken::with_glyph("replace", '\u{E8AC}'),
        replace_action,
        visual,
    );
    push_result_action(
        &mut actions,
        item_id,
        "Speak",
        icon::speaker(),
        speak_action,
        visual,
    );

    actions
}

fn push_result_action<'a, Message>(
    actions: &mut Vec<IcedElement<'a, Message>>,
    item_id: &str,
    label: &str,
    icon: win_fluent::IconToken,
    action: &Action<Message>,
    visual: IcedVisualTheme,
) where
    Message: Clone + Send + 'static,
{
    let message = action
        .press()
        .or_else(|| action.input_text(item_id.to_string()));
    let Some(message) = message else {
        return;
    };
    let mut button = iced_button(button_content(
        label,
        ButtonKind::ResultAction,
        Some(&icon),
        None,
        None,
        visual,
        false,
    ))
    .width(IcedLength::Fixed(visual.result_action_button_size))
    .height(IcedLength::Fixed(visual.result_action_button_size))
    .padding(0)
    .style(move |_, status| button_style(visual, ButtonKind::ResultAction, status));
    button = button.on_press(message);
    actions.push(button.into());
}

fn animated_collapse<'a, Message>(
    trace_label: String,
    content: impl Into<IcedElement<'a, Message>>,
    expanded: bool,
    transition: CollapseTransition,
) -> IcedElement<'a, Message>
where
    Message: 'a,
{
    AnimatedCollapse::new(trace_label, content, expanded, transition).into()
}

struct AnimatedCollapse<'a, Message> {
    trace_label: String,
    content: IcedElement<'a, Message>,
    expanded: bool,
    transition: CollapseTransition,
}

#[derive(Debug)]
struct AnimatedCollapseState {
    progress: f32,
    from: f32,
    target: f32,
    started_at: Option<iced::time::Instant>,
    trace_id: u64,
    last_visible_height: f32,
    last_child_height: f32,
}

impl AnimatedCollapseState {
    fn new(expanded: bool) -> Self {
        let progress = if expanded { 1.0 } else { 0.0 };

        Self {
            progress,
            from: progress,
            target: progress,
            started_at: None,
            trace_id: 0,
            last_visible_height: 0.0,
            last_child_height: 0.0,
        }
    }

    fn set_target(&mut self, trace_label: &str, expanded: bool, transition: CollapseTransition) {
        let target = if expanded { 1.0 } else { 0.0 };

        if (self.target - target).abs() <= f32::EPSILON {
            return;
        }

        self.from = self.progress;
        self.target = target;
        self.started_at = None;
        self.trace_id = next_collapse_trace_id();
        trace_collapse_sample(CollapseTraceRecord {
            trace_label,
            trace_id: self.trace_id,
            event: "target",
            elapsed_ms: 0.0,
            duration_ms: f32::from(transition.duration_ms),
            from: self.from,
            target: self.target,
            progress: self.progress,
            visible_height: self.last_visible_height,
            child_height: self.last_child_height,
        });

        if transition.duration_ms == 0 {
            self.progress = target;
            self.from = target;
        }
    }

    fn tick(
        &mut self,
        trace_label: &str,
        now: iced::time::Instant,
        transition: CollapseTransition,
    ) -> (bool, bool) {
        if (self.progress - self.target).abs() <= 0.001 {
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return (false, false);
        }

        if transition.duration_ms == 0 {
            let changed = (self.progress - self.target).abs() > 0.001;
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return (changed, false);
        }

        let previous = self.progress;
        let started_at = *self.started_at.get_or_insert(now);
        let elapsed_ms = now.duration_since(started_at).as_secs_f32() * 1000.0;
        let motion = if self.target >= self.from {
            transition.expand_transition()
        } else {
            transition.collapse_transition()
        };
        self.progress = motion
            .value_at(elapsed_ms, self.from, self.target)
            .clamp(0.0, 1.0);
        trace_collapse_sample(CollapseTraceRecord {
            trace_label,
            trace_id: self.trace_id,
            event: "redraw",
            elapsed_ms,
            duration_ms: f32::from(transition.duration_ms),
            from: self.from,
            target: self.target,
            progress: self.progress,
            visible_height: self.last_visible_height,
            child_height: self.last_child_height,
        });

        if elapsed_ms >= f32::from(transition.duration_ms) {
            self.progress = self.target;
            self.from = self.target;
            self.started_at = None;
            return ((previous - self.progress).abs() > 0.001, false);
        }

        ((previous - self.progress).abs() > 0.001, true)
    }
}

impl<'a, Message> AnimatedCollapse<'a, Message> {
    fn new(
        trace_label: String,
        content: impl Into<IcedElement<'a, Message>>,
        expanded: bool,
        transition: CollapseTransition,
    ) -> Self {
        Self {
            trace_label,
            content: content.into(),
            expanded,
            transition,
        }
    }
}

impl<Message> Widget<Message, iced::Theme, iced::Renderer> for AnimatedCollapse<'_, Message> {
    fn tag(&self) -> widget::tree::Tag {
        widget::tree::Tag::of::<AnimatedCollapseState>()
    }

    fn state(&self) -> widget::tree::State {
        widget::tree::State::new(AnimatedCollapseState::new(self.expanded))
    }

    fn children(&self) -> Vec<Tree> {
        vec![Tree::new(&self.content)]
    }

    fn diff(&self, tree: &mut Tree) {
        tree.state
            .downcast_mut::<AnimatedCollapseState>()
            .set_target(&self.trace_label, self.expanded, self.transition);
        tree.diff_children(std::slice::from_ref(&self.content));
    }

    fn size(&self) -> Size<IcedLength> {
        Size::new(IcedLength::Fill, IcedLength::Shrink)
    }

    fn layout(
        &mut self,
        tree: &mut Tree,
        renderer: &iced::Renderer,
        limits: &layout::Limits,
    ) -> layout::Node {
        let state = tree.state.downcast_mut::<AnimatedCollapseState>();
        let progress = state.progress.clamp(0.0, 1.0);

        if progress <= 0.001 && state.target <= 0.001 {
            state.last_visible_height = 0.0;
            state.last_child_height = 0.0;
            trace_collapse_sample(CollapseTraceRecord {
                trace_label: &self.trace_label,
                trace_id: state.trace_id,
                event: "layout",
                elapsed_ms: 0.0,
                duration_ms: f32::from(self.transition.duration_ms),
                from: state.from,
                target: state.target,
                progress,
                visible_height: 0.0,
                child_height: 0.0,
            });
            let size = limits.resolve(
                IcedLength::Fill,
                IcedLength::Fixed(0.0),
                Size::new(0.0, 0.0),
            );

            return layout::Node::with_children(size, vec![layout::Node::new(Size::ZERO)]);
        }

        let mut child =
            self.content
                .as_widget_mut()
                .layout(&mut tree.children[0], renderer, limits);
        let child_size = child.size();
        child.translate_mut(Vector::new(
            0.0,
            -CollapseTransition::RESULT_BOX_BODY_TRANSLATION_DIP * (1.0 - progress),
        ));
        let height = (child_size.height * progress)
            .min(limits.max().height)
            .max(0.0);
        state.last_visible_height = height;
        state.last_child_height = child_size.height;
        trace_collapse_sample(CollapseTraceRecord {
            trace_label: &self.trace_label,
            trace_id: state.trace_id,
            event: "layout",
            elapsed_ms: 0.0,
            duration_ms: f32::from(self.transition.duration_ms),
            from: state.from,
            target: state.target,
            progress,
            visible_height: height,
            child_height: child_size.height,
        });
        let size = limits.resolve(
            IcedLength::Fill,
            IcedLength::Fixed(height),
            Size::new(child_size.width, height),
        );

        layout::Node::with_children(size, vec![child])
    }

    fn update(
        &mut self,
        tree: &mut Tree,
        event: &Event,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        renderer: &iced::Renderer,
        clipboard: &mut dyn Clipboard,
        shell: &mut Shell<'_, Message>,
        viewport: &Rectangle,
    ) {
        if let Event::Window(window::Event::RedrawRequested(now)) = event {
            let (changed, animating) = tree.state.downcast_mut::<AnimatedCollapseState>().tick(
                &self.trace_label,
                *now,
                self.transition,
            );

            if changed {
                shell.invalidate_layout();
            }

            if animating {
                shell.request_redraw();
            }
        }

        if layout.bounds().height <= 0.5 {
            return;
        }

        let viewport = layout.bounds().intersection(viewport).unwrap_or(*viewport);
        self.content.as_widget_mut().update(
            &mut tree.children[0],
            event,
            layout.children().next().unwrap(),
            cursor,
            renderer,
            clipboard,
            shell,
            &viewport,
        );
    }

    fn operate(
        &mut self,
        tree: &mut Tree,
        layout: Layout<'_>,
        renderer: &iced::Renderer,
        operation: &mut dyn Operation,
    ) {
        if layout.bounds().height <= 0.5 {
            return;
        }

        let Some(child_layout) = layout.children().next() else {
            return;
        };

        operation.container(None, layout.bounds());
        operation.traverse(&mut |operation| {
            self.content.as_widget_mut().operate(
                &mut tree.children[0],
                child_layout,
                renderer,
                operation,
            );
        });
    }

    fn draw(
        &self,
        tree: &Tree,
        renderer: &mut iced::Renderer,
        theme: &iced::Theme,
        style: &renderer::Style,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
    ) {
        if layout.bounds().height <= 0.5 {
            return;
        }

        if let Some(viewport) = layout.bounds().intersection(viewport) {
            self.content.as_widget().draw(
                &tree.children[0],
                renderer,
                theme,
                style,
                layout.children().next().unwrap(),
                cursor,
                &viewport,
            );
        }
    }

    fn mouse_interaction(
        &self,
        tree: &Tree,
        layout: Layout<'_>,
        cursor: mouse::Cursor,
        viewport: &Rectangle,
        renderer: &iced::Renderer,
    ) -> mouse::Interaction {
        if layout.bounds().height <= 0.5 {
            return mouse::Interaction::None;
        }

        self.content.as_widget().mouse_interaction(
            &tree.children[0],
            layout.children().next().unwrap(),
            cursor,
            viewport,
            renderer,
        )
    }

    fn overlay<'b>(
        &'b mut self,
        tree: &'b mut Tree,
        layout: Layout<'b>,
        renderer: &iced::Renderer,
        viewport: &Rectangle,
        translation: Vector,
    ) -> Option<overlay::Element<'b, Message, iced::Theme, iced::Renderer>> {
        if layout.bounds().height <= 0.5 {
            return None;
        }

        self.content.as_widget_mut().overlay(
            &mut tree.children[0],
            layout.children().next().unwrap(),
            renderer,
            viewport,
            translation,
        )
    }
}

impl<'a, Message> From<AnimatedCollapse<'a, Message>> for IcedElement<'a, Message>
where
    Message: 'a,
{
    fn from(animated: AnimatedCollapse<'a, Message>) -> Self {
        Self::new(animated)
    }
}

static COLLAPSE_TRACE_ID: AtomicU64 = AtomicU64::new(1);
static COLLAPSE_TRACE_FILE: OnceLock<Option<Mutex<File>>> = OnceLock::new();

struct CollapseTraceRecord<'a> {
    trace_label: &'a str,
    trace_id: u64,
    event: &'a str,
    elapsed_ms: f32,
    duration_ms: f32,
    from: f32,
    target: f32,
    progress: f32,
    visible_height: f32,
    child_height: f32,
}

fn next_collapse_trace_id() -> u64 {
    COLLAPSE_TRACE_ID.fetch_add(1, Ordering::Relaxed)
}

fn trace_collapse_sample(record: CollapseTraceRecord<'_>) {
    if record.trace_id == 0 {
        return;
    }

    let Some(file) = collapse_trace_file() else {
        return;
    };

    if let Ok(mut file) = file.lock() {
        let _ = writeln!(
            file,
            "{},{},{},{},{:.3},{:.3},{:.5},{:.5},{:.5},{:.3},{:.3}",
            trace_wall_ms(),
            record.trace_id,
            csv_escape(record.trace_label),
            record.event,
            record.elapsed_ms,
            record.duration_ms,
            record.from,
            record.target,
            record.progress,
            record.visible_height,
            record.child_height
        );
    }
}

fn collapse_trace_file() -> Option<&'static Mutex<File>> {
    COLLAPSE_TRACE_FILE
        .get_or_init(|| {
            let path = env::var_os("WINFLUENT_COLLAPSE_TRACE")?;
            let path = PathBuf::from(path);
            let path = if path.as_os_str() == "1" {
                PathBuf::from("winfluent-collapse-trace.csv")
            } else {
                path
            };

            if let Some(parent) = path.parent() {
                let _ = std::fs::create_dir_all(parent);
            }

            let mut file = OpenOptions::new()
                .create(true)
                .write(true)
                .truncate(true)
                .open(path)
                .ok()?;
            let _ = writeln!(
                file,
                "wall_ms,trace_id,label,event,elapsed_ms,duration_ms,from,target,progress,visible_height,child_height"
            );

            Some(Mutex::new(file))
        })
        .as_ref()
}

fn trace_wall_ms() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
}

fn csv_escape(value: &str) -> String {
    if value.contains([',', '"', '\n', '\r']) {
        format!("\"{}\"", value.replace('"', "\"\""))
    } else {
        value.to_string()
    }
}

fn compile_command<'a, Message>(
    command: &'a CommandToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mut control = iced_button(button_content(
        &command.label,
        ButtonKind::Standard,
        command.icon.as_ref(),
        None,
        None,
        visual,
        false,
    ))
    .style(move |_, status| button_style(visual, ButtonKind::Standard, status));
    if command.enabled {
        if let Some(message) = command.action.press() {
            control = control.on_press(message);
        }
    }
    control.into()
}

fn label_with_icon(
    label: &str,
    icon: Option<&win_fluent::IconToken>,
    visual: IcedVisualTheme,
) -> String {
    if visual.mode == ThemeMode::Minimal {
        return label.to_string();
    }

    match icon {
        Some(icon) => format!("{} {label}", icon_symbol(icon)),
        None => label.to_string(),
    }
}

fn expander_title_content<'a, Message>(
    title: &str,
    icon: Option<&win_fluent::IconToken>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let title_text = compile_text(title, TextStyle::BodyStrong, visual);
    if visual.mode == ThemeMode::Minimal {
        return title_text;
    }

    let Some(icon) = icon else {
        return title_text;
    };

    iced_row(vec![expander_icon_element(icon, visual), title_text])
        .spacing(10)
        .align_y(alignment::Vertical::Center)
        .into()
}

fn expander_icon_element<'a, Message>(
    icon: &win_fluent::IconToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    if let Some(image) = icon.image {
        return service_icon_image(image);
    }

    if let Some(color) = service_configuration_icon_color(icon) {
        return service_icon_badge(icon, color);
    }

    icon_element(icon, 20.0, visual.text_secondary)
}

fn compile_image<'a, Message>(
    token: &win_fluent::ImageToken,
    _visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    use std::collections::HashMap;
    use std::sync::{Mutex, OnceLock};

    // Raw BGRA dumps are large (a full screen is tens of MB); cache the decoded
    // handle per path so view rebuilds reuse the uploaded texture instead of
    // re-reading and re-swizzling the file every frame.
    static HANDLES: OnceLock<Mutex<HashMap<String, iced::widget::image::Handle>>> = OnceLock::new();

    let handle = {
        let mut cache = HANDLES
            .get_or_init(|| Mutex::new(HashMap::new()))
            .lock()
            .expect("image handle cache poisoned");
        match cache.get(&token.bgra_path) {
            Some(handle) => Some(handle.clone()),
            None => std::fs::read(&token.bgra_path).ok().and_then(|mut bytes| {
                let expected = (token.pixel_width as usize)
                    .checked_mul(token.pixel_height as usize)?
                    .checked_mul(4)?;
                if bytes.len() < expected {
                    return None;
                }
                bytes.truncate(expected);
                for pixel in bytes.chunks_exact_mut(4) {
                    pixel.swap(0, 2); // BGRA -> RGBA
                    pixel[3] = 0xFF; // GDI leaves alpha undefined
                }
                let handle = iced::widget::image::Handle::from_rgba(
                    token.pixel_width,
                    token.pixel_height,
                    bytes,
                );
                cache.insert(token.bgra_path.clone(), handle.clone());
                Some(handle)
            }),
        }
    };

    match handle {
        Some(handle) => iced_image(handle)
            .content_fit(iced::ContentFit::Fill)
            .width(iced_length(token.width))
            .height(iced_length(token.height))
            .into(),
        // Unreadable/incomplete pixel file: keep layout stable with an empty box.
        None => iced_space()
            .width(iced_length(token.width))
            .height(iced_length(token.height))
            .into(),
    }
}

fn service_icon_image<'a, Message>(image: &'static [u8]) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    service_icon_image_sized(image, 20.0)
}

fn service_icon_image_sized<'a, Message>(
    image: &'static [u8],
    size: f32,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    iced_image(iced::widget::image::Handle::from_bytes(image))
        .width(IcedLength::Fixed(size))
        .height(IcedLength::Fixed(size))
        .into()
}

fn service_icon_badge<'a, Message>(
    icon: &win_fluent::IconToken,
    background: Color,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let label = service_icon_badge_label(icon.name).to_string();
    iced_container(
        iced_text(label)
            .font(Font::DEFAULT)
            .size(8.0)
            .line_height(1.0)
            .color(Color::WHITE),
    )
    .width(IcedLength::Fixed(20.0))
    .height(IcedLength::Fixed(20.0))
    .align_x(alignment::Horizontal::Center)
    .align_y(alignment::Vertical::Center)
    .style(move |_| service_icon_badge_style(background))
    .into()
}

fn service_icon_badge_style(background: Color) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(background)
        .color(Color::WHITE)
        .border(Border::default().rounded(4.0))
}

fn service_icon_badge_label(name: &str) -> &'static str {
    match name {
        "service-deepl" => "D",
        "service-github" => "GH",
        "service-ollama" => "O",
        "service-openai" => "AI",
        "service-deepseek" => "D",
        "service-groq" => "G",
        "service-zhipu" => "Z",
        "service-gemini" => "G",
        "service-doubao" => "D",
        "service-caiyun" => "C",
        "service-youdao" => "Y",
        "service-volcano" => "V",
        "service-mdx" => "M",
        "service-mdx-encrypted" => "M",
        "service-windows-local-ai" | "service-local-ai" | "service-ai" => "AI",
        "service-custom-openai" => "AI",
        "service-builtin-ai" => "AI",
        "service-niutrans" => "N",
        _ => "",
    }
}

fn service_configuration_icon_color(icon: &win_fluent::IconToken) -> Option<Color> {
    match icon.name {
        "service-deepl" => Some(Color::from_rgb8(17, 35, 55)),
        "service-windows-local-ai" | "service-local-ai" => Some(Color::from_rgb8(112, 72, 232)),
        "service-ollama" => Some(Color::from_rgb8(43, 43, 43)),
        "service-openai" | "service-ai" | "service-custom-openai" => {
            Some(Color::from_rgb8(16, 163, 127))
        }
        "service-deepseek" => Some(Color::from_rgb8(74, 111, 255)),
        "service-groq" => Some(Color::from_rgb8(242, 78, 48)),
        "service-zhipu" => Some(Color::from_rgb8(87, 96, 255)),
        "service-github" => Some(Color::from_rgb8(36, 41, 47)),
        "service-gemini" => Some(Color::from_rgb8(66, 133, 244)),
        "service-builtin-ai" => Some(Color::from_rgb8(92, 91, 230)),
        "service-doubao" => Some(Color::from_rgb8(242, 81, 132)),
        "service-caiyun" => Some(Color::from_rgb8(43, 129, 255)),
        "service-niutrans" => Some(Color::from_rgb8(0, 111, 205)),
        "service-youdao" => Some(Color::from_rgb8(236, 65, 65)),
        "service-volcano" => Some(Color::from_rgb8(239, 68, 68)),
        "service-mdx" => Some(Color::from_rgb8(35, 134, 54)),
        "service-mdx-encrypted" => Some(Color::from_rgb8(116, 74, 22)),
        _ => None,
    }
}

fn button_content<'a, Message>(
    label: &str,
    kind: ButtonKind,
    icon: Option<&win_fluent::IconToken>,
    text_style: Option<TextStyle>,
    font_size: Option<u16>,
    visual: IcedVisualTheme,
    selected: bool,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let icon_color = button_foreground_color(kind, visual, selected);
    let text_style = if kind == ButtonKind::Tile {
        TextStyle::Caption
    } else {
        text_style.unwrap_or(TextStyle::Body)
    };
    let text_size = font_size
        .map(f32::from)
        .unwrap_or_else(|| text_size(text_style, visual));
    let icon_size = font_size
        .map(f32::from)
        .unwrap_or_else(|| button_icon_size(kind));

    match (kind, icon, label.trim().is_empty()) {
        (ButtonKind::Tile, Some(icon), false) => {
            let content = iced_column(vec![
                icon_element(icon, icon_size, icon_color),
                iced_text(label.to_string())
                    .font(text_font_for_value(text_style, label))
                    .size(
                        font_size
                            .map(f32::from)
                            .unwrap_or_else(|| button_text_size(kind, visual)),
                    )
                    .color(icon_color)
                    .into(),
            ])
            .spacing(6)
            .align_x(alignment::Horizontal::Center);

            iced_container(content)
                .width(IcedLength::Fill)
                .height(IcedLength::Fill)
                .align_x(alignment::Horizontal::Center)
                .align_y(alignment::Vertical::Center)
                .into()
        }
        (
            ButtonKind::Icon | ButtonKind::FloatingAction | ButtonKind::ResultAction,
            Some(icon),
            _,
        )
        | (_, Some(icon), true) => icon_element(icon, icon_size, icon_color),
        (_, Some(icon), false) => iced_row(vec![
            icon_element(icon, icon_size, icon_color),
            iced_text(label.to_string())
                .font(text_font_for_value(text_style, label))
                .size(text_size)
                .color(icon_color)
                .into(),
        ])
        .spacing(8)
        .align_y(alignment::Vertical::Center)
        .into(),
        (_, None, _) => iced_text(label.to_string())
            .font(text_font_for_value(text_style, label))
            .size(text_size)
            .color(icon_color)
            .into(),
    }
}

fn button_foreground_color(kind: ButtonKind, visual: IcedVisualTheme, selected: bool) -> Color {
    match kind {
        ButtonKind::Primary | ButtonKind::PrimaryRound => visual.text_on_accent,
        ButtonKind::Tile if selected => visual.selected_foreground,
        ButtonKind::Tile => visual.tile_foreground,
        ButtonKind::FloatingAction | ButtonKind::Link => visual.accent,
        _ => visual.text_primary,
    }
}

fn icon_element<'a, Message>(
    icon: &win_fluent::IconToken,
    size: f32,
    color: Color,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    // Image-backed tokens (e.g. service icons like Google) must render as the
    // bitmap; otherwise they fall back to `icon_symbol`'s default glyph. The
    // expander and result-list paths already do this — buttons funnel through
    // here, so handle it centrally.
    if let Some(image) = icon.image {
        return service_icon_image_sized(image, size);
    }

    let symbol = icon_symbol(icon);
    iced_text(symbol.to_string())
        .font(icon_symbol_font(symbol))
        .size(size)
        .line_height(1.0)
        .width(IcedLength::Fixed(size + 2.0))
        .height(IcedLength::Fixed(size + 2.0))
        .center()
        .color(color)
        .into()
}

fn title_bar_icon_element<'a, Message>(
    icon: &win_fluent::IconToken,
    color: Color,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    if icon.name == "app" {
        return iced::widget::svg(iced::widget::svg::Handle::from_memory(DEFAULT_APP_ICON_SVG))
            .width(IcedLength::Fixed(16.0))
            .height(IcedLength::Fixed(16.0))
            .into();
    }

    icon_element(icon, 16.0, color)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CaptionButtonKind {
    Minimize,
    ToggleMaximize,
    Close,
}

impl CaptionButtonKind {
    const fn glyph(self) -> char {
        match self {
            Self::Minimize => '\u{E921}',
            Self::ToggleMaximize => '\u{E922}',
            Self::Close => '\u{E8BB}',
        }
    }
}

fn caption_button<'a, Message>(
    kind: CaptionButtonKind,
    action: &Action<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let content = iced_container(
        iced_text(kind.glyph().to_string())
            .font(caption_icon_font())
            .size(10.0)
            .color(visual.text_primary),
    )
    .center_x(IcedLength::Fill)
    .center_y(IcedLength::Fill);

    let mut button = iced_button(content)
        .width(IcedLength::Fixed(visual.caption_button_width))
        .height(IcedLength::Fixed(visual.title_bar_height))
        .padding(0)
        .style(move |_, status| caption_button_style(visual, kind, status));

    if let Some(message) = action.press() {
        button = button.on_press(message);
    }

    button.into()
}

fn icon_symbol(icon: &win_fluent::IconToken) -> char {
    if let Some(glyph) = icon.glyph {
        return glyph;
    }

    match icon.name {
        "add" => '\u{E710}',
        "camera" => '\u{E722}',
        "check" => '\u{E8FB}',
        "clear" => '\u{E711}',
        "copy" => '\u{E8C8}',
        "delete" => '\u{E74D}',
        "edit" => '\u{E70F}',
        "help" => '\u{E897}',
        "keyboard" => '\u{E765}',
        "microphone" => '\u{E720}',
        "more" => '\u{E712}',
        "pin" => '\u{E718}',
        "play" => '\u{E768}',
        "search" => '\u{E721}',
        "settings" => '\u{E713}',
        "speaker" => '\u{E767}',
        "swap" => '\u{E8AB}',
        "translate" => '\u{E8C1}',
        _ => '\u{E8A5}',
    }
}

fn button_text_size(kind: ButtonKind, visual: IcedVisualTheme) -> f32 {
    match kind {
        ButtonKind::Icon | ButtonKind::ResultAction | ButtonKind::FloatingAction => 18.0,
        ButtonKind::Primary | ButtonKind::PrimaryRound => visual.body_size,
        ButtonKind::Standard | ButtonKind::Subtle | ButtonKind::Link | ButtonKind::Chip => {
            visual.body_size
        }
        ButtonKind::Tile => visual.caption_size,
    }
}

fn button_icon_size(kind: ButtonKind) -> f32 {
    match kind {
        ButtonKind::Icon => 18.0,
        // Match the WinUI ServiceResultItem action glyphs (FontIcon FontSize=12)
        // inside their 24x24 buttons; 18 renders them oversized and crowded.
        ButtonKind::ResultAction => 12.0,
        ButtonKind::FloatingAction => 16.0,
        ButtonKind::Primary => 20.0,
        ButtonKind::PrimaryRound => 16.0,
        ButtonKind::Standard | ButtonKind::Subtle | ButtonKind::Link | ButtonKind::Chip => 16.0,
        ButtonKind::Tile => 22.0,
    }
}

fn title_bar_title_font() -> Font {
    text_font(TextStyle::Caption)
}

fn title_bar_title_size(visual: IcedVisualTheme) -> f32 {
    text_size(TextStyle::Caption, visual)
}

fn text_size(style: TextStyle, visual: IcedVisualTheme) -> f32 {
    match style {
        TextStyle::Caption => visual.caption_size,
        TextStyle::CaptionSmall => 11.0,
        TextStyle::Body => visual.body_size,
        TextStyle::BodyLarge => visual.body_large_size,
        TextStyle::BodyStrong => visual.body_strong_size,
        TextStyle::Success => visual.body_strong_size,
        TextStyle::Warning => visual.body_strong_size,
        TextStyle::SectionTitle => 18.0,
        TextStyle::Subtitle => visual.subtitle_size,
        TextStyle::Title => visual.title_size,
        TextStyle::TitleLarge => visual.title_large_size,
    }
}

fn text_font(style: TextStyle) -> Font {
    let weight = match style {
        TextStyle::BodyStrong
        | TextStyle::Success
        | TextStyle::Warning
        | TextStyle::SectionTitle
        | TextStyle::Subtitle
        | TextStyle::Title
        | TextStyle::TitleLarge => font::Weight::Semibold,
        TextStyle::Caption | TextStyle::CaptionSmall | TextStyle::Body | TextStyle::BodyLarge => {
            font::Weight::Normal
        }
    };

    Font {
        family: font::Family::Name("Segoe UI Variable"),
        weight,
        ..Font::DEFAULT
    }
}

fn text_font_for_value(style: TextStyle, value: &str) -> Font {
    let mut font = text_font(style);
    if is_private_use_icon_text(value) {
        font.family = icon_font().family;
        return font;
    }
    if is_status_symbol_text(value) {
        font.family = font::Family::Name("Segoe UI Emoji");
        return font;
    }
    if contains_cjk(value) {
        font.family = font::Family::Name("Microsoft YaHei UI");
        if matches!(
            style,
            TextStyle::BodyStrong
                | TextStyle::SectionTitle
                | TextStyle::Success
                | TextStyle::Warning
                | TextStyle::Subtitle
                | TextStyle::Title
                | TextStyle::TitleLarge
        ) {
            font.weight = font::Weight::Medium;
        }
    }
    font
}

fn is_private_use_icon_text(value: &str) -> bool {
    let trimmed = value.trim();
    !trimmed.is_empty()
        && trimmed
            .chars()
            .all(|ch| ('\u{E000}'..='\u{F8FF}').contains(&ch))
}

fn is_status_symbol_text(value: &str) -> bool {
    matches!(value.trim(), "✓" | "✔" | "⚠")
}

fn icon_font() -> Font {
    Font::with_name("Segoe Fluent Icons")
}

fn caption_icon_font() -> Font {
    Font::with_name("Segoe MDL2 Assets")
}

fn icon_symbol_font(symbol: char) -> Font {
    if symbol >= '\u{E000}' && symbol <= '\u{F8FF}' {
        icon_font()
    } else {
        text_font(TextStyle::Body)
    }
}

fn horizontal_alignment(align: win_fluent::view::Alignment) -> alignment::Horizontal {
    match align {
        win_fluent::view::Alignment::Start | win_fluent::view::Alignment::Stretch => {
            alignment::Horizontal::Left
        }
        win_fluent::view::Alignment::Center => alignment::Horizontal::Center,
        win_fluent::view::Alignment::End => alignment::Horizontal::Right,
    }
}

fn vertical_alignment(align: win_fluent::view::Alignment) -> alignment::Vertical {
    match align {
        win_fluent::view::Alignment::Start | win_fluent::view::Alignment::Stretch => {
            alignment::Vertical::Top
        }
        win_fluent::view::Alignment::Center => alignment::Vertical::Center,
        win_fluent::view::Alignment::End => alignment::Vertical::Bottom,
    }
}

fn iced_length(length: Length) -> IcedLength {
    match length {
        Length::Shrink => IcedLength::Shrink,
        Length::Fill => IcedLength::Fill,
        Length::FillPortion(value) => IcedLength::FillPortion(value),
        Length::Fixed(value) => IcedLength::Fixed(f32::from(value)),
    }
}

fn scroll_direction(
    horizontal: ScrollPolicy,
    vertical: ScrollPolicy,
    scrollbars_visible: bool,
) -> iced::widget::scrollable::Direction {
    use iced::widget::scrollable::Direction;

    let horizontal_bar = scroll_bar(horizontal, scrollbars_visible);
    let vertical_bar = scroll_bar(vertical, scrollbars_visible);

    match (horizontal, vertical) {
        (ScrollPolicy::Never, _) => Direction::Vertical(vertical_bar),
        (_, ScrollPolicy::Never) => Direction::Horizontal(horizontal_bar),
        _ => Direction::Both {
            vertical: vertical_bar,
            horizontal: horizontal_bar,
        },
    }
}

fn scroll_bar(
    policy: ScrollPolicy,
    scrollbars_visible: bool,
) -> iced::widget::scrollable::Scrollbar {
    use iced::widget::scrollable::Scrollbar;

    match policy {
        ScrollPolicy::Always => visible_scrollbar(),
        ScrollPolicy::Auto if scrollbars_visible => visible_scrollbar(),
        ScrollPolicy::Auto | ScrollPolicy::Never => Scrollbar::hidden(),
    }
}

fn visible_scrollbar() -> iced::widget::scrollable::Scrollbar {
    use iced::widget::scrollable::Scrollbar;

    Scrollbar::new().width(4).scroller_width(2).margin(1)
}

fn distribute_children<'a, Message>(
    children: Vec<IcedElement<'a, Message>>,
    kind: LayoutKind,
    distribution: LayoutDistribution,
) -> Vec<IcedElement<'a, Message>>
where
    Message: Clone + Send + 'static,
{
    if distribution != LayoutDistribution::SpaceBetween || children.len() <= 1 {
        return children;
    }

    let mut distributed = Vec::with_capacity(children.len() * 2 - 1);
    for (index, child) in children.into_iter().enumerate() {
        if index > 0 {
            let spacer: IcedElement<'a, Message> = match kind {
                LayoutKind::Row => iced_space().width(IcedLength::Fill).into(),
                LayoutKind::Column => iced_space().height(IcedLength::Fill).into(),
            };
            distributed.push(spacer);
        }
        distributed.push(child);
    }

    distributed
}

fn empty<'a, Message>() -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    iced_text("").into()
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct IcedVisualTheme {
    mode: ThemeMode,
    background: Color,
    surface: Color,
    surface_alt: Color,
    selected_surface: Color,
    selected_foreground: Color,
    selected_border: Color,
    tile_surface: Color,
    tile_foreground: Color,
    tile_border: Color,
    input_surface: Color,
    result_surface: Color,
    result_header: Color,
    result_header_foreground: Color,
    result_header_hover: Color,
    button_hover: Color,
    button_pressed: Color,
    floating_input_surface: Color,
    floating_input_border: Color,
    floating_action_surface: Color,
    floating_action_border: Color,
    text_primary: Color,
    text_secondary: Color,
    text_on_accent: Color,
    border: Color,
    focus: Color,
    success: Color,
    disconnected: Color,
    warning: Color,
    error: Color,
    accent: Color,
    accent_hover: Color,
    accent_pressed: Color,
    accent_light: Color,
    accent_light_alt: Color,
    accent_dark: Color,
    caption_size: f32,
    body_size: f32,
    body_large_size: f32,
    body_strong_size: f32,
    subtitle_size: f32,
    title_size: f32,
    title_large_size: f32,
    radius_control: f32,
    stroke_control: f32,
    stroke_focus: f32,
    control_height: f32,
    icon_button_size: f32,
    compact_icon_button_size: f32,
    result_action_button_size: f32,
    primary_round_button_size: f32,
    floating_action_button_size: f32,
    title_bar_height: f32,
    caption_button_width: f32,
    card_padding: f32,
    result_header_height: f32,
    elevation_raised: f32,
    disabled_opacity: f32,
    dimmed_opacity: f32,
    floating_action_rest_opacity: f32,
    floating_action_hover_opacity: f32,
    floating_action_pressed_opacity: f32,
}

impl IcedVisualTheme {
    fn primary_icon_button_size(self) -> f32 {
        self.primary_round_button_size
    }

    fn from_tokens(theme: &ThemeTokens) -> Self {
        Self {
            mode: theme.mode,
            background: iced_color(theme.background),
            surface: iced_color(theme.surface),
            surface_alt: iced_color(theme.surface_alt),
            selected_surface: iced_color(theme.selected_surface),
            selected_foreground: iced_color(theme.selected_foreground),
            selected_border: iced_color(theme.selected_border),
            tile_surface: iced_color(theme.tile_surface),
            tile_foreground: iced_color(theme.tile_foreground),
            tile_border: iced_color(theme.tile_border),
            input_surface: iced_color(theme.input_surface),
            result_surface: iced_color(theme.result_surface),
            result_header: iced_color(theme.result_header),
            result_header_foreground: iced_color(theme.result_header_foreground),
            result_header_hover: iced_color(theme.result_header_hover),
            button_hover: iced_color(theme.button_hover),
            button_pressed: iced_color(theme.button_pressed),
            floating_input_surface: iced_color(theme.floating_input_surface),
            floating_input_border: iced_color(theme.floating_input_border),
            floating_action_surface: iced_color(theme.floating_action_surface),
            floating_action_border: iced_color(theme.floating_action_border),
            text_primary: iced_color(theme.text_primary),
            text_secondary: iced_color(theme.text_secondary),
            text_on_accent: iced_color(theme.accent_foreground),
            border: iced_color(theme.border),
            focus: iced_color(theme.focus),
            success: iced_color(theme.status_connected),
            disconnected: iced_color(theme.status_disconnected),
            warning: iced_color(theme.warning),
            error: iced_color(theme.status_error),
            accent: iced_color(theme.accent.base),
            accent_hover: iced_color(theme.accent_hover),
            accent_pressed: iced_color(theme.accent_pressed),
            accent_light: iced_color(theme.accent.light_1),
            accent_light_alt: iced_color(theme.accent.light_2),
            accent_dark: iced_color(theme.accent.dark_1),
            caption_size: theme.typography.caption,
            body_size: theme.typography.body,
            body_large_size: theme.typography.body_large,
            body_strong_size: theme.typography.body_strong,
            subtitle_size: theme.typography.subtitle,
            title_size: theme.typography.title,
            title_large_size: theme.typography.title_large,
            radius_control: theme.radius.control,
            stroke_control: theme.stroke.control,
            stroke_focus: theme.stroke.focus,
            control_height: theme.control.height,
            icon_button_size: theme.control.icon_button,
            compact_icon_button_size: theme.control.compact_icon_button,
            result_action_button_size: theme.control.result_action_button,
            primary_round_button_size: theme.control.primary_round_button,
            floating_action_button_size: theme.control.floating_action_button,
            title_bar_height: theme.control.title_bar_height,
            caption_button_width: theme.control.caption_button_width,
            card_padding: theme.control.card_padding,
            result_header_height: theme.control.result_header_height,
            elevation_raised: theme.elevation.raised,
            disabled_opacity: theme.effects.disabled_opacity,
            dimmed_opacity: theme.effects.dimmed_opacity,
            floating_action_rest_opacity: theme.effects.floating_action_rest_opacity,
            floating_action_hover_opacity: theme.effects.floating_action_hover_opacity,
            floating_action_pressed_opacity: theme.effects.floating_action_pressed_opacity,
        }
    }
}

fn iced_color(color: FluentColor) -> Color {
    Color::from_rgba8(color.r, color.g, color.b, f32::from(color.a) / 255.0)
}

fn page_container_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.background)
        .color(visual.text_primary)
}

fn title_bar_container_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.background)
        .color(visual.text_primary)
}

fn status_badge_container_style(
    visual: IcedVisualTheme,
    severity: ValidationSeverity,
) -> iced::widget::container::Style {
    let background = match severity {
        ValidationSeverity::Success => visual.success,
        ValidationSeverity::Warning => visual.warning,
        ValidationSeverity::Error => visual.error,
        ValidationSeverity::Info => visual.disconnected,
    };

    iced::widget::container::Style::default()
        .background(background)
        .color(visual.text_on_accent)
        .border(Border {
            // .NET status pill uses a 12 DIP corner radius (Colors.xaml).
            radius: 12.0.into(),
            ..Border::default()
        })
}

/// Saturated accent (icon + border hue) for an InfoBar severity. Mirrors the
/// WinUI `InfoBarSeverity` color mapping; `Info` uses the system accent (blue)
/// rather than the muted "disconnected" gray the status pill uses.
fn info_bar_accent_color(visual: IcedVisualTheme, severity: ValidationSeverity) -> Color {
    match severity {
        ValidationSeverity::Success => visual.success,
        ValidationSeverity::Warning => visual.warning,
        ValidationSeverity::Error => visual.error,
        ValidationSeverity::Info => visual.accent,
    }
}

/// White glyph centered in the filled severity badge. The check mark is a
/// Segoe Fluent glyph; `!`/`i` are plain characters so they render in the UI
/// font like the WinUI InfoBar caution/info badges.
fn info_bar_badge_mark(severity: ValidationSeverity) -> char {
    match severity {
        ValidationSeverity::Success => '\u{E8FB}',
        ValidationSeverity::Warning | ValidationSeverity::Error => '!',
        ValidationSeverity::Info => 'i',
    }
}

/// The filled circular severity badge (saturated fill + white glyph) that the
/// WinUI InfoBar shows at its leading edge.
fn info_bar_badge<'a, Message>(
    severity: ValidationSeverity,
    accent: Color,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mark = info_bar_badge_mark(severity).to_string();
    let glyph = iced_text(mark.clone())
        .font(text_font_for_value(TextStyle::BodyStrong, &mark))
        .size(11.0)
        .line_height(1.0)
        .color(visual.text_on_accent);

    iced_container(glyph)
        .width(IcedLength::Fixed(18.0))
        .height(IcedLength::Fixed(18.0))
        .align_x(alignment::Horizontal::Center)
        .align_y(alignment::Vertical::Center)
        .style(move |_| {
            iced::widget::container::Style::default()
                .background(accent)
                .color(visual.text_on_accent)
                .border(Border {
                    radius: 9.0.into(),
                    ..Border::default()
                })
        })
        .into()
}

fn info_bar_style(
    visual: IcedVisualTheme,
    severity: ValidationSeverity,
) -> iced::widget::container::Style {
    let accent = info_bar_accent_color(visual, severity);
    // WinUI InfoBar: Success/Warning/Error use a low-saturation tint of the
    // severity hue; Informational stays a neutral card (only the badge is
    // colored). Translucent fills composite over the host surface so the tint
    // tracks light/dark themes automatically.
    let (background, border_color) = match severity {
        ValidationSeverity::Info => (visual.surface_alt, visual.border),
        _ => (accent.scale_alpha(0.16), accent.scale_alpha(0.32)),
    };
    iced::widget::container::Style::default()
        .background(background)
        .color(visual.text_primary)
        .border(Border {
            color: border_color,
            width: 1.0,
            // Match the WinUI InfoBar, which inherits the app's 10 DIP
            // ControlCornerRadius (Colors.xaml) like the text boxes beside it.
            radius: visual.radius_control.into(),
        })
}

fn busy_overlay_style(visual: IcedVisualTheme, opacity: f32) -> iced::widget::container::Style {
    let _ = visual;
    iced::widget::container::Style::default()
        .background(Color::BLACK.scale_alpha(opacity.clamp(0.0, 1.0)))
        .color(Color::WHITE)
}

fn capture_overlay_frame_style(
    visual: IcedVisualTheme,
    border_color: Color,
    fill_color: Color,
    selected: bool,
) -> iced::widget::container::Style {
    let border_width = if selected {
        2.0
    } else {
        visual.stroke_focus.max(1.5)
    };
    iced::widget::container::Style::default()
        .background(fill_color)
        .border(Border {
            color: border_color,
            width: border_width,
            radius: 2.0.into(),
        })
}

fn capture_overlay_handle_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.surface)
        .border(Border {
            color: visual.accent,
            width: 2.0,
            radius: 2.0.into(),
        })
        .shadow(elevation_shadow(visual, 2.0))
}

fn capture_overlay_size_chip_style(
    visual: IcedVisualTheme,
    selected: bool,
) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(if selected {
            visual.accent
        } else {
            visual.warning
        })
        .color(visual.text_on_accent)
        .border(Border {
            radius: 6.0.into(),
            ..Border::default()
        })
        .shadow(elevation_shadow(visual, 2.0))
}

fn capture_overlay_magnifier_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.surface.scale_alpha(0.96))
        .color(visual.text_primary)
        .border(Border {
            color: visual.accent,
            width: 2.0,
            radius: 8.0.into(),
        })
        .shadow(elevation_shadow(visual, visual.elevation_raised))
}

fn utility_container_style(
    style: &FluentStyle,
    visual: IcedVisualTheme,
) -> iced::widget::container::Style {
    let mut container = iced::widget::container::Style::default().color(visual.text_primary);

    if style.has("capture-tip") {
        // The WinUI capture overlay tip bar: a dark chip (~78% black) with
        // white text, floating over the dim mask.
        container = container
            .background(Color::BLACK.scale_alpha(0.78))
            .color(Color::WHITE);
    } else if style.has("info-bar") {
        container = container.background(info_bar_background_color(visual));
    } else if style.has("input-surface") {
        container = container.background(visual.input_surface);
    } else if style.has("surface-card") {
        container = container.background(visual.surface);
    } else if style.has("bg-app") {
        container = container.background(visual.background);
    } else if style.has("bg-surface") {
        container = container.background(visual.surface);
    } else if style.has("bg-muted") || style.has("bg-surface-alt") {
        container = container.background(visual.surface_alt);
    } else if style.has("bg-border") {
        container = container.background(visual.border);
    } else if style.has("bg-accent") {
        container = container.background(visual.accent);
    }

    let radius = utility_radius(style, visual);
    let border_width = if style.has("border") || style.has("surface-card") {
        visual.stroke_control
    } else {
        0.0
    };

    let border_color = if style.has("info-bar") {
        info_bar_border_color(visual)
    } else if style.has("input-surface") {
        visual.floating_input_border
    } else {
        visual.border
    };

    container = container.border(Border {
        radius: radius.into(),
        width: border_width,
        color: border_color,
    });

    if let Some(shadow) = utility_shadow(style, visual) {
        container = container.shadow(shadow);
    }

    container
}

fn info_bar_background_color(visual: IcedVisualTheme) -> Color {
    if matches!(visual.mode, ThemeMode::HighContrast) || !is_light_surface(visual.surface) {
        visual.surface_alt
    } else {
        Color::from_rgb8(238, 239, 240)
    }
}

fn info_bar_border_color(visual: IcedVisualTheme) -> Color {
    if matches!(visual.mode, ThemeMode::HighContrast) || !is_light_surface(visual.surface) {
        visual.border
    } else {
        Color::from_rgb8(224, 226, 230)
    }
}

fn utility_radius(style: &FluentStyle, visual: IcedVisualTheme) -> f32 {
    match style.last_with_prefix("rounded") {
        Some("rounded-none") => 0.0,
        Some("rounded-sm") => 4.0,
        Some("rounded") | Some("rounded-md") => visual.radius_control,
        Some("rounded-lg") => 8.0,
        Some("rounded-xl") => 12.0,
        Some("rounded-2xl") => 16.0,
        Some("rounded-full") => 999.0,
        Some(value) => exact_rounded_radius(value).unwrap_or(0.0),
        _ if style.has("surface-card") => 12.0,
        _ => 0.0,
    }
}

fn exact_rounded_radius(class: &str) -> Option<f32> {
    let value = class.strip_prefix("rounded-[")?.strip_suffix(']')?;
    value
        .strip_suffix("px")
        .unwrap_or(value)
        .parse::<f32>()
        .ok()
}

fn utility_shadow(style: &FluentStyle, visual: IcedVisualTheme) -> Option<Shadow> {
    // Minimal and high-contrast themes are intentionally flat: elevation shadows
    // are suppressed (matching WinUI dropping `ThemeShadow` in the Minimal theme).
    if matches!(visual.mode, ThemeMode::Minimal | ThemeMode::HighContrast) {
        return None;
    }
    match style.last_with_prefix("shadow") {
        Some("shadow-none") => Some(Shadow::default()),
        Some("shadow-sm") => Some(elevation_shadow(visual, 2.0)),
        Some("shadow") | Some("shadow-md") => Some(elevation_shadow(visual, 4.0)),
        Some("shadow-lg") => Some(elevation_shadow(visual, 8.0)),
        Some("shadow-xl") => Some(elevation_shadow(visual, 16.0)),
        _ => None,
    }
}

fn button_style(
    visual: IcedVisualTheme,
    kind: ButtonKind,
    status: iced::widget::button::Status,
) -> iced::widget::button::Style {
    button_style_with_state(visual, kind, false, false, status)
}

fn button_status_with_state(
    state: &ControlState,
    status: iced::widget::button::Status,
) -> iced::widget::button::Status {
    if !state.enabled {
        iced::widget::button::Status::Disabled
    } else if state.pressed {
        iced::widget::button::Status::Pressed
    } else if state.hovered {
        iced::widget::button::Status::Hovered
    } else {
        status
    }
}

fn text_input_status_with_state(
    state: &ControlState,
    status: iced::widget::text_input::Status,
) -> iced::widget::text_input::Status {
    if !state.enabled {
        iced::widget::text_input::Status::Disabled
    } else if state.focused || state.pressed {
        iced::widget::text_input::Status::Focused {
            is_hovered: state.hovered || text_input_status_is_hovered(status),
        }
    } else if state.hovered {
        match status {
            iced::widget::text_input::Status::Focused { .. } => {
                iced::widget::text_input::Status::Focused { is_hovered: true }
            }
            _ => iced::widget::text_input::Status::Hovered,
        }
    } else {
        status
    }
}

fn text_input_status_is_hovered(status: iced::widget::text_input::Status) -> bool {
    matches!(
        status,
        iced::widget::text_input::Status::Hovered
            | iced::widget::text_input::Status::Focused { is_hovered: true }
    )
}

fn text_editor_status_with_state(
    state: &ControlState,
    status: iced::widget::text_editor::Status,
) -> iced::widget::text_editor::Status {
    if !state.enabled {
        iced::widget::text_editor::Status::Disabled
    } else if state.focused || state.pressed {
        iced::widget::text_editor::Status::Focused {
            is_hovered: state.hovered || text_editor_status_is_hovered(status),
        }
    } else if state.hovered {
        match status {
            iced::widget::text_editor::Status::Focused { .. } => {
                iced::widget::text_editor::Status::Focused { is_hovered: true }
            }
            _ => iced::widget::text_editor::Status::Hovered,
        }
    } else {
        status
    }
}

fn text_editor_status_is_hovered(status: iced::widget::text_editor::Status) -> bool {
    matches!(
        status,
        iced::widget::text_editor::Status::Hovered
            | iced::widget::text_editor::Status::Focused { is_hovered: true }
    )
}

fn slider_status_with_state(
    state: &ControlState,
    status: iced::widget::slider::Status,
) -> iced::widget::slider::Status {
    if state.pressed {
        iced::widget::slider::Status::Dragged
    } else if state.hovered {
        iced::widget::slider::Status::Hovered
    } else {
        status
    }
}

fn toggle_switch_status_with_state(
    state: &ControlState,
    checked: bool,
    status: iced::widget::toggler::Status,
) -> iced::widget::toggler::Status {
    if !state.enabled {
        iced::widget::toggler::Status::Disabled {
            is_toggled: checked,
        }
    } else if state.hovered || state.pressed {
        iced::widget::toggler::Status::Hovered {
            is_toggled: checked,
        }
    } else {
        status
    }
}

fn pick_list_status_with_state(
    state: &ControlState,
    status: iced::widget::pick_list::Status,
) -> iced::widget::pick_list::Status {
    if !state.enabled {
        status
    } else if state.hovered || state.pressed {
        iced::widget::pick_list::Status::Hovered
    } else {
        status
    }
}

fn button_style_with_state(
    visual: IcedVisualTheme,
    kind: ButtonKind,
    focused: bool,
    selected: bool,
    status: iced::widget::button::Status,
) -> iced::widget::button::Style {
    let (background, text_color, border_color) = match kind {
        ButtonKind::Primary => match status {
            iced::widget::button::Status::Hovered => (
                Some(visual.accent_hover),
                visual.text_on_accent,
                visual.accent_hover,
            ),
            iced::widget::button::Status::Pressed => (
                Some(visual.accent_pressed),
                visual.text_on_accent,
                visual.accent_pressed,
            ),
            iced::widget::button::Status::Disabled => (
                Some(visual.surface_alt),
                visual.text_secondary.scale_alpha(visual.disabled_opacity),
                visual.border,
            ),
            iced::widget::button::Status::Active => {
                (Some(visual.accent), visual.text_on_accent, visual.accent)
            }
        },
        ButtonKind::PrimaryRound => match status {
            iced::widget::button::Status::Pressed => (
                Some(visual.accent_pressed),
                visual.text_on_accent,
                visual.accent_pressed,
            ),
            iced::widget::button::Status::Disabled => (
                Some(visual.surface_alt),
                visual.text_secondary.scale_alpha(visual.disabled_opacity),
                visual.border,
            ),
            _ => (Some(visual.accent), visual.text_on_accent, visual.accent),
        },
        ButtonKind::Link => match status {
            iced::widget::button::Status::Hovered => {
                (Some(visual.button_hover), visual.accent, visual.border)
            }
            iced::widget::button::Status::Pressed => {
                (Some(visual.button_pressed), visual.accent, visual.border)
            }
            iced::widget::button::Status::Disabled => (
                None,
                visual.accent.scale_alpha(visual.disabled_opacity),
                visual.border,
            ),
            iced::widget::button::Status::Active => (None, visual.accent, visual.border),
        },
        ButtonKind::Subtle | ButtonKind::Icon | ButtonKind::ResultAction => match status {
            iced::widget::button::Status::Hovered => (
                Some(visual.button_hover),
                visual.text_primary,
                visual.border,
            ),
            iced::widget::button::Status::Pressed => (
                Some(visual.button_pressed),
                visual.text_primary,
                visual.border,
            ),
            iced::widget::button::Status::Disabled => (
                None,
                visual.text_secondary.scale_alpha(visual.disabled_opacity),
                visual.border,
            ),
            iced::widget::button::Status::Active => (None, visual.text_primary, visual.border),
        },
        ButtonKind::FloatingAction => {
            let opacity = match status {
                iced::widget::button::Status::Hovered => visual.floating_action_hover_opacity,
                iced::widget::button::Status::Pressed => visual.floating_action_pressed_opacity,
                iced::widget::button::Status::Disabled => visual.disabled_opacity,
                iced::widget::button::Status::Active => visual.floating_action_rest_opacity,
            };
            (
                Some(visual.floating_action_surface.scale_alpha(opacity)),
                visual.accent.scale_alpha(opacity),
                visual.floating_action_border.scale_alpha(opacity),
            )
        }
        ButtonKind::Tile if selected => match status {
            iced::widget::button::Status::Disabled => (
                Some(visual.surface_alt),
                visual.text_secondary.scale_alpha(visual.disabled_opacity),
                visual.border,
            ),
            _ => (
                Some(visual.selected_surface),
                visual.selected_foreground,
                visual.selected_border,
            ),
        },
        ButtonKind::Standard | ButtonKind::Chip | ButtonKind::Tile => match status {
            iced::widget::button::Status::Hovered if kind == ButtonKind::Tile => (
                Some(visual.tile_surface),
                visual.tile_foreground,
                visual.tile_border,
            ),
            iced::widget::button::Status::Pressed if kind == ButtonKind::Tile => (
                Some(visual.tile_surface),
                visual.tile_foreground,
                visual.tile_border,
            ),
            iced::widget::button::Status::Hovered => {
                (Some(visual.surface), visual.text_primary, visual.border)
            }
            iced::widget::button::Status::Pressed => (
                Some(visual.button_pressed),
                visual.text_primary,
                visual.border,
            ),
            iced::widget::button::Status::Disabled => (
                Some(visual.surface_alt),
                visual.text_secondary.scale_alpha(visual.disabled_opacity),
                visual.border,
            ),
            iced::widget::button::Status::Active if kind == ButtonKind::Tile => (
                Some(visual.tile_surface),
                visual.tile_foreground,
                visual.tile_border,
            ),
            iced::widget::button::Status::Active => {
                (Some(visual.surface), visual.text_primary, visual.border)
            }
        },
    };
    let focus_visible = focused && !matches!(status, iced::widget::button::Status::Disabled);
    let border_color = if focus_visible {
        visual.focus
    } else {
        border_color
    };
    let border_width = match (kind, status) {
        _ if focus_visible => visual.stroke_focus,
        (
            ButtonKind::Icon | ButtonKind::Subtle | ButtonKind::Link | ButtonKind::ResultAction,
            iced::widget::button::Status::Active,
        ) => 0.0,
        (ButtonKind::Tile, _) if selected && !focused => visual.stroke_control,
        (ButtonKind::Tile, _) if focused => visual.stroke_focus,
        _ => visual.stroke_control,
    };
    let border_radius = match kind {
        ButtonKind::Chip => 18.0,
        ButtonKind::PrimaryRound => visual.primary_icon_button_size() / 2.0,
        ButtonKind::FloatingAction => visual.floating_action_button_size / 2.0,
        _ => visual.radius_control,
    };

    iced::widget::button::Style {
        background: background.map(Background::Color),
        text_color,
        border: control_border_with_radius(border_color, border_width, border_radius),
        shadow: Shadow::default(),
        ..iced::widget::button::Style::default()
    }
}

fn caption_button_style(
    visual: IcedVisualTheme,
    kind: CaptionButtonKind,
    status: iced::widget::button::Status,
) -> iced::widget::button::Style {
    let background = match (kind, status) {
        (CaptionButtonKind::Close, iced::widget::button::Status::Hovered) => {
            Some(Color::from_rgb8(196, 43, 28))
        }
        (CaptionButtonKind::Close, iced::widget::button::Status::Pressed) => {
            Some(Color::from_rgb8(154, 30, 20))
        }
        (_, iced::widget::button::Status::Hovered) => Some(visual.border),
        (_, iced::widget::button::Status::Pressed) => Some(visual.surface),
        (_, iced::widget::button::Status::Disabled | iced::widget::button::Status::Active) => None,
    };
    let text_color = if kind == CaptionButtonKind::Close
        && matches!(
            status,
            iced::widget::button::Status::Hovered | iced::widget::button::Status::Pressed
        ) {
        Color::WHITE
    } else {
        visual.text_primary
    };

    iced::widget::button::Style {
        background: background.map(Background::Color),
        text_color,
        border: Border::default(),
        shadow: Shadow::default(),
        ..iced::widget::button::Style::default()
    }
}

fn result_header_button_style(
    visual: IcedVisualTheme,
    status: iced::widget::button::Status,
) -> iced::widget::button::Style {
    // WinUI result headers keep their resting fill on hover/press: the
    // ServiceResultHeaderHoverBackgroundColor delta is imperceptible in the
    // rendered reference, so the header bar stays at result_header in every
    // interaction state (see the result_header_button_style parity tests).
    let _ = status;

    iced::widget::button::Style {
        background: Some(Background::Color(visual.result_header)),
        text_color: visual.result_header_foreground,
        border: control_border_with_radius(Color::TRANSPARENT, 0.0, visual.radius_control),
        shadow: Shadow::default(),
        ..iced::widget::button::Style::default()
    }
}

fn text_input_style(
    visual: IcedVisualTheme,
    status: iced::widget::text_input::Status,
    chrome: TextEditorChrome,
) -> iced::widget::text_input::Style {
    let border = match (chrome, status) {
        (TextEditorChrome::Frameless, _) => control_border(visual, visual.border, 0.0),
        (_, iced::widget::text_input::Status::Focused { .. }) => {
            control_border(visual, visual.focus, visual.stroke_focus)
        }
        (_, iced::widget::text_input::Status::Hovered) => {
            control_border(visual, visual.border, visual.stroke_control)
        }
        (
            _,
            iced::widget::text_input::Status::Disabled | iced::widget::text_input::Status::Active,
        ) => control_border(visual, visual.border, visual.stroke_control),
    };

    let value = if status == iced::widget::text_input::Status::Disabled {
        visual.text_secondary
    } else {
        visual.text_primary
    };

    iced::widget::text_input::Style {
        background: Background::Color(if status == iced::widget::text_input::Status::Disabled {
            visual.surface_alt
        } else {
            visual.input_surface
        }),
        border,
        icon: visual.text_secondary,
        placeholder: visual.text_secondary,
        value,
        selection: visual.accent_light_alt,
    }
}

fn text_editor_style(
    visual: IcedVisualTheme,
    status: iced::widget::text_editor::Status,
    chrome: TextEditorChrome,
) -> iced::widget::text_editor::Style {
    // Standard-chrome text editors (e.g. the main window InputTextBox) use a
    // dedicated input-border color that is intentionally distinct from the
    // surrounding card border, mirroring the .NET WinUI app where the text
    // control border (#E1E7EF) differs from the card border (#DDE4EE). Without
    // this the 1px inner border has no contrast against the white card edge and
    // is effectively invisible.
    let border = match (chrome, status) {
        (TextEditorChrome::Frameless, _) => control_border(visual, visual.border, 0.0),
        (_, iced::widget::text_editor::Status::Focused { .. }) => {
            control_border(visual, visual.floating_input_border, visual.stroke_control)
        }
        (_, iced::widget::text_editor::Status::Hovered) => {
            control_border(visual, visual.floating_input_border, visual.stroke_control)
        }
        (
            _,
            iced::widget::text_editor::Status::Disabled | iced::widget::text_editor::Status::Active,
        ) => control_border(visual, visual.floating_input_border, visual.stroke_control),
    };

    let value = if status == iced::widget::text_editor::Status::Disabled {
        visual.text_secondary
    } else {
        visual.text_primary
    };

    iced::widget::text_editor::Style {
        background: Background::Color(if status == iced::widget::text_editor::Status::Disabled {
            visual.surface_alt
        } else {
            visual.input_surface
        }),
        border,
        placeholder: visual.text_secondary,
        value,
        selection: visual.accent_light_alt,
    }
}

fn toggle_switch_label(label: &str, checked: bool) -> String {
    if checked {
        return label.to_string();
    }

    match label {
        "On" => "Off",
        "Auto" => "Manual",
        "\u{5f00}" => "\u{5173}",
        "\u{81ea}\u{52a8}" => "\u{624b}\u{52a8}",
        _ => label,
    }
    .to_string()
}

fn compile_check_box<'a, Message>(
    token: &'a CheckBoxToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mut control = iced_checkbox(token.checked)
        .label(token.label.clone())
        .size(20)
        .spacing(8)
        .text_size(visual.body_size)
        .font(checkbox_label_font(&token.label, token.label_italic))
        .style({
            let state = token.state.clone();
            move |_, status| checkbox_style_with_state(visual, status, &state)
        });

    if token.state.enabled && token.action.kind() == ActionKind::BoolInput {
        let action = token.action.clone();
        control = control.on_toggle(move |value| {
            action
                .input_bool(value)
                .expect("checkbox action must produce a message")
        });
    }

    control.into()
}

fn checkbox_label_font(label: &str, label_italic: bool) -> Font {
    let mut font = text_font_for_value(TextStyle::Body, label);
    if label_italic && !contains_cjk(label) {
        font.family = font::Family::Name("Segoe UI");
    }

    Font {
        style: if label_italic {
            font::Style::Italic
        } else {
            font::Style::Normal
        },
        ..font
    }
}

fn contains_cjk(value: &str) -> bool {
    value.chars().any(|ch| {
        matches!(
            ch as u32,
            0x3400..=0x4DBF
                | 0x4E00..=0x9FFF
                | 0xF900..=0xFAFF
                | 0x20000..=0x2A6DF
                | 0x2A700..=0x2B73F
                | 0x2B740..=0x2B81F
                | 0x2B820..=0x2CEAF
                | 0x2CEB0..=0x2EBEF
                | 0x30000..=0x3134F
        )
    })
}

fn checkbox_style_with_state(
    visual: IcedVisualTheme,
    status: iced::widget::checkbox::Status,
    state: &ControlState,
) -> iced::widget::checkbox::Style {
    let is_checked = match status {
        iced::widget::checkbox::Status::Active { is_checked }
        | iced::widget::checkbox::Status::Hovered { is_checked }
        | iced::widget::checkbox::Status::Disabled { is_checked } => is_checked,
    };
    let enabled =
        state.enabled && !matches!(status, iced::widget::checkbox::Status::Disabled { .. });
    let hovered = state.hovered || matches!(status, iced::widget::checkbox::Status::Hovered { .. });

    let background = if is_checked {
        if !enabled {
            visual.text_secondary.scale_alpha(0.45)
        } else if hovered {
            visual.accent_hover
        } else {
            visual.accent
        }
    } else {
        visual.surface
    };

    let border_color = if is_checked {
        background
    } else if !enabled {
        visual.text_secondary.scale_alpha(0.45)
    } else if hovered {
        visual.text_primary
    } else {
        visual.text_secondary
    };

    iced::widget::checkbox::Style {
        background: Background::Color(background),
        icon_color: if is_checked {
            visual.text_on_accent
        } else {
            Color::TRANSPARENT
        },
        border: control_border_with_radius(border_color, 1.0, 9.0),
        text_color: Some(if enabled {
            visual.text_primary
        } else {
            visual.text_secondary.scale_alpha(0.45)
        }),
    }
}

fn slider_style(
    visual: IcedVisualTheme,
    status: iced::widget::slider::Status,
) -> iced::widget::slider::Style {
    let accent = match status {
        iced::widget::slider::Status::Active => visual.accent,
        iced::widget::slider::Status::Hovered => visual.accent_hover,
        iced::widget::slider::Status::Dragged => visual.accent_pressed,
    };

    iced::widget::slider::Style {
        rail: iced::widget::slider::Rail {
            backgrounds: (
                Background::Color(accent),
                Background::Color(visual.button_pressed),
            ),
            width: 4.0,
            border: Border {
                radius: 2.0.into(),
                width: 0.0,
                color: Color::TRANSPARENT,
            },
        },
        handle: iced::widget::slider::Handle {
            shape: iced::widget::slider::HandleShape::Circle { radius: 8.0 },
            background: Background::Color(visual.surface),
            border_width: visual.stroke_control,
            border_color: accent,
        },
    }
}

fn slider_style_with_state(
    visual: IcedVisualTheme,
    status: iced::widget::slider::Status,
    state: &ControlState,
) -> iced::widget::slider::Style {
    let mut style = slider_style(visual, slider_status_with_state(state, status));
    if state.enabled && state.focused {
        style.handle.border_color = visual.focus;
        style.handle.border_width = visual.stroke_focus;
    }

    style
}

fn slider_read_only_rail_style(
    visual: IcedVisualTheme,
    state: &ControlState,
    active: bool,
) -> iced::widget::container::Style {
    let color = if !state.enabled {
        if active {
            visual.text_secondary.scale_alpha(visual.disabled_opacity)
        } else {
            visual.surface_alt
        }
    } else if active {
        if state.pressed {
            visual.accent_pressed
        } else if state.hovered {
            visual.accent_hover
        } else {
            visual.accent
        }
    } else {
        visual.button_pressed
    };

    iced::widget::container::Style::default()
        .background(color)
        .border(Border {
            radius: 2.0.into(),
            width: 0.0,
            color: Color::TRANSPARENT,
        })
}

fn slider_read_only_thumb_style(
    visual: IcedVisualTheme,
    state: &ControlState,
) -> iced::widget::container::Style {
    let accent = if !state.enabled {
        visual.text_secondary.scale_alpha(visual.disabled_opacity)
    } else if state.pressed {
        visual.accent_pressed
    } else if state.hovered {
        visual.accent_hover
    } else if state.focused {
        visual.focus
    } else {
        visual.accent
    };

    iced::widget::container::Style::default()
        .background(visual.surface)
        .border(Border {
            radius: 8.0.into(),
            width: visual.stroke_control,
            color: accent,
        })
}

fn toggle_switch_style(
    visual: IcedVisualTheme,
    status: iced::widget::toggler::Status,
) -> iced::widget::toggler::Style {
    toggle_switch_style_for_state(visual, status, false)
}

fn toggle_switch_style_with_state(
    visual: IcedVisualTheme,
    status: iced::widget::toggler::Status,
    state: &ControlState,
) -> iced::widget::toggler::Style {
    let mut style = if state.pressed {
        toggle_switch_style_for_state(visual, status, true)
    } else {
        toggle_switch_style(visual, status)
    };

    if state.enabled && state.focused {
        style.background_border_color = visual.focus;
        style.background_border_width = visual.stroke_focus;
    }

    style
}

fn toggle_switch_style_for_state(
    visual: IcedVisualTheme,
    status: iced::widget::toggler::Status,
    is_pressed: bool,
) -> iced::widget::toggler::Style {
    let (is_toggled, is_hovered, is_disabled) = match status {
        iced::widget::toggler::Status::Active { is_toggled } => (is_toggled, false, false),
        iced::widget::toggler::Status::Hovered { is_toggled } => (is_toggled, true, false),
        iced::widget::toggler::Status::Disabled { is_toggled } => (is_toggled, false, true),
    };

    let (track, track_border, thumb) = if is_disabled {
        (
            visual.surface_alt,
            visual.border,
            visual.text_secondary.scale_alpha(visual.disabled_opacity),
        )
    } else if is_toggled {
        (
            if is_pressed {
                visual.accent_pressed
            } else {
                visual.accent
            },
            if is_pressed {
                visual.accent_pressed
            } else {
                visual.accent
            },
            if is_pressed {
                visual.text_on_accent.scale_alpha(0.72)
            } else {
                visual.text_on_accent
            },
        )
    } else {
        let resting_light = matches!(visual.mode, ThemeMode::Light | ThemeMode::Minimal)
            && !is_pressed
            && !is_hovered;
        if resting_light {
            (
                Color::from_rgb8(249, 249, 249),
                Color::from_rgb8(139, 139, 139),
                Color::from_rgb8(95, 95, 95),
            )
        } else {
            (
                if is_pressed {
                    visual.button_pressed
                } else {
                    visual.surface
                },
                visual.border,
                visual.text_secondary,
            )
        }
    };

    iced::widget::toggler::Style {
        background: Background::Color(track),
        background_border_width: visual.stroke_control,
        background_border_color: track_border,
        foreground: Background::Color(thumb),
        foreground_border_width: 0.0,
        foreground_border_color: Color::TRANSPARENT,
        text_color: Some(if is_disabled {
            visual.text_secondary.scale_alpha(visual.disabled_opacity)
        } else {
            visual.text_primary
        }),
        border_radius: None,
        padding_ratio: 0.20,
    }
}

fn pick_list_style(
    visual: IcedVisualTheme,
    status: iced::widget::pick_list::Status,
) -> iced::widget::pick_list::Style {
    let border = match status {
        iced::widget::pick_list::Status::Active => {
            control_border(visual, visual.border, visual.stroke_control)
        }
        iced::widget::pick_list::Status::Hovered => {
            control_border(visual, visual.border, visual.stroke_control)
        }
        iced::widget::pick_list::Status::Opened { .. } => {
            control_border(visual, visual.focus, visual.stroke_focus)
        }
    };

    iced::widget::pick_list::Style {
        text_color: visual.text_primary,
        placeholder_color: visual.text_secondary,
        handle_color: visual.text_secondary,
        background: Background::Color(visual.surface),
        border,
    }
}

fn pick_list_style_with_state(
    visual: IcedVisualTheme,
    status: iced::widget::pick_list::Status,
    state: &ControlState,
) -> iced::widget::pick_list::Style {
    let mut style = pick_list_style(visual, pick_list_status_with_state(state, status));
    if !state.enabled {
        style.background = Background::Color(visual.surface_alt);
        style.text_color = visual.text_secondary.scale_alpha(visual.disabled_opacity);
        style.placeholder_color = visual.text_secondary.scale_alpha(visual.disabled_opacity);
        style.handle_color = visual.text_secondary.scale_alpha(visual.disabled_opacity);
        style.border = control_border(visual, visual.border, visual.stroke_control);
    } else if state.pressed {
        style.background = Background::Color(visual.button_pressed);
    } else if state.hovered {
        style.background = Background::Color(visual.button_hover);
    }
    if state.enabled && state.focused {
        style.border = control_border(visual, visual.focus, visual.stroke_focus);
    }
    style
}

fn read_only_combo_box_style(
    visual: IcedVisualTheme,
    state: &ControlState,
) -> iced::widget::container::Style {
    let mut pick_list_style =
        pick_list_style_with_state(visual, iced::widget::pick_list::Status::Active, state);

    if !state.enabled {
        pick_list_style.background = Background::Color(visual.surface_alt);
        pick_list_style.border = control_border(visual, visual.border, visual.stroke_control);
    }

    iced::widget::container::Style::default()
        .background(pick_list_style.background)
        .color(pick_list_style.text_color)
        .border(pick_list_style.border)
}

fn progress_bar_style(visual: IcedVisualTheme, active: bool) -> iced::widget::progress_bar::Style {
    iced::widget::progress_bar::Style {
        background: Background::Color(visual.surface_alt),
        bar: Background::Color(if active { visual.accent } else { visual.border }),
        border: control_border_with_radius(Color::TRANSPARENT, 0.0, 2.0),
    }
}

fn flyout_pick_list_style(
    visual: IcedVisualTheme,
    status: iced::widget::pick_list::Status,
    border_width: f32,
    radius: f32,
) -> iced::widget::pick_list::Style {
    let background = match status {
        iced::widget::pick_list::Status::Active => visual.surface.scale_alpha(0.0),
        iced::widget::pick_list::Status::Hovered
        | iced::widget::pick_list::Status::Opened { .. } => visual.button_hover,
    };

    iced::widget::pick_list::Style {
        text_color: visual.text_primary,
        placeholder_color: visual.text_primary,
        handle_color: visual.text_secondary,
        background: Background::Color(background),
        border: control_border_with_radius(visual.border.scale_alpha(0.0), border_width, radius),
    }
}

fn menu_style(visual: IcedVisualTheme) -> iced::overlay::menu::Style {
    iced::overlay::menu::Style {
        background: Background::Color(visual.surface),
        border: control_border(visual, visual.border, visual.stroke_control),
        text_color: visual.text_primary,
        selected_text_color: visual.text_on_accent,
        selected_background: Background::Color(visual.accent),
        shadow: elevation_shadow(visual, visual.elevation_raised),
    }
}

fn settings_row_container_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    settings_row_container_style_with_state(visual, &ControlState::default())
}

fn settings_row_container_style_with_state(
    visual: IcedVisualTheme,
    state: &ControlState,
) -> iced::widget::container::Style {
    let background = if !state.enabled {
        visual.surface
    } else if state.pressed {
        visual.button_pressed
    } else if state.hovered {
        visual.button_hover
    } else {
        visual.surface
    };
    let border_color = if state.focused {
        visual.accent
    } else {
        visual.border
    };

    iced::widget::container::Style::default()
        .background(background)
        .color(visual.text_primary)
        .border(control_border(visual, border_color, visual.stroke_control))
}

fn expander_container_style_with_state(
    visual: IcedVisualTheme,
    state: &ControlState,
    style: &FluentStyle,
) -> iced::widget::container::Style {
    let border_color = if state.focused {
        visual.accent
    } else {
        expander_border_color(visual)
    };

    iced::widget::container::Style::default()
        .background(expander_background_color_with_state(visual, state, style))
        .color(visual.text_primary)
        .border(control_border(visual, border_color, visual.stroke_control))
}

fn expander_background_color_with_state(
    visual: IcedVisualTheme,
    state: &ControlState,
    style: &FluentStyle,
) -> Color {
    let _ = state;
    expander_header_background_color(visual, style)
}

fn expander_background_color(visual: IcedVisualTheme) -> Color {
    if matches!(visual.mode, ThemeMode::HighContrast) || !is_light_surface(visual.surface) {
        visual.surface
    } else {
        Color::from_rgb8(253, 253, 254)
    }
}

fn expander_header_background_color(visual: IcedVisualTheme, style: &FluentStyle) -> Color {
    if matches!(visual.mode, ThemeMode::HighContrast) || !is_light_surface(visual.surface) {
        return expander_background_color(visual);
    }

    if style.has("header-surface-f8f9fc") {
        Color::from_rgb8(248, 249, 252)
    } else if style.has("header-surface-f9fafc") {
        Color::from_rgb8(249, 250, 252)
    } else if style.has("header-surface-fafbfd") {
        Color::from_rgb8(250, 251, 253)
    } else if style.has("header-surface-fbfcfd") {
        Color::from_rgb8(251, 252, 253)
    } else if style.has("header-surface-fcfcfd") {
        Color::from_rgb8(252, 252, 253)
    } else {
        expander_background_color(visual)
    }
}

fn expander_border_color(visual: IcedVisualTheme) -> Color {
    visual.border
}

fn is_light_surface(color: Color) -> bool {
    ((color.r * 0.299) + (color.g * 0.587) + (color.b * 0.114)) >= 0.72
}

fn expander_content_container_style(
    visual: IcedVisualTheme,
    style: &FluentStyle,
) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(expander_content_background_color(visual, style))
        .color(visual.text_primary)
}

fn expander_content_divider_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(expander_border_color(visual))
        .color(visual.text_primary)
}

fn expander_content_background_color(visual: IcedVisualTheme, style: &FluentStyle) -> Color {
    if style.has("info-bar") {
        return info_bar_background_color(visual);
    }

    if !matches!(visual.mode, ThemeMode::HighContrast) && is_light_surface(visual.surface) {
        if style.has("content-surface-f5f5f4") {
            return Color::from_rgb8(245, 245, 244);
        }
        if style.has("content-surface-f7f8fa") {
            return Color::from_rgb8(247, 248, 250);
        }
        if style.has("content-surface-f8f8f7") {
            return Color::from_rgb8(248, 248, 247);
        }
        if style.has("content-surface-f8f8fa") {
            return Color::from_rgb8(248, 248, 250);
        }
        if style.has("content-surface-f8f9fb") {
            return Color::from_rgb8(248, 249, 251);
        }
    }

    if matches!(visual.mode, ThemeMode::HighContrast) || !is_light_surface(visual.surface) {
        visual.background
    } else {
        Color::from_rgb8(246, 247, 249)
    }
}

fn card_container_style(visual: IcedVisualTheme, kind: CardKind) -> iced::widget::container::Style {
    let background = match kind {
        CardKind::Surface | CardKind::Expander => visual.surface,
        CardKind::Elevated => visual.surface_alt,
        CardKind::FloatingInput => visual.floating_input_surface,
    };
    let border_color = match kind {
        CardKind::FloatingInput => visual.floating_input_border,
        _ => visual.border,
    };
    let border_radius = match kind {
        CardKind::FloatingInput => {
            if matches!(visual.mode, ThemeMode::Minimal | ThemeMode::HighContrast) {
                visual.radius_control
            } else {
                18.0
            }
        }
        _ => visual.radius_control,
    };

    let mut style = iced::widget::container::Style::default()
        .background(background)
        .color(visual.text_primary)
        .border(control_border_with_radius(
            border_color,
            visual.stroke_control,
            border_radius,
        ));

    if kind == CardKind::Elevated {
        style = style.shadow(elevation_shadow(visual, visual.elevation_raised));
    }

    style
}

fn dialog_container_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.surface)
        .color(visual.text_primary)
        .border(control_border(visual, visual.border, visual.stroke_control))
        .shadow(elevation_shadow(visual, 16.0))
}

fn result_card_container_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.result_surface)
        .color(visual.text_primary)
        .border(control_border(
            visual,
            // WinUI card stroke color (light-blue #DDE4EE in Light, #3A4250 in
            // Dark), not the grayer generic control border.
            match visual.mode {
                ThemeMode::Light => Color::from_rgb8(0xDD, 0xE4, 0xEE),
                ThemeMode::Dark => Color::from_rgb8(0x3A, 0x42, 0x50),
                _ => visual.border,
            },
            visual.stroke_control,
        ))
        // The WinUI reference uses flat outlined result rows in the main list.
        // Keep them visually quiet so hover/pressed effects are not polluted by elevation.
        .shadow(Shadow::default())
}

fn result_list_container_style(
    visual: IcedVisualTheme,
    border_width: f32,
) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .color(visual.text_primary)
        .border(control_border_with_radius(
            Color::TRANSPARENT,
            border_width,
            0.0,
        ))
}

fn control_border(visual: IcedVisualTheme, color: Color, width: f32) -> Border {
    control_border_with_radius(color, width, visual.radius_control)
}

fn control_border_with_radius(color: Color, width: f32, radius: f32) -> Border {
    Border::default().color(color).width(width).rounded(radius)
}

fn elevation_shadow(visual: IcedVisualTheme, elevation: f32) -> Shadow {
    if visual.mode == ThemeMode::HighContrast || elevation == 0.0 {
        return Shadow::default();
    }

    Shadow {
        color: Color::BLACK.scale_alpha(0.18),
        offset: Vector::new(0.0, elevation / 2.0),
        blur_radius: elevation,
    }
}

fn text_color(style: TextStyle, visual: IcedVisualTheme) -> Color {
    match style {
        TextStyle::Caption | TextStyle::CaptionSmall => visual.text_secondary,
        TextStyle::Body
        | TextStyle::BodyLarge
        | TextStyle::BodyStrong
        | TextStyle::SectionTitle
        | TextStyle::Subtitle
        | TextStyle::Title
        | TextStyle::TitleLarge => visual.text_primary,
        TextStyle::Success => visual.success,
        TextStyle::Warning => visual.warning,
    }
}

fn iced_hotkey_subscription(hotkey: Hotkey) -> Subscription<IcedHotkeyEvent> {
    platform_hotkey_subscription(hotkey)
}

fn iced_named_event_subscription(name: String, auto_reset: bool) -> Subscription<IcedNamedEvent> {
    platform_named_event_subscription(name, auto_reset)
}

fn iced_tray_subscription(
    plan: win_fluent_platform_win::WindowsTrayPlan,
) -> Subscription<IcedTrayEvent> {
    platform_tray_subscription(plan)
}

#[cfg(windows)]
fn platform_hotkey_subscription(hotkey: Hotkey) -> Subscription<IcedHotkeyEvent> {
    Subscription::run_with(HotkeySubscriptionData::from(hotkey), hotkey_stream)
}

#[cfg(not(windows))]
fn platform_hotkey_subscription(_hotkey: Hotkey) -> Subscription<IcedHotkeyEvent> {
    Subscription::none()
}

#[cfg(windows)]
fn platform_named_event_subscription(
    name: String,
    auto_reset: bool,
) -> Subscription<IcedNamedEvent> {
    Subscription::run_with(
        NamedEventSubscriptionData { name, auto_reset },
        named_event_stream,
    )
}

#[cfg(not(windows))]
fn platform_named_event_subscription(
    _name: String,
    _auto_reset: bool,
) -> Subscription<IcedNamedEvent> {
    Subscription::none()
}

#[cfg(windows)]
fn platform_tray_subscription(
    plan: win_fluent_platform_win::WindowsTrayPlan,
) -> Subscription<IcedTrayEvent> {
    Subscription::run_with(TraySubscriptionData::from(plan), tray_stream)
}

#[cfg(not(windows))]
fn platform_tray_subscription(
    _plan: win_fluent_platform_win::WindowsTrayPlan,
) -> Subscription<IcedTrayEvent> {
    Subscription::none()
}

#[cfg(windows)]
fn hotkey_stream(
    data: &HotkeySubscriptionData,
) -> impl iced::futures::Stream<Item = IcedHotkeyEvent> {
    let hotkey = data.to_hotkey();

    iced::stream::channel(
        16,
        move |mut output: iced::futures::channel::mpsc::Sender<IcedHotkeyEvent>| async move {
            use std::sync::{
                atomic::{AtomicBool, Ordering},
                Arc,
            };

            let running = Arc::new(AtomicBool::new(true));
            let thread_running = Arc::clone(&running);
            let _guard = HotkeyBridgeGuard {
                running: Arc::clone(&running),
            };

            std::thread::spawn(move || {
                let handle =
                    match win_fluent_platform_win::WindowsPlatformAdapter::register_global_hotkey(
                        &hotkey,
                    ) {
                        Ok(handle) => handle,
                        Err(error) => {
                            let _ = output.try_send(IcedHotkeyEvent::Error {
                                message: format!("{error:?}"),
                            });
                            return;
                        }
                    };

                while thread_running.load(Ordering::Relaxed) {
                    match win_fluent_platform_win::WindowsPlatformAdapter::wait_for_hotkey_event(
                        &[&handle],
                        std::time::Duration::from_millis(100),
                    ) {
                        Ok(Some(event)) => {
                            let _ = output.try_send(IcedHotkeyEvent::Pressed { id: event.id });
                        }
                        Ok(None) => {}
                        Err(error) => {
                            let _ = output.try_send(IcedHotkeyEvent::Error {
                                message: format!("{error:?}"),
                            });
                            return;
                        }
                    }
                }
            });

            std::future::pending::<()>().await;
        },
    )
}

#[cfg(windows)]
fn named_event_stream(
    data: &NamedEventSubscriptionData,
) -> impl iced::futures::Stream<Item = IcedNamedEvent> {
    let data = data.clone();

    iced::stream::channel(
        16,
        move |mut output: iced::futures::channel::mpsc::Sender<IcedNamedEvent>| async move {
            use std::sync::{
                atomic::{AtomicBool, Ordering},
                Arc,
            };

            let running = Arc::new(AtomicBool::new(true));
            let thread_running = Arc::clone(&running);
            let _guard = NamedEventBridgeGuard {
                running: Arc::clone(&running),
            };

            std::thread::spawn(move || {
                let handle =
                    match win_fluent_platform_win::WindowsPlatformAdapter::create_named_event(
                        &data.name,
                        data.auto_reset,
                    ) {
                        Ok(handle) => handle,
                        Err(error) => {
                            let _ = output.try_send(IcedNamedEvent::Error {
                                name: data.name,
                                message: format!("{error:?}"),
                            });
                            return;
                        }
                    };

                while thread_running.load(Ordering::Relaxed) {
                    match win_fluent_platform_win::WindowsPlatformAdapter::wait_for_named_event(
                        &handle,
                        std::time::Duration::from_millis(100),
                    ) {
                        Ok(Some(event)) => {
                            let _ = output.try_send(IcedNamedEvent::Signaled { name: event.name });
                        }
                        Ok(None) => {}
                        Err(error) => {
                            let _ = output.try_send(IcedNamedEvent::Error {
                                name: handle.name().to_string(),
                                message: format!("{error:?}"),
                            });
                            return;
                        }
                    }
                }
            });

            std::future::pending::<()>().await;
        },
    )
}

#[cfg(windows)]
fn tray_stream(data: &TraySubscriptionData) -> impl iced::futures::Stream<Item = IcedTrayEvent> {
    let plan = data.to_plan();

    iced::stream::channel(
        16,
        move |mut output: iced::futures::channel::mpsc::Sender<IcedTrayEvent>| async move {
            use std::sync::{
                atomic::{AtomicBool, Ordering},
                Arc,
            };

            let running = Arc::new(AtomicBool::new(true));
            let thread_running = Arc::clone(&running);
            let _guard = TrayBridgeGuard {
                running: Arc::clone(&running),
            };

            std::thread::spawn(move || {
                let handle = match win_fluent_platform_win::WindowsPlatformAdapter::create_tray_icon(
                    &plan,
                ) {
                    Ok(handle) => handle,
                    Err(error) => {
                        let _ = output.try_send(IcedTrayEvent::Error {
                            message: format!("{error:?}"),
                        });
                        return;
                    }
                };

                while thread_running.load(Ordering::Relaxed) {
                    match win_fluent_platform_win::WindowsPlatformAdapter::wait_for_tray_event(
                        &handle,
                        std::time::Duration::from_millis(100),
                    ) {
                        Ok(Some(event)) => {
                            let _ = output.try_send(IcedTrayEvent::Command { id: event.id });
                        }
                        Ok(None) => {}
                        Err(error) => {
                            let _ = output.try_send(IcedTrayEvent::Error {
                                message: format!("{error:?}"),
                            });
                            return;
                        }
                    }
                }
            });

            std::future::pending::<()>().await;
        },
    )
}

#[cfg(windows)]
struct HotkeyBridgeGuard {
    running: std::sync::Arc<std::sync::atomic::AtomicBool>,
}

#[cfg(windows)]
impl Drop for HotkeyBridgeGuard {
    fn drop(&mut self) {
        self.running
            .store(false, std::sync::atomic::Ordering::Relaxed);
    }
}

#[cfg(windows)]
struct NamedEventBridgeGuard {
    running: std::sync::Arc<std::sync::atomic::AtomicBool>,
}

#[cfg(windows)]
impl Drop for NamedEventBridgeGuard {
    fn drop(&mut self) {
        self.running
            .store(false, std::sync::atomic::Ordering::Relaxed);
    }
}

#[cfg(windows)]
struct TrayBridgeGuard {
    running: std::sync::Arc<std::sync::atomic::AtomicBool>,
}

#[cfg(windows)]
impl Drop for TrayBridgeGuard {
    fn drop(&mut self) {
        self.running
            .store(false, std::sync::atomic::Ordering::Relaxed);
    }
}

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct HotkeySubscriptionData {
    id: String,
    key: HotkeyKeyData,
    modifiers: Vec<HotkeyModifierData>,
}

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct NamedEventSubscriptionData {
    name: String,
    auto_reset: bool,
}

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct TraySubscriptionData {
    tooltip: String,
    icon_path: Option<String>,
    presenter_min_width: Option<u16>,
    callback_message: u32,
    item_count: usize,
    default_command_id: Option<u32>,
    items: Vec<TrayItemSubscriptionData>,
}

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct TrayItemSubscriptionData {
    id: String,
    label: String,
    tooltip: Option<String>,
    enabled: bool,
    command_id: u32,
    action_kind: ActionKind,
    kind: win_fluent_platform_win::WindowsTrayItemKind,
    children: Vec<TrayItemSubscriptionData>,
}

impl HotkeySubscriptionData {
    fn to_hotkey(&self) -> Hotkey {
        let mut hotkey = Hotkey::new(self.id.clone(), self.key.to_hotkey_key());
        for modifier in &self.modifiers {
            hotkey = hotkey.modifier(modifier.to_hotkey_modifier());
        }

        hotkey
    }
}

impl TraySubscriptionData {
    fn to_plan(&self) -> win_fluent_platform_win::WindowsTrayPlan {
        let items = self
            .items
            .iter()
            .map(TrayItemSubscriptionData::to_plan_item)
            .collect::<Vec<_>>();

        win_fluent_platform_win::WindowsTrayPlan {
            tooltip: self.tooltip.clone(),
            icon_path: self.icon_path.clone(),
            presenter_min_width: self.presenter_min_width,
            callback_message: self.callback_message,
            item_count: self.item_count,
            default_command_id: self.default_command_id,
            items,
        }
    }
}

impl TrayItemSubscriptionData {
    fn to_plan_item(&self) -> win_fluent_platform_win::WindowsTrayItemPlan {
        win_fluent_platform_win::WindowsTrayItemPlan {
            id: self.id.clone(),
            label: self.label.clone(),
            tooltip: self.tooltip.clone(),
            enabled: self.enabled,
            command_id: self.command_id,
            action_kind: self.action_kind,
            kind: self.kind,
            children: self
                .children
                .iter()
                .map(TrayItemSubscriptionData::to_plan_item)
                .collect(),
        }
    }
}

impl From<Hotkey> for HotkeySubscriptionData {
    fn from(hotkey: Hotkey) -> Self {
        Self {
            id: hotkey.id,
            key: HotkeyKeyData::from(hotkey.key),
            modifiers: hotkey
                .modifiers
                .into_iter()
                .map(HotkeyModifierData::from)
                .collect(),
        }
    }
}

impl From<win_fluent_platform_win::WindowsTrayPlan> for TraySubscriptionData {
    fn from(plan: win_fluent_platform_win::WindowsTrayPlan) -> Self {
        Self {
            tooltip: plan.tooltip,
            icon_path: plan.icon_path,
            presenter_min_width: plan.presenter_min_width,
            callback_message: plan.callback_message,
            item_count: plan.item_count,
            default_command_id: plan.default_command_id,
            items: plan
                .items
                .into_iter()
                .map(|item| TrayItemSubscriptionData {
                    id: item.id,
                    label: item.label,
                    tooltip: item.tooltip,
                    enabled: item.enabled,
                    command_id: item.command_id,
                    action_kind: item.action_kind,
                    kind: item.kind,
                    children: item
                        .children
                        .into_iter()
                        .map(TrayItemSubscriptionData::from)
                        .collect(),
                })
                .collect(),
        }
    }
}

impl From<win_fluent_platform_win::WindowsTrayItemPlan> for TrayItemSubscriptionData {
    fn from(item: win_fluent_platform_win::WindowsTrayItemPlan) -> Self {
        Self {
            id: item.id,
            label: item.label,
            tooltip: item.tooltip,
            enabled: item.enabled,
            command_id: item.command_id,
            action_kind: item.action_kind,
            kind: item.kind,
            children: item
                .children
                .into_iter()
                .map(TrayItemSubscriptionData::from)
                .collect(),
        }
    }
}

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
enum HotkeyKeyData {
    Character(char),
    Function(u8),
    Named(String),
}

impl HotkeyKeyData {
    fn to_hotkey_key(&self) -> HotkeyKey {
        match self {
            Self::Character(value) => HotkeyKey::Character(*value),
            Self::Function(value) => HotkeyKey::Function(*value),
            Self::Named(value) => HotkeyKey::Named(value.clone()),
        }
    }
}

impl From<HotkeyKey> for HotkeyKeyData {
    fn from(key: HotkeyKey) -> Self {
        match key {
            HotkeyKey::Character(value) => Self::Character(value),
            HotkeyKey::Function(value) => Self::Function(value),
            HotkeyKey::Named(value) => Self::Named(value),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
enum HotkeyModifierData {
    Control,
    Alt,
    Shift,
    Logo,
}

impl HotkeyModifierData {
    fn to_hotkey_modifier(self) -> HotkeyModifier {
        match self {
            Self::Control => HotkeyModifier::Control,
            Self::Alt => HotkeyModifier::Alt,
            Self::Shift => HotkeyModifier::Shift,
            Self::Logo => HotkeyModifier::Logo,
        }
    }
}

impl From<HotkeyModifier> for HotkeyModifierData {
    fn from(modifier: HotkeyModifier) -> Self {
        match modifier {
            HotkeyModifier::Control => Self::Control,
            HotkeyModifier::Alt => Self::Alt,
            HotkeyModifier::Shift => Self::Shift,
            HotkeyModifier::Logo => Self::Logo,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ComboChoice {
    id: String,
    label: String,
}

impl fmt::Display for ComboChoice {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.label)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use win_fluent::prelude::*;

    #[allow(dead_code)]
    #[derive(Clone, Debug)]
    enum Msg {
        Input(String),
        Toggle(bool),
        Pick(String),
        Pointer(PointerPosition),
        Wheel(PointerWheel),
        Run,
    }

    struct TestApp;

    impl FluentApplication for TestApp {
        type Message = Msg;
        type Flags = ();

        fn new(_flags: Self::Flags) -> (Self, FluentTask<Self::Message>) {
            (Self, FluentTask::none())
        }

        fn title(&self, _window: &WindowId) -> String {
            "Original title".to_string()
        }

        fn view(&self, _window: &WindowId) -> View<Self::Message> {
            page("Test").content(text("Ready")).into_view()
        }

        fn update(&mut self, _message: Self::Message) -> FluentTask<Self::Message> {
            FluentTask::none()
        }

        fn subscription(&self) -> FluentSubscription<Self::Message> {
            FluentSubscription::window("main", |_| Msg::Run)
        }

        fn window_options(&self, window: &WindowId) -> Option<WindowOptions> {
            match window.as_str() {
                "mini" => Some(
                    WindowOptions::new("mini", "Mini")
                        .size(320.0, 200.0)
                        .skip_taskbar(true),
                ),
                _ => None,
            }
        }
    }

    #[derive(Debug)]
    struct WindowEventRecorderApp {
        events: Vec<WindowEvent>,
    }

    impl FluentApplication for WindowEventRecorderApp {
        type Message = WindowEvent;
        type Flags = ();

        fn new(_flags: Self::Flags) -> (Self, FluentTask<Self::Message>) {
            (Self { events: Vec::new() }, FluentTask::none())
        }

        fn title(&self, _window: &WindowId) -> String {
            "Window event recorder".to_string()
        }

        fn view(&self, _window: &WindowId) -> View<Self::Message> {
            page("Recorder").content(text("Ready")).into_view()
        }

        fn update(&mut self, message: Self::Message) -> FluentTask<Self::Message> {
            self.events.push(message);
            FluentTask::none()
        }

        fn subscription(&self) -> FluentSubscription<Self::Message> {
            FluentSubscription::window("mini", |event| event)
        }
    }

    fn empty_desktop_integration_plan<Message>() -> DesktopIntegrationPlan<Message> {
        DesktopIntegrationPlan {
            tray_menu: None,
            named_events: Vec::new(),
            shell_verbs: Vec::new(),
            protocol_registrations: Vec::new(),
        }
    }

    #[test]
    fn winfluent_open_url_rejects_non_web_and_retained_targets_before_shellexecute() {
        for target in [
            "file:///C:/Easydict/dotnet.exe",
            "powershell.exe",
            "easydict://ocr-translate",
            "C:\\Easydict\\workers\\localai\\Easydict.Workers.LocalAi.exe",
        ] {
            let error = run_platform_open_url(target.to_string()).unwrap_err();
            assert!(
                error.contains("invalid URL target"),
                "{target} should be rejected by the guarded shell URL boundary, got {error}"
            );
        }

        assert!(
            run_platform_open_url("   ".to_string()).is_ok(),
            "blank URL should preserve the existing no-op behavior"
        );
    }

    #[test]
    fn winfluent_bundled_executable_rejects_retained_runtime_or_script_targets_before_spawn() {
        for executable_name in [
            "dotnet.exe",
            "powershell.exe",
            "pwsh.exe",
            "Easydict.CompatHost.exe",
            "workers\\localai\\Easydict.Workers.LocalAi.exe",
        ] {
            let error = run_platform_bundled_executable(executable_name.to_string(), Vec::new())
                .unwrap_err();
            assert!(
                error.contains("invalid bundled executable name"),
                "{executable_name} should be rejected before filesystem lookup or spawn, got {error}"
            );
        }
    }

    #[test]
    fn winfluent_bundled_executable_rejects_retained_runtime_arguments_before_spawn() {
        let error = run_platform_bundled_executable(
            "easydict_browser_registrar.exe".to_string(),
            vec!["--runtime=dotnet.exe".to_string()],
        )
        .unwrap_err();

        assert!(
            error.contains("invalid bundled executable argument"),
            "guarded bundled executable boundary should reject retained arguments before spawn, got {error}"
        );
    }

    #[test]
    fn winfluent_bundled_executable_rejects_retained_runtime_content_before_spawn() {
        let current_exe = std::env::current_exe().expect("current test executable");
        let exe_dir = current_exe
            .parent()
            .expect("current test executable should have parent");
        let executable_name = format!(
            "winfluent-retained-marker-{}-{}.exe",
            std::process::id(),
            trace_wall_ms()
        );
        let executable_path = exe_dir.join(&executable_name);
        std::fs::write(
            &executable_path,
            b"fake rust helper bytes with stale hostfxr.dll marker",
        )
        .expect("write retained marker executable fixture");

        let error = run_platform_bundled_executable(executable_name, Vec::new()).unwrap_err();
        let _ = std::fs::remove_file(&executable_path);

        assert!(
            error.contains("contains retained runtime marker"),
            "guarded bundled executable boundary should reject marker bytes before spawn, got {error}"
        );
    }

    #[test]
    fn winfluent_register_shell_verb_and_protocol_guard_current_exe_before_registry_write() {
        let retained_path = Path::new(r"C:\Payload\workers\localai\Easydict.Workers.LocalAi.exe");
        let retained_error = validate_platform_registry_executable_path(retained_path).expect_err(
            "registry command target path markers should be rejected before registry IO",
        );
        assert!(
            retained_error.contains("retained runtime"),
            "registry command target should reject retained path markers, got {retained_error}"
        );

        let dir = std::env::temp_dir().join(format!(
            "winfluent-registry-target-{}-{}",
            std::process::id(),
            trace_wall_ms()
        ));
        std::fs::create_dir_all(&dir).expect("create temp dir");
        let executable_path = dir.join("Easydict.Rust.exe");
        std::fs::write(
            &executable_path,
            b"fake rust exe with stale System.Private.CoreLib.dll marker",
        )
        .expect("write fake executable");

        let content_error = validate_platform_registry_executable_path(&executable_path)
            .expect_err("registry command target bytes should be scanned before registry IO");
        let _ = std::fs::remove_dir_all(&dir);

        assert!(
            content_error.contains("contains retained runtime marker"),
            "registry command target should reject retained marker bytes, got {content_error}"
        );
    }

    #[test]
    fn maps_close_request_to_logical_window_event() {
        let id = WindowId::new("main");

        assert_eq!(
            close_requested_platform_event(&id),
            PlatformEvent::Window(WindowEvent::CloseRequested(WindowId::new("main")))
        );
    }

    #[test]
    fn compiles_basic_view_to_iced_element() {
        let view = page("Demo")
            .content(column((
                text("Ready"),
                text_editor("value")
                    .placeholder("Type")
                    .on_input(Msg::Input),
                toggle_switch("Enabled", true).on_toggle(Msg::Toggle),
                combo_box([
                    ComboBoxItem::new("en", "English"),
                    ComboBoxItem::new("zh", "Chinese"),
                ])
                .selected("en")
                .on_change(Msg::Pick),
                primary_button("Run").on_press(Msg::Run),
            )))
            .into_view();

        let _element: IcedElement<'_, Msg> = IcedAdapter::compile_view(&view);
    }

    #[test]
    fn set_title_window_command_overrides_logical_window_titles() {
        let options = WindowOptions::new("main", "Boot title");
        let mut runtime = IcedSingleWindowRuntime::<TestApp>::new(
            TestApp,
            options,
            empty_desktop_integration_plan(),
        );

        assert_eq!(
            runtime.title_for_logical_window(&WindowId::new("main")),
            "Original title".to_string()
        );

        let _ = runtime.window_command(WindowCommand::SetTitle {
            id: WindowId::new("main"),
            title: "Updated title".to_string(),
        });

        assert_eq!(
            runtime.title_for_logical_window(&WindowId::new("main")),
            "Updated title".to_string()
        );

        let _ = runtime.window_command(WindowCommand::SetTitle {
            id: WindowId::new("settings"),
            title: "Settings title".to_string(),
        });

        assert_eq!(
            runtime.title_for_logical_window(&WindowId::new("main")),
            "Updated title".to_string()
        );
        assert_eq!(
            runtime.title_for_logical_window(&WindowId::new("settings")),
            "Settings title".to_string()
        );
    }

    #[test]
    fn show_logical_window_opens_pending_window_with_app_options() {
        let options = WindowOptions::new("main", "Boot title");
        let mut runtime = IcedSingleWindowRuntime::<TestApp>::new(
            TestApp,
            options,
            empty_desktop_integration_plan(),
        );

        let _ = runtime.window_command(WindowCommand::Show(WindowId::new("mini")));

        let pending = runtime
            .pending_windows
            .values()
            .find(|window| window.logical_id.as_str() == "mini")
            .expect("mini window should be pending");
        assert_eq!(pending.options.width, 320.0);
        assert_eq!(pending.options.height, 200.0);
        assert!(pending.options.skip_taskbar);
        assert!(runtime.views.contains_key(&WindowId::new("mini")));
    }

    #[test]
    fn closed_native_window_removes_logical_mapping() {
        let options = WindowOptions::new("main", "Boot title");
        let mut runtime = IcedSingleWindowRuntime::<TestApp>::new(
            TestApp,
            options,
            empty_desktop_integration_plan(),
        );
        let native_id = window::Id::unique();
        let runtime_window = RuntimeWindow {
            logical_id: WindowId::new("mini"),
            options: WindowOptions::new("mini", "Mini"),
        };
        runtime
            .logical_windows
            .insert(WindowId::new("mini"), native_id);
        runtime.native_windows.insert(native_id, runtime_window);

        let _ = IcedSingleWindowRuntime::<TestApp>::update(
            &mut runtime,
            IcedRuntimeMessage::WindowClosed(native_id),
        );

        assert!(!runtime.logical_windows.contains_key(&WindowId::new("mini")));
        assert!(!runtime.native_windows.contains_key(&native_id));
    }

    #[test]
    fn opened_native_window_dispatches_logical_opened_event() {
        let options = WindowOptions::new("main", "Boot title");
        let mut runtime = IcedSingleWindowRuntime::<WindowEventRecorderApp>::new(
            WindowEventRecorderApp { events: Vec::new() },
            options,
            empty_desktop_integration_plan(),
        );
        let native_id = window::Id::unique();
        runtime.pending_windows.insert(
            native_id,
            RuntimeWindow {
                logical_id: WindowId::new("mini"),
                options: WindowOptions::new("mini", "Mini"),
            },
        );

        let _ = IcedSingleWindowRuntime::<WindowEventRecorderApp>::update(
            &mut runtime,
            IcedRuntimeMessage::WindowOpened(native_id),
        );

        assert_eq!(
            runtime.app.events,
            vec![WindowEvent::Opened(WindowId::new("mini"))]
        );
        assert_eq!(
            runtime.logical_windows.get(&WindowId::new("mini")),
            Some(&native_id)
        );
        assert!(runtime.native_windows.contains_key(&native_id));
    }

    #[test]
    fn closed_native_window_dispatches_logical_closed_event() {
        let options = WindowOptions::new("main", "Boot title");
        let mut runtime = IcedSingleWindowRuntime::<WindowEventRecorderApp>::new(
            WindowEventRecorderApp { events: Vec::new() },
            options,
            empty_desktop_integration_plan(),
        );
        let native_id = window::Id::unique();
        let runtime_window = RuntimeWindow {
            logical_id: WindowId::new("mini"),
            options: WindowOptions::new("mini", "Mini"),
        };
        runtime
            .logical_windows
            .insert(WindowId::new("mini"), native_id);
        runtime.native_windows.insert(native_id, runtime_window);

        let _ = IcedSingleWindowRuntime::<WindowEventRecorderApp>::update(
            &mut runtime,
            IcedRuntimeMessage::WindowClosed(native_id),
        );

        assert_eq!(
            runtime.app.events,
            vec![WindowEvent::Closed(WindowId::new("mini"))]
        );
        assert!(!runtime.logical_windows.contains_key(&WindowId::new("mini")));
        assert!(!runtime.native_windows.contains_key(&native_id));
    }

    #[test]
    fn focused_native_window_dispatches_logical_focus_event() {
        let options = WindowOptions::new("main", "Boot title");
        let mut runtime = IcedSingleWindowRuntime::<WindowEventRecorderApp>::new(
            WindowEventRecorderApp { events: Vec::new() },
            options,
            empty_desktop_integration_plan(),
        );
        let native_id = window::Id::unique();
        runtime
            .logical_windows
            .insert(WindowId::new("mini"), native_id);
        runtime.native_windows.insert(
            native_id,
            RuntimeWindow {
                logical_id: WindowId::new("mini"),
                options: WindowOptions::new("mini", "Mini"),
            },
        );

        let _ = IcedSingleWindowRuntime::<WindowEventRecorderApp>::update(
            &mut runtime,
            IcedRuntimeMessage::WindowNativeEvent(native_id, window::Event::Focused),
        );

        assert_eq!(
            runtime.app.events,
            vec![WindowEvent::Focused(WindowId::new("mini"))]
        );
    }

    #[test]
    fn rescaled_native_window_dispatches_logical_dpi_event() {
        let options = WindowOptions::new("main", "Boot title");
        let mut runtime = IcedSingleWindowRuntime::<WindowEventRecorderApp>::new(
            WindowEventRecorderApp { events: Vec::new() },
            options,
            empty_desktop_integration_plan(),
        );
        let native_id = window::Id::unique();
        runtime
            .logical_windows
            .insert(WindowId::new("mini"), native_id);
        runtime.native_windows.insert(
            native_id,
            RuntimeWindow {
                logical_id: WindowId::new("mini"),
                options: WindowOptions::new("mini", "Mini"),
            },
        );

        let _ = IcedSingleWindowRuntime::<WindowEventRecorderApp>::update(
            &mut runtime,
            IcedRuntimeMessage::WindowNativeEvent(native_id, window::Event::Rescaled(1.5)),
        );

        assert_eq!(
            runtime.app.events,
            vec![WindowEvent::DpiChanged(WindowId::new("mini"))]
        );
    }

    #[test]
    fn platform_window_events_are_filtered_by_logical_subscription_id() {
        let options = WindowOptions::new("main", "Boot title");
        let mut runtime = IcedSingleWindowRuntime::<TestApp>::new(
            TestApp,
            options,
            empty_desktop_integration_plan(),
        );

        assert!(runtime
            .platform_event_task(PlatformEvent::Window(WindowEvent::CloseRequested(
                WindowId::new("mini"),
            )))
            .is_none());
        assert!(runtime
            .platform_event_task(PlatformEvent::Window(WindowEvent::CloseRequested(
                WindowId::new("main"),
            )))
            .is_some());
    }

    #[test]
    fn compiles_disabled_and_read_only_combo_boxes_with_combo_chrome() {
        let disabled_view = combo_box([
            ComboBoxItem::new("en", "English"),
            ComboBoxItem::new("zh", "Chinese"),
        ])
        .selected("en")
        .state(ControlState::default().disabled())
        .on_change(Msg::Pick)
        .into_view();
        let _disabled_element: IcedElement<'_, Msg> = IcedAdapter::compile_view(&disabled_view);

        let read_only_view = combo_box([
            ComboBoxItem::new("en", "English"),
            ComboBoxItem::new("zh", "Chinese"),
        ])
        .selected("zh")
        .into_view();
        let _read_only_element: IcedElement<'_, Msg> = IcedAdapter::compile_view(&read_only_view);
    }

    #[test]
    fn compiles_disabled_and_read_only_sliders_with_slider_chrome() {
        let disabled_view = slider(1.2)
            .range(0.5, 3.0)
            .state(ControlState::default().disabled())
            .on_change(|_| Msg::Run)
            .into_view();
        let _disabled_element: IcedElement<'_, Msg> = IcedAdapter::compile_view(&disabled_view);

        let read_only_view = slider(0.7).range(0.0, 1.0).into_view();
        let _read_only_element: IcedElement<'_, Msg> = IcedAdapter::compile_view(&read_only_view);
    }

    #[test]
    fn compiles_mini_window_like_view_to_iced_element() {
        let view = page("Mini")
            .content(
                column((
                    text_editor("Selected text")
                        .id("mini.input")
                        .on_input(Msg::Input),
                    command_bar((primary_button("Translate").on_press(Msg::Run),)),
                    service_result_list([ResultItem::new("openai", "OpenAI", "Streaming")])
                        .on_copy(Msg::Run)
                        .on_speak(Msg::Run),
                ))
                .padding(16)
                .spacing(12),
            )
            .into_view();

        let _element: IcedElement<'_, Msg> = IcedAdapter::compile_view(&view);
    }

    #[test]
    fn compiles_pointer_region_to_iced_element() {
        let view = page("Pointer")
            .content(
                pointer_region(spacer().height(Length::Fill))
                    .id("pointer.surface")
                    .on_move(Msg::Pointer)
                    .on_left_down(Msg::Pointer)
                    .on_left_up(Msg::Pointer)
                    .on_double_click(Msg::Pointer)
                    .on_wheel(Msg::Wheel)
                    .on_right_down(Msg::Run)
                    .on_escape(Msg::Run),
            )
            .into_view();

        let _element: IcedElement<'_, Msg> = IcedAdapter::compile_view(&view);
    }

    #[test]
    fn compiles_text_editor_with_stateful_multiline_content() {
        let content = IcedTextEditorContent::with_text("Line 1\nLine 2");
        let view = page("Editor")
            .content(
                text_editor("Line 1\nLine 2")
                    .id("editor")
                    .on_key(
                        TextEditorKey::Enter,
                        TextEditorKeyModifiers::none(),
                        Msg::Run,
                    )
                    .min_height(120)
                    .on_input(Msg::Input),
            )
            .into_view();

        let _element: IcedElement<'_, Msg> =
            IcedAdapter::compile_view_with_text_editors(&view, |id| {
                (id == "editor").then_some(&content)
            });
    }

    #[test]
    fn finds_focused_text_editor_inside_view_tree() {
        let view = page("Editor")
            .content(
                card("Input").content(
                    column((
                        text_editor("").id("unfocused").on_input(Msg::Input),
                        text_editor("ready")
                            .id("focused-editor")
                            .focused(true)
                            .on_input(Msg::Input),
                    ))
                    .spacing(8),
                ),
            )
            .into_view();

        assert_eq!(
            focused_text_editor_id(&view).as_deref(),
            Some("focused-editor")
        );
    }

    #[test]
    fn custom_text_editor_key_binding_matches_exact_modifiers_and_keeps_default_bindings() {
        let bindings = vec![TextEditorKeyBinding {
            key: TextEditorKey::Tab,
            modifiers: TextEditorKeyModifiers::shift(),
            message: Msg::Run,
        }];

        let shift_tab = iced_text_editor_state::KeyPress {
            key: keyboard::Key::Named(keyboard::key::Named::Tab),
            modified_key: keyboard::Key::Named(keyboard::key::Named::Tab),
            physical_key: keyboard::key::Physical::Code(keyboard::key::Code::Tab),
            modifiers: keyboard::Modifiers::SHIFT,
            text: None,
            status: iced_text_editor_state::Status::Focused { is_hovered: false },
        };

        assert!(matches!(
            text_editor_key_binding(&bindings, &shift_tab),
            Some(iced_text_editor_state::Binding::Custom(Msg::Run))
        ));

        let plain_tab = iced_text_editor_state::KeyPress {
            modifiers: keyboard::Modifiers::empty(),
            ..shift_tab
        };

        assert!(text_editor_key_binding(&bindings, &plain_tab).is_none());
        assert!(iced_text_editor_state::Binding::<Msg>::from_key_press(plain_tab).is_none());
    }

    #[test]
    fn compiles_view_with_explicit_dark_theme() {
        let content = IcedTextEditorContent::with_text("Selected text");
        let view = page("Mini")
            .content(
                column((
                    text("Ready"),
                    text_editor("Selected text")
                        .id("mini.input")
                        .on_input(Msg::Input),
                    primary_button("Translate").on_press(Msg::Run),
                ))
                .padding(16)
                .spacing(12),
            )
            .into_view();

        let _element: IcedElement<'_, Msg> = IcedAdapter::compile_view_with_text_editors_and_theme(
            &view,
            |id| (id == "mini.input").then_some(&content),
            &ThemeTokens::fluent_dark(),
        );
    }

    #[test]
    fn title_bar_uses_window_background_like_winui_chrome() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);
        let style = title_bar_container_style(visual);

        assert_eq!(
            optional_background_color(style.background),
            iced_color(theme.background)
        );
    }

    #[test]
    fn title_bar_title_uses_native_caption_typography() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);
        let font = title_bar_title_font();

        assert_eq!(font.weight, font::Weight::Normal);
        assert_eq!(title_bar_title_size(visual), theme.typography.caption);
    }

    #[test]
    fn caption_buttons_use_mdl2_chrome_glyphs() {
        assert_eq!(CaptionButtonKind::Minimize.glyph(), '\u{E921}');
        assert_eq!(CaptionButtonKind::ToggleMaximize.glyph(), '\u{E922}');
        assert_eq!(CaptionButtonKind::Close.glyph(), '\u{E8BB}');
        assert_eq!(caption_icon_font(), Font::with_name("Segoe MDL2 Assets"));
    }

    #[test]
    fn app_icons_use_fluent_font_to_match_winui_font_icon() {
        assert_eq!(icon_font(), Font::with_name("Segoe Fluent Icons"));
        assert_eq!(
            icon_symbol_font('\u{E713}'),
            Font::with_name("Segoe Fluent Icons")
        );
    }

    #[test]
    fn status_symbols_use_windows_emoji_font_for_winui_badges() {
        let warning_font = text_font_for_value(TextStyle::Warning, "⚠");
        assert_eq!(
            warning_font.family,
            Font::with_name("Segoe UI Emoji").family
        );
        assert_eq!(warning_font.weight, font::Weight::Semibold);

        let success_font = text_font_for_value(TextStyle::Success, "✓");
        assert_eq!(
            success_font.family,
            Font::with_name("Segoe UI Emoji").family
        );
        assert_eq!(success_font.weight, font::Weight::Semibold);
    }

    #[test]
    fn expander_status_spacing_matches_winui_header_grid() {
        assert_eq!(expander_header_trailing_spacing(false, false), 8);
        assert_eq!(expander_header_trailing_spacing(false, true), 8);
        assert_eq!(expander_header_trailing_spacing(true, false), 8);
        assert_eq!(expander_header_trailing_spacing(true, true), 20);
    }

    #[test]
    fn maps_visual_theme_to_iced_page_button_and_input_styles() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let page = page_container_style(visual);
        assert_eq!(
            optional_background_color(page.background),
            iced_color(theme.background)
        );
        assert_eq!(page.text_color, Some(iced_color(theme.text_primary)));

        let primary = button_style(
            visual,
            ButtonKind::Primary,
            iced::widget::button::Status::Active,
        );
        assert_eq!(
            optional_background_color(primary.background),
            iced_color(theme.accent.base)
        );
        assert_eq!(primary.text_color, iced::Color::WHITE);
        assert_eq!(primary.border.width, theme.stroke.control);
        assert_eq!(primary.border.radius.top_left, theme.radius.control);
        assert_eq!(primary.shadow, Shadow::default());

        let primary_hover = button_style(
            visual,
            ButtonKind::Primary,
            iced::widget::button::Status::Hovered,
        );
        assert_eq!(
            optional_background_color(primary_hover.background),
            iced_color(theme.accent_hover)
        );
        assert_eq!(primary_hover.border.color, iced_color(theme.accent_hover));

        let primary_pressed = button_style(
            visual,
            ButtonKind::Primary,
            iced::widget::button::Status::Pressed,
        );
        assert_eq!(
            optional_background_color(primary_pressed.background),
            iced_color(theme.accent_pressed)
        );
        assert_eq!(
            primary_pressed.border.color,
            iced_color(theme.accent_pressed)
        );

        let primary_round = button_style(
            visual,
            ButtonKind::PrimaryRound,
            iced::widget::button::Status::Active,
        );
        assert_eq!(
            optional_background_color(primary_round.background),
            iced_color(theme.accent.base)
        );
        assert_eq!(
            primary_round.text_color,
            iced_color(theme.accent_foreground)
        );
        assert_eq!(
            primary_round.border.radius.top_left,
            theme.control.primary_round_button / 2.0
        );

        let focused = text_input_style(
            visual,
            iced::widget::text_input::Status::Focused { is_hovered: false },
            TextEditorChrome::Standard,
        );
        assert_eq!(focused.border.color, iced_color(theme.focus));
        assert_eq!(focused.border.width, theme.stroke.focus);
        assert_eq!(focused.selection, iced_color(theme.accent.light_2));

        let frameless_editor_active = text_editor_style(
            visual,
            iced::widget::text_editor::Status::Active,
            TextEditorChrome::Frameless,
        );
        assert_eq!(
            frameless_editor_active.background,
            Background::Color(iced_color(theme.input_surface))
        );
        assert_eq!(
            frameless_editor_active.border.color,
            iced_color(theme.border)
        );
        assert_eq!(frameless_editor_active.border.width, 0.0);

        let frameless_editor = text_editor_style(
            visual,
            iced::widget::text_editor::Status::Focused { is_hovered: false },
            TextEditorChrome::Frameless,
        );
        assert_eq!(
            frameless_editor.background,
            Background::Color(iced_color(theme.input_surface))
        );
        assert_eq!(frameless_editor.border.color, iced_color(theme.border));
        assert_eq!(frameless_editor.border.width, 0.0);
    }

    #[test]
    fn maps_visual_theme_to_remaining_control_and_surface_styles() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let toggle_on = toggle_switch_style(
            visual,
            iced::widget::toggler::Status::Active { is_toggled: true },
        );
        assert_eq!(
            background_color(toggle_on.background),
            iced_color(theme.accent.base)
        );
        assert_eq!(
            background_color(toggle_on.foreground),
            iced_color(theme.accent_foreground)
        );
        assert_eq!(toggle_on.background_border_width, theme.stroke.control);
        assert_eq!(
            toggle_on.padding_ratio, 0.20,
            "WinUI ToggleSwitch thumb is closer to 12px inside a 20px track"
        );
        assert_eq!(toggle_switch_label("On", true), "On");
        assert_eq!(toggle_switch_label("On", false), "Off");
        assert_eq!(toggle_switch_label("Auto", true), "Auto");
        assert_eq!(toggle_switch_label("Auto", false), "Manual");
        assert_eq!(toggle_switch_label("\u{5f00}", false), "\u{5173}");
        assert_eq!(
            toggle_switch_label("\u{81ea}\u{52a8}", false),
            "\u{624b}\u{52a8}"
        );

        let toggle_off = toggle_switch_style(
            visual,
            iced::widget::toggler::Status::Active { is_toggled: false },
        );
        assert_eq!(
            background_color(toggle_off.background),
            iced::Color::from_rgb8(249, 249, 249)
        );
        assert_eq!(
            toggle_off.background_border_color,
            iced::Color::from_rgb8(139, 139, 139)
        );
        assert_eq!(
            background_color(toggle_off.foreground),
            iced::Color::from_rgb8(95, 95, 95)
        );

        let pick_list = pick_list_style(visual, iced::widget::pick_list::Status::Hovered);
        assert_eq!(
            background_color(pick_list.background),
            iced_color(theme.surface)
        );
        assert_eq!(pick_list.border.color, iced_color(theme.border));
        assert_eq!(pick_list.border.width, theme.stroke.control);

        let menu = menu_style(visual);
        assert_eq!(background_color(menu.background), iced_color(theme.surface));
        assert_eq!(
            background_color(menu.selected_background),
            iced_color(theme.accent.base)
        );
        assert!(menu.shadow.blur_radius > 0.0);

        let settings_row = settings_row_container_style(visual);
        assert_eq!(
            optional_background_color(settings_row.background),
            iced_color(theme.surface)
        );
        assert_eq!(settings_row.border.width, theme.stroke.control);

        let default_expander_style = FluentStyle::new();
        let expander_active = expander_container_style_with_state(
            visual,
            &ControlState::default(),
            &default_expander_style,
        );
        let expander_hover = expander_container_style_with_state(
            visual,
            &ControlState::default().hovered(true),
            &default_expander_style,
        );
        let expander_pressed = expander_container_style_with_state(
            visual,
            &ControlState::default().pressed(true),
            &default_expander_style,
        );
        assert_eq!(
            optional_background_color(expander_active.background),
            iced::Color::from_rgb8(253, 253, 254)
        );
        assert_eq!(expander_active.border.color, iced_color(theme.border));
        assert_eq!(expander_active.border.width, theme.stroke.control);
        assert_eq!(
            optional_background_color(expander_hover.background),
            iced::Color::from_rgb8(253, 253, 254),
            "WinUI Settings service expanders keep the header bar fill stable while hovered"
        );
        assert_eq!(
            optional_background_color(expander_pressed.background),
            iced::Color::from_rgb8(253, 253, 254),
            "WinUI Settings service expanders keep the header bar fill stable while pressed"
        );
        let expander_header_hover = expander_header_button_style(
            visual,
            &default_expander_style,
            iced::widget::button::Status::Hovered,
        );
        let expander_header_pressed = expander_header_button_style(
            visual,
            &default_expander_style,
            iced::widget::button::Status::Pressed,
        );
        assert_eq!(
            optional_background_color(expander_header_hover.background),
            iced::Color::from_rgb8(253, 253, 254),
            "WinUI Expander header button does not tint the bar on hover"
        );
        assert_eq!(
            optional_background_color(expander_header_pressed.background),
            iced::Color::from_rgb8(253, 253, 254),
            "WinUI Expander header button does not tint the bar on press"
        );
        assert_eq!(expander_header_hover.border.width, 0.0);
        let openai_header = expander_header_button_style(
            visual,
            &FluentStyle::from_classes("header-surface-fafbfd"),
            iced::widget::button::Status::Active,
        );
        assert_eq!(
            optional_background_color(openai_header.background),
            iced::Color::from_rgb8(250, 251, 253)
        );
        let expander_content = expander_content_container_style(visual, &FluentStyle::new());
        assert_eq!(
            optional_background_color(expander_content.background),
            iced::Color::from_rgb8(246, 247, 249)
        );
        let local_ai_expander_content = expander_content_container_style(
            visual,
            &FluentStyle::from_classes("content-surface-f5f5f4"),
        );
        assert_eq!(
            optional_background_color(local_ai_expander_content.background),
            iced::Color::from_rgb8(245, 245, 244)
        );
        let info_expander_content =
            expander_content_container_style(visual, &FluentStyle::from_classes("info-bar"));
        assert_eq!(
            optional_background_color(info_expander_content.background),
            iced::Color::from_rgb8(238, 239, 240)
        );
        let expander_content_divider = expander_content_divider_style(visual);
        assert_eq!(
            optional_background_color(expander_content_divider.background),
            iced_color(theme.border)
        );

        let divider = utility_container_style(&FluentStyle::from_classes("bg-border"), visual);
        assert_eq!(
            optional_background_color(divider.background),
            iced_color(theme.border)
        );
        assert_eq!(divider.border.width, 0.0);

        let input_container = utility_container_style(
            &FluentStyle::from_classes("input-surface border rounded-2xl"),
            visual,
        );
        assert_eq!(
            optional_background_color(input_container.background),
            iced_color(theme.input_surface)
        );
        assert_eq!(
            input_container.border.color,
            iced_color(theme.floating_input_border)
        );
        assert_eq!(input_container.border.width, theme.stroke.control);
        assert_eq!(input_container.border.radius.top_left, 16.0);
        let exact_radius_container = utility_container_style(
            &FluentStyle::from_classes("surface-card border rounded-[10px]"),
            visual,
        );
        assert_eq!(exact_radius_container.border.width, theme.stroke.control);
        assert_eq!(exact_radius_container.border.radius.top_left, 10.0);

        let result_card = result_card_container_style(visual);
        assert_eq!(
            optional_background_color(result_card.background),
            iced_color(theme.result_surface)
        );
        // Result rows are flat outlined strips like the WinUI reference.
        assert_eq!(result_card.shadow, Shadow::default());

        let floating_action = button_style(
            visual,
            ButtonKind::FloatingAction,
            iced::widget::button::Status::Active,
        );
        assert_eq!(
            optional_background_color(floating_action.background),
            iced_color(theme.floating_action_surface)
                .scale_alpha(theme.effects.floating_action_rest_opacity)
        );
        assert_eq!(floating_action.shadow, Shadow::default());
    }

    #[test]
    fn maps_fluent_interaction_effects_to_state_styles() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let standard_hover = button_style(
            visual,
            ButtonKind::Standard,
            iced::widget::button::Status::Hovered,
        );
        assert_eq!(
            optional_background_color(standard_hover.background),
            iced_color(theme.surface),
            "WinUI Services command buttons keep the active surface on hover"
        );
        assert_eq!(standard_hover.border.color, iced_color(theme.border));

        let standard_pressed = button_style(
            visual,
            ButtonKind::Standard,
            iced::widget::button::Status::Pressed,
        );
        assert_eq!(
            optional_background_color(standard_pressed.background),
            iced_color(theme.button_pressed)
        );
        assert_eq!(standard_pressed.border.color, iced_color(theme.border));

        let tile_hover = button_style(
            visual,
            ButtonKind::Tile,
            iced::widget::button::Status::Hovered,
        );
        assert_eq!(
            optional_background_color(tile_hover.background),
            iced_color(theme.tile_surface),
            "WinUI settings tab template keeps unselected tab backgrounds bound to the tab surface while hovered"
        );
        assert_eq!(tile_hover.text_color, iced_color(theme.tile_foreground));
        assert_eq!(tile_hover.border.color, iced_color(theme.tile_border));

        let tile_pressed = button_style(
            visual,
            ButtonKind::Tile,
            iced::widget::button::Status::Pressed,
        );
        assert_eq!(
            optional_background_color(tile_pressed.background),
            iced_color(theme.tile_surface),
            "WinUI settings tab template keeps unselected tab backgrounds bound to the tab surface while pressed"
        );
        assert_eq!(tile_pressed.text_color, iced_color(theme.tile_foreground));
        assert_eq!(tile_pressed.border.color, iced_color(theme.tile_border));

        let result_header_hover =
            result_header_button_style(visual, iced::widget::button::Status::Hovered);
        assert_eq!(
            optional_background_color(result_header_hover.background),
            iced_color(theme.result_header),
            "WinUI result headers keep their resting fill while hovered"
        );

        let result_header_pressed =
            result_header_button_style(visual, iced::widget::button::Status::Pressed);
        assert_eq!(
            optional_background_color(result_header_pressed.background),
            iced_color(theme.result_header),
            "WinUI result headers keep their resting fill while pressed"
        );

        let floating_action_hover = button_style(
            visual,
            ButtonKind::FloatingAction,
            iced::widget::button::Status::Hovered,
        );
        assert_eq!(
            optional_background_color(floating_action_hover.background),
            iced_color(theme.floating_action_surface)
                .scale_alpha(theme.effects.floating_action_hover_opacity)
        );
        assert_eq!(
            floating_action_hover.border.color,
            iced_color(theme.floating_action_border)
                .scale_alpha(theme.effects.floating_action_hover_opacity)
        );

        let floating_action_pressed = button_style(
            visual,
            ButtonKind::FloatingAction,
            iced::widget::button::Status::Pressed,
        );
        assert_eq!(
            optional_background_color(floating_action_pressed.background),
            iced_color(theme.floating_action_surface)
                .scale_alpha(theme.effects.floating_action_pressed_opacity)
        );
        assert_eq!(
            floating_action_pressed.text_color,
            iced_color(theme.accent.base)
                .scale_alpha(theme.effects.floating_action_pressed_opacity)
        );

        let primary_round_hover = button_style(
            visual,
            ButtonKind::PrimaryRound,
            iced::widget::button::Status::Hovered,
        );
        assert_eq!(
            optional_background_color(primary_round_hover.background),
            iced_color(theme.accent.base),
            "WinUI primary round translate buttons keep the accent fill on hover"
        );
        assert_eq!(
            primary_round_hover.border.color,
            iced_color(theme.accent.base)
        );

        let primary_round_pressed = button_style(
            visual,
            ButtonKind::PrimaryRound,
            iced::widget::button::Status::Pressed,
        );
        assert_eq!(
            optional_background_color(primary_round_pressed.background),
            iced_color(theme.accent_pressed),
            "WinUI primary round translate buttons use the pressed accent fill"
        );
        assert_eq!(
            primary_round_pressed.border.color,
            iced_color(theme.accent_pressed)
        );
        assert_eq!(
            primary_round_pressed.text_color,
            iced_color(theme.accent_foreground)
        );

        let focused_standard = button_style_with_state(
            visual,
            ButtonKind::Standard,
            true,
            false,
            iced::widget::button::Status::Active,
        );
        assert_eq!(focused_standard.border.color, iced_color(theme.focus));
        assert_eq!(focused_standard.border.width, theme.stroke.focus);

        let focused_icon = button_style_with_state(
            visual,
            ButtonKind::Icon,
            true,
            false,
            iced::widget::button::Status::Active,
        );
        assert_eq!(focused_icon.border.color, iced_color(theme.focus));
        assert_eq!(focused_icon.border.width, theme.stroke.focus);

        let focused_disabled = button_style_with_state(
            visual,
            ButtonKind::Standard,
            true,
            false,
            iced::widget::button::Status::Disabled,
        );
        assert_eq!(focused_disabled.border.color, iced_color(theme.border));
        assert_eq!(focused_disabled.border.width, theme.stroke.control);

        let toggle_on_hover = toggle_switch_style(
            visual,
            iced::widget::toggler::Status::Hovered { is_toggled: true },
        );
        assert_eq!(
            background_color(toggle_on_hover.background),
            iced_color(theme.accent.base),
            "WinUI Services toggles keep the checked track fill on hover"
        );

        let toggle_off_hover = toggle_switch_style(
            visual,
            iced::widget::toggler::Status::Hovered { is_toggled: false },
        );
        assert_eq!(
            background_color(toggle_off_hover.background),
            iced_color(theme.surface),
            "WinUI Services toggles keep the unchecked track fill subtle on hover"
        );

        let slider_hover = slider_style(visual, iced::widget::slider::Status::Hovered);
        assert_eq!(
            background_color(slider_hover.rail.backgrounds.0),
            iced_color(theme.accent_hover)
        );
    }

    #[test]
    fn auto_scroll_policy_keeps_winui_like_scrollbars_hidden_at_rest() {
        use iced::widget::scrollable::Scrollbar;

        assert_eq!(scroll_bar(ScrollPolicy::Auto, false), Scrollbar::hidden());
        assert_eq!(scroll_bar(ScrollPolicy::Never, false), Scrollbar::hidden());
        assert_eq!(scroll_bar(ScrollPolicy::Always, false), visible_scrollbar());
    }

    #[test]
    fn auto_scroll_policy_can_show_winui_like_scrollbars_during_interaction() {
        use iced::widget::scrollable::Scrollbar;

        assert_eq!(scroll_bar(ScrollPolicy::Auto, true), visible_scrollbar());
        assert_eq!(scroll_bar(ScrollPolicy::Never, true), Scrollbar::hidden());
        assert_eq!(scroll_bar(ScrollPolicy::Always, true), visible_scrollbar());
    }

    #[test]
    fn explicit_button_control_state_overrides_runtime_status_for_previews() {
        let runtime_active = iced::widget::button::Status::Active;
        let runtime_hovered = iced::widget::button::Status::Hovered;

        let hovered = ControlState::default().hovered(true);
        assert_eq!(
            button_status_with_state(&hovered, runtime_active),
            iced::widget::button::Status::Hovered
        );

        let pressed = ControlState::default().hovered(true).pressed(true);
        assert_eq!(
            button_status_with_state(&pressed, runtime_active),
            iced::widget::button::Status::Pressed
        );

        let disabled = ControlState::default()
            .hovered(true)
            .pressed(true)
            .disabled();
        assert_eq!(
            button_status_with_state(&disabled, runtime_hovered),
            iced::widget::button::Status::Disabled
        );

        let inherited = ControlState::default();
        assert_eq!(
            button_status_with_state(&inherited, runtime_hovered),
            runtime_hovered
        );
    }

    #[test]
    fn explicit_input_control_state_overrides_runtime_status_for_previews() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let focused = ControlState::default().focused(true);
        let text_input = text_input_style(
            visual,
            text_input_status_with_state(&focused, iced::widget::text_input::Status::Active),
            TextEditorChrome::Standard,
        );
        assert_eq!(text_input.border.color, iced_color(theme.focus));
        assert_eq!(text_input.border.width, theme.stroke.focus);

        let frameless_text_input = text_input_style(
            visual,
            text_input_status_with_state(&focused, iced::widget::text_input::Status::Active),
            TextEditorChrome::Frameless,
        );
        assert_eq!(frameless_text_input.border.color, iced_color(theme.border));
        assert_eq!(frameless_text_input.border.width, 0.0);

        let frameless_text_input_hovered = text_input_style(
            visual,
            text_input_status_with_state(
                &ControlState::default().hovered(true),
                iced::widget::text_input::Status::Active,
            ),
            TextEditorChrome::Frameless,
        );
        assert_eq!(
            frameless_text_input_hovered.border.color,
            iced_color(theme.border)
        );
        assert_eq!(frameless_text_input_hovered.border.width, 0.0);

        let frameless_text_editor_hovered = text_editor_style(
            visual,
            text_editor_status_with_state(
                &ControlState::default().hovered(true),
                iced::widget::text_editor::Status::Active,
            ),
            TextEditorChrome::Frameless,
        );
        assert_eq!(
            frameless_text_editor_hovered.border.color,
            iced_color(theme.border)
        );
        assert_eq!(frameless_text_editor_hovered.border.width, 0.0);

        let frameless_text_editor_focused = text_editor_style(
            visual,
            text_editor_status_with_state(
                &ControlState::default().focused(true),
                iced::widget::text_editor::Status::Active,
            ),
            TextEditorChrome::Frameless,
        );
        assert_eq!(
            frameless_text_editor_focused.border.color,
            iced_color(theme.border)
        );
        assert_eq!(frameless_text_editor_focused.border.width, 0.0);

        let frameless_text_input_active = text_input_style(
            visual,
            iced::widget::text_input::Status::Active,
            TextEditorChrome::Frameless,
        );
        assert_eq!(
            frameless_text_input_active.border.color,
            iced_color(theme.border)
        );
        assert_eq!(frameless_text_input_active.border.width, 0.0);

        let disabled = ControlState::default().hovered(true).disabled();
        let editor = text_editor_style(
            visual,
            text_editor_status_with_state(&disabled, iced::widget::text_editor::Status::Hovered),
            TextEditorChrome::Standard,
        );
        assert_eq!(editor.background, Background::Color(visual.surface_alt));
        assert_eq!(editor.value, iced_color(theme.text_secondary));

        let slider_pressed = ControlState::default().hovered(true).pressed(true);
        let slider = slider_style(
            visual,
            slider_status_with_state(&slider_pressed, iced::widget::slider::Status::Active),
        );
        assert_eq!(
            background_color(slider.rail.backgrounds.0),
            iced_color(theme.accent_pressed)
        );

        let slider_focused = ControlState::default().focused(true);
        assert_eq!(
            slider_status_with_state(&slider_focused, iced::widget::slider::Status::Active),
            iced::widget::slider::Status::Active
        );
        let slider = slider_style_with_state(
            visual,
            iced::widget::slider::Status::Active,
            &slider_focused,
        );
        assert_eq!(background_color(slider.rail.backgrounds.0), visual.accent);
        assert_eq!(slider.handle.border_color, visual.focus);
        assert_eq!(slider.handle.border_width, visual.stroke_focus);

        let slider_disabled = ControlState::default()
            .hovered(true)
            .pressed(true)
            .disabled();
        let slider_active_rail = slider_read_only_rail_style(visual, &slider_disabled, true);
        assert_eq!(
            background_color(slider_active_rail.background.unwrap()),
            visual.text_secondary.scale_alpha(visual.disabled_opacity)
        );
        let slider_thumb = slider_read_only_thumb_style(visual, &slider_disabled);
        assert_eq!(
            slider_thumb.border.color,
            visual.text_secondary.scale_alpha(visual.disabled_opacity)
        );

        let toggle_pressed = ControlState::default().hovered(true).pressed(true);
        let toggle = toggle_switch_style_with_state(
            visual,
            toggle_switch_status_with_state(
                &toggle_pressed,
                true,
                iced::widget::toggler::Status::Active { is_toggled: true },
            ),
            &toggle_pressed,
        );
        assert_eq!(
            background_color(toggle.background),
            iced_color(theme.accent_pressed)
        );
        assert_eq!(
            background_color(toggle.foreground),
            visual.text_on_accent.scale_alpha(0.72)
        );

        let toggle_focused = ControlState::default().focused(true);
        assert_eq!(
            toggle_switch_status_with_state(
                &toggle_focused,
                true,
                iced::widget::toggler::Status::Active { is_toggled: true },
            ),
            iced::widget::toggler::Status::Active { is_toggled: true }
        );
        let toggle = toggle_switch_style_with_state(
            visual,
            toggle_switch_status_with_state(
                &toggle_focused,
                true,
                iced::widget::toggler::Status::Active { is_toggled: true },
            ),
            &toggle_focused,
        );
        assert_eq!(background_color(toggle.background), visual.accent);
        assert_eq!(toggle.background_border_color, visual.focus);
        assert_eq!(toggle.background_border_width, visual.stroke_focus);

        let combo_hover = ControlState::default().hovered(true);
        let combo = pick_list_style_with_state(
            visual,
            iced::widget::pick_list::Status::Active,
            &combo_hover,
        );
        assert_eq!(
            background_color(combo.background),
            iced_color(theme.button_hover)
        );
        assert_eq!(combo.border.color, visual.border);
        assert_eq!(combo.border.width, visual.stroke_control);

        let combo_pressed = ControlState::default().hovered(true).pressed(true);
        let combo = pick_list_style_with_state(
            visual,
            iced::widget::pick_list::Status::Active,
            &combo_pressed,
        );
        assert_eq!(
            background_color(combo.background),
            iced_color(theme.button_pressed)
        );

        let combo_focused = ControlState::default().focused(true);
        assert_eq!(
            pick_list_status_with_state(&combo_focused, iced::widget::pick_list::Status::Active),
            iced::widget::pick_list::Status::Active
        );
        let combo = pick_list_style_with_state(
            visual,
            iced::widget::pick_list::Status::Active,
            &combo_focused,
        );
        assert_eq!(background_color(combo.background), visual.surface);
        assert_eq!(combo.border.color, visual.focus);
        assert_eq!(combo.border.width, visual.stroke_focus);

        let combo_disabled = ControlState::default()
            .hovered(true)
            .pressed(true)
            .disabled();
        let combo = pick_list_style_with_state(
            visual,
            iced::widget::pick_list::Status::Hovered,
            &combo_disabled,
        );
        assert_eq!(
            background_color(combo.background),
            iced_color(theme.surface_alt)
        );
        assert_eq!(
            combo.text_color,
            visual.text_secondary.scale_alpha(visual.disabled_opacity)
        );
        assert_eq!(
            read_only_combo_box_style(visual, &combo_disabled)
                .border
                .color,
            iced_color(theme.border)
        );
    }

    #[test]
    fn result_header_control_state_overrides_runtime_status_for_previews() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let hovered = ControlState::default().hovered(true);
        let active_style = result_header_button_style(visual, iced::widget::button::Status::Active);
        assert_eq!(
            optional_background_color(active_style.background),
            iced_color(theme.result_header),
            "resting result rows use the WinUI result header fill"
        );

        let hover_style = result_header_button_style(
            visual,
            button_status_with_state(&hovered, iced::widget::button::Status::Active),
        );
        assert_eq!(
            optional_background_color(hover_style.background),
            iced_color(theme.result_header)
        );
        assert_eq!(hover_style.border.radius.top_left, theme.radius.control);

        let pressed = ControlState::default().pressed(true);
        let pressed_style = result_header_button_style(
            visual,
            button_status_with_state(&pressed, iced::widget::button::Status::Active),
        );
        assert_eq!(
            optional_background_color(pressed_style.background),
            iced_color(theme.result_header)
        );
    }

    #[test]
    fn service_result_icons_use_service_accent_colors() {
        let fallback = iced::Color::from_rgb8(1, 2, 3);

        assert_eq!(
            service_result_icon_color(
                &win_fluent::IconToken::with_glyph("service-bing", '\u{E774}'),
                fallback
            ),
            iced::Color::from_rgb8(0, 120, 212)
        );
        assert_eq!(
            service_result_icon_color(
                &win_fluent::IconToken::with_glyph("unknown", '\u{E8D4}'),
                fallback
            ),
            fallback
        );
    }

    #[test]
    fn high_contrast_style_uses_solid_focus_without_elevation() {
        let theme = ThemeTokens::high_contrast();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let primary = button_style(
            visual,
            ButtonKind::Primary,
            iced::widget::button::Status::Active,
        );
        assert_eq!(
            optional_background_color(primary.background),
            iced_color(theme.accent.base)
        );
        assert_eq!(primary.text_color, iced::Color::BLACK);
        assert_eq!(primary.shadow, Shadow::default());

        let editor = text_editor_style(
            visual,
            iced::widget::text_editor::Status::Focused { is_hovered: false },
            TextEditorChrome::Standard,
        );
        assert_eq!(
            editor.background,
            Background::Color(iced_color(theme.surface))
        );
        assert_eq!(editor.border.color, iced_color(theme.floating_input_border));
        assert_eq!(editor.border.width, theme.stroke.control);

        let result_card = result_card_container_style(visual);
        assert_eq!(result_card.shadow, Shadow::default());
    }

    #[test]
    fn maps_window_options_to_iced_window_settings() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .min_size(320.0, 220.0)
            .level(WindowLevel::TopMost)
            .frame(WindowFrame::Acrylic)
            .resize_mode(WindowResizeMode::CanResize)
            .skip_taskbar(true);

        let settings = IcedAdapter::window_settings(&options);

        assert_eq!(settings.size, Size::new(420.0, 360.0));
        assert_eq!(settings.min_size, Some(Size::new(320.0, 220.0)));
        assert!(settings.resizable);
        assert!(settings.transparent);
        assert_eq!(settings.level, iced::window::Level::AlwaysOnTop);

        #[cfg(windows)]
        assert!(settings.platform_specific.skip_taskbar);
    }

    #[test]
    fn maps_pop_button_window_options_to_fixed_utility_window_settings() {
        let options = WindowOptions::new("pop-button", "Selection")
            .size(30.0, 30.0)
            .min_size(30.0, 30.0)
            .level(WindowLevel::ToolWindow)
            .frame(WindowFrame::Borderless)
            .resize_mode(WindowResizeMode::Fixed)
            .skip_taskbar(true);

        let settings = IcedAdapter::window_settings(&options);

        assert_eq!(settings.size, Size::new(30.0, 30.0));
        assert_eq!(settings.min_size, Some(Size::new(30.0, 30.0)));
        assert!(!settings.resizable);
        assert!(!settings.minimizable);
        assert!(!settings.decorations);
        assert_eq!(settings.level, iced::window::Level::AlwaysOnTop);

        #[cfg(windows)]
        assert!(settings.platform_specific.skip_taskbar);
    }

    #[test]
    fn show_at_window_options_preserves_utility_flags_and_overrides_placement() {
        let options = WindowOptions::new("pop-button", "Selection")
            .size(30.0, 30.0)
            .min_size(30.0, 30.0)
            .level(WindowLevel::ToolWindow)
            .frame(WindowFrame::Borderless)
            .resize_mode(WindowResizeMode::Fixed)
            .placement(WindowPlacement::CursorOffset { x: 8.0, y: 8.0 })
            .skip_taskbar(true)
            .no_activate(true);

        let show_at_options = show_at_window_options(&options, 408.0, 208.0);

        assert_eq!(show_at_options.id.as_str(), "pop-button");
        assert_eq!(show_at_options.width, 30.0);
        assert_eq!(show_at_options.height, 30.0);
        assert_eq!(show_at_options.level, WindowLevel::ToolWindow);
        assert!(show_at_options.skip_taskbar);
        assert!(show_at_options.no_activate);
        assert!(matches!(
            show_at_options.placement,
            WindowPlacement::Explicit { x: 408.0, y: 208.0 }
        ));
    }

    #[test]
    fn no_activate_utility_windows_apply_native_options_before_showing() {
        let options = WindowOptions::new("pop-button", "Selection")
            .size(30.0, 30.0)
            .level(WindowLevel::ToolWindow)
            .skip_taskbar(true)
            .no_activate(true);

        assert_eq!(
            show_window_steps(&options),
            vec![
                ShowWindowStep::ApplyNativeOptions {
                    delayed_check: false
                },
                ShowWindowStep::ResolvePlacement,
                ShowWindowStep::ShowWindowed,
                ShowWindowStep::ApplyNativeOptions {
                    delayed_check: true
                },
            ]
        );
    }

    #[test]
    fn normal_windows_apply_native_options_before_and_after_showing() {
        let options = WindowOptions::new("main", "Main").size(940.0, 1220.0);

        assert_eq!(
            show_window_steps(&options),
            vec![
                ShowWindowStep::ApplyNativeOptions {
                    delayed_check: false
                },
                ShowWindowStep::ResolvePlacement,
                ShowWindowStep::ShowWindowed,
                ShowWindowStep::ApplyNativeOptions {
                    delayed_check: true
                },
            ]
        );
    }

    #[test]
    fn maps_resolved_window_position_to_iced_window_settings() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 });

        let settings =
            IcedAdapter::window_settings_with_position(&options, Point::new(1500.0, 720.0));

        match settings.position {
            iced::window::Position::Specific(point) => {
                assert_eq!(point, Point::new(1500.0, 720.0));
            }
            position => panic!("expected specific position, got {position:?}"),
        }
    }

    #[test]
    fn maps_iced_screenshot_to_dpi_aware_window_screenshot() {
        let iced_screenshot =
            iced::window::Screenshot::new(vec![0; 200 * 100 * 4], Size::new(200, 100), 2.0);

        let screenshot = IcedAdapter::screenshot_frame(iced_screenshot).unwrap();

        assert_eq!(screenshot.dpi, 192);
        assert_eq!(screenshot.width_physical, 200);
        assert_eq!(screenshot.height_physical, 100);
        assert_eq!(screenshot.width_dips, 100.0);
        assert_eq!(screenshot.height_dips, 50.0);
    }

    #[test]
    fn hotkey_subscription_data_round_trips_token_hotkey() {
        let hotkey = Hotkey::new("mini.translate", HotkeyKey::Function(24))
            .modifier(HotkeyModifier::Control)
            .modifier(HotkeyModifier::Alt)
            .modifier(HotkeyModifier::Shift);

        let data = HotkeySubscriptionData::from(hotkey.clone());

        assert_eq!(data.to_hotkey(), hotkey);
    }

    #[test]
    fn tray_subscription_data_round_trips_native_plan() {
        let plan = win_fluent_platform_win::WindowsTrayPlan {
            tooltip: "Easydict".to_string(),
            icon_path: Some("C:\\Easydict\\AppIcon.ico".to_string()),
            presenter_min_width: Some(300),
            callback_message: 1025,
            item_count: 2,
            default_command_id: Some(1000),
            items: vec![
                win_fluent_platform_win::WindowsTrayItemPlan {
                    id: "show-main".to_string(),
                    label: "Show Easydict".to_string(),
                    tooltip: Some("Show Easydict".to_string()),
                    enabled: true,
                    command_id: 1000,
                    action_kind: ActionKind::Message,
                    kind: win_fluent_platform_win::WindowsTrayItemKind::Command,
                    children: Vec::new(),
                },
                win_fluent_platform_win::WindowsTrayItemPlan {
                    id: "settings".to_string(),
                    label: "Settings".to_string(),
                    tooltip: Some("Settings".to_string()),
                    enabled: false,
                    command_id: 1001,
                    action_kind: ActionKind::Message,
                    kind: win_fluent_platform_win::WindowsTrayItemKind::Command,
                    children: Vec::new(),
                },
            ],
        };

        let data = TraySubscriptionData::from(plan.clone());
        let round_trip = data.to_plan();

        assert_eq!(round_trip.tooltip, plan.tooltip);
        assert_eq!(round_trip.icon_path, plan.icon_path);
        assert_eq!(round_trip.presenter_min_width, Some(300));
        assert_eq!(round_trip.callback_message, plan.callback_message);
        assert_eq!(round_trip.item_count, plan.item_count);
        assert_eq!(round_trip.default_command_id, Some(1000));
        assert_eq!(round_trip.items[0].id, "show-main");
        assert_eq!(
            round_trip.items[0].tooltip.as_deref(),
            Some("Show Easydict")
        );
        assert_eq!(round_trip.items[0].command_id, 1000);
        assert!(round_trip.items[0].enabled);
        assert_eq!(round_trip.items[1].id, "settings");
        assert_eq!(round_trip.items[1].command_id, 1001);
        assert!(!round_trip.items[1].enabled);
    }

    #[test]
    fn tray_subscription_data_preserves_structured_menu_items() {
        let plan = win_fluent_platform_win::WindowsTrayPlan {
            tooltip: "Easydict".to_string(),
            icon_path: Some("C:\\Easydict\\AppIcon.ico".to_string()),
            presenter_min_width: Some(300),
            callback_message: 1025,
            item_count: 2,
            default_command_id: Some(1000),
            items: vec![
                win_fluent_platform_win::WindowsTrayItemPlan {
                    id: String::new(),
                    label: String::new(),
                    tooltip: None,
                    enabled: false,
                    command_id: 0,
                    action_kind: ActionKind::None,
                    kind: win_fluent_platform_win::WindowsTrayItemKind::Separator,
                    children: Vec::new(),
                },
                win_fluent_platform_win::WindowsTrayItemPlan {
                    id: "browser-support".to_string(),
                    label: "Browser Support".to_string(),
                    tooltip: Some("Browser Support".to_string()),
                    enabled: true,
                    command_id: 0,
                    action_kind: ActionKind::None,
                    kind: win_fluent_platform_win::WindowsTrayItemKind::Submenu,
                    children: vec![
                        win_fluent_platform_win::WindowsTrayItemPlan {
                            id: "browser-install".to_string(),
                            label: "Install Browser Support".to_string(),
                            tooltip: Some("Install Browser Support".to_string()),
                            enabled: true,
                            command_id: 1000,
                            action_kind: ActionKind::Message,
                            kind: win_fluent_platform_win::WindowsTrayItemKind::Command,
                            children: Vec::new(),
                        },
                        win_fluent_platform_win::WindowsTrayItemPlan {
                            id: "browser-uninstall".to_string(),
                            label: "Uninstall Browser Support".to_string(),
                            tooltip: Some("Uninstall Browser Support".to_string()),
                            enabled: false,
                            command_id: 1001,
                            action_kind: ActionKind::Message,
                            kind: win_fluent_platform_win::WindowsTrayItemKind::Command,
                            children: Vec::new(),
                        },
                    ],
                },
            ],
        };

        let round_trip = TraySubscriptionData::from(plan).to_plan();

        assert_eq!(
            round_trip.icon_path.as_deref(),
            Some("C:\\Easydict\\AppIcon.ico")
        );
        assert_eq!(round_trip.item_count, 2);
        assert_eq!(round_trip.presenter_min_width, Some(300));
        assert_eq!(round_trip.default_command_id, Some(1000));
        assert_eq!(
            round_trip.items[0].kind,
            win_fluent_platform_win::WindowsTrayItemKind::Separator
        );
        assert_eq!(
            round_trip.items[1].kind,
            win_fluent_platform_win::WindowsTrayItemKind::Submenu
        );
        assert_eq!(round_trip.items[1].children[0].id, "browser-install");
        assert_eq!(
            round_trip.items[1].tooltip.as_deref(),
            Some("Browser Support")
        );
        assert_eq!(round_trip.items[1].children[0].command_id, 1000);
        assert!(round_trip.items[1].children[0].enabled);
        assert_eq!(round_trip.items[1].children[1].id, "browser-uninstall");
        assert!(!round_trip.items[1].children[1].enabled);
    }

    // Render-level proof that `apply_layout_box` produces centered + capped
    // geometry for `max-w-[1040px] mx-auto` — driving the REAL production
    // function through the real iced layout engine headlessly via the `()` null
    // renderer (the framework's compiled `IcedElement` is pinned to the GPU
    // renderer, so we feed `apply_layout_box` a `()`-typed content element).
    //
    // Background (source analysis: iced_widget-0.14.2 container::layout +
    // iced_core-0.14.0 Limits::resolve): a SINGLE
    // `container(c).max_width(1040).center_x(Fill)` resolves to 1040 (Fill fills
    // the *capped* limit) and sits flush-left — it does NOT center in the
    // viewport. `apply_layout_box` therefore uses a NESTED double container.
    fn layout_token_with(
        max_width: Option<u16>,
        center_x: bool,
        margin: Edges,
    ) -> LayoutToken<Msg> {
        LayoutToken {
            id: None,
            kind: LayoutKind::Column,
            children: Vec::new(),
            padding: 0,
            padding_edges: None,
            spacing: 0,
            width: Length::Fill,
            height: Length::Shrink,
            max_width,
            max_height: None,
            center_x,
            margin,
            align: Alignment::Start,
            distribution: LayoutDistribution::Start,
            style: FluentStyle::new(),
            a11y: Default::default(),
        }
    }

    fn measure_layout_box(viewport_w: f32, token: &LayoutToken<Msg>) -> (f32, f32, f32) {
        use iced::advanced::layout::Limits;
        use iced::advanced::widget::Tree;
        use iced::widget::Space;

        let content: iced::Element<'static, Msg, iced::Theme, ()> = Space::new()
            .width(IcedLength::Fill)
            .height(IcedLength::Fixed(50.0))
            .into();
        let mut element = apply_layout_box(content, token);
        let mut tree = Tree::new(element.as_widget());
        let limits = Limits::new(Size::ZERO, Size::new(viewport_w, 900.0));
        let node = element.as_widget_mut().layout(&mut tree, &(), &limits);
        let outer_w = node.size().width;
        let child = node.children()[0].bounds();
        (outer_w, child.x, child.width)
    }

    #[test]
    fn apply_layout_box_centers_and_caps_max_width() {
        let token = layout_token_with(Some(1040), true, Edges::ZERO);

        // Wide viewport: inner capped at 1040 and horizontally centered.
        let (outer_w, inner_x, inner_w) = measure_layout_box(1400.0, &token);
        assert_eq!(outer_w, 1400.0, "outer fills viewport width");
        assert_eq!(inner_w, 1040.0, "inner capped at max-width");
        assert!(
            (inner_x - 180.0).abs() < 0.5,
            "inner centered within viewport, got x={inner_x}"
        );

        // Narrow viewport (< max-width): inner fills, flush left, no negative offset.
        let (outer_w, inner_x, inner_w) = measure_layout_box(800.0, &token);
        assert_eq!(outer_w, 800.0);
        assert_eq!(inner_w, 800.0, "inner fills when viewport < max-width");
        assert!(
            inner_x.abs() < 0.5,
            "inner flush left when uncapped, got x={inner_x}"
        );
    }

    #[test]
    fn apply_layout_box_caps_without_centering_when_no_mx_auto() {
        // max-w without mx-auto: capped at 1040 and flush-left (no centering).
        let token = layout_token_with(Some(1040), false, Edges::ZERO);
        let (_outer_w, inner_x, inner_w) = measure_layout_box(1400.0, &token);
        assert_eq!(inner_w, 1040.0, "capped at max-width");
        assert!(
            inner_x.abs() < 0.5,
            "flush-left without mx-auto, got x={inner_x}"
        );
    }

    #[test]
    fn apply_layout_box_applies_margin_as_outer_offset() {
        // m-* becomes outer container padding: content is inset by the margin.
        let token = layout_token_with(
            None,
            false,
            Edges {
                top: 8,
                right: 12,
                bottom: 8,
                left: 12,
            },
        );
        let (_outer_w, inner_x, _inner_w) = measure_layout_box(800.0, &token);
        assert!(
            (inner_x - 12.0).abs() < 0.5,
            "left margin offsets content, got x={inner_x}"
        );
    }

    #[test]
    fn selected_tile_renders_theme_selected_surface() {
        // Style-level (not token-level) check: the selected tab tile actually
        // paints the selected trio instead of falling back to a generic accent.
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let selected = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            true,
            iced::widget::button::Status::Active,
        );
        assert_eq!(
            optional_background_color(selected.background),
            iced_color(theme.selected_surface)
        );
        assert_eq!(selected.text_color, iced_color(theme.selected_foreground));
        assert_eq!(selected.border.color, iced_color(theme.selected_border));
        assert_eq!(
            selected.border.width, theme.stroke.control,
            "WinUI settings tab selected overlay uses a 1px border unless focus is visible"
        );
        assert_eq!(
            button_foreground_color(ButtonKind::Tile, visual, true),
            iced_color(theme.selected_foreground),
            "selected tile content should use the same foreground as the selected style"
        );

        let unselected = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            false,
            iced::widget::button::Status::Active,
        );
        assert_eq!(
            optional_background_color(unselected.background),
            iced_color(theme.tile_surface),
            "WinUI settings tab button background uses the unselected tile surface and must not paint the selected surface"
        );
        assert_eq!(unselected.text_color, iced_color(theme.tile_foreground));
        assert_eq!(unselected.border.color, iced_color(theme.tile_border));

        let focused_selected = button_style_with_state(
            visual,
            ButtonKind::Tile,
            true,
            true,
            iced::widget::button::Status::Active,
        );
        assert_eq!(focused_selected.border.color, iced_color(theme.focus));
        assert_eq!(focused_selected.border.width, theme.stroke.focus);
    }

    #[test]
    fn tile_interaction_states_match_winui_static_tab_template() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let unselected_active = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            false,
            iced::widget::button::Status::Active,
        );
        let unselected_hover = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            false,
            iced::widget::button::Status::Hovered,
        );
        let unselected_pressed = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            false,
            iced::widget::button::Status::Pressed,
        );
        assert_button_style_visual_eq(&unselected_active, &unselected_hover);
        assert_button_style_visual_eq(&unselected_active, &unselected_pressed);

        let selected_active = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            true,
            iced::widget::button::Status::Active,
        );
        let selected_hover = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            true,
            iced::widget::button::Status::Hovered,
        );
        let selected_pressed = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            true,
            iced::widget::button::Status::Pressed,
        );
        assert_button_style_visual_eq(&selected_active, &selected_hover);
        assert_button_style_visual_eq(&selected_active, &selected_pressed);
    }

    #[test]
    fn checkbox_label_italic_maps_to_iced_font_style() {
        let normal = checkbox_label_font("OpenAI", false);
        assert_eq!(normal.style, font::Style::Normal);

        let italic = checkbox_label_font("OpenAI", true);
        assert_eq!(italic.style, font::Style::Italic);
        match italic.family {
            font::Family::Name(name) => assert_eq!(name, "Segoe UI"),
            family => panic!("unexpected italic latin checkbox family: {family:?}"),
        }

        let cjk_italic = checkbox_label_font("Zhipu (\u{667a}\u{8c31})", true);
        assert_eq!(cjk_italic.style, font::Style::Italic);
        match cjk_italic.family {
            font::Family::Name(name) => assert_eq!(name, "Microsoft YaHei UI"),
            family => panic!("unexpected italic cjk checkbox family: {family:?}"),
        }
    }

    #[test]
    fn overlay_scrim_dims_with_requested_opacity() {
        // The modal/loading scrim uses the requested opacity over surface_alt.
        let visual = IcedVisualTheme::from_tokens(&ThemeTokens::fluent_light());
        let scrim = busy_overlay_style(visual, 0.4);
        let background = background_color(scrim.background.expect("scrim has a background"));
        assert!(
            (background.a - 0.4).abs() < 0.001,
            "scrim alpha should match requested opacity, got {}",
            background.a
        );
    }

    #[test]
    fn progress_ring_animation_advances_frames_over_time() {
        let start = iced::time::Instant::now();

        assert_eq!(progress_ring_frame_index(start, start), 0);
        assert_eq!(
            progress_ring_frame_index(start, start + Duration::from_millis(100)),
            1
        );
        assert_eq!(
            progress_ring_frame_index(start, start + Duration::from_millis(800)),
            0
        );
        assert_ne!(
            progress_ring_segment_alpha(0, 0),
            progress_ring_segment_alpha(0, 1),
            "the highlighted segment must move between frames"
        );
    }

    #[test]
    fn busy_overlay_fade_progress_moves_between_hidden_and_visible() {
        let fade_in_mid = busy_overlay_fade_progress(0.0, 1.0, 90.0, 180);
        let fade_out_mid = busy_overlay_fade_progress(1.0, 0.0, 90.0, 180);

        assert!(fade_in_mid > 0.0 && fade_in_mid < 1.0);
        assert!(fade_out_mid > 0.0 && fade_out_mid < 1.0);
        assert!(fade_in_mid > busy_overlay_fade_progress(0.0, 1.0, 45.0, 180));
        assert!(fade_out_mid < busy_overlay_fade_progress(1.0, 0.0, 45.0, 180));
        assert_eq!(busy_overlay_fade_progress(0.0, 1.0, 180.0, 180), 1.0);
        assert_eq!(busy_overlay_fade_progress(1.0, 0.0, 180.0, 180), 0.0);
    }

    #[test]
    fn busy_overlay_state_animates_in_and_out_with_redraws() {
        let start = iced::time::Instant::now();
        let mut state = AnimatedBusyOverlayState::new(false);

        assert_eq!(state.progress, 0.0);
        assert!(!state.is_visible_or_targeting_visible());

        state.set_target(true, 180);
        assert!(state.is_visible_or_targeting_visible());
        assert_eq!(state.tick(start, 180), (false, true));
        let (changed, animating) = state.tick(start + Duration::from_millis(90), 180);
        assert!(changed);
        assert!(animating);
        assert!(state.progress > 0.0 && state.progress < 1.0);
        assert_eq!(
            state.tick(start + Duration::from_millis(180), 180),
            (true, false)
        );
        assert_eq!(state.progress, 1.0);

        state.set_target(false, 180);
        assert!(state.is_visible_or_targeting_visible());
        assert_eq!(
            state.tick(start + Duration::from_millis(270), 180),
            (false, true)
        );
        assert_eq!(state.progress, 1.0);
        assert_eq!(
            state.tick(start + Duration::from_millis(360), 180),
            (true, true)
        );
        assert!(state.progress > 0.0 && state.progress < 1.0);
        assert_eq!(
            state.tick(start + Duration::from_millis(450), 180),
            (true, false)
        );
        assert_eq!(state.progress, 0.0);
        assert!(!state.is_visible_or_targeting_visible());
    }

    #[test]
    fn active_progress_ring_state_keeps_requesting_redraws() {
        let start = iced::time::Instant::now();
        let mut state = AnimatedProgressRingState::new();

        assert_eq!(state.tick(true, start), (false, true));
        assert_eq!(
            state.tick(true, start + Duration::from_millis(100)),
            (true, true)
        );
        assert_eq!(state.frame_index, 1);
        assert_eq!(
            state.tick(false, start + Duration::from_millis(200)),
            (true, false)
        );
        assert_eq!(state.frame_index, 0);
    }

    #[test]
    fn hover_reveal_actions_state_tracks_runtime_hover_and_preview_override() {
        let start = iced::time::Instant::now();
        let mut state = HoverRevealActionsState::new(false);

        assert!(!state.drawn(false));
        assert!(!state.interactive(false));
        assert!(
            HoverRevealActionsState::new(true).interactive(true),
            "preview-forced actions start fully visible"
        );
        assert!(state.set_hovered(true));
        let target_visible = state.target_visible(false);
        state.set_target(target_visible, HOVER_REVEAL_TRANSITION_MS);
        assert!(state.drawn(false));
        assert!(state.interactive(false));
        assert!(!state.tick(
            start + Duration::from_millis(u64::from(HOVER_REVEAL_TRANSITION_MS)),
            HOVER_REVEAL_TRANSITION_MS
        ));
        assert_eq!(state.progress, 1.0);
        assert!(state.interactive(false));

        assert!(!state.set_hovered(true));
        assert!(state.set_hovered(false));
        let target_visible = state.target_visible(false);
        state.set_target(target_visible, HOVER_REVEAL_TRANSITION_MS);
        assert!(!state.drawn(false));
        assert!(!state.interactive(false));
    }

    #[test]
    fn hover_reveal_motion_matches_instant_winui_visibility_toggle() {
        let mid = hover_reveal_progress(0.0, 1.0, 0.0, HOVER_REVEAL_TRANSITION_MS);

        assert_eq!(mid, 1.0);
        assert_eq!(
            hover_reveal_progress(
                0.0,
                1.0,
                f32::from(HOVER_REVEAL_TRANSITION_MS),
                HOVER_REVEAL_TRANSITION_MS
            ),
            1.0
        );
        assert_eq!(hover_reveal_slide_offset(0.0), 0.0);
        assert_eq!(hover_reveal_slide_offset(mid), 0.0);
        assert_eq!(hover_reveal_slide_offset(1.0), 0.0);
    }

    #[test]
    fn shadow_is_suppressed_in_flat_themes() {
        let style = FluentStyle::from_classes("shadow-lg");

        let light = IcedVisualTheme::from_tokens(&ThemeTokens::fluent_light());
        assert!(
            utility_shadow(&style, light).is_some(),
            "light theme keeps elevation shadow"
        );

        for flat in [ThemeTokens::minimal(), ThemeTokens::high_contrast()] {
            let visual = IcedVisualTheme::from_tokens(&flat);
            assert!(
                utility_shadow(&style, visual).is_none(),
                "{:?} theme suppresses shadow",
                flat.mode
            );
        }
    }

    #[test]
    fn flow_layout_wraps_on_width_and_column_cap() {
        let row_count = |positions: &[Point]| {
            let mut ys: Vec<f32> = positions.iter().map(|p| p.y).collect();
            ys.dedup();
            ys.len()
        };
        // 7 tabs, 86 DIP tile, 10 DIP gap, cap 7.
        let tiles = vec![Size::new(86.0, 76.0); 7];
        // Wide window → single row (matches .NET wide-screen tab bar).
        let (wide, _) = flow_positions(&tiles, 1700.0, 10.0, 10.0, 7);
        assert_eq!(row_count(&wide), 1);
        // Exactly enough for 7 (7*86 + 6*10 = 662) → still one row.
        let (exact, _) = flow_positions(&tiles, 662.0, 10.0, 10.0, 7);
        assert_eq!(row_count(&exact), 1);
        // Narrow window fits 4 per row (4*86 + 3*10 = 374 <= 400) → 2 rows (4 + 3).
        let (narrow, _) = flow_positions(&tiles, 400.0, 10.0, 10.0, 7);
        assert_eq!(row_count(&narrow), 2);
        assert_eq!(narrow[4].y, narrow[0].y + 76.0 + 10.0);
        // Very narrow → one tile per row (7 rows), never zero columns.
        let (tiny, _) = flow_positions(&tiles, 50.0, 10.0, 10.0, 7);
        assert_eq!(row_count(&tiny), 7);
        // Column cap forces wrapping even when more would fit by width.
        let chips = vec![Size::new(180.0, 32.0); 8];
        let (capped, _) = flow_positions(&chips, 4000.0, 8.0, 4.0, 4);
        assert_eq!(row_count(&capped), 2); // 8 items, cap 4 → 2 rows
    }

    fn assert_button_style_visual_eq(
        left: &iced::widget::button::Style,
        right: &iced::widget::button::Style,
    ) {
        assert_eq!(
            optional_background_color(left.background),
            optional_background_color(right.background)
        );
        assert_eq!(left.text_color, right.text_color);
        assert_eq!(left.border.color, right.border.color);
        assert_eq!(left.border.width, right.border.width);
        assert_eq!(left.border.radius.top_left, right.border.radius.top_left);
        assert_eq!(left.border.radius.top_right, right.border.radius.top_right);
        assert_eq!(
            left.border.radius.bottom_right,
            right.border.radius.bottom_right
        );
        assert_eq!(
            left.border.radius.bottom_left,
            right.border.radius.bottom_left
        );
        assert_eq!(left.shadow, right.shadow);
    }

    fn optional_background_color(background: Option<Background>) -> iced::Color {
        match background {
            Some(Background::Color(color)) => color,
            other => panic!("expected solid color background, got {other:?}"),
        }
    }

    fn background_color(background: Background) -> iced::Color {
        match background {
            Background::Color(color) => color,
            other => panic!("expected solid color background, got {other:?}"),
        }
    }
}
