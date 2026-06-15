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
    pub items: Vec<TrayMenuItem<Message>>,
}

impl<Message> TrayMenu<Message> {
    pub fn new(tooltip: impl Into<String>) -> Self {
        Self {
            tooltip: tooltip.into(),
            items: Vec::new(),
        }
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

#[derive(Clone, Debug)]
pub struct TrayMenuItem<Message> {
    pub id: String,
    pub label: String,
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
}
