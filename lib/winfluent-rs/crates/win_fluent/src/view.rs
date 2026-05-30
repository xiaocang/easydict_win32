use crate::a11y::A11yHint;
use crate::action::Action;
use crate::command::CommandToken;
use crate::icon::IconToken;
use crate::state::{ControlState, ValidationState};

#[derive(Clone, Debug)]
pub struct View<Message> {
    token: ViewToken<Message>,
}

impl<Message> View<Message> {
    pub fn new(token: ViewToken<Message>) -> Self {
        Self { token }
    }

    pub fn token(&self) -> &ViewToken<Message> {
        &self.token
    }

    pub fn into_token(self) -> ViewToken<Message> {
        self.token
    }
}

pub trait IntoView<Message> {
    fn into_view(self) -> View<Message>;
}

impl<Message> IntoView<Message> for View<Message> {
    fn into_view(self) -> View<Message> {
        self
    }
}

pub trait IntoChildren<Message> {
    fn into_children(self) -> Vec<View<Message>>;
}

impl<Message> IntoChildren<Message> for () {
    fn into_children(self) -> Vec<View<Message>> {
        Vec::new()
    }
}

impl<Message, T> IntoChildren<Message> for Vec<T>
where
    T: IntoView<Message>,
{
    fn into_children(self) -> Vec<View<Message>> {
        self.into_iter().map(IntoView::into_view).collect()
    }
}

impl<Message, T, const N: usize> IntoChildren<Message> for [T; N]
where
    T: IntoView<Message>,
{
    fn into_children(self) -> Vec<View<Message>> {
        self.into_iter().map(IntoView::into_view).collect()
    }
}

impl<Message, A> IntoChildren<Message> for (A,)
where
    A: IntoView<Message>,
{
    fn into_children(self) -> Vec<View<Message>> {
        vec![self.0.into_view()]
    }
}

macro_rules! tuple_children {
    ($($name:ident),+ $(,)?) => {
        impl<Message, $($name),+> IntoChildren<Message> for ($($name,)+)
        where
            $($name: IntoView<Message>,)+
        {
            #[allow(non_snake_case)]
            fn into_children(self) -> Vec<View<Message>> {
                let ($($name,)+) = self;
                vec![$($name.into_view(),)+]
            }
        }
    };
}

tuple_children!(A, B);
tuple_children!(A, B, C);
tuple_children!(A, B, C, D);
tuple_children!(A, B, C, D, E);
tuple_children!(A, B, C, D, E, F);
tuple_children!(A, B, C, D, E, F, G);
tuple_children!(A, B, C, D, E, F, G, H);

#[derive(Clone, Debug)]
pub enum ViewToken<Message> {
    Page(PageToken<Message>),
    Text(TextToken),
    Button(ButtonToken<Message>),
    TextEditor(TextEditorToken<Message>),
    ToggleSwitch(ToggleSwitchToken<Message>),
    ComboBox(ComboBoxToken<Message>),
    CommandBar(CommandBarToken<Message>),
    NavigationView(NavigationViewToken<Message>),
    Dialog(DialogToken<Message>),
    Layout(LayoutToken<Message>),
    Lazy(LazyToken<Message>),
    ScrollView(ScrollViewToken<Message>),
    SettingsRow(SettingsRowToken<Message>),
    ServiceResultCard(ServiceResultCardToken<Message>),
    ServiceResultList(ServiceResultListToken<Message>),
    Custom(CustomToken<Message>),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Length {
    Shrink,
    Fill,
    Fixed(u16),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Alignment {
    Start,
    Center,
    End,
    Stretch,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextStyle {
    Caption,
    Body,
    BodyStrong,
    Subtitle,
    Title,
    TitleLarge,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ButtonKind {
    Standard,
    Primary,
    Subtle,
    Icon,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DialogKind {
    Content,
    Confirmation,
    Error,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ScrollPolicy {
    Auto,
    Always,
    Never,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SettingsRowKind {
    Normal,
    Expander,
    Warning,
}

#[derive(Clone, Debug)]
pub struct PageToken<Message> {
    pub id: Option<String>,
    pub title: String,
    pub content: Option<Box<View<Message>>>,
    pub commands: Vec<CommandToken<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextToken {
    pub id: Option<String>,
    pub value: String,
    pub style: TextStyle,
    pub selectable: bool,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct ButtonToken<Message> {
    pub id: Option<String>,
    pub label: String,
    pub kind: ButtonKind,
    pub icon: Option<IconToken>,
    pub tooltip: Option<String>,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct TextEditorToken<Message> {
    pub id: Option<String>,
    pub text: String,
    pub placeholder: Option<String>,
    pub min_height: Option<u16>,
    pub max_height: Option<u16>,
    pub read_only: bool,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct ToggleSwitchToken<Message> {
    pub id: Option<String>,
    pub label: String,
    pub checked: bool,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ComboBoxItem {
    pub id: String,
    pub label: String,
}

impl ComboBoxItem {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
        }
    }
}

#[derive(Clone, Debug)]
pub struct ComboBoxToken<Message> {
    pub id: Option<String>,
    pub label: Option<String>,
    pub items: Vec<ComboBoxItem>,
    pub selected: Option<String>,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct CommandBarToken<Message> {
    pub id: Option<String>,
    pub items: Vec<View<Message>>,
    pub compact: bool,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NavigationItem {
    pub id: String,
    pub label: String,
    pub icon: Option<IconToken>,
}

impl NavigationItem {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            icon: None,
        }
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }
}

#[derive(Clone, Debug)]
pub struct NavigationViewToken<Message> {
    pub id: Option<String>,
    pub selected: Option<String>,
    pub items: Vec<NavigationItem>,
    pub content: Option<Box<View<Message>>>,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct DialogToken<Message> {
    pub id: Option<String>,
    pub title: String,
    pub kind: DialogKind,
    pub content: Option<Box<View<Message>>>,
    pub primary: Option<CommandToken<Message>>,
    pub secondary: Option<CommandToken<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LayoutKind {
    Column,
    Row,
}

#[derive(Clone, Debug)]
pub struct LayoutToken<Message> {
    pub id: Option<String>,
    pub kind: LayoutKind,
    pub children: Vec<View<Message>>,
    pub padding: u16,
    pub spacing: u16,
    pub width: Length,
    pub height: Length,
    pub align: Alignment,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct LazyToken<Message> {
    pub id: Option<String>,
    pub key: String,
    pub content: Box<View<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct ScrollViewToken<Message> {
    pub id: Option<String>,
    pub content: Option<Box<View<Message>>>,
    pub horizontal: ScrollPolicy,
    pub vertical: ScrollPolicy,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct SettingsRowToken<Message> {
    pub id: Option<String>,
    pub title: String,
    pub description: Option<String>,
    pub icon: Option<IconToken>,
    pub kind: SettingsRowKind,
    pub content: Option<Box<View<Message>>>,
    pub trailing: Vec<View<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ResultItem {
    pub id: String,
    pub title: String,
    pub body: String,
    pub status: ResultStatus,
}

impl ResultItem {
    pub fn new(id: impl Into<String>, title: impl Into<String>, body: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            title: title.into(),
            body: body.into(),
            status: ResultStatus::Ready,
        }
    }

    pub fn status(mut self, status: ResultStatus) -> Self {
        self.status = status;
        self
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ResultStatus {
    Loading,
    Streaming,
    Ready,
    Error,
}

#[derive(Clone, Debug)]
pub struct ServiceResultCardToken<Message> {
    pub id: Option<String>,
    pub item: ResultItem,
    pub copy_action: Action<Message>,
    pub speak_action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct ServiceResultListToken<Message> {
    pub id: Option<String>,
    pub items: Vec<ResultItem>,
    pub copy_action: Action<Message>,
    pub speak_action: Action<Message>,
    pub virtualized: bool,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct CustomToken<Message> {
    pub id: Option<String>,
    pub control: String,
    pub children: Vec<View<Message>>,
    pub a11y: A11yHint,
}

pub fn page<Message>(title: impl Into<String>) -> PageBuilder<Message> {
    PageBuilder {
        id: None,
        title: title.into(),
        content: None,
        commands: Vec::new(),
        a11y: A11yHint::default(),
    }
}

pub fn text<Message>(value: impl Into<String>) -> View<Message> {
    View::new(ViewToken::Text(TextToken {
        id: None,
        value: value.into(),
        style: TextStyle::Body,
        selectable: false,
        a11y: A11yHint::default(),
    }))
}

pub fn button<Message>(label: impl Into<String>) -> ButtonBuilder<Message> {
    ButtonBuilder::new(label, ButtonKind::Standard)
}

pub fn primary_button<Message>(label: impl Into<String>) -> ButtonBuilder<Message> {
    ButtonBuilder::new(label, ButtonKind::Primary)
}

pub fn text_editor<Message>(text: impl Into<String>) -> TextEditorBuilder<Message> {
    TextEditorBuilder {
        id: None,
        text: text.into(),
        placeholder: None,
        min_height: None,
        max_height: None,
        read_only: false,
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn toggle_switch<Message>(
    label: impl Into<String>,
    checked: bool,
) -> ToggleSwitchBuilder<Message> {
    ToggleSwitchBuilder {
        id: None,
        label: label.into(),
        checked,
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn combo_box<Message>(
    items: impl IntoIterator<Item = ComboBoxItem>,
) -> ComboBoxBuilder<Message> {
    ComboBoxBuilder {
        id: None,
        label: None,
        items: items.into_iter().collect(),
        selected: None,
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn command_bar<Message, Children>(children: Children) -> CommandBarBuilder<Message>
where
    Children: IntoChildren<Message>,
{
    CommandBarBuilder {
        id: None,
        items: children.into_children(),
        compact: false,
        a11y: A11yHint::default(),
    }
}

pub fn navigation_view<Message>(
    items: impl IntoIterator<Item = NavigationItem>,
) -> NavigationViewBuilder<Message> {
    NavigationViewBuilder {
        id: None,
        selected: None,
        items: items.into_iter().collect(),
        content: None,
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn dialog<Message>(title: impl Into<String>) -> DialogBuilder<Message> {
    DialogBuilder {
        id: None,
        title: title.into(),
        kind: DialogKind::Content,
        content: None,
        primary: None,
        secondary: None,
        a11y: A11yHint::default(),
    }
}

pub fn column<Message, Children>(children: Children) -> LayoutBuilder<Message>
where
    Children: IntoChildren<Message>,
{
    LayoutBuilder::new(LayoutKind::Column, children.into_children())
}

pub fn row<Message, Children>(children: Children) -> LayoutBuilder<Message>
where
    Children: IntoChildren<Message>,
{
    LayoutBuilder::new(LayoutKind::Row, children.into_children())
}

pub fn lazy<Message, Child>(key: impl Into<String>, content: Child) -> LazyBuilder<Message>
where
    Child: IntoView<Message>,
{
    LazyBuilder {
        id: None,
        key: key.into(),
        content: Box::new(content.into_view()),
        a11y: A11yHint::default(),
    }
}

pub fn scroll_view<Message, Child>(content: Child) -> ScrollViewBuilder<Message>
where
    Child: IntoView<Message>,
{
    ScrollViewBuilder {
        id: None,
        content: Some(Box::new(content.into_view())),
        horizontal: ScrollPolicy::Never,
        vertical: ScrollPolicy::Auto,
        a11y: A11yHint::default(),
    }
}

pub fn settings_row<Message>(title: impl Into<String>) -> SettingsRowBuilder<Message> {
    SettingsRowBuilder {
        id: None,
        title: title.into(),
        description: None,
        icon: None,
        kind: SettingsRowKind::Normal,
        content: None,
        trailing: Vec::new(),
        a11y: A11yHint::default(),
    }
}

pub fn service_result_card<Message>(item: ResultItem) -> ServiceResultCardBuilder<Message> {
    ServiceResultCardBuilder {
        id: None,
        item,
        copy_action: Action::None,
        speak_action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn service_result_list<Message>(
    items: impl IntoIterator<Item = ResultItem>,
) -> ServiceResultListBuilder<Message> {
    ServiceResultListBuilder {
        id: None,
        items: items.into_iter().collect(),
        copy_action: Action::None,
        speak_action: Action::None,
        virtualized: true,
        a11y: A11yHint::default(),
    }
}

#[derive(Clone, Debug)]
pub struct PageBuilder<Message> {
    id: Option<String>,
    title: String,
    content: Option<Box<View<Message>>>,
    commands: Vec<CommandToken<Message>>,
    a11y: A11yHint,
}

impl<Message> PageBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn content(mut self, content: impl IntoView<Message>) -> Self {
        self.content = Some(Box::new(content.into_view()));
        self
    }

    pub fn command(mut self, command: CommandToken<Message>) -> Self {
        self.commands.push(command);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for PageBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Page(PageToken {
            id: self.id,
            title: self.title,
            content: self.content,
            commands: self.commands,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ButtonBuilder<Message> {
    id: Option<String>,
    label: String,
    kind: ButtonKind,
    icon: Option<IconToken>,
    tooltip: Option<String>,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> ButtonBuilder<Message> {
    fn new(label: impl Into<String>, kind: ButtonKind) -> Self {
        Self {
            id: None,
            label: label.into(),
            kind,
            icon: None,
            tooltip: None,
            state: ControlState::default(),
            action: Action::None,
            a11y: A11yHint::default(),
        }
    }

    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn tooltip(mut self, tooltip: impl Into<String>) -> Self {
        self.tooltip = Some(tooltip.into());
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.state.enabled = enabled;
        self
    }

    pub fn state(mut self, state: ControlState) -> Self {
        self.state = state;
        self
    }

    pub fn hovered(mut self, hovered: bool) -> Self {
        self.state.hovered = hovered;
        self
    }

    pub fn pressed(mut self, pressed: bool) -> Self {
        self.state.pressed = pressed;
        self
    }

    pub fn focused(mut self, focused: bool) -> Self {
        self.state.focused = focused;
        self
    }

    pub fn validation(mut self, validation: ValidationState) -> Self {
        self.state.validation = validation;
        self
    }

    pub fn subtle(mut self) -> Self {
        self.kind = ButtonKind::Subtle;
        self
    }

    pub fn icon_only(mut self) -> Self {
        self.kind = ButtonKind::Icon;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    pub fn on_press(mut self, message: Message) -> View<Message> {
        self.action = Action::Message(message);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for ButtonBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Button(ButtonToken {
            id: self.id,
            label: self.label,
            kind: self.kind,
            icon: self.icon,
            tooltip: self.tooltip,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct TextEditorBuilder<Message> {
    id: Option<String>,
    text: String,
    placeholder: Option<String>,
    min_height: Option<u16>,
    max_height: Option<u16>,
    read_only: bool,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> TextEditorBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn placeholder(mut self, value: impl Into<String>) -> Self {
        self.placeholder = Some(value.into());
        self
    }

    pub fn min_height(mut self, value: u16) -> Self {
        self.min_height = Some(value);
        self
    }

    pub fn max_height(mut self, value: u16) -> Self {
        self.max_height = Some(value);
        self
    }

    pub fn read_only(mut self, read_only: bool) -> Self {
        self.read_only = read_only;
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.state.enabled = enabled;
        self
    }

    pub fn state(mut self, state: ControlState) -> Self {
        self.state = state;
        self
    }

    pub fn hovered(mut self, hovered: bool) -> Self {
        self.state.hovered = hovered;
        self
    }

    pub fn pressed(mut self, pressed: bool) -> Self {
        self.state.pressed = pressed;
        self
    }

    pub fn focused(mut self, focused: bool) -> Self {
        self.state.focused = focused;
        self
    }

    pub fn validation(mut self, validation: ValidationState) -> Self {
        self.state.validation = validation;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    pub fn on_input(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::text_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for TextEditorBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::TextEditor(TextEditorToken {
            id: self.id,
            text: self.text,
            placeholder: self.placeholder,
            min_height: self.min_height,
            max_height: self.max_height,
            read_only: self.read_only,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ToggleSwitchBuilder<Message> {
    id: Option<String>,
    label: String,
    checked: bool,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> ToggleSwitchBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.state.enabled = enabled;
        self
    }

    pub fn state(mut self, state: ControlState) -> Self {
        self.state = state;
        self
    }

    pub fn hovered(mut self, hovered: bool) -> Self {
        self.state.hovered = hovered;
        self
    }

    pub fn pressed(mut self, pressed: bool) -> Self {
        self.state.pressed = pressed;
        self
    }

    pub fn focused(mut self, focused: bool) -> Self {
        self.state.focused = focused;
        self
    }

    pub fn validation(mut self, validation: ValidationState) -> Self {
        self.state.validation = validation;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    pub fn on_toggle(
        mut self,
        map: impl Fn(bool) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::bool_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for ToggleSwitchBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ToggleSwitch(ToggleSwitchToken {
            id: self.id,
            label: self.label,
            checked: self.checked,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ComboBoxBuilder<Message> {
    id: Option<String>,
    label: Option<String>,
    items: Vec<ComboBoxItem>,
    selected: Option<String>,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> ComboBoxBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn label(mut self, label: impl Into<String>) -> Self {
        self.label = Some(label.into());
        self
    }

    pub fn selected(mut self, selected: impl Into<String>) -> Self {
        self.selected = Some(selected.into());
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.state.enabled = enabled;
        self
    }

    pub fn state(mut self, state: ControlState) -> Self {
        self.state = state;
        self
    }

    pub fn hovered(mut self, hovered: bool) -> Self {
        self.state.hovered = hovered;
        self
    }

    pub fn pressed(mut self, pressed: bool) -> Self {
        self.state.pressed = pressed;
        self
    }

    pub fn focused(mut self, focused: bool) -> Self {
        self.state.focused = focused;
        self
    }

    pub fn validation(mut self, validation: ValidationState) -> Self {
        self.state.validation = validation;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    pub fn on_change(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::selection_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for ComboBoxBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ComboBox(ComboBoxToken {
            id: self.id,
            label: self.label,
            items: self.items,
            selected: self.selected,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct CommandBarBuilder<Message> {
    id: Option<String>,
    items: Vec<View<Message>>,
    compact: bool,
    a11y: A11yHint,
}

impl<Message> CommandBarBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn compact(mut self, compact: bool) -> Self {
        self.compact = compact;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for CommandBarBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::CommandBar(CommandBarToken {
            id: self.id,
            items: self.items,
            compact: self.compact,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct NavigationViewBuilder<Message> {
    id: Option<String>,
    selected: Option<String>,
    items: Vec<NavigationItem>,
    content: Option<Box<View<Message>>>,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> NavigationViewBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn selected(mut self, selected: impl Into<String>) -> Self {
        self.selected = Some(selected.into());
        self
    }

    pub fn content(mut self, content: impl IntoView<Message>) -> Self {
        self.content = Some(Box::new(content.into_view()));
        self
    }

    pub fn on_select(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::selection_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for NavigationViewBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::NavigationView(NavigationViewToken {
            id: self.id,
            selected: self.selected,
            items: self.items,
            content: self.content,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct DialogBuilder<Message> {
    id: Option<String>,
    title: String,
    kind: DialogKind,
    content: Option<Box<View<Message>>>,
    primary: Option<CommandToken<Message>>,
    secondary: Option<CommandToken<Message>>,
    a11y: A11yHint,
}

impl<Message> DialogBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn kind(mut self, kind: DialogKind) -> Self {
        self.kind = kind;
        self
    }

    pub fn content(mut self, content: impl IntoView<Message>) -> Self {
        self.content = Some(Box::new(content.into_view()));
        self
    }

    pub fn primary(mut self, command: CommandToken<Message>) -> Self {
        self.primary = Some(command);
        self
    }

    pub fn secondary(mut self, command: CommandToken<Message>) -> Self {
        self.secondary = Some(command);
        self
    }
}

impl<Message> IntoView<Message> for DialogBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Dialog(DialogToken {
            id: self.id,
            title: self.title,
            kind: self.kind,
            content: self.content,
            primary: self.primary,
            secondary: self.secondary,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct LayoutBuilder<Message> {
    id: Option<String>,
    kind: LayoutKind,
    children: Vec<View<Message>>,
    padding: u16,
    spacing: u16,
    width: Length,
    height: Length,
    align: Alignment,
    a11y: A11yHint,
}

impl<Message> LayoutBuilder<Message> {
    fn new(kind: LayoutKind, children: Vec<View<Message>>) -> Self {
        Self {
            id: None,
            kind,
            children,
            padding: 0,
            spacing: 0,
            width: Length::Shrink,
            height: Length::Shrink,
            align: Alignment::Start,
            a11y: A11yHint::default(),
        }
    }

    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn padding(mut self, value: u16) -> Self {
        self.padding = value;
        self
    }

    pub fn spacing(mut self, value: u16) -> Self {
        self.spacing = value;
        self
    }

    pub fn width(mut self, value: Length) -> Self {
        self.width = value;
        self
    }

    pub fn height(mut self, value: Length) -> Self {
        self.height = value;
        self
    }

    pub fn align(mut self, value: Alignment) -> Self {
        self.align = value;
        self
    }
}

impl<Message> IntoView<Message> for LayoutBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Layout(LayoutToken {
            id: self.id,
            kind: self.kind,
            children: self.children,
            padding: self.padding,
            spacing: self.spacing,
            width: self.width,
            height: self.height,
            align: self.align,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct LazyBuilder<Message> {
    id: Option<String>,
    key: String,
    content: Box<View<Message>>,
    a11y: A11yHint,
}

impl<Message> LazyBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for LazyBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Lazy(LazyToken {
            id: self.id,
            key: self.key,
            content: self.content,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ScrollViewBuilder<Message> {
    id: Option<String>,
    content: Option<Box<View<Message>>>,
    horizontal: ScrollPolicy,
    vertical: ScrollPolicy,
    a11y: A11yHint,
}

impl<Message> ScrollViewBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn horizontal(mut self, policy: ScrollPolicy) -> Self {
        self.horizontal = policy;
        self
    }

    pub fn vertical(mut self, policy: ScrollPolicy) -> Self {
        self.vertical = policy;
        self
    }
}

impl<Message> IntoView<Message> for ScrollViewBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ScrollView(ScrollViewToken {
            id: self.id,
            content: self.content,
            horizontal: self.horizontal,
            vertical: self.vertical,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct SettingsRowBuilder<Message> {
    id: Option<String>,
    title: String,
    description: Option<String>,
    icon: Option<IconToken>,
    kind: SettingsRowKind,
    content: Option<Box<View<Message>>>,
    trailing: Vec<View<Message>>,
    a11y: A11yHint,
}

impl<Message> SettingsRowBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn description(mut self, value: impl Into<String>) -> Self {
        self.description = Some(value.into());
        self
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn kind(mut self, kind: SettingsRowKind) -> Self {
        self.kind = kind;
        self
    }

    pub fn content(mut self, content: impl IntoView<Message>) -> Self {
        self.content = Some(Box::new(content.into_view()));
        self
    }

    pub fn trailing(mut self, children: impl IntoChildren<Message>) -> Self {
        self.trailing = children.into_children();
        self
    }
}

impl<Message> IntoView<Message> for SettingsRowBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::SettingsRow(SettingsRowToken {
            id: self.id,
            title: self.title,
            description: self.description,
            icon: self.icon,
            kind: self.kind,
            content: self.content,
            trailing: self.trailing,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ServiceResultCardBuilder<Message> {
    id: Option<String>,
    item: ResultItem,
    copy_action: Action<Message>,
    speak_action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> ServiceResultCardBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn on_copy(mut self, message: Message) -> Self {
        self.copy_action = Action::Message(message);
        self
    }

    pub fn on_speak(mut self, message: Message) -> Self {
        self.speak_action = Action::Message(message);
        self
    }
}

impl<Message> IntoView<Message> for ServiceResultCardBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ServiceResultCard(ServiceResultCardToken {
            id: self.id,
            item: self.item,
            copy_action: self.copy_action,
            speak_action: self.speak_action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ServiceResultListBuilder<Message> {
    id: Option<String>,
    items: Vec<ResultItem>,
    copy_action: Action<Message>,
    speak_action: Action<Message>,
    virtualized: bool,
    a11y: A11yHint,
}

impl<Message> ServiceResultListBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn on_copy(mut self, message: Message) -> Self {
        self.copy_action = Action::Message(message);
        self
    }

    pub fn on_speak(mut self, message: Message) -> Self {
        self.speak_action = Action::Message(message);
        self
    }

    pub fn virtualized(mut self, enabled: bool) -> Self {
        self.virtualized = enabled;
        self
    }
}

impl<Message> IntoView<Message> for ServiceResultListBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ServiceResultList(ServiceResultListToken {
            id: self.id,
            items: self.items,
            copy_action: self.copy_action,
            speak_action: self.speak_action,
            virtualized: self.virtualized,
            a11y: self.a11y,
        }))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[allow(dead_code)]
    #[derive(Clone, Debug)]
    enum Msg {
        InputChanged(String),
        Submit,
    }

    #[test]
    fn builds_page_tree_without_backend_types() {
        let view = page("Home")
            .content(
                column((
                    text("Ready"),
                    text_editor("")
                        .id("input")
                        .placeholder("Type")
                        .on_input(Msg::InputChanged),
                    primary_button("Run")
                        .icon(crate::icon::translate())
                        .on_press(Msg::Submit),
                ))
                .padding(24)
                .spacing(12),
            )
            .into_view();

        match view.token() {
            ViewToken::Page(page) => {
                assert_eq!(page.title, "Home");
                assert!(page.content.is_some());
            }
            _ => panic!("expected page"),
        }
    }
}
