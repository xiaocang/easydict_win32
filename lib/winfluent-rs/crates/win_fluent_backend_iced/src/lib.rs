use std::fmt;

use iced::widget::text_editor as iced_text_editor_state;
use iced::widget::{
    button as iced_button, checkbox as iced_checkbox, column as iced_column,
    container as iced_container, pick_list as iced_pick_list, row as iced_row,
    scrollable as iced_scrollable, text as iced_text, text_editor as iced_text_editor,
    text_input as iced_text_input,
};
use iced::{
    Background, Border, Color, Element, Length as IcedLength, Point, Shadow, Size, Subscription,
    Vector,
};
use win_fluent::action::{Action, ActionKind};
use win_fluent::command::CommandToken;
use win_fluent::platform::{Hotkey, HotkeyKey, HotkeyModifier};
use win_fluent::theme::{Color as FluentColor, ThemeMode, ThemeTokens};
use win_fluent::view::{
    ButtonKind, ComboBoxItem, LayoutKind, Length, ResultItem, ServiceResultCardToken,
    ServiceResultListToken, SettingsRowToken, TextEditorToken, TextStyle, View, ViewToken,
};
use win_fluent::window::{
    WindowFrame, WindowLevel, WindowOptions, WindowPlacement, WindowResizeMode,
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
    }

    settings
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
                .padding(12)
                .style(move |_| page_container_style(visual))
                .into()
        }
        ViewToken::Text(token) => compile_text(&token.value, token.style, visual),
        ViewToken::Button(token) => {
            let label = label_with_icon(&token.label, token.icon.as_ref().map(|icon| icon.name));
            let kind = token.kind;
            let mut control = iced_button(iced_text(label))
                .style(move |_, status| button_style(visual, kind, status));

            if token.state.enabled {
                if let Some(message) = token.action.press() {
                    control = control.on_press(message);
                }
            }

            control.into()
        }
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
            iced_row(children)
                .spacing(if token.compact { 4 } else { 8 })
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
            match token.kind {
                LayoutKind::Column => iced_column(children)
                    .padding(token.padding)
                    .spacing(u32::from(token.spacing))
                    .width(iced_length(token.width))
                    .height(iced_length(token.height))
                    .into(),
                LayoutKind::Row => iced_row(children)
                    .padding(token.padding)
                    .spacing(u32::from(token.spacing))
                    .width(iced_length(token.width))
                    .height(iced_length(token.height))
                    .into(),
            }
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
            iced_scrollable(content).into()
        }
        ViewToken::SettingsRow(token) => compile_settings_row(token, provider, visual),
        ViewToken::ServiceResultCard(token) => compile_result_card(token, visual),
        ViewToken::ServiceResultList(token) => compile_result_list(token, visual),
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
        .size(text_size(style))
        .color(text_color(style, visual))
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
            .style(move |_, status| text_editor_style(visual, status));

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
        .style(move |_, status| text_input_style(visual, status));

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
    let title = label_with_icon(&token.title, token.icon.as_ref().map(|icon| icon.name));
    let mut text_column =
        iced_column(vec![compile_text(&title, TextStyle::BodyStrong, visual)]).spacing(2);

    if let Some(description) = &token.description {
        text_column = text_column.push(compile_text(description, TextStyle::Caption, visual));
    }
    if let Some(content) = &token.content {
        text_column = text_column.push(compile_view_with_text_editors_and_visual(
            content, provider, visual,
        ));
    }

    let mut trailing = iced_row(Vec::new()).spacing(8);
    for child in &token.trailing {
        trailing = trailing.push(compile_view_with_text_editors_and_visual(
            child, provider, visual,
        ));
    }

    let row = iced_row(vec![
        text_column.width(IcedLength::Fill).into(),
        trailing.into(),
    ])
    .padding(8)
    .spacing(12);

    iced_container(row)
        .style(move |_| settings_row_container_style(visual))
        .into()
}

fn compile_result_card<'a, Message>(
    token: &'a ServiceResultCardToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let content = result_item_column(&token.item, visual)
        .push(iced_row(vec![
            action_button("Copy", &token.copy_action, visual),
            action_button("Speak", &token.speak_action, visual),
        ]))
        .padding(12)
        .spacing(8);

    iced_container(content)
        .style(move |_| result_card_container_style(visual))
        .into()
}

fn compile_result_list<'a, Message>(
    token: &'a ServiceResultListToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mut list = iced_column(Vec::new()).spacing(8);

    for item in &token.items {
        list = list.push(
            iced_container(
                result_item_column(item, visual)
                    .push(iced_row(vec![
                        action_button("Copy", &token.copy_action, visual),
                        action_button("Speak", &token.speak_action, visual),
                    ]))
                    .padding(12),
            )
            .style(move |_| result_card_container_style(visual)),
        );
    }

    list.into()
}

fn result_item_column<'a, Message>(
    item: &ResultItem,
    visual: IcedVisualTheme,
) -> iced::widget::Column<'a, Message>
where
    Message: Clone + Send + 'static,
{
    iced_column(vec![
        compile_text(
            &format!("{} ({:?})", item.title, item.status),
            TextStyle::BodyStrong,
            visual,
        ),
        compile_text(&item.body, TextStyle::Body, visual),
    ])
}

fn compile_command<'a, Message>(
    command: &'a CommandToken<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let label = label_with_icon(&command.label, command.icon.as_ref().map(|icon| icon.name));
    let mut control = iced_button(iced_text(label))
        .style(move |_, status| button_style(visual, ButtonKind::Standard, status));
    if command.enabled {
        if let Some(message) = command.action.press() {
            control = control.on_press(message);
        }
    }
    control.into()
}

fn action_button<'a, Message>(
    label: &str,
    action: &Action<Message>,
    visual: IcedVisualTheme,
) -> IcedElement<'a, Message>
where
    Message: Clone + Send + 'static,
{
    let mut control = iced_button(iced_text(label.to_string()))
        .style(move |_, status| button_style(visual, ButtonKind::Standard, status));
    if let Some(message) = action.press() {
        control = control.on_press(message);
    }
    control.into()
}

fn label_with_icon(label: &str, icon: Option<&'static str>) -> String {
    match icon {
        Some(icon) => format!("{icon} {label}"),
        None => label.to_string(),
    }
}

fn text_size(style: TextStyle) -> u32 {
    match style {
        TextStyle::Caption => 12,
        TextStyle::Body | TextStyle::BodyStrong => 14,
        TextStyle::Subtitle => 20,
        TextStyle::Title => 28,
        TextStyle::TitleLarge => 40,
    }
}

fn iced_length(length: Length) -> IcedLength {
    match length {
        Length::Shrink => IcedLength::Shrink,
        Length::Fill => IcedLength::Fill,
        Length::Fixed(value) => IcedLength::Fixed(f32::from(value)),
    }
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
    accent: Color,
    accent_light: Color,
    accent_light_alt: Color,
    accent_dark: Color,
    radius_control: f32,
    stroke_control: f32,
    stroke_focus: f32,
    elevation_raised: f32,
}

impl IcedVisualTheme {
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
            accent: iced_color(theme.accent.base),
            accent_light: iced_color(theme.accent.light_1),
            accent_light_alt: iced_color(theme.accent.light_2),
            accent_dark: iced_color(theme.accent.dark_1),
            radius_control: theme.radius.control,
            stroke_control: theme.stroke.control,
            stroke_focus: theme.stroke.focus,
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

    iced::widget::button::Style {
        background: background.map(Background::Color),
        text_color,
        border: control_border(visual, border_color, visual.stroke_control),
        shadow: if disabled {
            Shadow::default()
        } else {
            elevation_shadow(visual, visual.elevation_raised)
        },
        ..iced::widget::button::Style::default()
    }
}

fn text_input_style(
    visual: IcedVisualTheme,
    status: iced::widget::text_input::Status,
) -> iced::widget::text_input::Style {
    let border = match status {
        iced::widget::text_input::Status::Focused { .. } => {
            control_border(visual, visual.focus, visual.stroke_focus)
        }
        iced::widget::text_input::Status::Hovered => {
            control_border(visual, visual.accent, visual.stroke_control)
        }
        iced::widget::text_input::Status::Disabled | iced::widget::text_input::Status::Active => {
            control_border(visual, visual.border, visual.stroke_control)
        }
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
) -> iced::widget::text_editor::Style {
    let border = match status {
        iced::widget::text_editor::Status::Focused { .. } => {
            control_border(visual, visual.focus, visual.stroke_focus)
        }
        iced::widget::text_editor::Status::Hovered => {
            control_border(visual, visual.accent, visual.stroke_control)
        }
        iced::widget::text_editor::Status::Disabled | iced::widget::text_editor::Status::Active => {
            control_border(visual, visual.border, visual.stroke_control)
        }
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

fn result_card_container_style(visual: IcedVisualTheme) -> iced::widget::container::Style {
    iced::widget::container::Style::default()
        .background(visual.surface)
        .color(visual.text_primary)
        .border(control_border(visual, visual.border, visual.stroke_control))
        .shadow(elevation_shadow(visual, visual.elevation_raised))
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
            iced_color(theme.surface)
        );
        assert!(result_card.shadow.blur_radius > 0.0);
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
