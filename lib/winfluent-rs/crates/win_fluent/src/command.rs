use crate::action::Action;
use crate::icon::IconToken;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct KeyboardAccelerator {
    pub key: String,
    pub modifiers: Vec<String>,
}

impl KeyboardAccelerator {
    pub fn new(key: impl Into<String>) -> Self {
        Self {
            key: key.into(),
            modifiers: Vec::new(),
        }
    }

    pub fn modifier(mut self, modifier: impl Into<String>) -> Self {
        self.modifiers.push(modifier.into());
        self
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CommandPlacement {
    Primary,
    Secondary,
    Overflow,
}

#[derive(Clone, Debug)]
pub struct CommandToken<Message> {
    pub id: Option<String>,
    pub label: String,
    pub icon: Option<IconToken>,
    pub placement: CommandPlacement,
    pub keyboard: Option<KeyboardAccelerator>,
    pub action: Action<Message>,
    pub enabled: bool,
}

pub fn command<Message>(label: impl Into<String>) -> CommandBuilder<Message> {
    CommandBuilder {
        id: None,
        label: label.into(),
        icon: None,
        placement: CommandPlacement::Primary,
        keyboard: None,
        action: Action::None,
        enabled: true,
    }
}

#[derive(Clone, Debug)]
pub struct CommandBuilder<Message> {
    id: Option<String>,
    label: String,
    icon: Option<IconToken>,
    placement: CommandPlacement,
    keyboard: Option<KeyboardAccelerator>,
    action: Action<Message>,
    enabled: bool,
}

impl<Message> CommandBuilder<Message> {
    pub fn id(mut self, id: impl Into<String>) -> Self {
        self.id = Some(id.into());
        self
    }

    pub fn icon(mut self, icon: IconToken) -> Self {
        self.icon = Some(icon);
        self
    }

    pub fn placement(mut self, placement: CommandPlacement) -> Self {
        self.placement = placement;
        self
    }

    pub fn keyboard(mut self, keyboard: KeyboardAccelerator) -> Self {
        self.keyboard = Some(keyboard);
        self
    }

    pub fn enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }

    pub fn on_invoke(mut self, message: Message) -> Self {
        self.action = Action::Message(message);
        self
    }

    pub fn build(self) -> CommandToken<Message> {
        CommandToken {
            id: self.id,
            label: self.label,
            icon: self.icon,
            placement: self.placement,
            keyboard: self.keyboard,
            action: self.action,
            enabled: self.enabled,
        }
    }
}
