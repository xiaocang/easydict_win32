use std::collections::HashMap;
use std::env;
use std::fmt;
use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};

use iced::advanced::{
    clipboard::Clipboard,
    layout, mouse, overlay, renderer,
    widget::{self, Operation, Tree},
    Layout, Shell, Widget,
};
use iced::widget::text_editor as iced_text_editor_state;
use iced::widget::{
    button as iced_button, checkbox as iced_checkbox, column as iced_column,
    container as iced_container, pick_list as iced_pick_list, row as iced_row,
    scrollable as iced_scrollable, space as iced_space, text as iced_text,
    text_editor as iced_text_editor, text_input as iced_text_input,
};
use iced::{
    alignment, font, window, Background, Border, Color, Element, Event, Font, Length as IcedLength,
    Point, Rectangle, Shadow, Size, Subscription, Vector,
};
use win_fluent::action::{Action, ActionKind};
use win_fluent::command::CommandToken;
use win_fluent::platform::{Hotkey, HotkeyKey, HotkeyModifier};
use win_fluent::runtime::{Application as FluentApplication, RuntimePlan};
use win_fluent::screenshot::WindowScreenshot;
use win_fluent::state::ValidationSeverity;
use win_fluent::style::FluentStyle;
use win_fluent::task::Task as FluentTask;
use win_fluent::theme::{Color as FluentColor, ThemeMode, ThemeTokens};
use win_fluent::view::{
    ButtonKind, CardKind, CardToken, CollapseTransition, ComboBoxItem, LayoutDistribution,
    LayoutKind, Length, ResultCardToken, ResultItem, ResultListToken, SettingsRowToken,
    StatusBadgeToken, TextEditorChrome, TextEditorToken, TextStyle, TitleBarToken, View, ViewToken,
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
        Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent>,
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
        Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent>,
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
    WindowOpened(window::Id),
}

struct IcedSingleWindowRuntime<App: FluentApplication> {
    app: App,
    logical_window_id: WindowId,
    native_window_id: Option<window::Id>,
    view: View<App::Message>,
    text_editors: TextEditorCache,
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
        let runtime = Self::new(plan.app, options.id);
        let initial_task = runtime.fluent_task(plan.initial_task);

        (runtime, initial_task)
    }

    fn new(app: App, logical_window_id: WindowId) -> Self {
        let view = app.view(&logical_window_id);
        let mut text_editors = TextEditorCache::default();
        text_editors.sync(&view);

        Self {
            app,
            logical_window_id,
            native_window_id: None,
            view,
            text_editors,
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
                state.fluent_task(task)
            }
            IcedRuntimeMessage::WindowOpened(window_id) => {
                state.native_window_id = Some(window_id);
                iced::Task::none()
            }
        }
    }

    fn view(state: &Self) -> IcedElement<'_, IcedRuntimeMessage<App::Message>> {
        let theme = ThemeTokens::resolve(state.app.theme());
        IcedAdapter::compile_view_with_text_editors_and_theme(
            &state.view,
            |id| state.text_editors.get(id),
            &theme,
        )
        .map(IcedRuntimeMessage::App)
    }

    fn subscription(_state: &Self) -> Subscription<IcedRuntimeMessage<App::Message>> {
        window::open_events().map(IcedRuntimeMessage::WindowOpened)
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
            FluentTask::Window(command) => self.window_command(command),
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
        ViewToken::Lazy(token) => collect_text_editor_values(&token.content, values),
        ViewToken::ScrollView(token) => {
            if let Some(content) = &token.content {
                collect_text_editor_values(content, values);
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
        ViewToken::Custom(token) => {
            for child in &token.children {
                collect_text_editor_values(child, values);
            }
        }
        ViewToken::Text(_)
        | ViewToken::Button(_)
        | ViewToken::StatusBadge(_)
        | ViewToken::Spacer(_)
        | ViewToken::ToggleSwitch(_)
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

fn compile_view_with_text_editors_and_visual<'a, Message, Provider>(
    view: &'a View<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent>,
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
            .style(move |_, status| button_style(visual, kind, status));

            control = match kind {
                ButtonKind::Icon => control
                    .width(IcedLength::Fixed(visual.icon_button_size))
                    .height(IcedLength::Fixed(visual.icon_button_size))
                    .padding(0),
                ButtonKind::Primary if token.icon.is_some() && token.label.trim().is_empty() => {
                    control
                        .width(IcedLength::Fixed(visual.primary_icon_button_size()))
                        .height(IcedLength::Fixed(visual.primary_icon_button_size()))
                        .padding(0)
                }
                ButtonKind::Primary => control.padding([8, 14]),
                ButtonKind::Subtle => control.padding([6, 10]),
                ButtonKind::Standard => control.padding([6, 12]),
            };

            if token.state.enabled {
                if let Some(message) = token.action.press() {
                    control = control.on_press(message);
                }
            }

            control.into()
        }
        ViewToken::StatusBadge(token) => compile_status_badge(token, visual),
        ViewToken::Card(token) => compile_card(token, provider, visual),
        ViewToken::Spacer(token) => iced_space()
            .width(iced_length(token.width))
            .height(iced_length(token.height))
            .into(),
        ViewToken::TextEditor(token) => compile_text_editor(token, provider, visual),
        ViewToken::ToggleSwitch(token) => {
            let mut control = iced_checkbox(token.checked)
                .label(token.label.clone())
                .style(move |_, status| checkbox_style(visual, status));

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

            apply_layout_style(content, &token.style, token.width, token.height, visual)
        }
        ViewToken::Lazy(token) => {
            compile_view_with_text_editors_and_visual(&token.content, provider, visual)
        }
        ViewToken::ScrollView(token) => {
            let content = token
                .content
                .as_deref()
                .map(|content| compile_view_with_text_editors_and_visual(content, provider, visual))
                .unwrap_or_else(empty);
            iced_scrollable(iced_container(content).width(IcedLength::Fill))
                .width(IcedLength::Fill)
                .height(IcedLength::Fill)
                .into()
        }
        ViewToken::SettingsRow(token) => compile_settings_row(token, provider, visual),
        ViewToken::ResultCard(token) => compile_result_card(token, visual),
        ViewToken::ResultList(token) => compile_result_list(token, visual),
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

fn compile_title_bar<'a, Message, Provider>(
    token: &'a TitleBarToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent>,
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
        children.push(icon_element(icon, 12.0, Color::WHITE));
    } else {
        children.push(
            iced_text("●")
                .font(text_font(TextStyle::Caption))
                .size(12.0)
                .color(Color::WHITE)
                .into(),
        );
    }

    children.push(
        iced_text(token.label.clone())
            .font(text_font(TextStyle::Body))
            .size(text_size(TextStyle::Body, visual))
            .color(Color::WHITE)
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

fn compile_card<'a, Message, Provider>(
    token: &'a CardToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent>,
{
    let title = label_with_icon(&token.title, token.icon.as_ref());
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
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent>,
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

fn compile_settings_row<'a, Message, Provider>(
    token: &'a SettingsRowToken<Message>,
    provider: Provider,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
    Provider: Copy + Fn(&str) -> Option<&'a IcedTextEditorContent>,
{
    let title = label_with_icon(&token.title, token.icon.as_ref());
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

    let header = iced_row(vec![
        text_column.width(IcedLength::Fill).into(),
        trailing.into(),
    ])
    .spacing(12)
    .width(IcedLength::Fill)
    .align_y(alignment::Vertical::Center);

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
        visual.text_secondary
    } else {
        visual.text_primary
    };
    let secondary_color = visual.text_secondary;

    if let Some(icon) = &item.icon {
        header_left_children.push(
            iced_container(icon_element(icon, 16.0, primary_color))
                .width(IcedLength::Fixed(22.0))
                .height(IcedLength::Fixed(visual.result_header_height))
                .align_y(alignment::Vertical::Center)
                .into(),
        );
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
                .color(primary_color)
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

    content
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
        operation.container(None, layout.bounds());
        operation.traverse(&mut |operation| {
            self.content.as_widget_mut().operate(
                &mut tree.children[0],
                layout.children().next().unwrap(),
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

fn label_with_icon(label: &str, icon: Option<&win_fluent::IconToken>) -> String {
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
    let icon_color = if kind == ButtonKind::Primary {
        visual.text_on_accent
    } else {
        visual.text_primary
    };

    match (kind, icon, label.trim().is_empty()) {
        (ButtonKind::Icon, Some(icon), _) | (_, Some(icon), true) => {
            icon_element(icon, button_icon_size(kind), icon_color)
        }
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
        "keyboard" => '\u{E765}',
        "microphone" => '\u{E720}',
        "more" => '\u{E712}',
        "play" => '\u{E768}',
        "search" => '\u{E721}',
        "settings" => '\u{E713}',
        "speaker" => '\u{E767}',
        "swap" => '\u{E8AB}',
        "translate" => '\u{5b57}',
        _ => '\u{E8A5}',
    }
}

fn button_text_size(kind: ButtonKind, visual: IcedVisualTheme) -> f32 {
    match kind {
        ButtonKind::Icon => 18.0,
        ButtonKind::Primary => visual.body_size,
        ButtonKind::Standard | ButtonKind::Subtle => visual.body_size,
    }
}

fn button_icon_size(kind: ButtonKind) -> f32 {
    match kind {
        ButtonKind::Icon => 18.0,
        ButtonKind::Primary => 20.0,
        ButtonKind::Standard | ButtonKind::Subtle => 16.0,
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
    text_primary: Color,
    text_secondary: Color,
    text_on_accent: Color,
    border: Color,
    focus: Color,
    success: Color,
    warning: Color,
    error: Color,
    accent: Color,
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
    title_bar_height: f32,
    caption_button_width: f32,
    card_padding: f32,
    result_header_height: f32,
    elevation_raised: f32,
}

impl IcedVisualTheme {
    fn primary_icon_button_size(self) -> f32 {
        self.control_height + 8.0
    }

    fn from_tokens(theme: &ThemeTokens) -> Self {
        Self {
            mode: theme.mode,
            background: iced_color(theme.background),
            surface: iced_color(theme.surface),
            surface_alt: iced_color(theme.surface_alt),
            text_primary: iced_color(theme.text_primary),
            text_secondary: iced_color(theme.text_secondary),
            text_on_accent: if theme.mode == ThemeMode::HighContrast {
                Color::BLACK
            } else {
                Color::WHITE
            },
            border: iced_color(theme.border),
            focus: iced_color(theme.focus),
            success: iced_color(theme.success),
            warning: iced_color(theme.warning),
            error: iced_color(theme.error),
            accent: iced_color(theme.accent.base),
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
            title_bar_height: theme.control.title_bar_height,
            caption_button_width: theme.control.caption_button_width,
            card_padding: theme.control.card_padding,
            result_header_height: theme.control.result_header_height,
            elevation_raised: theme.elevation.raised,
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
        ValidationSeverity::Info => visual.accent,
    };

    iced::widget::container::Style::default()
        .background(background)
        .color(Color::WHITE)
        .border(Border {
            radius: (visual.control_height / 2.0).into(),
            ..Border::default()
        })
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
    let disabled = status == iced::widget::button::Status::Disabled;
    let (background, text_color, border_color) = match kind {
        ButtonKind::Primary => match status {
            iced::widget::button::Status::Hovered => (
                Some(visual.accent_light),
                visual.text_on_accent,
                visual.accent_light,
            ),
            iced::widget::button::Status::Pressed => (
                Some(visual.accent_dark),
                visual.text_on_accent,
                visual.accent_dark,
            ),
            iced::widget::button::Status::Disabled => (
                Some(visual.surface_alt),
                visual.text_secondary,
                visual.border,
            ),
            iced::widget::button::Status::Active => {
                (Some(visual.accent), visual.text_on_accent, visual.accent)
            }
        },
        ButtonKind::Subtle | ButtonKind::Icon => match status {
            iced::widget::button::Status::Hovered => {
                (Some(visual.surface_alt), visual.text_primary, visual.border)
            }
            iced::widget::button::Status::Pressed => {
                (Some(visual.border), visual.text_primary, visual.border)
            }
            iced::widget::button::Status::Disabled => (None, visual.text_secondary, visual.border),
            iced::widget::button::Status::Active => (None, visual.text_primary, visual.border),
        },
        ButtonKind::Standard => match status {
            iced::widget::button::Status::Hovered => {
                (Some(visual.surface_alt), visual.text_primary, visual.border)
            }
            iced::widget::button::Status::Pressed => {
                (Some(visual.border), visual.text_primary, visual.border)
            }
            iced::widget::button::Status::Disabled => (
                Some(visual.surface_alt),
                visual.text_secondary,
                visual.border,
            ),
            iced::widget::button::Status::Active => {
                (Some(visual.surface), visual.text_primary, visual.border)
            }
        },
    };
    let border_width = match (kind, status) {
        (ButtonKind::Icon | ButtonKind::Subtle, iced::widget::button::Status::Active) => 0.0,
        _ => visual.stroke_control,
    };

    iced::widget::button::Style {
        background: background.map(Background::Color),
        text_color,
        border: control_border(visual, border_color, border_width),
        shadow: if disabled {
            Shadow::default()
        } else if matches!(kind, ButtonKind::Icon | ButtonKind::Subtle) {
            Shadow::default()
        } else {
            elevation_shadow(visual, visual.elevation_raised)
        },
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
        iced::widget::button::Status::Hovered => Some(visual.accent_light_alt),
        iced::widget::button::Status::Pressed => Some(visual.accent_light),
        iced::widget::button::Status::Disabled | iced::widget::button::Status::Active => None,
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
        } else if chrome == TextEditorChrome::Frameless {
            visual.surface_alt
        } else {
            visual.surface
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
        } else if chrome == TextEditorChrome::Frameless {
            visual.surface_alt
        } else {
            visual.surface
        }),
        border,
        placeholder: visual.text_secondary,
        value,
        selection: visual.accent_light_alt,
    }
}

fn checkbox_style(
    visual: IcedVisualTheme,
    status: iced::widget::checkbox::Status,
) -> iced::widget::checkbox::Style {
    let (is_checked, disabled, hovered) = match status {
        iced::widget::checkbox::Status::Active { is_checked } => (is_checked, false, false),
        iced::widget::checkbox::Status::Hovered { is_checked } => (is_checked, false, true),
        iced::widget::checkbox::Status::Disabled { is_checked } => (is_checked, true, false),
    };

    let background = if is_checked {
        if hovered {
            visual.accent_light
        } else {
            visual.accent
        }
    } else if hovered {
        visual.surface_alt
    } else {
        visual.surface
    };

    iced::widget::checkbox::Style {
        background: Background::Color(if disabled {
            visual.surface_alt
        } else {
            background
        }),
        icon_color: if visual.mode == ThemeMode::HighContrast {
            Color::BLACK
        } else {
            visual.text_on_accent
        },
        border: control_border(
            visual,
            if is_checked && !disabled {
                visual.accent
            } else {
                visual.border
            },
            visual.stroke_control,
        ),
        text_color: Some(if disabled {
            visual.text_secondary
        } else {
            visual.text_primary
        }),
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
        .background(visual.surface_alt)
        .color(visual.text_primary)
        .border(control_border(visual, visual.border, visual.stroke_control))
        .shadow(Shadow::default())
}

fn control_border(visual: IcedVisualTheme, color: Color, width: f32) -> Border {
    Border::default()
        .color(color)
        .width(width)
        .rounded(visual.radius_control)
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

#[cfg(windows)]
fn platform_hotkey_subscription(hotkey: Hotkey) -> Subscription<IcedHotkeyEvent> {
    Subscription::run_with(HotkeySubscriptionData::from(hotkey), hotkey_stream)
}

#[cfg(not(windows))]
fn platform_hotkey_subscription(_hotkey: Hotkey) -> Subscription<IcedHotkeyEvent> {
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

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct HotkeySubscriptionData {
    id: String,
    key: HotkeyKeyData,
    modifiers: Vec<HotkeyModifierData>,
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
    fn compiles_text_editor_with_stateful_multiline_content() {
        let content = IcedTextEditorContent::with_text("Line 1\nLine 2");
        let view = page("Editor")
            .content(
                text_editor("Line 1\nLine 2")
                    .id("editor")
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
        assert!(primary.shadow.blur_radius > 0.0);

        let focused = text_input_style(
            visual,
            iced::widget::text_input::Status::Focused { is_hovered: false },
            TextEditorChrome::Standard,
        );
        assert_eq!(focused.border.color, iced_color(theme.focus));
        assert_eq!(focused.border.width, theme.stroke.focus);
        assert_eq!(focused.selection, iced_color(theme.accent.light_2));
    }

    #[test]
    fn maps_visual_theme_to_remaining_control_and_surface_styles() {
        let theme = ThemeTokens::fluent_light();
        let visual = IcedVisualTheme::from_tokens(&theme);

        let checked = checkbox_style(
            visual,
            iced::widget::checkbox::Status::Active { is_checked: true },
        );
        assert_eq!(
            background_color(checked.background),
            iced_color(theme.accent.base)
        );
        assert_eq!(checked.text_color, Some(iced_color(theme.text_primary)));
        assert_eq!(checked.border.width, theme.stroke.control);

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
            iced_color(theme.surface_alt)
        );
        assert_eq!(result_card.shadow, Shadow::default());
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
