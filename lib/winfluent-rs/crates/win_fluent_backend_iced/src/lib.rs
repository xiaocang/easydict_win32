use std::collections::HashMap;
use std::env;
use std::fmt;
use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use iced::advanced::{
    clipboard::Clipboard,
    layout, mouse, overlay, renderer,
    widget::{self, Operation, Tree},
    Layout, Shell, Widget,
};
use iced::widget::text_editor as iced_text_editor_state;
use iced::widget::{
    button as iced_button, column as iced_column, container as iced_container,
    opaque as iced_opaque, pick_list as iced_pick_list, responsive as iced_responsive,
    row as iced_row, scrollable as iced_scrollable, slider as iced_slider, space as iced_space,
    stack as iced_stack, text as iced_text, text_editor as iced_text_editor,
    text_input as iced_text_input, toggler as iced_toggler,
};
use iced::{
    alignment, font, keyboard, window, Background, Border, Color, Element, Event, Font,
    Length as IcedLength, Padding as IcedPadding, Point, Rectangle, Shadow, Size, Subscription,
    Vector,
};
use win_fluent::action::{Action, ActionKind};
use win_fluent::command::CommandToken;
use win_fluent::icon;
use win_fluent::platform::{
    FileDialogFilter, FileDialogOptions, Hotkey, HotkeyKey, HotkeyModifier, PlatformCommand,
    ProtocolRegistration, ShellVerb,
};
use win_fluent::runtime::{Application as FluentApplication, DesktopIntegrationPlan, RuntimePlan};
use win_fluent::screenshot::WindowScreenshot;
use win_fluent::state::ValidationSeverity;
use win_fluent::style::FluentStyle;
use win_fluent::subscription::{
    PlatformEvent, Subscription as FluentSubscription, SubscriptionKind as FluentSubscriptionKind,
};
use win_fluent::task::Task as FluentTask;
use win_fluent::theme::{Color as FluentColor, ThemeMode, ThemeTokens};
use win_fluent::view::{
    AdaptiveSwitchToken, BusyOverlayToken, ButtonKind, CardKind, CardToken, CollapseTransition,
    ComboBoxItem, ExpanderToken, FlyoutButtonToken, LayoutDistribution, LayoutKind, LayoutToken,
    Length, OverlayToken, PointerPosition, PointerRegionAction, PointerRegionToken, PointerWheel,
    ProgressRingToken, ResultCardToken, ResultItem, ResultListToken, ResultStatus,
    SettingsRowToken, SliderToken, StatusBadgeToken, TextEditorChrome, TextEditorKey,
    TextEditorKeyBinding, TextEditorKeyModifiers, TextEditorToken, TextStyle, TitleBarToken, View,
    ViewToken, WrapToken,
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
    let window_settings = window_settings(&options);
    let boot_options = options.clone();

    iced::application(
        move || IcedSingleWindowRuntime::<App>::boot(flags.clone(), boot_options.clone()),
        IcedSingleWindowRuntime::<App>::update,
        IcedSingleWindowRuntime::<App>::view,
    )
    .title(|state: &IcedSingleWindowRuntime<App>| state.app.title(&state.logical_window_id))
    .window(window_settings)
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
}

struct IcedSingleWindowRuntime<App: FluentApplication> {
    app: App,
    logical_window_id: WindowId,
    native_window_id: Option<window::Id>,
    view: View<App::Message>,
    text_editors: TextEditorCache,
    desktop_integration: DesktopIntegrationPlan<App::Message>,
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
        let runtime = Self::new(plan.app, options.id, plan.desktop_integration);
        let initial_task = runtime.fluent_task(plan.initial_task);
        let focus_task = runtime.delayed_focused_text_editor_task();

        (runtime, iced::Task::batch([initial_task, focus_task]))
    }

    fn new(
        app: App,
        logical_window_id: WindowId,
        desktop_integration: DesktopIntegrationPlan<App::Message>,
    ) -> Self {
        let view = app.view(&logical_window_id);
        let mut text_editors = TextEditorCache::default();
        text_editors.sync(&view);

        Self {
            app,
            logical_window_id,
            native_window_id: None,
            view,
            text_editors,
            desktop_integration,
        }
    }

    fn rebuild_view(&mut self) {
        self.view = self.app.view(&self.logical_window_id);
        self.text_editors.sync(&self.view);
    }

    fn update(
        state: &mut Self,
        message: IcedRuntimeMessage<App::Message>,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        match message {
            IcedRuntimeMessage::App(message) => {
                let task = state.app.update(message);
                state.rebuild_view();
                iced::Task::batch([state.fluent_task(task), state.focused_text_editor_task()])
            }
            IcedRuntimeMessage::PlatformEvent(event) => {
                let Some(message) = map_platform_event(&state.app.subscription(), event) else {
                    return iced::Task::none();
                };

                let task = state.app.update(message);
                state.rebuild_view();
                iced::Task::batch([state.fluent_task(task), state.focused_text_editor_task()])
            }
            IcedRuntimeMessage::FocusWidget(id) => iced::widget::operation::focus(id),
            IcedRuntimeMessage::WindowOpened(window_id) => {
                state.native_window_id = Some(window_id);
                state.delayed_focused_text_editor_task()
            }
        }
    }

    fn view(state: &Self) -> IcedElement<'_, IcedRuntimeMessage<App::Message>> {
        let theme = state.app.theme_tokens();
        IcedAdapter::compile_view_with_text_editors_and_theme(
            &state.view,
            |id| state.text_editors.get(id),
            &theme,
        )
        .map(IcedRuntimeMessage::App)
    }

    fn subscription(state: &Self) -> Subscription<IcedRuntimeMessage<App::Message>> {
        let _desktop_entry_count = state.desktop_integration.entry_count();
        Subscription::batch([
            window::open_events().map(IcedRuntimeMessage::WindowOpened),
            fluent_subscription(state.app.subscription()),
        ])
    }

    fn fluent_task(
        &self,
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
            FluentTask::ScrollToTop(id) => iced::widget::operation::snap_to(
                iced::advanced::widget::Id::from(id),
                iced::widget::scrollable::RelativeOffset::START,
            ),
            FluentTask::ReadClipboardText(map) => {
                iced::clipboard::read().map(move |text| IcedRuntimeMessage::App(map(text)))
            }
            FluentTask::CaptureScreenRegion { request, map } => {
                iced::Task::future(async move { run_platform_capture_screen_region(request) })
                    .map(move |capture| IcedRuntimeMessage::App(map(capture)))
            }
            FluentTask::OpenFileDialog { options, map } => {
                iced::Task::future(async move { run_platform_open_file_dialog(options) })
                    .map(move |path| IcedRuntimeMessage::App(map(path)))
            }
        }
    }

    fn focused_text_editor_task(&self) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        focused_text_editor_id(&self.view)
            .map(IcedRuntimeMessage::FocusWidget)
            .map(iced::Task::done)
            .unwrap_or_else(iced::Task::none)
    }

    fn delayed_focused_text_editor_task(&self) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        focused_text_editor_id(&self.view)
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
        &self,
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
            WindowCommand::Close(id) => self
                .with_logical_window(&id, window::close)
                .unwrap_or_else(iced::Task::none),
            WindowCommand::Show(id) => self
                .with_logical_window(&id, |window_id| {
                    window::set_mode::<IcedRuntimeMessage<App::Message>>(
                        window_id,
                        window::Mode::Windowed,
                    )
                })
                .unwrap_or_else(iced::Task::none),
            WindowCommand::Hide(id) => self
                .with_logical_window(&id, |window_id| {
                    window::set_mode::<IcedRuntimeMessage<App::Message>>(
                        window_id,
                        window::Mode::Hidden,
                    )
                })
                .unwrap_or_else(iced::Task::none),
            WindowCommand::ToggleVisibility(id) => self
                .with_logical_window(&id, |window_id| {
                    window::mode(window_id).then(move |mode| {
                        let next_mode = if mode == window::Mode::Hidden {
                            window::Mode::Windowed
                        } else {
                            window::Mode::Hidden
                        };

                        window::set_mode::<IcedRuntimeMessage<App::Message>>(window_id, next_mode)
                    })
                })
                .unwrap_or_else(iced::Task::none),
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
            WindowCommand::Open { .. }
            | WindowCommand::ReplaceView { .. }
            | WindowCommand::SetTitle { .. } => iced::Task::none(),
        }
    }

    fn with_current_window(
        &self,
        command: impl Fn(window::Id) -> iced::Task<IcedRuntimeMessage<App::Message>> + Send + 'static,
    ) -> iced::Task<IcedRuntimeMessage<App::Message>> {
        if let Some(window_id) = self.native_window_id {
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
        (id == &self.logical_window_id).then(|| self.with_current_window(command))
    }
}

fn fluent_subscription<Message>(
    subscription: FluentSubscription<Message>,
) -> Subscription<IcedRuntimeMessage<Message>>
where
    Message: Clone + Send + 'static,
{
    match subscription {
        FluentSubscription::None => Subscription::none(),
        FluentSubscription::Batch(subscriptions) => Subscription::batch(
            subscriptions
                .into_iter()
                .map(fluent_subscription::<Message>),
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
            FluentSubscriptionKind::Clipboard
            | FluentSubscriptionKind::Theme
            | FluentSubscriptionKind::Tray
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
    #[cfg(windows)]
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

    #[cfg(not(windows))]
    {
        let _ = options;
        None
    }
}

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
    #[cfg(windows)]
    {
        win_fluent_platform_win::WindowsPlatformAdapter::open_url(&url)
            .map_err(|error| format!("{error:?}"))
    }

    #[cfg(not(windows))]
    {
        let _ = url;
        Ok(())
    }
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
    let executable = env::current_exe()
        .map_err(|error| error.to_string())?
        .parent()
        .ok_or_else(|| "current executable has no parent directory".to_string())?
        .join(executable_name);

    let mut command = Command::new(&executable);
    command.args(arguments);
    hide_process_window(&mut command);

    let status = command.status().map_err(|error| {
        format!(
            "failed to run bundled executable {}: {error}",
            executable.display()
        )
    })?;
    if !status.success() {
        return Err(format!(
            "bundled executable {} exited with {status}",
            executable.display()
        ));
    }

    Ok(())
}

fn hide_process_window(command: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }

    #[cfg(not(windows))]
    {
        let _ = command;
    }
}

#[cfg(windows)]
fn current_executable_path_string() -> Result<String, String> {
    env::current_exe()
        .map_err(|error| error.to_string())
        .map(|path| path.display().to_string())
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
        ViewToken::Custom(token) => {
            for child in &token.children {
                collect_text_editor_values(child, values);
            }
        }
        ViewToken::Text(_)
        | ViewToken::Button(_)
        | ViewToken::FlyoutButton(_)
        | ViewToken::StatusBadge(_)
        | ViewToken::ProgressRing(_)
        | ViewToken::Spacer(_)
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
            WindowPlacement::CursorOffset { .. } | WindowPlacement::TopRight { .. } => {
                iced::window::Position::Default
            }
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
        | ViewToken::ProgressRing(_)
        | ViewToken::Spacer(_)
        | ViewToken::Text(_)
        | ViewToken::ToggleSwitch(_)
        | ViewToken::Slider(_)
        | ViewToken::ComboBox(_)
        | ViewToken::ResultCard(_)
        | ViewToken::ResultList(_)
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
        ViewToken::Text(token) => compile_text(&token.value, token.style, visual),
        ViewToken::Button(token) => {
            let kind = token.kind;
            let mut control = iced_button(button_content(
                &token.label,
                kind,
                token.icon.as_ref(),
                visual,
            ))
            .style(move |_, status| {
                button_style_with_state(
                    visual,
                    kind,
                    token.state.focused,
                    token.state.selected,
                    status,
                )
            });

            control = match kind {
                ButtonKind::Icon => control
                    .width(IcedLength::Fixed(visual.icon_button_size))
                    .height(IcedLength::Fixed(visual.icon_button_size))
                    .padding(0),
                ButtonKind::ResultAction => control
                    .width(IcedLength::Fixed(visual.result_action_button_size))
                    .height(IcedLength::Fixed(visual.result_action_button_size))
                    .padding(0),
                ButtonKind::FloatingAction => control
                    .width(IcedLength::Fixed(visual.floating_action_button_size))
                    .height(IcedLength::Fixed(visual.floating_action_button_size))
                    .padding(0),
                ButtonKind::Primary if token.icon.is_some() && token.label.trim().is_empty() => {
                    control
                        .width(IcedLength::Fixed(visual.primary_icon_button_size()))
                        .height(IcedLength::Fixed(visual.primary_icon_button_size()))
                        .padding(0)
                }
                ButtonKind::Primary => control.padding([8, 14]),
                ButtonKind::Chip => control.padding([7, 12]),
                ButtonKind::Tile => control
                    .width(IcedLength::Fixed(86.0))
                    .height(IcedLength::Fixed(76.0))
                    .padding([8, 10]),
                ButtonKind::Subtle => control.padding([6, 10]),
                ButtonKind::Link => control.padding([2, 0]),
                ButtonKind::Standard => control.padding([6, 12]),
            };

            if token.state.enabled {
                if let Some(message) = token.action.press() {
                    control = control.on_press(message);
                }
            }

            control.into()
        }
        ViewToken::FlyoutButton(token) => compile_flyout_button(token, visual),
        ViewToken::StatusBadge(token) => compile_status_badge(token, visual),
        ViewToken::ProgressRing(token) => compile_progress_ring(token, visual),
        ViewToken::BusyOverlay(token) => compile_busy_overlay(token, provider, visual),
        ViewToken::Card(token) => compile_card(token, provider, visual),
        ViewToken::Spacer(token) => iced_space()
            .width(iced_length(token.width))
            .height(iced_length(token.height))
            .into(),
        ViewToken::TextEditor(token) => compile_text_editor(token, provider, visual),
        ViewToken::ToggleSwitch(token) => {
            let mut control = iced_toggler(token.checked)
                .label(toggle_switch_label(&token.label, token.checked))
                .size(20)
                .spacing(8)
                .text_size(visual.body_size)
                .style(move |_, status| toggle_switch_style(visual, status));

            if token.state.enabled && token.action.kind() == ActionKind::BoolInput {
                let action = token.action.clone();
                control = control.on_toggle(move |value| {
                    action
                        .input_bool(value)
                        .expect("toggle action must produce a message")
                });
            }

            control.into()
        }
        ViewToken::Slider(token) => compile_slider(token, visual),
        ViewToken::ComboBox(token) => compile_combo_box(
            &token.items,
            token.selected.as_deref(),
            token.label.as_deref(),
            token.width,
            &token.action,
            token.state.enabled,
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
            .padding(16)
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

            iced_container(content).into()
        }
        ViewToken::Layout(token) => {
            let children = token
                .children
                .iter()
                .map(|child| compile_view_with_text_editors_and_visual(child, provider, visual))
                .collect::<Vec<_>>();
            let children = distribute_children(children, token.kind, token.distribution);
            let content: IcedElement<'a, Message> = match token.kind {
                LayoutKind::Column => iced_column(children)
                    .padding(token.padding)
                    .spacing(u32::from(token.spacing))
                    .width(iced_length(token.width))
                    .height(iced_length(token.height))
                    .align_x(horizontal_alignment(token.align))
                    .into(),
                LayoutKind::Row => iced_row(children)
                    .padding(token.padding)
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
                .height(IcedLength::Fill);
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

fn compile_text<'a, Message>(
    value: &str,
    style: TextStyle,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    iced_text(value.to_string())
        .font(text_font(style))
        .size(text_size(style, visual))
        .color(text_color(style, visual))
        .into()
}

fn compile_slider<'a, Message>(
    token: &'a SliderToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    if !token.state.enabled || token.action.kind() != ActionKind::NumberInput {
        return iced_container(iced_text(format!("{:.1}x", token.value)))
            .width(iced_length(token.width))
            .into();
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
    .style(move |_, status| slider_style(visual, status))
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
        title_bits.push(icon_element(icon, 16.0, visual.text_primary));
    }

    title_bits.push(
        iced_text(token.title.clone())
            .font(text_font(TextStyle::BodyStrong))
            .size(text_size(TextStyle::Body, visual))
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

    let row = iced_row(vec![
        left.into(),
        iced_space().width(IcedLength::Fill).into(),
        right_controls.into(),
    ])
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
        children.push(
            iced_text("●")
                .font(text_font(TextStyle::Caption))
                .size(8.0)
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
            .spacing(8)
            .align_y(alignment::Vertical::Center),
    )
    .height(IcedLength::Fixed(visual.control_height))
    .padding([0, 10])
    .align_y(alignment::Vertical::Center)
    .style(move |_| status_badge_container_style(visual, severity))
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

        return iced_pick_list(choices, Option::<ComboChoice>::None, move |choice| {
            action
                .input_text(choice.id)
                .expect("flyout selection action must produce a message")
        })
        .placeholder(token.label.clone())
        .width(trigger_width)
        .padding([2, 4])
        .text_size(text_size(TextStyle::Body, visual))
        .style(move |_, status| flyout_pick_list_style(visual, status))
        .menu_style(move |_| menu_style(visual))
        .into();
    }

    let kind = ButtonKind::Subtle;
    iced_button(button_content(
        &token.label,
        kind,
        token.icon.as_ref(),
        visual,
    ))
    .padding([4, 8])
    .style(move |_, status| button_style(visual, kind, status))
    .into()
}

fn compile_progress_ring<'a, Message>(
    token: &ProgressRingToken,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let label = token
        .label
        .as_deref()
        .unwrap_or(if token.active { "◌" } else { "" });

    iced_text(label.to_string())
        .font(text_font(TextStyle::Caption))
        .size(token.size as f32)
        .color(if token.active {
            visual.accent
        } else {
            visual.text_secondary
        })
        .into()
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
    if !token.active {
        return content;
    }

    // The busy overlay is a specialized centered, scrimmed layer built on the
    // same stack-layer mechanism as the general `overlay` primitive.
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

    let overlay = overlay_layer_element(
        indicator,
        alignment::Horizontal::Center,
        alignment::Vertical::Center,
        Some(token.opacity),
        token.blocks_input,
        visual,
    );

    iced_stack(vec![content, overlay]).into()
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
    // Chunk children into rows of at most `max_columns`, matching WinUI
    // ItemsWrapGrid's column cap. (Width-responsive narrow reflow can later be
    // layered in here via `iced_responsive` without changing the token API.)
    let compiled = token
        .children
        .iter()
        .map(|child| compile_view_with_text_editors_and_visual(child, provider, visual))
        .collect::<Vec<_>>();

    let rows = chunk_for_wrap(compiled, usize::from(token.max_columns.max(1)))
        .into_iter()
        .map(|row| iced_row(row).spacing(u32::from(token.spacing)).into())
        .collect::<Vec<IcedElement<'a, Message>>>();

    iced_column(rows)
        .spacing(u32::from(token.run_spacing))
        .width(IcedLength::Fill)
        .into()
}

/// Splits `items` into consecutive chunks of at most `max_columns` (>= 1),
/// the row-wrapping rule used by [`compile_wrap`].
fn chunk_for_wrap<T>(items: Vec<T>, max_columns: usize) -> Vec<Vec<T>> {
    let max = max_columns.max(1);
    let mut rows: Vec<Vec<T>> = Vec::new();
    let mut current: Vec<T> = Vec::with_capacity(max);
    for item in items {
        current.push(item);
        if current.len() == max {
            rows.push(std::mem::take(&mut current));
        }
    }
    if !current.is_empty() {
        rows.push(current);
    }
    rows
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

    if !token.margin.is_zero() {
        let margin = token.margin;
        element = iced_container(element)
            .padding(IcedPadding {
                top: f32::from(margin.top),
                right: f32::from(margin.right),
                bottom: f32::from(margin.bottom),
                left: f32::from(margin.left),
            })
            .into();
    }

    element
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
    let title = label_with_icon(&token.title, token.icon.as_ref(), visual);
    let mut text_column =
        iced_column(vec![compile_text(&title, TextStyle::BodyStrong, visual)]).spacing(4);

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

    let mut layout = iced_column(vec![header.into()])
        .padding(visual.card_padding)
        .spacing(12)
        .width(IcedLength::Fill);

    if let Some(content) = &token.content {
        layout = layout.push(compile_view_with_text_editors_and_visual(
            content, provider, visual,
        ));
    }

    iced_container(layout)
        .width(IcedLength::Fill)
        .style({
            let kind = token.kind;
            move |_| card_container_style(visual, kind)
        })
        .into()
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

    if let Some(content) = token.id.as_deref().and_then(provider) {
        let mut control = iced_text_editor(content)
            .placeholder(placeholder.to_string())
            .font(text_font(token.text_style))
            .size(text_size(token.text_style, visual))
            .style({
                let chrome = token.chrome;
                move |_, status| text_editor_style(visual, status, chrome)
            });

        if let Some(id) = &token.id {
            control = control.id(id.clone());
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

    let mut control = iced_text_input(placeholder, &token.text)
        .font(text_font(token.text_style))
        .size(text_size(token.text_style, visual))
        .style({
            let chrome = token.chrome;
            move |_, status| text_input_style(visual, status, chrome)
        });

    if let Some(id) = &token.id {
        control = control.id(id.clone());
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
    width: Length,
    action: &Action<Message>,
    enabled: bool,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    if !enabled || !matches!(action.kind(), ActionKind::SelectionInput) {
        return compile_text(
            selected
                .and_then(|id| items.iter().find(|item| item.id == id))
                .map(|item| item.label.as_str())
                .or(label)
                .unwrap_or_default(),
            TextStyle::Body,
            visual,
        );
    }

    let choices = items
        .iter()
        .map(|item| ComboChoice {
            id: item.id.clone(),
            label: item.label.clone(),
        })
        .collect::<Vec<_>>();
    let selected = selected.and_then(|id| choices.iter().find(|item| item.id == id).cloned());
    let action = action.clone();

    iced_pick_list(choices, selected, move |choice| {
        action
            .input_text(choice.id)
            .expect("selection action must produce a message")
    })
    .placeholder(label.unwrap_or("Select"))
    .width(iced_length(width))
    .padding([8, 12])
    .text_size(text_size(TextStyle::Body, visual))
    .style(move |_, status| pick_list_style(visual, status))
    .menu_style(move |_| menu_style(visual))
    .into()
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
    let title = label_with_icon(&token.title, token.icon.as_ref(), visual);
    let mut text_column =
        iced_column(vec![compile_text(&title, TextStyle::Subtitle, visual)]).spacing(6);

    if let Some(description) = &token.description {
        text_column = text_column.push(compile_text(description, TextStyle::Caption, visual));
    }

    let mut trailing = iced_row(Vec::new()).spacing(8);
    for child in &token.trailing {
        trailing = trailing.push(compile_view_with_text_editors_and_visual(
            child, provider, visual,
        ));
    }

    if token.action.kind() == ActionKind::BoolInput {
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
        let expand_button = iced_button(button_content("", ButtonKind::Icon, Some(&icon), visual))
            .width(IcedLength::Fixed(32.0))
            .height(IcedLength::Fixed(32.0))
            .padding(0)
            .style(move |_, status| button_style(visual, ButtonKind::Icon, status))
            .on_press(
                action
                    .input_bool(next_expanded)
                    .expect("expander action must produce a message"),
            );
        trailing = trailing.push(expand_button);
    }

    let has_header_controls =
        !token.trailing.is_empty() || token.action.kind() == ActionKind::BoolInput;
    let header = if has_header_controls {
        iced_row(vec![
            text_column.width(IcedLength::Fill).into(),
            trailing.into(),
        ])
        .spacing(12)
        .width(IcedLength::Fill)
        .align_y(alignment::Vertical::Center)
    } else {
        iced_row(vec![text_column.width(IcedLength::Fill).into()])
            .spacing(12)
            .width(IcedLength::Fill)
            .align_y(alignment::Vertical::Center)
    };

    let mut layout = iced_column(vec![header.into()])
        .padding(24)
        .spacing(12)
        .width(IcedLength::Fill);

    if token.expanded {
        if let Some(content) = &token.content {
            layout = layout.push(compile_view_with_text_editors_and_visual(
                content, provider, visual,
            ));
        }
    }

    iced_container(layout)
        .width(IcedLength::Fill)
        .style(move |_| settings_row_container_style(visual))
        .into()
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
    let mut text_column =
        iced_column(vec![compile_text(&title, TextStyle::Subtitle, visual)]).spacing(6);

    if let Some(description) = &token.description {
        text_column = text_column.push(compile_text(description, TextStyle::Caption, visual));
    }

    let mut trailing = iced_row(Vec::new()).spacing(8);
    for child in &token.trailing {
        trailing = trailing.push(compile_view_with_text_editors_and_visual(
            child, provider, visual,
        ));
    }

    let has_header_controls = !token.trailing.is_empty();
    let header = if has_header_controls {
        iced_row(vec![
            text_column.width(IcedLength::Fill).into(),
            trailing.into(),
        ])
        .spacing(12)
        .width(IcedLength::Fill)
        .align_y(alignment::Vertical::Center)
    } else {
        iced_row(vec![text_column.width(IcedLength::Fill).into()])
            .spacing(12)
            .width(IcedLength::Fill)
            .align_y(alignment::Vertical::Center)
    };

    let mut layout = iced_column(vec![header.into()])
        .padding(24)
        .spacing(12)
        .width(IcedLength::Fill);

    if let Some(content) = &token.content {
        layout = layout.push(compile_view_with_text_editors_and_visual(
            content, provider, visual,
        ));
    }

    iced_container(layout)
        .width(IcedLength::Fill)
        .style(move |_| settings_row_container_style(visual))
        .into()
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

    list.width(IcedLength::Fill).into()
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
            header_left_children.push(
                iced_container(icon_element(icon, 16.0, primary_color))
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

    if let Some(metadata) = &item.metadata {
        header_right_children.push(
            iced_text(metadata.clone())
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
        }
        ResultStatus::Ready => {}
    }

    if item.toggleable {
        header_right_children.push(
            iced_text(if item.expanded {
                "\u{E70D}"
            } else {
                "\u{E76C}"
            })
            .font(caption_icon_font())
            .size(12.0)
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

    let mut header = iced_button(header_content)
        .height(IcedLength::Fixed(visual.result_header_height))
        .padding([0, 8])
        .width(IcedLength::Fill)
        .style(move |_, status| result_header_button_style(visual, status));

    if item.toggleable && matches!(toggle_action.kind(), ActionKind::SelectionInput) {
        if let Some(message) = toggle_action.input_text(item.id.clone()) {
            header = header.on_press(message);
        }
    }

    let mut content = iced_column(vec![header.into()])
        .width(IcedLength::Fill)
        .clip(true);

    let body_text = if item.body.trim().is_empty() {
        item.pending_hint.as_deref().unwrap_or_default()
    } else {
        item.body.as_str()
    };

    if !body_text.trim().is_empty() {
        let body = iced_container(
            iced_text(body_text.to_string())
                .font(text_font(TextStyle::BodyLarge))
                .size(text_size(TextStyle::BodyLarge, visual))
                .color(if item.status == ResultStatus::Error {
                    visual.error
                } else {
                    primary_color
                })
                .width(IcedLength::Fill),
        )
        .padding([8, 10])
        .width(IcedLength::Fill);

        content = content.push(animated_collapse(
            item.id.clone(),
            body,
            item.expanded,
            collapse_transition,
        ));
    }

    if item.expanded {
        let actions = result_action_buttons(
            &item.id,
            item.status,
            copy_action,
            speak_action,
            replace_action,
            retry_action,
            visual,
        );
        if !actions.is_empty() {
            content = content.push(
                iced_container(
                    iced_row(actions)
                        .spacing(4)
                        .align_y(alignment::Vertical::Center),
                )
                .padding([8, 8])
                .width(IcedLength::Fill),
            );
        }
    }

    content
}

fn result_action_buttons<'a, Message>(
    item_id: &str,
    status: ResultStatus,
    copy_action: &Action<Message>,
    speak_action: &Action<Message>,
    replace_action: &Action<Message>,
    retry_action: &Action<Message>,
    visual: IcedVisualTheme,
) -> Vec<IcedElement<'a, Message>>
where
    Message: Clone + Send + 'static,
{
    let mut actions = Vec::new();

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
    if status == ResultStatus::Error {
        push_result_action(
            &mut actions,
            item_id,
            "Retry",
            win_fluent::IconToken::with_glyph("retry", '\u{E72C}'),
            retry_action,
            visual,
        );
    }

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
        visual,
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
        visual,
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

fn button_content<'a, Message>(
    label: &str,
    kind: ButtonKind,
    icon: Option<&win_fluent::IconToken>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let icon_color = match kind {
        ButtonKind::Primary => visual.text_on_accent,
        ButtonKind::FloatingAction | ButtonKind::Link => visual.accent,
        _ => visual.text_primary,
    };

    match (kind, icon, label.trim().is_empty()) {
        (ButtonKind::Tile, Some(icon), false) => {
            let content = iced_column(vec![
                icon_element(icon, button_icon_size(kind), icon_color),
                iced_text(label.to_string())
                    .font(text_font(TextStyle::Caption))
                    .size(button_text_size(kind, visual))
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
        | (_, Some(icon), true) => icon_element(icon, button_icon_size(kind), icon_color),
        (_, Some(icon), false) => iced_row(vec![
            icon_element(icon, button_icon_size(kind), icon_color),
            iced_text(label.to_string())
                .font(text_font(TextStyle::Body))
                .size(button_text_size(kind, visual))
                .color(icon_color)
                .into(),
        ])
        .spacing(8)
        .align_y(alignment::Vertical::Center)
        .into(),
        (_, None, _) => iced_text(label.to_string())
            .font(text_font(TextStyle::Body))
            .size(button_text_size(kind, visual))
            .color(icon_color)
            .into(),
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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CaptionButtonKind {
    Minimize,
    ToggleMaximize,
    Close,
}

impl CaptionButtonKind {
    const fn glyph(self) -> char {
        match self {
            Self::Minimize => '-',
            Self::ToggleMaximize => '□',
            Self::Close => '×',
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
            .font(text_font(TextStyle::Body))
            .size(18.0)
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
        ButtonKind::Primary => visual.body_size,
        ButtonKind::Standard | ButtonKind::Subtle | ButtonKind::Link | ButtonKind::Chip => {
            visual.body_size
        }
        ButtonKind::Tile => visual.caption_size,
    }
}

fn button_icon_size(kind: ButtonKind) -> f32 {
    match kind {
        ButtonKind::Icon | ButtonKind::ResultAction => 18.0,
        ButtonKind::FloatingAction => 16.0,
        ButtonKind::Primary => 20.0,
        ButtonKind::Standard | ButtonKind::Subtle | ButtonKind::Link | ButtonKind::Chip => 16.0,
        ButtonKind::Tile => 22.0,
    }
}

fn text_size(style: TextStyle, visual: IcedVisualTheme) -> f32 {
    match style {
        TextStyle::Caption => visual.caption_size,
        TextStyle::Body => visual.body_size,
        TextStyle::BodyLarge => visual.body_large_size,
        TextStyle::BodyStrong => visual.body_strong_size,
        TextStyle::Subtitle => visual.subtitle_size,
        TextStyle::Title => visual.title_size,
        TextStyle::TitleLarge => visual.title_large_size,
    }
}

fn text_font(style: TextStyle) -> Font {
    let weight = match style {
        TextStyle::BodyStrong | TextStyle::Subtitle | TextStyle::Title | TextStyle::TitleLarge => {
            font::Weight::Semibold
        }
        TextStyle::Caption | TextStyle::Body | TextStyle::BodyLarge => font::Weight::Normal,
    };

    Font {
        family: font::Family::Name("Segoe UI Variable Text"),
        weight,
        ..Font::DEFAULT
    }
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
        Length::Fixed(value) => IcedLength::Fixed(f32::from(value)),
    }
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
    input_surface: Color,
    result_surface: Color,
    result_header: Color,
    result_header_hover: Color,
    button_hover: Color,
    button_pressed: Color,
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
            input_surface: iced_color(theme.input_surface),
            result_surface: iced_color(theme.result_surface),
            result_header: iced_color(theme.result_header),
            result_header_hover: iced_color(theme.result_header_hover),
            button_hover: iced_color(theme.button_hover),
            button_pressed: iced_color(theme.button_pressed),
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
        .background(visual.surface_alt)
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
            radius: (visual.control_height / 2.0).into(),
            ..Border::default()
        })
}

fn busy_overlay_style(visual: IcedVisualTheme, opacity: f32) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.surface_alt.scale_alpha(opacity.clamp(0.0, 1.0)))
        .color(visual.text_primary)
}

fn utility_container_style(
    style: &FluentStyle,
    visual: IcedVisualTheme,
) -> iced::widget::container::Style {
    let mut container = iced::widget::container::Style::default().color(visual.text_primary);

    if style.has("surface-card") {
        container = container.background(visual.surface);
    } else if style.has("bg-app") {
        container = container.background(visual.background);
    } else if style.has("bg-surface") {
        container = container.background(visual.surface);
    } else if style.has("bg-muted") || style.has("bg-surface-alt") {
        container = container.background(visual.surface_alt);
    } else if style.has("bg-accent") {
        container = container.background(visual.accent);
    }

    let radius = utility_radius(style, visual);
    let border_width = if style.has("border") || style.has("surface-card") {
        visual.stroke_control
    } else {
        0.0
    };

    container = container.border(Border {
        radius: radius.into(),
        width: border_width,
        color: visual.border,
    });

    if let Some(shadow) = utility_shadow(style, visual) {
        container = container.shadow(shadow);
    }

    container
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
        _ if style.has("surface-card") => 12.0,
        _ => 0.0,
    }
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

fn button_style_with_state(
    visual: IcedVisualTheme,
    kind: ButtonKind,
    focused: bool,
    selected: bool,
    status: iced::widget::button::Status,
) -> iced::widget::button::Style {
    let (background, text_color, border_color) = match kind {
        ButtonKind::Primary => match status {
            iced::widget::button::Status::Hovered | iced::widget::button::Status::Pressed => {
                (Some(visual.accent), visual.text_on_accent, visual.accent)
            }
            iced::widget::button::Status::Disabled => (
                Some(visual.surface_alt),
                visual.text_secondary.scale_alpha(visual.disabled_opacity),
                visual.border,
            ),
            iced::widget::button::Status::Active => {
                (Some(visual.accent), visual.text_on_accent, visual.accent)
            }
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
            // Selected tab: themed selected surface (#EAF3FF / #243247) with an
            // accent foreground and border, per the migration spec.
            _ => (Some(visual.selected_surface), visual.accent, visual.accent),
        },
        ButtonKind::Standard | ButtonKind::Chip | ButtonKind::Tile => match status {
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
                Some(visual.surface_alt),
                visual.text_secondary.scale_alpha(visual.disabled_opacity),
                visual.border,
            ),
            iced::widget::button::Status::Active => {
                (Some(visual.surface), visual.text_primary, visual.border)
            }
        },
    };
    let border_width = match (kind, status) {
        (
            ButtonKind::Icon | ButtonKind::Subtle | ButtonKind::Link | ButtonKind::ResultAction,
            iced::widget::button::Status::Active,
        ) => 0.0,
        (ButtonKind::Tile, _) if selected || focused => visual.stroke_focus,
        _ => visual.stroke_control,
    };
    let border_radius = match kind {
        ButtonKind::Chip => 18.0,
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
    let background = match status {
        iced::widget::button::Status::Hovered => Some(visual.result_header_hover),
        iced::widget::button::Status::Pressed => Some(visual.button_pressed),
        iced::widget::button::Status::Disabled | iced::widget::button::Status::Active => {
            Some(visual.result_header)
        }
    };

    iced::widget::button::Style {
        background: background.map(Background::Color),
        text_color: visual.text_primary,
        border: Border::default(),
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
            control_border(visual, visual.accent, visual.stroke_control)
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
    let border = match (chrome, status) {
        (TextEditorChrome::Frameless, _) => control_border(visual, visual.border, 0.0),
        (_, iced::widget::text_editor::Status::Focused { .. }) => {
            control_border(visual, visual.focus, visual.stroke_focus)
        }
        (_, iced::widget::text_editor::Status::Hovered) => {
            control_border(visual, visual.accent, visual.stroke_control)
        }
        (
            _,
            iced::widget::text_editor::Status::Disabled | iced::widget::text_editor::Status::Active,
        ) => control_border(visual, visual.border, visual.stroke_control),
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
    if label == "On" && !checked {
        "Off".to_string()
    } else {
        label.to_string()
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

fn toggle_switch_style(
    visual: IcedVisualTheme,
    status: iced::widget::toggler::Status,
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
            if is_hovered {
                visual.accent_hover
            } else {
                visual.accent
            },
            if is_hovered {
                visual.accent_hover
            } else {
                visual.accent
            },
            visual.text_on_accent,
        )
    } else {
        (
            if is_hovered {
                visual.button_hover
            } else {
                visual.surface
            },
            visual.border,
            visual.text_secondary,
        )
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
        padding_ratio: 0.15,
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
        iced::widget::pick_list::Status::Hovered
        | iced::widget::pick_list::Status::Opened { .. } => {
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

fn flyout_pick_list_style(
    visual: IcedVisualTheme,
    status: iced::widget::pick_list::Status,
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
        border: control_border(visual, visual.border.scale_alpha(0.0), 0.0),
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
    iced::widget::container::Style::default()
        .background(visual.surface)
        .color(visual.text_primary)
        .border(control_border(visual, visual.border, visual.stroke_control))
}

fn card_container_style(visual: IcedVisualTheme, kind: CardKind) -> iced::widget::container::Style {
    let background = match kind {
        CardKind::Surface | CardKind::Expander => visual.surface,
        CardKind::Elevated => visual.surface_alt,
    };

    let mut style = iced::widget::container::Style::default()
        .background(background)
        .color(visual.text_primary)
        .border(control_border(visual, visual.border, visual.stroke_control));

    if kind == CardKind::Elevated {
        style = style.shadow(elevation_shadow(visual, visual.elevation_raised));
    }

    style
}

fn result_card_container_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.result_surface)
        .color(visual.text_primary)
        .border(control_border(visual, visual.border, visual.stroke_control))
        .shadow(Shadow::default())
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
        TextStyle::Caption => visual.text_secondary,
        TextStyle::Body
        | TextStyle::BodyLarge
        | TextStyle::BodyStrong
        | TextStyle::Subtitle
        | TextStyle::Title
        | TextStyle::TitleLarge => visual.text_primary,
    }
}

fn iced_hotkey_subscription(hotkey: Hotkey) -> Subscription<IcedHotkeyEvent> {
    platform_hotkey_subscription(hotkey)
}

fn iced_named_event_subscription(name: String, auto_reset: bool) -> Subscription<IcedNamedEvent> {
    platform_named_event_subscription(name, auto_reset)
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

impl HotkeySubscriptionData {
    fn to_hotkey(&self) -> Hotkey {
        let mut hotkey = Hotkey::new(self.id.clone(), self.key.to_hotkey_key());
        for modifier in &self.modifiers {
            hotkey = hotkey.modifier(modifier.to_hotkey_modifier());
        }

        hotkey
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
            iced_color(theme.accent.base)
        );

        let focused = text_input_style(
            visual,
            iced::widget::text_input::Status::Focused { is_hovered: false },
            TextEditorChrome::Standard,
        );
        assert_eq!(focused.border.color, iced_color(theme.focus));
        assert_eq!(focused.border.width, theme.stroke.focus);
        assert_eq!(focused.selection, iced_color(theme.accent.light_2));

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
        assert_eq!(toggle_switch_label("On", true), "On");
        assert_eq!(toggle_switch_label("On", false), "Off");

        let toggle_off = toggle_switch_style(
            visual,
            iced::widget::toggler::Status::Active { is_toggled: false },
        );
        assert_eq!(
            background_color(toggle_off.background),
            iced_color(theme.surface)
        );
        assert_eq!(
            background_color(toggle_off.foreground),
            iced_color(theme.text_secondary)
        );

        let pick_list = pick_list_style(visual, iced::widget::pick_list::Status::Hovered);
        assert_eq!(
            background_color(pick_list.background),
            iced_color(theme.surface)
        );
        assert_eq!(pick_list.border.color, iced_color(theme.focus));
        assert_eq!(pick_list.border.width, theme.stroke.focus);

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

        let result_card = result_card_container_style(visual);
        assert_eq!(
            optional_background_color(result_card.background),
            iced_color(theme.result_surface)
        );
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
        assert_eq!(editor.border.color, iced_color(theme.focus));
        assert_eq!(editor.border.width, theme.stroke.focus);

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
            spacing: 0,
            width: Length::Fill,
            height: Length::Shrink,
            max_width,
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
        // paints the theme's selected surface with an accent foreground, while
        // an unselected tile does not — closing the "selected=true but wrong
        // color" gap that a schema test alone cannot catch.
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
        assert_eq!(selected.text_color, iced_color(theme.accent.base));

        let unselected = button_style_with_state(
            visual,
            ButtonKind::Tile,
            false,
            false,
            iced::widget::button::Status::Active,
        );
        assert_ne!(
            optional_background_color(unselected.background),
            iced_color(theme.selected_surface),
            "unselected tile must not paint the selected surface"
        );
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
    fn chunk_for_wrap_respects_column_cap() {
        // 7 tabs, cap 7 → a single row (wide-screen behavior).
        assert_eq!(
            chunk_for_wrap((1..=7).collect(), 7),
            vec![vec![1, 2, 3, 4, 5, 6, 7]]
        );
        // 7 tabs, cap 5 → two rows [5, 2].
        assert_eq!(
            chunk_for_wrap((1..=7).collect(), 5),
            vec![vec![1, 2, 3, 4, 5], vec![6, 7]]
        );
        // Empty input → no rows.
        assert_eq!(chunk_for_wrap(Vec::<i32>::new(), 3), Vec::<Vec<i32>>::new());
        // Zero cap is clamped to 1 (one item per row).
        assert_eq!(chunk_for_wrap(vec![1, 2], 0), vec![vec![1], vec![2]]);
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
