use crate::action::Action;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HotkeyModifier {
    Control,
    Alt,
    Shift,
    Logo,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum HotkeyKey {
    Character(char),
    Function(u8),
    Named(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Hotkey {
    pub id: String,
    pub modifiers: Vec<HotkeyModifier>,
    pub key: HotkeyKey,
}

impl Hotkey {
    pub fn new(id: impl Into<String>, key: HotkeyKey) -> Self {
        Self {
            id: id.into(),
            modifiers: Vec::new(),
            key,
        }
    }

    pub fn modifier(mut self, modifier: HotkeyModifier) -> Self {
        self.modifiers.push(modifier);
        self
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ClipboardFormat {
    Text,
    Image,
    Files,
    Custom(&'static str),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PlatformCommand {
    CaptureTextInsertionTarget,
    WriteClipboardText(String),
    InsertText(String),
    OpenUrl(String),
    RegisterShellVerb(ShellVerb),
    UnregisterShellVerb(ShellVerb),
    RegisterProtocol(ProtocolRegistration),
    UnregisterProtocol(ProtocolRegistration),
    RunBundledExecutable {
        executable_name: String,
        arguments: Vec<String>,
    },
    SpeakText {
        text: String,
        language: Option<String>,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ScreenRect {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

impl ScreenRect {
    pub const fn new(x: i32, y: i32, width: u32, height: u32) -> Self {
        Self {
            x,
            y,
            width,
            height,
        }
    }

    pub const fn is_empty(self) -> bool {
        self.width == 0 || self.height == 0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ScreenCaptureRequest {
    pub region: Option<ScreenRect>,
}

impl ScreenCaptureRequest {
    pub const fn virtual_desktop() -> Self {
        Self { region: None }
    }

    pub const fn region(region: ScreenRect) -> Self {
        Self {
            region: Some(region),
        }
    }
}

impl Default for ScreenCaptureRequest {
    fn default() -> Self {
        Self::virtual_desktop()
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScreenCaptureResult {
    pub pixel_data_path: String,
    pub pixel_width: u32,
    pub pixel_height: u32,
    pub screen_rect: ScreenRect,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScreenWindow {
    pub id: isize,
    pub parent_id: Option<isize>,
    pub rect: ScreenRect,
    pub class_name: String,
}

impl ScreenWindow {
    pub fn new(id: isize, parent_id: Option<isize>, rect: ScreenRect) -> Self {
        Self {
            id,
            parent_id,
            rect,
            class_name: String::new(),
        }
    }

    pub fn class_name(mut self, class_name: impl Into<String>) -> Self {
        self.class_name = class_name.into();
        self
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ScreenWindowSnapshotRequest {
    pub excluded_titles: Vec<String>,
}

impl ScreenWindowSnapshotRequest {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn exclude_title(mut self, title: impl Into<String>) -> Self {
        self.excluded_titles.push(title.into());
        self
    }
}

#[derive(Clone, Debug)]
pub struct NamedEventRegistration<Message> {
    pub name: String,
    pub auto_reset: bool,
    pub action: Action<Message>,
}

impl<Message> NamedEventRegistration<Message> {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            auto_reset: true,
            action: Action::None,
        }
    }

    pub fn manual_reset(mut self) -> Self {
        self.auto_reset = false;
        self
    }

    pub fn on_signal(mut self, message: Message) -> Self {
        self.action = Action::Message(message);
        self
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FileDialogFilter {
    pub name: String,
    pub patterns: Vec<String>,
}

impl FileDialogFilter {
    pub fn new<I, P>(name: impl Into<String>, patterns: I) -> Self
    where
        I: IntoIterator<Item = P>,
        P: Into<String>,
    {
        Self {
            name: name.into(),
            patterns: patterns.into_iter().map(Into::into).collect(),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FileDialogOptions {
    pub title: String,
    pub filters: Vec<FileDialogFilter>,
    pub initial_directory: Option<String>,
}

impl FileDialogOptions {
    pub fn new(title: impl Into<String>) -> Self {
        Self {
            title: title.into(),
            filters: Vec::new(),
            initial_directory: None,
        }
    }

    pub fn filter(mut self, filter: FileDialogFilter) -> Self {
        self.filters.push(filter);
        self
    }

    pub fn initial_directory(mut self, directory: impl Into<String>) -> Self {
        self.initial_directory = Some(directory.into());
        self
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FolderDialogOptions {
    pub title: String,
    pub initial_directory: Option<String>,
}

impl FolderDialogOptions {
    pub fn new(title: impl Into<String>) -> Self {
        Self {
            title: title.into(),
            initial_directory: None,
        }
    }

    pub fn initial_directory(mut self, directory: impl Into<String>) -> Self {
        self.initial_directory = Some(directory.into());
        self
    }
}

#[derive(Clone, Debug)]
pub struct TrayMenu<Message> {
    pub tooltip: String,
    pub icon_path: Option<String>,
    pub presenter_kind: TrayMenuPresenterKind,
    pub presenter_min_width: Option<u16>,
    pub presenter_style: TrayMenuPresenterStyle,
    pub default_item_id: Option<String>,
    pub items: Vec<TrayMenuItem<Message>>,
}

impl<Message> TrayMenu<Message> {
    pub fn new(tooltip: impl Into<String>) -> Self {
        Self {
            tooltip: tooltip.into(),
            icon_path: None,
            presenter_kind: TrayMenuPresenterKind::default(),
            presenter_min_width: None,
            presenter_style: TrayMenuPresenterStyle::default(),
            default_item_id: None,
            items: Vec::new(),
        }
    }

    pub fn icon_path(mut self, path: impl Into<String>) -> Self {
        self.icon_path = Some(path.into());
        self
    }

    pub fn presenter_min_width(mut self, width: u16) -> Self {
        self.presenter_min_width = Some(width);
        self
    }

    pub fn presenter_kind(mut self, kind: TrayMenuPresenterKind) -> Self {
        self.presenter_kind = kind;
        self
    }

    pub fn presenter_style(mut self, style: TrayMenuPresenterStyle) -> Self {
        self.presenter_style = style;
        self
    }

    pub fn default_item(mut self, id: impl Into<String>) -> Self {
        self.default_item_id = Some(id.into());
        self
    }

    pub fn item(mut self, item: TrayMenuItem<Message>) -> Self {
        self.items.push(item);
        self
    }

    pub fn separator(mut self) -> Self {
        self.items.push(TrayMenuItem::separator());
        self
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
pub enum TrayMenuPresenterKind {
    #[default]
    Native,
    Fluent,
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct TrayMenuPresenterStyle {
    pub presenter_corner_radius: u16,
    pub presenter_shadow_margin: u16,
    pub presenter_max_height: Option<u16>,
    pub popup_animation: TrayMenuPopupAnimation,
    pub item_corner_radius: u16,
    pub item_font_size: u16,
    pub item_min_height: u16,
    pub item_vertical_padding: u16,
    pub item_horizontal_padding: u16,
    pub submenu_arrow_column_width: u16,
    pub hover_inset_x: u16,
    pub hover_inset_y: u16,
    pub separator_height: u16,
    pub separator_line_thickness: u16,
    pub separator_horizontal_inset: u16,
    pub light_surface: TrayMenuColor,
    pub light_foreground: TrayMenuColor,
    pub light_separator: TrayMenuColor,
    pub dark_surface: TrayMenuColor,
    pub dark_foreground: TrayMenuColor,
    pub dark_separator: TrayMenuColor,
    pub hover_foreground_mix_percent: u8,
}

impl TrayMenuPresenterStyle {
    pub const fn winui() -> Self {
        Self {
            presenter_corner_radius: 10,
            presenter_shadow_margin: 12,
            presenter_max_height: Some(520),
            popup_animation: TrayMenuPopupAnimation::Vertical,
            item_corner_radius: 8,
            item_font_size: 14,
            item_min_height: 30,
            item_vertical_padding: 10,
            item_horizontal_padding: 14,
            submenu_arrow_column_width: 30,
            hover_inset_x: 4,
            hover_inset_y: 4,
            separator_height: 7,
            separator_line_thickness: 1,
            separator_horizontal_inset: 4,
            light_surface: TrayMenuColor::rgb(0xF9, 0xF9, 0xF9),
            light_foreground: TrayMenuColor::rgb(0x1A, 0x1A, 0x1A),
            light_separator: TrayMenuColor::rgb(0xE5, 0xE5, 0xE5),
            dark_surface: TrayMenuColor::system_menu(),
            dark_foreground: TrayMenuColor::rgb(0xF3, 0xF3, 0xF3),
            dark_separator: TrayMenuColor::rgb(0x3A, 0x3A, 0x3A),
            hover_foreground_mix_percent: 10,
        }
    }

    pub fn presenter_corner_radius(mut self, radius: u16) -> Self {
        self.presenter_corner_radius = radius;
        self
    }

    pub fn presenter_shadow_margin(mut self, margin: u16) -> Self {
        self.presenter_shadow_margin = margin;
        self
    }

    pub fn presenter_max_height(mut self, height: Option<u16>) -> Self {
        self.presenter_max_height = height;
        self
    }

    pub fn popup_animation(mut self, animation: TrayMenuPopupAnimation) -> Self {
        self.popup_animation = animation;
        self
    }

    pub fn item_corner_radius(mut self, radius: u16) -> Self {
        self.item_corner_radius = radius;
        self
    }

    pub fn item_font_size(mut self, size: u16) -> Self {
        self.item_font_size = size;
        self
    }

    pub fn item_min_height(mut self, height: u16) -> Self {
        self.item_min_height = height;
        self
    }

    pub fn item_vertical_padding(mut self, padding: u16) -> Self {
        self.item_vertical_padding = padding;
        self
    }

    pub fn item_horizontal_padding(mut self, padding: u16) -> Self {
        self.item_horizontal_padding = padding;
        self
    }

    pub fn submenu_arrow_column_width(mut self, width: u16) -> Self {
        self.submenu_arrow_column_width = width;
        self
    }

    pub fn hover_inset(mut self, x: u16, y: u16) -> Self {
        self.hover_inset_x = x;
        self.hover_inset_y = y;
        self
    }

    pub fn separator_height(mut self, height: u16) -> Self {
        self.separator_height = height;
        self
    }

    pub fn separator_line_thickness(mut self, thickness: u16) -> Self {
        self.separator_line_thickness = thickness;
        self
    }

    pub fn separator_horizontal_inset(mut self, inset: u16) -> Self {
        self.separator_horizontal_inset = inset;
        self
    }

    pub fn light_palette(
        mut self,
        surface: TrayMenuColor,
        foreground: TrayMenuColor,
        separator: TrayMenuColor,
    ) -> Self {
        self.light_surface = surface;
        self.light_foreground = foreground;
        self.light_separator = separator;
        self
    }

    pub fn dark_palette(
        mut self,
        surface: TrayMenuColor,
        foreground: TrayMenuColor,
        separator: TrayMenuColor,
    ) -> Self {
        self.dark_surface = surface;
        self.dark_foreground = foreground;
        self.dark_separator = separator;
        self
    }

    pub fn hover_foreground_mix_percent(mut self, percent: u8) -> Self {
        self.hover_foreground_mix_percent = percent.min(100);
        self
    }
}

impl Default for TrayMenuPresenterStyle {
    fn default() -> Self {
        Self::winui()
    }
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum TrayMenuColor {
    SystemMenu,
    Rgb(u8, u8, u8),
}

impl TrayMenuColor {
    pub const fn system_menu() -> Self {
        Self::SystemMenu
    }

    pub const fn rgb(red: u8, green: u8, blue: u8) -> Self {
        Self::Rgb(red, green, blue)
    }
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum TrayMenuPopupAnimation {
    System,
    None,
    Vertical,
}

#[derive(Clone, Debug)]
pub struct TrayMenuItem<Message> {
    pub id: String,
    pub label: String,
    pub tooltip: Option<String>,
    pub action: Action<Message>,
    pub enabled: bool,
    pub children: Vec<TrayMenuItem<Message>>,
    pub separator: bool,
}

impl<Message> TrayMenuItem<Message> {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            tooltip: None,
            action: Action::None,
            enabled: true,
            children: Vec::new(),
            separator: false,
        }
    }

    pub fn separator() -> Self {
        Self {
            id: String::new(),
            label: String::new(),
            tooltip: None,
            action: Action::None,
            enabled: false,
            children: Vec::new(),
            separator: true,
        }
    }

    pub fn submenu(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self::new(id, label)
    }

    pub fn on_invoke(mut self, message: Message) -> Self {
        self.action = Action::Message(message);
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }

    pub fn tooltip(mut self, tooltip: impl Into<String>) -> Self {
        self.tooltip = Some(tooltip.into());
        self
    }

    pub fn item(mut self, item: TrayMenuItem<Message>) -> Self {
        self.children.push(item);
        self
    }

    pub fn is_separator(&self) -> bool {
        self.separator
    }

    pub fn is_submenu(&self) -> bool {
        !self.children.is_empty()
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShellVerb {
    pub id: String,
    pub label: String,
    pub accepts_files: bool,
    pub accepts_directory_background: bool,
    pub arguments: Vec<String>,
}

impl ShellVerb {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            accepts_files: true,
            accepts_directory_background: false,
            arguments: Vec::new(),
        }
    }

    pub fn directory_background(mut self, enabled: bool) -> Self {
        self.accepts_directory_background = enabled;
        self
    }

    pub fn argument(mut self, argument: impl Into<String>) -> Self {
        self.arguments.push(argument.into());
        self
    }

    pub fn is_registry_safe_id(&self) -> bool {
        is_registry_safe_identifier(&self.id)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProtocolRegistration {
    pub scheme: String,
    pub description: String,
    pub arguments: Vec<String>,
}

impl ProtocolRegistration {
    pub fn new(scheme: impl Into<String>, description: impl Into<String>) -> Self {
        Self {
            scheme: scheme.into(),
            description: description.into(),
            arguments: Vec::new(),
        }
    }

    pub fn argument(mut self, argument: impl Into<String>) -> Self {
        self.arguments.push(argument.into());
        self
    }

    pub fn is_valid_scheme(&self) -> bool {
        is_valid_protocol_scheme(&self.scheme)
    }
}

fn is_registry_safe_identifier(value: &str) -> bool {
    !value.is_empty()
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
}

fn is_valid_protocol_scheme(value: &str) -> bool {
    let mut bytes = value.bytes();
    let Some(first) = bytes.next() else {
        return false;
    };
    first.is_ascii_alphabetic()
        && bytes.all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'+' | b'-' | b'.'))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shell_verb_ids_expose_registry_safety_guard() {
        assert!(ShellVerb::new("inspect-files", "Inspect").is_registry_safe_id());
        assert!(ShellVerb::new("inspect.files_2", "Inspect").is_registry_safe_id());
        assert!(!ShellVerb::new("", "Empty").is_registry_safe_id());
        assert!(!ShellVerb::new(r"bad\path", "Bad").is_registry_safe_id());
        assert!(!ShellVerb::new("bad path", "Bad").is_registry_safe_id());
    }

    #[test]
    fn protocol_registration_exposes_uri_scheme_guard() {
        assert!(ProtocolRegistration::new("demo+v1", "Demo").is_valid_scheme());
        assert!(ProtocolRegistration::new("demo.app", "Demo").is_valid_scheme());
        assert!(!ProtocolRegistration::new("", "Empty").is_valid_scheme());
        assert!(!ProtocolRegistration::new("1demo", "Bad").is_valid_scheme());
        assert!(!ProtocolRegistration::new("demo app", "Bad").is_valid_scheme());
    }
}
