use crate::a11y::A11yHint;
use crate::action::Action;
use crate::command::CommandToken;
use crate::icon::IconToken;
use crate::motion::Transition;
use crate::state::{ControlState, ValidationSeverity, ValidationState};
use crate::style::{utility_scale, FluentStyle};

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
    TitleBar(TitleBarToken<Message>),
    Text(TextToken),
    Button(ButtonToken<Message>),
    FlyoutButton(FlyoutButtonToken<Message>),
    StatusBadge(StatusBadgeToken),
    ProgressRing(ProgressRingToken),
    BusyOverlay(BusyOverlayToken<Message>),
    Card(CardToken<Message>),
    Spacer(SpacerToken),
    TextEditor(TextEditorToken<Message>),
    ToggleSwitch(ToggleSwitchToken<Message>),
    ComboBox(ComboBoxToken<Message>),
    CommandBar(CommandBarToken<Message>),
    NavigationView(NavigationViewToken<Message>),
    Dialog(DialogToken<Message>),
    Layout(LayoutToken<Message>),
    AdaptiveSwitch(AdaptiveSwitchToken<Message>),
    Lazy(LazyToken<Message>),
    ScrollView(ScrollViewToken<Message>),
    SettingsRow(SettingsRowToken<Message>),
    ResultCard(ResultCardToken<Message>),
    ResultList(ResultListToken<Message>),
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
pub enum LayoutDistribution {
    Start,
    SpaceBetween,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextStyle {
    Caption,
    Body,
    BodyLarge,
    BodyStrong,
    Subtitle,
    Title,
    TitleLarge,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ButtonKind {
    Standard,
    Primary,
    Chip,
    Subtle,
    Icon,
    ResultAction,
    FloatingAction,
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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CardKind {
    Surface,
    Elevated,
    Expander,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextEditorChrome {
    Standard,
    Frameless,
}

#[derive(Clone, Debug)]
pub struct PageToken<Message> {
    pub id: Option<String>,
    pub title: String,
    pub content: Option<Box<View<Message>>>,
    pub commands: Vec<CommandToken<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct TitleBarToken<Message> {
    pub id: Option<String>,
    pub title: String,
    pub subtitle: Option<String>,
    pub icon: Option<IconToken>,
    pub commands: Vec<View<Message>>,
    pub show_caption_controls: bool,
    pub minimize_action: Action<Message>,
    pub toggle_maximize_action: Action<Message>,
    pub close_action: Action<Message>,
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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FlyoutMenuItemKind {
    Command,
    Radio,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FlyoutMenuItem {
    pub id: String,
    pub label: String,
    pub icon: Option<IconToken>,
    pub kind: FlyoutMenuItemKind,
    pub checked: bool,
    pub enabled: bool,
}

impl FlyoutMenuItem {
    pub fn command(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            icon: None,
            kind: FlyoutMenuItemKind::Command,
            checked: false,
            enabled: true,
        }
    }

    pub fn radio(id: impl Into<String>, label: impl Into<String>, checked: bool) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            icon: None,
            kind: FlyoutMenuItemKind::Radio,
            checked,
            enabled: true,
        }
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }
}

#[derive(Clone, Debug)]
pub struct FlyoutButtonToken<Message> {
    pub id: Option<String>,
    pub label: String,
    pub icon: Option<IconToken>,
    pub tooltip: Option<String>,
    pub selected: Option<String>,
    pub items: Vec<FlyoutMenuItem>,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct StatusBadgeToken {
    pub id: Option<String>,
    pub label: String,
    pub severity: ValidationSeverity,
    pub icon: Option<IconToken>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProgressRingToken {
    pub id: Option<String>,
    pub active: bool,
    pub size: u16,
    pub label: Option<String>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct BusyOverlayToken<Message> {
    pub id: Option<String>,
    pub active: bool,
    pub opacity: f32,
    pub blocks_input: bool,
    pub label: Option<String>,
    pub content: Box<View<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct CardToken<Message> {
    pub id: Option<String>,
    pub title: String,
    pub description: Option<String>,
    pub icon: Option<IconToken>,
    pub kind: CardKind,
    pub content: Option<Box<View<Message>>>,
    pub trailing: Vec<View<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SpacerToken {
    pub id: Option<String>,
    pub width: Length,
    pub height: Length,
}

#[derive(Clone, Debug)]
pub struct TextEditorToken<Message> {
    pub id: Option<String>,
    pub text: String,
    pub placeholder: Option<String>,
    pub min_height: Option<u16>,
    pub max_height: Option<u16>,
    pub text_style: TextStyle,
    pub chrome: TextEditorChrome,
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
    pub width: Length,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct CommandBarToken<Message> {
    pub id: Option<String>,
    pub items: Vec<View<Message>>,
    pub compact: bool,
    pub width: Length,
    pub align: Alignment,
    pub distribution: LayoutDistribution,
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
    pub distribution: LayoutDistribution,
    pub style: FluentStyle,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct AdaptiveSwitchToken<Message> {
    pub id: Option<String>,
    pub breakpoint_width: u16,
    pub wide: Box<View<Message>>,
    pub narrow: Box<View<Message>>,
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
    pub icon: Option<IconToken>,
    pub metadata: Option<String>,
    pub pending_hint: Option<String>,
    pub expanded: bool,
    pub toggleable: bool,
    pub dimmed: bool,
    pub status: ResultStatus,
}

impl ResultItem {
    pub fn new(id: impl Into<String>, title: impl Into<String>, body: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            title: title.into(),
            body: body.into(),
            icon: None,
            metadata: None,
            pending_hint: None,
            expanded: true,
            toggleable: true,
            dimmed: false,
            status: ResultStatus::Ready,
        }
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn metadata(mut self, metadata: impl Into<String>) -> Self {
        self.metadata = Some(metadata.into());
        self
    }

    pub fn pending_hint(mut self, hint: impl Into<String>) -> Self {
        self.pending_hint = Some(hint.into());
        self
    }

    pub fn expanded(mut self, expanded: bool) -> Self {
        self.expanded = expanded;
        self
    }

    pub fn toggleable(mut self, toggleable: bool) -> Self {
        self.toggleable = toggleable;
        self
    }

    pub fn dimmed(mut self, dimmed: bool) -> Self {
        self.dimmed = dimmed;
        self
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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CollapseTransition {
    pub duration_ms: u16,
}

impl Default for CollapseTransition {
    fn default() -> Self {
        Self::result_box()
    }
}

impl CollapseTransition {
    pub const DEFAULT_TRACE_FRAME_MS: f32 = 1000.0 / 60.0;
    pub const RESULT_BOX_ANIMATION_MS: u16 = 100;
    pub const RESULT_BOX_BODY_TRANSLATION_DIP: f32 = 2.0;

    pub const fn result_box() -> Self {
        Self {
            duration_ms: Self::RESULT_BOX_ANIMATION_MS,
        }
    }

    pub const fn new(duration_ms: u16) -> Self {
        Self { duration_ms }
    }

    pub const fn transition(self) -> Transition {
        Transition::fluent_content(self.duration_ms)
    }

    pub const fn expand_transition(self) -> Transition {
        Transition::fluent_content(self.duration_ms)
    }

    pub const fn collapse_transition(self) -> Transition {
        Transition::fluent_content(self.duration_ms)
    }

    pub fn trace_result_box(
        self,
        direction: CollapseTraceDirection,
        body_height: f32,
        header_height: f32,
        item_spacing: f32,
    ) -> Vec<CollapseTraceSample> {
        self.trace_result_box_at_interval(
            direction,
            body_height,
            header_height,
            item_spacing,
            Self::DEFAULT_TRACE_FRAME_MS,
        )
    }

    pub fn trace_result_box_at_interval(
        self,
        direction: CollapseTraceDirection,
        body_height: f32,
        header_height: f32,
        item_spacing: f32,
        frame_interval_ms: f32,
    ) -> Vec<CollapseTraceSample> {
        let duration_ms = f32::from(self.duration_ms);
        let frame_interval_ms = if frame_interval_ms.is_finite() && frame_interval_ms > 0.0 {
            frame_interval_ms
        } else {
            Self::DEFAULT_TRACE_FRAME_MS
        };
        let frame_count = if self.duration_ms == 0 {
            1
        } else {
            (duration_ms / frame_interval_ms).ceil().max(1.0) as usize
        };
        let body_height = body_height.max(0.0);
        let header_height = header_height.max(0.0);
        let item_spacing = item_spacing.max(0.0);
        let mut samples = Vec::with_capacity(frame_count + 1);

        for frame in 0..=frame_count {
            let elapsed_ms = if frame == frame_count {
                duration_ms
            } else {
                (frame as f32 * frame_interval_ms).min(duration_ms)
            };
            let transition = match direction {
                CollapseTraceDirection::Expand => self.expand_transition(),
                CollapseTraceDirection::Collapse => self.collapse_transition(),
            };
            let expanded_progress = match direction {
                CollapseTraceDirection::Expand => transition.progress_at(elapsed_ms),
                CollapseTraceDirection::Collapse => 1.0 - transition.progress_at(elapsed_ms),
            }
            .clamp(0.0, 1.0);
            let visible_body_height = body_height * expanded_progress;
            let box_height = header_height + visible_body_height;

            samples.push(CollapseTraceSample {
                frame,
                elapsed_ms,
                expanded_progress,
                visible_body_height,
                box_height,
                next_box_top: box_height + item_spacing,
                body_translate_y: -Self::RESULT_BOX_BODY_TRANSLATION_DIP
                    * (1.0 - expanded_progress),
            });
        }

        samples
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CollapseTraceDirection {
    Expand,
    Collapse,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CollapseTraceSample {
    pub frame: usize,
    pub elapsed_ms: f32,
    pub expanded_progress: f32,
    pub visible_body_height: f32,
    pub box_height: f32,
    pub next_box_top: f32,
    pub body_translate_y: f32,
}

#[derive(Clone, Debug)]
pub struct ResultCardToken<Message> {
    pub id: Option<String>,
    pub item: ResultItem,
    pub copy_action: Action<Message>,
    pub speak_action: Action<Message>,
    pub replace_action: Action<Message>,
    pub retry_action: Action<Message>,
    pub toggle_action: Action<Message>,
    pub collapse_transition: CollapseTransition,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct ResultListToken<Message> {
    pub id: Option<String>,
    pub items: Vec<ResultItem>,
    pub copy_action: Action<Message>,
    pub speak_action: Action<Message>,
    pub replace_action: Action<Message>,
    pub retry_action: Action<Message>,
    pub toggle_action: Action<Message>,
    pub virtualized: bool,
    pub collapse_transition: CollapseTransition,
    pub a11y: A11yHint,
}

#[deprecated(note = "use ResultCardToken")]
pub type ServiceResultCardToken<Message> = ResultCardToken<Message>;

#[deprecated(note = "use ResultListToken")]
pub type ServiceResultListToken<Message> = ResultListToken<Message>;

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

pub fn flyout_button<Message>(label: impl Into<String>) -> FlyoutButtonBuilder<Message> {
    FlyoutButtonBuilder {
        id: None,
        label: label.into(),
        icon: None,
        tooltip: None,
        selected: None,
        items: Vec::new(),
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn title_bar<Message>(title: impl Into<String>) -> TitleBarBuilder<Message> {
    TitleBarBuilder {
        id: None,
        title: title.into(),
        subtitle: None,
        icon: None,
        commands: Vec::new(),
        show_caption_controls: true,
        minimize_action: Action::None,
        toggle_maximize_action: Action::None,
        close_action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn status_badge<Message>(
    label: impl Into<String>,
    severity: ValidationSeverity,
) -> StatusBadgeBuilder<Message> {
    StatusBadgeBuilder {
        id: None,
        label: label.into(),
        severity,
        icon: None,
        a11y: A11yHint::default(),
        _message: std::marker::PhantomData,
    }
}

pub fn progress_ring<Message>() -> ProgressRingBuilder<Message> {
    ProgressRingBuilder {
        id: None,
        active: true,
        size: 16,
        label: None,
        a11y: A11yHint::default(),
        _message: std::marker::PhantomData,
    }
}

pub fn busy_overlay<Message, Child>(content: Child) -> BusyOverlayBuilder<Message>
where
    Child: IntoView<Message>,
{
    BusyOverlayBuilder {
        id: None,
        active: false,
        opacity: 0.72,
        blocks_input: true,
        label: None,
        content: Box::new(content.into_view()),
        a11y: A11yHint::default(),
    }
}

pub fn card<Message>(title: impl Into<String>) -> CardBuilder<Message> {
    CardBuilder {
        id: None,
        title: title.into(),
        description: None,
        icon: None,
        kind: CardKind::Surface,
        content: None,
        trailing: Vec::new(),
        a11y: A11yHint::default(),
    }
}

pub fn spacer<Message>() -> SpacerBuilder<Message> {
    SpacerBuilder {
        id: None,
        width: Length::Fill,
        height: Length::Shrink,
        _message: std::marker::PhantomData,
    }
}

pub fn text_editor<Message>(text: impl Into<String>) -> TextEditorBuilder<Message> {
    TextEditorBuilder {
        id: None,
        text: text.into(),
        placeholder: None,
        min_height: None,
        max_height: None,
        text_style: TextStyle::Body,
        chrome: TextEditorChrome::Standard,
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
        width: Length::Shrink,
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
        width: Length::Shrink,
        align: Alignment::Center,
        distribution: LayoutDistribution::Start,
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

pub fn adaptive_switch<Message, Wide, Narrow>(
    breakpoint_width: u16,
    wide: Wide,
    narrow: Narrow,
) -> AdaptiveSwitchBuilder<Message>
where
    Wide: IntoView<Message>,
    Narrow: IntoView<Message>,
{
    AdaptiveSwitchBuilder {
        id: None,
        breakpoint_width,
        wide: Box::new(wide.into_view()),
        narrow: Box::new(narrow.into_view()),
        a11y: A11yHint::default(),
    }
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

pub fn result_card<Message>(item: ResultItem) -> ResultCardBuilder<Message> {
    ResultCardBuilder {
        id: None,
        item,
        copy_action: Action::None,
        speak_action: Action::None,
        replace_action: Action::None,
        retry_action: Action::None,
        toggle_action: Action::None,
        collapse_transition: CollapseTransition::default(),
        a11y: A11yHint::default(),
    }
}

pub fn result_list<Message>(
    items: impl IntoIterator<Item = ResultItem>,
) -> ResultListBuilder<Message> {
    ResultListBuilder {
        id: None,
        items: items.into_iter().collect(),
        copy_action: Action::None,
        speak_action: Action::None,
        replace_action: Action::None,
        retry_action: Action::None,
        toggle_action: Action::None,
        virtualized: true,
        collapse_transition: CollapseTransition::default(),
        a11y: A11yHint::default(),
    }
}

pub fn service_result_card<Message>(item: ResultItem) -> ResultCardBuilder<Message> {
    result_card(item)
}

pub fn service_result_list<Message>(
    items: impl IntoIterator<Item = ResultItem>,
) -> ResultListBuilder<Message> {
    result_list(items)
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
pub struct TitleBarBuilder<Message> {
    id: Option<String>,
    title: String,
    subtitle: Option<String>,
    icon: Option<IconToken>,
    commands: Vec<View<Message>>,
    show_caption_controls: bool,
    minimize_action: Action<Message>,
    toggle_maximize_action: Action<Message>,
    close_action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> TitleBarBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn subtitle(mut self, subtitle: impl Into<String>) -> Self {
        self.subtitle = Some(subtitle.into());
        self
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn commands(mut self, commands: impl IntoChildren<Message>) -> Self {
        self.commands = commands.into_children();
        self
    }

    pub fn caption_controls(mut self, visible: bool) -> Self {
        self.show_caption_controls = visible;
        self
    }

    pub fn on_minimize(mut self, message: Message) -> Self {
        self.minimize_action = Action::Message(message);
        self
    }

    pub fn on_toggle_maximize(mut self, message: Message) -> Self {
        self.toggle_maximize_action = Action::Message(message);
        self
    }

    pub fn on_close(mut self, message: Message) -> Self {
        self.close_action = Action::Message(message);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for TitleBarBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::TitleBar(TitleBarToken {
            id: self.id,
            title: self.title,
            subtitle: self.subtitle,
            icon: self.icon,
            commands: self.commands,
            show_caption_controls: self.show_caption_controls,
            minimize_action: self.minimize_action,
            toggle_maximize_action: self.toggle_maximize_action,
            close_action: self.close_action,
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

    pub fn chip(mut self) -> Self {
        self.kind = ButtonKind::Chip;
        self
    }

    pub fn icon_only(mut self) -> Self {
        self.kind = ButtonKind::Icon;
        self
    }

    pub fn result_action(mut self) -> Self {
        self.kind = ButtonKind::ResultAction;
        self
    }

    pub fn floating_action(mut self) -> Self {
        self.kind = ButtonKind::FloatingAction;
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
pub struct FlyoutButtonBuilder<Message> {
    id: Option<String>,
    label: String,
    icon: Option<IconToken>,
    tooltip: Option<String>,
    selected: Option<String>,
    items: Vec<FlyoutMenuItem>,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> FlyoutButtonBuilder<Message> {
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

    pub fn selected(mut self, selected: impl Into<String>) -> Self {
        self.selected = Some(selected.into());
        self
    }

    pub fn items(mut self, items: impl IntoIterator<Item = FlyoutMenuItem>) -> Self {
        self.items = items.into_iter().collect();
        self
    }

    pub fn item(mut self, item: FlyoutMenuItem) -> Self {
        self.items.push(item);
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

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
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

impl<Message> IntoView<Message> for FlyoutButtonBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::FlyoutButton(FlyoutButtonToken {
            id: self.id,
            label: self.label,
            icon: self.icon,
            tooltip: self.tooltip,
            selected: self.selected,
            items: self.items,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct StatusBadgeBuilder<Message> {
    id: Option<String>,
    label: String,
    severity: ValidationSeverity,
    icon: Option<IconToken>,
    a11y: A11yHint,
    _message: std::marker::PhantomData<Message>,
}

impl<Message> StatusBadgeBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for StatusBadgeBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::StatusBadge(StatusBadgeToken {
            id: self.id,
            label: self.label,
            severity: self.severity,
            icon: self.icon,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ProgressRingBuilder<Message> {
    id: Option<String>,
    active: bool,
    size: u16,
    label: Option<String>,
    a11y: A11yHint,
    _message: std::marker::PhantomData<Message>,
}

impl<Message> ProgressRingBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn active(mut self, active: bool) -> Self {
        self.active = active;
        self
    }

    pub fn size(mut self, size: u16) -> Self {
        self.size = size;
        self
    }

    pub fn label(mut self, label: impl Into<String>) -> Self {
        self.label = Some(label.into());
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for ProgressRingBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ProgressRing(ProgressRingToken {
            id: self.id,
            active: self.active,
            size: self.size,
            label: self.label,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct BusyOverlayBuilder<Message> {
    id: Option<String>,
    active: bool,
    opacity: f32,
    blocks_input: bool,
    label: Option<String>,
    content: Box<View<Message>>,
    a11y: A11yHint,
}

impl<Message> BusyOverlayBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn active(mut self, active: bool) -> Self {
        self.active = active;
        self
    }

    pub fn opacity(mut self, opacity: f32) -> Self {
        self.opacity = opacity.clamp(0.0, 1.0);
        self
    }

    pub fn blocks_input(mut self, blocks_input: bool) -> Self {
        self.blocks_input = blocks_input;
        self
    }

    pub fn label(mut self, label: impl Into<String>) -> Self {
        self.label = Some(label.into());
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for BusyOverlayBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::BusyOverlay(BusyOverlayToken {
            id: self.id,
            active: self.active,
            opacity: self.opacity,
            blocks_input: self.blocks_input,
            label: self.label,
            content: self.content,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct CardBuilder<Message> {
    id: Option<String>,
    title: String,
    description: Option<String>,
    icon: Option<IconToken>,
    kind: CardKind,
    content: Option<Box<View<Message>>>,
    trailing: Vec<View<Message>>,
    a11y: A11yHint,
}

impl<Message> CardBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn description(mut self, description: impl Into<String>) -> Self {
        self.description = Some(description.into());
        self
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn kind(mut self, kind: CardKind) -> Self {
        self.kind = kind;
        self
    }

    pub fn content(mut self, content: impl IntoView<Message>) -> Self {
        self.content = Some(Box::new(content.into_view()));
        self
    }

    pub fn trailing(mut self, trailing: impl IntoChildren<Message>) -> Self {
        self.trailing = trailing.into_children();
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for CardBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Card(CardToken {
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
pub struct SpacerBuilder<Message> {
    id: Option<String>,
    width: Length,
    height: Length,
    _message: std::marker::PhantomData<Message>,
}

impl<Message> SpacerBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn width(mut self, width: Length) -> Self {
        self.width = width;
        self
    }

    pub fn height(mut self, height: Length) -> Self {
        self.height = height;
        self
    }
}

impl<Message> IntoView<Message> for SpacerBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Spacer(SpacerToken {
            id: self.id,
            width: self.width,
            height: self.height,
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
    text_style: TextStyle,
    chrome: TextEditorChrome,
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

    pub fn text_style(mut self, style: TextStyle) -> Self {
        self.text_style = style;
        self
    }

    pub fn chrome(mut self, chrome: TextEditorChrome) -> Self {
        self.chrome = chrome;
        self
    }

    pub fn frameless(mut self) -> Self {
        self.chrome = TextEditorChrome::Frameless;
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
            text_style: self.text_style,
            chrome: self.chrome,
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
    width: Length,
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

    pub fn width(mut self, width: Length) -> Self {
        self.width = width;
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
            width: self.width,
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
    width: Length,
    align: Alignment,
    distribution: LayoutDistribution,
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

    pub fn width(mut self, width: Length) -> Self {
        self.width = width;
        self
    }

    pub fn align(mut self, align: Alignment) -> Self {
        self.align = align;
        self
    }

    pub fn distribution(mut self, distribution: LayoutDistribution) -> Self {
        self.distribution = distribution;
        self
    }

    pub fn space_between(mut self) -> Self {
        self.distribution = LayoutDistribution::SpaceBetween;
        self.width = Length::Fill;
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
            width: self.width,
            align: self.align,
            distribution: self.distribution,
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
    distribution: LayoutDistribution,
    style: FluentStyle,
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
            distribution: LayoutDistribution::Start,
            style: FluentStyle::new(),
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

    pub fn distribution(mut self, value: LayoutDistribution) -> Self {
        self.distribution = value;
        self
    }

    pub fn space_between(mut self) -> Self {
        self.distribution = LayoutDistribution::SpaceBetween;
        self.width = Length::Fill;
        self
    }

    pub fn tw(mut self, classes: impl AsRef<str>) -> Self {
        let classes = classes.as_ref();
        self.style.extend(classes);

        for class in classes.split_whitespace() {
            if let Some(value) = class.strip_prefix("p-").and_then(utility_scale) {
                self.padding = value;
            } else if let Some(value) = class.strip_prefix("gap-").and_then(utility_scale) {
                self.spacing = value;
            } else {
                match class {
                    "w-full" | "w-fill" => self.width = Length::Fill,
                    "w-fit" | "w-auto" => self.width = Length::Shrink,
                    "h-full" | "h-fill" => self.height = Length::Fill,
                    "h-fit" | "h-auto" => self.height = Length::Shrink,
                    "items-start" => self.align = Alignment::Start,
                    "items-center" => self.align = Alignment::Center,
                    "items-end" => self.align = Alignment::End,
                    "items-stretch" => self.align = Alignment::Stretch,
                    "justify-between" | "space-between" => {
                        self.distribution = LayoutDistribution::SpaceBetween;
                        self.width = Length::Fill;
                    }
                    _ => {
                        if let Some(value) = class.strip_prefix("w-").and_then(utility_scale) {
                            self.width = Length::Fixed(value);
                        } else if let Some(value) = class.strip_prefix("h-").and_then(utility_scale)
                        {
                            self.height = Length::Fixed(value);
                        }
                    }
                }
            }
        }

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
            distribution: self.distribution,
            style: self.style,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct AdaptiveSwitchBuilder<Message> {
    id: Option<String>,
    breakpoint_width: u16,
    wide: Box<View<Message>>,
    narrow: Box<View<Message>>,
    a11y: A11yHint,
}

impl<Message> AdaptiveSwitchBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn breakpoint_width(mut self, breakpoint_width: u16) -> Self {
        self.breakpoint_width = breakpoint_width;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for AdaptiveSwitchBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::AdaptiveSwitch(AdaptiveSwitchToken {
            id: self.id,
            breakpoint_width: self.breakpoint_width,
            wide: self.wide,
            narrow: self.narrow,
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
pub struct ResultCardBuilder<Message> {
    id: Option<String>,
    item: ResultItem,
    copy_action: Action<Message>,
    speak_action: Action<Message>,
    replace_action: Action<Message>,
    retry_action: Action<Message>,
    toggle_action: Action<Message>,
    collapse_transition: CollapseTransition,
    a11y: A11yHint,
}

impl<Message> ResultCardBuilder<Message> {
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

    pub fn on_replace(mut self, message: Message) -> Self {
        self.replace_action = Action::Message(message);
        self
    }

    pub fn on_retry(mut self, message: Message) -> Self {
        self.retry_action = Action::Message(message);
        self
    }

    pub fn on_toggle(mut self, map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        self.toggle_action = Action::selection_input(map);
        self
    }

    pub fn collapse_transition(mut self, transition: CollapseTransition) -> Self {
        self.collapse_transition = transition;
        self
    }

    pub fn collapse_transition_ms(mut self, duration_ms: u16) -> Self {
        self.collapse_transition = CollapseTransition::new(duration_ms);
        self
    }
}

impl<Message> IntoView<Message> for ResultCardBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ResultCard(ResultCardToken {
            id: self.id,
            item: self.item,
            copy_action: self.copy_action,
            speak_action: self.speak_action,
            replace_action: self.replace_action,
            retry_action: self.retry_action,
            toggle_action: self.toggle_action,
            collapse_transition: self.collapse_transition,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ResultListBuilder<Message> {
    id: Option<String>,
    items: Vec<ResultItem>,
    copy_action: Action<Message>,
    speak_action: Action<Message>,
    replace_action: Action<Message>,
    retry_action: Action<Message>,
    toggle_action: Action<Message>,
    virtualized: bool,
    collapse_transition: CollapseTransition,
    a11y: A11yHint,
}

impl<Message> ResultListBuilder<Message> {
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

    pub fn on_replace(mut self, message: Message) -> Self {
        self.replace_action = Action::Message(message);
        self
    }

    pub fn on_retry(mut self, message: Message) -> Self {
        self.retry_action = Action::Message(message);
        self
    }

    pub fn on_toggle(mut self, map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        self.toggle_action = Action::selection_input(map);
        self
    }

    pub fn virtualized(mut self, enabled: bool) -> Self {
        self.virtualized = enabled;
        self
    }

    pub fn collapse_transition(mut self, transition: CollapseTransition) -> Self {
        self.collapse_transition = transition;
        self
    }

    pub fn collapse_transition_ms(mut self, duration_ms: u16) -> Self {
        self.collapse_transition = CollapseTransition::new(duration_ms);
        self
    }
}

impl<Message> IntoView<Message> for ResultListBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ResultList(ResultListToken {
            id: self.id,
            items: self.items,
            copy_action: self.copy_action,
            speak_action: self.speak_action,
            replace_action: self.replace_action,
            retry_action: self.retry_action,
            toggle_action: self.toggle_action,
            virtualized: self.virtualized,
            collapse_transition: self.collapse_transition,
            a11y: self.a11y,
        }))
    }
}

#[deprecated(note = "use ResultCardBuilder")]
pub type ServiceResultCardBuilder<Message> = ResultCardBuilder<Message>;

#[deprecated(note = "use ResultListBuilder")]
pub type ServiceResultListBuilder<Message> = ResultListBuilder<Message>;

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

    #[test]
    fn row_and_command_bar_can_space_children_between_edges() {
        let row_view: View<Msg> = row((text("Left"), text("Right")))
            .space_between()
            .into_view();
        let command_bar = command_bar((
            button("Cancel").on_press(Msg::Submit),
            primary_button("Save").on_press(Msg::Submit),
        ))
        .space_between()
        .into_view();

        match row_view.token() {
            ViewToken::Layout(layout) => {
                assert_eq!(layout.width, Length::Fill);
                assert_eq!(layout.distribution, LayoutDistribution::SpaceBetween);
            }
            _ => panic!("expected row layout"),
        }

        match command_bar.token() {
            ViewToken::CommandBar(command_bar) => {
                assert_eq!(command_bar.width, Length::Fill);
                assert_eq!(command_bar.distribution, LayoutDistribution::SpaceBetween);
            }
            _ => panic!("expected command bar"),
        }
    }

    #[test]
    fn collapse_transition_uses_winui_result_box_motion_defaults() {
        let transition = CollapseTransition::default();

        assert_eq!(
            transition.duration_ms,
            CollapseTransition::RESULT_BOX_ANIMATION_MS
        );
        assert_eq!(
            transition.expand_transition(),
            Transition::fluent_content(100)
        );
        assert_eq!(
            transition.collapse_transition(),
            Transition::fluent_content(100)
        );
    }

    #[test]
    fn result_box_trace_exposes_responsive_winui_shape() {
        let trace = CollapseTransition::default().trace_result_box(
            CollapseTraceDirection::Expand,
            50.0,
            30.0,
            8.0,
        );

        assert!(trace.len() >= 7);
        assert_trace_monotonic(&trace, true);
        assert_eq!(trace.first().unwrap().visible_body_height, 0.0);
        assert_eq!(trace.last().unwrap().visible_body_height, 50.0);

        let first_moving_frame = trace
            .iter()
            .find(|sample| sample.elapsed_ms > 0.0)
            .expect("trace must contain an animated frame");
        assert!(first_moving_frame.visible_body_height > 2.0);
        assert!(first_moving_frame.visible_body_height < 6.5);

        let around_50ms = nearest_elapsed(&trace, 50.0);
        assert!(around_50ms.visible_body_height > 34.0);

        let around_83ms = nearest_elapsed(&trace, 83.0);
        assert!(around_83ms.visible_body_height > 48.0);
        assert!(around_83ms.body_translate_y.abs() < 0.1);
        assert!(trace.last().unwrap().elapsed_ms <= 100.0);
    }

    #[test]
    fn result_box_collapse_trace_moves_related_boxes_without_jumps() {
        let trace = CollapseTransition::default().trace_result_box(
            CollapseTraceDirection::Collapse,
            50.0,
            30.0,
            8.0,
        );

        assert!(trace.len() >= 7);
        assert_trace_monotonic(&trace, false);
        assert_eq!(trace.first().unwrap().next_box_top, 88.0);
        assert_eq!(trace.last().unwrap().next_box_top, 38.0);

        let first_moving_frame = trace
            .iter()
            .find(|sample| sample.elapsed_ms > 0.0)
            .expect("trace must contain an animated frame");
        assert!(first_moving_frame.next_box_top > 80.0);

        let around_50ms = nearest_elapsed(&trace, 50.0);
        assert!(around_50ms.next_box_top < 55.0);
        assert!(trace.last().unwrap().elapsed_ms <= 100.0);
    }

    fn nearest_elapsed(samples: &[CollapseTraceSample], elapsed_ms: f32) -> CollapseTraceSample {
        samples
            .iter()
            .copied()
            .min_by(|left, right| {
                (left.elapsed_ms - elapsed_ms)
                    .abs()
                    .total_cmp(&(right.elapsed_ms - elapsed_ms).abs())
            })
            .expect("trace must have samples")
    }

    fn assert_trace_monotonic(samples: &[CollapseTraceSample], increasing: bool) {
        for pair in samples.windows(2) {
            if increasing {
                assert!(pair[1].expanded_progress >= pair[0].expanded_progress);
                assert!(pair[1].next_box_top >= pair[0].next_box_top);
            } else {
                assert!(pair[1].expanded_progress <= pair[0].expanded_progress);
                assert!(pair[1].next_box_top <= pair[0].next_box_top);
            }
        }
    }
}
