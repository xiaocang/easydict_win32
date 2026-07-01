use std::fmt;
use std::sync::Arc;

use crate::a11y::A11yHint;
use crate::action::Action;
use crate::command::CommandToken;
use crate::icon::IconToken;
use crate::motion::Transition;
use crate::platform::{TrayMenu, TrayMenuItem, TrayMenuPresenterStyle};
use crate::state::{ControlState, ValidationSeverity, ValidationState};
use crate::style::{utility_scale, FluentStyle};

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum TooltipPlacement {
    #[default]
    Top,
    Bottom,
    Left,
    Right,
}

#[derive(Clone, Debug)]
pub struct View<Message> {
    token: ViewToken<Message>,
    /// Optional hover tooltip for *any* element (WinUI `ToolTipService.ToolTip`).
    /// Lives on the wrapper rather than per-token so it applies uniformly to
    /// every control, not just `button`.
    tooltip: Option<String>,
    tooltip_placement: TooltipPlacement,
}

impl<Message> View<Message> {
    pub fn new(token: ViewToken<Message>) -> Self {
        Self {
            token,
            tooltip: None,
            tooltip_placement: TooltipPlacement::default(),
        }
    }

    pub fn token(&self) -> &ViewToken<Message> {
        &self.token
    }

    pub fn into_token(self) -> ViewToken<Message> {
        self.token
    }

    /// Attach a hover tooltip to this view. Works on any element, mirroring
    /// WinUI's attached `ToolTipService.ToolTip` property.
    pub fn tooltip(mut self, tooltip: impl Into<String>) -> Self {
        self.tooltip = Some(tooltip.into());
        self
    }

    /// Attach a hover tooltip with an explicit placement.
    pub fn tooltip_at(mut self, tooltip: impl Into<String>, placement: TooltipPlacement) -> Self {
        self.tooltip = Some(tooltip.into());
        self.tooltip_placement = placement;
        self
    }

    /// The tooltip text attached via [`View::tooltip`], if any.
    pub fn tooltip_text(&self) -> Option<&str> {
        self.tooltip.as_deref()
    }

    /// The tooltip placement attached via [`View::tooltip_at`].
    pub fn tooltip_placement(&self) -> TooltipPlacement {
        self.tooltip_placement
    }

    pub fn text_margin(mut self, margin: Edges) -> Self {
        if let ViewToken::Text(token) = &mut self.token {
            token.margin = margin;
        }
        self
    }

    pub fn text_align_x(mut self, align: Alignment) -> Self {
        if let ViewToken::Text(token) = &mut self.token {
            token.align_x = align;
        }
        self
    }

    pub fn text_align_y(mut self, align: Alignment) -> Self {
        if let ViewToken::Text(token) = &mut self.token {
            token.align_y = align;
        }
        self
    }

    pub fn text_align(mut self, x: Alignment, y: Alignment) -> Self {
        if let ViewToken::Text(token) = &mut self.token {
            token.align_x = x;
            token.align_y = y;
        }
        self
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
    RichText(RichTextToken<Message>),
    Button(ButtonToken<Message>),
    ToggleButton(ToggleButtonToken<Message>),
    SplitButton(SplitButtonToken<Message>),
    FlyoutButton(FlyoutButtonToken<Message>),
    StatusBadge(StatusBadgeToken),
    InfoBar(InfoBarToken),
    ProgressRing(ProgressRingToken),
    ProgressBar(ProgressBarToken),
    BusyOverlay(BusyOverlayToken<Message>),
    Card(CardToken<Message>),
    Spacer(SpacerToken),
    TextEditor(TextEditorToken<Message>),
    CheckBox(CheckBoxToken<Message>),
    RadioGroup(RadioGroupToken<Message>),
    ToggleSwitch(ToggleSwitchToken<Message>),
    Slider(SliderToken<Message>),
    NumberBox(NumberBoxToken<Message>),
    AutoSuggestBox(AutoSuggestBoxToken<Message>),
    ComboBox(ComboBoxToken<Message>),
    CommandBar(CommandBarToken<Message>),
    NavigationView(NavigationViewToken<Message>),
    Dialog(DialogToken<Message>),
    Layout(LayoutToken<Message>),
    Grid(GridToken<Message>),
    Border(BorderToken<Message>),
    Viewbox(ViewboxToken<Message>),
    TabView(TabViewToken<Message>),
    TreeView(TreeViewToken<Message>),
    Wrap(WrapToken<Message>),
    Flyout(FlyoutToken<Message>),
    Overlay(OverlayToken<Message>),
    AdaptiveSwitch(AdaptiveSwitchToken<Message>),
    Lazy(LazyToken<Message>),
    ScrollView(ScrollViewToken<Message>),
    Expander(ExpanderToken<Message>),
    SettingsRow(SettingsRowToken<Message>),
    ResultCard(ResultCardToken<Message>),
    ResultList(ResultListToken<Message>),
    ListView(ListViewToken<Message>),
    TrayMenu(TrayMenuToken<Message>),
    PointerRegion(PointerRegionToken<Message>),
    CaptureOverlay(CaptureOverlayToken),
    Image(ImageToken),
    WebView(WebViewToken),
    Custom(CustomToken<Message>),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Length {
    Shrink,
    Fill,
    FillPortion(u16),
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

/// Per-side outer spacing (margin) in device-independent pixels.
///
/// Modeled separately from `padding` (which is a uniform inner inset) so that
/// Tailwind-style `m-*`/`mx-*`/`my-*` classes map to a real, rendered offset
/// rather than being silently dropped.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct Edges {
    pub top: u16,
    pub right: u16,
    pub bottom: u16,
    pub left: u16,
}

impl Edges {
    pub const ZERO: Self = Self {
        top: 0,
        right: 0,
        bottom: 0,
        left: 0,
    };

    pub fn is_zero(self) -> bool {
        self == Self::ZERO
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextStyle {
    Caption,
    CaptionSmall,
    Body,
    BodyLarge,
    BodyStrong,
    Success,
    Warning,
    SectionTitle,
    Subtitle,
    Title,
    TitleLarge,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextWrapping {
    Word,
    None,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ButtonKind {
    Standard,
    Primary,
    PrimaryRound,
    Chip,
    Subtle,
    Link,
    Tile,
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
pub enum StatusBadgeKind {
    Text,
    Count,
    Dot,
}

impl StatusBadgeKind {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Text => "text",
            Self::Count => "count",
            Self::Dot => "dot",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ListContractKind {
    GenericListView,
    TranslationResultList,
}

impl ListContractKind {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::GenericListView => "generic-list-view",
            Self::TranslationResultList => "translation-result-list",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CustomControlKind {
    Custom,
    ControlTemplate,
}

impl CustomControlKind {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Custom => "custom",
            Self::ControlTemplate => "control-template",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CardKind {
    Surface,
    Elevated,
    Expander,
    FloatingInput,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextEditorChrome {
    Standard,
    Frameless,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TextEditorKey {
    Enter,
    Tab,
    Escape,
    ArrowUp,
    ArrowDown,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TextEditorKeyModifiers {
    pub shift: bool,
    pub control: bool,
    pub alt: bool,
    pub logo: bool,
}

impl TextEditorKeyModifiers {
    pub const fn none() -> Self {
        Self {
            shift: false,
            control: false,
            alt: false,
            logo: false,
        }
    }

    pub const fn shift() -> Self {
        Self {
            shift: true,
            control: false,
            alt: false,
            logo: false,
        }
    }

    pub const fn control() -> Self {
        Self {
            shift: false,
            control: true,
            alt: false,
            logo: false,
        }
    }

    pub const fn alt() -> Self {
        Self {
            shift: false,
            control: false,
            alt: true,
            logo: false,
        }
    }

    pub const fn logo() -> Self {
        Self {
            shift: false,
            control: false,
            alt: false,
            logo: true,
        }
    }
}

#[derive(Clone, Debug)]
pub struct TextEditorKeyBinding<Message> {
    pub key: TextEditorKey,
    pub modifiers: TextEditorKeyModifiers,
    pub message: Message,
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
    pub drag_action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextToken {
    pub id: Option<String>,
    pub value: String,
    pub style: TextStyle,
    pub font_size: Option<u16>,
    pub width: Option<Length>,
    pub height: Option<Length>,
    pub margin: Edges,
    pub align_x: Alignment,
    pub align_y: Alignment,
    pub wrapping: TextWrapping,
    pub selectable: bool,
    pub a11y: A11yHint,
}

/// Styling of a single inline run within a [`RichTextToken`].
#[derive(Clone, Copy, Debug, Eq, PartialEq, Default)]
pub enum TextRunKind {
    #[default]
    Plain,
    Bold,
    Italic,
    /// A hyperlink; carries an `href` and fires the rich text's link action.
    Link,
}

/// A single inline run of text (WinUI `RichTextBlock` `Run`/`Hyperlink`).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextRun {
    pub text: String,
    pub kind: TextRunKind,
    pub href: Option<String>,
}

impl TextRun {
    pub fn plain(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            kind: TextRunKind::Plain,
            href: None,
        }
    }

    pub fn bold(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            kind: TextRunKind::Bold,
            href: None,
        }
    }

    pub fn italic(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            kind: TextRunKind::Italic,
            href: None,
        }
    }

    /// A hyperlink run. The `href` is the value passed to the rich text's
    /// `on_link` handler when clicked.
    pub fn link(text: impl Into<String>, href: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            kind: TextRunKind::Link,
            href: Some(href.into()),
        }
    }
}

/// Inline rich text composed of styled [`TextRun`]s (WinUI `RichTextBlock`):
/// the base for dictionary entries and MDX rich documents.
#[derive(Clone, Debug)]
pub struct RichTextToken<Message> {
    pub id: Option<String>,
    pub runs: Vec<TextRun>,
    pub style: TextStyle,
    pub wrapping: TextWrapping,
    /// Fired when a link run is clicked, with the run's `href` (`SelectionInput`).
    pub link_action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct ButtonToken<Message> {
    pub id: Option<String>,
    pub label: String,
    pub kind: ButtonKind,
    pub icon: Option<IconToken>,
    pub tooltip: Option<String>,
    pub width: Option<Length>,
    pub height: Option<Length>,
    pub padding: Option<Edges>,
    pub text_style: Option<TextStyle>,
    pub font_size: Option<u16>,
    pub margin: Edges,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

/// A button that holds an on/off pressed state (WinUI `ToggleButton`).
#[derive(Clone, Debug)]
pub struct ToggleButtonToken<Message> {
    pub id: Option<String>,
    pub label: String,
    pub icon: Option<IconToken>,
    pub pressed: bool,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

/// A two-part button: a primary action plus a dropdown menu (WinUI `SplitButton`).
#[derive(Clone, Debug)]
pub struct SplitButtonToken<Message> {
    pub id: Option<String>,
    pub label: String,
    pub icon: Option<IconToken>,
    pub items: Vec<FlyoutMenuItem>,
    pub open: bool,
    pub state: ControlState,
    /// Fired when the primary segment is pressed (`Message`).
    pub primary_action: Action<Message>,
    /// Fired when a menu item is chosen (`SelectionInput` with the item id).
    pub select_action: Action<Message>,
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
    pub min_width: Option<u16>,
    pub min_height: Option<u16>,
    pub padding: Option<Edges>,
    pub border_width: Option<u16>,
    pub radius: Option<u16>,
    pub align_y: Alignment,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct StatusBadgeToken {
    pub id: Option<String>,
    pub label: String,
    pub kind: StatusBadgeKind,
    pub count: Option<u32>,
    pub severity: ValidationSeverity,
    pub icon: Option<IconToken>,
    pub a11y: A11yHint,
}

impl StatusBadgeToken {
    pub fn automation_name(&self) -> String {
        match self.kind {
            StatusBadgeKind::Dot => format!("{:?} status", self.severity),
            StatusBadgeKind::Count => {
                format!(
                    "{} {:?} notifications",
                    self.count.unwrap_or_default(),
                    self.severity
                )
            }
            StatusBadgeKind::Text => self.label.clone(),
        }
    }
}

/// Fluent `InfoBar`-style status surface: a tinted box with a severity icon, a
/// bold title, and a wrapping message. Mirrors the WinUI `InfoBar` control used
/// for the local-AI provider panels (Phi Silica / Foundry Local / OpenVINO).
#[derive(Clone, Debug)]
pub struct InfoBarToken {
    pub id: Option<String>,
    pub title: String,
    pub message: String,
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

#[derive(Clone, Debug, PartialEq)]
pub struct ProgressBarToken {
    pub id: Option<String>,
    pub active: bool,
    pub value: Option<f32>,
    pub width: Length,
    pub height: u16,
    pub label: Option<String>,
    pub a11y: A11yHint,
}

impl ProgressBarToken {
    /// Determinate progress value normalized to WinUI's default `0..=100` range.
    pub fn normalized_value(&self) -> Option<f32> {
        self.value
    }
}

fn normalize_progress_bar_value(value: f32) -> Option<f32> {
    value.is_finite().then(|| value.clamp(0.0, 100.0))
}

#[derive(Clone, Debug)]
pub struct BusyOverlayToken<Message> {
    pub id: Option<String>,
    pub active: bool,
    pub opacity: f32,
    pub fade_transition_ms: u16,
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
    pub content_spacing: u16,
    pub margin: Edges,
    pub max_height: Option<u16>,
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
    pub width: Option<Length>,
    pub min_height: Option<u16>,
    pub max_height: Option<u16>,
    pub padding: Option<Edges>,
    pub text_style: TextStyle,
    pub chrome: TextEditorChrome,
    pub secure: bool,
    pub read_only: bool,
    pub state: ControlState,
    pub action: Action<Message>,
    pub key_bindings: Vec<TextEditorKeyBinding<Message>>,
    pub trailing_icon: Option<TextEditorTrailingIcon>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextEditorTrailingIcon {
    pub id: String,
    pub icon: IconToken,
    pub label: String,
    pub width: u16,
    pub height: u16,
    pub spacing: u16,
}

#[derive(Clone, Debug)]
pub struct ToggleSwitchToken<Message> {
    pub id: Option<String>,
    pub header: Option<String>,
    pub label: String,
    pub checked: bool,
    pub width: Option<Length>,
    pub height: Option<Length>,
    pub margin: Edges,
    pub align_y: Alignment,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct CheckBoxToken<Message> {
    pub id: Option<String>,
    pub label: String,
    pub checked: bool,
    /// Third (mixed) state, mirroring WinUI `CheckBox.IsThreeState`/`IsChecked == null`.
    /// When `true`, the box renders a dash glyph and `checked` is ignored visually.
    pub indeterminate: bool,
    pub label_italic: bool,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

/// Layout orientation for controls that can stack vertically or horizontally
/// (WinUI `Orientation`).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Orientation {
    Vertical,
    Horizontal,
}

/// A single option within a [`RadioGroupToken`].
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RadioOption {
    pub id: String,
    pub label: String,
    pub enabled: bool,
}

impl RadioOption {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            enabled: true,
        }
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }
}

/// A single-selection radio group (WinUI `RadioButtons`). One option is selected
/// at a time; selecting another fires the group's `SelectionInput` with the new
/// option's id.
#[derive(Clone, Debug)]
pub struct RadioGroupToken<Message> {
    pub id: Option<String>,
    pub header: Option<String>,
    pub options: Vec<RadioOption>,
    pub selected: Option<String>,
    pub orientation: Orientation,
    pub spacing: u16,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct SliderToken<Message> {
    pub id: Option<String>,
    pub value: f32,
    pub min: f32,
    pub max: f32,
    pub step: f32,
    pub width: Length,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

impl<Message> SliderToken<Message> {
    /// Whether the slider should expose its transient value preview state.
    pub fn preview_active(&self) -> bool {
        self.state.hovered || self.state.pressed || self.state.focused
    }
}

/// A numeric input with optional spin buttons and range/step (WinUI `NumberBox`).
#[derive(Clone, Debug)]
pub struct NumberBoxToken<Message> {
    pub id: Option<String>,
    pub value: f32,
    pub min: Option<f32>,
    pub max: Option<f32>,
    pub step: f32,
    pub header: Option<String>,
    pub placeholder: Option<String>,
    pub spin_buttons: bool,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

impl<Message> NumberBoxToken<Message> {
    /// Clamp a candidate value to the configured `[min, max]` range.
    pub fn clamp(&self, value: f32) -> f32 {
        clamp_number_box_value(value, self.min, self.max)
    }
}

fn clamp_number_box_value(value: f32, min: Option<f32>, max: Option<f32>) -> f32 {
    let mut value = if value.is_finite() {
        value
    } else {
        min.or(max).unwrap_or(0.0)
    };
    if let Some(min) = min {
        value = value.max(min);
    }
    if let Some(max) = max {
        value = value.min(max);
    }
    value
}

fn normalize_number_box_step(step: f32) -> f32 {
    if step.is_finite() && step > 0.0 {
        step
    } else {
        1.0
    }
}

/// A text box with as-you-type suggestions (WinUI `AutoSuggestBox`): used for
/// search and language pickers.
#[derive(Clone, Debug)]
pub struct AutoSuggestBoxToken<Message> {
    pub id: Option<String>,
    pub text: String,
    pub placeholder: Option<String>,
    pub header: Option<String>,
    /// Suggestion list shown beneath the box (already filtered by the app).
    pub suggestions: Vec<String>,
    /// Whether the suggestion list is open.
    pub open: bool,
    /// Zero-based suggestion highlighted by keyboard navigation, if any.
    pub highlighted_index: Option<usize>,
    pub width: Length,
    pub state: ControlState,
    /// Fired as the user types (`TextInput`).
    pub change_action: Action<Message>,
    /// Fired when a suggestion is chosen (`SelectionInput` with the suggestion text).
    pub submit_action: Action<Message>,
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
    pub placeholder: Option<String>,
    pub items: Vec<ComboBoxItem>,
    pub selected: Option<String>,
    pub width: Length,
    pub height: Length,
    pub state: ControlState,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

impl<Message> ComboBoxToken<Message> {
    /// The selected item, if the selected id still matches an item.
    pub fn selected_item(&self) -> Option<&ComboBoxItem> {
        let selected = self.selected.as_deref()?;
        self.items.iter().find(|item| item.id == selected)
    }
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

/// How the NavigationView pane is laid out (WinUI `NavigationViewPaneDisplayMode`).
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum PaneDisplayMode {
    /// Adapt between expanded/compact/minimal based on width.
    #[default]
    Auto,
    /// Always-expanded left pane.
    Left,
    /// Top horizontal menu bar.
    Top,
    /// Icon-only left rail.
    LeftCompact,
    /// Collapsed to a hamburger button.
    LeftMinimal,
}

#[derive(Clone, Debug)]
pub struct NavigationViewToken<Message> {
    pub id: Option<String>,
    pub selected: Option<String>,
    pub items: Vec<NavigationItem>,
    /// Items pinned to the bottom of the pane (WinUI `FooterMenuItems`).
    pub footer_items: Vec<NavigationItem>,
    pub content: Option<Box<View<Message>>>,
    pub pane_display_mode: PaneDisplayMode,
    /// Pane header text (WinUI `PaneHeader`).
    pub header: Option<String>,
    /// Whether the built-in settings entry is shown (WinUI `IsSettingsVisible`).
    pub settings_visible: bool,
    /// Whether the back button is shown (WinUI `IsBackButtonVisible`).
    pub back_button_visible: bool,
    /// Selection callback: receives the chosen item id (or the settings id).
    pub action: Action<Message>,
    /// Back-button callback (WinUI `BackRequested`).
    pub back_action: Action<Message>,
    pub a11y: A11yHint,
}

impl<Message> NavigationViewToken<Message> {
    /// The id reported through `action` when the built-in settings item is chosen.
    pub const SETTINGS_ID: &'static str = "settings";
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
    pub padding_edges: Option<Edges>,
    pub spacing: u16,
    pub width: Length,
    pub height: Length,
    pub max_width: Option<u16>,
    pub max_height: Option<u16>,
    pub center_x: bool,
    pub margin: Edges,
    pub align: Alignment,
    pub distribution: LayoutDistribution,
    pub style: FluentStyle,
    pub a11y: A11yHint,
}

/// A 2D grid layout, mirroring WinUI `Grid`. Tracks are declared with the
/// existing [`Length`] enum, which maps cleanly onto WinUI's sizing semantics:
/// `Length::Fixed` = absolute, `Length::FillPortion`/`Length::Fill` = star (`*`),
/// `Length::Shrink` = `Auto`. Children are placed by `(row, column)` with an
/// optional `(row_span, column_span)`.
#[derive(Clone, Debug)]
pub struct GridToken<Message> {
    pub id: Option<String>,
    pub rows: Vec<Length>,
    pub columns: Vec<Length>,
    pub row_spacing: u16,
    pub column_spacing: u16,
    pub padding: u16,
    pub padding_edges: Option<Edges>,
    pub width: Length,
    pub height: Length,
    pub align: Alignment,
    pub children: Vec<GridChild<Message>>,
    pub a11y: A11yHint,
}

/// A single placed child within a [`GridToken`].
#[derive(Clone, Debug)]
pub struct GridChild<Message> {
    pub row: u16,
    pub column: u16,
    pub row_span: u16,
    pub column_span: u16,
    pub view: View<Message>,
}

impl<Message> GridChild<Message> {
    pub fn new(row: u16, column: u16, view: impl IntoView<Message>) -> Self {
        Self {
            row,
            column,
            row_span: 1,
            column_span: 1,
            view: view.into_view(),
        }
    }

    /// Set the row and column span (WinUI `Grid.RowSpan`/`Grid.ColumnSpan`).
    pub fn span(mut self, row_span: u16, column_span: u16) -> Self {
        self.row_span = row_span.max(1);
        self.column_span = column_span.max(1);
        self
    }
}

/// A single-child container with rounded corners, an optional stroke, and an
/// optional surface fill (WinUI `Border`). Styling is theme-driven.
#[derive(Clone, Debug)]
pub struct BorderToken<Message> {
    pub id: Option<String>,
    pub content: Box<View<Message>>,
    pub corner_radius: u16,
    pub stroke_width: u16,
    /// Fill the interior with the theme surface color.
    pub filled: bool,
    pub padding: Edges,
    pub width: Length,
    pub height: Length,
    pub a11y: A11yHint,
}

/// A single-child container that uniformly scales its content to fit (WinUI
/// `Viewbox`).
#[derive(Clone, Debug)]
pub struct ViewboxToken<Message> {
    pub id: Option<String>,
    pub content: Box<View<Message>>,
    pub stretch: ImageStretch,
    pub width: Length,
    pub height: Length,
    pub a11y: A11yHint,
}

/// A single tab within a [`TabViewToken`].
#[derive(Clone, Debug)]
pub struct TabItem<Message> {
    pub id: String,
    pub header: String,
    pub closable: bool,
    pub close_a11y_name: Option<String>,
    pub content: View<Message>,
}

impl<Message> TabItem<Message> {
    pub fn new(
        id: impl Into<String>,
        header: impl Into<String>,
        content: impl IntoView<Message>,
    ) -> Self {
        Self {
            id: id.into(),
            header: header.into(),
            closable: false,
            close_a11y_name: None,
            content: content.into_view(),
        }
    }

    pub fn closable(mut self, closable: bool) -> Self {
        self.closable = closable;
        self
    }

    pub fn close_a11y_name(mut self, name: impl Into<String>) -> Self {
        self.close_a11y_name = Some(name.into());
        self
    }
}

/// A tabbed document/page container (WinUI `TabView`). The selected tab's
/// content is shown.
#[derive(Clone, Debug)]
pub struct TabViewToken<Message> {
    pub id: Option<String>,
    pub tabs: Vec<TabItem<Message>>,
    pub selected: Option<String>,
    /// Tab-selected callback (`SelectionInput` with the tab id).
    pub action: Action<Message>,
    /// Tab-close callback (`SelectionInput` with the tab id).
    pub close_action: Action<Message>,
    pub a11y: A11yHint,
}

/// A node in a [`TreeViewToken`].
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TreeNode {
    pub id: String,
    pub label: String,
    pub expanded: bool,
    pub children: Vec<TreeNode>,
}

impl TreeNode {
    pub fn leaf(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            expanded: false,
            children: Vec::new(),
        }
    }

    pub fn branch(
        id: impl Into<String>,
        label: impl Into<String>,
        children: impl IntoIterator<Item = TreeNode>,
    ) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            expanded: true,
            children: children.into_iter().collect(),
        }
    }

    pub fn expanded(mut self, expanded: bool) -> Self {
        self.expanded = expanded;
        self
    }
}

/// A hierarchical list (WinUI `TreeView`): dictionary/settings hierarchies and
/// long-document outlines.
#[derive(Clone, Debug)]
pub struct TreeViewToken<Message> {
    pub id: Option<String>,
    pub roots: Vec<TreeNode>,
    pub selected: Option<String>,
    /// Node-selected callback (`SelectionInput` with the node id).
    pub action: Action<Message>,
    /// Node expand/collapse-toggle callback (`SelectionInput` with the node id).
    pub toggle_action: Action<Message>,
    pub a11y: A11yHint,
}

/// A flow layout that arranges children into rows, wrapping to a new row after
/// `max_columns` items.
///
/// Mirrors WinUI `ItemsWrapGrid` with `MaximumRowsOrColumns`: at typical widths
/// the items fit on as few rows as the column cap allows. (Width-responsive
/// narrow reflow is a future backend enhancement; the token already carries the
/// column cap so the app-level API will not change.)
#[derive(Clone, Debug)]
pub struct WrapToken<Message> {
    pub id: Option<String>,
    pub children: Vec<View<Message>>,
    pub max_columns: u16,
    pub spacing: u16,
    pub run_spacing: u16,
    pub a11y: A11yHint,
}

/// Placement of a [`FlyoutToken`] relative to its anchor (WinUI `FlyoutPlacementMode`).
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum FlyoutPlacement {
    Top,
    #[default]
    Bottom,
    Left,
    Right,
}

/// Whether a flyout closes when pointer/keyboard focus moves outside it.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum FlyoutLightDismiss {
    #[default]
    Enabled,
    Disabled,
}

impl From<bool> for FlyoutLightDismiss {
    fn from(enabled: bool) -> Self {
        if enabled {
            Self::Enabled
        } else {
            Self::Disabled
        }
    }
}

/// Focus transition policy used when an open flyout is shown or dismissed.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum FlyoutFocusBehavior {
    #[default]
    RestoreFocus,
    KeepFocus,
    MoveFocusToContent,
}

/// A generic flyout (WinUI `Flyout`): arbitrary `content` anchored to an
/// `anchor` element, shown when `open`. Unlike `flyout_button`, the content is
/// not limited to a menu and the anchor can be any view.
#[derive(Clone, Debug)]
pub struct FlyoutToken<Message> {
    pub id: Option<String>,
    pub anchor: Box<View<Message>>,
    pub content: Box<View<Message>>,
    pub open: bool,
    pub placement: FlyoutPlacement,
    pub light_dismiss: FlyoutLightDismiss,
    pub focus_behavior: FlyoutFocusBehavior,
    pub a11y: A11yHint,
}

/// A z-stacked layering primitive: a `base` view with zero or more `layers`
/// drawn on top, each independently aligned, optionally dimming the content
/// behind it (`scrim`) and/or blocking input to it (`blocks_input`).
///
/// This is the shared mechanism behind floating action bars (aligned, no scrim,
/// pass-through) and modal dialogs (centered, scrim, input-blocking), mirroring
/// WinUI Grid Z-stacking + ContentDialog overlays.
#[derive(Clone, Debug)]
pub struct OverlayToken<Message> {
    pub id: Option<String>,
    pub base: Box<View<Message>>,
    pub layers: Vec<OverlayLayer<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct OverlayLayer<Message> {
    pub content: Box<View<Message>>,
    pub align_x: Alignment,
    pub align_y: Alignment,
    /// Scrim (dim) opacity behind this layer in `0.0..=1.0`; `None` = transparent.
    pub scrim: Option<f32>,
    pub blocks_input: bool,
}

impl<Message> OverlayLayer<Message> {
    pub fn new(content: impl IntoView<Message>) -> Self {
        Self {
            content: Box::new(content.into_view()),
            align_x: Alignment::Center,
            align_y: Alignment::Center,
            scrim: None,
            blocks_input: false,
        }
    }

    pub fn align(mut self, x: Alignment, y: Alignment) -> Self {
        self.align_x = x;
        self.align_y = y;
        self
    }

    pub fn scrim(mut self, opacity: f32) -> Self {
        self.scrim = Some(opacity.clamp(0.0, 1.0));
        self
    }

    pub fn blocks_input(mut self, value: bool) -> Self {
        self.blocks_input = value;
        self
    }

    /// Convenience for a centered, scrimmed, input-blocking modal layer.
    pub fn modal(content: impl IntoView<Message>) -> Self {
        Self::new(content)
            .align(Alignment::Center, Alignment::Center)
            .scrim(0.4)
            .blocks_input(true)
    }
}

impl<Message> OverlayToken<Message> {
    pub fn blocking_layer_count(&self) -> usize {
        self.layers
            .iter()
            .filter(|layer| layer.blocks_input)
            .count()
    }

    pub fn scrim_layer_count(&self) -> usize {
        self.layers
            .iter()
            .filter(|layer| layer.scrim.is_some())
            .count()
    }
}

#[derive(Clone, Debug)]
pub struct AdaptiveSwitchToken<Message> {
    pub id: Option<String>,
    pub breakpoint_width: u16,
    pub wide: Box<View<Message>>,
    pub narrow: Box<View<Message>>,
    /// When set, schema/a11y/diff resolve to the single branch that is actually
    /// painted at this layout width (`width >= breakpoint_width` => `wide`),
    /// matching the iced `responsive` render. When `None`, both branches are
    /// reported (width-agnostic, back-compat default).
    pub resolved_width: Option<f32>,
    pub a11y: A11yHint,
}

impl<Message> AdaptiveSwitchToken<Message> {
    /// Returns the branch that is painted at `resolved_width`, or `None` when no
    /// resolution width is set (caller should report both branches).
    pub fn resolved_branch(&self) -> Option<&View<Message>> {
        self.resolved_width.map(|width| {
            if width >= f32::from(self.breakpoint_width) {
                self.wide.as_ref()
            } else {
                self.narrow.as_ref()
            }
        })
    }

    pub fn resolved_branch_name(&self) -> &'static str {
        match self.resolved_width {
            Some(width) if width >= f32::from(self.breakpoint_width) => "wide",
            Some(_) => "narrow",
            None => "unresolved",
        }
    }
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
    pub scrollbars_visible: bool,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct ExpanderToken<Message> {
    pub id: Option<String>,
    pub title: String,
    pub title_id: Option<String>,
    pub description: Option<String>,
    pub icon: Option<IconToken>,
    pub expanded: bool,
    pub header_state: ControlState,
    pub header_style: FluentStyle,
    pub content_style: FluentStyle,
    pub content: Option<Box<View<Message>>>,
    pub trailing: Vec<View<Message>>,
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct SettingsRowToken<Message> {
    pub id: Option<String>,
    pub title: String,
    pub title_id: Option<String>,
    pub description: Option<String>,
    pub description_id: Option<String>,
    pub icon: Option<IconToken>,
    pub kind: SettingsRowKind,
    pub margin: Edges,
    pub align_x: Alignment,
    pub content_align_x: Alignment,
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
    pub header_state: ControlState,
    pub actions_visible: bool,
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
            header_state: ControlState::default(),
            actions_visible: false,
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

    pub fn header_state(mut self, state: ControlState) -> Self {
        self.header_state = state;
        self
    }

    pub fn actions_visible(mut self, visible: bool) -> Self {
        self.actions_visible = visible;
        self
    }

    pub fn header_hovered(mut self, hovered: bool) -> Self {
        self.header_state.hovered = hovered;
        self
    }

    pub fn header_pressed(mut self, pressed: bool) -> Self {
        self.header_state.pressed = pressed;
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
    pub const RESULT_BOX_ANIMATION_MS: u16 = 0;
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
    pub max_height: Option<u16>,
    pub padding: Option<Edges>,
    pub border_width: Option<u16>,
    pub collapse_transition: CollapseTransition,
    pub a11y: A11yHint,
}

impl<Message> ResultListToken<Message> {
    pub const fn list_contract_kind(&self) -> ListContractKind {
        ListContractKind::TranslationResultList
    }
}

/// A generic, data-driven list (WinUI `ListView`/`ItemsRepeater`). Where
/// `result_list` hardcodes the translation-result row shape, `ListView` renders
/// arbitrary per-item views — the base primitive for history, dictionary entries,
/// language pickers, and long-document outlines.
#[derive(Clone, Debug)]
pub struct ListViewToken<Message> {
    pub id: Option<String>,
    pub items: Vec<ListViewItem<Message>>,
    /// Id of the currently selected item, if any (WinUI `SelectedItem`).
    pub selected: Option<String>,
    pub spacing: u16,
    pub max_height: Option<u16>,
    /// Hint that the backend may recycle off-screen item views (WinUI virtualizing
    /// panel). Recorded for parity/telemetry; the token tree is unaffected.
    pub virtualized: bool,
    /// Selection callback: receives the clicked item's id (`SelectionInput`).
    pub action: Action<Message>,
    pub a11y: A11yHint,
}

impl<Message> ListViewToken<Message> {
    pub const fn list_contract_kind(&self) -> ListContractKind {
        ListContractKind::GenericListView
    }
}

/// A single row in a [`ListViewToken`], pairing a stable id with its view.
#[derive(Clone, Debug)]
pub struct ListViewItem<Message> {
    pub id: String,
    pub view: View<Message>,
}

impl<Message> ListViewItem<Message> {
    pub fn new(id: impl Into<String>, view: impl IntoView<Message>) -> Self {
        Self {
            id: id.into(),
            view: view.into_view(),
        }
    }
}

/// A tray/context-menu presenter surface. Backends render this as a Fluent menu
/// popup rather than as generic buttons, so row metrics and hover treatment can
/// track the native WinUI `MenuFlyout` reference.
#[derive(Clone, Debug)]
pub struct TrayMenuToken<Message> {
    pub id: Option<String>,
    pub min_width: u16,
    pub style: TrayMenuPresenterStyle,
    pub animation_offset_y: u16,
    pub items: Vec<TrayMenuItem<Message>>,
    pub a11y: A11yHint,
}

#[deprecated(note = "use ResultCardToken")]
pub type ServiceResultCardToken<Message> = ResultCardToken<Message>;

#[deprecated(note = "use ResultListToken")]
pub type ServiceResultListToken<Message> = ResultListToken<Message>;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CaptureOverlayRect {
    pub left: i32,
    pub top: i32,
    pub width: i32,
    pub height: i32,
}

impl CaptureOverlayRect {
    pub const fn new(left: i32, top: i32, width: i32, height: i32) -> Self {
        Self {
            left,
            top,
            width,
            height,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CaptureOverlayPhase {
    Detecting,
    Selecting,
}

impl CaptureOverlayPhase {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Detecting => "Detecting",
            Self::Selecting => "Selecting",
        }
    }
}

/// Pointer position in overlay-local (logical) coordinates, used to place the
/// magnifier and read the pixel under the cursor.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct CaptureOverlayPoint {
    pub x: i32,
    pub y: i32,
}

impl CaptureOverlayPoint {
    pub const fn new(x: i32, y: i32) -> Self {
        Self { x, y }
    }
}

/// Frozen desktop screenshot backing the capture overlay (raw BGRA pixel dump
/// written by the platform screen-capture helper), shown under the dim mask
/// like the WinUI ScreenCaptureWindow's BitBlt-on-open.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CaptureOverlayBackground {
    pub bgra_path: String,
    pub pixel_width: u32,
    pub pixel_height: u32,
}

impl CaptureOverlayBackground {
    pub fn new(bgra_path: impl Into<String>, pixel_width: u32, pixel_height: u32) -> Self {
        Self {
            bgra_path: bgra_path.into(),
            pixel_width,
            pixel_height,
        }
    }
}

impl fmt::Display for CaptureOverlayPhase {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

#[derive(Clone, Debug)]
pub struct CaptureOverlayToken {
    pub id: Option<String>,
    pub phase: CaptureOverlayPhase,
    pub detection_depth: usize,
    pub dragging: bool,
    pub detected_rect: Option<CaptureOverlayRect>,
    pub selection_rect: Option<CaptureOverlayRect>,
    pub handles_visible: bool,
    pub magnifier_visible: bool,
    /// Frozen desktop drawn full-bleed under the dim mask.
    pub background: Option<CaptureOverlayBackground>,
    /// Pointer position (overlay-local logical coords) for the magnifier.
    pub cursor: Option<CaptureOverlayPoint>,
    pub a11y: A11yHint,
}

impl CaptureOverlayToken {
    pub fn background_pixel_size(&self) -> Option<(u32, u32)> {
        self.background
            .as_ref()
            .map(|background| (background.pixel_width, background.pixel_height))
    }
}

/// A bitmap image. The only supported source today is a raw 32-bit BGRA pixel
/// file (the format written by the platform screen-capture API), which lets the
/// OCR capture overlay show the frozen desktop like the WinUI implementation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ImageToken {
    pub id: Option<String>,
    /// Path to a raw BGRA8 pixel dump (no header). Used by the OCR magnifier;
    /// empty when the image is sourced from an encoded file via `raster_path`.
    pub bgra_path: String,
    pub pixel_width: u32,
    pub pixel_height: u32,
    /// Path or URI to an encoded image file (PNG/JPG/…) for generic images —
    /// service icons, language flags, etc. (WinUI `Image.Source`). Takes
    /// precedence over `bgra_path` when set.
    pub raster_path: Option<String>,
    /// How the image scales to fill its bounds (WinUI `Image.Stretch`).
    pub stretch: ImageStretch,
    pub width: Length,
    pub height: Length,
    pub a11y: A11yHint,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ImageSourceKind {
    Empty,
    Bgra,
    Raster,
}

impl ImageToken {
    pub fn source_kind(&self) -> ImageSourceKind {
        if self
            .raster_path
            .as_deref()
            .is_some_and(|path| !path.is_empty())
        {
            ImageSourceKind::Raster
        } else if !self.bgra_path.is_empty() && self.pixel_width > 0 && self.pixel_height > 0 {
            ImageSourceKind::Bgra
        } else {
            ImageSourceKind::Empty
        }
    }

    pub fn fallback_behavior(&self) -> &'static str {
        match self.source_kind() {
            ImageSourceKind::Raster => "backend-decodes-or-reserves-layout",
            ImageSourceKind::Bgra => "empty-layout-slot-on-read-error",
            ImageSourceKind::Empty => "empty-layout-slot",
        }
    }
}

/// How an image is scaled to fit its layout bounds (WinUI `Stretch`).
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum ImageStretch {
    /// Render at native size, no scaling.
    None,
    /// Stretch to fill bounds, ignoring aspect ratio.
    Fill,
    /// Scale to fit within bounds, preserving aspect ratio (letterboxed).
    #[default]
    Uniform,
    /// Scale to fill bounds, preserving aspect ratio (cropped).
    UniformToFill,
}

/// Source content for a [`WebViewToken`] (WinUI `WebView2`).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum WebViewSource {
    /// Inline HTML markup (`NavigateToString`).
    Html(String),
    /// A URL to navigate to (`Source`).
    Url(String),
}

/// An embedded web content host (WinUI `WebView2`) for HTML/MDX dictionary
/// content.
///
/// Interface-level only: the token + schema describe the web content surface so
/// app code and snapshot tests can target it, but the iced backend has no
/// embedded browser engine and renders a labeled placeholder panel. A real host
/// (WebView2 control hosted in the native window) is a separate platform effort.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WebViewToken {
    pub id: Option<String>,
    pub source: WebViewSource,
    pub width: Length,
    pub height: Length,
    pub a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct CustomToken<Message> {
    pub id: Option<String>,
    pub kind: CustomControlKind,
    pub control: String,
    pub target_type: Option<String>,
    pub children: Vec<View<Message>>,
    pub a11y: A11yHint,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PointerPosition {
    pub x: i32,
    pub y: i32,
}

impl PointerPosition {
    pub const fn new(x: i32, y: i32) -> Self {
        Self { x, y }
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct PointerWheel {
    pub delta: i32,
    pub position: PointerPosition,
}

#[derive(Clone)]
pub enum PointerRegionAction<Message> {
    None,
    Position(Arc<dyn Fn(PointerPosition) -> Message + Send + Sync + 'static>),
    Wheel(Arc<dyn Fn(PointerWheel) -> Message + Send + Sync + 'static>),
}

impl<Message> PointerRegionAction<Message> {
    pub const fn none() -> Self {
        Self::None
    }

    pub fn position(map: impl Fn(PointerPosition) -> Message + Send + Sync + 'static) -> Self {
        Self::Position(Arc::new(map))
    }

    pub fn wheel(map: impl Fn(PointerWheel) -> Message + Send + Sync + 'static) -> Self {
        Self::Wheel(Arc::new(map))
    }

    pub const fn kind(&self) -> PointerRegionActionKind {
        match self {
            Self::None => PointerRegionActionKind::None,
            Self::Position(_) => PointerRegionActionKind::Position,
            Self::Wheel(_) => PointerRegionActionKind::Wheel,
        }
    }

    pub fn at(&self, position: PointerPosition) -> Option<Message> {
        match self {
            Self::Position(map) => Some(map(position)),
            _ => None,
        }
    }

    pub fn wheel_at(&self, wheel: PointerWheel) -> Option<Message> {
        match self {
            Self::Wheel(map) => Some(map(wheel)),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Eq, PartialEq)]
pub enum PointerRegionActionKind {
    None,
    Position,
    Wheel,
}

impl fmt::Debug for PointerRegionActionKind {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::None => "none",
            Self::Position => "position",
            Self::Wheel => "wheel",
        })
    }
}

impl<Message: fmt::Debug> fmt::Debug for PointerRegionAction<Message> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::None => formatter.write_str("PointerRegionAction::None"),
            Self::Position(_) => formatter.write_str("PointerRegionAction::Position(<handler>)"),
            Self::Wheel(_) => formatter.write_str("PointerRegionAction::Wheel(<handler>)"),
        }
    }
}

#[derive(Clone, Debug)]
pub struct PointerRegionToken<Message> {
    pub id: Option<String>,
    pub content: Box<View<Message>>,
    pub width: Length,
    pub height: Length,
    pub move_action: PointerRegionAction<Message>,
    pub left_down_action: PointerRegionAction<Message>,
    pub left_up_action: PointerRegionAction<Message>,
    pub double_click_action: PointerRegionAction<Message>,
    pub right_down_action: Action<Message>,
    pub wheel_action: PointerRegionAction<Message>,
    pub escape_action: Action<Message>,
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
        font_size: None,
        width: None,
        height: None,
        margin: Edges::ZERO,
        align_x: Alignment::Start,
        align_y: Alignment::Start,
        wrapping: TextWrapping::Word,
        selectable: false,
        a11y: A11yHint::default(),
    }))
}

/// Inline rich text (WinUI `RichTextBlock`): a run of styled spans with optional
/// hyperlinks, for dictionary entries and MDX content.
///
/// ```no_run
/// # use win_fluent::prelude::*;
/// # #[derive(Clone)]
/// # enum Msg { OpenWord(String) }
/// # fn main() {
/// # let _: View<Msg> =
/// text_runs([
///     TextRun::plain("see also "),
///     TextRun::link("hello", "word:hello"),
///     TextRun::plain(" ("),
///     TextRun::italic("interj."),
///     TextRun::plain(")"),
/// ])
/// .on_link(Msg::OpenWord);
/// # }
/// ```
pub fn text_runs<Message>(runs: impl IntoIterator<Item = TextRun>) -> RichTextBuilder<Message> {
    RichTextBuilder {
        id: None,
        runs: runs.into_iter().collect(),
        style: TextStyle::Body,
        wrapping: TextWrapping::Word,
        link_action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn button<Message>(label: impl Into<String>) -> ButtonBuilder<Message> {
    ButtonBuilder::new(label, ButtonKind::Standard)
}

pub fn primary_button<Message>(label: impl Into<String>) -> ButtonBuilder<Message> {
    ButtonBuilder::new(label, ButtonKind::Primary)
}

/// A button with a sticky on/off pressed state (WinUI `ToggleButton`).
pub fn toggle_button<Message>(
    label: impl Into<String>,
    pressed: bool,
) -> ToggleButtonBuilder<Message> {
    ToggleButtonBuilder {
        id: None,
        label: label.into(),
        icon: None,
        pressed,
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

/// A primary action segment plus a dropdown menu (WinUI `SplitButton`).
pub fn split_button<Message>(label: impl Into<String>) -> SplitButtonBuilder<Message> {
    SplitButtonBuilder {
        id: None,
        label: label.into(),
        icon: None,
        items: Vec::new(),
        open: false,
        state: ControlState::default(),
        primary_action: Action::None,
        select_action: Action::None,
        a11y: A11yHint::default(),
    }
}

/// A single-child rounded/stroked container (WinUI `Border`).
pub fn border<Message>(content: impl IntoView<Message>) -> BorderBuilder<Message> {
    BorderBuilder {
        id: None,
        content: Box::new(content.into_view()),
        corner_radius: 4,
        stroke_width: 1,
        filled: false,
        padding: Edges::ZERO,
        width: Length::Shrink,
        height: Length::Shrink,
        a11y: A11yHint::default(),
    }
}

/// A single-child uniformly scaling container (WinUI `Viewbox`).
pub fn viewbox<Message>(content: impl IntoView<Message>) -> ViewboxBuilder<Message> {
    ViewboxBuilder {
        id: None,
        content: Box::new(content.into_view()),
        stretch: ImageStretch::Uniform,
        width: Length::Shrink,
        height: Length::Shrink,
        a11y: A11yHint::default(),
    }
}

/// A tabbed container (WinUI `TabView`).
pub fn tab_view<Message>(
    tabs: impl IntoIterator<Item = TabItem<Message>>,
) -> TabViewBuilder<Message> {
    TabViewBuilder {
        id: None,
        tabs: tabs.into_iter().collect(),
        selected: None,
        action: Action::None,
        close_action: Action::None,
        a11y: A11yHint::default(),
    }
}

/// A hierarchical list (WinUI `TreeView`).
pub fn tree_view<Message>(roots: impl IntoIterator<Item = TreeNode>) -> TreeViewBuilder<Message> {
    TreeViewBuilder {
        id: None,
        roots: roots.into_iter().collect(),
        selected: None,
        action: Action::None,
        toggle_action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn flyout_button<Message>(label: impl Into<String>) -> FlyoutButtonBuilder<Message> {
    FlyoutButtonBuilder {
        id: None,
        label: label.into(),
        icon: None,
        tooltip: None,
        selected: None,
        items: Vec::new(),
        min_width: None,
        min_height: None,
        padding: None,
        border_width: None,
        radius: None,
        align_y: Alignment::Start,
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
        drag_action: Action::None,
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
        kind: StatusBadgeKind::Text,
        count: None,
        severity,
        icon: None,
        a11y: A11yHint::default(),
        _message: std::marker::PhantomData,
    }
}

pub fn info_bar<Message>(
    title: impl Into<String>,
    severity: ValidationSeverity,
) -> InfoBarBuilder<Message> {
    InfoBarBuilder {
        id: None,
        title: title.into(),
        message: String::new(),
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

pub fn progress_bar<Message>() -> ProgressBarBuilder<Message> {
    ProgressBarBuilder {
        id: None,
        active: true,
        value: None,
        width: Length::Fill,
        height: 4,
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
        fade_transition_ms: 120,
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
        content_spacing: 12,
        margin: Edges::ZERO,
        max_height: None,
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
        width: None,
        min_height: None,
        max_height: None,
        padding: None,
        text_style: TextStyle::Body,
        chrome: TextEditorChrome::Standard,
        secure: false,
        read_only: false,
        state: ControlState::default(),
        action: Action::None,
        key_bindings: Vec::new(),
        trailing_icon: None,
        a11y: A11yHint::default(),
    }
}

pub fn toggle_switch<Message>(
    label: impl Into<String>,
    checked: bool,
) -> ToggleSwitchBuilder<Message> {
    ToggleSwitchBuilder {
        id: None,
        header: None,
        label: label.into(),
        checked,
        width: None,
        height: None,
        margin: Edges::ZERO,
        align_y: Alignment::Start,
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn checkbox<Message>(label: impl Into<String>, checked: bool) -> CheckBoxBuilder<Message> {
    CheckBoxBuilder {
        id: None,
        label: label.into(),
        checked,
        indeterminate: false,
        label_italic: false,
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

/// A single-selection radio group (WinUI `RadioButtons`).
///
/// ```no_run
/// # use win_fluent::prelude::*;
/// # #[derive(Clone)]
/// # enum Msg { ThemeChanged(String) }
/// # fn main() {
/// # let _: View<Msg> =
/// radio_group()
///     .header("Theme")
///     .option("system", "Use system setting")
///     .option("light", "Light")
///     .option("dark", "Dark")
///     .selected("system")
///     .on_select(Msg::ThemeChanged);
/// # }
/// ```
pub fn radio_group<Message>() -> RadioGroupBuilder<Message> {
    RadioGroupBuilder {
        id: None,
        header: None,
        options: Vec::new(),
        selected: None,
        orientation: Orientation::Vertical,
        spacing: 6,
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

/// A numeric input (WinUI `NumberBox`) with optional spin buttons and range.
///
/// ```no_run
/// # use win_fluent::prelude::*;
/// # #[derive(Clone)]
/// # enum Msg { Speed(f32) }
/// # fn main() {
/// # let _: View<Msg> =
/// number_box(1.0).range(0.5, 3.0).step(0.1).spin_buttons(true).on_change(Msg::Speed);
/// # }
/// ```
pub fn number_box<Message>(value: f32) -> NumberBoxBuilder<Message> {
    NumberBoxBuilder {
        id: None,
        value,
        min: None,
        max: None,
        step: 1.0,
        header: None,
        placeholder: None,
        spin_buttons: true,
        state: ControlState::default(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

/// A text box with as-you-type suggestions (WinUI `AutoSuggestBox`).
///
/// ```no_run
/// # use win_fluent::prelude::*;
/// # #[derive(Clone)]
/// # enum Msg { QueryChanged(String), LanguagePicked(String) }
/// # fn main() {
/// # let query = "";
/// # let matches = ["English", "Chinese"];
/// # let _: View<Msg> =
/// auto_suggest_box(query)
///     .placeholder("Search languages")
///     .suggestions(matches)
///     .on_change(Msg::QueryChanged)
///     .on_submit(Msg::LanguagePicked);
/// # }
/// ```
pub fn auto_suggest_box<Message>(text: impl Into<String>) -> AutoSuggestBoxBuilder<Message> {
    AutoSuggestBoxBuilder {
        id: None,
        text: text.into(),
        placeholder: None,
        header: None,
        suggestions: Vec::new(),
        open: false,
        highlighted_index: None,
        width: Length::Fill,
        state: ControlState::default(),
        change_action: Action::None,
        submit_action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn slider<Message>(value: f32) -> SliderBuilder<Message> {
    SliderBuilder {
        id: None,
        value,
        min: 0.0,
        max: 1.0,
        step: 0.1,
        width: Length::Fill,
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
        placeholder: None,
        items: items.into_iter().collect(),
        selected: None,
        width: Length::Shrink,
        height: Length::Fixed(32),
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
        footer_items: Vec::new(),
        content: None,
        pane_display_mode: PaneDisplayMode::default(),
        header: None,
        settings_visible: false,
        back_button_visible: false,
        action: Action::None,
        back_action: Action::None,
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

/// A 2D grid layout (WinUI `Grid`). Declare tracks with [`rows`](GridBuilder::rows)
/// and [`columns`](GridBuilder::columns), then place children with
/// [`cell`](GridBuilder::cell) / [`cell_span`](GridBuilder::cell_span).
///
/// ```no_run
/// # use win_fluent::prelude::*;
/// # #[derive(Clone)]
/// # enum Msg { Name(String), Save }
/// # fn main() {
/// # let _: GridBuilder<Msg> =
/// grid()
///     .columns([Length::Shrink, Length::Fill])     // Auto | *
///     .rows([Length::Shrink, Length::Shrink])
///     .cell(0, 0, text("Name"))
///     .cell(0, 1, text_editor("").on_input(Msg::Name))
///     .cell_span(1, 0, 1, 2, button("Save").on_press(Msg::Save));
/// # }
/// ```
pub fn grid<Message>() -> GridBuilder<Message> {
    GridBuilder {
        id: None,
        rows: Vec::new(),
        columns: Vec::new(),
        row_spacing: 0,
        column_spacing: 0,
        padding: 0,
        padding_edges: None,
        width: Length::Shrink,
        height: Length::Shrink,
        align: Alignment::Start,
        children: Vec::new(),
        a11y: A11yHint::default(),
    }
}

/// A width-responsive flow layout (WinUI `ItemsWrapGrid`): children pack
/// left-to-right and wrap to a new row when they run out of horizontal room or
/// the row reaches the column cap, so the grid reflows as the window resizes.
///
/// Use instead of hand-splitting children across fixed rows. Set the per-row
/// column cap with [`max_columns`](WrapBuilder::max_columns) (defaults to 1).
pub fn wrap<Message, Children>(children: Children) -> WrapBuilder<Message>
where
    Children: IntoChildren<Message>,
{
    WrapBuilder {
        id: None,
        children: children.into_children(),
        max_columns: 1,
        spacing: 0,
        run_spacing: 0,
        a11y: A11yHint::default(),
    }
}

/// A z-stacked layering: `base` with [`layer`](OverlayBuilder::layer)s on top.
///
/// Use for floating action bars and modal dialogs instead of stacking siblings
/// in a column. See [`OverlayLayer`] for per-layer alignment / scrim / input.
pub fn overlay<Message, Base>(base: Base) -> OverlayBuilder<Message>
where
    Base: IntoView<Message>,
{
    OverlayBuilder {
        id: None,
        base: Box::new(base.into_view()),
        layers: Vec::new(),
        a11y: A11yHint::default(),
    }
}

/// A generic flyout (WinUI `Flyout`): `content` anchored to `anchor`, shown when
/// open. Use for any popover content (not just menus).
///
/// ```no_run
/// # use win_fluent::prelude::*;
/// # #[derive(Clone)]
/// # enum Msg { Toggle }
/// # fn main() {
/// # let settings_panel = text("Settings");
/// # let menu_open = true;
/// # let _: FlyoutBuilder<Msg> =
/// flyout(button("Options").on_press(Msg::Toggle), settings_panel)
///     .open(menu_open)
///     .placement(FlyoutPlacement::Bottom);
/// # }
/// ```
pub fn flyout<Message, Anchor, Content>(anchor: Anchor, content: Content) -> FlyoutBuilder<Message>
where
    Anchor: IntoView<Message>,
    Content: IntoView<Message>,
{
    FlyoutBuilder {
        id: None,
        anchor: Box::new(anchor.into_view()),
        content: Box::new(content.into_view()),
        open: false,
        placement: FlyoutPlacement::default(),
        light_dismiss: FlyoutLightDismiss::default(),
        focus_behavior: FlyoutFocusBehavior::default(),
        a11y: A11yHint::default(),
    }
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
        resolved_width: None,
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
        scrollbars_visible: false,
        a11y: A11yHint::default(),
    }
}

pub fn pointer_region<Message, Child>(content: Child) -> PointerRegionBuilder<Message>
where
    Child: IntoView<Message>,
{
    PointerRegionBuilder {
        id: None,
        content: Box::new(content.into_view()),
        width: Length::Fill,
        height: Length::Fill,
        move_action: PointerRegionAction::None,
        left_down_action: PointerRegionAction::None,
        left_up_action: PointerRegionAction::None,
        double_click_action: PointerRegionAction::None,
        right_down_action: Action::None,
        wheel_action: PointerRegionAction::None,
        escape_action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn capture_overlay(phase: CaptureOverlayPhase) -> CaptureOverlayBuilder {
    CaptureOverlayBuilder {
        id: None,
        phase,
        detection_depth: 0,
        dragging: false,
        detected_rect: None,
        selection_rect: None,
        handles_visible: false,
        magnifier_visible: false,
        background: None,
        cursor: None,
        a11y: A11yHint::default(),
    }
}

pub fn expander<Message>(title: impl Into<String>) -> ExpanderBuilder<Message> {
    ExpanderBuilder {
        id: None,
        title: title.into(),
        title_id: None,
        description: None,
        icon: None,
        expanded: false,
        header_state: ControlState::default(),
        header_style: FluentStyle::new(),
        content_style: FluentStyle::new(),
        content: None,
        trailing: Vec::new(),
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn settings_row<Message>(title: impl Into<String>) -> SettingsRowBuilder<Message> {
    SettingsRowBuilder {
        id: None,
        title: title.into(),
        title_id: None,
        description: None,
        description_id: None,
        icon: None,
        kind: SettingsRowKind::Normal,
        margin: Edges::ZERO,
        align_x: Alignment::Start,
        content_align_x: Alignment::Start,
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
        max_height: None,
        padding: None,
        border_width: None,
        collapse_transition: CollapseTransition::default(),
        a11y: A11yHint::default(),
    }
}

/// A generic data-driven list (WinUI `ListView`/`ItemsRepeater`). Pass
/// pre-built [`ListViewItem`]s (id + view); `result_list` is a specialization of
/// this for translation results.
///
/// ```no_run
/// # use win_fluent::prelude::*;
/// # #[derive(Clone)]
/// # enum Msg { LanguagePicked(String) }
/// # fn main() {
/// # let _: View<Msg> =
/// list_view([
///     ListViewItem::new("en", text("English")),
///     ListViewItem::new("zh", text("中文")),
/// ])
/// .selected("en")
/// .on_select(Msg::LanguagePicked);
/// # }
/// ```
pub fn list_view<Message>(
    items: impl IntoIterator<Item = ListViewItem<Message>>,
) -> ListViewBuilder<Message> {
    ListViewBuilder {
        id: None,
        items: items.into_iter().collect(),
        selected: None,
        spacing: 4,
        max_height: None,
        virtualized: true,
        action: Action::None,
        a11y: A11yHint::default(),
    }
}

pub fn custom_control<Message, Children>(
    control: impl Into<String>,
    children: Children,
) -> View<Message>
where
    Children: IntoChildren<Message>,
{
    View::new(ViewToken::Custom(CustomToken {
        id: None,
        kind: CustomControlKind::Custom,
        control: control.into(),
        target_type: None,
        children: children.into_children(),
        a11y: A11yHint::default(),
    }))
}

pub fn control_template<Message, Children>(
    target_type: impl Into<String>,
    template_key: impl Into<String>,
    children: Children,
) -> View<Message>
where
    Children: IntoChildren<Message>,
{
    View::new(ViewToken::Custom(CustomToken {
        id: None,
        kind: CustomControlKind::ControlTemplate,
        control: template_key.into(),
        target_type: Some(target_type.into()),
        children: children.into_children(),
        a11y: A11yHint::default(),
    }))
}

pub fn tray_menu_presenter<Message>(menu: TrayMenu<Message>) -> View<Message> {
    tray_menu_presenter_with_animation_offset(menu, 0)
}

pub fn tray_menu_presenter_with_animation_offset<Message>(
    menu: TrayMenu<Message>,
    animation_offset_y: u16,
) -> View<Message> {
    View::new(ViewToken::TrayMenu(TrayMenuToken {
        id: None,
        min_width: menu.presenter_min_width.unwrap_or(240),
        style: menu.presenter_style,
        animation_offset_y,
        items: menu.items,
        a11y: A11yHint::default(),
    }))
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
    drag_action: Action<Message>,
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

    /// Message emitted when the user presses the empty title-bar region,
    /// used by the backend to begin an OS-level window move/drag.
    pub fn on_drag(mut self, message: Message) -> Self {
        self.drag_action = Action::Message(message);
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
            drag_action: self.drag_action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct RichTextBuilder<Message> {
    id: Option<String>,
    runs: Vec<TextRun>,
    style: TextStyle,
    wrapping: TextWrapping,
    link_action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> RichTextBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn style(mut self, style: TextStyle) -> Self {
        self.style = style;
        self
    }

    pub fn wrapping(mut self, wrapping: TextWrapping) -> Self {
        self.wrapping = wrapping;
        self
    }

    /// Append a run.
    pub fn run(mut self, run: TextRun) -> Self {
        self.runs.push(run);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// Link-clicked callback: receives the clicked run's `href`.
    pub fn on_link(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.link_action = Action::selection_input(map);
        self.into_view()
    }

    /// Finish without a link handler.
    pub fn build(self) -> View<Message> {
        self.into_view()
    }
}

impl<Message> IntoView<Message> for RichTextBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::RichText(RichTextToken {
            id: self.id,
            runs: self.runs,
            style: self.style,
            wrapping: self.wrapping,
            link_action: self.link_action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ToggleButtonBuilder<Message> {
    id: Option<String>,
    label: String,
    icon: Option<IconToken>,
    pressed: bool,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> ToggleButtonBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
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

    pub fn on_toggle(
        mut self,
        map: impl Fn(bool) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::bool_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for ToggleButtonBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ToggleButton(ToggleButtonToken {
            id: self.id,
            label: self.label,
            icon: self.icon,
            pressed: self.pressed,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct SplitButtonBuilder<Message> {
    id: Option<String>,
    label: String,
    icon: Option<IconToken>,
    items: Vec<FlyoutMenuItem>,
    open: bool,
    state: ControlState,
    primary_action: Action<Message>,
    select_action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> SplitButtonBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn items(mut self, items: impl IntoIterator<Item = FlyoutMenuItem>) -> Self {
        self.items = items.into_iter().collect();
        self
    }

    pub fn open(mut self, open: bool) -> Self {
        self.open = open;
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.state.enabled = enabled;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// Primary-segment press callback.
    pub fn on_press(mut self, message: Message) -> Self {
        self.primary_action = Action::message(message);
        self
    }

    /// Menu-item-chosen callback (receives the item id).
    pub fn on_select(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.select_action = Action::selection_input(map);
        self.into_view()
    }

    /// Finish without a menu-select handler.
    pub fn build(self) -> View<Message> {
        self.into_view()
    }
}

impl<Message> IntoView<Message> for SplitButtonBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::SplitButton(SplitButtonToken {
            id: self.id,
            label: self.label,
            icon: self.icon,
            items: self.items,
            open: self.open,
            state: self.state,
            primary_action: self.primary_action,
            select_action: self.select_action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct BorderBuilder<Message> {
    id: Option<String>,
    content: Box<View<Message>>,
    corner_radius: u16,
    stroke_width: u16,
    filled: bool,
    padding: Edges,
    width: Length,
    height: Length,
    a11y: A11yHint,
}

impl<Message> BorderBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn corner_radius(mut self, radius: u16) -> Self {
        self.corner_radius = radius;
        self
    }

    pub fn stroke_width(mut self, width: u16) -> Self {
        self.stroke_width = width;
        self
    }

    /// Fill the interior with the theme surface color.
    pub fn filled(mut self, filled: bool) -> Self {
        self.filled = filled;
        self
    }

    pub fn padding(mut self, padding: Edges) -> Self {
        self.padding = padding;
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

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for BorderBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Border(BorderToken {
            id: self.id,
            content: self.content,
            corner_radius: self.corner_radius,
            stroke_width: self.stroke_width,
            filled: self.filled,
            padding: self.padding,
            width: self.width,
            height: self.height,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ViewboxBuilder<Message> {
    id: Option<String>,
    content: Box<View<Message>>,
    stretch: ImageStretch,
    width: Length,
    height: Length,
    a11y: A11yHint,
}

impl<Message> ViewboxBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn stretch(mut self, stretch: ImageStretch) -> Self {
        self.stretch = stretch;
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

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for ViewboxBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Viewbox(ViewboxToken {
            id: self.id,
            content: self.content,
            stretch: self.stretch,
            width: self.width,
            height: self.height,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct TabViewBuilder<Message> {
    id: Option<String>,
    tabs: Vec<TabItem<Message>>,
    selected: Option<String>,
    action: Action<Message>,
    close_action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> TabViewBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn selected(mut self, id: impl Into<String>) -> Self {
        self.selected = Some(id.into());
        self
    }

    /// Tab-close callback (receives the closed tab id).
    pub fn on_close(mut self, map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        self.close_action = Action::selection_input(map);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// Tab-selected callback (receives the selected tab id).
    pub fn on_select(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::selection_input(map);
        self.into_view()
    }

    /// Finish without a select handler.
    pub fn build(self) -> View<Message> {
        self.into_view()
    }
}

impl<Message> IntoView<Message> for TabViewBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::TabView(TabViewToken {
            id: self.id,
            tabs: self.tabs,
            selected: self.selected,
            action: self.action,
            close_action: self.close_action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct TreeViewBuilder<Message> {
    id: Option<String>,
    roots: Vec<TreeNode>,
    selected: Option<String>,
    action: Action<Message>,
    toggle_action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> TreeViewBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn selected(mut self, id: impl Into<String>) -> Self {
        self.selected = Some(id.into());
        self
    }

    /// Node expand/collapse-toggle callback (receives the node id).
    pub fn on_toggle(mut self, map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        self.toggle_action = Action::selection_input(map);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// Node-selected callback (receives the node id).
    pub fn on_select(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::selection_input(map);
        self.into_view()
    }

    /// Finish without a select handler.
    pub fn build(self) -> View<Message> {
        self.into_view()
    }
}

impl<Message> IntoView<Message> for TreeViewBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::TreeView(TreeViewToken {
            id: self.id,
            roots: self.roots,
            selected: self.selected,
            action: self.action,
            toggle_action: self.toggle_action,
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
    width: Option<Length>,
    height: Option<Length>,
    padding: Option<Edges>,
    text_style: Option<TextStyle>,
    font_size: Option<u16>,
    margin: Edges,
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
            width: None,
            height: None,
            padding: None,
            text_style: None,
            font_size: None,
            margin: Edges::ZERO,
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

    pub fn width(mut self, width: Length) -> Self {
        self.width = Some(width);
        self
    }

    pub fn height(mut self, height: Length) -> Self {
        self.height = Some(height);
        self
    }

    pub fn padding(mut self, value: Edges) -> Self {
        self.padding = Some(value);
        self
    }

    pub fn text_style(mut self, style: TextStyle) -> Self {
        self.text_style = Some(style);
        self
    }

    pub fn font_size(mut self, size: u16) -> Self {
        self.font_size = Some(size);
        self
    }

    pub fn margin(mut self, value: Edges) -> Self {
        self.margin = value;
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

    /// Marks the button as persistently selected (e.g. the active tab), distinct
    /// from keyboard focus. Renders the theme's selected surface.
    pub fn selected(mut self, selected: bool) -> Self {
        self.state.selected = selected;
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

    pub fn link(mut self) -> Self {
        self.kind = ButtonKind::Link;
        self
    }

    pub fn chip(mut self) -> Self {
        self.kind = ButtonKind::Chip;
        self
    }

    pub fn tile(mut self) -> Self {
        self.kind = ButtonKind::Tile;
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

    pub fn primary_round(mut self) -> Self {
        self.kind = ButtonKind::PrimaryRound;
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
            width: self.width,
            height: self.height,
            padding: self.padding,
            text_style: self.text_style,
            font_size: self.font_size,
            margin: self.margin,
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
    min_width: Option<u16>,
    min_height: Option<u16>,
    padding: Option<Edges>,
    border_width: Option<u16>,
    radius: Option<u16>,
    align_y: Alignment,
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

    pub fn min_width(mut self, value: u16) -> Self {
        self.min_width = Some(value);
        self
    }

    pub fn min_height(mut self, value: u16) -> Self {
        self.min_height = Some(value);
        self
    }

    pub fn padding(mut self, value: Edges) -> Self {
        self.padding = Some(value);
        self
    }

    pub fn border_width(mut self, value: u16) -> Self {
        self.border_width = Some(value);
        self
    }

    pub fn radius(mut self, value: u16) -> Self {
        self.radius = Some(value);
        self
    }

    pub fn align_y(mut self, align_y: Alignment) -> Self {
        self.align_y = align_y;
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
            min_width: self.min_width,
            min_height: self.min_height,
            padding: self.padding,
            border_width: self.border_width,
            radius: self.radius,
            align_y: self.align_y,
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
    kind: StatusBadgeKind,
    count: Option<u32>,
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

    pub fn count(mut self, value: u32) -> Self {
        self.label = value.to_string();
        self.kind = StatusBadgeKind::Count;
        self.count = Some(value);
        self
    }

    pub fn dot(mut self) -> Self {
        self.label.clear();
        self.kind = StatusBadgeKind::Dot;
        self.count = None;
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
            kind: self.kind,
            count: self.count,
            severity: self.severity,
            icon: self.icon,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct InfoBarBuilder<Message> {
    id: Option<String>,
    title: String,
    message: String,
    severity: ValidationSeverity,
    icon: Option<IconToken>,
    a11y: A11yHint,
    _message: std::marker::PhantomData<Message>,
}

impl<Message> InfoBarBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn message(mut self, message: impl Into<String>) -> Self {
        self.message = message.into();
        self
    }

    /// Override the default severity glyph (CheckMark / Warning / Error / Info).
    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for InfoBarBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::InfoBar(InfoBarToken {
            id: self.id,
            title: self.title,
            message: self.message,
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
pub struct ProgressBarBuilder<Message> {
    id: Option<String>,
    active: bool,
    value: Option<f32>,
    width: Length,
    height: u16,
    label: Option<String>,
    a11y: A11yHint,
    _message: std::marker::PhantomData<Message>,
}

impl<Message> ProgressBarBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn active(mut self, active: bool) -> Self {
        self.active = active;
        self
    }

    pub fn value(mut self, value: f32) -> Self {
        self.value = normalize_progress_bar_value(value);
        self
    }

    pub fn indeterminate(mut self) -> Self {
        self.value = None;
        self
    }

    pub fn width(mut self, width: Length) -> Self {
        self.width = width;
        self
    }

    pub fn height(mut self, height: u16) -> Self {
        self.height = height;
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

impl<Message> IntoView<Message> for ProgressBarBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ProgressBar(ProgressBarToken {
            id: self.id,
            active: self.active,
            value: self.value.and_then(normalize_progress_bar_value),
            width: self.width,
            height: self.height,
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
    fade_transition_ms: u16,
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

    /// Drives the overlay directly from a [`Loadable`](crate::loadable::Loadable):
    /// the busy indicator is shown while the value is loading. This is the
    /// packaged "async load → loading state → overlay" interface.
    pub fn loading<T, E>(self, loadable: &crate::loadable::Loadable<T, E>) -> Self {
        self.active(loadable.is_loading())
    }

    pub fn opacity(mut self, opacity: f32) -> Self {
        self.opacity = opacity.clamp(0.0, 1.0);
        self
    }

    pub fn fade_transition_ms(mut self, duration_ms: u16) -> Self {
        self.fade_transition_ms = duration_ms;
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
            fade_transition_ms: self.fade_transition_ms,
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
    content_spacing: u16,
    margin: Edges,
    max_height: Option<u16>,
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

    pub fn content_spacing(mut self, spacing: u16) -> Self {
        self.content_spacing = spacing;
        self
    }

    pub fn margin(mut self, value: Edges) -> Self {
        self.margin = value;
        self
    }

    pub fn max_height(mut self, value: u16) -> Self {
        self.max_height = Some(value);
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
            content_spacing: self.content_spacing,
            margin: self.margin,
            max_height: self.max_height,
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
    width: Option<Length>,
    min_height: Option<u16>,
    max_height: Option<u16>,
    padding: Option<Edges>,
    text_style: TextStyle,
    chrome: TextEditorChrome,
    secure: bool,
    read_only: bool,
    state: ControlState,
    action: Action<Message>,
    key_bindings: Vec<TextEditorKeyBinding<Message>>,
    trailing_icon: Option<TextEditorTrailingIcon>,
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

    pub fn width(mut self, value: Length) -> Self {
        self.width = Some(value);
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

    pub fn padding(mut self, value: Edges) -> Self {
        self.padding = Some(value);
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

    pub fn secure(mut self, secure: bool) -> Self {
        self.secure = secure;
        self
    }

    pub fn password(self) -> Self {
        self.secure(true)
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

    pub fn on_key(
        mut self,
        key: TextEditorKey,
        modifiers: TextEditorKeyModifiers,
        message: Message,
    ) -> Self {
        self.key_bindings.push(TextEditorKeyBinding {
            key,
            modifiers,
            message,
        });
        self
    }

    pub fn trailing_icon(
        mut self,
        id: impl Into<String>,
        icon: IconToken,
        label: impl Into<String>,
    ) -> Self {
        self.trailing_icon = Some(TextEditorTrailingIcon {
            id: id.into(),
            icon,
            label: label.into(),
            width: 28,
            height: 28,
            spacing: 6,
        });
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
            width: self.width,
            min_height: self.min_height,
            max_height: self.max_height,
            padding: self.padding,
            text_style: self.text_style,
            chrome: self.chrome,
            secure: self.secure,
            read_only: self.read_only,
            state: self.state,
            action: self.action,
            key_bindings: self.key_bindings,
            trailing_icon: self.trailing_icon,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ToggleSwitchBuilder<Message> {
    id: Option<String>,
    header: Option<String>,
    label: String,
    checked: bool,
    width: Option<Length>,
    height: Option<Length>,
    margin: Edges,
    align_y: Alignment,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> ToggleSwitchBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn header(mut self, header: impl Into<String>) -> Self {
        self.header = Some(header.into());
        self
    }

    pub fn width(mut self, width: Length) -> Self {
        self.width = Some(width);
        self
    }

    pub fn height(mut self, height: Length) -> Self {
        self.height = Some(height);
        self
    }

    pub fn margin(mut self, margin: Edges) -> Self {
        self.margin = margin;
        self
    }

    pub fn align_y(mut self, align_y: Alignment) -> Self {
        self.align_y = align_y;
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
            header: self.header,
            label: self.label,
            checked: self.checked,
            width: self.width,
            height: self.height,
            margin: self.margin,
            align_y: self.align_y,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct RadioGroupBuilder<Message> {
    id: Option<String>,
    header: Option<String>,
    options: Vec<RadioOption>,
    selected: Option<String>,
    orientation: Orientation,
    spacing: u16,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> RadioGroupBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn header(mut self, header: impl Into<String>) -> Self {
        self.header = Some(header.into());
        self
    }

    /// Append an option.
    pub fn option(mut self, id: impl Into<String>, label: impl Into<String>) -> Self {
        self.options.push(RadioOption::new(id, label));
        self
    }

    /// Append a pre-built option (lets callers disable it).
    pub fn option_item(mut self, option: RadioOption) -> Self {
        self.options.push(option);
        self
    }

    pub fn options(mut self, options: impl IntoIterator<Item = RadioOption>) -> Self {
        self.options.extend(options);
        self
    }

    pub fn selected(mut self, id: impl Into<String>) -> Self {
        self.selected = Some(id.into());
        self
    }

    pub fn horizontal(mut self) -> Self {
        self.orientation = Orientation::Horizontal;
        self
    }

    pub fn vertical(mut self) -> Self {
        self.orientation = Orientation::Vertical;
        self
    }

    pub fn spacing(mut self, spacing: u16) -> Self {
        self.spacing = spacing;
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

    /// Selection callback: receives the newly selected option's id.
    pub fn on_select(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::selection_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for RadioGroupBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::RadioGroup(RadioGroupToken {
            id: self.id,
            header: self.header,
            options: self.options,
            selected: self.selected,
            orientation: self.orientation,
            spacing: self.spacing,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct CheckBoxBuilder<Message> {
    id: Option<String>,
    label: String,
    checked: bool,
    indeterminate: bool,
    label_italic: bool,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> CheckBoxBuilder<Message> {
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

    pub fn label_italic(mut self, italic: bool) -> Self {
        self.label_italic = italic;
        self
    }

    /// Put the checkbox in the mixed/indeterminate state (WinUI three-state).
    pub fn indeterminate(mut self, indeterminate: bool) -> Self {
        self.indeterminate = indeterminate;
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

impl<Message> IntoView<Message> for CheckBoxBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::CheckBox(CheckBoxToken {
            id: self.id,
            label: self.label,
            checked: self.checked,
            indeterminate: self.indeterminate,
            label_italic: self.label_italic,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct NumberBoxBuilder<Message> {
    id: Option<String>,
    value: f32,
    min: Option<f32>,
    max: Option<f32>,
    step: f32,
    header: Option<String>,
    placeholder: Option<String>,
    spin_buttons: bool,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> NumberBoxBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn range(mut self, min: f32, max: f32) -> Self {
        self.min = Some(min);
        self.max = Some(max);
        self
    }

    pub fn min(mut self, min: f32) -> Self {
        self.min = Some(min);
        self
    }

    pub fn max(mut self, max: f32) -> Self {
        self.max = Some(max);
        self
    }

    pub fn step(mut self, step: f32) -> Self {
        self.step = normalize_number_box_step(step);
        self
    }

    pub fn header(mut self, header: impl Into<String>) -> Self {
        self.header = Some(header.into());
        self
    }

    pub fn placeholder(mut self, placeholder: impl Into<String>) -> Self {
        self.placeholder = Some(placeholder.into());
        self
    }

    pub fn spin_buttons(mut self, spin_buttons: bool) -> Self {
        self.spin_buttons = spin_buttons;
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

    pub fn on_change(
        mut self,
        map: impl Fn(f32) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::number_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for NumberBoxBuilder<Message> {
    fn into_view(self) -> View<Message> {
        let value = clamp_number_box_value(self.value, self.min, self.max);
        View::new(ViewToken::NumberBox(NumberBoxToken {
            id: self.id,
            value,
            min: self.min,
            max: self.max,
            step: normalize_number_box_step(self.step),
            header: self.header,
            placeholder: self.placeholder,
            spin_buttons: self.spin_buttons,
            state: self.state,
            action: self.action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct AutoSuggestBoxBuilder<Message> {
    id: Option<String>,
    text: String,
    placeholder: Option<String>,
    header: Option<String>,
    suggestions: Vec<String>,
    open: bool,
    highlighted_index: Option<usize>,
    width: Length,
    state: ControlState,
    change_action: Action<Message>,
    submit_action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> AutoSuggestBoxBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn placeholder(mut self, placeholder: impl Into<String>) -> Self {
        self.placeholder = Some(placeholder.into());
        self
    }

    pub fn header(mut self, header: impl Into<String>) -> Self {
        self.header = Some(header.into());
        self
    }

    /// Set the suggestion list (the app filters these as the user types). Opens
    /// the dropdown automatically when non-empty unless overridden by [`open`].
    pub fn suggestions(mut self, suggestions: impl IntoIterator<Item = impl Into<String>>) -> Self {
        self.suggestions = suggestions.into_iter().map(Into::into).collect();
        self.open = !self.suggestions.is_empty();
        self
    }

    pub fn open(mut self, open: bool) -> Self {
        self.open = open;
        self
    }

    pub fn highlighted_index(mut self, index: Option<usize>) -> Self {
        self.highlighted_index = index;
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

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// As-you-type callback (fires on each edit).
    pub fn on_change(mut self, map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        self.change_action = Action::text_input(map);
        self
    }

    /// Suggestion-chosen callback (receives the picked suggestion text).
    pub fn on_submit(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.submit_action = Action::selection_input(map);
        self.into_view()
    }

    /// Finish without a submit handler.
    pub fn build(self) -> View<Message> {
        self.into_view()
    }
}

impl<Message> IntoView<Message> for AutoSuggestBoxBuilder<Message> {
    fn into_view(self) -> View<Message> {
        let highlighted_index = self
            .highlighted_index
            .filter(|index| *index < self.suggestions.len());
        View::new(ViewToken::AutoSuggestBox(AutoSuggestBoxToken {
            id: self.id,
            text: self.text,
            placeholder: self.placeholder,
            header: self.header,
            suggestions: self.suggestions,
            open: self.open,
            highlighted_index,
            width: self.width,
            state: self.state,
            change_action: self.change_action,
            submit_action: self.submit_action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct SliderBuilder<Message> {
    id: Option<String>,
    value: f32,
    min: f32,
    max: f32,
    step: f32,
    width: Length,
    state: ControlState,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> SliderBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn range(mut self, min: f32, max: f32) -> Self {
        self.min = min;
        self.max = max;
        self
    }

    pub fn step(mut self, step: f32) -> Self {
        self.step = step;
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
        map: impl Fn(f32) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::number_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for SliderBuilder<Message> {
    fn into_view(self) -> View<Message> {
        let min = self.min.min(self.max);
        let max = self.max.max(self.min);
        let step = if self.step.is_finite() && self.step > 0.0 {
            self.step
        } else {
            1.0
        };
        View::new(ViewToken::Slider(SliderToken {
            id: self.id,
            value: self.value.clamp(min, max),
            min,
            max,
            step,
            width: self.width,
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
    placeholder: Option<String>,
    items: Vec<ComboBoxItem>,
    selected: Option<String>,
    width: Length,
    height: Length,
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

    pub fn placeholder(mut self, placeholder: impl Into<String>) -> Self {
        self.placeholder = Some(placeholder.into());
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

    pub fn height(mut self, height: Length) -> Self {
        self.height = height;
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
        let selected = self
            .selected
            .filter(|selected| self.items.iter().any(|item| item.id == *selected));
        View::new(ViewToken::ComboBox(ComboBoxToken {
            id: self.id,
            label: self.label,
            placeholder: self.placeholder,
            items: self.items,
            selected,
            width: self.width,
            height: self.height,
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
    footer_items: Vec<NavigationItem>,
    content: Option<Box<View<Message>>>,
    pane_display_mode: PaneDisplayMode,
    header: Option<String>,
    settings_visible: bool,
    back_button_visible: bool,
    action: Action<Message>,
    back_action: Action<Message>,
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

    /// Set the pane layout mode (WinUI `PaneDisplayMode`).
    pub fn pane_display_mode(mut self, mode: PaneDisplayMode) -> Self {
        self.pane_display_mode = mode;
        self
    }

    /// Pane header text (WinUI `PaneHeader`).
    pub fn header(mut self, header: impl Into<String>) -> Self {
        self.header = Some(header.into());
        self
    }

    /// Items pinned to the bottom of the pane (WinUI `FooterMenuItems`).
    pub fn footer_items(mut self, items: impl IntoIterator<Item = NavigationItem>) -> Self {
        self.footer_items = items.into_iter().collect();
        self
    }

    pub fn footer_item(mut self, item: NavigationItem) -> Self {
        self.footer_items.push(item);
        self
    }

    /// Show the built-in settings entry (WinUI `IsSettingsVisible`). Selecting it
    /// fires `on_select` with [`NavigationViewToken::SETTINGS_ID`].
    pub fn settings_visible(mut self, visible: bool) -> Self {
        self.settings_visible = visible;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// Show a back button that fires `message` when pressed (WinUI `BackRequested`).
    pub fn back_button(mut self, message: Message) -> Self {
        self.back_button_visible = true;
        self.back_action = Action::message(message);
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
            footer_items: self.footer_items,
            content: self.content,
            pane_display_mode: self.pane_display_mode,
            header: self.header,
            settings_visible: self.settings_visible,
            back_button_visible: self.back_button_visible,
            action: self.action,
            back_action: self.back_action,
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
    padding_edges: Option<Edges>,
    spacing: u16,
    width: Length,
    height: Length,
    max_width: Option<u16>,
    max_height: Option<u16>,
    center_x: bool,
    margin: Edges,
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
            padding_edges: None,
            spacing: 0,
            width: Length::Shrink,
            height: Length::Shrink,
            max_width: None,
            max_height: None,
            center_x: false,
            margin: Edges::ZERO,
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
        self.padding_edges = None;
        self
    }

    pub fn padding_edges(mut self, value: Edges) -> Self {
        self.padding_edges = Some(value);
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

    /// Caps the layout's width, filling available space up to `value` dips.
    pub fn max_width(mut self, value: u16) -> Self {
        self.max_width = Some(value);
        self
    }

    /// Caps the layout's height to `value` dips.
    pub fn max_height(mut self, value: u16) -> Self {
        self.max_height = Some(value);
        self
    }

    /// Centers the (bounded-width) layout horizontally within its parent.
    ///
    /// Equivalent to Tailwind `mx-auto`. Only has a visible effect when the
    /// layout's width is bounded (e.g. via [`max_width`](Self::max_width) or a
    /// fixed width); centering a fill-width layout is a no-op, matching CSS.
    pub fn center_x(mut self, value: bool) -> Self {
        self.center_x = value;
        self
    }

    /// Sets per-side outer spacing (margin) in dips.
    pub fn margin(mut self, value: Edges) -> Self {
        self.margin = value;
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
            } else if let Some(value) = class.strip_prefix("max-w-").and_then(utility_scale) {
                self.max_width = Some(value);
            } else if let Some(value) = class.strip_prefix("max-h-").and_then(utility_scale) {
                self.max_height = Some(value);
            } else {
                match class {
                    "w-full" | "w-fill" => self.width = Length::Fill,
                    "w-fit" | "w-auto" => self.width = Length::Shrink,
                    "h-full" | "h-fill" => self.height = Length::Fill,
                    "h-fit" | "h-auto" => self.height = Length::Shrink,
                    "mx-auto" => self.center_x = true,
                    "items-start" => self.align = Alignment::Start,
                    "items-center" => self.align = Alignment::Center,
                    "items-end" => self.align = Alignment::End,
                    "items-stretch" => self.align = Alignment::Stretch,
                    "justify-between" | "space-between" => {
                        self.distribution = LayoutDistribution::SpaceBetween;
                        self.width = Length::Fill;
                    }
                    _ => {
                        if let Some(value) = class.strip_prefix("mx-").and_then(utility_scale) {
                            self.margin.left = value;
                            self.margin.right = value;
                        } else if let Some(value) =
                            class.strip_prefix("my-").and_then(utility_scale)
                        {
                            self.margin.top = value;
                            self.margin.bottom = value;
                        } else if let Some(value) =
                            class.strip_prefix("mt-").and_then(utility_scale)
                        {
                            self.margin.top = value;
                        } else if let Some(value) =
                            class.strip_prefix("mr-").and_then(utility_scale)
                        {
                            self.margin.right = value;
                        } else if let Some(value) =
                            class.strip_prefix("mb-").and_then(utility_scale)
                        {
                            self.margin.bottom = value;
                        } else if let Some(value) =
                            class.strip_prefix("ml-").and_then(utility_scale)
                        {
                            self.margin.left = value;
                        } else if let Some(value) = class.strip_prefix("m-").and_then(utility_scale)
                        {
                            self.margin = Edges {
                                top: value,
                                right: value,
                                bottom: value,
                                left: value,
                            };
                        } else if let Some(value) = class.strip_prefix("w-").and_then(utility_scale)
                        {
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
            padding_edges: self.padding_edges,
            spacing: self.spacing,
            width: self.width,
            height: self.height,
            max_width: self.max_width,
            max_height: self.max_height,
            center_x: self.center_x,
            margin: self.margin,
            align: self.align,
            distribution: self.distribution,
            style: self.style,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct GridBuilder<Message> {
    id: Option<String>,
    rows: Vec<Length>,
    columns: Vec<Length>,
    row_spacing: u16,
    column_spacing: u16,
    padding: u16,
    padding_edges: Option<Edges>,
    width: Length,
    height: Length,
    align: Alignment,
    children: Vec<GridChild<Message>>,
    a11y: A11yHint,
}

impl<Message> GridBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    /// Declare the row tracks (WinUI `Grid.RowDefinitions`).
    pub fn rows(mut self, rows: impl IntoIterator<Item = Length>) -> Self {
        self.rows = rows.into_iter().collect();
        self
    }

    /// Declare the column tracks (WinUI `Grid.ColumnDefinitions`).
    pub fn columns(mut self, columns: impl IntoIterator<Item = Length>) -> Self {
        self.columns = columns.into_iter().collect();
        self
    }

    /// Gap between rows.
    pub fn row_spacing(mut self, value: u16) -> Self {
        self.row_spacing = value;
        self
    }

    /// Gap between columns.
    pub fn column_spacing(mut self, value: u16) -> Self {
        self.column_spacing = value;
        self
    }

    /// Set both row and column gaps at once.
    pub fn spacing(mut self, value: u16) -> Self {
        self.row_spacing = value;
        self.column_spacing = value;
        self
    }

    pub fn padding(mut self, value: u16) -> Self {
        self.padding = value;
        self
    }

    pub fn padding_edges(mut self, edges: Edges) -> Self {
        self.padding_edges = Some(edges);
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

    pub fn align(mut self, align: Alignment) -> Self {
        self.align = align;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// Place a child at `(row, column)` spanning a single cell.
    pub fn cell(mut self, row: u16, column: u16, view: impl IntoView<Message>) -> Self {
        self.children.push(GridChild::new(row, column, view));
        self
    }

    /// Place a child at `(row, column)` spanning `(row_span, column_span)` cells.
    pub fn cell_span(
        mut self,
        row: u16,
        column: u16,
        row_span: u16,
        column_span: u16,
        view: impl IntoView<Message>,
    ) -> Self {
        self.children
            .push(GridChild::new(row, column, view).span(row_span, column_span));
        self
    }

    /// Add a pre-built [`GridChild`].
    pub fn child(mut self, child: GridChild<Message>) -> Self {
        self.children.push(child);
        self
    }
}

impl<Message> IntoView<Message> for GridBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Grid(GridToken {
            id: self.id,
            rows: self.rows,
            columns: self.columns,
            row_spacing: self.row_spacing,
            column_spacing: self.column_spacing,
            padding: self.padding,
            padding_edges: self.padding_edges,
            width: self.width,
            height: self.height,
            align: self.align,
            children: self.children,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct WrapBuilder<Message> {
    id: Option<String>,
    children: Vec<View<Message>>,
    max_columns: u16,
    spacing: u16,
    run_spacing: u16,
    a11y: A11yHint,
}

impl<Message> WrapBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    /// Maximum number of items per row. The grid still reflows to fewer columns
    /// when the available width is too narrow (WinUI `ItemsWrapGrid`); this is
    /// the upper bound per row. Defaults to 1.
    pub fn max_columns(mut self, value: u16) -> Self {
        self.max_columns = value.max(1);
        self
    }

    /// Gap between items within a row.
    pub fn spacing(mut self, value: u16) -> Self {
        self.spacing = value;
        self
    }

    /// Gap between wrapped rows. Defaults to `spacing` when left unset.
    pub fn run_spacing(mut self, value: u16) -> Self {
        self.run_spacing = value;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for WrapBuilder<Message> {
    fn into_view(self) -> View<Message> {
        let run_spacing = if self.run_spacing == 0 {
            self.spacing
        } else {
            self.run_spacing
        };
        View::new(ViewToken::Wrap(WrapToken {
            id: self.id,
            children: self.children,
            max_columns: self.max_columns.max(1),
            spacing: self.spacing,
            run_spacing,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct FlyoutBuilder<Message> {
    id: Option<String>,
    anchor: Box<View<Message>>,
    content: Box<View<Message>>,
    open: bool,
    placement: FlyoutPlacement,
    light_dismiss: FlyoutLightDismiss,
    focus_behavior: FlyoutFocusBehavior,
    a11y: A11yHint,
}

impl<Message> FlyoutBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn open(mut self, open: bool) -> Self {
        self.open = open;
        self
    }

    pub fn placement(mut self, placement: FlyoutPlacement) -> Self {
        self.placement = placement;
        self
    }

    pub fn light_dismiss(mut self, enabled: bool) -> Self {
        self.light_dismiss = FlyoutLightDismiss::from(enabled);
        self
    }

    pub fn light_dismiss_mode(mut self, mode: FlyoutLightDismiss) -> Self {
        self.light_dismiss = mode;
        self
    }

    pub fn focus_behavior(mut self, behavior: FlyoutFocusBehavior) -> Self {
        self.focus_behavior = behavior;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for FlyoutBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Flyout(FlyoutToken {
            id: self.id,
            anchor: self.anchor,
            content: self.content,
            open: self.open,
            placement: self.placement,
            light_dismiss: self.light_dismiss,
            focus_behavior: self.focus_behavior,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct OverlayBuilder<Message> {
    id: Option<String>,
    base: Box<View<Message>>,
    layers: Vec<OverlayLayer<Message>>,
    a11y: A11yHint,
}

impl<Message> OverlayBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    /// Adds a layer drawn on top of the base (and any earlier layers).
    pub fn layer(mut self, layer: OverlayLayer<Message>) -> Self {
        self.layers.push(layer);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for OverlayBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Overlay(OverlayToken {
            id: self.id,
            base: self.base,
            layers: self.layers,
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
    resolved_width: Option<f32>,
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

    /// Pin the layout width used by schema/a11y/diff to resolve to a single
    /// painted branch (matches the iced `responsive` rule `width >= breakpoint`).
    pub fn resolved_width(mut self, resolved_width: f32) -> Self {
        self.resolved_width = Some(resolved_width);
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
            resolved_width: self.resolved_width,
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
    scrollbars_visible: bool,
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

    pub fn scrollbars_visible(mut self, visible: bool) -> Self {
        self.scrollbars_visible = visible;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// Sets the accessibility help text (UIA automation hook) for this scroll view.
    pub fn help_text(mut self, help_text: impl Into<String>) -> Self {
        self.a11y.help_text = Some(help_text.into());
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
            scrollbars_visible: self.scrollbars_visible,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct PointerRegionBuilder<Message> {
    id: Option<String>,
    content: Box<View<Message>>,
    width: Length,
    height: Length,
    move_action: PointerRegionAction<Message>,
    left_down_action: PointerRegionAction<Message>,
    left_up_action: PointerRegionAction<Message>,
    double_click_action: PointerRegionAction<Message>,
    right_down_action: Action<Message>,
    wheel_action: PointerRegionAction<Message>,
    escape_action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> PointerRegionBuilder<Message> {
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

    pub fn on_move(
        mut self,
        map: impl Fn(PointerPosition) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.move_action = PointerRegionAction::position(map);
        self
    }

    pub fn on_left_down(
        mut self,
        map: impl Fn(PointerPosition) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.left_down_action = PointerRegionAction::position(map);
        self
    }

    pub fn on_left_up(
        mut self,
        map: impl Fn(PointerPosition) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.left_up_action = PointerRegionAction::position(map);
        self
    }

    pub fn on_double_click(
        mut self,
        map: impl Fn(PointerPosition) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.double_click_action = PointerRegionAction::position(map);
        self
    }

    pub fn on_right_down(mut self, message: Message) -> Self {
        self.right_down_action = Action::message(message);
        self
    }

    pub fn on_wheel(
        mut self,
        map: impl Fn(PointerWheel) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.wheel_action = PointerRegionAction::wheel(map);
        self
    }

    pub fn on_escape(mut self, message: Message) -> Self {
        self.escape_action = Action::message(message);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for PointerRegionBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::PointerRegion(PointerRegionToken {
            id: self.id,
            content: self.content,
            width: self.width,
            height: self.height,
            move_action: self.move_action,
            left_down_action: self.left_down_action,
            left_up_action: self.left_up_action,
            double_click_action: self.double_click_action,
            right_down_action: self.right_down_action,
            wheel_action: self.wheel_action,
            escape_action: self.escape_action,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct CaptureOverlayBuilder {
    id: Option<String>,
    phase: CaptureOverlayPhase,
    detection_depth: usize,
    dragging: bool,
    detected_rect: Option<CaptureOverlayRect>,
    selection_rect: Option<CaptureOverlayRect>,
    handles_visible: bool,
    magnifier_visible: bool,
    background: Option<CaptureOverlayBackground>,
    cursor: Option<CaptureOverlayPoint>,
    a11y: A11yHint,
}

impl CaptureOverlayBuilder {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn detection_depth(mut self, detection_depth: usize) -> Self {
        self.detection_depth = detection_depth;
        self
    }

    pub fn dragging(mut self, dragging: bool) -> Self {
        self.dragging = dragging;
        self
    }

    pub fn detected_rect(mut self, rect: CaptureOverlayRect) -> Self {
        self.detected_rect = Some(rect);
        self
    }

    pub fn selection_rect(mut self, rect: CaptureOverlayRect) -> Self {
        self.selection_rect = Some(rect);
        self
    }

    pub fn handles_visible(mut self, handles_visible: bool) -> Self {
        self.handles_visible = handles_visible;
        self
    }

    pub fn magnifier_visible(mut self, magnifier_visible: bool) -> Self {
        self.magnifier_visible = magnifier_visible;
        self
    }

    pub fn background(mut self, background: CaptureOverlayBackground) -> Self {
        self.background = Some(background);
        self
    }

    pub fn cursor(mut self, cursor: CaptureOverlayPoint) -> Self {
        self.cursor = Some(cursor);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for CaptureOverlayBuilder {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::CaptureOverlay(CaptureOverlayToken {
            id: self.id,
            phase: self.phase,
            detection_depth: self.detection_depth,
            dragging: self.dragging,
            detected_rect: self.detected_rect,
            selection_rect: self.selection_rect,
            handles_visible: self.handles_visible,
            magnifier_visible: self.magnifier_visible,
            background: self.background,
            cursor: self.cursor,
            a11y: self.a11y,
        }))
    }
}

/// Builds an image view from a raw BGRA8 pixel file written by the platform
/// screen-capture API.
pub fn image_bgra_file(
    path: impl Into<String>,
    pixel_width: u32,
    pixel_height: u32,
) -> ImageBuilder {
    ImageBuilder {
        id: None,
        bgra_path: path.into(),
        pixel_width,
        pixel_height,
        raster_path: None,
        // Screenshot/magnifier dumps fill their bounds exactly (the historical
        // behavior before generic stretch existed).
        stretch: ImageStretch::Fill,
        width: Length::Fill,
        height: Length::Fill,
        a11y: A11yHint::default(),
    }
}

/// A generic image element from an encoded file or URI (WinUI `Image`):
/// service icons, language flags, dictionary artwork, etc. Use
/// [`stretch`](ImageBuilder::stretch) to control scaling.
///
/// ```no_run
/// # use win_fluent::prelude::*;
/// # fn main() {
/// # let _ =
/// image("assets/flags/zh.png").stretch(ImageStretch::Uniform).width(Length::Fixed(24));
/// # }
/// ```
pub fn image(path: impl Into<String>) -> ImageBuilder {
    ImageBuilder {
        id: None,
        bgra_path: String::new(),
        pixel_width: 0,
        pixel_height: 0,
        raster_path: Some(path.into()),
        stretch: ImageStretch::default(),
        width: Length::Shrink,
        height: Length::Shrink,
        a11y: A11yHint::default(),
    }
}

/// An embedded web view (WinUI `WebView2`) navigating to `url`.
pub fn web_view_url(url: impl Into<String>) -> WebViewBuilder {
    WebViewBuilder {
        id: None,
        source: WebViewSource::Url(url.into()),
        width: Length::Fill,
        height: Length::Fill,
        a11y: A11yHint::default(),
    }
}

/// An embedded web view (WinUI `WebView2`) rendering inline `html`.
pub fn web_view_html(html: impl Into<String>) -> WebViewBuilder {
    WebViewBuilder {
        id: None,
        source: WebViewSource::Html(html.into()),
        width: Length::Fill,
        height: Length::Fill,
        a11y: A11yHint::default(),
    }
}

#[derive(Clone, Debug)]
pub struct WebViewBuilder {
    id: Option<String>,
    source: WebViewSource,
    width: Length,
    height: Length,
    a11y: A11yHint,
}

impl WebViewBuilder {
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

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for WebViewBuilder {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::WebView(WebViewToken {
            id: self.id,
            source: self.source,
            width: self.width,
            height: self.height,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct ImageBuilder {
    id: Option<String>,
    bgra_path: String,
    pixel_width: u32,
    pixel_height: u32,
    raster_path: Option<String>,
    stretch: ImageStretch,
    width: Length,
    height: Length,
    a11y: A11yHint,
}

impl ImageBuilder {
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

    /// Set the scaling mode (WinUI `Image.Stretch`).
    pub fn stretch(mut self, stretch: ImageStretch) -> Self {
        self.stretch = stretch;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for ImageBuilder {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Image(ImageToken {
            id: self.id,
            bgra_path: self.bgra_path,
            pixel_width: self.pixel_width,
            pixel_height: self.pixel_height,
            raster_path: self.raster_path,
            stretch: self.stretch,
            width: self.width,
            height: self.height,
            a11y: self.a11y,
        }))
    }
}

#[derive(Clone, Debug)]
pub struct SettingsRowBuilder<Message> {
    id: Option<String>,
    title: String,
    title_id: Option<String>,
    description: Option<String>,
    description_id: Option<String>,
    icon: Option<IconToken>,
    kind: SettingsRowKind,
    margin: Edges,
    align_x: Alignment,
    content_align_x: Alignment,
    content: Option<Box<View<Message>>>,
    trailing: Vec<View<Message>>,
    a11y: A11yHint,
}

#[derive(Clone, Debug)]
pub struct ExpanderBuilder<Message> {
    id: Option<String>,
    title: String,
    title_id: Option<String>,
    description: Option<String>,
    icon: Option<IconToken>,
    expanded: bool,
    header_state: ControlState,
    header_style: FluentStyle,
    content_style: FluentStyle,
    content: Option<Box<View<Message>>>,
    trailing: Vec<View<Message>>,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> ExpanderBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn title_id(mut self, id: impl Into<String>) -> Self {
        self.title_id = Some(id.into());
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

    pub fn expanded(mut self, expanded: bool) -> Self {
        self.expanded = expanded;
        self
    }

    pub fn header_state(mut self, state: ControlState) -> Self {
        self.header_state = state;
        self
    }

    pub fn header_hovered(mut self, hovered: bool) -> Self {
        self.header_state.hovered = hovered;
        self
    }

    pub fn header_pressed(mut self, pressed: bool) -> Self {
        self.header_state.pressed = pressed;
        self
    }

    pub fn header_style(mut self, classes: impl AsRef<str>) -> Self {
        self.header_style.extend(classes);
        self
    }

    pub fn content(mut self, content: impl IntoView<Message>) -> Self {
        self.content = Some(Box::new(content.into_view()));
        self
    }

    pub fn content_style(mut self, classes: impl AsRef<str>) -> Self {
        self.content_style.extend(classes);
        self
    }

    pub fn trailing(mut self, children: impl IntoChildren<Message>) -> Self {
        self.trailing = children.into_children();
        self
    }

    pub fn on_toggle(mut self, map: impl Fn(bool) -> Message + Send + Sync + 'static) -> Self {
        self.action = Action::bool_input(map);
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }
}

impl<Message> IntoView<Message> for ExpanderBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::Expander(ExpanderToken {
            id: self.id,
            title: self.title,
            title_id: self.title_id,
            description: self.description,
            icon: self.icon,
            expanded: self.expanded,
            header_state: self.header_state,
            header_style: self.header_style,
            content_style: self.content_style,
            content: self.content,
            trailing: self.trailing,
            action: self.action,
            a11y: self.a11y,
        }))
    }
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

    pub fn title_id(mut self, id: impl Into<String>) -> Self {
        self.title_id = Some(id.into());
        self
    }

    pub fn description_id(mut self, id: impl Into<String>) -> Self {
        self.description_id = Some(id.into());
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

    pub fn margin(mut self, margin: Edges) -> Self {
        self.margin = margin;
        self
    }

    pub fn align_x(mut self, align_x: Alignment) -> Self {
        self.align_x = align_x;
        self
    }

    pub fn content_align_x(mut self, align_x: Alignment) -> Self {
        self.content_align_x = align_x;
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
            title_id: self.title_id,
            description: self.description,
            description_id: self.description_id,
            icon: self.icon,
            kind: self.kind,
            margin: self.margin,
            align_x: self.align_x,
            content_align_x: self.content_align_x,
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

    pub fn on_copy_item(mut self, map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        self.copy_action = Action::selection_input(map);
        self
    }

    pub fn on_speak(mut self, message: Message) -> Self {
        self.speak_action = Action::Message(message);
        self
    }

    pub fn on_speak_item(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.speak_action = Action::selection_input(map);
        self
    }

    pub fn on_replace(mut self, message: Message) -> Self {
        self.replace_action = Action::Message(message);
        self
    }

    pub fn on_replace_item(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.replace_action = Action::selection_input(map);
        self
    }

    pub fn on_retry(mut self, message: Message) -> Self {
        self.retry_action = Action::Message(message);
        self
    }

    pub fn on_retry_item(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.retry_action = Action::selection_input(map);
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
pub struct ListViewBuilder<Message> {
    id: Option<String>,
    items: Vec<ListViewItem<Message>>,
    selected: Option<String>,
    spacing: u16,
    max_height: Option<u16>,
    virtualized: bool,
    action: Action<Message>,
    a11y: A11yHint,
}

impl<Message> ListViewBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    /// Append a single item.
    pub fn item(mut self, id: impl Into<String>, view: impl IntoView<Message>) -> Self {
        self.items.push(ListViewItem::new(id, view));
        self
    }

    /// Mark the selected item by id (WinUI `SelectedItem`).
    pub fn selected(mut self, id: impl Into<String>) -> Self {
        self.selected = Some(id.into());
        self
    }

    pub fn spacing(mut self, spacing: u16) -> Self {
        self.spacing = spacing;
        self
    }

    pub fn max_height(mut self, max_height: u16) -> Self {
        self.max_height = Some(max_height);
        self
    }

    /// Toggle the virtualization hint (defaults to `true`).
    pub fn virtualized(mut self, virtualized: bool) -> Self {
        self.virtualized = virtualized;
        self
    }

    pub fn a11y(mut self, a11y: A11yHint) -> Self {
        self.a11y = a11y;
        self
    }

    /// Selection callback: receives the clicked item's id.
    pub fn on_select(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> View<Message> {
        self.action = Action::selection_input(map);
        self.into_view()
    }
}

impl<Message> IntoView<Message> for ListViewBuilder<Message> {
    fn into_view(self) -> View<Message> {
        View::new(ViewToken::ListView(ListViewToken {
            id: self.id,
            items: self.items,
            selected: self.selected,
            spacing: self.spacing,
            max_height: self.max_height,
            virtualized: self.virtualized,
            action: self.action,
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
    max_height: Option<u16>,
    padding: Option<Edges>,
    border_width: Option<u16>,
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

    pub fn on_copy_item(mut self, map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        self.copy_action = Action::selection_input(map);
        self
    }

    pub fn on_speak(mut self, message: Message) -> Self {
        self.speak_action = Action::Message(message);
        self
    }

    pub fn on_speak_item(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.speak_action = Action::selection_input(map);
        self
    }

    pub fn on_replace(mut self, message: Message) -> Self {
        self.replace_action = Action::Message(message);
        self
    }

    pub fn on_replace_item(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.replace_action = Action::selection_input(map);
        self
    }

    pub fn on_retry(mut self, message: Message) -> Self {
        self.retry_action = Action::Message(message);
        self
    }

    pub fn on_retry_item(
        mut self,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> Self {
        self.retry_action = Action::selection_input(map);
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

    pub fn max_height(mut self, value: u16) -> Self {
        self.max_height = Some(value);
        self
    }

    pub fn padding(mut self, value: Edges) -> Self {
        self.padding = Some(value);
        self
    }

    pub fn border_width(mut self, value: u16) -> Self {
        self.border_width = Some(value);
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
            max_height: self.max_height,
            padding: self.padding,
            border_width: self.border_width,
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
    fn tw_parses_max_width_and_centering() {
        let view: View<Msg> = column((text("A"),))
            .width(Length::Fill)
            .tw("max-w-[1040px] mx-auto")
            .into_view();

        match view.token() {
            ViewToken::Layout(layout) => {
                assert_eq!(layout.max_width, Some(1040));
                assert!(layout.center_x);
                // Raw classes are still retained for the visual style backend.
                assert_eq!(layout.style.summary(), "max-w-[1040px] mx-auto");
            }
            _ => panic!("expected column layout"),
        }
    }

    #[test]
    fn tw_parses_max_width_with_scale_units() {
        // `max-w-N` uses the same ×4 spacing scale as p-/gap-.
        let view: View<Msg> = column((text("A"),)).tw("max-w-64").into_view();
        match view.token() {
            ViewToken::Layout(layout) => assert_eq!(layout.max_width, Some(256)),
            _ => panic!("expected column layout"),
        }
    }

    #[test]
    fn slider_builder_records_preview_interaction_state() {
        let view = slider::<Msg>(1.0)
            .id("preview.slider")
            .hovered(true)
            .pressed(true)
            .focused(true)
            .into_view();

        match view.token() {
            ViewToken::Slider(token) => {
                assert_eq!(token.id.as_deref(), Some("preview.slider"));
                assert!(token.state.hovered);
                assert!(token.state.pressed);
                assert!(token.state.focused);
            }
            _ => panic!("expected slider token"),
        }
    }

    #[test]
    fn tw_parses_margin_shorthands() {
        let uniform: View<Msg> = column((text("A"),)).tw("m-2").into_view();
        let axis: View<Msg> = column((text("A"),)).tw("mx-3 my-1").into_view();
        let sides: View<Msg> = column((text("A"),)).tw("mt-1 mr-2 mb-3 ml-4").into_view();

        let margin = |view: &View<Msg>| match view.token() {
            ViewToken::Layout(layout) => layout.margin,
            _ => panic!("expected column layout"),
        };

        assert_eq!(
            margin(&uniform),
            Edges {
                top: 8,
                right: 8,
                bottom: 8,
                left: 8
            }
        );
        assert_eq!(
            margin(&axis),
            Edges {
                top: 4,
                right: 12,
                bottom: 4,
                left: 12
            }
        );
        assert_eq!(
            margin(&sides),
            Edges {
                top: 4,
                right: 8,
                bottom: 12,
                left: 16
            }
        );
    }

    #[test]
    fn tw_leaves_layout_box_defaults_when_classes_absent() {
        // Regression guard against the original bug class: the previous parser
        // had no fields for these, so they could never be asserted.
        let view: View<Msg> = column((text("A"),)).tw("p-6 gap-4 w-full").into_view();
        match view.token() {
            ViewToken::Layout(layout) => {
                assert_eq!(layout.max_width, None);
                assert!(!layout.center_x);
                assert!(layout.margin.is_zero());
            }
            _ => panic!("expected column layout"),
        }
    }

    #[test]
    fn wrap_builder_carries_column_cap_and_spacing() {
        let view: View<Msg> = wrap((text("A"), text("B"), text("C")))
            .id("tabs")
            .max_columns(7)
            .spacing(10)
            .into_view();

        match view.token() {
            ViewToken::Wrap(token) => {
                assert_eq!(token.id.as_deref(), Some("tabs"));
                assert_eq!(token.children.len(), 3);
                assert_eq!(token.max_columns, 7);
                assert_eq!(token.spacing, 10);
                // run_spacing defaults to spacing when unset.
                assert_eq!(token.run_spacing, 10);
            }
            _ => panic!("expected wrap"),
        }
    }

    #[test]
    fn wrap_builder_clamps_zero_columns_to_one() {
        let view: View<Msg> = wrap((text("A"),)).max_columns(0).into_view();
        match view.token() {
            ViewToken::Wrap(token) => assert_eq!(token.max_columns, 1),
            _ => panic!("expected wrap"),
        }
    }

    #[test]
    fn collapse_transition_matches_winui_result_box_visibility_toggle() {
        let transition = CollapseTransition::default();

        assert_eq!(
            transition.duration_ms,
            CollapseTransition::RESULT_BOX_ANIMATION_MS
        );
        assert_eq!(
            transition.expand_transition(),
            Transition::fluent_content(0)
        );
        assert_eq!(
            transition.collapse_transition(),
            Transition::fluent_content(0)
        );
    }

    #[test]
    fn result_box_default_trace_is_instant_like_winui_visibility() {
        let expand = CollapseTransition::default().trace_result_box(
            CollapseTraceDirection::Expand,
            50.0,
            30.0,
            8.0,
        );

        assert_eq!(expand.len(), 2);
        assert_trace_monotonic(&expand, true);
        assert_eq!(expand.first().unwrap().visible_body_height, 50.0);
        assert_eq!(expand.last().unwrap().visible_body_height, 50.0);
        assert_eq!(expand.last().unwrap().body_translate_y, 0.0);

        let collapse = CollapseTransition::default().trace_result_box(
            CollapseTraceDirection::Collapse,
            50.0,
            30.0,
            8.0,
        );

        assert_eq!(collapse.len(), 2);
        assert_trace_monotonic(&collapse, false);
        assert_eq!(collapse.first().unwrap().visible_body_height, 0.0);
        assert_eq!(collapse.last().unwrap().visible_body_height, 0.0);
        assert_eq!(
            collapse.last().unwrap().body_translate_y,
            -CollapseTransition::RESULT_BOX_BODY_TRANSLATION_DIP
        );
    }

    #[test]
    fn result_box_trace_exposes_responsive_custom_motion_shape() {
        let trace = CollapseTransition::new(100).trace_result_box(
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
    fn result_box_custom_collapse_trace_moves_related_boxes_without_jumps() {
        let trace = CollapseTransition::new(100).trace_result_box(
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
