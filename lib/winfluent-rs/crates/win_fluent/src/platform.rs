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
}

#[derive(Clone, Debug)]
pub struct TrayMenuItem<Message> {
    pub id: String,
    pub label: String,
    pub action: Action<Message>,
    pub enabled: bool,
}

impl<Message> TrayMenuItem<Message> {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            action: Action::None,
            enabled: true,
        }
    }

    pub fn on_invoke(mut self, message: Message) -> Self {
        self.action = Action::Message(message);
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShellVerb {
    pub id: String,
    pub label: String,
    pub accepts_files: bool,
    pub accepts_directory_background: bool,
}

impl ShellVerb {
    pub fn new(id: impl Into<String>, label: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            accepts_files: true,
            accepts_directory_background: false,
        }
    }

    pub fn directory_background(mut self, enabled: bool) -> Self {
        self.accepts_directory_background = enabled;
        self
    }
}
